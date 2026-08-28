using CanTerminal.Core;
using CanTerminal.Core.Xcp;

// Headless harness: virtual bus + TCP API server, no GUI.
// Used by tests/smoke_test.py; also handy for developing python tests without hardware.
// Usage: CanTerminal.SmokeTest [port]
//
// The XCP profile is wired to req 0x601 / rsp 0x701: the virtual bus echoes every transmitted
// frame back with ArbId+0x100, so 0x601 commands surface on 0x701 as if a slave had answered.
// Both stay under 0x7FF so neither is auto-promoted to a 29-bit ID.

int port = args.Length > 0 ? int.Parse(args[0]) : 39999;

var hub = new MessageHub();
var dbc = new DbcDecoder();
var annotator = new FrameAnnotator(dbc)
{
    XcpSessions = [new XcpDecoder(new XcpConfig(0x601, 0x701, Channel: "CAN1"))],
};
hub.Annotator = annotator.Annotate;

using var adapter = new VirtualAdapter(generateTraffic: true, echoResponder: true);
adapter.FrameReceived += hub.Publish;
adapter.Open([new CanChannelConfig("CAN1"), new CanChannelConfig("CAN2")]);

var api = new CanApi(hub)
{
    OnSend = (channel, id, data, ext, fd, brs, source) => adapter.Send(channel, id, data, ext, fd, brs, source),
    StatusProvider = () => new ApiStatus(adapter.IsOpen, adapter.Name, adapter.Channels, dbc.FilePath, annotator.ProfileName),
};
using var server = new TcpApiServer(hub, api);
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
