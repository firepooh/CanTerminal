using System.Windows;
using System.Windows.Input;
using CanTerminal.Core;
using CanTerminal.Core.IcsNeo;
using CanTerminal.Core.Slcan;

namespace CanTerminal.App;

// The half of the window that talks to hardware: device scan and selection, connect and
// disconnect, and the transmit panel. Split along the section seams of what was one
// 2,000-line file; nothing here changed in the move.
public partial class MainWindow
{
    // ---------- devices / connection ----------

    /// <summary>
    /// One scan result. Exactly one of <paramref name="Ics"/> / <paramref name="SlcanPort"/> is
    /// set for real hardware; both null is the virtual bus.
    /// </summary>
    private sealed record DeviceItem(string Label, IcsDeviceInfo? Ics, string? SlcanPort = null)
    {
        public override string ToString() => Label;
    }

    /// <summary>
    /// Which device a scan should leave selected. Split out from <see cref="RefreshDevices"/>
    /// so the rule can be tested without a window: an explicit earlier pick wins, then the
    /// first real device, then nothing. The virtual bus is only ever reached by the first arm,
    /// i.e. because the user asked for it by name.
    /// </summary>
    internal static T? PreferredDevice<T>(IReadOnlyList<T> devices, string? previousLabel, Func<T, string> label, Func<T, bool> isRealHardware)
        where T : class
        => devices.FirstOrDefault(d => label(d) == previousLabel) ?? devices.FirstOrDefault(isRealHardware);

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

        // Serial SLCAN interfaces (WeAct USB2CANFDV2 and other CANable-2 derivatives). The USB
        // identity is generic ST CDC, so this is a candidate list — Connect verifies the device
        // actually speaks SLCAN before configuring it.
        foreach (var port in SlcanAdapter.FindPorts())
            items.Add(new DeviceItem($"USB2CAN SLCAN ({port})", null, port));

        _devices = items;

        // An explicit pick survives a rescan — including the virtual bus, so choosing it once
        // does not have to be repeated on every F5.
        //
        // Otherwise the first real device, and nothing at all when there is none. The virtual
        // bus is never selected on the program's own initiative: this is a monitor, and a
        // monitor that quietly hands you invented traffic when the hardware is missing is
        // answering a question nobody asked. Connect says so instead.
        _device = PreferredDevice(items, _device?.Label, d => d.Label,
                                  d => d.Ics is not null || d.SlcanPort is not null);
        UpdateConnStatus();
    }

    private void UpdateConnStatus()
    {
        if (_adapter is null)
            ConnStatusText.Text = _device is null ? "Disconnected — no device" : $"Disconnected — {_device.Label}";
    }

    private void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        if (_log is not null) CloseLog();
        else if (_adapter != null) Disconnect();
        else Connect();
    }

    private void Connect()
    {
        if (_adapter != null) return;
        if (_device is null)
        {
            // Reachable only when the scan found no hardware, since that is the one case
            // RefreshDevices leaves unselected. Saying nothing here would be the old behaviour
            // wearing a different mask.
            MessageBox.Show(this,
                "No CAN interface found.\n\n" +
                "Plug in a ValueCAN / neoVI, or a USB2CAN SLCAN device, and press F5 " +
                "(Bus ▸ Refresh devices).\n\n" +
                "To work without hardware, pick Bus ▸ Device ▸ Virtual bus (no hardware) — " +
                "it generates traffic of its own, so what you see will not have come from a bus.",
                "Connect", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        try
        {
            bool fd = MenuFdEnabled.IsChecked;
            var channels = ParseChannels(_channelsText, _bitrate, fd, _fdBitrate);
            if (channels.Count == 0) channels.Add(new CanChannelConfig("CAN1", _bitrate, fd, _fdBitrate));

            ICanAdapter adapter = _device.Ics is { } ics ? new IcsNeoAdapter(ics)
                : _device.SlcanPort is { } slcanPort ? new SlcanAdapter(slcanPort)
                : new VirtualAdapter();

            // A single-channel device connects with the first entry of a multi-channel setting
            // rather than failing on it. The alternative — telling the user to narrow
            // Bus ▸ Channels… — persists that narrowing globally, and the next two-port
            // ValueCAN session then opens one port and silently loses the other bus.
            string? channelNote = null;
            if (adapter is SlcanAdapter && channels.Count > 1)
            {
                channelNote = $"{_device.Label} has a single CAN channel — opened " +
                              $"{channels[0].Name.ToUpperInvariant()} only. The Channels setting is unchanged.";
                channels = [channels[0]];
            }

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
            if (channelNote is not null) InfoText.Text = channelNote;
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
    /// <summary>
    /// Offers a set of channels to everything that can be pointed at one. Called both when a
    /// device opens and when a log does — a file names its channels just as a device does, and
    /// without this the panes offer only "All" and a two-channel capture cannot be split.
    /// </summary>
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
        // The time anchor belongs to a capture, not to the program. The next device may run on
        // a different clock entirely: ValueCAN timestamps come from the device's continuously
        // running hardware clock, which hid this — the SLCAN adapter's stopwatch restarts at
        // zero on every open, and against a stale anchor its whole session rendered thousands
        // of seconds in the past. Queued frames of the old era go too, so the first frame the
        // new anchor is measured from is actually the new capture's.
        _haveZero = false;
        while (_pending.TryDequeue(out _)) { }
        _txChannel = null;
        TxChannelText.Text = "—";       // no channel is open, so naming one would be a lie
        ConnectButton.Content = "Connect";
        UpdateConnStatus();
        UpdateMenuState();
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

    // ---------- bus commands ----------

    // A log holds the ring, so starting a live capture on top of one would interleave two clocks.
    private void CanEditBusSettings(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter is null && _log is null;
    private void CanConnect(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter is null && _log is null;

    /// <summary>
    /// Clear is refused over a file: it is reflexive enough to throw away a load that took
    /// seconds, with no way back. Leaving the log is what Close log is for.
    /// </summary>
    private void CanLiveOnly(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _log is null;

    /// <summary>Pause means the display while live and the replay while a file is open.</summary>
    private void CanTogglePause(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = true;

    private void CanGoToTime(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _log is not null;
    private void CanDisconnect(object sender, CanExecuteRoutedEventArgs e) => e.CanExecute = _adapter != null;

    private void RefreshDevices_Executed(object sender, ExecutedRoutedEventArgs e) => RefreshDevices();
    private void Connect_Executed(object sender, ExecutedRoutedEventArgs e) => Connect();
    private void Disconnect_Executed(object sender, ExecutedRoutedEventArgs e) => Disconnect();

    private void EditChannels_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        string? entered = InputDialog.Ask(this, "Channels", "Channels to open:", _channelsText,
            "Comma separated: CAN1,CAN2,CAN3,CAN4,MSCAN,SWCAN\n" +
            "Per-channel speed: NAME@bitrate[:fdbitrate], e.g. CAN1@500000,CAN2@125000\n" +
            "Without @ the Bus ▸ Bitrate value is used.\n" +
            "A USB2CAN SLCAN device has a single channel — only the first entry is used.");
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
}
