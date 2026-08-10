using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;
using CanTerminal.Core;

namespace CanTerminal.App;

/// <summary>
/// Deterministic tint per channel, so a channel reads the same in every view and across runs.
/// Known channels follow the adapter's channel map; anything else falls back to a stable hash
/// (string.GetHashCode is randomised per process and would change colours between runs).
/// </summary>
internal static class ChannelPalette
{
    private static readonly string[] Known = ["CAN1", "CAN2", "CAN3", "CAN4", "MSCAN", "SWCAN"];

    private static readonly Brush[] Tints = Freeze(
        "#D8E6FA", "#D7F0DC", "#FBE7CC", "#EADDF7", "#FBD9DD", "#D3EEF1");

    public static Brush Tint(string channel)
    {
        int i = KnownIndex(channel);
        return Tints[i >= 0 ? i : StableHash(channel) % Tints.Length];
    }

    /// <summary>Stable ordinal for sorting, so channels group in the same order every run.</summary>
    public static int Index(string channel)
    {
        int i = KnownIndex(channel);
        return i >= 0 ? i : Known.Length + (StableHash(channel) % 64);
    }

    // Runs per frame, so it compares in place rather than allocating an upper-cased copy.
    private static int KnownIndex(string channel)
    {
        for (int i = 0; i < Known.Length; i++)
            if (string.Equals(Known[i], channel, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    private static int StableHash(string s)
    {
        int h = 0;
        foreach (char c in s) h = (h * 31) + c;
        return h & 0x7FFFFFFF;
    }

    private static Brush[] Freeze(params string[] hex) => hex.Select(h =>
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(h)!);
        b.Freeze();
        return (Brush)b;
    }).ToArray();
}

/// <summary>
/// Colours for the two sides of a request/response protocol.
///
/// The Sender cell is tinted, and slave rows get a background wash so a command and its answer
/// read as a pair down a long initialisation sequence. That wash is deliberately grey: the
/// channel chips already own pastel blue and orange, and a second hue on the same row leaves
/// you unable to say whether a colour means channel or sender.
///
/// The text colours are darker than the blue and orange these protocols are usually drawn in.
/// Those are poster colours — at 12 pt on white the familiar orange lands near 2:1 contrast and
/// simply cannot be read. These clear 5:1, and blue/orange stays distinguishable to the common
/// forms of colour blindness in a way red/green would not.
/// </summary>
internal static class SenderPalette
{
    private static readonly Brush MasterText = Frozen("#1F6FB2");   // 5.3:1 on white
    private static readonly Brush SlaveText = Frozen("#A05F00");    // 5.1:1 on white
    private static readonly Brush SlaveRow = Frozen("#F7F9FB");

    public static Brush Text(string? sender) => sender switch
    {
        "master" => MasterText,
        "slave" => SlaveText,
        _ => SystemColors.ControlTextBrush,
    };

    public static Brush Row(string? sender) =>
        sender == "slave" ? SlaveRow : Brushes.Transparent;

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)!);
        b.Freeze();
        return b;
    }
}

/// <summary>How the Time column reads.</summary>
public enum TimestampMode
{
    /// <summary>Time of day, from a wall clock anchored to the start of the capture.</summary>
    Absolute,
    /// <summary>Seconds since the first frame of the capture.</summary>
    Relative,
    /// <summary>Seconds since the previous row of the same pane.</summary>
    Delta,
}

/// <summary>
/// Where the Time column's three readings come from. The hardware timestamp is the only clock
/// the device gives us, so absolute time is that timestamp carried on a wall-clock reading
/// taken once, at the frame the capture started from — accurate relative to itself, and honest
/// about the offset it inherited.
/// </summary>
/// <param name="ZeroTs">Hardware timestamp of the first frame of the capture.</param>
/// <param name="ZeroWall">Wall clock observed at that frame.</param>
public readonly record struct TimeBase(TimestampMode Mode, double ZeroTs, DateTime ZeroWall)
{
    public string Format(double ts, double delta) => Mode switch
    {
        TimestampMode.Absolute => ZeroWall.AddSeconds(ts - ZeroTs).ToString("HH:mm:ss.ffffff"),
        TimestampMode.Delta => delta.ToString("0.000000"),
        _ => (ts - ZeroTs).ToString("0.000000"),
    };

    public string ColumnHeader => Mode switch
    {
        TimestampMode.Absolute => "Time",
        TimestampMode.Delta => "Δt [s]",
        _ => "Time [s]",
    };
}

/// <summary>Immutable, pre-formatted row for the trace view.</summary>
public sealed class TraceRow
{
    public required string Time { get; init; }
    public required string Chan { get; init; }
    public required string Dir { get; init; }
    public required string Id { get; init; }
    public required string Flags { get; init; }
    public required string Dlc { get; init; }
    public required string Data { get; init; }
    public string? Type { get; init; }
    public string? Decoded { get; init; }
    public string? Sender { get; init; }
    public required Brush ChanTint { get; init; }

