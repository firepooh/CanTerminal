using System.Text;

namespace CanTerminal.Core.Xcp;

/// <summary>CAN IDs that carry an XCP session. Request = master→slave, Response = slave→master.</summary>
public sealed record XcpConfig(uint RequestId, uint ResponseId, uint? BroadcastId = null, string? Channel = null)
{
    public override string ToString() =>
        $"req 0x{RequestId:X}, rsp 0x{ResponseId:X}" +
        (BroadcastId is uint b ? $", bcast 0x{b:X}" : "") +
        (Channel is null ? "" : $" on {Channel}");
}

/// <summary>
/// Decodes XCP on CAN traffic into a frame type + parameter comment, in the style of a
/// Vector/ASAM trace listing.
///
/// The decoder is stateful on purpose: a slave's "FF" response only means something in the
/// context of the command that preceded it, and a DAQ-DTO's PID can only be resolved to
/// DAQ/ODT numbers by following the ALLOC_DAQ / ALLOC_ODT sequence. Frames must therefore be
/// fed exactly once, in capture order (<see cref="MessageHub"/> guarantees this).
///
/// When the session was already running before capture started, the missing context is
/// reported honestly (e.g. "DAQ-DTO (PID = 0x02)") instead of being guessed.
/// </summary>
public sealed class XcpDecoder
{
    private const int MaxPendingAgeSeconds = 5;

    private readonly object _lock = new();
    private readonly XcpConfig _config;

    // --- session state (reset by CONNECT) ---
    private bool _bigEndian;
    private byte _identificationFieldType;   // DAQ_KEY_BYTE bits 7-6; 0 = absolute ODT number
    private bool _daqKeyKnown;
    private byte? _pendingCmd;
    private byte[]? _pendingData;
    private double _pendingTs;

    // --- dynamic DAQ configuration (reset by FREE_DAQ) ---
    private int _daqCount;
    private readonly Dictionary<int, int> _odtCounts = [];

    public XcpDecoder(XcpConfig config) => _config = config;

    public XcpConfig Config => _config;

    /// <summary>
    /// True when the frame belongs to the configured XCP session, i.e. it rides on the request,
    /// response or broadcast ID. Views use this to filter a busy bus down to the session.
    /// </summary>
    public bool Matches(CanFrame f)
    {
        if (_config.Channel is { } ch && !f.Channel.Equals(ch, StringComparison.OrdinalIgnoreCase))
            return false;
        return f.ArbId == _config.RequestId
            || f.ArbId == _config.ResponseId
            || f.ArbId == _config.BroadcastId;
    }

    public FrameAnnotation? Decode(CanFrame f)
    {
        if (!Matches(f)) return null;

        bool fromMaster = f.ArbId == _config.RequestId || f.ArbId == _config.BroadcastId;
        lock (_lock)
        {
            var annotation = fromMaster ? DecodeMaster(f) : DecodeSlave(f);
            // Stamped once here rather than at each of the return sites below: which side sent
            // the frame follows from the CAN ID alone, and nothing in the decode can change it.
            return annotation with { Sender = fromMaster ? "master" : "slave" };
        }
    }

    // ---------------- master → slave ----------------

    private FrameAnnotation DecodeMaster(CanFrame f)
    {
        var d = f.Data;
        if (d.Length == 0) return new FrameAnnotation("CTO (?)", "empty frame");

        byte pid = d[0];
        if (pid < XcpTables.CommandPidMin) return DecodeDto(f, stim: true);

        string name = XcpTables.Command(pid) ?? $"CMD 0x{pid:X2}";
        if (pid == 0xC0 && d.Length > 1) name = XcpTables.Level1(d[1]);
        if (pid == 0xF2 && d.Length > 1) name = XcpTables.TransportLayer(d[1]);

        string? comment = DescribeCommand(pid, d);
        ApplyCommandState(pid, d);

        _pendingCmd = pid;
        _pendingData = d;
        _pendingTs = f.Timestamp;
        return new FrameAnnotation($"CTO ({name})", comment);
    }

