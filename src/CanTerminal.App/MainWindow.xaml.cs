using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using CanTerminal.Core;
using CanTerminal.Core.IcsNeo;
using CanTerminal.Core.Xcp;
using Microsoft.Win32;

namespace CanTerminal.App;

public partial class MainWindow : Window
{
    private const string RepoUrl = "https://github.com/firepooh/CanTerminal";

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
    private static readonly int[] FdBitrates = [2_000_000, 5_000_000, 8_000_000];

    private readonly MessageHub _hub = new();
    private readonly DbcDecoder _dbc = new();
    private readonly FrameAnnotator _annotator;
    private readonly TcpApiServer _server;
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

    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _periodicTimer;
    private long _lastTotal;
    private long _displaySkipped;
    private Dictionary<string, long> _lastChannelTotals = [];

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

        _server = new TcpApiServer(_hub)
        {
            OnSend = (channel, id, data, ext, fd, brs, source) =>
            {
                var a = _adapter;
                if (a?.IsOpen != true) throw new InvalidOperationException("No device connected in CanTerminal.");
                a.Send(channel, id, data, ext, fd, brs, source);
            },
            StatusProvider = () => new ApiStatus(_adapter?.IsOpen == true, _adapter?.Name, _adapter?.Channels ?? [],
                                                 _dbc.FilePath, _annotator.ProfileName),
        };
        _server.Info += msg => Dispatcher.BeginInvoke(() => InfoText.Text = msg);

        _hub.FrameObserved += f => _pending.Enqueue(f);

        PaneA.SelectionChanged += OnPaneSelectionChanged;
        PaneB.SelectionChanged += OnPaneSelectionChanged;
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

        ApplyLayout();
        RefreshDevices();
        ApplyServerSetting();
        UpdateConnStatus();
        UpdateMenuState();
        UpdateSummary();
        UpdatePeriodicButton();
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
        FillRadioMenu(MenuBitrate, Bitrates, b => $"{b:N0} bit/s", b => b == _bitrate, b =>
        {
            _bitrate = b;
            UpdateSummary();
        });

    private void FdBitrateMenu_Opened(object sender, RoutedEventArgs e) =>
        FillRadioMenu(MenuFdBitrate, FdBitrates, b => $"{b:N0} bit/s", b => b == _fdBitrate, b =>
        {
            _fdBitrate = b;
            UpdateSummary();
        });

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
        MenuTransmit.IsEnabled = connected;
        PeriodicButton.IsEnabled = connected;   // Send is driven by the command's CanExecute
        MenuDevice.IsEnabled = MenuBitrate.IsEnabled = MenuFd.IsEnabled = !connected;
        MenuUnloadDbc.IsEnabled = _dbc.IsLoaded;
        MenuPaneB.IsEnabled = _layout != 0;