    /// <summary>
    /// Precomputed rather than resolved by a style trigger. A DataTrigger on Sender would make
    /// WPF subscribe to this row through PropertyDescriptor — TraceRow has no change
    /// notification — once per cell per realised row, which is the churn that OneTime bindings
    /// were introduced to remove.
    /// </summary>
    public required Brush SenderBrush { get; init; }
    public required Brush RowBackground { get; init; }

    /// <summary>Raw hardware timestamp, kept so the Time column can be re-read in another mode.</summary>
    public required double Ts { get; init; }

    /// <summary>Seconds since the previous row of the pane that built this one.</summary>
    public required double Delta { get; init; }

    public static TraceRow From(CanFrame f, TimeBase time, double previousTs)
    {
        double delta = double.IsNaN(previousTs) ? 0 : f.Timestamp - previousTs;
        return new TraceRow
        {
            Ts = f.Timestamp,
            Delta = delta,
            Time = time.Format(f.Timestamp, delta),
            Chan = f.Channel,
            ChanTint = ChannelPalette.Tint(f.Channel),
            Dir = f.Direction == FrameDirection.Tx ? "TX" : "RX",
            Id = f.IdText,
            Flags = FlagsText(f),
            Dlc = f.Data.Length.ToString(),
            Data = FormatData(f.Data),
            Type = f.Annotation?.Type,
            Decoded = f.Annotation?.Comment,
            Sender = f.Annotation?.Sender,
            SenderBrush = SenderPalette.Text(f.Annotation?.Sender),
            RowBackground = SenderPalette.Row(f.Annotation?.Sender),
        };
    }

    /// <summary>The same row read on a different clock. Everything else is already formatted.</summary>
    public TraceRow WithTime(TimeBase time) => new()
    {
        Ts = Ts, Delta = Delta, Time = time.Format(Ts, Delta),
        Chan = Chan, ChanTint = ChanTint, Dir = Dir, Id = Id, Flags = Flags,
        Dlc = Dlc, Data = Data, Type = Type, Decoded = Decoded, Sender = Sender,
        SenderBrush = SenderBrush, RowBackground = RowBackground,
    };

    public static string FlagsText(CanFrame f)
    {
        var parts = new List<string>(3);
        if (f.IsExtended) parts.Add("EXT");
        if (f.IsFd) parts.Add(f.IsBrs ? "FD+BRS" : "FD");
        if (f.IsRemote) parts.Add("RTR");
        if (f.IsError) parts.Add("ERR");
        return string.Join(" ", parts);
    }

    /// <summary>
    /// "1A 2B 3C". Formatted by hand into one buffer: this runs for every captured frame, and
    /// the LINQ + string.Join version allocated a string per byte on top of the result.
    ///
    /// The stack buffer is sized for a full CAN FD payload and no more. Adapters reject anything
    /// longer (<see cref="CanFrame.ValidatePayload"/>), so the heap path below is unreachable
    /// today — it exists because sizing a stackalloc from data that arrived over a socket is how
    /// a formatter turns into a crash the moment some future adapter forgets to check.
    /// </summary>
    public static string FormatData(byte[] data)
    {
        if (data.Length == 0) return "";
        int chars = (data.Length * 3) - 1;
        Span<char> buffer = chars <= (CanFrame.MaxPayload(fd: true) * 3) - 1
            ? stackalloc char[chars]
            : new char[chars];
        const string Hex = "0123456789ABCDEF";
        int at = 0;
        for (int i = 0; i < data.Length; i++)
        {
            if (i > 0) buffer[at++] = ' ';
            buffer[at++] = Hex[data[i] >> 4];
            buffer[at++] = Hex[data[i] & 0xF];
        }
        return new string(buffer);
    }
}

/// <summary>
/// Background shades a changed byte passes through as the highlight decays, newest first.
/// The last entry is transparent, i.e. "no longer highlighted".
/// </summary>
internal static class FadePalette
{
    private const double StepSeconds = 0.2;

    private static readonly Brush[] Backgrounds = Freeze(
        "#4A7EDC", "#6D97E4", "#90B0EC", "#B0C7F1", "#CBDAF6", "#E0E9FA", "#F0F4FD");

    private static readonly Brush White = Frozen(Colors.White);
    private static readonly Brush Black = Frozen(Colors.Black);

    /// <summary>Step index meaning "fully decayed".</summary>
    public static readonly int Faded = Backgrounds.Length;

    public static int StepFor(double secondsSinceChange)
    {
        int step = (int)(secondsSinceChange / StepSeconds);
        return step < 0 ? 0 : Math.Min(step, Faded);
    }

    public static Brush Background(int step) => step >= Faded ? Brushes.Transparent : Backgrounds[step];

    // The two darkest shades need light text to stay readable.
    public static Brush Foreground(int step) => step < 2 ? White : Black;

    private static Brush[] Freeze(params string[] hex) =>
        hex.Select(h => Frozen((Color)ColorConverter.ConvertFromString(h)!)).ToArray();

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }
}

