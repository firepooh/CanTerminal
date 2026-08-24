using System.Diagnostics;
using System.IO.Ports;
using System.Text;
using Microsoft.Win32;

namespace CanTerminal.Core.Slcan;

/// <summary>
/// SLCAN serial adapter for the WeAct USB2CANFDV2 (and other CANable-2-derived firmware) on a
/// USB CDC serial port. Single CAN channel, classic + CAN FD.
///
/// Two properties of this transport shape the code:
///
/// * The device has no clock to give us — SLCAN lines carry no timestamp — so frames are stamped
///   with a host stopwatch started at open. Same contract the virtual bus already lives by:
///   monotonic seconds within the session.
///
/// * The firmware acknowledges a transmit command only when the frame has actually gone out on
///   the bus (CR), or failed there (BELL). Acks carry no payload, so they are matched to sends
///   by order; the TX echo the rest of the program expects is published when the ack arrives —
///   which makes the echo mean "it was on the wire", not "it left this process".
/// </summary>
public sealed class SlcanAdapter : ICanAdapter
{
    /// <summary>USB identity of an ST CDC port — what the WeAct firmware enumerates as.</summary>
    private const string UsbEnumKey = @"SYSTEM\CurrentControlSet\Enum\USB\VID_0483&PID_5740";

    /// <summary>
    /// Sends allowed to sit unacknowledged. The firmware's own TX queue is 32 slots; past that a
    /// send would only pile onto a bus that is not draining, so refusing is the honest answer.
    /// </summary>
    private const int MaxPendingTx = 64;

    /// <summary>
    /// How long <see cref="Send"/> waits for the firmware's verdict on the frame it just wrote.
    ///
    /// Generous next to what it costs when the bus is healthy — a frame is on the wire and
    /// acknowledged inside a couple of milliseconds — and bounded when it is not. The wait is
    /// the whole point: without it the caller was told the frame was sent while the firmware
    /// was still finding out, and a frame no other node acknowledged reported success to python
    /// and MCP clients with the failure visible only on screen.
    /// </summary>
    private const int TxAckTimeoutMs = 250;

    private readonly object _txLock = new();
    private readonly Queue<PendingTx> _pendingTx = new();

    private SerialPort? _port;
    private Thread? _rxThread;
    private volatile bool _running;
    private volatile bool _open;
    private Stopwatch _clock = new();
    private string _channel = "CAN1";
    private string? _deviceVersion;     // the V-command reply, e.g. "WeAct Studio V1.0.0.3_bb264e71"
    private bool _reportedUnknownLine;

    /// <summary>
    /// A frame written to the device and still waiting for its ack. Acks carry nothing to
    /// identify themselves, so they are matched to sends in order; <see cref="Gate"/> is how the
    /// RX thread hands the verdict back to whoever is blocked in <see cref="Send"/>.
    /// </summary>
    private sealed class PendingTx(uint id, byte[] data, bool extended, bool fd, bool brs, string? source)
    {
        public uint Id { get; } = id;
        public byte[] Data { get; } = data;
        public bool Extended { get; } = extended;
        public bool Fd { get; } = fd;
        public bool Brs { get; } = brs;
        public string? Source { get; } = source;

        public object Gate { get; } = new();
        public bool Settled;
        public bool Failed;

        /// <summary>Set when the sender stopped waiting. The entry stays queued regardless —
        /// dropping it would shift every later ack onto the wrong frame.</summary>
        public bool Abandoned;
    }

    public SlcanAdapter(string portName) => PortName = portName;

    public string PortName { get; }

    public string Name => _deviceVersion is { } v ? $"{v} ({PortName})" : $"SLCAN ({PortName})";

    public bool IsOpen => _open;

    public IReadOnlyList<string> Channels { get; private set; } = [];

    public event Action<CanFrame>? FrameReceived;
    public event Action<string>? ErrorOccurred;

