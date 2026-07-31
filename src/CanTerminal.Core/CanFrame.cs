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

    /// <summary>
    /// Protocol interpretation, attached by <see cref="MessageHub"/> when the frame is published.
    /// Adapters never set this.
    /// </summary>
    public FrameAnnotation? Annotation { get; internal set; }

    public string IdText => IsExtended ? ArbId.ToString("X8") : ArbId.ToString("X3");
    public string DataText => Convert.ToHexString(Data);
}
