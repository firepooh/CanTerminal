namespace CanTerminal.Core;

public sealed record CanChannelConfig(string Name, int Bitrate = 500_000, bool Fd = false, int FdBitrate = 2_000_000);

/// <summary>Abstraction over a CAN interface (Intrepid ValueCAN, virtual bus, ...).</summary>
public interface ICanAdapter : IDisposable
{
    string Name { get; }
    bool IsOpen { get; }

    /// <summary>Channel names usable with <see cref="Send"/> once open.</summary>
    IReadOnlyList<string> Channels { get; }

    event Action<CanFrame>? FrameReceived;
    event Action<string>? ErrorOccurred;

    void Open(IReadOnlyList<CanChannelConfig> channels);
    void Close();

    /// <summary>Transmit a frame. Throws on failure. The TX frame is reported back via FrameReceived (Direction=Tx).</summary>
    void Send(string channel, uint arbId, byte[] data, bool extended = false, bool fd = false, bool brs = false, string? source = null);
}
