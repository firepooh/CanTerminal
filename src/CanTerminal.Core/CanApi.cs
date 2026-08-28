namespace CanTerminal.Core;

/// <summary>
/// What the monitor lets an outside program do: read its status, send a frame, look back through
/// what it has captured, and wait for something to arrive.
///
/// This is the operations themselves, with no transport attached. Two front ends sit on top —
/// the newline-delimited TCP protocol the python client speaks (<see cref="TcpApiServer"/>) and
/// the MCP endpoint Claude connects to — and it matters that they are the same operations rather
/// than two implementations that drift apart: a rule enforced in one and forgotten in the other
/// (the log-mode guard on <see cref="WaitForAsync"/>, say) is a difference nobody would notice
/// until it mattered.
/// </summary>
public sealed class CanApi
{
    /// <param name="source">Who asked, e.g. "ui", "tcp:127.0.0.1:1234", "mcp". Rides along on the
    /// frame so the trace can say where a transmission came from.</param>
    public delegate void SendHandler(string channel, uint arbId, byte[] data, bool ext, bool fd, bool brs, string source);

    private readonly MessageHub _hub;

    public CanApi(MessageHub hub) => _hub = hub;

    /// <summary>Set by the window: performs a transmission on the open device.</summary>
    public SendHandler? OnSend { get; set; }

    /// <summary>Set by the window: reports what is connected right now.</summary>
    public Func<ApiStatus>? StatusProvider { get; set; }

    public long TotalFrames => _hub.TotalFrames;

    public ApiStatus Status() => StatusProvider?.Invoke() ?? new ApiStatus(false, null, [], null, "none");

    public void Send(string channel, uint arbId, byte[] data, bool ext, bool fd, bool brs, string source)
    {
        var sender = OnSend ?? throw new InvalidOperationException("No device connected.");
        sender(channel, arbId, data, ext, fd, brs, source);
    }

    /// <summary>Up to <paramref name="count"/> captured frames matching the filters, newest last.</summary>
    public List<CanFrame> Recent(int count, string? channel, uint? id)
    {
        count = Math.Clamp(count, 1, 10_000);
        string? wanted = channel?.ToUpperInvariant();
        return _hub.GetRecent(count, f =>
            (wanted is null || f.Channel == wanted) &&
            (id is null || f.ArbId == id.Value));
    }

    /// <summary>
    /// Waits for a received frame with this identifier, or null on timeout.
    ///
    /// Refused outright while a log file is open: nothing is ever published in that mode, so the
    /// call would block for the whole timeout and then report a timeout that means nothing.
    /// </summary>
    public async Task<CanFrame?> WaitForAsync(uint id, string? channel, int timeoutMs)
    {
        if (Status().Mode == "log")
            throw new InvalidOperationException(
                "A log file is open, so no frames will arrive. Use 'recent' to read it.");

        string? wanted = channel?.ToUpperInvariant();
        timeoutMs = Math.Clamp(timeoutMs, 1, 300_000);
        return await _hub.WaitForAsync(
            f => f.ArbId == id && (wanted is null || f.Channel == wanted) && f.Direction == FrameDirection.Rx,
            TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads an arbitration identifier written as hex ("123", "0x18FF50E5"). Clients that send a
    /// JSON number instead get decimal semantics, which is what the TCP protocol has always done.
    /// </summary>
    public static uint ParseId(string text)
    {
        string s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint id))
            throw new ArgumentException($"'{text}' is not a hex arbitration id.");
        return id;
    }
}
