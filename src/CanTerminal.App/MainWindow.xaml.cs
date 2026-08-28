using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CanTerminal.Core;
using CanTerminal.Core.Logs;
using CanTerminal.Core.Xcp;

namespace CanTerminal.App;

public partial class MainWindow : Window
{
    private static string RepoUrl => AppInfo.RepositoryUrl;

    /// <summary>
    /// How long one flush may hold the UI thread. A count-based budget cannot bound this: the
    /// cost of a frame depends on how many panes take it and how many rows already exist, so a
    /// busy bus turns a "30,000 frames" budget into a multi-second freeze.
    /// </summary>
    private const double FlushBudgetMs = 12;

    /// <summary>
    /// Display backlog allowed before frames are skipped on screen. Capture is never affected —
    /// the ring buffer and the TCP API still see everything — but the UI must not be dragged
    /// under by a burst it can never catch up with.
    /// </summary>
    private const int DisplayBacklogCap = 150_000;

    private static readonly int[] Bitrates = [125_000, 250_000, 500_000, 1_000_000];

    // 500k and 1M are here because a CAN FD bus does not have to speed up for its data phase:
    // plenty run the data rate at the arbitration rate and take only the 64-byte payload.
    // 8M is a ValueCAN figure — the SLCAN board's transceiver stops at 5M and says so.
    private static readonly int[] FdBitrates =
        [500_000, 1_000_000, 2_000_000, 5_000_000, 8_000_000];

    private readonly MessageHub _hub = new();
    private readonly DbcDecoder _dbc = new();
    private readonly FrameAnnotator _annotator;

    /// <summary>The operations both remote front ends offer; the servers below are transports.</summary>
    private readonly CanApi _api;
    private readonly TcpApiServer _server;
    private Core.Mcp.McpHttpServer? _mcp;
    private readonly AppSettings _settings = AppSettings.Load();
    private ICanAdapter? _adapter;

    private readonly ConcurrentQueue<CanFrame> _pending = new();

    /// <summary>One XCP session per channel; a 2-port master uses a different ID pair on each.</summary>
    private readonly Dictionary<string, XcpConfig> _xcpSessions = new(StringComparer.OrdinalIgnoreCase);

    // Settings that used to live in a toolbar control and now live in the menu. The menu items
    // themselves hold every boolean (checkable items are the state); only the values that need a
    // dialog are kept here.
    private List<DeviceItem> _devices = [];
    private DeviceItem? _device;
    private string _channelsText = "CAN1,CAN2";
    private int _bitrate = 500_000;
    private int _fdBitrate = 2_000_000;
    private int _historyCapacity = TraceBuffer.DefaultCapacity;
    private int _cycleMs = 100;
    private int _apiPort = 29536;
    private int _mcpPort = 29537;
    private int _layout;                // 0 single, 1 split ↔, 2 split ↕
    private string? _txChannel;
    private string? _xcpChannel;        // channel the XCP dialog opens on

    // The device's hardware timestamp is the only clock on a frame, so absolute and relative
    // readings hang off the first frame of the capture: its timestamp, and the wall clock at
    // the moment it was taken off the queue.
    private TimestampMode _timestampMode = TimestampMode.Relative;
    private double _zeroTs;
    private DateTime _zeroWall = DateTime.Now;
    private bool _haveZero;

    /// <summary>
    /// The log file on screen, or null while this is a live monitor. Every offline decision hangs
    /// off this one field rather than a mode flag sprinkled about, so the live path stays exactly
    /// the code it was.
    /// </summary>
    private LogFile? _log;

    /// <summary>Databases bound to one channel each; see <see cref="FrameAnnotator.ChannelDbc"/>.</summary>
    private readonly Dictionary<string, DbcDecoder> _channelDbc = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Frame count past which opening a log is offered rather than done. Frame, annotation and
    /// row together run to roughly half a kilobyte each, so a million is already most of a
    /// gigabyte and the layout passes stop being instant.
    /// </summary>
    private const int LargeLogFrames = 1_000_000;

    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _periodicTimer;
    private long _lastTotal;
    private long _displaySkipped;
    private Dictionary<string, long> _lastChannelTotals = [];
    private double _lastRateAt;         // when the fps readings were last recomputed

    /// <summary>
    /// Drives the change-highlight decay. Deliberately independent of frame timestamps: the
    /// highlight has to keep fading in wall time even when the bus goes quiet.
    /// </summary>
    private readonly Stopwatch _uiClock = Stopwatch.StartNew();

