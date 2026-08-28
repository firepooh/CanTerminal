using System.ComponentModel;
using System.Text;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace CanTerminal.Core.Mcp;

/// <summary>
/// The four things Claude can do with the bus the user has open, served straight out of the
/// monitor rather than through a relay process.
///
/// <para>Every method here runs on a thread-pool thread, not the UI thread. That is safe because
/// each one goes through <see cref="CanApi"/>, which reaches the hub and the adapter — both of
/// which are already called from the receive thread and the TCP clients. Nothing here touches a
/// WPF control.</para>
///
/// <para>The wording of the descriptions is the documentation Claude actually reads, so it says
/// what the tool does to the bus, not how it is implemented.</para>
/// </summary>
[McpServerToolType]
public sealed class CanMcpTools
{
    private readonly CanApi _api;

    public CanMcpTools(CanApi api) => _api = api;

    /// <summary>Told to the model once, at connect. Explains what it is attached to.</summary>
    public static string Instructions =>
        "This server is the CAN monitor the user has open on their screen. It owns the device, and " +
        "these tools share it: a frame sent here goes out on the same bus the user is watching and " +
        "appears in their trace. " +
        "Call can_status first — it names the channels that exist (CAN1, CAN2, ...), whether a device " +
        "is connected at all, and whether a log file is loaded instead of a live bus. " +
        "Frames are read from a ring buffer with can_recent, which returns what has already been " +
        "captured; can_wait_for blocks until a frame arrives, so it is the one to use for a reply to " +
        "something you just sent. " +
        "While a log file is open the bus is not live: can_recent reads the file, and can_wait_for is " +
        "refused rather than left to time out for nothing.";

    private const string ChannelArg =
        "Channel name as reported by can_status, e.g. \"CAN1\". A device may carry more than one bus.";

    [McpServerTool(Name = "can_status", ReadOnly = true, OpenWorld = false)]
    [Description("Report what the monitor is attached to: the connected device and its channels, any " +
                 "loaded DBC database, the active protocol profile, whether this is a live bus or an " +
                 "opened log file, and how many frames have been captured. Call this before the others " +
                 "— the channel names the other tools need come from here.")]
    public StatusResult Status()
    {
        var s = _api.Status();
        return new StatusResult(
            s.Connected, s.Adapter, [.. s.Channels], s.DbcPath, s.Profile, s.Mode, s.LogPath,
            [.. s.ChannelDbc ?? []], _api.TotalFrames, AppInfo.Version);
    }

    [McpServerTool(Name = "can_send", Destructive = true, OpenWorld = false)]
    [Description("Transmit one CAN frame on the bus the monitor has open. The frame is real traffic: " +
                 "other nodes see it, and it appears in the user's trace marked as coming from MCP. " +
                 "Returns an error rather than a false success if no device is connected, or if the " +
                 "device could not get the frame onto the bus (nothing acknowledged it, wrong bitrate).")]
    public string Send(
        [Description(ChannelArg)] string channel,
        [Description("Arbitration ID in hex, e.g. \"123\" or \"0x18FF50E5\".")] string id,
        [Description("Payload as a hex string, e.g. \"0011AABB\". May be empty for a zero-length frame.")] string? data = null,
        [Description("Extended 29-bit identifier. Defaults to whatever the ID value requires.")] bool? ext = null,
        [Description("Send as a CAN FD frame (payload may exceed 8 bytes).")] bool fd = false,
        [Description("CAN FD bit-rate switch: run the data phase at the faster data bitrate.")] bool brs = false)
    {
        uint arbId = ParseId(id);
        byte[] payload;
        try { payload = Convert.FromHexString((data ?? "").Replace(" ", "")); }
        catch (FormatException) { throw new McpException($"'{data}' is not a hex payload."); }

        try
        {
            _api.Send(channel, arbId, payload, ext ?? arbId > 0x7FF, fd, brs, "mcp");
        }
        catch (Exception ex)
        {
            // Surfaced as an error result rather than a thrown host fault, so the model is told
            // what went wrong on the bus and can decide what to do about it.
            throw new McpException(ex.Message);
        }
        return $"Sent 0x{arbId:X} on {channel} ({payload.Length} byte(s)).";
    }