    private string? DescribeCommand(byte pid, byte[] d)
    {
        var r = new Rd(d, _bigEndian);
        return pid switch
        {
            0xFF => $"MODE = 0x{r.U8(1):X2}" + (r.U8(1) == 0 ? " (normal)" : r.U8(1) == 1 ? " (user defined)" : ""),
            0xFA => $"TYPE = 0x{r.U8(1):X2} ({XcpTables.GetIdType(r.U8(1))})",
            0xF9 => $"MODE = 0x{r.U8(1):X2}|SESSION_CONFIGURATION_ID = 0x{r.U16(2):X4}",
            0xF8 => $"MODE = 0x{r.U8(1):X2}|RESOURCE = 0x{r.U8(2):X2} ({XcpTables.Resource(r.U8(2))})",
            0xF7 => $"LENGTH = 0x{r.U8(1):X2}|KEY = {Hex(d, 2)}",
            0xF6 => $"EXTENSION = 0x{r.U8(3):X2}|ADDRESS = 0x{r.U32(4):X8}",
            0xF5 => $"NUMBER_OF_ELEMENTS = 0x{r.U8(1):X2}",
            0xF4 => $"NUMBER_OF_ELEMENTS = 0x{r.U8(1):X2}|EXTENSION = 0x{r.U8(3):X2}|ADDRESS = 0x{r.U32(4):X8}",
            0xF3 => $"BLOCK_SIZE = 0x{r.U32(4):X8}",
            0xF2 => DescribeTransportLayer(d),
            0xF0 => $"NUMBER_OF_ELEMENTS = 0x{r.U8(1):X2}|DATA = {Hex(d, 2)}",
            0xEF => $"NUMBER_OF_ELEMENTS = 0x{r.U8(1):X2}|DATA = {Hex(d, 2)}",
            // DOWNLOAD_MAX / PROGRAM_MAX carry no element count — the payload is the rest of the
            // CTO and its length is MAX_CTO-1 by definition. Reading a count here would report
            // the first data byte as one and start DATA a byte late.
            0xEE or 0xC9 => $"DATA = {Hex(d, 1)}",
            0xED => $"NUMBER_OF_ELEMENTS = 0x{r.U8(1):X2}|EXTENSION = 0x{r.U8(3):X2}|ADDRESS = 0x{r.U32(4):X8}",
            0xEC => $"SHIFT = 0x{r.U8(1):X2}|AND_MASK = 0x{r.U16(2):X4}|XOR_MASK = 0x{r.U16(4):X4}",
            0xEB => $"MODE = 0x{r.U8(1):X2}|SEGMENT = 0x{r.U8(2):X2}|PAGE = 0x{r.U8(3):X2}",
            0xEA => $"MODE = 0x{r.U8(1):X2}|SEGMENT = 0x{r.U8(2):X2}",
            0xE3 => $"DAQ_LIST_NUMBER = 0x{r.U16(2):X4}",
            0xE2 => $"DAQ_LIST_NUMBER = 0x{r.U16(2):X4}|ODT_NUMBER = 0x{r.U8(4):X2}|ODT_ENTRY_NUMBER = 0x{r.U8(5):X2}",
            0xE1 => $"BIT_OFFSET = 0x{r.U8(1):X2}|SIZE = 0x{r.U8(2):X2}|EXTENSION = 0x{r.U8(3):X2}|ADDRESS = 0x{r.U32(4):X8}",
            0xE0 => $"MODE = 0x{r.U8(1):X2}{DaqListMode(r.U8(1))}|DAQ_LIST_NUMBER = 0x{r.U16(2):X4}" +
                    $"|EVENT_CHANNEL_NUMBER = 0x{r.U16(4):X4}|PRESCALER = 0x{r.U8(6):X2}|PRIORITY = 0x{r.U8(7):X2}",
            0xDF => $"DAQ_LIST_NUMBER = 0x{r.U16(2):X4}",
            0xDE => $"MODE = 0x{r.U8(1):X2} ({StartStopList(r.U8(1))})|DAQ_LIST_NUMBER = 0x{r.U16(2):X4}",
            0xDD => $"MODE = 0x{r.U8(1):X2} ({StartStopSynch(r.U8(1))})",
            0xD8 => $"DAQ_LIST_NUMBER = 0x{r.U16(2):X4}",
            0xD7 => $"EVENT_CHANNEL_NUMBER = 0x{r.U16(2):X4}",
            0xD5 => $"DAQ_COUNT = 0x{r.U16(2):X4}",
            0xD4 => $"DAQ_LIST_NUMBER = 0x{r.U16(2):X4}|ODT_COUNT = 0x{r.U8(4):X2}",
            0xD3 => $"DAQ_LIST_NUMBER = 0x{r.U16(2):X4}|ODT_NUMBER = 0x{r.U8(4):X2}|ODT_ENTRIES_COUNT = 0x{r.U8(5):X2}",
            0xD1 => $"MODE = 0x{r.U8(1):X2}|CLEAR_RANGE = 0x{r.U32(4):X8}",
            0xD0 or 0xCA => $"NUMBER_OF_ELEMENTS = 0x{r.U8(1):X2}|DATA = {Hex(d, 2)}",
            0xC0 => d.Length > 2 ? $"PARAMS = {Hex(d, 2)}" : null,
            // no-parameter commands: DISCONNECT, GET_STATUS, SYNCH, GET_COMM_MODE_INFO,
            // GET_DAQ_CLOCK, READ_DAQ, GET_DAQ_PROCESSOR_INFO, GET_DAQ_RESOLUTION_INFO, FREE_DAQ...
            _ => d.Length > 1 ? $"PARAMS = {Hex(d, 1)}" : null,
        };
    }

