using System.ComponentModel;
using CanTerminal.Core;

namespace CanTerminal.App;

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
    public string? Decoded { get; init; }

    public static TraceRow From(CanFrame f, string? decoded) => new()
    {
        Time = f.Timestamp.ToString("0.000000"),
        Chan = f.Channel,
        Dir = f.Direction == FrameDirection.Tx ? "TX" : "RX",
        Id = f.IdText,
        Flags = FlagsText(f),
        Dlc = f.Data.Length.ToString(),
        Data = FormatData(f.Data),
        Decoded = decoded,
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

    public static string FormatData(byte[] data) =>
        string.Join(" ", data.Select(b => b.ToString("X2")));
}

/// <summary>Mutable row for the fixed (aggregate-by-ID) view.</summary>
public sealed class FixedRow : INotifyPropertyChanged
{
    private long _count;
    private double _lastTs;

    public FixedRow(CanFrame f, string? decoded)
    {
        Chan = f.Channel;
        Id = f.IdText;
        SortKey = ((ulong)(f.Channel.GetHashCode() & 0xFF) << 32) | f.ArbId;
        Flags = TraceRow.FlagsText(f);
        _count = 1;
        _lastTs = f.Timestamp;
        Dlc = f.Data.Length.ToString();
        Data = TraceRow.FormatData(f.Data);
        Decoded = decoded;
    }

    public string Chan { get; }
    public string Id { get; }
    public ulong SortKey { get; }
    public string Flags { get; private set; }
    public long Count => _count;
    public string PeriodMs { get; private set; } = "";
    public string Dlc { get; private set; }
    public string Data { get; private set; }
    public string? Decoded { get; private set; }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Update(CanFrame f, string? decoded)
    {
        _count++;
        double dt = (f.Timestamp - _lastTs) * 1000.0;
        _lastTs = f.Timestamp;
        PeriodMs = dt is > 0 and < 1_000_000 ? dt.ToString("0.0") : "";
        Flags = TraceRow.FlagsText(f);
        Dlc = f.Data.Length.ToString();
        Data = TraceRow.FormatData(f.Data);
        Decoded = decoded;

        Raise(nameof(Count));
        Raise(nameof(PeriodMs));
        Raise(nameof(Flags));
        Raise(nameof(Dlc));
        Raise(nameof(Data));
        Raise(nameof(Decoded));
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