    public MainWindow()
    {
        InitializeComponent();

        _annotator = new FrameAnnotator(_dbc);
        _hub.Annotator = _annotator.Annotate;

        _api = new CanApi(_hub)
        {
            OnSend = (channel, id, data, ext, fd, brs, source) =>
            {
                var a = _adapter;
                if (a?.IsOpen != true) throw new InvalidOperationException("No device connected in CanTerminal.");
                a.Send(channel, id, data, ext, fd, brs, source);
            },
            StatusProvider = () => new ApiStatus(
                _adapter?.IsOpen == true, _adapter?.Name,
                _adapter?.Channels ?? _log?.Channels ?? [],
                _dbc.FilePath, _annotator.ProfileName,
                Mode: _log is null ? "live" : "log", LogPath: _log?.Path,
                ChannelDbc: _channelDbc.Select(kv => $"{kv.Key}={Path.GetFileName(kv.Value.FilePath)}").ToArray()),
        };
        _server = new TcpApiServer(_hub, _api);
        _server.Info += msg => Dispatcher.BeginInvoke(() => InfoText.Text = msg);

        _hub.FrameObserved += f => _pending.Enqueue(f);

        PaneA.SelectionChanged += () => OnPaneSelectionChanged(PaneA);
        PaneB.SelectionChanged += () => OnPaneSelectionChanged(PaneB);
        PaneA.ZoomRequested += ZoomText;
        PaneB.ZoomRequested += ZoomText;

        // 20 Hz, chosen with its cost known. Every tick that publishes rows repaints the whole
        // trace list, and on a maximised 4K window that is the app's dominant cost — GPU and
        // compositor work rather than CPU, which is why it drags the whole desktop and not just
        // this window. Measured on a live 2-channel bus at ~1,080 frames/s:
        // 20 Hz = 34% GPU / 8% DWM, 10 Hz = 13% / 3%, 5 Hz = 9% / 5%; paused = 4%.
        // 20 Hz costs about 2.6x what 10 Hz does and buys a visibly smoother scroll. This is
        // the single number to lower on a weak GPU or a large multi-monitor desktop.
        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        _periodicTimer = new DispatcherTimer();
        _periodicTimer.Tick += (_, _) => DoSend(silent: true);

        ApplySettings();    // before the calls below, which read what it sets
        ApplyLayout();
        RefreshDevices();
        ApplyServerSetting();
        ApplyMcpSetting();
        UpdateConnStatus();
        UpdateMenuState();
        UpdateSummary();
        UpdatePeriodicButton();
    }

