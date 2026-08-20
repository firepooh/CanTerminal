using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// CanTerminal MCP server (stdio transport).
// Bridges MCP tool calls to a running CanTerminal monitor via its TCP JSON API.
// Usage in .mcp.json:  "command": ".../CanTerminal.Mcp.exe", "args": ["--port", "29536"]

string host = "127.0.0.1";
int port = 29536;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--host") host = args[i + 1];
    if (args[i] == "--port") port = int.Parse(args[i + 1]);
}

var stdout = Console.Out;
var writeLock = new object();

void WriteMessage(JsonObject msg)
{
    var line = msg.ToJsonString(JsonSerializerOptions.Default);
    lock (writeLock)
    {
        stdout.WriteLine(line);
        stdout.Flush();
    }
}

void WriteResult(JsonNode id, JsonNode result) =>
    WriteMessage(new JsonObject { ["jsonrpc"] = "2.0", ["id"] = id.DeepClone(), ["result"] = result });

void WriteError(JsonNode? id, int code, string message) =>
    WriteMessage(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id?.DeepClone(),
        ["error"] = new JsonObject { ["code"] = code, ["message"] = message },
    });

// This build's version. Read from this assembly rather than taken from Core — the relay
// deliberately has no reference to it, and the repository stamps every assembly with one version.
string serverVersion = ReadVersion();

static string ReadVersion()
{
    string informational = Assembly.GetExecutingAssembly()
        .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "";
    if (informational.Length == 0) return "dev";
    int plus = informational.IndexOf('+');          // the SDK appends "+<commit sha>"
    return plus > 0 ? informational[..plus] : informational;
}

string? line;
while ((line = Console.ReadLine()) != null)
{
    if (string.IsNullOrWhiteSpace(line)) continue;
    JsonObject req;
    try { req = JsonNode.Parse(line)!.AsObject(); }
    catch { continue; }

    var id = req["id"];
    string method = req["method"]?.GetValue<string>() ?? "";
    var p = req["params"]?.AsObject();

    try
    {
        switch (method)
        {
            case "initialize":
                WriteResult(id!, new JsonObject
                {
                    ["protocolVersion"] = p?["protocolVersion"]?.DeepClone() ?? "2025-03-26",
                    ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                    ["serverInfo"] = new JsonObject { ["name"] = "canterminal", ["version"] = serverVersion },
                });
                break;

            case "notifications/initialized":
            case "notifications/cancelled":
                break;

            case "ping":
                WriteResult(id!, new JsonObject());
                break;

            case "tools/list":
                WriteResult(id!, new JsonObject { ["tools"] = ToolDefinitions() });
                break;

            case "tools/call":
            {
                string name = p?["name"]?.GetValue<string>() ?? "";
                var toolArgs = p?["arguments"]?.AsObject() ?? [];
                string text;
                bool isError = false;
                try { text = CallTool(name, toolArgs); }
                catch (Exception ex) { text = ex.Message; isError = true; }
                WriteResult(id!, new JsonObject
                {
                    ["content"] = new JsonArray(new JsonObject { ["type"] = "text", ["text"] = text }),
                    ["isError"] = isError,
                });
                break;
            }

            default:
                if (id != null) WriteError(id, -32601, $"Method not found: {method}");
                break;
        }
    }
    catch (Exception ex)
    {
        if (id != null) WriteError(id, -32603, ex.Message);
        Console.Error.WriteLine($"[canterminal-mcp] {ex}");
    }
}

return;

// ---------------- tools ----------------

JsonArray ToolDefinitions()
{
    static JsonObject Tool(string name, string description, JsonObject properties, params string[] required) => new()
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = new JsonObject
        {
            ["type"] = "object",
            ["properties"] = properties,
            ["required"] = new JsonArray(required.Select(r => (JsonNode)r).ToArray()),
        },
    };

    static JsonObject Prop(string type, string desc) => new() { ["type"] = type, ["description"] = desc };

    return
    [
        Tool("can_status",
            "Get CanTerminal monitor status: connected device, open channels, loaded DBC, frame counts.",
            []),
        Tool("can_send",
            "Transmit a CAN frame through the connected device. The frame also appears in the monitor trace.",
            new JsonObject
            {
                ["channel"] = Prop("string", "Channel name, e.g. CAN1"),
                ["id"] = Prop("string", "Arbitration ID in hex, e.g. '123' or '0x18FF50E5'"),
                ["data"] = Prop("string", "Payload as hex string, e.g. '0011AABB' (may be empty)"),
                ["ext"] = Prop("boolean", "Extended 29-bit ID (default: auto from ID value)"),
                ["fd"] = Prop("boolean", "CAN FD frame (default false)"),
                ["brs"] = Prop("boolean", "FD bit-rate switch (default false)"),
            },
            "channel", "id"),
        Tool("can_recent",
            "Get recent frames seen on the bus (from the monitor's ring buffer), newest last. Includes DBC-decoded signals when a DBC is loaded, and the decoded frame type/parameters when a protocol profile (e.g. XCP) is active.",
            new JsonObject
            {
                ["count"] = Prop("integer", "Max frames to return (default 50, max 1000)"),
                ["channel"] = Prop("string", "Filter by channel, e.g. CAN1"),
                ["id"] = Prop("string", "Filter by arbitration ID in hex"),
            }),
        Tool("can_wait_for",
            "Wait until a frame with the given arbitration ID is received on the bus (or time out).",
            new JsonObject
            {
                ["id"] = Prop("string", "Arbitration ID in hex to wait for"),
                ["channel"] = Prop("string", "Channel name, e.g. CAN1"),
                ["timeout_ms"] = Prop("integer", "Timeout in milliseconds (default 5000)"),
            },
            "id"),
    ];
}