    private static string? DescribeTransportLayer(byte[] d)
    {
        byte sub = d.Length > 1 ? d[1] : (byte)0;
        // GET_SLAVE_ID carries the "XCP" magic and an identification mode.
        if (sub == 0xFF && d.Length >= 6)
            return $"MAGIC = '{Ascii(d, 2, 3)}'|MODE = 0x{d[5]:X2}" +
                   (d[5] == 0 ? " (identify by echo)" : d[5] == 1 ? " (confirm by inverse echo)" : "");
        return d.Length > 2 ? $"PARAMS = {Hex(d, 2)}" : null;
    }

    private void ApplyCommandState(byte pid, byte[] d)
    {
        var r = new Rd(d, _bigEndian);
        switch (pid)
        {
            case 0xFF: // CONNECT — a new session invalidates everything we thought we knew
                ResetSession();
                break;
            case 0xD6: // FREE_DAQ — releases the dynamic DAQ configuration
                _daqCount = 0;
                _odtCounts.Clear();
                break;
            case 0xD5: // ALLOC_DAQ
                _daqCount = r.U16(2);
                _odtCounts.Clear();
                break;
            case 0xD4: // ALLOC_ODT
                _odtCounts[r.U16(2)] = r.U8(4);
                break;
        }
    }

    // ---------------- slave → master ----------------

    private FrameAnnotation DecodeSlave(CanFrame f)
    {
        var d = f.Data;
        if (d.Length == 0) return new FrameAnnotation("CTO (?)", "empty frame");

        switch (d[0])
        {
            case XcpTables.PidRes:
            {
                var (cmd, request) = TakePending(f.Timestamp);
                string type = d.Length > 1 ? "CTO (OK + INFO)" : "CTO (OK)";
                return new FrameAnnotation(type, DescribeResponse(cmd, request, d));
            }
            case XcpTables.PidErr:
            {
                var (cmd, request) = TakePending(f.Timestamp);
                string name = d.Length > 1 ? XcpTables.Error(d[1]) : "ERR (no code)";
                string comment = d.Length > 1 ? $"{name} (0x{d[1]:X2})" : name;
                if (cmd is byte c) comment += $" ← {CommandName(c, request)}";
                return new FrameAnnotation("CTO (ERR)", comment);
            }
            // EV and SERV are asynchronous: they must not consume the pending command.
            case XcpTables.PidEv:
                return new FrameAnnotation("CTO (EV)",
                    d.Length > 1 ? $"{XcpTables.Event(d[1])} (0x{d[1]:X2})" + (d.Length > 2 ? $"|DATA = {Hex(d, 2)}" : "") : "EV (no code)");
            case XcpTables.PidServ:
                return new FrameAnnotation("CTO (SERV)",
                    d.Length > 1 ? $"{XcpTables.Service(d[1])} (0x{d[1]:X2})" + (d[1] == 0x01 ? $"|TEXT = '{Ascii(d, 2, d.Length - 2)}'" : "") : "SERV (no code)");
            default:
                return DecodeDto(f, stim: false);
        }
    }