        bool xcp = IsXcpSelected;
        MenuXcpSet.IsEnabled = MenuXcpRemove.IsEnabled = MenuXcpDetect.IsEnabled =
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
        string channels = string.IsNullOrWhiteSpace(_channelsText) ? "CAN1" : _channelsText;
        string speed = FormatBitrate(_bitrate) +
                       (MenuFdEnabled.IsChecked ? $" + FD {FormatBitrate(_fdBitrate)}" : "");
        string dbc = _dbc.IsLoaded ? Path.GetFileName(_dbc.FilePath!) : "no DBC";
        SummaryText.Text = $"{channels} · {speed} · {dbc} · Profile: {(IsXcpSelected ? "XCP" : "None")}";
    }

    private static string FormatBitrate(int bps) =>
        bps % 1_000_000 == 0 ? $"{bps / 1_000_000}M" : $"{bps / 1000}k";

    // ---------- devices / connection ----------

    private sealed record DeviceItem(string Label, IcsDeviceInfo? Ics)
    {
        public override string ToString() => Label;
    }

    private void RefreshDevices()
    {
        var items = new List<DeviceItem> { new("Virtual bus (no hardware)", null) };
        try
        {
            foreach (var d in IcsNeoAdapter.FindDevices())
                items.Add(new DeviceItem($"{d} ({d.NumberOfClients} clients)", d));
        }
        catch (DllNotFoundException)
        {
            InfoText.Text = "icsneo40.dll not found — install Intrepid drivers for ValueCAN support.";
        }
        catch (Exception ex)
        {
            InfoText.Text = $"Device scan failed: {ex.Message}";
        }
        _devices = items;

        // Keep the current pick across a rescan; otherwise prefer real hardware over the virtual bus.
        string? previous = _device?.Label;
        _device = items.FirstOrDefault(d => d.Label == previous) ?? (items.Count > 1 ? items[1] : items[0]);
        UpdateConnStatus();
    }

    private void UpdateConnStatus()
    {
        if (_adapter is null)
            ConnStatusText.Text = _device is null ? "Disconnected" : $"Disconnected — {_device.Label}";
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adapter != null) Disconnect();
        else Connect();
    }

    private void Connect()
    {
        if (_adapter != null || _device is null) return;
        try
        {
            bool fd = MenuFdEnabled.IsChecked;
            var channels = ParseChannels(_channelsText, _bitrate, fd, _fdBitrate);
            if (channels.Count == 0) channels.Add(new CanChannelConfig("CAN1", _bitrate, fd, _fdBitrate));

            ICanAdapter adapter = _device.Ics is null ? new VirtualAdapter() : new IcsNeoAdapter(_device.Ics);
            adapter.FrameReceived += _hub.Publish;
            adapter.ErrorOccurred += msg => Dispatcher.BeginInvoke(() => InfoText.Text = msg);
            adapter.Open(channels);
            _adapter = adapter;

            _txChannel = adapter.Channels.FirstOrDefault();
            TxChannelText.Text = _txChannel ?? "—";
            ConnectButton.Content = "Disconnect";
            ConnStatusText.Text = $"Connected: {adapter.Name} [" +
                string.Join(", ", channels.Select(c => $"{c.Name.ToUpperInvariant()}@{c.Bitrate}")) +
                $"]{(fd ? " FD" : "")}";
            OnChannelsOpened(adapter.Channels);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connect failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateMenuState();
    }

    /// <summary>
    /// Parses the Channels box. An entry is "NAME", "NAME@bitrate" or "NAME@bitrate:fdbitrate";
    /// whatever is omitted falls back to the Bus menu values, so a plain "CAN1,CAN2" behaves as
    /// before. Per-channel speeds matter as soon as the two ports are not the same bus
    /// (e.g. 500k powertrain on CAN1, 125k body on CAN2).
    /// </summary>
    private static List<CanChannelConfig> ParseChannels(string text, int defaultBitrate, bool fd, int defaultFdBitrate)
    {
        var list = new List<CanChannelConfig>();
        foreach (var entry in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string name = entry;
            int bitrate = defaultBitrate, fdBitrate = defaultFdBitrate;

            int at = entry.IndexOf('@');
            if (at >= 0)
            {
                name = entry[..at].Trim();
                var speeds = entry[(at + 1)..]
                    .Split(':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (speeds.Length > 0 && !int.TryParse(speeds[0], out bitrate))
                    throw new FormatException($"'{entry}': '{speeds[0]}' is not a bitrate.");
                if (speeds.Length > 1 && !int.TryParse(speeds[1], out fdBitrate))
                    throw new FormatException($"'{entry}': '{speeds[1]}' is not an FD bitrate.");
            }

            if (name.Length == 0) throw new FormatException($"'{entry}' has no channel name.");
            list.Add(new CanChannelConfig(name, bitrate, fd, fdBitrate));
        }
        return list;
    }

    /// <summary>Offers the freshly opened channels to every control that targets one.</summary>
    private void OnChannelsOpened(IReadOnlyList<string> channels)
    {
        PaneA.SetChannels(channels);                                    // stays on "All" — merged timeline
        PaneB.SetChannels(channels, prefer: channels.Count > 1 ? channels[1] : null);

        if (_xcpChannel is null || !channels.Contains(_xcpChannel, StringComparer.OrdinalIgnoreCase))
            _xcpChannel = channels.FirstOrDefault();
    }

    private void Disconnect()
    {
        StopPeriodic();
        try { _adapter?.Dispose(); } catch { }
        _adapter = null;
        _txChannel = null;
        TxChannelText.Text = "—";       // no channel is open, so naming one would be a lie
        ConnectButton.Content = "Connect";
        UpdateConnStatus();
        UpdateMenuState();
    }

    // ---------- frame flow ----------

    private void FlushPending()
    {
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
        long total = _hub.TotalFrames;
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
                return $"{c}: {now - was} fps";
            })
            .ToList();
        RateText.Text = parts.Count > 0 ? string.Join("   ", parts) : $"{total - _lastTotal} fps";
        _lastChannelTotals = perChannel;
        _lastTotal = total;

        TotalText.Text = $"{total:N0} frames" +
            (_displaySkipped > 0 ? $"  (display behind: {_displaySkipped:N0} not shown)" : "");
        ServerStatusText.Text = _server.IsRunning
            ? $"API server: 127.0.0.1:{_server.Port} ({_server.ClientCount} clients)"
            : "API server: off";

        // "paused" is worth saying on the pane itself: it is both why the rows stopped moving and
        // the reason scrolling back works at all.
        string state = PauseButton.IsChecked == true ? " — paused, scroll to browse" : "";
        foreach (var pane in ActivePanes)
            pane.UpdateStats($"{pane.TraceCount:N0} rows{state}");
    }

    // ---------- TX ----------

    private void CanTransmit(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter != null;

    private void SendFrame_Executed(object sender, ExecutedRoutedEventArgs e) => DoSend(silent: false);

    private void DoSend(bool silent)
    {
        try
        {
            var a = _adapter ?? throw new InvalidOperationException("Not connected.");
            string idText = TxIdBox.Text.Trim();
            if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText[2..];
            uint id = uint.Parse(idText, System.Globalization.NumberStyles.HexNumber);
            byte[] data = Convert.FromHexString(TxDataBox.Text.Replace(" ", "").Replace("-", ""));
            string channel = _txChannel ?? a.Channels.FirstOrDefault() ?? "CAN1";
            bool ext = MenuTxExt.IsChecked || id > 0x7FF;
            a.Send(channel, id, data, ext, MenuTxFd.IsChecked, MenuTxBrs.IsChecked, "ui");
        }
        catch (Exception ex)
        {
            StopPeriodic();
            if (!silent) MessageBox.Show(this, ex.Message, "Send failed", MessageBoxButton.OK, MessageBoxImage.Warning);
            else InfoText.Text = $"Periodic TX stopped: {ex.Message}";
        }
    }

    private void PeriodicButton_Click(object sender, RoutedEventArgs e)
    {
        if (_periodicTimer.IsEnabled) StopPeriodic();
        else StartPeriodic();
    }

    private void StartCyclic_Executed(object sender, ExecutedRoutedEventArgs e) => StartPeriodic();
    private void StopCyclic_Executed(object sender, ExecutedRoutedEventArgs e) => StopPeriodic();

    private void StartPeriodic()
    {
        if (_periodicTimer.IsEnabled) return;
        _periodicTimer.Interval = TimeSpan.FromMilliseconds(_cycleMs);
        _periodicTimer.Start();
        UpdatePeriodicButton();
    }

    private void StopPeriodic()
    {
        _periodicTimer.Stop();
        UpdatePeriodicButton();
    }

    /// <summary>The cycle time lives in a dialog now, so the button label has to carry it.</summary>
    private void UpdatePeriodicButton() =>
        PeriodicButton.Content = $"{(_periodicTimer.IsEnabled ? "Stop" : "Start")} · {_cycleMs} ms";

    private void CycleTime_Click(object sender, RoutedEventArgs e)
    {
        int? ms = InputDialog.AskInt(this, "Cycle time", "Cyclic TX period in milliseconds:",
                                     _cycleMs, 1, 3_600_000);
        if (ms is null) return;
        _cycleMs = ms.Value;
        if (_periodicTimer.IsEnabled) _periodicTimer.Interval = TimeSpan.FromMilliseconds(_cycleMs);
        UpdatePeriodicButton();
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
        double clamped = Math.Clamp(size, 8, 28);
        if (Math.Abs(clamped - _fontSize) < 0.01) return;
        _fontSize = clamped;
        PaneA.SetFontSize(_fontSize);
        PaneB.SetFontSize(_fontSize);
        InfoText.Text = $"Text size {_fontSize:0} pt (Ctrl+wheel, Ctrl+/Ctrl-, Ctrl+0 to reset).";
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

    // ---------- protocol profile (XCP) ----------

    private bool IsXcpSelected => MenuProfileXcp.IsChecked;

    private void ProfileNone_Click(object sender, RoutedEventArgs e) => SetProfile(false);
    private void ProfileXcp_Click(object sender, RoutedEventArgs e) => SetProfile(true);

    private void SetProfile(bool xcp)
    {
        MenuProfileNone.IsChecked = !xcp;
        MenuProfileXcp.IsChecked = xcp;
        if (!xcp) _xcpSessions.Clear();
        RebuildXcpSessions();
        UpdateMenuState();
        UpdateSummary();
        PaneA.ShowSenderColumn(xcp);
        PaneB.ShowSenderColumn(xcp);

        if (xcp)
        {
            // Selecting the profile is the moment the split becomes the useful layout, so go
            // there rather than making it a second thing to find. View ▸ Layout still wins
            // afterwards.
            ApplyXcpSplit();
        }
        else
        {
            // The XCP filters would leave both panes permanently blank without a session.
            PaneA.SetContentMode(ChannelPane.PaneContent.All);
            PaneB.SetContentMode(ChannelPane.PaneContent.All);
            ApplyContentFilters();
        }
    }

    /// <summary>
    /// Rebuilds the decoders from the configured sessions. Every change starts fresh decoders —
    /// their session state describes the old configuration — and resets the aggregate views,
    /// whose grouping basis changes with them.
    /// </summary>
    private void RebuildXcpSessions()
    {
        _annotator.XcpSessions = _xcpSessions.Values.Select(cfg => new XcpDecoder(cfg)).ToList();
        foreach (var pane in ActivePanes) pane.ResetFixed();

        XcpStatusText.Text = _xcpSessions.Count == 0
            ? IsXcpSelected ? "XCP: no session configured" : ""
            : "XCP  " + string.Join("    ", _xcpSessions
                .OrderBy(kv => ChannelPalette.Index(kv.Key))
                .Select(kv => $"{kv.Key} 0x{kv.Value.RequestId:X}/0x{kv.Value.ResponseId:X}"));
    }

    private void XcpSet_Click(object sender, RoutedEventArgs e)
    {
        var existing = _xcpSessions.ToDictionary(kv => kv.Key, kv => (kv.Value.RequestId, kv.Value.ResponseId),
                                                 StringComparer.OrdinalIgnoreCase);
        var result = XcpSessionDialog.Ask(this, AvailableXcpChannels(), _xcpChannel, existing);
        if (result is null) return;

        _xcpChannel = result.Channel;
        _xcpSessions[result.Channel] = new XcpConfig(result.RequestId, result.ResponseId, Channel: result.Channel);
        RebuildXcpSessions();
        InfoText.Text = $"XCP on {result.Channel}: req 0x{result.RequestId:X} / rsp 0x{result.ResponseId:X} — " +
                        "frames from here on are decoded.";
    }

    private void XcpRemoveMenu_Opened(object sender, RoutedEventArgs e)
    {
        var configured = _xcpSessions.Keys.OrderBy(ChannelPalette.Index).ToList();
        FillRadioMenu(MenuXcpRemove, configured,
                      c => $"{c}  0x{_xcpSessions[c].RequestId:X}/0x{_xcpSessions[c].ResponseId:X}",
                      _ => false,
                      c =>
                      {
                          _xcpSessions.Remove(c);
                          RebuildXcpSessions();
                          InfoText.Text = $"Removed the XCP session on {c}.";
                      });
    }

    private void XcpDetect_Click(object sender, RoutedEventArgs e)
    {
        var frames = _hub.GetRecent(100_000);
        var candidates = XcpAutoDetect.Scan(frames);
        if (candidates.Count == 0)
        {
            MessageBox.Show(this,
                $"No XCP-looking exchange found in the last {frames.Count:N0} captured frames.\n\n" +
                "Detection needs a command/response pair in the capture — most reliably a CONNECT. " +
                "If the session was already running before capture started, enter the IDs manually " +
                "or read them from the A2L file.",
                "XCP detect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // Scan returns candidates ranked by score, so the first hit per channel is its best.
        var best = candidates
            .GroupBy(c => c.Channel, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
        foreach (var c in best)
            _xcpSessions[c.Channel] = new XcpConfig(c.RequestId, c.ResponseId, Channel: c.Channel);
        RebuildXcpSessions();
        _xcpChannel = best[0].Channel;

        string applied = string.Join("\n", best.Select(c => $"  {c}"));
        int others = candidates.Count - best.Count;
        InfoText.Text = $"XCP detected on {best.Count} channel(s).";
        MessageBox.Show(this,
            $"Configured {best.Count} session(s):\n{applied}" +
            (others > 0 ? $"\n\n{others} weaker candidate(s) ignored." : ""),
            "XCP detect", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void XcpA2l_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "A2L files (*.a2l)|*.a2l|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // Sorted by name so a p1/p2 pair lands on the channels in the obvious order.
            var found = dlg.FileNames
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .Select(p => (File: Path.GetFileName(p), Result: A2lXcpReader.Read(p)))
                .Where(x => x.Result.HasPair)
                .ToList();

            if (found.Count == 0)
            {
                MessageBox.Show(this,
                    "No CAN_ID_MASTER / CAN_ID_SLAVE pair found in an IF_DATA XCP_ON_CAN block.\n\n" +
                    "The file may describe a different transport layer (Ethernet, USB, FlexRay).",
                    "Load A2L", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            // One file configures the selected channel; several are spread over the open
            // channels in order. The mapping is always reported so it can be checked.
            var targets = found.Count == 1
                ? [SelectedXcpChannel()]
                : AvailableXcpChannels().Take(found.Count).ToList();

            var lines = new List<string>();
            for (int i = 0; i < found.Count && i < targets.Count; i++)
            {
                var (file, r) = found[i];
                _xcpSessions[targets[i]] = new XcpConfig(r.Master!.Value, r.Slave!.Value, Channel: targets[i]);
                lines.Add($"  {file}  →  {targets[i]}   master 0x{r.Master:X} / slave 0x{r.Slave:X}" +
                          (r.Extended ? "  (29-bit)" : ""));
            }
            RebuildXcpSessions();
            _xcpChannel = targets[0];

            string skipped = found.Count > targets.Count
                ? $"\n\n{found.Count - targets.Count} file(s) had no channel to go to — only {targets.Count} channel(s) are open."
                : "";
            InfoText.Text = $"XCP IDs loaded from {lines.Count} A2L file(s).";
            MessageBox.Show(this, $"Configured:\n{string.Join("\n", lines)}{skipped}",
                            "Load A2L", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load A2L", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private List<string> AvailableXcpChannels() => _adapter?.Channels.ToList() ?? ["CAN1"];

    private string SelectedXcpChannel() =>
        _xcpChannel ?? AvailableXcpChannels().FirstOrDefault() ?? "CAN1";

    private void XcpOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (PaneA is null) return; // fires during InitializeComponent
        // The aggregate would otherwise keep rows collected under the previous filter, which
        // stop updating and read as live traffic.
        foreach (var pane in ActivePanes) pane.ResetFixed();
        InfoText.Text = MenuXcpOnly.IsChecked
            ? "Showing XCP IDs only — other traffic is still captured, just not displayed."
            : "Showing all CAN IDs.";
    }

    // ---------- view ----------

    private void JumpToLive_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        foreach (var pane in ActivePanes) pane.JumpToLive();
    }

    private void TogglePause_Executed(object sender, ExecutedRoutedEventArgs e) =>
        PauseButton.IsChecked = PauseButton.IsChecked != true;

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
        PaneA.ClearAll();
        PaneB.ClearAll();
        _hub.Clear();
        _displaySkipped = 0;
        _haveZero = false;              // the next frame starts a new capture to measure from
        InfoText.Text = $"Cleared — {discarded:N0} captured frames discarded (recent() and the TCP API no longer see them).";
        UpdateStatusBar();
    }

    // ---------- file / DBC ----------

    private void LoadDbc_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DBC files (*.dbc)|*.dbc|All files|*.*" };
        if (dlg.ShowDialog(this) == true) LoadDbc(dlg.FileName);
    }

    private void LoadDbc(string path)
    {
        try
        {
            _dbc.Load(path);
            _settings.PushRecentDbc(path);
            InfoText.Text = $"DBC loaded: {path}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DBC load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        UpdateMenuState();
        UpdateSummary();
    }

    private void UnloadDbc_Click(object sender, RoutedEventArgs e)
    {
        _dbc.Unload();
        InfoText.Text = "DBC unloaded — frames captured from here on carry no signal comments.";
        UpdateMenuState();
        UpdateSummary();
    }

    private void SaveCsv_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new SaveFileDialog
        {
            Filter = "CSV files (*.csv)|*.csv",
            FileName = $"cantrace_{DateTime.Now:yyyyMMdd_HHmmss}.csv",
        };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            // Exported from the capture buffer, not from a pane: with two panes and per-pane
            // filters "what the screen shows" is ambiguous, and the full capture is what you
            // actually want in a file.
            var frames = _hub.GetRecent(int.MaxValue);
            // The Time column follows the mode on screen; delta runs over the exported sequence,
            // which is the whole capture rather than one pane's slice of it.
            var time = new TimeBase(_timestampMode, _zeroTs, _zeroWall);
            double previousTs = double.NaN;
            var sb = new StringBuilder($"{time.ColumnHeader};Chan;Dir;ID;Flags;DLC;Data;Sender;FrameType;Comments\r\n");
            foreach (var f in frames)
            {
                var r = TraceRow.From(f, time, previousTs);
                previousTs = f.Timestamp;
                sb.Append(r.Time).Append(';').Append(r.Chan).Append(';').Append(r.Dir).Append(';')
                  .Append(r.Id).Append(';').Append(r.Flags).Append(';').Append(r.Dlc).Append(';')
                  .Append(r.Data).Append(';').Append(r.Sender).Append(';')
                  .Append(r.Type?.Replace(';', ',')).Append(';')
                  .Append(r.Decoded?.Replace(';', ',')).Append("\r\n");
            }
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            InfoText.Text = $"Saved {frames.Count:N0} captured frames to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

    // ---------- bus commands ----------

    private void CanEditBusSettings(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter is null;
    private void CanConnect(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter is null;
    private void CanDisconnect(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter != null;

    private void RefreshDevices_Executed(object sender, ExecutedRoutedEventArgs e) => RefreshDevices();
    private void Connect_Executed(object sender, ExecutedRoutedEventArgs e) => Connect();
    private void Disconnect_Executed(object sender, ExecutedRoutedEventArgs e) => Disconnect();

    private void EditChannels_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        string? entered = InputDialog.Ask(this, "Channels", "Channels to open:", _channelsText,
            "Comma separated: CAN1,CAN2,CAN3,CAN4,MSCAN,SWCAN\n" +
            "Per-channel speed: NAME@bitrate[:fdbitrate], e.g. CAN1@500000,CAN2@125000\n" +
            "Without @ the Bus ▸ Bitrate value is used.");
        if (entered is null) return;
        try
        {
            // Validate here rather than at connect time, so a typo is reported where it was made.
            ParseChannels(entered, _bitrate, MenuFdEnabled.IsChecked, _fdBitrate);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Channels", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _channelsText = entered;
        UpdateSummary();
    }

    // ---------- tools ----------

    private void ApiServer_Changed(object sender, RoutedEventArgs e) => ApplyServerSetting();

    private void ApplyServerSetting()
    {
        if (_server is null) return; // fires during InitializeComponent
        try
        {
            if (MenuApiServer.IsChecked)
            {
                if (!_server.IsRunning) _server.Start(_apiPort);
            }
            else
            {
                _server.Stop();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "API server", MessageBoxButton.OK, MessageBoxImage.Error);
            MenuApiServer.IsChecked = false;
        }
        UpdateStatusBar();
    }

    private void ApiPort_Click(object sender, RoutedEventArgs e)
    {
        int? port = InputDialog.AskInt(this, "API server port", "TCP port for the local JSON API:",
                                       _apiPort, 1, 65535, "The server always binds 127.0.0.1.");
        if (port is null || port.Value == _apiPort) return;
        _apiPort = port.Value;
        if (_server.IsRunning)
        {
            _server.Stop();
            ApplyServerSetting();
        }
        UpdateStatusBar();
    }

    private void CopySnippet_Click(object sender, RoutedEventArgs e)
    {
        string channel = _txChannel ?? _adapter?.Channels.FirstOrDefault() ?? "CAN1";
        string idText = TxIdBox.Text.Trim();
        if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText[2..];
        string id = uint.TryParse(idText, System.Globalization.NumberStyles.HexNumber, null, out uint parsed)
            ? $"0x{parsed:X}" : "0x123";
        string data = TxDataBox.Text.Replace(" ", "").Replace("-", "");

        string snippet = $"""
            # CanTerminal local JSON API — add <repo>/python to sys.path or pip install it
            from canterminal_can import CanTerminalClient

            with CanTerminalClient(port={_apiPort}) as ct:
                print(ct.status())
                ct.send("{channel}", {id}, bytes.fromhex("{data}"))
                for f in ct.recent(count=20, channel="{channel}"):
                    print(f["ts"], f["idHex"], f["data"], f["type"] or "")
            """;
        try
        {
            Clipboard.SetText(snippet);
            InfoText.Text = "Python snippet copied to the clipboard.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy snippet", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    // ---------- help ----------

    private void Shortcuts_Executed(object sender, ExecutedRoutedEventArgs e) =>
        MessageBox.Show(this, """
            File
              Ctrl+D          Load DBC…
              Ctrl+S          Save trace as CSV…

            Bus
              F5              Refresh devices
              Ctrl+Shift+C    Channels…
              F9              Connect
              Shift+F9        Disconnect

            View
              Ctrl+1 / 2 / 3  Single / Split ↔ / Split ↕
              Ctrl+4          XCP command / data split
              Ctrl++ / Ctrl+- Text larger / smaller  (also Ctrl + mouse wheel)
              Ctrl+0          Reset text size
              End             Jump to newest
              F7              Pause display
              Ctrl+L          Clear all

            Transmit
              Ctrl+Enter      Send frame
              F6              Start cyclic TX
              Shift+F6        Stop cyclic TX

            Help
              Ctrl+/          This list
            """, "Keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

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

    private void About_Click(object sender, RoutedEventArgs e)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "dev";
        MessageBox.Show(this,
            $"CanTerminal {version}\nCAN monitor for Intrepid ValueCAN / neoVI.\n\n{RepoUrl}",
            "About CanTerminal", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _flushTimer.Stop();
        _statusTimer.Stop();
        StopPeriodic();
        _server.Dispose();
        try { _adapter?.Dispose(); } catch { }
    }
}