string CallTool(string name, JsonObject a)
{
    switch (name)
    {
        case "can_status":
        {
            var st = Api(new JsonObject { ["op"] = "status" });
            return st.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        }
        case "can_send":
        {
            var req = new JsonObject
            {
                ["op"] = "send",
                ["channel"] = a["channel"]?.GetValue<string>() ?? "CAN1",
                ["id"] = NormalizeHexId(a["id"]),
                ["data"] = (a["data"]?.GetValue<string>() ?? "").Replace(" ", ""),
            };
            if (a["ext"] != null) req["ext"] = a["ext"]!.GetValue<bool>();
            if (a["fd"] != null) req["fd"] = a["fd"]!.GetValue<bool>();
            if (a["brs"] != null) req["brs"] = a["brs"]!.GetValue<bool>();
            Api(req);
            return $"Sent frame id=0x{NormalizeHexId(a["id"])} on {req["channel"]}.";
        }
        case "can_recent":
        {
            var req = new JsonObject
            {
                ["op"] = "recent",
                ["count"] = Math.Clamp(a["count"]?.GetValue<int>() ?? 50, 1, 1000),
            };
            if (a["channel"] != null) req["channel"] = a["channel"]!.GetValue<string>();
            if (a["id"] != null) req["id"] = NormalizeHexId(a["id"]);
            var frames = Api(req)["frames"]!.AsArray();
            if (frames.Count == 0) return "No frames in buffer (matching the filter).";
            var sb = new StringBuilder($"{frames.Count} frame(s), newest last:\n");
            foreach (var f in frames) sb.AppendLine(FormatFrame(f!.AsObject()));
            return sb.ToString();
        }
        case "can_wait_for":
        {
            int timeoutMs = a["timeout_ms"]?.GetValue<int>() ?? 5000;
            var req = new JsonObject
            {
                ["op"] = "waitfor",
                ["id"] = NormalizeHexId(a["id"]),
                ["timeoutMs"] = timeoutMs,
            };
            if (a["channel"] != null) req["channel"] = a["channel"]!.GetValue<string>();
            var reply = Api(req, timeoutMs + 5000);
            return reply["op"]?.GetValue<string>() == "frame"
                ? "Received:\n" + FormatFrame(reply["frame"]!.AsObject())
                : $"Timeout: no frame with id 0x{NormalizeHexId(a["id"])} within {timeoutMs} ms.";
        }
        default:
            throw new InvalidOperationException($"Unknown tool: {name}");
    }
}

static string NormalizeHexId(JsonNode? node)
{
    if (node is null) throw new InvalidOperationException("Missing 'id'.");
    // The schema says hex string, but clients sometimes send a JSON number anyway;
    // treat that as a decimal arbitration ID (matching the TCP API's number semantics).
    if (node is JsonValue v && v.TryGetValue<long>(out var num))
    {
        if (num is < 0 or > uint.MaxValue) throw new InvalidOperationException($"Id out of range: {num}.");
        return ((uint)num).ToString("X");
    }
    var s = node.GetValue<string>().Trim();
    if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
    _ = uint.Parse(s, System.Globalization.NumberStyles.HexNumber); // validate
    return s;
}

static string FormatFrame(JsonObject f)
{
    var flags = new List<string>();
    if (f["ext"]?.GetValue<bool>() == true) flags.Add("EXT");
    if (f["fd"]?.GetValue<bool>() == true) flags.Add("FD");
    if (f["brs"]?.GetValue<bool>() == true) flags.Add("BRS");
    if (f["rtr"]?.GetValue<bool>() == true) flags.Add("RTR");
    if (f["err"]?.GetValue<bool>() == true) flags.Add("ERR");
    var type = f["type"]?.GetValue<string>();
    var decoded = f["decoded"]?.GetValue<string>();
    return $"  t={f["ts"]} {f["channel"]} {f["dir"]?.GetValue<string>()?.ToUpperInvariant()} " +
           $"0x{f["idHex"]} [{(f["data"]?.GetValue<string>()?.Length ?? 0) / 2}] {f["data"]}" +
           (flags.Count > 0 ? $" ({string.Join(",", flags)})" : "") +
           (type != null ? $"  {type}" : "") +
           (decoded != null ? $"  |  {decoded}" : "");
}

JsonObject Api(JsonObject request, int timeoutMs = 10_000)
{
    using var tcp = new TcpClient();
    try { tcp.Connect(host, port); }
    catch (SocketException)
    {
        throw new InvalidOperationException(
            $"Cannot reach CanTerminal on {host}:{port}. Start CanTerminal.exe and enable its API server.");
    }
    tcp.NoDelay = true;
    tcp.ReceiveTimeout = timeoutMs;
    using var stream = tcp.GetStream();
    var bytes = Encoding.UTF8.GetBytes(request.ToJsonString(JsonSerializerOptions.Default) + "\n");
    stream.Write(bytes);
    using var reader = new StreamReader(stream, Encoding.UTF8);
    var replyLine = reader.ReadLine() ?? throw new InvalidOperationException("CanTerminal closed the connection.");
    var reply = JsonNode.Parse(replyLine)!.AsObject();
    if (reply["op"]?.GetValue<string>() == "error")
        throw new InvalidOperationException(reply["message"]?.GetValue<string>() ?? "unknown error");
    return reply;
}
