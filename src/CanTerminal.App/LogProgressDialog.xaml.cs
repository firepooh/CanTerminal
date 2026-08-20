using System.IO;
using System.Windows;
using CanTerminal.Core;
using CanTerminal.Core.Logs;

namespace CanTerminal.App;

/// <summary>
/// Reads a log file off the UI thread and shows how far it has got.
///
/// Half a million frames is a second or two of parsing and rather more of protocol decoding, and
/// a window that stops repainting for that long reads as a hang. Cancelling abandons the whole
/// load: a partly-read capture that looks like a whole one is the failure this program exists to
/// avoid.
/// </summary>
public partial class LogProgressDialog : Window
{
    private readonly CancellationTokenSource _cts = new();
    private readonly string _path;
    private readonly ILogReader _reader;
    private readonly MessageHub _hub;

    private LogFile? _result;
    private Exception? _error;
    private bool _finished;

    private LogProgressDialog(string path, ILogReader reader, MessageHub hub)
    {
        InitializeComponent();
        _path = path;
        _reader = reader;
        _hub = hub;
        FileText.Text = Path.GetFileName(path);
        PhaseText.Text = "Reading…";
    }

    /// <summary>
    /// Reads the file and loads it into the hub, decoding as it goes. Returns null if the user
    /// cancelled or the read failed — the failure is reported here, so the caller only has to
    /// decide what to do with nothing.
    /// </summary>
    public static LogFile? Run(Window owner, string path, ILogReader reader, MessageHub hub)
    {
        var dialog = new LogProgressDialog(path, reader, hub) { Owner = owner };
        dialog.ShowDialog();
        if (dialog._error is { } error)
        {
            MessageBox.Show(owner, $"{Path.GetFileName(path)}\n\n{error.Message}",
                            "Open log", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
        return dialog._result;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        var read = new Progress<double>(f =>
        {
            PhaseText.Text = $"Reading… {f * 100:0}%";
            Bar.Value = f * 60;                      // parsing is the first 60% of the bar
        });

        try
        {
            _result = await Task.Run(() =>
            {
                var log = _reader.Read(_path, read, _cts.Token);
                _cts.Token.ThrowIfCancellationRequested();

                Dispatcher.Invoke(() =>
                {
                    PhaseText.Text = $"Decoding {log.Frames.Count:N0} frames…";
                    Bar.Value = 60;
                    // The decode is one pass under the hub's lock and reports nothing from
                    // inside, so the bar goes indeterminate rather than pretending to move.
                    Bar.IsIndeterminate = true;
                    CancelButton.IsEnabled = false;
                });

                // Sized to the file so the ring never wraps: everything stays reachable through
                // the same reader the live path uses.
                _hub.Clear();
                _hub.SetCapacity(Math.Max(1, log.Frames.Count));
                _hub.PublishBulk(log.Frames);
                return log;
            }, _cts.Token).ConfigureAwait(true);
        }
        catch (OperationCanceledException) { _result = null; }
        catch (Exception ex) { _error = ex; _result = null; }

        _finished = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => _cts.Cancel();

    private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
    {
        // Esc or the close box while the read is still running: cancel and let the load finish
        // unwinding, rather than leaving the window gone and the work still going.
        if (_finished) { _cts.Dispose(); return; }
        _cts.Cancel();
        e.Cancel = true;
    }
}
