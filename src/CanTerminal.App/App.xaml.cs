using System.IO;
using System.Windows;
using System.Windows.Threading;

namespace CanTerminal.App;

public partial class App : Application
{
    /// <summary>
    /// Where a crash is written down. The window is normally gone by the time anyone reads it,
    /// so the message has to survive the process.
    /// </summary>
    private static string CrashLogPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanTerminal", "crash.log");

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        // A UI-thread fault is recoverable often enough to be worth surviving: a dialog that
        // failed to open should not take a running capture with it.
        DispatcherUnhandledException += (_, args) =>
        {
            Record("dispatcher", args.Exception);
            MessageBox.Show(
                $"{args.Exception.Message}\n\nThe capture is still running. Details were written to:\n{CrashLogPath}",
                "CanTerminal — unexpected error", MessageBoxButton.OK, MessageBoxImage.Error);
            args.Handled = true;
        };

        // A background-thread fault is not recoverable — the runtime tears the process down
        // regardless. The point here is only that it stops being invisible: the receive thread
        // and the API server both run off the UI thread, and until now a fault on either simply
        // made the window disappear with nothing said and nothing written anywhere.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            Record("background thread", args.ExceptionObject as Exception);
    }

    private static void Record(string where, Exception? ex)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(CrashLogPath)!);
            File.AppendAllText(CrashLogPath,
                $"--- {DateTime.Now:yyyy-MM-dd HH:mm:ss} ({where}) ---{Environment.NewLine}{ex}{Environment.NewLine}{Environment.NewLine}");
        }
        catch { /* the handler of last resort cannot itself throw */ }
    }
}