    /// <summary>
    /// Puts the persisted session settings back where they live: fields for the values behind
    /// dialogs, menu checks for the booleans. Load() has already clamped every numeric, so this
    /// only validates what needs more than a range — the channel string, and a window position
    /// that must land on a monitor that still exists.
    /// </summary>
    private void ApplySettings()
    {
        var s = _settings;

        if (s.Channels is { Length: > 0 } channels)
        {
            try
            {
                ParseChannels(channels, s.Bitrate, s.FdEnabled, s.FdBitrate);
                _channelsText = channels;
            }
            catch (FormatException) { }     // hand-edited into nonsense; keep the default
        }
        _bitrate = s.Bitrate;
        _fdBitrate = s.FdBitrate;
        MenuFdEnabled.IsChecked = s.FdEnabled;
        _layout = s.Layout;
        _historyCapacity = s.HistoryCapacity;
        PaneA.SetHistoryCapacity(_historyCapacity);
        PaneB.SetHistoryCapacity(_historyCapacity);
        _apiPort = s.ApiPort;
        _mcpPort = s.McpPort;
        _cycleMs = s.CycleMs;
        MenuApiServer.IsChecked = s.ApiServer;
        MenuMcpServer.IsChecked = s.McpServer;

        if (Enum.TryParse(s.Timestamps, out TimestampMode mode)) SetTimestampMode(mode);
        ApplyFontSize(s.FontSize);

        // Only when a placement was saved, and only onto a screen that still exists — restoring
        // onto a monitor that was unplugged would take the window with it.
        if (s.WindowWidth >= 400 && s.WindowHeight >= 300)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
            double right = SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth;
            double bottom = SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight;
            if (s.WindowLeft >= SystemParameters.VirtualScreenLeft - s.WindowWidth + 100 &&
                s.WindowLeft <= right - 100 &&
                s.WindowTop >= SystemParameters.VirtualScreenTop &&
                s.WindowTop <= bottom - 50)
            {
                WindowStartupLocation = WindowStartupLocation.Manual;
                Left = s.WindowLeft;
                Top = s.WindowTop;
            }
            if (s.WindowMaximized) WindowState = WindowState.Maximized;
        }
    }

    /// <summary>
    /// The mirror of <see cref="ApplySettings"/>, run once at close. The recent lists were saved
    /// as they changed; everything here is cheap to gather and gains nothing from being written
    /// earlier.
    /// </summary>
    private void SaveSettings()
    {
        _settings.Channels = _channelsText;
        _settings.Bitrate = _bitrate;
        _settings.FdBitrate = _fdBitrate;
        _settings.FdEnabled = MenuFdEnabled.IsChecked;
        _settings.Layout = _layout;
        _settings.FontSize = _fontSize;
        _settings.Timestamps = _timestampMode.ToString();
        _settings.HistoryCapacity = _historyCapacity;
        _settings.ApiServer = MenuApiServer.IsChecked;
        _settings.ApiPort = _apiPort;
        _settings.McpServer = MenuMcpServer.IsChecked;
        _settings.McpPort = _mcpPort;
        _settings.CycleMs = _cycleMs;

        // RestoreBounds rather than the live values when maximized or minimized, so the saved
        // size is the one the window would return to.
        var bounds = WindowState == WindowState.Normal
            ? new Rect(Left, Top, ActualWidth, ActualHeight)
            : RestoreBounds;
        if (bounds.Width >= 400 && bounds.Height >= 300)
        {
            _settings.WindowLeft = bounds.Left;
            _settings.WindowTop = bounds.Top;
            _settings.WindowWidth = bounds.Width;
            _settings.WindowHeight = bounds.Height;
        }
        _settings.WindowMaximized = WindowState == WindowState.Maximized;
        _settings.Save();
    }

    /// <summary>Panes that currently receive frames. A hidden pane is not fed, so it costs nothing.</summary>
    private IEnumerable<ChannelPane> ActivePanes
    {
        get
        {
            yield return PaneA;
            if (PaneB.Visibility == Visibility.Visible) yield return PaneB;
        }
    }

    // ---------- menu plumbing ----------

    /// <summary>
    /// Rebuilds a submenu as a radio group. Submenus are filled on open rather than kept in sync,
    /// so a list that changes underneath (devices, open channels, configured sessions) can never
    /// show a stale entry.
    /// </summary>
    private static void FillRadioMenu<T>(MenuItem parent, IReadOnlyList<T> items, Func<T, string> label,
                                         Func<T, bool> isSelected, Action<T> pick)
    {
        parent.Items.Clear();
        if (items.Count == 0)
        {
            parent.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        foreach (var item in items)
        {
            var captured = item;
            var entry = new MenuItem
            {
                Header = Escape(label(item)),
                IsCheckable = true,
                IsChecked = isSelected(item),
            };
            entry.Click += (_, _) => pick(captured);
            parent.Items.Add(entry);
        }
    }

    /// <summary>An underscore in a device name or file path is not an access key.</summary>
    private static string Escape(string header) => header.Replace("_", "__");

    private void DeviceMenu_Opened(object sender, RoutedEventArgs e) =>
        FillRadioMenu(MenuDevice, _devices, d => d.Label, d => ReferenceEquals(d, _device), d =>
        {
            _device = d;
            UpdateConnStatus();
        });

    private void BitrateMenu_Opened(object sender, RoutedEventArgs e) =>
        FillBitrateMenu(MenuBitrate, Bitrates, _bitrate, "Bitrate",
                        "Arbitration bitrate in bit/s:", b => { _bitrate = b; UpdateSummary(); });

    private void FdBitrateMenu_Opened(object sender, RoutedEventArgs e) =>
        FillBitrateMenu(MenuFdBitrate, FdBitrates, _fdBitrate, "CAN FD data bitrate",
                        "Bitrate of the data phase, in bit/s:", b => { _fdBitrate = b; UpdateSummary(); });

    /// <summary>
    /// The usual speeds, whatever is set now, and a Custom… entry for the rest.
    ///
    /// A fixed list was the only way in, so a bus running at a speed nobody thought to put in it
    /// could not be selected here at all — and the speeds that belong in the list depend on the
    /// device, which is not known until it is opened. Anything entered is offered to the device
    /// at connect, which is the only place that can actually judge it.
    /// </summary>
    private void FillBitrateMenu(MenuItem parent, IReadOnlyList<int> presets, int current,
                                 string title, string prompt, Action<int> pick)
    {
        List<int> rates = presets.Contains(current) ? [.. presets] : [.. presets, current];
        rates.Sort();
        FillRadioMenu(parent, rates, b => $"{b:N0} bit/s", b => b == current, pick);
        parent.Items.Add(new Separator());

        var custom = new MenuItem { Header = "_Custom…" };
        custom.Click += (_, _) =>
        {
            int? entered = InputDialog.AskInt(this, title, prompt, current, 1_000, 8_000_000,
                "Not every device can produce every speed. Connect says so if this one cannot, " +
                "and names what it can do instead.");
            if (entered is not null) pick(entered.Value);
        };
        parent.Items.Add(custom);
    }

    private void FdEnabled_Click(object sender, RoutedEventArgs e) => UpdateSummary();

    private void TxChannelMenu_Opened(object sender, RoutedEventArgs e) =>
        FillRadioMenu(MenuTxChannel, _adapter?.Channels ?? [], c => c,
                      c => string.Equals(c, _txChannel, StringComparison.OrdinalIgnoreCase), c =>
                      {
                          _txChannel = c;
                          TxChannelText.Text = c;
                      });

    private void PaneAMenu_Opened(object sender, RoutedEventArgs e) => FillPaneMenu(MenuPaneA, PaneA);
    private void PaneBMenu_Opened(object sender, RoutedEventArgs e) => FillPaneMenu(MenuPaneB, PaneB);

    private static void FillPaneMenu(MenuItem parent, ChannelPane pane)
    {
        FillRadioMenu(parent, pane.ChannelItems, c => c,
                      c => string.Equals(c, pane.SelectedChannel, StringComparison.OrdinalIgnoreCase),
                      pane.SelectChannel);
        parent.Items.Add(new Separator());
        foreach (var (label, trace) in new[] { ("_Trace", true), ("_Fixed", false) })
        {
            bool captured = trace;
            var entry = new MenuItem { Header = label, IsCheckable = true, IsChecked = pane.ShowsTrace == trace };
            entry.Click += (_, _) => pane.SetTraceView(captured);
            parent.Items.Add(entry);
        }
    }

    private void RecentDbcMenu_Opened(object sender, RoutedEventArgs e)
    {
        MenuRecentDbc.Items.Clear();
        if (_settings.RecentDbc.Count == 0)
        {
            MenuRecentDbc.Items.Add(new MenuItem { Header = "(none)", IsEnabled = false });
            return;
        }
        for (int i = 0; i < _settings.RecentDbc.Count; i++)
        {
            string path = _settings.RecentDbc[i];
            var entry = new MenuItem
            {
                Header = $"_{i + 1} {Escape(Path.GetFileName(path))}",
                ToolTip = path,
                IsChecked = string.Equals(path, _dbc.FilePath, StringComparison.OrdinalIgnoreCase),
                IsCheckable = true,
            };
            entry.Click += (_, _) => LoadDbc(path);
            MenuRecentDbc.Items.Add(entry);
        }
    }

    /// <summary>Enabled state and radio marks for everything the menu owns.</summary>
    private void UpdateMenuState()
    {
        bool connected = _adapter != null;
        bool offline = _log is not null;
        MenuTransmit.IsEnabled = connected;
        PeriodicButton.IsEnabled = connected;   // Send is driven by the command's CanExecute
        MenuDevice.IsEnabled = MenuBitrate.IsEnabled = MenuFd.IsEnabled = !connected && !offline;
        MenuUnloadDbc.IsEnabled = _dbc.IsLoaded || _channelDbc.Count > 0;
        MenuUnloadDbcChannel.IsEnabled = _channelDbc.Count > 0;
        MenuCloseLog.IsEnabled = offline;
        MenuPaneB.IsEnabled = _layout != 0;

        // Both would lie about what the view is doing over a file: Pause claims it is following a
        // bus, Clear offers to throw away a load with no way back.
        MenuPause.IsEnabled = PauseButton.IsEnabled = !offline;

        bool xcp = IsXcpSelected;
        MenuXcpSet.IsEnabled = MenuXcpRemove.IsEnabled =
            MenuXcpA2l.IsEnabled = MenuXcpOnly.IsEnabled = xcp;

        MenuLayoutSingle.IsChecked = _layout == 0;
        MenuLayoutSplitH.IsChecked = _layout == 1;
        MenuLayoutSplitV.IsChecked = _layout == 2;

        CommandManager.InvalidateRequerySuggested();
    }

    /// <summary>
    /// The line that keeps the hidden settings visible. A setting moved into a menu that shows
    /// nowhere else is a setting nobody can check against the device in front of them.
    /// </summary>
    private void UpdateSummary()
    {
        // Over a file the bus settings describe nothing: the bitrate is whatever the recording was
        // made at, which the file does not say, and the channels are the file's, not the boxes'.
        string channels = _log is { } open
            ? string.Join(",", open.Channels)
            : string.IsNullOrWhiteSpace(_channelsText) ? "CAN1" : _channelsText;
        string speed = _log is not null
            ? "from file"
            : FormatBitrate(_bitrate) + (MenuFdEnabled.IsChecked ? $" + FD {FormatBitrate(_fdBitrate)}" : "");
        string dbc = _channelDbc.Count > 0
            ? string.Join(" + ", _channelDbc.OrderBy(kv => ChannelPalette.Index(kv.Key))
                                            .Select(kv => $"{kv.Key}:{Path.GetFileName(kv.Value.FilePath)}"))
            : _dbc.IsLoaded ? Path.GetFileName(_dbc.FilePath!) : "no DBC";
        SummaryText.Text = $"{channels} · {speed} · {dbc} · Profile: {(IsXcpSelected ? "XCP" : "None")}";
    }

    private static string FormatBitrate(int bps) =>
        bps % 1_000_000 == 0 ? $"{bps / 1_000_000}M" : $"{bps / 1000}k";

    // ---------- frame flow ----------

    private void FlushPending()
    {
        // A file has its own clock; nothing arrives on the queue and the transport drives the
        // views instead.
        if (_log is not null) { FlushReplay(); return; }

        bool paused = PauseButton.IsChecked == true;
        bool highlight = MenuHighlight.IsChecked;
        double now = _uiClock.Elapsed.TotalSeconds;

        // Null unless the XCP-only filter is armed *and* at least one session is configured —
        // a half-configured profile must not blank the whole view.
        var xcpFilter = IsXcpSelected && MenuXcpOnly.IsChecked && _annotator.XcpSessions.Count > 0
            ? _annotator
            : null;

        // Hoisted: enumerating the panes per frame allocated an iterator for every frame.
        var panes = ActivePanes.ToArray();
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * FlushBudgetMs / 1000.0);
        int added = 0, sinceClockCheck = 0;

        while (_pending.TryDequeue(out var f))
        {
            if (!_haveZero)
            {
                // First frame of the capture: this is what absolute and relative are measured
                // from. Taken here rather than at connect so an idle bus does not skew it.
                _haveZero = true;
                _zeroTs = f.Timestamp;
                _zeroWall = DateTime.Now;
                ApplyTimeBase();
            }
            if (!paused && (xcpFilter is null || xcpFilter.IsProtocolFrame(f)))
            {
                foreach (var pane in panes)
                {
                    if (!pane.Accepts(f)) continue;
                    pane.Append(f, now, highlight);
                    added++;
                }
            }
            // Reading the clock every frame would itself be a measurable cost.
            if (++sinceClockCheck >= 256)
            {
                sinceClockCheck = 0;
                if (Stopwatch.GetTimestamp() >= deadline) break;
            }
        }

        DropDisplayBacklog();

        // Decay runs on every tick, not only when frames arrived, so highlights still fade out
        // on an idle bus. Rows that have finished fading skip out immediately.
        if (highlight)
            foreach (var pane in panes)
                if (pane.FixedVisible) pane.TickFade(now);

        if (added == 0) return;
        foreach (var pane in panes) pane.AfterAppend();
    }

    /// <summary>
    /// Discards the display backlog when it grows past what the UI could ever catch up with.
    /// Silently falling further behind would be worse: the trace would drift minutes into the
    /// past while claiming to be live. The skipped count is surfaced in the status bar.
    /// </summary>
    private void DropDisplayBacklog()
    {
        int backlog = _pending.Count;
        if (backlog <= DisplayBacklogCap) return;

        int drop = backlog - (DisplayBacklogCap / 2);
        int dropped = 0;
        while (dropped < drop && _pending.TryDequeue(out _)) dropped++;
        _displaySkipped += dropped;
    }

    private void UpdateStatusBar()
    {
        if (_log is { } log)
        {
            // A rate means nothing here. Differencing the totals of a file that finished loading
            // shows one enormous spike and then zero for ever, which reads as a dead bus.
            ConnStatusText.Text = $"LOG FILE — {Path.GetFileName(log.Path)}";
            RateText.Text = $"{log.FirstTimestamp:0.000} – {log.LastTimestamp:0.000} s   ({log.Duration:0.000} s)";
            TotalText.Text = $"{log.Frames.Count:N0} frames in file" +
                (log.SkippedLines > 0 ? $"   ·   {log.SkippedLines:N0} lines not understood" : "");
            ServerStatusText.Text = ServerStatusLine();
            foreach (var pane in ActivePanes)
                pane.UpdateStats($"{pane.TraceCount:N0} rows — log file");
            return;
        }

        long total = _hub.TotalFrames;

        // Rates are recomputed only when enough time has passed to mean something. This method
        // also runs on demand — a pane change, a play/pause — and differencing the totals over
        // those few milliseconds used to show a near-zero rate until the next timer tick. The
        // divisor is measured rather than assumed to be the timer's interval, which drifts on a
        // busy UI thread.
        double nowSeconds = _uiClock.Elapsed.TotalSeconds;
        double elapsed = nowSeconds - _lastRateAt;
        if (elapsed >= 0.5)
        {
            var perChannel = _hub.ChannelTotals();

            // Per-channel rate: on a 2-port setup "CAN2 went quiet" is the thing you need to see,
            // and a single combined number hides exactly that. Every open channel is listed even
            // when it has never carried a frame — a channel that silently vanishes from the list
            // is precisely the failure this is meant to surface.
            var channels = _adapter?.Channels.ToList() ?? [.. perChannel.Keys];
            foreach (var extra in perChannel.Keys.Where(c => !channels.Contains(c, StringComparer.OrdinalIgnoreCase)))
                channels.Add(extra);

            var parts = channels
                .OrderBy(ChannelPalette.Index)
                .Select(c =>
                {
                    long now = perChannel.TryGetValue(c, out long n) ? n : 0;
                    long was = _lastChannelTotals.TryGetValue(c, out long p) ? p : 0;
                    return $"{c}: {(long)Math.Round((now - was) / elapsed)} fps";
                })
                .ToList();
            RateText.Text = parts.Count > 0
                ? string.Join("   ", parts)
                : $"{(long)Math.Round((total - _lastTotal) / elapsed)} fps";
            _lastChannelTotals = perChannel;
            _lastTotal = total;
            _lastRateAt = nowSeconds;
        }

        TotalText.Text = $"{total:N0} frames" +
            (_displaySkipped > 0 ? $"  (display behind: {_displaySkipped:N0} not shown)" : "");
        ServerStatusText.Text = ServerStatusLine();

        // "paused" is worth saying on the pane itself: it is both why the rows stopped moving and
        // the reason scrolling back works at all.
        string state = PauseButton.IsChecked == true ? " — paused, scroll to browse" : "";
        foreach (var pane in ActivePanes)
            pane.UpdateStats($"{pane.TraceCount:N0} rows{state}");
    }

    /// <summary>Both remote front ends in one status-bar field, so neither is invisible.</summary>
    private string ServerStatusLine()
    {
        string api = _server.IsRunning
            ? $"API: 127.0.0.1:{_server.Port} ({_server.ClientCount} clients)"
            : "API: off";
        string mcp = _mcp?.State == Core.Mcp.McpHttpState.Running
            ? $"MCP: 127.0.0.1:{_mcp.Port}"
            : "MCP: off";
        return $"{api}   ·   {mcp}";
    }

    // ---------- layout ----------

    private void LayoutSingle_Executed(object sender, ExecutedRoutedEventArgs e) => SetLayout(0);
    private void LayoutSplitH_Executed(object sender, ExecutedRoutedEventArgs e) => SetLayout(1);
    private void LayoutSplitV_Executed(object sender, ExecutedRoutedEventArgs e) => SetLayout(2);

    private void CanSplitForXcp(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = IsXcpSelected;
    private void LayoutXcpSplit_Executed(object sender, ExecutedRoutedEventArgs e) => ApplyXcpSplit();

    /// <summary>
    /// The two halves of an XCP session want opposite views, so give them one pane each: the
    /// command exchange scrolling on top, where the order of CONNECT / ALLOC / WRITE_DAQ is the
    /// whole point, and the DAQ stream aggregated underneath, where the order is noise and what
    /// matters is one row per ODT with its period and its changing bytes.
    /// </summary>
    private void ApplyXcpSplit()
    {
        SetLayout(2);                                   // stacked: commands above, data below
        // Both panes span every channel. They are two views of the same thing, so pinning one
        // to CAN1 and leaving the other on CAN2 — which is what the split defaults would do on
        // a 2-port master — would pair a command exchange with somebody else's DAQ stream.
        PaneA.SelectChannel(ChannelPane.AllChannels);
        PaneB.SelectChannel(ChannelPane.AllChannels);
        PaneA.SetTraceView(true);
        PaneA.SetContentMode(ChannelPane.PaneContent.XcpCommands);
        PaneB.SetTraceView(false);
        PaneB.SetContentMode(ChannelPane.PaneContent.XcpData);
        ApplyContentFilters();
        InfoText.Text = "XCP split: commands above (trace), DAQ data below (aggregated per ODT).";
    }

    /// <summary>
    /// Hands each pane the test behind its content selection. The decoder has already separated
    /// the two: it reserves aggregate group 0 for command traffic and gives every DAQ/STIM
    /// object its PID + 1, so nothing has to be classified a second time here.
    /// </summary>
    private void ApplyContentFilters()
    {
        foreach (var pane in new[] { PaneA, PaneB })
            pane.SetContentFilter(pane.ContentMode switch
            {
                // Group 0 also covers every frame no session claimed, hence the second test.
                ChannelPane.PaneContent.XcpCommands =>
                    f => (f.Annotation?.GroupKey ?? 0) == 0 && _annotator.IsProtocolFrame(f),
                // A non-zero group can only have come from the XCP decoder.
                ChannelPane.PaneContent.XcpData => f => (f.Annotation?.GroupKey ?? 0) > 0,
                _ => null,
            });
    }

    private void OnPaneSelectionChanged(ChannelPane pane)
    {
        // The pane cleared itself because its basis changed; with a file behind it there is
        // something to put back.
        if (_log is not null) ProjectToPanes(pane);
        OnPaneSelectionChanged();
    }

    private void OnPaneSelectionChanged()
    {
        ApplyContentFilters();
        UpdateStatusBar();
    }

    // ---------- text size ----------

    private const double DefaultFontSize = 12;
    private double _fontSize = DefaultFontSize;

    private void FontLarger_Executed(object sender, ExecutedRoutedEventArgs e) => ZoomText(+1);
    private void FontSmaller_Executed(object sender, ExecutedRoutedEventArgs e) => ZoomText(-1);
    private void FontReset_Executed(object sender, ExecutedRoutedEventArgs e) => SetFontSize(DefaultFontSize);

    private void ZoomText(int steps) => SetFontSize(_fontSize + steps);

    /// <summary>
    /// Both panes share one text size — they are two views of the same capture, and a trace at
    /// 9 pt above one at 18 pt is nobody's intent. Column widths scale with it, otherwise the
    /// first step up clips every column.
    /// </summary>
    private void SetFontSize(double size)
    {
        if (!ApplyFontSize(size)) return;
        InfoText.Text = $"Text size {_fontSize:0} pt (Ctrl+wheel, Ctrl+/Ctrl-, Ctrl+0 to reset).";
    }

    /// <summary>The change itself, silent — startup restores a saved size through here without
    /// putting a message in the status line before anything has happened.</summary>
    private bool ApplyFontSize(double size)
    {
        double clamped = Math.Clamp(size, 8, 28);
        if (Math.Abs(clamped - _fontSize) < 0.01) return false;
        _fontSize = clamped;
        PaneA.SetFontSize(_fontSize);
        PaneB.SetFontSize(_fontSize);
        return true;
    }

    // ---------- timestamps ----------

    private void Timestamps_Click(object sender, RoutedEventArgs e)
    {
        SetTimestampMode(
            ReferenceEquals(sender, MenuTsAbsolute) ? TimestampMode.Absolute :
            ReferenceEquals(sender, MenuTsDelta) ? TimestampMode.Delta :
            TimestampMode.Relative);
    }

    private void SetTimestampMode(TimestampMode mode)
    {
        _timestampMode = mode;
        MenuTsAbsolute.IsChecked = mode == TimestampMode.Absolute;
        MenuTsRelative.IsChecked = mode == TimestampMode.Relative;
        MenuTsDelta.IsChecked = mode == TimestampMode.Delta;
        ApplyTimeBase();
    }

    /// <summary>
    /// Hands both panes the current mode and capture anchor. Rows already on screen are re-read
    /// rather than discarded — switching to delta is most useful on a capture you have already
    /// paused and are picking through.
    /// </summary>
    private void ApplyTimeBase()
    {
        var time = new TimeBase(_timestampMode, _zeroTs, _zeroWall);
        PaneA.SetTimeBase(time);
        PaneB.SetTimeBase(time);
    }

    private void SetLayout(int layout)
    {
        _layout = layout;
        ApplyLayout();
        UpdateMenuState();
    }

    private void ApplyLayout()
    {
        if (PaneHost is null) return; // fires during InitializeComponent

        PaneHost.RowDefinitions.Clear();
        PaneHost.ColumnDefinitions.Clear();
        foreach (UIElement el in new UIElement[] { PaneA, PaneSplitter, PaneB })
        {
            Grid.SetRow(el, 0);
            Grid.SetColumn(el, 0);
        }

        if (_layout == 0)
        {
            PaneSplitter.Visibility = Visibility.Collapsed;
            PaneB.Visibility = Visibility.Collapsed;
            PaneB.ClearAll();   // don't keep a hidden backlog alive
            UpdateStatusBar();
            return;
        }

        PaneSplitter.Visibility = Visibility.Visible;
        PaneB.Visibility = Visibility.Visible;

        if (_layout == 1)   // side by side
        {
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(PaneA, 0);
            Grid.SetColumn(PaneSplitter, 1);
            Grid.SetColumn(PaneB, 2);
            PaneSplitter.ResizeDirection = GridResizeDirection.Columns;
            PaneSplitter.Cursor = Cursors.SizeWE;
        }
        else                                   // stacked
        {
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(5) });
            PaneHost.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            Grid.SetRow(PaneA, 0);
            Grid.SetRow(PaneSplitter, 1);
            Grid.SetRow(PaneB, 2);
            PaneSplitter.ResizeDirection = GridResizeDirection.Rows;
            PaneSplitter.Cursor = Cursors.SizeNS;
        }
        UpdateStatusBar();
    }

    // ---------- view ----------

    private void GoToTime_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        if (_log is not { } log) return;
        string? entered = InputDialog.Ask(this, "Go to time", "Seconds on the log's own clock:",
                                          log.FirstTimestamp.ToString("0.000"),
                                          $"This file runs {log.FirstTimestamp:0.000} to {log.LastTimestamp:0.000} s.");
        if (entered is null) return;
        if (!double.TryParse(entered.Trim(), System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double seconds))
        {
            MessageBox.Show(this, $"'{entered}' is not a number of seconds.",
                            "Go to time", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Moves the replay, not just the scroll position — otherwise the transport would go on
        // claiming to be somewhere else.
        SeekTo(seconds);
        ShowPlayState();
        int missed = ActivePanes.Count(p => !p.ScrollToTime(seconds));
        InfoText.Text = missed == 0
            ? $"Moved to {seconds:0.000} s."
            : $"Moved to {seconds:0.000} s — {missed} pane(s) hold nothing that late.";
    }

    private void JumpToLive_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        foreach (var pane in ActivePanes) pane.JumpToLive();
    }

    private void TogglePause_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        // Same key, same meaning either way: stop the view moving. Over a file that is the
        // replay; over a capture it is the display.
        if (_log is not null) { TogglePlay(); return; }
        PauseButton.IsChecked = PauseButton.IsChecked != true;
    }

    // Two views of one state. Assigning a value a control already holds raises nothing, so the
    // pair settles after one hop rather than bouncing.
    private void PauseButton_Changed(object sender, RoutedEventArgs e)
    {
        MenuPause.IsChecked = PauseButton.IsChecked == true;
        // Resuming means the view follows the tail again, so put it there rather than leaving it
        // parked wherever the user stopped reading.
        if (PauseButton.IsChecked != true)
            foreach (var pane in ActivePanes) pane.JumpToLive();
        UpdateStatusBar();
    }

    private void MenuPause_Changed(object sender, RoutedEventArgs e) =>
        PauseButton.IsChecked = MenuPause.IsChecked;

    private void HistorySize_Click(object sender, RoutedEventArgs e)
    {
        int? capacity = InputDialog.AskInt(this, "History size", "Trace rows kept per pane:",
                                           _historyCapacity, TraceBuffer.MinCapacity, 5_000_000,
                                           "Once full, the oldest row is overwritten in place.");
        if (capacity is null) return;
        _historyCapacity = capacity.Value;
        PaneA.SetHistoryCapacity(_historyCapacity);
        PaneB.SetHistoryCapacity(_historyCapacity);
        InfoText.Text = $"History set to {_historyCapacity:N0} rows per pane.";
    }

    private void Highlight_Changed(object sender, RoutedEventArgs e)
    {
        // Leaving stale highlights frozen on screen would misreport them as recent changes.
        foreach (var pane in ActivePanes) pane.ClearHighlights();
    }

    /// <summary>
    /// Clears immediately, with no confirmation. The design handoff asked for a prompt on a
    /// destructive action, but this one is pressed reflexively while watching traffic — a modal
    /// in that path costs more than the mistake does, and the buffer refills the moment it is
    /// gone. The status line says what went.
    /// </summary>
    private void ClearAll_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        long discarded = _hub.TotalFrames;
        // Before the panes: frames already queued for display were captured before Clear was
        // pressed, and the next tick would put them straight back into the emptied trace —
        // rows the hub has just dropped and recent() can no longer return. The first of them
        // would also become the new time anchor, dating the capture from before the Clear.
        while (_pending.TryDequeue(out _)) { }
        PaneA.ClearAll();
        PaneB.ClearAll();
        _hub.Clear();
        _displaySkipped = 0;
        _haveZero = false;              // the next frame starts a new capture to measure from
        InfoText.Text = $"Cleared — {discarded:N0} captured frames discarded (recent() and the TCP API no longer see them).";
        UpdateStatusBar();
    }

    // ---------- help ----------

    // Generated from AppCommands rather than written here, so a command added there appears in
    // this dialog by construction. The previous hardcoded copy had already lost Ctrl+O and
    // Ctrl+G while the menus carried them.
    private void Shortcuts_Executed(object sender, ExecutedRoutedEventArgs e) =>
        MessageBox.Show(this, AppCommands.ShortcutsText(),
                        "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    private void Readme_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(RepoUrl) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Could not open the browser: {ex.Message}";
        }
    }

    private void About_Click(object sender, RoutedEventArgs e) =>
        new AboutDialog { Owner = this }.ShowDialog();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _flushTimer.Stop();
        _statusTimer.Stop();
        StopPeriodic();
        SaveSettings();
        _mcp?.Stop();
        _server.Dispose();
        try { _adapter?.Dispose(); } catch { }
    }
}