    /// <summary>
    /// Consumes the command this reply answers, together with the bytes that were sent with it.
    ///
    /// The payload has to come back with the command: several commands are only distinguishable
    /// by their sub-command byte, and the response carries no copy of it. Reading the field
    /// afterwards is not an option — this method is what clears it.
    /// </summary>
    private (byte? Cmd, byte[]? Request) TakePending(double ts)
    {
        // A stale pending command means we missed its response (frame loss, or the trace
        // started mid-command); don't pair it with an unrelated later reply.
        if (_pendingCmd is null) return (null, null);
        if (ts - _pendingTs > MaxPendingAgeSeconds) { _pendingCmd = null; _pendingData = null; return (null, null); }
        var cmd = _pendingCmd;
        var data = _pendingData;
        _pendingCmd = null;
        _pendingData = null;
        return (cmd, data);
    }

    /// <summary>Names a command, resolving the sub-command byte where one exists.</summary>
    private static string CommandName(byte cmd, byte[]? request) => cmd switch
    {
        0xC0 when request is { Length: > 1 } r => XcpTables.Level1(r[1]),
        0xF2 when request is { Length: > 1 } r => XcpTables.TransportLayer(r[1]),
        _ => XcpTables.Command(cmd) ?? $"CMD 0x{cmd:X2}",
    };

    private string? DescribeResponse(byte? cmd, byte[]? request, byte[] d)
    {
        if (cmd is null)
            return d.Length > 1 ? $"DATA = {Hex(d, 1)}" : null;

        // CONNECT's response defines the byte order for every multi-byte field that follows,
        // so it has to be applied before the remaining fields are read.
        if (cmd == 0xFF && d.Length >= 8)
        {
            _bigEndian = (d[2] & 0x01) != 0;
            var rc = new Rd(d, _bigEndian);
            return $"CONNECT: RESOURCE = 0x{d[1]:X2} ({XcpTables.Resource(d[1])})" +
                   $"|COMM_MODE_BASIC = 0x{d[2]:X2} ({(_bigEndian ? "Motorola" : "Intel")}, AG={1 << ((d[2] >> 1) & 0x03)})" +
                   $"|MAX_CTO = {d[3]}|MAX_DTO = {rc.U16(4)}" +
                   $"|PROTOCOL_LAYER = 0x{d[6]:X2}|TRANSPORT_LAYER = 0x{d[7]:X2}";
        }

        var r = new Rd(d, _bigEndian);
        switch (cmd)
        {
            case 0xFD when d.Length >= 6: // GET_STATUS
                return $"GET_STATUS: SESSION_STATUS = 0x{d[1]:X2} ({XcpTables.SessionStatus(d[1])})" +
                       $"|RESOURCE_PROTECTION = 0x{d[2]:X2} ({XcpTables.Resource(d[2])})" +
                       $"|SESSION_CONFIGURATION_ID = 0x{r.U16(4):X4}";

            case 0xFB when d.Length >= 8: // GET_COMM_MODE_INFO
                return $"GET_COMM_MODE_INFO: COMM_MODE_OPTIONAL = 0x{d[2]:X2}|MAX_BS = {d[4]}" +
                       $"|MIN_ST = {d[5]}|QUEUE_SIZE = {d[6]}|DRIVER_VERSION = 0x{d[7]:X2}";

            case 0xFA when d.Length >= 8: // GET_ID
                return $"GET_ID: MODE = 0x{d[1]:X2}|LENGTH = {r.U32(4)}";

            case 0xF8 when d.Length >= 2: // GET_SEED
                return $"GET_SEED: LENGTH = {d[1]}|SEED = {Hex(d, 2)}";

            case 0xF7 when d.Length >= 2: // UNLOCK
                return $"UNLOCK: PROTECTION_STATUS = 0x{d[1]:X2} (still protected: {XcpTables.Resource(d[1])})";

            case 0xDA when d.Length >= 8: // GET_DAQ_PROCESSOR_INFO
            {
                _identificationFieldType = (byte)((d[7] >> 6) & 0x03);
                _daqKeyKnown = true;
                return $"GET_DAQ_PROCESSOR_INFO: DAQ_PROPERTIES = 0x{d[1]:X2}|MAX_DAQ = {r.U16(2)}" +
                       $"|MAX_EVENT_CHANNEL = {r.U16(4)}|MIN_DAQ = {d[6]}" +
                       $"|DAQ_KEY_BYTE = 0x{d[7]:X2} (id field: {IdFieldName(_identificationFieldType)})";
            }

            case 0xD9 when d.Length >= 8: // GET_DAQ_RESOLUTION_INFO
                return $"GET_DAQ_RESOLUTION_INFO: GRANULARITY_ODT_ENTRY_DAQ = {d[1]}|MAX_ODT_ENTRY_SIZE_DAQ = {d[2]}" +
                       $"|GRANULARITY_ODT_ENTRY_STIM = {d[3]}|MAX_ODT_ENTRY_SIZE_STIM = {d[4]}" +
                       $"|TIMESTAMP_MODE = 0x{d[5]:X2}|TIMESTAMP_TICKS = {r.U16(6)}";

            case 0xDC when d.Length >= 8: // GET_DAQ_CLOCK
                return $"GET_DAQ_CLOCK: TIMESTAMP = 0x{r.U32(4):X8}";

            case 0xDF when d.Length >= 8: // GET_DAQ_LIST_MODE
                return $"GET_DAQ_LIST_MODE: MODE = 0x{d[1]:X2}{DaqListMode(d[1])}|EVENT_CHANNEL_NUMBER = 0x{r.U16(4):X4}" +
                       $"|PRESCALER = 0x{d[6]:X2}|PRIORITY = 0x{d[7]:X2}";

            case 0xF2 when d.Length >= 8 && IsGetSlaveId(request): // TRANSPORT_LAYER_CMD / GET_SLAVE_ID
                return $"GET_SLAVE_ID: MAGIC = '{Ascii(d, 1, 3)}'|CAN_ID = 0x{r.U32(4):X8}";

            default:
            {
                string name = CommandName(cmd.Value, request);
                return d.Length > 1 ? $"{name}: DATA = {Hex(d, 1)}" : name;
            }
        }
    }

