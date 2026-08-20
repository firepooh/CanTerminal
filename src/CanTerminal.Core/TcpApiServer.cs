using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;

namespace CanTerminal.Core;

/// <param name="Mode">"live" when a device is or could be attached, "log" while a file is open.</param>
/// <param name="LogPath">The open log file, if any.</param>
/// <param name="ChannelDbc">Per-channel database bindings as "CAN1=engine.dbc", if any.</param>
public sealed record ApiStatus(bool Connected, string? Adapter, IReadOnlyList<string> Channels, string? DbcPath,
                               string Profile = "none", string Mode = "live", string? LogPath = null,
                               IReadOnlyList<string>? ChannelDbc = null);

/// <summary>
/// Loopback TCP remote-control API: newline-delimited JSON, UTF-8.
///
/// Requests (optional "seq" is echoed back on the matching response):
///   {"op":"hello"} {"op":"status"} {"op":"ping"}
///   {"op":"send","channel":"CAN1","id":"123","data":"AABB","ext":false,"fd":false,"brs":false}
///   {"op":"subscribe","channels":["CAN1"],"ids":[291]}   (both filters optional)
///   {"op":"unsubscribe"}
///   {"op":"recent","count":100,"channel":"CAN1","id":291}
///   {"op":"waitfor","id":291,"channel":"CAN1","timeoutMs":1000}
/// Pushed while subscribed: {"op":"rx","frame":{...}}  (includes TX frames, see frame.dir)
/// </summary>
public sealed class TcpApiServer : IDisposable
{
    public delegate void SendHandler(string channel, uint arbId, byte[] data, bool ext, bool fd, bool brs, string source);

    private readonly MessageHub _hub;
    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly List<Client> _clients = [];
    private readonly object _clientsLock = new();

    private sealed class Client
    {
        public required TcpClient Tcp;
        public required Channel<string> Outbox;
        public volatile bool Subscribed;
        public HashSet<string>? ChannelFilter;
        public HashSet<uint>? IdFilter;
        public string Name = "?";
    }

    public TcpApiServer(MessageHub hub) => _hub = hub;

    public SendHandler? OnSend { get; set; }
    public Func<ApiStatus>? StatusProvider { get; set; }
    public event Action<string>? Info;

    public int Port { get; private set; }
    public bool IsRunning => _listener != null;

    public int ClientCount
    {
        get { lock (_clientsLock) return _clients.Count; }
    }

    public void Start(int port = 29536)
    {
        if (_listener != null) return;
        // Bind before publishing anything. Assigning _listener first would leave IsRunning
        // reporting a server that never bound, and since Start returns early on a non-null
        // listener, the port-in-use case could not even be retried.
        var listener = new TcpListener(IPAddress.Loopback, port);
        listener.Start();

        _cts = new CancellationTokenSource();
        _listener = listener;
        Port = port;
        _hub.FrameObserved += OnFrame;
        _ = AcceptLoop(_cts.Token);
        Info?.Invoke($"API server listening on 127.0.0.1:{port}");
    }

    public void Stop()
    {
        if (_listener == null) return;
        _hub.FrameObserved -= OnFrame;
        _cts?.Cancel();
        _listener.Stop();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        lock (_clientsLock)
        {
            foreach (var c in _clients) { try { c.Tcp.Close(); } catch { } }
            _clients.Clear();
        }
        Info?.Invoke("API server stopped");
    }

    private async Task AcceptLoop(CancellationToken ct)
    {
        var listener = _listener!;
        int consecutiveFailures = 0;
        while (!ct.IsCancellationRequested)
        {
            TcpClient tcp;
            try
            {
                tcp = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
                consecutiveFailures = 0;
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }   // Stop() closed the listener
            catch (Exception ex)
            {
                // One client aborting its connection mid-handshake fails the accept, not the
                // listener. Leaving the loop here used to retire the server for the rest of the
                // session while the socket stayed bound — so clients still connected, then
                // waited out their read timeout with no explanation anywhere.
                if (++consecutiveFailures >= 10)
                {
                    Info?.Invoke($"API server stopped accepting after {consecutiveFailures} consecutive errors: {ex.Message}");
                    break;
                }
                Info?.Invoke($"API server: accept failed ({ex.Message}) — still listening.");
                try { await Task.Delay(50, ct).ConfigureAwait(false); } catch { break; }
                continue;
            }

            tcp.NoDelay = true;
            var client = new Client
            {
                Tcp = tcp,
                Outbox = Channel.CreateBounded<string>(new BoundedChannelOptions(100_000)
                {
                    FullMode = BoundedChannelFullMode.DropOldest,
                    SingleReader = true,
                }),
                Name = tcp.Client.RemoteEndPoint?.ToString() ?? "?",
            };
            lock (_clientsLock) _clients.Add(client);
            Info?.Invoke($"Client connected: {client.Name}");
            _ = HandleClient(client, ct);
        }
    }