    [McpServerTool(Name = "can_recent", ReadOnly = true, OpenWorld = false)]
    [Description("Read frames the monitor has already captured, newest last. Includes DBC-decoded " +
                 "signals when a database is loaded and the decoded command when a protocol profile " +
                 "such as XCP is active. This does not wait for anything — it returns what is in the " +
                 "buffer now, which is also how an opened log file is read.")]
    public string Recent(
        [Description("Maximum frames to return (default 50, capped at 1000).")] int count = 50,
        [Description(ChannelArg + " Omit to read every channel.")] string? channel = null,
        [Description("Only frames with this arbitration ID, in hex. Omit for all IDs.")] string? id = null)
    {
        var frames = _api.Recent(Math.Clamp(count, 1, 1000), channel, id is null ? null : ParseId(id));
        if (frames.Count == 0) return "No frames in the buffer matching that filter.";

        var sb = new StringBuilder($"{frames.Count} frame(s), newest last:\n");
        foreach (var f in frames) sb.AppendLine(Format(f));
        return sb.ToString();
    }

    [McpServerTool(Name = "can_wait_for", ReadOnly = true, OpenWorld = false)]
    [Description("Block until a frame with this arbitration ID is received, then return it. Use this " +
                 "for the answer to something just sent, rather than polling can_recent. Only received " +
                 "frames match — a frame this tool sent does not satisfy its own wait. Refused while a " +
                 "log file is open, because nothing new arrives in that mode.")]
    public string WaitFor(
        [Description("Arbitration ID to wait for, in hex.")] string id,
        [Description(ChannelArg + " Omit to accept the frame on any channel.")] string? channel = null,
        [Description("How long to wait, in milliseconds (default 5000, capped at 300000).")] int timeout_ms = 5000)
    {
        uint arbId = ParseId(id);
        CanFrame? frame;
        try { frame = _api.WaitForAsync(arbId, channel, timeout_ms).GetAwaiter().GetResult(); }
        catch (InvalidOperationException ex) { throw new McpException(ex.Message); }

        return frame is null
            ? $"Timed out: no frame with ID 0x{arbId:X} within {timeout_ms} ms."
            : "Received:\n" + Format(frame);
    }

    private static uint ParseId(string id)
    {
        try { return CanApi.ParseId(id); }
        catch (ArgumentException ex) { throw new McpException(ex.Message); }
    }

    /// <summary>One frame as a line the model reads, matching what the trace shows.</summary>
    private static string Format(CanFrame f)
    {
        var flags = new List<string>(4);
        if (f.IsExtended) flags.Add("EXT");
        if (f.IsFd) flags.Add(f.IsBrs ? "FD+BRS" : "FD");
        if (f.IsRemote) flags.Add("RTR");
        if (f.IsError) flags.Add("ERR");

        return $"  t={f.Timestamp:0.000000} {f.Channel} {(f.Direction == FrameDirection.Tx ? "TX" : "RX")} " +
               $"0x{f.IdText} [{f.Data.Length}] {f.DataText}" +
               (flags.Count > 0 ? $" ({string.Join(",", flags)})" : "") +
               (f.Annotation?.Type is { } type ? $"  {type}" : "") +
               (f.Annotation?.Comment is { } comment ? $"  |  {comment}" : "");
    }

    /// <param name="Mode">"live" when a bus is or could be attached, "log" while a file is open.</param>
    /// <param name="ChannelDbc">Per-channel database bindings, as "CAN1=engine.dbc".</param>
    public sealed record StatusResult(
        bool Connected, string? Adapter, string[] Channels, string? Dbc, string Profile,
        string Mode, string? Log, string[] ChannelDbc, long TotalFrames, string Version);
}