    private static bool IsGetSlaveId(byte[]? request) => request is { Length: > 1 } p && p[1] == 0xFF;

    // ---------------- DAQ / STIM data objects ----------------

    private FrameAnnotation DecodeDto(CanFrame f, bool stim)
    {
        var d = f.Data;
        byte pid = d[0];
        string kind = stim ? "STIM-DTO" : "DAQ-DTO";
        string comment = $"Data length: {d.Length}";
        // Every ODT of every DAQ list shares the response CAN ID, so the PID is what makes
        // them distinct messages in an aggregate view. Group 0 is reserved for CTO traffic.
        int group = DtoGroup(pid);

        // The identification field layout decides how DAQ/ODT are encoded in the header.
        switch (_identificationFieldType)
        {
            case 0: // absolute ODT number, 1-byte header
                if (TryResolveAbsoluteOdt(pid, out int daq, out int odt))
                    return new FrameAnnotation($"{kind} (DAQ #{daq}|ODT #{odt})", comment, group);
                break;
            case 1: // relative ODT number + absolute DAQ list number (byte)
                if (d.Length >= 2)
                    return new FrameAnnotation($"{kind} (DAQ #{d[1]}|ODT #{pid})", comment, group);
                break;
            case 2: // relative ODT number + absolute DAQ list number (word): [PID][DAQ word]
                if (d.Length >= 3)
                    return new FrameAnnotation($"{kind} (DAQ #{new Rd(d, _bigEndian).U16(1)}|ODT #{pid})", comment, group);
                break;
            default: // 3: the same, word-aligned — [PID][FILL][DAQ word], so the word is at 2
                if (d.Length >= 4)
                    return new FrameAnnotation($"{kind} (DAQ #{new Rd(d, _bigEndian).U16(2)}|ODT #{pid})", comment, group);
                break;
        }

        // Not enough context — say so rather than inventing DAQ/ODT numbers. The PID still
        // separates the rows, so the view stays usable even mid-session.
        string why = _daqCount == 0
            ? "DAQ allocation not seen in capture"
            : "PID outside the allocated ODT range";
        return new FrameAnnotation($"{kind} (PID = 0x{pid:X2})", $"{comment}|{why}", group);
    }

