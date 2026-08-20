using System.Globalization;
using System.Text;

namespace CanTerminal.Core.Logs;

/// <summary>
/// Reads a Vector ASCII (.asc) trace.
///
/// The format is a header of directives followed by one line per bus event:
///
///     date Thu Jan 2 09:14:24 2025
///     base hex  timestamps absolute
///     Begin Triggerblock Thu Jan 2 09:14:24 2025
///        0.000000 Start of measurement
///          0.012700 1  18DAF110x  Rx   d 8 AD 21 00 00 00 00 00 00
///
/// Two decisions in here are not stylistic:
///
/// * The direction token is found by searching, not by counting columns. A CANoe database
///   export writes the message name between the identifier and the direction, and a
///   position-based parser drops every frame of such a file without raising anything.
/// * Whatever the parser does not understand is counted and sampled rather than ignored. A
///   text format fails by silently matching fewer lines, so the count is the only thing
///   standing between a grammar mismatch and a trace that quietly lost frames.
/// </summary>
public sealed class AscLogReader : ILogReader
{
    public string Description => "Vector ASCII log";

    public string Filter => "Vector ASCII log (*.asc)|*.asc";

    public bool CanRead(string path) =>
        System.IO.Path.GetExtension(path).Equals(".asc", StringComparison.OrdinalIgnoreCase);

    /// <summary>Verbatim skipped lines kept for the report. Enough to see a pattern, not a second copy of the file.</summary>
    private const int MaxSamples = 12;

    public LogFile Read(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        var frames = new List<CanFrame>(1 << 16);
        var channels = new SortedSet<int>();
        var skippedByShape = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new List<string>();
        int skipped = 0;

        DateTime? startWall = null;
        bool baseHex = true;             // "base hex" is the overwhelming default, and the
                                         // header states it either way; only a file that says
                                         // "base dec" changes this.
        bool relativeTimestamps = false;
        double runningTime = 0;          // only used when the header says relative
        double first = 0, last = 0;
        bool haveFirst = false;

        long length = new FileInfo(path).Length;
        long consumed = 0;
        int sinceReport = 0;

        // Byte-level so the payload never becomes a string. The format is ASCII; a UTF-8 comment
        // in the header survives because header text is decoded separately.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        var scratch = new byte[64];

        foreach (var line in ReadLines(stream))
        {
            consumed += line.Length + 1;
            if (++sinceReport >= 4096)
            {
                sinceReport = 0;
                ct.ThrowIfCancellationRequested();
                progress?.Report(length == 0 ? 1 : Math.Min(1, consumed / (double)length));
            }

            var l = Trim(line);
            if (l.IsEmpty) continue;

            // Header directives and block markers. Recognised, so they are not counted as
            // "not understood" — that count means something only if it stays at zero on a file
            // the parser genuinely handles.
            if (IsDirective(l, out var directive))
            {
                switch (directive)
                {
                    case Directive.Date:
                        startWall = ParseDate(l);
                        break;
                    case Directive.Base:
                        ApplyBaseLine(l, ref baseHex, ref relativeTimestamps);
                        break;
                }
                continue;
            }

            var parsed = TryParseEvent(l, baseHex, scratch, out var ev);
            if (parsed == Parsed.Frame)
            {
                double ts = relativeTimestamps ? (runningTime += ev.Timestamp) : ev.Timestamp;
                if (!haveFirst) { first = ts; haveFirst = true; }
                last = ts;
                channels.Add(ev.Channel);
                frames.Add(new CanFrame
                {
                    Timestamp = ts,
                    Channel = ChannelName(ev.Channel),
                    ArbId = ev.ArbId,
                    IsExtended = ev.IsExtended,
                    IsRemote = ev.IsRemote,
                    Direction = ev.IsTx ? FrameDirection.Tx : FrameDirection.Rx,
                    Data = ev.DataLength == 0 ? [] : scratch.AsSpan(0, ev.DataLength).ToArray(),
                });
                continue;
            }

            // Everything else. Named by shape so the report says what was dropped, not just
            // how much.
            string shape = parsed == Parsed.Ignorable ? ev.Shape : Shape.Of(l);
            if (shape.Length == 0) continue;              // recognised and deliberately silent
            skipped++;
            skippedByShape[shape] = skippedByShape.GetValueOrDefault(shape) + 1;
            if (samples.Count < MaxSamples) samples.Add(Decode(l));
        }

        progress?.Report(1);

        // A header date that does not sit at the first timestamp means this file was split off a
        // longer session: the board stamps every part with the session start, but part two opens
        // hundreds of seconds in. The time of day is then an inference, and says so.
        bool approximate = startWall is not null && haveFirst && first > 1.0;

        return new LogFile(
            path, frames, channels.Select(ChannelName).ToArray(),
            startWall, approximate, first, last,
            skipped, skippedByShape, samples);
    }

