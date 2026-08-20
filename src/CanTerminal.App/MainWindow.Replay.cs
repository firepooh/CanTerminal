using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using CanTerminal.Core.Logs;

namespace CanTerminal.App;

// The replay transport: play/pause, speed, and the seek slider over an open log file.
public partial class MainWindow
{
    // ---------- replay transport ----------

    /// <summary>
    /// Frames handed to the panes in one tick while replaying.
    ///
    /// Sized from the measured cost of turning a frame into a row — about five microseconds per
    /// pane — so a tick at the ceiling stays near the same budget the live path holds itself to.
    /// It only binds at high replay rates: at 1x a busy capture is fifty frames a tick.
    /// </summary>
    private const int ReplayBudget = 4000;

    /// <summary>
    /// Most file time one tick may account for, however long the tick actually took.
    ///
    /// A stall on the UI thread — a garbage collection, a dialog, a busy machine — otherwise
    /// arrives as one enormous step, and the replay teleports: several seconds of bus appear at
    /// once and the position jumps past whatever the user was watching for. Capping it makes a
    /// stall show up as a replay that ran slow, which is both true and harmless.
    /// </summary>
    private const double MaxReplayStep = 0.25;

    private LogPlayer? _player;
    private double _lastReplayTick;
    private bool _seeking;
    private bool _updatingTransport;

    private void FlushReplay()
    {
        if (_player is not { } player) return;

        double clock = _uiClock.Elapsed.TotalSeconds;
        double elapsed = Math.Clamp(clock - _lastReplayTick, 0, MaxReplayStep);
        _lastReplayTick = clock;
        if (!player.IsPlaying) return;

        bool highlight = MenuHighlight.IsChecked;
        var due = player.Advance(elapsed, ReplayBudget);
        var panes = ActivePanes.ToArray();

        foreach (var frame in due)
            foreach (var pane in panes)
                if (pane.Accepts(frame)) pane.Append(frame, player.Position, highlight);

        // The highlight decays against the replay clock, not the wall clock. Otherwise a capture
        // replayed at ten times speed shows every byte lit at once, because ten seconds of change
        // arrive inside one second of fading.
        if (highlight)
            foreach (var pane in panes)
                if (pane.FixedVisible) pane.TickFade(player.Position);

        if (due.Count > 0) foreach (var pane in panes) pane.AfterAppend();
        UpdateTransport();
        if (!player.IsPlaying) ShowPlayState();       // reached the end on its own
    }

    private void Play_Click(object sender, RoutedEventArgs e) => TogglePlay();

    private void TogglePlay()
    {
        if (_player is not { } player) return;
        if (player.IsPlaying) player.Pause();
        else
        {
            // Playing from the end restarts, which is what the button appears to offer there.
            if (player.AtEnd) RewindReplay();
            _lastReplayTick = _uiClock.Elapsed.TotalSeconds;
            player.Play();
        }
        ShowPlayState();
    }

    private void Rewind_Click(object sender, RoutedEventArgs e)
    {
        RewindReplay();
        ShowPlayState();
    }

    private void RewindReplay()
    {
        if (_player is not { } player) return;
        player.Rewind();
        ProjectToPanes();
        UpdateTransport();
    }

    private void Speed_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_player is null || SpeedCombo.SelectedItem is not ComboBoxItem item) return;
        _player.Speed = (double)item.Tag;
    }

    private void Seek_DragStarted(object sender, System.Windows.Controls.Primitives.DragStartedEventArgs e) =>
        _seeking = true;

    private void Seek_DragCompleted(object sender, System.Windows.Controls.Primitives.DragCompletedEventArgs e)
    {
        _seeking = false;
        SeekTo(SeekSlider.Value);
    }

    /// <summary>
    /// Committed on release rather than continuously. Seeking backwards replays the file from its
    /// start to rebuild the aggregate counts, which is seconds of work on a large capture — doing
    /// that for every pixel of a drag would make the slider unusable.
    /// </summary>
    private void Seek_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_updatingTransport || _seeking || _player is null) return;
        SeekTo(e.NewValue);                          // a click on the track, not a drag
    }

    private void SeekTo(double seconds)
    {
        if (_player is not { } player) return;
        var previous = Mouse.OverrideCursor;
        Mouse.OverrideCursor = Cursors.Wait;
        try
        {
            var forward = player.SeekTo(seconds, out bool rebuild);
            if (rebuild)
            {
                ProjectToPanes();
            }
            else if (forward is { Count: > 0 } gap)
            {
                var panes = ActivePanes.ToArray();
                foreach (var frame in gap)
                    foreach (var pane in panes)
                        if (pane.Accepts(frame)) pane.Append(frame, player.Position, highlight: false);
                foreach (var pane in panes) { pane.ClearHighlights(); pane.FinishProjection(); }
            }
        }
        finally { Mouse.OverrideCursor = previous; }
        UpdateTransport();
    }

    /// <summary>Reflects the player's state on the transport, without the controls answering back.</summary>
    private void UpdateTransport()
    {
        if (_player is not { } player) return;
        _updatingTransport = true;
        try
        {
            if (!_seeking) SeekSlider.Value = player.Position;
            PositionText.Text = $"{player.Position:0.000} / {player.End:0.000} s";
            PlayedText.Text = $"{player.EmittedCount:N0} / {player.TotalCount:N0} frames";
        }
        finally { _updatingTransport = false; }
    }

    private void ShowPlayState()
    {
        bool playing = _player?.IsPlaying == true;
        PlayButton.Content = playing ? "❚❚  Pause" : "▶  Play";
        UpdateTransport();
        // The row counts on the panes are refreshed once a second while playing; at a change of
        // state they would otherwise sit a second behind the thing that just happened.
        UpdateStatusBar();
    }

    private void SetUpTransport(LogFile log)
    {
        _player = new LogPlayer(log.Frames);
        _lastReplayTick = _uiClock.Elapsed.TotalSeconds;

        SpeedCombo.Items.Clear();
        foreach (double speed in LogPlayer.Speeds)
            SpeedCombo.Items.Add(new ComboBoxItem
            {
                Content = speed <= 0 ? "Max" : speed < 1 ? $"{speed:0.##}x" : $"{speed:0.#}x",
                Tag = speed,
            });
        SpeedCombo.SelectedIndex = Array.IndexOf(LogPlayer.Speeds, 1.0);

        SeekSlider.Minimum = _player.Start;
        SeekSlider.Maximum = _player.End <= _player.Start ? _player.Start + 1 : _player.End;
        SeekSlider.LargeChange = Math.Max(0.001, _player.Duration / 20);
        SeekSlider.SmallChange = Math.Max(0.001, _player.Duration / 200);
        TransportBar.Visibility = Visibility.Visible;
        ShowPlayState();
    }

    private void TearDownTransport()
    {
        _player = null;
        TransportBar.Visibility = Visibility.Collapsed;
    }
}
