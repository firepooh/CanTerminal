using CanTerminal.Core;

// Headless harness: virtual bus + TCP API server, no GUI.
// Used by tests/smoke_test.py; also handy for developing python tests without hardware.
// Usage: CanTerminal.SmokeTest [port]

int port = args.Length > 0 ? int.Parse(args[0]) : 39999;

var hub = new MessageHub();
var dbc = new DbcDecoder();
using var adapter = new VirtualAdapter(generateTraffic: true, echoResponder: true);
adapter.FrameReceived += hub.Publish;
adapter.Open([new CanChannelConfig("CAN1"), new CanChannelConfig("CAN2")]);

using var server = new TcpApiServer(hub, dbc)
{
    OnSend = (channel, id, data, ext, fd, brs, source) => adapter.Send(channel, id, data, ext, fd, brs, source),
    StatusProvider = () => new ApiStatus(adapter.IsOpen, adapter.Name, adapter.Channels, dbc.FilePath),
};
server.Info += msg => Console.Error.WriteLine($"[server] {msg}");
server.Start(port);

Console.WriteLine($"READY {port}");
Console.Out.Flush();

// exit when stdin closes (parent process ended) or "quit" is received
string? line;
while ((line = Console.ReadLine()) != null)
{
    if (line.Trim().Equals("quit", StringComparison.OrdinalIgnoreCase)) break;
}
Console.Error.WriteLine($"[server] exiting, {hub.TotalFrames} frames total");
