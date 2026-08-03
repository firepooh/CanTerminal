using System.IO;
using System.Text.Json;

namespace CanTerminal.App;

/// <summary>
/// The little that has to outlive the process. Everything else in the window is either derived
/// from the connected device or cheap enough to re-enter, so this stays deliberately small.
/// A corrupt or unreadable file is treated as "no settings" — losing the recent list is not
/// worth failing startup over.
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecentDbc = 9;

    public List<string> RecentDbc { get; set; } = [];

    private static string SettingsPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanTerminal", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath)) ?? new AppSettings();
        }
        catch { }
        return new AppSettings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(SettingsPath)!);
            File.WriteAllText(SettingsPath, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch { }
    }

    /// <summary>Moves a file to the front of the recent list, keeping it deduplicated and bounded.</summary>
    public void PushRecentDbc(string path)
    {
        RecentDbc.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        RecentDbc.Insert(0, path);
        if (RecentDbc.Count > MaxRecentDbc) RecentDbc.RemoveRange(MaxRecentDbc, RecentDbc.Count - MaxRecentDbc);
        Save();
    }
}
