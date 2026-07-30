namespace CanTerminal.Core;

public enum FrameDirection
{
    Rx,
    Tx,
}

/// <summary>A single CAN / CAN FD frame observed on (or sent to) the bus.</summary>
public sealed class CanFrame
{
    /// <summary>Seconds, hardware timebase where available (device-relative, monotonic).</summary>
    public double Timestamp { get; init; }

    /// <summary>PC wall clock when the frame was observed by this process.</summary>
    public DateTime WallClock { get; init; } = DateTime.Now;

    public string Channel { get; init; } = "";
    public uint ArbId { get; init; }
    public bool IsExtended { get; init; }
    public bool IsFd { get; init; }
    public bool IsBrs { get; init; }
    public bool IsRemote { get; init; }
    public bool IsError { get; init; }
    public FrameDirection Direction { get; init; }
    public byte[] Data { get; init; } = [];

    /// <summary>Origin of a TX frame: "ui", "python", "mcp", or null for bus traffic.</summary>
    public string? Source { get; init; }

    public string IdText => IsExtended ? ArbId.ToString("X8") : ArbId.ToString("X3");
    public string DataText => Convert.ToHexString(Data);
}
