using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using CanTerminal.Core;
using CanTerminal.Core.IcsNeo;
using CanTerminal.Core.Xcp;
using Microsoft.Win32;

namespace CanTerminal.App;

public partial class MainWindow : Window
{
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

    private readonly MessageHub _hub = new();
    private readonly DbcDecoder _dbc = new();
    private readonly FrameAnnotator _annotator;
    private readonly TcpApiServer _server;
    private ICanAdapter? _adapter;

    private readonly ConcurrentQueue<CanFrame> _pending = new();

    /// <summary>One XCP session per channel; a 2-port master uses a different ID pair on each.</summary>
    private readonly Dictionary<string, XcpConfig> _xcpSessions = new(StringComparer.OrdinalIgnoreCase);

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

        PaneA.SelectionChanged += UpdateStatusBar;
        PaneB.SelectionChanged += UpdateStatusBar;

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
        ApplyServerCheck();
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
        DeviceCombo.ItemsSource = items;
        DeviceCombo.SelectedIndex = items.Count > 1 ? 1 : 0;
    }

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => RefreshDevices();

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_adapter != null)
        {
            Disconnect();
            return;
        }

        if (DeviceCombo.SelectedItem is not DeviceItem item) return;
        try
        {
            int bitrate = int.Parse(GetComboText(BitrateCombo));
            int fdBitrate = int.Parse(GetComboText(FdBitrateCombo));
            bool fd = FdCheck.IsChecked == true;
            var channels = ParseChannels(ChannelsBox.Text, bitrate, fd, fdBitrate);
            if (channels.Count == 0) channels.Add(new CanChannelConfig("CAN1", bitrate, fd, fdBitrate));

            ICanAdapter adapter = item.Ics is null ? new VirtualAdapter() : new IcsNeoAdapter(item.Ics);
            adapter.FrameReceived += _hub.Publish;
            adapter.ErrorOccurred += msg => Dispatcher.BeginInvoke(() => InfoText.Text = msg);
            adapter.Open(channels);
            _adapter = adapter;

            TxChannelCombo.ItemsSource = adapter.Channels;
            TxChannelCombo.SelectedIndex = 0;
            ConnectButton.Content = "Disconnect";
            ConnStatusText.Text = $"Connected: {adapter.Name} [" +
                string.Join(", ", channels.Select(c => $"{c.Name.ToUpperInvariant()}@{c.Bitrate}")) +
                $"]{(fd ? " FD" : "")}";
            DeviceCombo.IsEnabled = RefreshButton.IsEnabled = false;
            OnChannelsOpened(adapter.Channels);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connect failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Parses the Channels box. An entry is "NAME", "NAME@bitrate" or "NAME@bitrate:fdbitrate";
    /// whatever is omitted falls back to the toolbar values, so a plain "CAN1,CAN2" behaves as
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

        string previous = XcpChannelCombo.SelectedItem as string ?? "";
        XcpChannelCombo.ItemsSource = channels;
        int index = channels.ToList().FindIndex(c => c.Equals(previous, StringComparison.OrdinalIgnoreCase));
        XcpChannelCombo.SelectedIndex = index >= 0 ? index : (channels.Count > 0 ? 0 : -1);
    }

    private void Disconnect()
    {
        StopPeriodic();
        try { _adapter?.Dispose(); } catch { }
        _adapter = null;
        ConnectButton.Content = "Connect";
        ConnStatusText.Text = "Disconnected";
        DeviceCombo.IsEnabled = RefreshButton.IsEnabled = true;
    }

    // ---------- frame flow ----------

    private void FlushPending()
    {
        bool paused = PauseCheck.IsChecked == true;
        bool highlight = HighlightCheck.IsChecked == true;
        bool autoScroll = AutoScrollCheck.IsChecked == true;
        double now = _uiClock.Elapsed.TotalSeconds;

        // Null unless the XCP-only filter is armed *and* at least one session is configured —
        // a half-configured profile must not blank the whole view.
        var xcpFilter = IsXcpSelected && XcpOnlyCheck.IsChecked == true && _annotator.XcpSessions.Count > 0
            ? _annotator
            : null;

        // Hoisted: enumerating the panes per frame allocated an iterator for every frame.
        var panes = ActivePanes.ToArray();
        long deadline = Stopwatch.GetTimestamp() + (long)(Stopwatch.Frequency * FlushBudgetMs / 1000.0);
        int added = 0, sinceClockCheck = 0;

        while (_pending.TryDequeue(out var f))
        {
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
        foreach (var pane in panes) pane.AfterAppend(autoScroll);
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
        // and a single combined number hides exactly that.
        var parts = perChannel
            .OrderBy(kv => ChannelPalette.Index(kv.Key))
            .Select(kv => $"{kv.Key}: {kv.Value - (_lastChannelTotals.TryGetValue(kv.Key, out long p) ? p : 0)} fps")
            .ToList();
        RateText.Text = parts.Count > 0 ? string.Join("   ", parts) : $"{total - _lastTotal} fps";
        _lastChannelTotals = perChannel;
        _lastTotal = total;

        TotalText.Text = $"{total:N0} frames" +
            (_displaySkipped > 0 ? $"  (display behind: {_displaySkipped:N0} not shown)" : "");
        ServerStatusText.Text = _server.IsRunning
            ? $"API server: 127.0.0.1:{_server.Port} ({_server.ClientCount} clients)"
            : "API server: off";

        foreach (var pane in ActivePanes)
            pane.UpdateStats($"{pane.TraceCount:N0} rows" + (pane.IsScrolledBack ? " — view held" : ""));
    }

    // ---------- TX ----------

    private void SendButton_Click(object sender, RoutedEventArgs e) => DoSend(silent: false);

    private void DoSend(bool silent)
    {
        try
        {
            var a = _adapter ?? throw new InvalidOperationException("Not connected.");
            string idText = TxIdBox.Text.Trim();
            if (idText.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) idText = idText[2..];
            uint id = uint.Parse(idText, System.Globalization.NumberStyles.HexNumber);
            byte[] data = Convert.FromHexString(TxDataBox.Text.Replace(" ", "").Replace("-", ""));
            string channel = TxChannelCombo.SelectedItem as string ?? "CAN1";
            bool ext = TxExtCheck.IsChecked == true || id > 0x7FF;
            a.Send(channel, id, data, ext, TxFdCheck.IsChecked == true, TxBrsCheck.IsChecked == true, "ui");
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
        if (_periodicTimer.IsEnabled)
        {
            StopPeriodic();
            return;
        }
        if (!int.TryParse(TxCycleBox.Text, out int ms) || ms < 1)
        {
            MessageBox.Show(this, "Invalid cycle time.", "Periodic TX", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        _periodicTimer.Interval = TimeSpan.FromMilliseconds(ms);
        _periodicTimer.Start();
        PeriodicButton.Content = "Stop";
    }

    private void StopPeriodic()
    {
        _periodicTimer.Stop();
        PeriodicButton.Content = "Start";
    }

    // ---------- layout ----------

    private void Layout_Changed(object sender, SelectionChangedEventArgs e) => ApplyLayout();

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

        if (LayoutCombo.SelectedIndex == 0)
        {
            PaneSplitter.Visibility = Visibility.Collapsed;
            PaneB.Visibility = Visibility.Collapsed;
            PaneB.ClearAll();   // don't keep a hidden backlog alive
            UpdateStatusBar();
            return;
        }

        PaneSplitter.Visibility = Visibility.Visible;
        PaneB.Visibility = Visibility.Visible;

        if (LayoutCombo.SelectedIndex == 1)   // side by side
        {
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
            PaneHost.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(PaneA, 0);
            Grid.SetColumn(PaneSplitter, 1);
            Grid.SetColumn(PaneB, 2);
            PaneSplitter.ResizeDirection = GridResizeDirection.Columns;
            PaneSplitter.Cursor = System.Windows.Input.Cursors.SizeWE;
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
            PaneSplitter.Cursor = System.Windows.Input.Cursors.SizeNS;
        }
        UpdateStatusBar();
    }

    // ---------- protocol profile (XCP) ----------

    private bool IsXcpSelected => ProfileCombo.SelectedIndex == 1;

    private void Profile_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (XcpPanel is null) return; // fires during InitializeComponent
        XcpPanel.Visibility = IsXcpSelected ? Visibility.Visible : Visibility.Collapsed;
        if (!IsXcpSelected) _xcpSessions.Clear();
        RebuildXcpSessions();
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
            ? IsXcpSelected ? "no session configured yet" : ""
            : string.Join("    ", _xcpSessions
                .OrderBy(kv => ChannelPalette.Index(kv.Key))
                .Select(kv => $"{kv.Key} 0x{kv.Value.RequestId:X}/0x{kv.Value.ResponseId:X}"));
    }

    private void XcpChannel_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (XcpReqBox is null) return; // fires during InitializeComponent
        // Show what is currently configured for the newly selected channel.
        if (XcpChannelCombo.SelectedItem is string channel && _xcpSessions.TryGetValue(channel, out var cfg))
        {
            XcpReqBox.Text = cfg.RequestId.ToString("X");
            XcpRspBox.Text = cfg.ResponseId.ToString("X");
        }
    }

    private void XcpApply_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            string channel = SelectedXcpChannel();
            uint req = ParseHexId(XcpReqBox.Text);
            uint rsp = ParseHexId(XcpRspBox.Text);
            if (req == rsp) throw new InvalidOperationException("Request and response IDs must differ.");
            _xcpSessions[channel] = new XcpConfig(req, rsp, Channel: channel);
            RebuildXcpSessions();
            InfoText.Text = $"XCP on {channel}: req 0x{req:X} / rsp 0x{rsp:X} — frames from here on are decoded.";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "XCP profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void XcpRemove_Click(object sender, RoutedEventArgs e)
    {
        string channel = SelectedXcpChannel();
        if (!_xcpSessions.Remove(channel))
        {
            InfoText.Text = $"No XCP session configured on {channel}.";
            return;
        }
        RebuildXcpSessions();
        InfoText.Text = $"Removed the XCP session on {channel}.";
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
        SelectXcpChannel(best[0].Channel);

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
            SelectXcpChannel(targets[0]);

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

    private List<string> AvailableXcpChannels() =>
        _adapter?.Channels.ToList() ?? (XcpChannelCombo.ItemsSource as IEnumerable<string>)?.ToList() ?? ["CAN1"];

    private string SelectedXcpChannel() =>
        XcpChannelCombo.SelectedItem as string ?? AvailableXcpChannels().FirstOrDefault() ?? "CAN1";

    private void SelectXcpChannel(string channel)
    {
        if (XcpChannelCombo.ItemsSource is not IEnumerable<string> items) return;
        int index = items.ToList().FindIndex(c => c.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) XcpChannelCombo.SelectedIndex = index;
    }

    private static uint ParseHexId(string text)
    {
        string s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out uint id))
            throw new InvalidOperationException($"'{text}' is not a hex CAN ID.");
        return id;
    }

    // ---------- view / DBC / server / misc ----------

    private void XcpOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (PaneA is null) return; // fires during InitializeComponent
        // The aggregate would otherwise keep rows collected under the previous filter, which
        // stop updating and read as live traffic.
        foreach (var pane in ActivePanes) pane.ResetFixed();
        InfoText.Text = XcpOnlyCheck.IsChecked == true
            ? "Showing XCP IDs only — other traffic is still captured, just not displayed."
            : "Showing all CAN IDs.";
    }

    private void History_Changed(object sender, RoutedEventArgs e)
    {
        if (PaneA is null) return; // fires during InitializeComponent
        if (!int.TryParse(HistoryBox.Text, out int capacity) || capacity < TraceBuffer.MinCapacity)
        {
            HistoryBox.Text = TraceBuffer.DefaultCapacity.ToString();
            capacity = TraceBuffer.DefaultCapacity;
            InfoText.Text = $"History must be at least {TraceBuffer.MinCapacity} rows — reset to {capacity:N0}.";
        }
        PaneA.SetHistoryCapacity(capacity);
        PaneB.SetHistoryCapacity(capacity);
    }

    private void HighlightCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Leaving stale highlights frozen on screen would misreport them as recent changes.
        foreach (var pane in ActivePanes) pane.ClearHighlights();
    }

    private void DbcButton_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DBC files (*.dbc)|*.dbc|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            _dbc.Load(dlg.FileName);
            DbcLabel.Text = Path.GetFileName(dlg.FileName);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DBC load failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void ServerCheck_Changed(object sender, RoutedEventArgs e) => ApplyServerCheck();

    private void ApplyServerCheck()
    {
        if (_server is null) return; // fires during InitializeComponent
        try
        {
            if (ServerCheck.IsChecked == true)
            {
                if (!_server.IsRunning)
                    _server.Start(int.TryParse(PortBox.Text, out int p) ? p : 29536);
            }
            else
            {
                _server.Stop();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "API server", MessageBoxButton.OK, MessageBoxImage.Error);
            ServerCheck.IsChecked = false;
        }
        UpdateStatusBar();
    }

    private void ClearButton_Click(object sender, RoutedEventArgs e)
    {
        PaneA.ClearAll();
        PaneB.ClearAll();
        _hub.Clear();
        _displaySkipped = 0;
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
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
            var sb = new StringBuilder("Time;Chan;Dir;ID;Flags;DLC;Data;FrameType;Comments\r\n");
            foreach (var f in frames)
            {
                var r = TraceRow.From(f);
                sb.Append(r.Time).Append(';').Append(r.Chan).Append(';').Append(r.Dir).Append(';')
                  .Append(r.Id).Append(';').Append(r.Flags).Append(';').Append(r.Dlc).Append(';')
                  .Append(r.Data).Append(';').Append(r.Type?.Replace(';', ',')).Append(';')
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

    private static string GetComboText(ComboBox combo) =>
        (combo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? combo.Text;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _flushTimer.Stop();
        _statusTimer.Stop();
        StopPeriodic();
        _server.Dispose();
        try { _adapter?.Dispose(); } catch { }
    }
}
