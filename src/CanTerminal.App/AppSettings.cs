using System.IO;
using System.Text.Json;

namespace CanTerminal.App;

/// <summary>
/// What has to outlive the process: the recent-file lists, and the session settings a user
/// would otherwise re-enter at every start — bus speeds, layout, text size, the API server,
/// where the window was. A corrupt or unreadable file is treated as "no settings"; losing
/// these is not worth failing startup over.
/// </summary>
public sealed class AppSettings
{
    public const int MaxRecentDbc = 9;
    public const int MaxRecentLogs = 9;

    public List<string> RecentDbc { get; set; } = [];
    public List<string> RecentLogs { get; set; } = [];

    // Session settings. The defaults are the values MainWindow started with before these were
    // persisted, so a missing file — and a settings.json written by an older build — behaves
    // exactly as before.
    public string? Channels { get; set; }               // null = the built-in "CAN1,CAN2"
    public int Bitrate { get; set; } = 500_000;
    public int FdBitrate { get; set; } = 2_000_000;
    public bool FdEnabled { get; set; }
    public int Layout { get; set; }                     // 0 single, 1 split ↔, 2 split ↕
    public double FontSize { get; set; } = 12;
    public string? Timestamps { get; set; }             // TimestampMode name; null = Relative
    public int HistoryCapacity { get; set; } = TraceBuffer.DefaultCapacity;
    public bool ApiServer { get; set; } = true;
    public int ApiPort { get; set; } = 29536;

    // The MCP endpoint the app serves itself. On by default — the program is meant to be driven
    // from Claude, and an endpoint nobody connects to costs a bound loopback socket. Turn it off
    // in Tools if that is not wanted; the choice is remembered.
    public bool McpServer { get; set; } = true;

    // Away from the TCP API's 29536 rather than next to it: adjacent numbers invite a collision
    // the moment someone moves one of them, and 5400 is clear of the usual development ports.
    public int McpPort { get; set; } = 5400;
    public int CycleMs { get; set; } = 100;

    // Window placement. Zero (never saved) leaves the XAML defaults alone.
    public double WindowWidth { get; set; }
    public double WindowHeight { get; set; }
    public double WindowLeft { get; set; }
    public double WindowTop { get; set; }
    public bool WindowMaximized { get; set; }

    /// <summary>A property rather than a constant so tests can point Load/Save at a scratch file.</summary>
    internal static string SettingsPath { get; set; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CanTerminal", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
                return (JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(SettingsPath))
                        ?? new AppSettings()).Sanitized();
        }
        catch { }
        return new AppSettings();
    }

    /// <summary>
    /// Clamps every numeric to the range its own dialog enforces, so a hand-edited file cannot
    /// put the program somewhere its UI cannot reach — a 0 pt font, a negative history size, a
    /// layout index no code path handles.
    /// </summary>
    internal AppSettings Sanitized()
    {
        Bitrate = Math.Clamp(Bitrate, 10_000, 1_000_000);
        FdBitrate = Math.Clamp(FdBitrate, 100_000, 8_000_000);
        Layout = Math.Clamp(Layout, 0, 2);
        FontSize = Math.Clamp(FontSize, 8, 28);
        HistoryCapacity = Math.Clamp(HistoryCapacity, TraceBuffer.MinCapacity, 5_000_000);
        ApiPort = Math.Clamp(ApiPort, 1, 65535);
        McpPort = Math.Clamp(McpPort, 1, 65535);
        CycleMs = Math.Clamp(CycleMs, 1, 3_600_000);
        if (!Enum.TryParse<TimestampMode>(Timestamps, out _)) Timestamps = null;
        return this;
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
    public void PushRecentDbc(string path) => Push(RecentDbc, path, MaxRecentDbc);

    public void PushRecentLog(string path) => Push(RecentLogs, path, MaxRecentLogs);

    private void Push(List<string> list, string path, int max)
    {
        list.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, path);
        if (list.Count > max) list.RemoveRange(max, list.Count - max);
        Save();
    }
}