    private async Task HandleClient(Client client, CancellationToken ct)
    {
        var stream = client.Tcp.GetStream();
        var writerTask = Task.Run(async () =>
        {
            try
            {
                await foreach (var line in client.Outbox.Reader.ReadAllAsync(ct).ConfigureAwait(false))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    await stream.WriteAsync(bytes, ct).ConfigureAwait(false);
                }
            }
            catch { }
        }, ct);

        try
        {
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            while (!ct.IsCancellationRequested)
            {
                var line = await reader.ReadLineAsync(ct).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;

                string response;
                try { response = await HandleRequest(client, line).ConfigureAwait(false); }
                catch (Exception ex) { response = Err(null, ex.Message); }
                client.Outbox.Writer.TryWrite(response);
            }
        }
        catch { }
        finally
        {
            lock (_clientsLock) _clients.Remove(client);
            client.Outbox.Writer.TryComplete();
            try { client.Tcp.Close(); } catch { }
            Info?.Invoke($"Client disconnected: {client.Name}");
            try { await writerTask.ConfigureAwait(false); } catch { }
        }
    }

    private async Task<string> HandleRequest(Client client, string line)
    {
        var req = JsonNode.Parse(line)?.AsObject() ?? throw new InvalidOperationException("Invalid JSON.");
        var seq = req["seq"];
        string op = req["op"]?.GetValue<string>() ?? throw new InvalidOperationException("Missing 'op'.");

        switch (op)
        {
            case "ping":
                return Reply(seq, new JsonObject { ["op"] = "pong" });

            case "hello":
            case "status":
            {
                var st = StatusProvider?.Invoke() ?? new ApiStatus(false, null, [], null, "none");
                return Reply(seq, new JsonObject
                {
                    ["op"] = op == "hello" ? "hello" : "status",
                    ["app"] = "CanTerminal",
                    ["version"] = "1.0",
                    ["connected"] = st.Connected,
                    ["adapter"] = st.Adapter,
                    ["channels"] = new JsonArray(st.Channels.Select(c => (JsonNode)c).ToArray()),
                    ["dbc"] = st.DbcPath,
                    ["profile"] = st.Profile,
                    ["mode"] = st.Mode,
                    ["log"] = st.LogPath,
                    ["channelDbc"] = new JsonArray((st.ChannelDbc ?? []).Select(c => (JsonNode)c).ToArray()),
                    ["totalFrames"] = _hub.TotalFrames,
                    ["clients"] = ClientCount,
                });
            }

            case "send":
            {
                var sender = OnSend ?? throw new InvalidOperationException("No device connected.");
                string channel = req["channel"]?.GetValue<string>() ?? "CAN1";
                uint id = ParseId(req["id"] ?? throw new InvalidOperationException("Missing 'id'."));
                byte[] data = Convert.FromHexString(req["data"]?.GetValue<string>() ?? "");
                bool ext = req["ext"]?.GetValue<bool>() ?? id > 0x7FF;
                bool fd = req["fd"]?.GetValue<bool>() ?? false;
                bool brs = req["brs"]?.GetValue<bool>() ?? false;
                sender(channel, id, data, ext, fd, brs, $"tcp:{client.Name}");
                return Reply(seq, new JsonObject { ["op"] = "ok" });
            }

            case "subscribe":
                client.ChannelFilter = req["channels"] is JsonArray chs && chs.Count > 0
                    ? chs.Select(c => c!.GetValue<string>().ToUpperInvariant()).ToHashSet()
                    : null;
                client.IdFilter = req["ids"] is JsonArray ids && ids.Count > 0
                    ? ids.Select(ParseIdNode).ToHashSet()
                    : null;
                client.Subscribed = true;
                return Reply(seq, new JsonObject { ["op"] = "ok" });

            case "unsubscribe":
                client.Subscribed = false;
                return Reply(seq, new JsonObject { ["op"] = "ok" });

            case "recent":
            {
                int count = Math.Clamp(req["count"]?.GetValue<int>() ?? 100, 1, 10_000);
                string? channel = req["channel"]?.GetValue<string>()?.ToUpperInvariant();
                uint? id = req["id"] is JsonNode idNode ? ParseId(idNode) : null;
                var frames = _hub.GetRecent(count, f =>
                    (channel is null || f.Channel == channel) &&
                    (id is null || f.ArbId == id.Value));
                var arr = new JsonArray();
                foreach (var f in frames) arr.Add(FrameJson(f));
                return Reply(seq, new JsonObject { ["op"] = "recent", ["frames"] = arr });
            }

            case "waitfor":
            {
                // Nothing is ever published while a file is open, so this would block for the
                // whole timeout and then report a timeout that means nothing.
                if (StatusProvider?.Invoke().Mode == "log")
                    return Err(seq, "A log file is open, so no frames will arrive. Use 'recent' to read it.");
                uint id = ParseId(req["id"] ?? throw new InvalidOperationException("Missing 'id'."));
                string? channel = req["channel"]?.GetValue<string>()?.ToUpperInvariant();
                int timeoutMs = Math.Clamp(req["timeoutMs"]?.GetValue<int>() ?? 5000, 1, 300_000);
                var frame = await _hub.WaitForAsync(
                    f => f.ArbId == id && (channel is null || f.Channel == channel) && f.Direction == FrameDirection.Rx,
                    TimeSpan.FromMilliseconds(timeoutMs)).ConfigureAwait(false);
                return frame is null
                    ? Reply(seq, new JsonObject { ["op"] = "timeout" })
                    : Reply(seq, new JsonObject { ["op"] = "frame", ["frame"] = FrameJson(frame) });
            }

            default:
                return Err(seq, $"Unknown op '{op}'.");
        }
    }

    private void OnFrame(CanFrame f)
    {
        List<Client> targets;
        lock (_clientsLock)
        {
            if (_clients.Count == 0) return;
            targets = _clients.Where(c => c.Subscribed
                && (c.ChannelFilter is null || c.ChannelFilter.Contains(f.Channel))
                && (c.IdFilter is null || c.IdFilter.Contains(f.ArbId))).ToList();
        }
        if (targets.Count == 0) return;

        var msg = new JsonObject { ["op"] = "rx", ["frame"] = FrameJson(f) }
            .ToJsonString(JsonSerializerOptions.Default);
        foreach (var c in targets) c.Outbox.Writer.TryWrite(msg);
    }

    private JsonObject FrameJson(CanFrame f) => new()
    {
        ["ts"] = Math.Round(f.Timestamp, 6),
        ["channel"] = f.Channel,
        ["id"] = f.ArbId,
        ["idHex"] = f.IdText,
        ["ext"] = f.IsExtended,
        ["fd"] = f.IsFd,
        ["brs"] = f.IsBrs,
        ["rtr"] = f.IsRemote,
        ["err"] = f.IsError,
        ["dir"] = f.Direction == FrameDirection.Tx ? "tx" : "rx",
        ["data"] = f.DataText,
        ["type"] = f.Annotation?.Type,
        ["decoded"] = f.Annotation?.Comment,
        ["sender"] = f.Annotation?.Sender,
    };

    private static uint ParseId(JsonNode node) => ParseIdNode(node);

    private static uint ParseIdNode(JsonNode? node) => node switch
    {
        null => throw new InvalidOperationException("Missing id."),
        JsonValue v when v.TryGetValue<uint>(out var n) => n,
        JsonValue v when v.TryGetValue<string>(out var s) =>
            uint.Parse(s.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? s[2..] : s,
                System.Globalization.NumberStyles.HexNumber),
        _ => throw new InvalidOperationException("Invalid id."),
    };

    private static string Reply(JsonNode? seq, JsonObject obj)
    {
        if (seq != null) obj["seq"] = seq.DeepClone();
        return obj.ToJsonString(JsonSerializerOptions.Default);
    }

    private static string Err(JsonNode? seq, string message)
    {
        var obj = new JsonObject { ["op"] = "error", ["message"] = message };
        if (seq != null) obj["seq"] = seq.DeepClone();
        return obj.ToJsonString(JsonSerializerOptions.Default);
    }

    public void Dispose() => Stop();
}
