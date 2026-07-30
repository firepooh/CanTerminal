using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using CanTerminal.Core;
using CanTerminal.Core.IcsNeo;
using Microsoft.Win32;

namespace CanTerminal.App;

public partial class MainWindow : Window
{
    private const int TraceCap = 100_000;
    private const int TraceTrimTo = 60_000;

    private readonly MessageHub _hub = new();
    private readonly DbcDecoder _dbc = new();
    private readonly TcpApiServer _server;
    private ICanAdapter? _adapter;

    private readonly ConcurrentQueue<CanFrame> _pending = new();
    private ObservableCollection<TraceRow> _traceRows = [];
    private readonly ObservableCollection<FixedRow> _fixedRows = [];
    private readonly Dictionary<(string Chan, uint Id, bool Ext), FixedRow> _fixedMap = [];

    private readonly DispatcherTimer _flushTimer;
    private readonly DispatcherTimer _statusTimer;
    private readonly DispatcherTimer _periodicTimer;
    private long _lastTotal;

    public MainWindow()
    {
        InitializeComponent();

        _server = new TcpApiServer(_hub, _dbc)
        {
            OnSend = (channel, id, data, ext, fd, brs, source) =>
            {
                var a = _adapter;
                if (a?.IsOpen != true) throw new InvalidOperationException("No device connected in CanTerminal.");
                a.Send(channel, id, data, ext, fd, brs, source);
            },
            StatusProvider = () => new ApiStatus(_adapter?.IsOpen == true, _adapter?.Name, _adapter?.Channels ?? [], _dbc.FilePath),
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
        bool haveDbc = _dbc.IsLoaded;
        int appended = 0;

        while (appended < 30_000 && _pending.TryDequeue(out var f))
        {
            appended++;
            string? decoded = haveDbc ? SafeDecode(f) : null;

            if (!paused)
            {
                _traceRows.Add(TraceRow.From(f, decoded));
                UpdateFixed(f, decoded);
            }
        }
        if (appended == 0 || paused) return;

        if (_traceRows.Count > TraceCap)
        {
            _traceRows = new ObservableCollection<TraceRow>(_traceRows.Skip(_traceRows.Count - TraceTrimTo));
            TraceList.ItemsSource = _traceRows;
        }

        if (AutoScrollCheck.IsChecked == true && TraceList.IsVisible && _traceRows.Count > 0)
            TraceList.ScrollIntoView(_traceRows[^1]);
    }

    private string? SafeDecode(CanFrame f)
    {
        try { return _dbc.Decode(f); }
        catch { return null; }
    }

    private void UpdateFixed(CanFrame f, string? decoded)
    {
        var key = (f.Channel, f.ArbId, f.IsExtended);
        if (_fixedMap.TryGetValue(key, out var row))
        {
            row.Update(f, decoded);
        }
        else
        {
            row = new FixedRow(f, decoded);
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

    // ---------- view / DBC / server / misc ----------

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
            var sb = new StringBuilder("Time;Chan;Dir;ID;Flags;DLC;Data;Decoded\r\n");
            foreach (var r in _traceRows)
                sb.Append(r.Time).Append(';').Append(r.Chan).Append(';').Append(r.Dir).Append(';')
                  .Append(r.Id).Append(';').Append(r.Flags).Append(';').Append(r.Dlc).Append(';')
                  .Append(r.Data).Append(';').Append(r.Decoded?.Replace(';', ',')).Append("\r\n");
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
