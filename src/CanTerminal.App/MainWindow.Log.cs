using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using CanTerminal.Core;
using CanTerminal.Core.Logs;
using Microsoft.Win32;

namespace CanTerminal.App;

// Opening and closing log files, and projecting the loaded capture into the panes.
public partial class MainWindow
{
    // ---------- log files ----------

    private void OpenLog_Executed(object sender, ExecutedRoutedEventArgs e)
    {
        var dlg = new OpenFileDialog { Filter = LogReaders.DialogFilter };
        if (dlg.ShowDialog(this) == true) OpenLog(dlg.FileName);
    }

    private void OpenLog(string path)
    {
        if (!File.Exists(path))
        {
            MessageBox.Show(this, $"{path}\n\nThe file is no longer there.",
                            "Open log", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        if (LogReaders.For(path) is not { } reader)
        {
            MessageBox.Show(this,
                $"{Path.GetFileName(path)}\n\nThis build reads " +
                $"{string.Join(", ", LogReaders.All.Select(r => r.Description))}.",
                "Open log", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        // Warned before reading rather than after, because the cost being warned about is the
        // read. The reader owns the estimate: bytes-per-frame is a property of the format.
        long size = new FileInfo(path).Length;
        long estimate = reader.EstimateFrames(size);
        if (estimate > LargeLogFrames)
        {
            var proceed = MessageBox.Show(this,
                $"{Path.GetFileName(path)} is {size / 1048576.0:N0} MB — very roughly {estimate:N0} frames, " +
                $"needing on the order of {estimate * 500 / 1073741824.0:N1} GB.\n\nOpen it anyway?",
                "Open log", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.OK) return;
        }

        // A live capture and a file cannot share the ring: one carries device timestamps and the
        // other the file's, and interleaved neither reading means anything.
        if (_adapter is not null)
        {
            var proceed = MessageBox.Show(this,
                $"Opening {Path.GetFileName(path)} disconnects from {_adapter.Name} and discards " +
                $"{_hub.TotalFrames:N0} captured frames.\n\nContinue?",
                "Open log", MessageBoxButton.OKCancel, MessageBoxImage.Warning);
            if (proceed != MessageBoxResult.OK) return;
            Disconnect();
        }

        // Before anything is cleared: frames already queued for display would otherwise land in
        // the log's panes and be read as part of the file. The ordering rule Clear documents.
        while (_pending.TryDequeue(out _)) { }

        var log = LogProgressDialog.Run(this, path, reader, _hub);
        if (log is null) return;                       // cancelled, or already reported

        if (log.Frames.Count == 0)
        {
            MessageBox.Show(this,
                $"{Path.GetFileName(path)}\n\nNo frames were found." +
                (log.SkippedLines > 0 ? $" {log.SkippedLines:N0} lines were not understood." : ""),
                "Open log", MessageBoxButton.OK, MessageBoxImage.Warning);
            _hub.Clear();
            return;
        }

        _log = log;
        _settings.PushRecentLog(path);
        _displaySkipped = 0;

        // The file's own clock, anchored on its header date. _haveZero is otherwise only reset by
        // Clear, so without this a log opened after a live session hangs off the device's anchor.
        _zeroTs = 0;
        _zeroWall = log.StartWall ?? DateTime.Now;
        _haveZero = true;

        EnterLogMode();
        // Before the panes are offered the channels, so the reprojection each pane does when its
        // channel is set replays from the player — which is at the start — rather than from the
        // whole hub.
        SetUpTransport(log);
        OnChannelsOpened(log.Channels);
        ApplyTimeBase();
        ApplyContentFilters();
        ProjectToPanes();
        UpdateMenuState();
        UpdateSummary();
        UpdateStatusBar();

        InfoText.Text = $"{Path.GetFileName(path)} — {log.Frames.Count:N0} frames, " +
                        $"{log.Duration:0.000} s, {string.Join(" + ", log.Channels)}. Press Play (F7) to replay it." +
                        (log.StartWallIsApproximate ? "  (start time approximate — this file is a continuation)" : "");
        if (log.SkippedLines > 0) ReportSkippedLines(log);
    }

    /// <summary>
    /// Says what the reader could not read.
    ///
    /// A text log fails by matching fewer lines, not by raising anything, so this count is the
    /// whole difference between "the file held no error frames" and "the parser cannot see error
    /// frames". Worth a dialog, and it stays in the status bar afterwards.
    /// </summary>
    private void ReportSkippedLines(LogFile log)
    {
        string shapes = string.Join("\n", log.SkippedByShape
            .OrderByDescending(kv => kv.Value)
            .Select(kv => $"    {kv.Value,9:N0}  {kv.Key}"));
        string samples = string.Join("\n", log.SkippedSamples.Take(6).Select(l => "    " + l.Trim()));
        MessageBox.Show(this,
            $"{log.SkippedLines:N0} of the lines in {Path.GetFileName(log.Path)} were not understood, " +
            $"and are not in the trace.\n\n{shapes}\n\nFor example:\n{samples}",
            "Open log", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    private void CloseLog_Click(object sender, RoutedEventArgs e) => CloseLog();

    private void CloseLog()
    {
        if (_log is null) return;
        _log = null;
        TearDownTransport();
        _hub.Clear();
        _hub.SetCapacity(200_000);                     // back to the live ring
        _haveZero = false;
        foreach (var pane in new[] { PaneA, PaneB })
        {
            pane.ClearAll();
            pane.SetHistoryCapacity(_historyCapacity);   // back to the size the user chose
        }
        // The file's channels go with it; a device is not open, so there are none to offer.
        OnChannelsOpened([]);
        ExitLogMode();
        ApplyTimeBase();
        UpdateConnStatus();
        UpdateMenuState();
        UpdateSummary();
        UpdateStatusBar();
        InfoText.Text = "Log closed.";
    }

    /// <summary>
    /// Dresses the window so a file can never be taken for a live bus.
    ///
    /// The amber is the one Pause already uses, deliberately: both states make the same claim —
    /// that what is on screen has stopped tracking the bus. A second colour would only invite the
    /// reader to work out what the difference meant.
    /// </summary>
    private static readonly Brush LogModeBackground = Frozen(Color.FromRgb(0xFF, 0xE9, 0xA8));
    private static readonly Brush LogModeBorder = Frozen(Color.FromRgb(0xD9, 0xA8, 0x1E));

    private static Brush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private void EnterLogMode()
    {
        ToolbarBar.Background = LogModeBackground;
        ToolbarBar.BorderBrush = LogModeBorder;
        ConnectButton.Content = "Close log";
        Title = $"{Path.GetFileName(_log!.Path)} — log file (offline) — CanTerminal";
    }

    private void ExitLogMode()
    {
        ToolbarBar.ClearValue(Border.BackgroundProperty);
        ToolbarBar.ClearValue(Border.BorderBrushProperty);
        ConnectButton.Content = _adapter is null ? "Connect" : "Disconnect";
        Title = "CanTerminal — ValueCAN Monitor";
    }

    private void RecentLogsMenu_Opened(object sender, RoutedEventArgs e) =>
        FillRadioMenu(MenuRecentLogs, _settings.RecentLogs, p => p, _ => false, OpenLog);

    /// <summary>
    /// Rebuilds the panes from the hub. Used after a load, and after anything that changes how a
    /// frame reads — a database, a protocol session, a pane's own filter.
    /// </summary>
    private void ProjectToPanes(ChannelPane? only = null)
    {
        var panes = only is null ? ActivePanes.ToArray() : [only];
        // Only as far as the replay has got. Showing the whole file behind a transport parked at
        // the start would contradict the position it reports.
        IReadOnlyList<CanFrame> frames = _player is { } player ? player.Played() : _hub.Snapshot();
        var previous = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            // The trace buffer is a ring sized for a live capture, and a file is bigger than it.
            // Left alone it would keep the newest rows and drop the rest — so opening a log would
            // show its last fifty thousand frames while looking exactly like the whole file.
            int capacity = Math.Max(TraceBuffer.MinCapacity, _log?.Frames.Count ?? frames.Count);
            foreach (var pane in panes) pane.SetHistoryCapacity(capacity);
            foreach (var pane in panes) pane.ClearAll();
            foreach (var frame in frames)
                foreach (var pane in panes)
                    if (pane.Accepts(frame)) pane.Append(frame, now: 0, highlight: false);
            foreach (var pane in panes)
            {
                // The change highlight decays in wall time, so replaying eight minutes of bus in
                // two seconds would light every aggregate row at once and fade them together —
                // "everything just changed", which is not what happened.
                pane.ClearHighlights();
                pane.FinishProjection();
            }
        }
        finally { Mouse.OverrideCursor = previous; }
    }

    /// <summary>
    /// Re-reads the whole loaded capture: annotate again, then rebuild the panes.
    ///
    /// This is the thing a file can do that a live bus cannot. A protocol session configured
    /// halfway through a capture only ever sees the rest of it; a file is replayed from its first
    /// frame, so the commands that set the session up are decoded too and the readings that
    /// depend on them come out right.
    /// </summary>
    private void ReplayFromHub()
    {
        if (_log is null) return;
        _hub.Reannotate();
        ApplyContentFilters();          // reads GroupKey, so it must follow the re-annotate
        ProjectToPanes();
    }
}
