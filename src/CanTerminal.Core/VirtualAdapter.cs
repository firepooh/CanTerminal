using System.Diagnostics;

namespace CanTerminal.Core;

/// <summary>
/// Software-only adapter for developing/testing without hardware.
/// - Sent frames are reported back as TX (like a real device's transmit report).
/// - A traffic generator emits a few periodic frames.
/// - A responder echoes every received TX frame back on the same channel with ArbId+0x100
///   after ~5 ms, so request/response test flows can be exercised end to end.
/// </summary>
public sealed class VirtualAdapter : ICanAdapter
{
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private Timer? _trafficTimer;
    private volatile bool _open;
    private bool _generateTraffic;
    private bool _echoResponder;
    private uint _counter;

    public VirtualAdapter(bool generateTraffic = true, bool echoResponder = true)
    {
        _generateTraffic = generateTraffic;
        _echoResponder = echoResponder;
    }

    public string Name => "Virtual bus (no hardware)";
    public bool IsOpen => _open;
    public IReadOnlyList<string> Channels { get; private set; } = [];

    public event Action<CanFrame>? FrameReceived;
#pragma warning disable CS0067 // virtual bus never raises errors
    public event Action<string>? ErrorOccurred;
#pragma warning restore CS0067

    public void Open(IReadOnlyList<CanChannelConfig> channels)
    {
        Channels = channels.Count > 0 ? channels.Select(c => c.Name.ToUpperInvariant()).ToList() : ["CAN1", "CAN2"];
        _open = true;
        if (_generateTraffic)
            _trafficTimer = new Timer(EmitTraffic, null, 100, 100);
    }

    public void Close()
    {
        _open = false;
        _trafficTimer?.Dispose();
        _trafficTimer = null;
        Channels = [];
    }

    public void Send(string channel, uint arbId, byte[] data, bool extended = false, bool fd = false, bool brs = false, string? source = null)
    {
        if (!_open) throw new InvalidOperationException("Virtual bus not open.");
        var ch = channel.ToUpperInvariant();
        if (!Channels.Contains(ch)) throw new ArgumentException($"Channel '{channel}' not configured.");
        // The virtual bus is still a bus. Without this an API client could publish a frame no
        // hardware could ever produce, and the display would try to format it.
        CanFrame.ValidatePayload(data, fd);

        Publish(new CanFrame
        {
            Timestamp = _clock.Elapsed.TotalSeconds,
            Channel = ch,
            ArbId = arbId,
            IsExtended = extended,
            IsFd = fd,
            IsBrs = brs,
            Direction = FrameDirection.Tx,
            Data = (byte[])data.Clone(),
            Source = source,
        });

        if (_echoResponder)
        {
            var echoData = (byte[])data.Clone();
            Task.Delay(5).ContinueWith(_ =>
            {
                if (!_open) return;
                Publish(new CanFrame
                {
                    Timestamp = _clock.Elapsed.TotalSeconds,
                    Channel = ch,
                    ArbId = arbId + 0x100,
                    IsExtended = extended,
                    IsFd = fd,
                    IsBrs = brs,
                    Direction = FrameDirection.Rx,
                    Data = echoData,
                });
            });
        }
    }

    private void EmitTraffic(object? _)
    {
        if (!_open) return;
        uint n = _counter++;
        // One read. Close() runs on the UI thread and replaces Channels with an empty list, so
        // testing Count and then indexing would be testing one list and indexing another —
        // Timer.Dispose() does not wait for a callback already in flight. The _open check above
        // narrows the window but does not close it.
        var open = Channels;
        var ch = open.Count > 0 ? open[0] : "CAN1";

        // fake "engine" data: rpm ramp + counter
        ushort rpm = (ushort)(800 + (n * 25) % 4200);
        Publish(new CanFrame
        {
            Timestamp = _clock.Elapsed.TotalSeconds,
            Channel = ch,
            ArbId = 0x0C0,
            Direction = FrameDirection.Rx,
            Data = [(byte)(rpm >> 8), (byte)rpm, (byte)(n & 0xFF), 0x00, 0x55, 0xAA, (byte)(n >> 8), (byte)n],
        });
        if (n % 5 == 0)
        {
            Publish(new CanFrame
            {
                Timestamp = _clock.Elapsed.TotalSeconds,
                Channel = ch,
                ArbId = 0x18FF50E5,
                IsExtended = true,
                Direction = FrameDirection.Rx,
                Data = [(byte)n, 0x01, 0x02, 0x03],
            });
        }
    }

    private void Publish(CanFrame f) => FrameReceived?.Invoke(f);

    public void Dispose() => Close();
}
