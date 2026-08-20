using System.IO;
using System.Windows;
using CanTerminal.Core.Xcp;
using Microsoft.Win32;

namespace CanTerminal.App;

// The XCP protocol profile: per-channel sessions, the A2L loader, and the XCP-only filter.
public partial class MainWindow
{
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

        // Every session above is a fresh decoder, so re-reading the file is a single pass in
        // capture order — exactly the condition a stateful decoder needs.
        if (_log is not null) ReplayFromHub();
    }

    private void XcpSet_Click(object sender, RoutedEventArgs e)
    {
        var existing = _xcpSessions.ToDictionary(kv => kv.Key, kv => (kv.Value.RequestId, kv.Value.ResponseId),
                                                 StringComparer.OrdinalIgnoreCase);
        var result = XcpSessionDialog.Ask(this, AvailableChannels(), _xcpChannel, existing);
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

    private void XcpA2l_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Filter = "A2L files (*.a2l)|*.a2l|All files|*.*",
            Multiselect = true,
        };
        if (dlg.ShowDialog(this) != true) return;

        List<(string Path, A2lXcpReader.Result Ids)> found;
        try
        {
            found = dlg.FileNames
                .OrderBy(p => Path.GetFileName(p), StringComparer.OrdinalIgnoreCase)
                .Select(p => (Path: p, Ids: A2lXcpReader.Read(p)))
                .Where(x => x.Ids.HasPair)
                .ToList();
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Load A2L", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }

        if (found.Count == 0)
        {
            MessageBox.Show(this,
                "No CAN_ID_MASTER / CAN_ID_SLAVE pair found in an IF_DATA XCP_ON_CAN block.\n\n" +
                "The file may describe a different transport layer (Ethernet, USB, FlexRay).",
                "Load A2L", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var channels = AvailableChannels();
        var rows = new List<ChannelAssignDialog.Row>();
        for (int i = 0; i < found.Count; i++)
        {
            var (path, ids) = found[i];
            var (channel, note) = ChannelCarrying(ids, channels, fallback: i < channels.Count ? channels[i] : null);
            rows.Add(new ChannelAssignDialog.Row(path, channel,
                $"master 0x{ids.Master:X} / slave 0x{ids.Slave:X}{(note.Length == 0 ? "" : "  —  " + note)}"));
        }

        var assignments = ChannelAssignDialog.Ask(this, "Load A2L",
            "Which channel each A2L describes. The file gives the CAN ID pair but not the bus it runs on, " +
            "so where a capture is loaded the suggestion below comes from which channel those identifiers " +
            "were actually seen on.",
            rows, channels, allowShared: false);
        if (assignments is null) return;

        var byPath = found.ToDictionary(f => f.Path, f => f.Ids, StringComparer.OrdinalIgnoreCase);
        var lines = new List<string>();
        foreach (var (path, channel) in assignments)
        {
            if (channel is null) continue;
            var ids = byPath[path];

            // A pair moved to another channel must not stay on the old one.
            foreach (var stale in _xcpSessions
                .Where(kv => kv.Value.RequestId == ids.Master && kv.Value.ResponseId == ids.Slave)
                .Select(kv => kv.Key).ToList())
                _xcpSessions.Remove(stale);

            _xcpSessions[channel] = new XcpConfig(ids.Master!.Value, ids.Slave!.Value, Channel: channel);
            lines.Add($"  {Path.GetFileName(path)}  →  {channel}   master 0x{ids.Master:X} / slave 0x{ids.Slave:X}" +
                      (ids.Extended ? "  (29-bit)" : ""));
        }
        if (lines.Count == 0) return;

        RebuildXcpSessions();
        _xcpChannel = assignments.FirstOrDefault(a => a.Channel is not null).Channel ?? _xcpChannel;
        InfoText.Text = $"XCP IDs loaded from {lines.Count} A2L file(s)." +
                        (_log is null ? "" : " The whole log has been decoded again from the start.");
        MessageBox.Show(this, $"Configured:\n{string.Join("\n", lines)}",
                        "Load A2L", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    /// <summary>
    /// Which channel actually carries an A2L's CAN ID pair, from what has been captured.
    ///
    /// The A2L names the identifiers but not the bus, so on a two-port device the two files are
    /// distinguishable only by their identifiers — and those are already on screen. Reading the
    /// answer off the capture beats inferring it from the order the files were named, and the
    /// reason is shown next to the suggestion so it can be disagreed with.
    /// </summary>
    private (string? Channel, string Note) ChannelCarrying(
        A2lXcpReader.Result ids, IReadOnlyList<string> channels, string? fallback)
    {
        if (ids.Master is not uint master || ids.Slave is not uint slave || _hub.TotalFrames == 0)
            return (fallback, "");

        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var frame in _hub.Snapshot())
            if (frame.ArbId == master || frame.ArbId == slave)
                counts[frame.Channel] = counts.GetValueOrDefault(frame.Channel) + 1;

        if (counts.Count == 0) return (fallback, "not seen in the capture");

        var best = counts.OrderByDescending(kv => kv.Value).First();
        if (!channels.Contains(best.Key, StringComparer.OrdinalIgnoreCase)) return (fallback, "");
        return (best.Key, $"seen on {best.Key} ({best.Value:N0} frames)");
    }

    /// <summary>
    /// Channels that can be targeted right now: the open device's, or the open log's. Without the
    /// log arm a two-port capture read from a file could only ever be given one XCP session and
    /// one database, which is half of what its own A2L and DBC pairs describe.
    /// </summary>
    private List<string> AvailableChannels() =>
        _adapter?.Channels.ToList() ?? _log?.Channels.ToList() ?? ["CAN1"];

    private string SelectedXcpChannel() =>
        _xcpChannel ?? AvailableChannels().FirstOrDefault() ?? "CAN1";

    private void XcpOnly_Changed(object sender, RoutedEventArgs e)
    {
        if (PaneA is null) return; // fires during InitializeComponent
        // The aggregate would otherwise keep rows collected under the previous filter, which
        // stop updating and read as live traffic.
        foreach (var pane in ActivePanes) pane.ResetFixed();
        InfoText.Text = MenuXcpOnly.IsChecked
            ? "Showing XCP IDs only — other traffic is still captured, just not displayed."
            : "Showing all CAN IDs.";
        if (_log is not null) ProjectToPanes();
    }
}
