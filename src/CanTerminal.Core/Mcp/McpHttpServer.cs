using System.IO;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace CanTerminal.Core.Mcp;

/// <summary>Why the endpoint is not answering, for the status bar to say.</summary>
public enum McpHttpState
{
    Stopped,
    Running,
    /// <summary>Another process holds the port — usually a second copy of this program.</summary>
    PortInUse,
    Failed,
}

/// <summary>
/// The MCP endpoint, served by the monitor itself over loopback HTTP.
///
/// <para>This replaces a separate relay executable that existed only because Claude Code launches
/// MCP servers as a child process over stdin/stdout, and a window cannot be launched that way.
/// The device can only be opened by one process, so the server always had to live inside this one;
/// HTTP just removes the middleman. Registration becomes a URL, with no build path in it.</para>
///
/// <para>Modelled on the same transition in UartTerminal (docs/mcp-http-transport.md there); the
/// hazards it documents — the dispatcher deadlock, the swallowed bind failure, the localised
/// port-conflict message — are handled here for the same reasons.</para>
/// </summary>
public sealed class McpHttpServer
{
    /// <summary>Mapping at the root would 404 "/mcp" and vice versa, so pick one and keep it.</summary>
    private const string EndpointPath = "/mcp";

    /// <summary>
    /// How long to let a shutdown run. MCP's HTTP transport keeps streams open, and this is called
    /// on the UI thread, so an unbounded wait would freeze the window on the way out. Kestrel drops
    /// what is left after this.
    /// </summary>
    private static readonly TimeSpan StopGrace = TimeSpan.FromSeconds(1);

    private readonly CanApi _api;
    private readonly object _gate = new();
    private WebApplication? _app;

    public McpHttpServer(CanApi api, int port)
    {
        _api = api;
        Port = port;
    }

    public int Port { get; }

    public McpHttpState State { get; private set; } = McpHttpState.Stopped;

    /// <summary>The URL to register while running, e.g. http://127.0.0.1:5400/mcp.</summary>
    public string? Endpoint { get; private set; }

    /// <summary>Why it did not start, verbatim, for the status line.</summary>
    public string? FailureDetail { get; private set; }

    /// <summary>Registration command. No checkout path, no port number of the device — the same
    /// string on every machine, which is what makes it safe to commit.</summary>
    public string RegistrationCommand =>
        $"claude mcp add --transport http canterminal http://127.0.0.1:{Port}{EndpointPath}";

    /// <summary>
    /// Runs the host's async lifecycle on the thread pool, always.
    ///
    /// <para>Start and Stop are called from the UI thread. Awaiting them there with
    /// <c>.GetAwaiter().GetResult()</c> deadlocks: WPF's synchronisation context is installed, so
    /// the continuations inside try to resume on the UI thread — which is the thread that is
    /// blocked waiting for them. Inside <see cref="Task.Run(Func{Task})"/> there is no such
    /// context, continuations go to the pool, and the outer wait is just a block.</para>
    /// </summary>
    private static void OffUiThread(Func<Task> work) => Task.Run(work).GetAwaiter().GetResult();

    /// <summary>
    /// Starts the host and waits until it is really listening, so a port conflict is reported here
    /// rather than surfacing later as something unrelated. (Fire-and-forget <c>RunAsync</c> traps
    /// the bind failure inside the task, and the next touch of <c>app.Services</c> throws
    /// ObjectDisposedException instead.)
    /// </summary>
    public bool Start()
    {
        lock (_gate)
        {
            if (_app is not null) return true;

            WebApplication? app = null;
            try
            {
                app = Build();
                var starting = app;
                OffUiThread(() => starting.StartAsync());

                string? bound = app.Services.GetRequiredService<IServer>()
                    .Features.Get<IServerAddressesFeature>()?.Addresses.FirstOrDefault()
                    ?? throw new InvalidOperationException("Kestrel bound no address.");

                _app = app;
                Endpoint = bound + EndpointPath;
                FailureDetail = null;
                State = McpHttpState.Running;
                return true;
            }
            catch (Exception ex)
            {
                // A second instance losing the port is an ordinary outcome, not a fault: that copy
                // simply has no endpoint. Matched on the exception type because the inner message
                // is translated into the machine's language.
                bool inUse = ex is IOException { InnerException: AddressInUseException }
                             || ex.InnerException is AddressInUseException;
                State = inUse ? McpHttpState.PortInUse : McpHttpState.Failed;
                Endpoint = null;
                FailureDetail = ex.Message;
                SafeDispose(app);
                return false;
            }
        }
    }

    /// <summary>
    /// Stops the host, holding the lock until it is down — releasing early would let an immediately
    /// following <see cref="Start"/> race the old host for the port, which is exactly what turning
    /// the setting off and on again does.
    /// </summary>
    public void Stop()
    {
        lock (_gate)
        {
            var app = _app;
            _app = null;
            State = McpHttpState.Stopped;
            Endpoint = null;
            FailureDetail = null;
            if (app is null) return;

            try
            {
                using var cts = new CancellationTokenSource(StopGrace);
                OffUiThread(() => app.StopAsync(cts.Token));
            }
            catch { /* going down anyway; the dispose below is what matters */ }
            SafeDispose(app);
        }
    }

    private WebApplication Build()
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            // Pinned beside the executable. Otherwise the content root follows the working
            // directory, which is wherever the shortcut that launched us happened to point.
            ContentRootPath = AppContext.BaseDirectory,
        });

        // A window has no console, so host logging has nowhere to go.
        builder.Logging.ClearProviders();

        // Loopback only. On 0.0.0.0 anyone on the network could put frames on the user's bus.
        builder.WebHost.ConfigureKestrel(k => k.Listen(IPAddress.Loopback, Port));

        builder.Services.AddSingleton(_api);
        builder.Services.AddSingleton<CanMcpTools>();
        builder.Services.AddMcpServer(o =>
        {
            o.ServerInfo = new Implementation
            {
                Name = "canterminal",
                Title = AppInfo.Name,
                Version = AppInfo.Version,
            };
            o.ServerInstructions = CanMcpTools.Instructions;
        })
        .WithHttpTransport()
        .WithTools<CanMcpTools>();

        var app = builder.Build();
        app.Use(RejectForeignOrigin);
        app.MapMcp(EndpointPath);
        return app;
    }

    /// <summary>
    /// Loopback is not the same as private: a web page the user visits can still reach
    /// 127.0.0.1 with fetch(), and would then be able to transmit on their bus. MCP clients send
    /// no Origin header and browsers always do, so refusing a non-local Origin costs nothing and
    /// closes that door.
    /// </summary>
    private static async Task RejectForeignOrigin(HttpContext ctx, RequestDelegate next)
    {
        string origin = ctx.Request.Headers.Origin.ToString();
        if (origin.Length > 0 && !IsLoopbackOrigin(origin))
        {
            ctx.Response.StatusCode = StatusCodes.Status403Forbidden;
            return;
        }
        await next(ctx).ConfigureAwait(false);
    }

    private static bool IsLoopbackOrigin(string origin) =>
        Uri.TryCreate(origin, UriKind.Absolute, out var u) &&
        (u.IsLoopback || string.Equals(u.Host, "localhost", StringComparison.OrdinalIgnoreCase));

    private static void SafeDispose(WebApplication? app)
    {
        if (app is null) return;
        try { ((IDisposable)app).Dispose(); } catch { }
    }
}