/// <summary>One data byte in the fixed view, carrying its own decaying "recently changed" highlight.</summary>
public sealed class ByteCell : INotifyPropertyChanged
{
    private byte _value;
    private double _changedAt;
    private int _step = FadePalette.Faded;

    public ByteCell(byte value)
    {
        _value = value;
        Text = value.ToString("X2");
    }

    public string Text { get; private set; }
    public Brush Background => FadePalette.Background(_step);
    public Brush Foreground => FadePalette.Foreground(_step);

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// Applies a new value, starting the highlight when it differs from the current one.
    /// Returns true if the value changed.
    /// </summary>
    public bool Set(byte value, double now, bool highlight)
    {
        if (value == _value) return false;
        _value = value;
        Text = value.ToString("X2");
        Raise(nameof(Text));
        if (highlight)
        {
            _changedAt = now;
            SetStep(0);
        }
        return true;
    }

    /// <summary>Advances the decay. Returns false once there is nothing left to fade.</summary>
    public bool Tick(double now)
    {
        if (_step >= FadePalette.Faded) return false;
        SetStep(FadePalette.StepFor(now - _changedAt));
        return _step < FadePalette.Faded;
    }

    public void ClearHighlight() => SetStep(FadePalette.Faded);

    private void SetStep(int step)
    {
        if (step == _step) return;
        bool foregroundFlips = (step < 2) != (_step < 2);
        _step = step;
        Raise(nameof(Background));
        if (foregroundFlips) Raise(nameof(Foreground));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Mutable row for the fixed (aggregate-by-ID) view.</summary>
public sealed class FixedRow : INotifyPropertyChanged
{
    private long _count;
    private double _lastTs;

    public FixedRow(CanFrame f)
    {
        Chan = f.Channel;
        ChanTint = ChannelPalette.Tint(f.Channel);
        Id = f.IdText;
        // channel (8) | arbitration id (29) | protocol group (11), so the split rows of one
        // CAN ID stay together and sort by PID underneath it.
        SortKey = ((ulong)(ChannelPalette.Index(f.Channel) & 0xFF) << 40)
                  | ((ulong)f.ArbId << 11)
                  | (uint)(f.Annotation?.GroupKey ?? 0);
        Flags = TraceRow.FlagsText(f);
        _count = 1;
        _lastTs = f.Timestamp;
        Dlc = f.Data.Length.ToString();
        Type = f.Annotation?.Type;
        Decoded = f.Annotation?.Comment;
        // The first sighting of an ID has nothing to compare against, so it starts unhighlighted.
        foreach (var b in f.Data) Bytes.Add(new ByteCell(b));
    }

    public string Chan { get; }
    public Brush ChanTint { get; }
    public string Id { get; }
    public ulong SortKey { get; }
    public string Flags { get; private set; }
    public long Count => _count;
    public string PeriodMs { get; private set; } = "";
    public string Dlc { get; private set; }
    public string? Type { get; private set; }
    public string? Decoded { get; private set; }
    public ObservableCollection<ByteCell> Bytes { get; } = [];

    /// <summary>True while at least one byte still has a highlight to decay.</summary>
    public bool Fading { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(CanFrame f, double now, bool highlight)
    {
        _count++;
        double dt = (f.Timestamp - _lastTs) * 1000.0;
        _lastTs = f.Timestamp;
        PeriodMs = dt is > 0 and < 1_000_000 ? dt.ToString("0.0") : "";
        Flags = TraceRow.FlagsText(f);
        Dlc = f.Data.Length.ToString();
        Type = f.Annotation?.Type;
        Decoded = f.Annotation?.Comment;

        var data = f.Data;
        while (Bytes.Count > data.Length) Bytes.RemoveAt(Bytes.Count - 1);
        bool changed = false;
        for (int i = 0; i < data.Length; i++)
        {
            if (i < Bytes.Count) changed |= Bytes[i].Set(data[i], now, highlight);
            else Bytes.Add(new ByteCell(data[i]));   // frame grew: new bytes aren't "changes"
        }
        // Only a real change starts a decay, so a row of constant data costs nothing to tick.
        if (highlight && changed) Fading = true;

        Raise(nameof(Count));
        Raise(nameof(PeriodMs));
        Raise(nameof(Flags));
        Raise(nameof(Dlc));
        Raise(nameof(Type));
        Raise(nameof(Decoded));
    }

    /// <summary>Advances every byte's decay; cheap no-op once the row has fully faded.</summary>
    public void TickFade(double now)
    {
        if (!Fading) return;
        bool any = false;
        foreach (var c in Bytes) any |= c.Tick(now);
        Fading = any;
    }

    public void ClearHighlight()
    {
        foreach (var c in Bytes) c.ClearHighlight();
        Fading = false;
    }

    /// <summary>Space-separated hex, for CSV export.</summary>
    public string DataText => string.Join(" ", Bytes.Select(b => b.Text));

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