    // ---------------- line shapes ----------------

    private enum Directive { None, Date, Base }

    private enum Parsed { Frame, Ignorable, Unknown }

    private struct Event
    {
        public double Timestamp;
        public int Channel;
        public uint ArbId;
        public bool IsExtended;
        public bool IsRemote;
        public bool IsTx;
        public int DataLength;
        public string Shape;
    }

    /// <summary>Names for the line shapes this reader knows it is not handling.</summary>
    private static class Shape
    {
        public const string ErrorFrame = "error frame";
        public const string CanFd = "CAN FD frame";
        public const string Statistic = "bus statistics";
        public const string TxRequest = "TxRq (transmit request, reported again on success)";
        public const string Unparsed = "unrecognised";

        /// <summary>Classifies a line the event parser rejected. Empty means "recognised, ignore silently".</summary>
        public static string Of(ReadOnlySpan<byte> l)
        {
            if (StartsWith(l, "//")) return "";                       // comment
            if (Contains(l, "Start of measurement")) return "";
            if (Contains(l, "Begin Trigger") || Contains(l, "End Trigger")) return "";
            if (Contains(l, "End TriggerBlock")) return "";
            if (Contains(l, "Statistic")) return Statistic;
            if (Contains(l, "ErrorFrame") || Contains(l, "Error Frame")) return ErrorFrame;
            if (Contains(l, "CANFD") || Contains(l, "CAN FD")) return CanFd;
            if (Contains(l, "Trigger Event")) return "";
            return Unparsed;
        }
    }

    private static bool IsDirective(ReadOnlySpan<byte> l, out Directive which)
    {
        if (StartsWith(l, "date ")) { which = Directive.Date; return true; }
        if (StartsWith(l, "base ")) { which = Directive.Base; return true; }
        which = Directive.None;
        // Recognised header noise that carries nothing we need.
        return StartsWith(l, "internal events") || StartsWith(l, "no internal events")
            || StartsWith(l, "Measurement UUID") || StartsWith(l, "// version")
            || StartsWith(l, "absolute timestamps") || StartsWith(l, "version ");
    }

    /// <summary>
    /// "base hex  timestamps absolute". Both halves matter: under "base dec" the identifier and
    /// the payload are decimal, and a parser that assumes hex reads 256 as 0x256 and turns eight
    /// decimal values into eleven bytes — with no error at any point.
    /// </summary>
    private static void ApplyBaseLine(ReadOnlySpan<byte> l, ref bool baseHex, ref bool relative)
    {
        if (Contains(l, "base dec")) baseHex = false;
        else if (Contains(l, "base hex")) baseHex = true;
        if (Contains(l, "timestamps relative")) relative = true;
        else if (Contains(l, "timestamps absolute")) relative = false;
    }

