using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CanTerminal.Core;
using CanTerminal.Core.IcsNeo;
using CanTerminal.Core.Xcp;
using Microsoft.Win32;

namespace CanTerminal.App;

public partial class MainWindow : Window
{
    private const int TraceCap = 100_000;
    private const int TraceTrimTo = 60_000;

    private readonly MessageHub _hub = new();
    private readonly DbcDecoder _dbc = new();
    private readonly FrameAnnotator _annotator;
    private readonly TcpApiServer _server;
    private ICanAdapter? _adapter;

    private readonly ConcurrentQueue<CanFrame> _pending = new();
    private ObservableCollection<TraceRow> _traceRows = [];
    private readonly ObservableCollection<FixedRow> _fixedRows = [];
    // Group is part of the key so XCP splits one CAN ID into per-ODT rows; it is 0 otherwise.
    private readonly Dictionary<(string Chan, uint Id, bool Ext, int Group), FixedRow> _fixedMap = [];

    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _periodicTimer;
    private long _lastTotal;

    /// <summary>
    /// Drives the change-highlight decay. Deliberately independent of frame timestamps: the
    /// highlight has to keep fading in wall time even when the bus goes quiet.
    /// </summary>
    private readonly System.Diagnostics.Stopwatch _uiClock = System.Diagnostics.Stopwatch.StartNew();

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

        TraceList.ItemsSource = _traceRows;
        FixedGrid.ItemsSource = _fixedRows;

        _flushTimer = new DispatcherTimer(DispatcherPriority.Background) { Interval = TimeSpan.FromMilliseconds(50) };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();

        _statusTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _statusTimer.Tick += (_, _) => UpdateStatusBar();
        _statusTimer.Start();

        _periodicTimer = new DispatcherTimer();
        _periodicTimer.Tick += (_, _) => DoSend(silent: true);

        RefreshDevices();
        ApplyServerCheck();
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
            var channels = ChannelsBox.Text
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(name => new CanChannelConfig(name, bitrate, fd, fdBitrate))
                .ToList();
            if (channels.Count == 0) channels.Add(new CanChannelConfig("CAN1", bitrate, fd, fdBitrate));

            ICanAdapter adapter = item.Ics is null ? new VirtualAdapter() : new IcsNeoAdapter(item.Ics);
            adapter.FrameReceived += _hub.Publish;
            adapter.ErrorOccurred += msg => Dispatcher.BeginInvoke(() => InfoText.Text = msg);
            adapter.Open(channels);
            _adapter = adapter;

            TxChannelCombo.ItemsSource = adapter.Channels;
            TxChannelCombo.SelectedIndex = 0;
            ConnectButton.Content = "Disconnect";
            ConnStatusText.Text = $"Connected: {adapter.Name} [{string.Join(",", adapter.Channels)}] @{bitrate}bps{(fd ? " FD" : "")}";
            DeviceCombo.IsEnabled = RefreshButton.IsEnabled = false;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Connect failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        double now = _uiClock.Elapsed.TotalSeconds;
        // Null unless the XCP-only filter is armed *and* a decoder is actually configured —
        // a half-configured profile must not blank the whole view.
        var xcpFilter = IsXcpSelected && XcpOnlyCheck.IsChecked == true ? _annotator.Xcp : null;
        int dequeued = 0, added = 0;

        // The dequeue budget counts every frame taken off the queue, filtered or not, so a
        // burst of uninteresting traffic still drains instead of backing up.
        while (dequeued < 30_000 && _pending.TryDequeue(out var f))
        {
            dequeued++;
            if (paused) continue;
            if (xcpFilter is not null && !xcpFilter.Matches(f)) continue;
            _traceRows.Add(TraceRow.From(f));
            UpdateFixed(f, now, highlight);
            added++;
        }

        // Decay runs on every tick, not only when frames arrived, so highlights still fade out
        // on an idle bus. Rows that have finished fading skip out immediately.
        if (highlight && FixedGrid.IsVisible)
            foreach (var row in _fixedRows) row.TickFade(now);
        if (added == 0) return;

        if (_traceRows.Count > TraceCap)
        {
            _traceRows = new ObservableCollection<TraceRow>(_traceRows.Skip(_traceRows.Count - TraceTrimTo));
            TraceList.ItemsSource = _traceRows;
        }