    /// <summary>Aggregate-view group for a DTO. Offset by one so 0 stays "CTO / not split".</summary>
    private static int DtoGroup(byte pid) => pid + 1;

    /// <summary>
    /// Absolute ODT numbers run consecutively across DAQ lists, so the ALLOC_ODT counts
    /// collected during configuration are what turns a PID back into DAQ/ODT.
    /// </summary>
    private bool TryResolveAbsoluteOdt(byte pid, out int daq, out int odt)
    {
        int firstAbsolute = 0;
        for (int list = 0; list < _daqCount; list++)
        {
            if (!_odtCounts.TryGetValue(list, out int n)) break;
            if (pid < firstAbsolute + n)
            {
                daq = list;
                odt = pid - firstAbsolute;
                return true;
            }
            firstAbsolute += n;
        }
        daq = odt = -1;
        return false;
    }

    private void ResetSession()
    {
        _bigEndian = false;
        _identificationFieldType = 0;
        _daqKeyKnown = false;
        _daqCount = 0;
        _odtCounts.Clear();
        _pendingCmd = null;
        _pendingData = null;
    }

    /// <summary>Snapshot of what the decoder has learned, for the UI status line.</summary>
    public string Describe()
    {
        lock (_lock)
        {
            var sb = new StringBuilder(_config.ToString());
            if (_daqCount > 0) sb.Append($" — {_daqCount} DAQ list(s), {_odtCounts.Values.Sum()} ODT(s)");
            if (_daqKeyKnown) sb.Append($", id field: {IdFieldName(_identificationFieldType)}");
            if (_bigEndian) sb.Append(", Motorola byte order");
            return sb.ToString();
        }
    }

    // ---------------- helpers ----------------

    private static string IdFieldName(byte type) => type switch
    {
        0 => "absolute ODT",
        1 => "relative ODT + DAQ (byte)",
        2 => "relative ODT + DAQ (word)",
        _ => "relative ODT + DAQ (word, aligned)",
    };

    private static string DaqListMode(byte mode)
    {
        var parts = new List<string>(4);
        if ((mode & 0x01) != 0) parts.Add("SELECTED");
        if ((mode & 0x02) != 0) parts.Add("DIRECTION=STIM");
        if ((mode & 0x10) != 0) parts.Add("TIMESTAMP");
        if ((mode & 0x20) != 0) parts.Add("PID_OFF");
        if ((mode & 0x40) != 0) parts.Add("RUNNING");
        if ((mode & 0x80) != 0) parts.Add("RESUME");
        return parts.Count == 0 ? "" : $" ({string.Join(",", parts)})";
    }

    private static string StartStopList(byte mode) => mode switch
    {
        0 => "stop",
        1 => "start",
        2 => "select",
        _ => $"0x{mode:X2}",
    };

    private static string StartStopSynch(byte mode) => mode switch
    {
        0 => "stop all",
        1 => "start selected",
        2 => "stop selected",
        3 => "prepare selected",
        _ => $"0x{mode:X2}",
    };

    private static string Hex(byte[] d, int from) =>
        from >= d.Length ? "" : Convert.ToHexString(d, from, d.Length - from);

    private static string Ascii(byte[] d, int from, int count)
    {
        if (from >= d.Length) return "";
        count = Math.Min(count, d.Length - from);
        var sb = new StringBuilder(count);
        for (int i = from; i < from + count; i++)
            sb.Append(d[i] is >= 0x20 and < 0x7F ? (char)d[i] : '.');
        return sb.ToString();
    }

    /// <summary>Byte-order aware reader that returns 0 past the end of short frames.</summary>
    private readonly struct Rd(byte[] d, bool bigEndian)
    {
        public byte U8(int i) => i < d.Length ? d[i] : (byte)0;

        public ushort U16(int i) => bigEndian
            ? (ushort)((U8(i) << 8) | U8(i + 1))
            : (ushort)(U8(i) | (U8(i + 1) << 8));

        public uint U32(int i) => bigEndian
            ? ((uint)U8(i) << 24) | ((uint)U8(i + 1) << 16) | ((uint)U8(i + 2) << 8) | U8(i + 3)
            : U8(i) | ((uint)U8(i + 1) << 8) | ((uint)U8(i + 2) << 16) | ((uint)U8(i + 3) << 24);
    }
}