    private static DateTime? ParseDate(ReadOnlySpan<byte> l)
    {
        string text = Decode(l)[5..].Trim();
        string[] formats =
        [
            "ddd MMM d HH:mm:ss yyyy", "ddd MMM dd HH:mm:ss yyyy",
            "ddd MMM d HH:mm:ss.fff yyyy", "ddd MMM dd HH:mm:ss.fff yyyy",
            "ddd MMM d hh:mm:ss tt yyyy", "ddd MMM dd hh:mm:ss tt yyyy",
        ];
        return DateTime.TryParseExact(text, formats, CultureInfo.InvariantCulture,
                                      DateTimeStyles.AllowWhiteSpaces, out var d)
            ? d
            : DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AllowWhiteSpaces, out d) ? d : null;
    }

    // ---------------- the event line ----------------

    private static readonly double[] Pow10 = [1, 1e1, 1e2, 1e3, 1e4, 1e5, 1e6, 1e7, 1e8, 1e9, 1e10];

    private static Parsed TryParseEvent(ReadOnlySpan<byte> l, bool baseHex, Span<byte> data, out Event ev)
    {
        ev = default;
        ev.Shape = Shape.Unparsed;
        int i = 0, n = l.Length;

        SkipSpace(l, ref i);

        // The whole literal is accumulated as one integer scaled by 10^digits and divided once.
        // Reconstructing it as whole + frac/10^d rounds twice and lands a bit away from what
        // double.Parse would give, which shows up as a timestamp that will not compare equal to
        // the same value read any other way.
        long scaled = 0; int digits = 0; bool any = false;
        while (i < n && IsDigit(l[i])) { scaled = scaled * 10 + (l[i] - '0'); i++; any = true; }
        if (!any) return Fail(out ev);
        if (i < n && l[i] == (byte)'.')
        {
            i++;
            while (i < n && IsDigit(l[i])) { scaled = scaled * 10 + (l[i] - '0'); digits++; i++; }
            if (digits >= Pow10.Length) return Fail(out ev);
        }
        ev.Timestamp = scaled / Pow10[digits];
        if (i >= n || l[i] != (byte)' ') return Fail(out ev);
        SkipSpace(l, ref i);

        // A CAN FD line puts the protocol keyword where the channel number would be.
        if (StartsWith(l[i..], "CANFD") || StartsWith(l[i..], "CAN FD"))
            return Ignorable(out ev, Shape.CanFd);

        int c0 = i, channel = 0;
        while (i < n && IsDigit(l[i])) { channel = channel * 10 + (l[i] - '0'); i++; }
        if (i == c0) return Fail(out ev);                 // "Start of measurement" and friends
        ev.Channel = channel;
        SkipSpace(l, ref i);

        int idStart = i;
        uint id = 0;
        bool ext = false;
        while (i < n && l[i] != (byte)' ')
        {
            byte c = l[i];
            if (c is (byte)'x' or (byte)'X') { ext = true; i++; break; }
            int d = baseHex ? HexDigit(c) : DecDigit(c);
            if (d < 0) return Fail(out ev);
            id = baseHex ? (id << 4) | (uint)d : (id * 10) + (uint)d;
            i++;
        }
        if (i == idStart) return Fail(out ev);
        ev.ArbId = id;
        ev.IsExtended = ext;

        // Search for the direction rather than assuming it is the next column. A database export
        // writes the symbolic message name in between, and dropping those lines would lose every
        // frame in such a file without a word.
        if (!FindDirection(l, ref i, out bool tx, out bool txRequest)) return Fail(out ev);
        if (txRequest) return Ignorable(out ev, Shape.TxRequest);
        ev.IsTx = tx;

        SkipSpace(l, ref i);
        if (i >= n) return Fail(out ev);

        // 'd' = data frame, 'r' = remote frame (which carries a length but no bytes).
        bool remote;
        if (l[i] is (byte)'d' or (byte)'D') remote = false;
        else if (l[i] is (byte)'r' or (byte)'R') remote = true;
        else return Fail(out ev);
        i++;
        if (i < n && l[i] != (byte)' ') return Fail(out ev);
        ev.IsRemote = remote;
        SkipSpace(l, ref i);

        int dlc = 0, d0 = i;
        while (i < n && IsDigit(l[i])) { dlc = dlc * 10 + (l[i] - '0'); i++; }
        if (i == d0)
        {
            // A remote frame may stop before its length.
            if (remote) { ev.DataLength = 0; return Parsed.Frame; }
            return Fail(out ev);
        }

        if (remote) { ev.DataLength = 0; return Parsed.Frame; }
        if (dlc > 8) return Ignorable(out ev, Shape.CanFd);   // classic line claiming an FD length

        // Exactly DLC bytes. Reading to the end of the line instead would swallow the trailing
        // metadata some writers append — "BitCount = 143" contributes a 'B' that scans as 0x0B.
        int count = 0;
        while (count < dlc)
        {
            SkipSpace(l, ref i);
            if (i >= n) return Fail(out ev);
            int hi = HexDigit(l[i]);
            if (hi < 0) return Fail(out ev);
            i++;
            int value = hi;
            if (i < n && HexDigit(l[i]) is var lo && lo >= 0) { value = (value << 4) | lo; i++; }
            data[count++] = (byte)value;
        }
        ev.DataLength = count;
        return Parsed.Frame;

        static Parsed Fail(out Event e) { e = default; e.Shape = Shape.Unparsed; return Parsed.Unknown; }
        static Parsed Ignorable(out Event e, string shape) { e = default; e.Shape = shape; return Parsed.Ignorable; }
    }

    /// <summary>
    /// Advances past the Rx / Tx / TxRq token, skipping any columns before it. TxRq is a
    /// transmit *request*: the controller reports the frame again when it actually goes out, so
    /// counting both would double every transmitted frame.
    /// </summary>
    private static bool FindDirection(ReadOnlySpan<byte> l, ref int i, out bool tx, out bool txRequest)
    {
        tx = false; txRequest = false;
        int n = l.Length;
        for (int guard = 0; guard < 8; guard++)
        {
            SkipSpace(l, ref i);
            if (i >= n) return false;
            int start = i;
            while (i < n && l[i] != (byte)' ') i++;
            var token = l[start..i];
            if (Equals(token, "Rx")) { tx = false; return true; }
            if (Equals(token, "Tx")) { tx = true; return true; }
            if (Equals(token, "TxRq")) { txRequest = true; return true; }
            // Anything else here is a symbolic message name; keep looking.
        }
        return false;
    }

    // ---------------- byte helpers ----------------

    private static IEnumerable<byte[]> ReadLines(Stream stream)
    {
        var buffer = new byte[1 << 16];
        var line = new byte[512];
        int lineLength = 0;
        int read;
        while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
        {
            for (int i = 0; i < read; i++)
            {
                byte b = buffer[i];
                if (b == (byte)'\n')
                {
                    yield return Copy(line, lineLength);
                    lineLength = 0;
                    continue;
                }
                if (lineLength == line.Length) Array.Resize(ref line, line.Length * 2);
                line[lineLength++] = b;
            }
        }
        if (lineLength > 0) yield return Copy(line, lineLength);

        static byte[] Copy(byte[] src, int length) => src.AsSpan(0, length).ToArray();
    }

    /// <summary>Drops the trailing CR of a CRLF file and any surrounding blanks.</summary>
    private static ReadOnlySpan<byte> Trim(ReadOnlySpan<byte> l)
    {
        int end = l.Length;
        while (end > 0 && (l[end - 1] == (byte)'\r' || l[end - 1] == (byte)' ' || l[end - 1] == (byte)'\t')) end--;
        int start = 0;
        while (start < end && (l[start] == (byte)' ' || l[start] == (byte)'\t')) start++;
        return l[start..end];
    }

    private static string ChannelName(int channel) => "CAN" + channel.ToString(CultureInfo.InvariantCulture);

    private static string Decode(ReadOnlySpan<byte> l) => Encoding.UTF8.GetString(l);

    private static bool IsDigit(byte c) => (uint)(c - '0') <= 9;

    private static int DecDigit(byte c) => IsDigit(c) ? c - '0' : -1;

    private static int HexDigit(byte c)
    {
        if ((uint)(c - '0') <= 9) return c - '0';
        int v = (c | 0x20) - 'a';
        return (uint)v <= 5 ? v + 10 : -1;
    }

    private static void SkipSpace(ReadOnlySpan<byte> l, ref int i)
    {
        while (i < l.Length && (l[i] == (byte)' ' || l[i] == (byte)'\t')) i++;
    }

    private static bool StartsWith(ReadOnlySpan<byte> l, string text)
    {
        if (l.Length < text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (l[i] != (byte)text[i]) return false;
        return true;
    }

    private static bool Equals(ReadOnlySpan<byte> token, string text)
    {
        if (token.Length != text.Length) return false;
        for (int i = 0; i < text.Length; i++) if (token[i] != (byte)text[i]) return false;
        return true;
    }

    private static bool Contains(ReadOnlySpan<byte> l, string text)
    {
        for (int i = 0; i + text.Length <= l.Length; i++)
            if (StartsWith(l[i..], text)) return true;
        return false;
    }
}
