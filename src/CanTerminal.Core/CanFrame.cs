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

    /// <summary>Largest payload the bus itself allows: 8 bytes classic, 64 with CAN FD.</summary>
    public static int MaxPayload(bool fd) => fd ? 64 : 8;

    /// <summary>
    /// Rejects a payload the bus could not carry. Every adapter calls this, not just the ones
    /// talking to hardware: an oversized frame that reaches the display formats itself into a
    /// stack-allocated buffer sized from its own length, so an adapter that accepts one hands
    /// the UI thread a way to overflow its stack. The check belongs on the way in.
    /// </summary>
    public static void ValidatePayload(byte[] data, bool fd)
    {
        int max = MaxPayload(fd);
        if (data.Length > max)
            throw new ArgumentException(
                $"Data too long ({data.Length} bytes); {(fd ? "CAN FD" : "classic CAN")} carries at most {max}.");
    }
}
