using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using CanTerminal.Core;

namespace CanTerminal.App;

/// <summary>
/// Name, version, runtime and repository — plus whether the device driver this program needs is
/// actually on the machine, which is the first thing to check when no hardware appears and the
/// one fact a user cannot easily get anywhere else.
/// </summary>
public partial class AboutDialog : Window
{
    /// <summary>Where the Intrepid driver installs itself.</summary>
    private static readonly string DriverPath =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "icsneo40.dll");

    public AboutDialog()
    {
        InitializeComponent();
        VersionText.Text = $"Version {AppInfo.Version}";
        RuntimeText.Text = $".NET {Environment.Version}  ·  {RuntimeInformation.ProcessArchitecture}";
        DriverText.Text = File.Exists(DriverPath)
            ? "icsneo40.dll found"
            : "icsneo40.dll not found — install the Intrepid drivers for ValueCAN support";
    }

    private void Repo_Click(object sender, RoutedEventArgs e)
    {
        try { Process.Start(new ProcessStartInfo(AppInfo.RepositoryUrl) { UseShellExecute = true }); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "About CanTerminal", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