        if (AutoScrollCheck.IsChecked == true && TraceList.IsVisible && _traceRows.Count > 0)
            TraceList.ScrollIntoView(_traceRows[^1]);
    }

    private void UpdateFixed(CanFrame f, double now, bool highlight)
    {
        var key = (f.Channel, f.ArbId, f.IsExtended, f.Annotation?.GroupKey ?? 0);
        if (_fixedMap.TryGetValue(key, out var row))
        {
            row.Update(f, now, highlight);
        }
        else
        {
            row = new FixedRow(f);
            _fixedMap[key] = row;
            _fixedRows.Add(row);
        }
    }

    private void UpdateStatusBar()
    {
        long total = _hub.TotalFrames;
        RateText.Text = $"{total - _lastTotal} fps";
        _lastTotal = total;
        TotalText.Text = $"{total:N0} frames";
        ServerStatusText.Text = _server.IsRunning
            ? $"API server: 127.0.0.1:{_server.Port} ({_server.ClientCount} clients)"
            : "API server: off";
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

    // ---------- protocol profile (XCP) ----------

    private bool IsXcpSelected => ProfileCombo.SelectedIndex == 1;

    private void Profile_Changed(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (XcpPanel is null) return; // fires during InitializeComponent
        XcpPanel.Visibility = IsXcpSelected ? Visibility.Visible : Visibility.Collapsed;
        if (IsXcpSelected) ApplyXcpProfile(announce: false);
        else
        {
            _annotator.Xcp = null;
            ResetFixedView();
            XcpStatusText.Text = "";
        }
    }

    /// <summary>
    /// Drops the aggregate rows. Required whenever the grouping basis changes: rows built under
    /// the previous basis stop updating and would sit there misreporting themselves as live.
    /// The trace view and the hub's ring buffer are untouched.
    /// </summary>
    private void ResetFixedView()
    {
        _fixedRows.Clear();
        _fixedMap.Clear();
    }

    private void XcpApply_Click(object sender, RoutedEventArgs e) => ApplyXcpProfile(announce: true);

    private void ApplyXcpProfile(bool announce)
    {
        try
        {
            uint req = ParseHexId(XcpReqBox.Text);
            uint rsp = ParseHexId(XcpRspBox.Text);
            if (req == rsp) throw new InvalidOperationException("Request and response IDs must differ.");
            _annotator.Xcp = new XcpDecoder(new XcpConfig(req, rsp));
            ResetFixedView();
            XcpStatusText.Text = $"XCP: req 0x{req:X} / rsp 0x{rsp:X} — Fixed view splits DTOs per ODT";
            if (announce) InfoText.Text = "XCP profile applied — frames from here on are decoded.";
        }
        catch (Exception ex)
        {
            _annotator.Xcp = null;
            ResetFixedView();
            XcpStatusText.Text = "XCP: not configured";
            MessageBox.Show(this, ex.Message, "XCP profile", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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

        var best = candidates[0];
        XcpReqBox.Text = best.RequestId.ToString("X");
        XcpRspBox.Text = best.ResponseId.ToString("X");
        ApplyXcpProfile(announce: false);

        var others = candidates.Skip(1).Take(4).Select(c => $"  {c}").ToList();
        InfoText.Text = $"XCP detected: {best}" + (others.Count > 0 ? $" (+{candidates.Count - 1} other candidate(s))" : "");
        if (others.Count > 0)
            MessageBox.Show(this,
                $"Applied the strongest candidate:\n  {best}\n\nOther candidates:\n{string.Join("\n", others)}",
                "XCP detect", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private void XcpA2l_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "A2L files (*.a2l)|*.a2l|All files|*.*" };
        if (dlg.ShowDialog(this) != true) return;
        try
        {
            var result = A2lXcpReader.Read(dlg.FileName);
            if (!result.HasPair)
            {
                MessageBox.Show(this,
                    "No CAN_ID_MASTER / CAN_ID_SLAVE pair found in an IF_DATA XCP_ON_CAN block.\n\n" +
                    "The file may describe a different transport layer (Ethernet, USB, FlexRay).",
                    "Load A2L", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            XcpReqBox.Text = result.Master!.Value.ToString("X");
            XcpRspBox.Text = result.Slave!.Value.ToString("X");
            ApplyXcpProfile(announce: false);
            InfoText.Text = $"XCP IDs from {Path.GetFileName(dlg.FileName)}: " +
                            $"master 0x{result.Master:X}, slave 0x{result.Slave:X}" +
                            (result.Extended ? " (29-bit)" : "");
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load A2L", MessageBoxButton.OK, MessageBoxImage.Error);
        }
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
        if (_fixedRows is null) return; // fires during InitializeComponent
        // The aggregate would otherwise keep rows collected under the previous filter, which
        // stop updating and read as live traffic.
        ResetFixedView();
        InfoText.Text = XcpOnlyCheck.IsChecked == true
            ? "Showing XCP IDs only — other traffic is still captured, just not displayed."
            : "Showing all CAN IDs.";
    }

    private void HighlightCheck_Changed(object sender, RoutedEventArgs e)
    {
        // Leaving stale highlights frozen on screen would misreport them as recent changes.
        foreach (var row in _fixedRows) row.ClearHighlight();
    }

    private void ViewMode_Changed(object sender, RoutedEventArgs e)
    {
        if (TraceList is null || FixedGrid is null) return;
        bool trace = TraceRadio.IsChecked == true;
        TraceList.Visibility = trace ? Visibility.Visible : Visibility.Collapsed;
        FixedGrid.Visibility = trace ? Visibility.Collapsed : Visibility.Visible;
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
        _traceRows.Clear();
        _fixedRows.Clear();
        _fixedMap.Clear();
        _hub.Clear();
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
            var sb = new StringBuilder("Time;Chan;Dir;ID;Flags;DLC;Data;FrameType;Comments\r\n");
            foreach (var r in _traceRows)
                sb.Append(r.Time).Append(';').Append(r.Chan).Append(';').Append(r.Dir).Append(';')
                  .Append(r.Id).Append(';').Append(r.Flags).Append(';').Append(r.Dlc).Append(';')
                  .Append(r.Data).Append(';').Append(r.Type?.Replace(';', ',')).Append(';')
                  .Append(r.Decoded?.Replace(';', ',')).Append("\r\n");
            File.WriteAllText(dlg.FileName, sb.ToString(), Encoding.UTF8);
            InfoText.Text = $"Saved {_traceRows.Count:N0} rows to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string GetComboText(System.Windows.Controls.ComboBox combo) =>
        (combo.SelectedItem as System.Windows.Controls.ComboBoxItem)?.Content?.ToString() ?? combo.Text;

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        _flushTimer.Stop();
        _statusTimer.Stop();
        StopPeriodic();
        _server.Dispose();
        try { _adapter?.Dispose(); } catch { }
    }
}