    /// <summary>
    /// COM ports that enumerate under the ST CDC identity the WeAct firmware uses, present right
    /// now. The registry remembers unplugged devices, so its port names are intersected with the
    /// ports that actually exist. The identity is generic ST, not WeAct-specific — which is why
    /// <see cref="Open"/> still verifies the device answers the SLCAN version query before use.
    /// </summary>
    public static List<string> FindPorts()
    {
        var result = new List<string>();
        if (!OperatingSystem.IsWindows()) return result;   // the registry walk below is Windows-only
        try
        {
            var present = SerialPort.GetPortNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
            using var key = Registry.LocalMachine.OpenSubKey(UsbEnumKey);
            if (key is null) return result;
            foreach (var instance in key.GetSubKeyNames())
            {
                using var parameters = key.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("PortName") is string port && present.Contains(port))
                    result.Add(port);
            }
            result.Sort(StringComparer.OrdinalIgnoreCase);
        }
        catch
        {
            // A scan that cannot read the registry just finds nothing; it must not take
            // Refresh devices down with it.
        }
        return result;
    }

    public void Open(IReadOnlyList<CanChannelConfig> channels)
    {
        if (_open) throw new InvalidOperationException("Already open.");
        if (channels.Count != 1)
            throw new ArgumentException(
                $"{PortName} is a single-channel device — set Bus ▸ Channels… to exactly one entry " +
                $"(e.g. CAN1), not '{string.Join(",", channels.Select(c => c.Name))}'.");

        var cfg = channels[0];
        // Resolved before the port is touched, so an unsupported speed fails without side effects.
        char nominal = SlcanProtocol.NominalBitrateCode(cfg.Bitrate);
        char? data = cfg.Fd ? SlcanProtocol.DataBitrateCode(cfg.FdBitrate) : null;

        // The port is USB CDC: the baud rate is ignored by the firmware and DTR is not required,
        // but some CDC stacks hold data back until DTR is raised, so raise it.
        var port = new SerialPort(PortName, 115200)
        {
            ReadTimeout = 200,
            WriteTimeout = 500,
            Encoding = Encoding.ASCII,
            DtrEnable = true,
        };
        try
        {
            port.Open();

            // A bare CR flushes whatever half-command a previous owner of the port left in the
            // firmware's input buffer; the BELL it answers with is drained along with anything
            // else already queued our way.
            port.Write("\r");
            Drain(port);

            // Close the channel a crashed host may have left open — while it is open, a busy
            // bus streams frame lines straight into this handshake — then discard whatever was
            // in flight. Only on a quiet line is a text reply attributable to its query.
            Command(port, "C", tolerateError: true);
            Drain(port);

            // The USB identity is generic ST CDC, so prove this is an SLCAN device before
            // configuring it — the version query is the one command with a text reply.
            _deviceVersion = QueryLine(port, "V")
                ?? throw new InvalidOperationException(
                    $"{PortName} did not answer the SLCAN version query — not an SLCAN device, " +
                    "or another program is holding it.");

            Command(port, $"S{nominal}");
            if (data is { } y) Command(port, $"Y{y}");
            Command(port, "O");

            _channel = cfg.Name.ToUpperInvariant();
            Channels = [_channel];
            _clock = Stopwatch.StartNew();
            _reportedUnknownLine = false;
            _port = port;
            _running = true;
            _open = true;
            _rxThread = new Thread(RxLoop) { IsBackground = true, Name = "slcan-rx" };
            _rxThread.Start();
        }
        catch
        {
            try { port.Dispose(); } catch { }
            throw;
        }
    }

    public void Close()
    {
        _open = false;
        _running = false;
        if (_rxThread is { } rx)
        {
            rx.Join(2000);
            _rxThread = null;
        }
        PendingTx[] stranded;
        lock (_txLock)
        {
            if (_port is { } port)
            {
                try { if (port.IsOpen) port.Write("C\r"); } catch { }
                try { port.Dispose(); } catch { }
                _port = null;
            }
            stranded = [.. _pendingTx];
            _pendingTx.Clear();
        }
        // Their acks can no longer arrive, so release the senders now rather than leaving each
        // of them to sit out the full ack timeout on a port that is already gone.
        foreach (var tx in stranded) Settle(tx, failed: true);
        Channels = [];
    }

    public void Send(string channel, uint arbId, byte[] data, bool extended = false, bool fd = false, bool brs = false, string? source = null)
    {
        if (!_open) throw new InvalidOperationException("Device not open.");
        if (!channel.Equals(_channel, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException($"Channel '{channel}' not configured. This device carries {_channel}.");
        CanFrame.ValidatePayload(data, fd);

        // What actually goes on the wire: FD payloads land on the next legal FD length. The
        // padded copy is also what the echo reports, because that is what the bus carried.
        byte[] payload = fd ? SlcanProtocol.PadToFd(data) : (byte[])data.Clone();
        string line = SlcanProtocol.BuildTxLine(arbId, payload, extended, fd, brs);

        // Write and enqueue under one lock, the same lock the RX thread takes to consume an ack —
        // so an ack can never be processed between the write and its bookkeeping.
        PendingTx pending;
        lock (_txLock)
        {
            if (_port is not { } port || !_open) throw new InvalidOperationException("Device not open.");
            if (_pendingTx.Count >= MaxPendingTx)
                throw new InvalidOperationException(
                    $"{_pendingTx.Count} frames are still waiting for the bus — it is not accepting traffic.");
            port.Write(line + "\r");
            pending = new PendingTx(arbId, payload, extended, fd, brs, source);
            _pendingTx.Enqueue(pending);
        }

        // Then wait for the firmware to say what became of it — outside the lock, which the RX
        // thread needs in order to answer. The TX echo is published by that thread before it
        // settles this, so a Send that returns means the frame is both on the bus and already
        // visible to everything reading the capture.
        lock (pending.Gate)
        {
            if (!pending.Settled) Monitor.Wait(pending.Gate, TxAckTimeoutMs);
            if (!pending.Settled)
            {
                pending.Abandoned = true;
                throw new TimeoutException(
                    $"{PortName} did not confirm the transmission of 0x{arbId:X} within {TxAckTimeoutMs} ms. " +
                    "It may still go out — a heavily loaded bus can hold a frame that long.");
            }
            if (pending.Failed)
                throw new InvalidOperationException(
                    $"0x{arbId:X} was not transmitted: the bus did not acknowledge it. " +
                    "Nothing else is listening on it, or the bitrate does not match.");
        }
    }

    /// <summary>Hands the firmware's verdict to a blocked <see cref="Send"/>. True if nobody was
    /// waiting any more, in which case the caller reports it instead.</summary>
    private static bool Settle(PendingTx pending, bool failed)
    {
        lock (pending.Gate)
        {
            pending.Settled = true;
            pending.Failed = failed;
            Monitor.Pulse(pending.Gate);
            return pending.Abandoned;
        }
    }

    private void RxLoop()
    {
        var buffer = new byte[4096];
        var line = new StringBuilder(160);
        var port = _port!;
        while (_running)
        {
            int count;
            try
            {
                count = port.Read(buffer, 0, buffer.Length);
            }
            catch (TimeoutException) { continue; }
            catch (Exception ex)
            {
                // Unplugging the cable lands here. The adapter stays "open" so the user sees the
                // report and disconnects deliberately, rather than the state changing under them.
                if (_running) ErrorOccurred?.Invoke($"{PortName} read failed ({ex.Message}) — device unplugged?");
                break;
            }

            // Same guard as the Intrepid RX loop: a throwing subscriber must cost this batch,
            // not the process the capture lives in.
            try
            {
                for (int i = 0; i < count; i++)
                {
                    byte b = buffer[i];
                    if (b == 0x07) { OnTxFailed(); continue; }          // BELL: a send died on the bus
                    if (b != 0x0D)
                    {
                        if (line.Length < 160) line.Append((char)b);    // an FD line tops out at 138
                        continue;
                    }
                    if (line.Length == 0) { OnTxDone(); continue; }     // bare CR: a send reached the bus
                    string token = line.ToString();
                    line.Clear();
                    OnLine(token);
                }
            }
            catch (Exception ex)
            {
                line.Clear();
                ErrorOccurred?.Invoke($"RX processing failed ({ex.Message}) — still receiving.");
            }
        }
    }

    private void OnLine(string token)
    {
        if (SlcanProtocol.TryParseRxLine(token, out var rx))
        {
            FrameReceived?.Invoke(new CanFrame
            {
                Timestamp = _clock.Elapsed.TotalSeconds,
                Channel = _channel,
                ArbId = rx.Id,
                IsExtended = rx.Extended,
                IsFd = rx.Fd,
                IsBrs = rx.Brs,
                IsRemote = rx.Remote,
                Direction = FrameDirection.Rx,
                Data = rx.Data,
            });
            return;
        }
        // Said once, not per line: a firmware whose grammar this parser does not know would
        // otherwise flood the status bar while silently dropping every frame.
        if (_reportedUnknownLine) return;
        _reportedUnknownLine = true;
        ErrorOccurred?.Invoke($"{PortName} sent a line this build does not understand: '{token}'. " +
                              "Such lines are dropped; only the first is reported.");
    }

    private void OnTxDone()
    {
        PendingTx? done = null;
        lock (_txLock)
        {
            if (_pendingTx.Count > 0) done = _pendingTx.Dequeue();
        }
        // A CR with nothing pending is the ack of a command from before the RX thread existed
        // (or a repeat); there is nothing truthful to publish for it.
        if (done is not { } tx) return;

        // Published before the sender is released, so a Send that returns has already put its
        // frame in front of every reader of the capture.
        FrameReceived?.Invoke(new CanFrame
        {
            Timestamp = _clock.Elapsed.TotalSeconds,
            Channel = _channel,
            ArbId = tx.Id,
            IsExtended = tx.Extended,
            IsFd = tx.Fd,
            IsBrs = tx.Brs,
            Direction = FrameDirection.Tx,
            Data = tx.Data,
            Source = tx.Source,
        });
        Settle(tx, failed: false);
    }

    private void OnTxFailed()
    {
        PendingTx? failed = null;
        lock (_txLock)
        {
            if (_pendingTx.Count > 0) failed = _pendingTx.Dequeue();
        }
        if (failed is not { } tx)
        {
            ErrorOccurred?.Invoke($"{PortName} reported an error (BELL).");
            return;
        }
        // Whoever sent it is waiting and gets this as an exception — saying it twice would put
        // the same failure in the status line and a dialog. Only an abandoned send, whose sender
        // has already given up, still needs reporting here.
        if (Settle(tx, failed: true))
            ErrorOccurred?.Invoke(
                $"TX of 0x{tx.Id:X} failed on the bus (no ACK — nothing else on the bus, or wrong bitrate). " +
                "The frame was not sent.");
    }

    /// <summary>
    /// Sends a command and reads its ack. An ack is a *bare* CR — one with no content in front
    /// of it — or BELL for failure. Frame lines end in CR too, and on a channel a crashed host
    /// left open they keep streaming while this handshake runs, so anything carrying content is
    /// residue to skip, never an answer. Accepting the first CR of any kind put a frame line's
    /// terminator where an ack belonged and desynchronized every later command by one reply.
    /// </summary>
    private static void Command(SerialPort port, string command, bool tolerateError = false)
    {
        port.Write(command + "\r");
        var deadline = Stopwatch.StartNew();
        int contentLength = 0;
        while (deadline.ElapsedMilliseconds < 1000)
        {
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (b == 0x07)
            {
                if (tolerateError) return;
                throw new InvalidOperationException($"The device rejected '{command}'.");
            }
            if (b != 0x0D) { contentLength++; continue; }
            if (contentLength == 0) return;             // bare CR: the ack
            contentLength = 0;                          // a frame line or stale text — skip it
        }
        throw new TimeoutException($"No answer to '{command}' from {port.PortName}.");
    }

    /// <summary>
    /// Sends a query whose reply is a CR-terminated text line (only V has one). Bare CRs (stale
    /// acks) and anything shaped like a frame line are skipped for the same reason as in
    /// <see cref="Command"/>: they can be in flight from before the query, and taking one as
    /// the answer names the adapter after a captured frame.
    /// </summary>
    private static string? QueryLine(SerialPort port, string command)
    {
        port.Write(command + "\r");
        var text = new StringBuilder(64);
        var deadline = Stopwatch.StartNew();
        while (deadline.ElapsedMilliseconds < 1000)
        {
            int b;
            try { b = port.ReadByte(); }
            catch (TimeoutException) { continue; }
            if (b == 0x07) return null;                     // command unknown to this firmware
            if (b != 0x0D)
            {
                if (text.Length < 160) text.Append((char)b);
                continue;
            }
            string token = text.ToString();
            text.Clear();
            if (token.Length == 0 || SlcanProtocol.TryParseRxLine(token, out _)) continue;
            return token;
        }
        return null;
    }

    /// <summary>Discards whatever the device has already queued our way.</summary>
    private static void Drain(SerialPort port)
    {
        Thread.Sleep(100);
        try { port.DiscardInBuffer(); } catch { }
    }

    public void Dispose() => Close();
}
