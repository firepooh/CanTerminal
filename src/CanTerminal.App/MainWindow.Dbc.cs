using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Input;
using CanTerminal.Core;
using Microsoft.Win32;

namespace CanTerminal.App;

// The File menu's data half — DBC binding and the CSV export — plus the Tools menu
// (API server, python snippet).
public partial class MainWindow
{
    // ---------- file / DBC ----------

    private void LoadDbc_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = "DBC files (*.dbc)|*.dbc|All files|*.*", Multiselect = true };
        if (dlg.ShowDialog(this) != true) return;

        var channels = AvailableChannels();

        // With one channel there is nothing to choose: "all channels" and that channel are the
        // same database. Asking anyway would be a dialog whose every answer is identical.
        if (channels.Count < 2 && dlg.FileNames.Length == 1)
        {
            LoadDbc(dlg.FileNames[0]);
            return;
        }

        // Filename order is offered as the starting point, because a matched pair is usually
        // named for its ports — but it is a suggestion in a dialog, not a rule applied silently.
        var byName = dlg.FileNames.OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase).ToList();
        var suggested = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < byName.Count; i++)
            suggested[byName[i]] = byName.Count == 1 ? CurrentChannelOf(byName[i])
                                 : i < channels.Count ? channels[i]
                                 : null;

        var assignments = ChannelAssignDialog.Ask(this, "Load DBC",
            "Which channel each database describes. Bound to the wrong one it either decodes nothing, " +
            "or — where the two buses share an identifier — decodes it as the wrong message. Neither is announced.",
            [.. byName.Select(f => new ChannelAssignDialog.Row(f, suggested.GetValueOrDefault(f)))],
            channels);
        if (assignments is null) return;
        ApplyDbcAssignments(assignments);
    }

    /// <summary>The channel a database is already bound to, so reloading it keeps its place.</summary>
    private string? CurrentChannelOf(string path) =>
        _channelDbc.FirstOrDefault(kv =>
            string.Equals(kv.Value.FilePath, path, StringComparison.OrdinalIgnoreCase)).Key;

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
        AfterDbcChanged();
    }

    /// <summary>
    /// Applies what the assignment dialog agreed: a null channel is the shared database, anything
    /// else replaces whatever that channel had.
    /// </summary>
    private void ApplyDbcAssignments(IReadOnlyList<(string Path, string? Channel)> assignments)
    {
        var lines = new List<string>();
        try
        {
            foreach (var (path, channel) in assignments)
            {
                var decoder = new DbcDecoder();
                decoder.Load(path);
                _settings.PushRecentDbc(path);

                if (channel is null)
                {
                    _dbc.Load(path);
                    lines.Add($"  {Path.GetFileName(path)}  →  all channels");
                }
                else
                {
                    // A file moved from one channel to another must not stay on the old one.
                    foreach (var stale in _channelDbc
                        .Where(kv => string.Equals(kv.Value.FilePath, path, StringComparison.OrdinalIgnoreCase))
                        .Select(kv => kv.Key).ToList())
                        _channelDbc.Remove(stale);

                    _channelDbc[channel] = decoder;
                    lines.Add($"  {Path.GetFileName(path)}  →  {channel}");
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "DBC load failed", MessageBoxButton.OK, MessageBoxImage.Error);
            return;
        }
        _annotator.ChannelDbc = _channelDbc;

        InfoText.Text = lines.Count == 1
            ? $"DBC bound: {lines[0].Trim()}"
            : $"{lines.Count} DBC file(s) bound: {string.Join(",  ", lines.Select(l => l.Trim()))}";
        AfterDbcChanged();
    }

    private void UnloadDbc_Click(object sender, RoutedEventArgs e)
    {
        _dbc.Unload();
        _channelDbc.Clear();
        _annotator.ChannelDbc = _channelDbc;
        InfoText.Text = _log is null
            ? "DBC unloaded — frames captured from here on carry no signal comments."
            : "DBC unloaded — the log has been read again without it.";
        AfterDbcChanged();
    }

    /// <summary>
    /// Drops the database bound to one channel, leaving the others alone. Filled on open, like the
    /// other per-channel submenus.
    /// </summary>
    private void UnloadDbcChannelMenu_Opened(object sender, RoutedEventArgs e)
    {
        var bound = _channelDbc.Keys.OrderBy(ChannelPalette.Index).ToList();
        FillRadioMenu(MenuUnloadDbcChannel, bound,
                      c => $"{c}  {Path.GetFileName(_channelDbc[c].FilePath)}",
                      _ => false,
                      c =>
                      {
                          _channelDbc.Remove(c);
                          _annotator.ChannelDbc = _channelDbc;
                          InfoText.Text = $"DBC on {c} unloaded.";
                          AfterDbcChanged();
                      });
    }

    /// <summary>
    /// A loaded log is decoded again from its first frame, so a database applies to the whole file
    /// rather than only to what arrives next. A live capture keeps the documented behaviour:
    /// frames are annotated once, as they arrive.
    /// </summary>
    private void AfterDbcChanged()
    {
        UpdateMenuState();
        UpdateSummary();
        if (_log is not null) ReplayFromHub();
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
            // Exported from the capture buffer — or the open log — rather than from a pane: with
            // two panes and per-pane filters "what the screen shows" is ambiguous, and the whole
            // capture is what you actually want in a file.
            var frames = _hub.Snapshot();
            // The Time column follows the mode on screen; delta runs over the exported sequence,
            // which is the whole capture rather than one pane's slice of it.
            var time = new TimeBase(_timestampMode, _zeroTs, _zeroWall);
            double previousTs = double.NaN;
            // Streamed rather than built as one string: a full ring is 200,000 rows, and
            // StringBuilder plus ToString plus the UTF-8 encode meant three copies of a ~25 MB
            // file on the large object heap before a byte reached the disk.
            // Encoding.UTF8 rather than a bare UTF8Encoding: it emits the BOM, which is what
            // makes Excel read the file as UTF-8 instead of the local code page. The previous
            // File.WriteAllText call wrote one, and unit strings out of a DBC depend on it.
            using (var writer = new StreamWriter(dlg.FileName, false, Encoding.UTF8))
            {
                writer.NewLine = "\r\n";
                writer.WriteLine($"{time.ColumnHeader};Chan;Dir;ID;Flags;DLC;Data;Sender;FrameType;Comments");
                foreach (var f in frames)
                {
                    var r = TraceRow.From(f, time, previousTs);
                    previousTs = f.Timestamp;
                    writer.Write(r.Time); writer.Write(';');
                    writer.Write(r.Chan); writer.Write(';');
                    writer.Write(r.Dir); writer.Write(';');
                    writer.Write(r.Id); writer.Write(';');
                    writer.Write(r.Flags); writer.Write(';');
                    writer.Write(r.Dlc); writer.Write(';');
                    writer.Write(r.Data); writer.Write(';');
                    writer.Write(r.Sender); writer.Write(';');
                    writer.Write(Csv(r.Type)); writer.Write(';');
                    writer.WriteLine(Csv(r.Decoded));
                }
            }
            InfoText.Text = $"Saved {frames.Length:N0} {(_log is null ? "captured" : "log")} frames to {dlg.FileName}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Save failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    /// <summary>
    /// Makes a decoded comment safe to put in a semicolon-separated field. The separator is
    /// swapped for a comma as before; newlines are folded because a comment carrying one would
    /// otherwise split a row in two and silently shift every column after it.
    /// </summary>
    private static string? Csv(string? field) =>
        field?.Replace(';', ',').Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ');

    private void Exit_Click(object sender, RoutedEventArgs e) => Close();

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

    // ---------- MCP endpoint ----------

    private void McpServer_Changed(object sender, RoutedEventArgs e) => ApplyMcpSetting();

    /// <summary>
    /// Opens or closes the MCP endpoint to match the menu.
    ///
    /// A port already taken is the ordinary case of a second copy of the program running, not a
    /// fault: that copy simply has no endpoint, and says so rather than fighting for the port —
    /// the registered URL has to keep meaning one program.
    /// </summary>
    private void ApplyMcpSetting()
    {
        // Checked="True" in the XAML raises this from inside InitializeComponent, before the
        // constructor has built anything — the same reason ApplyServerSetting guards on _server.
        // Testing the menu item is not enough: that one exists by then, and it was _api that was
        // still null. The constructor calls this again once there is something to start.
        if (_api is null || MenuMcpServer is null) return;

        if (!MenuMcpServer.IsChecked)
        {
            _mcp?.Stop();
            _mcp = null;
            UpdateStatusBar();
            return;
        }

        _mcp ??= new Core.Mcp.McpHttpServer(_api, _mcpPort);
        if (_mcp.Start())
        {
            InfoText.Text = $"MCP endpoint on {_mcp.Endpoint} — register it with:  {_mcp.RegistrationCommand}";
        }
        else
        {
            InfoText.Text = _mcp.State == Core.Mcp.McpHttpState.PortInUse
                ? $"MCP endpoint not started: port {_mcpPort} is already in use — another CanTerminal is probably serving it."
                : $"MCP endpoint failed to start: {_mcp.FailureDetail}";
            _mcp = null;
            MenuMcpServer.IsChecked = false;
        }
        UpdateStatusBar();
    }

    private void McpPort_Click(object sender, RoutedEventArgs e)
    {
        int? port = InputDialog.AskInt(this, "MCP server port", "HTTP port for the MCP endpoint:",
                                       _mcpPort, 1, 65535,
                                       "The endpoint always binds 127.0.0.1. Changing it means re-registering " +
                                       "the URL with Claude.");
        if (port is null || port.Value == _mcpPort) return;
        _mcpPort = port.Value;
        if (_mcp is not null)
        {
            _mcp.Stop();
            _mcp = null;
            ApplyMcpSetting();                 // rebuilt on the new port
        }
        UpdateStatusBar();
    }

    /// <summary>
    /// Where to register, and what has to be true for it to work. Opened from the menu, and worth
    /// having because the one thing that decides whether this works — registration scope — is a
    /// Claude Code concept with no presence anywhere in this program.
    /// </summary>
    private void McpGuide_Click(object sender, RoutedEventArgs e) =>
        new McpGuideDialog(this, _mcp?.Endpoint, _mcpPort).ShowDialog();

    private void CopyMcpCommand_Click(object sender, RoutedEventArgs e)
    {
        // Built from the configured port whether or not the endpoint is up, so the command can be
        // copied first and the switch thrown after.
        string command = $"claude mcp add --transport http canterminal http://127.0.0.1:{_mcpPort}/mcp";
        try
        {
            Clipboard.SetText(command);
            InfoText.Text = $"Copied:  {command}";
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "Copy registration command", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
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
}
