using System.Globalization;
using System.Text;
using System.Windows.Input;

namespace CanTerminal.App;

/// <summary>
/// Commands behind the menu items that carry a keyboard shortcut. Using RoutedUICommand rather
/// than a Click handler plus a KeyBinding buys two things: the menu renders the gesture text
/// itself, and CanExecute drives the item's enabled state, so a shortcut cannot fire an action
/// its menu entry is greyed out for.
/// </summary>
public static class AppCommands
{
    public static readonly RoutedUICommand LoadDbc =
        Make("Load DBC", nameof(LoadDbc), new KeyGesture(Key.D, ModifierKeys.Control));
    public static readonly RoutedUICommand SaveCsv =
        Make("Save trace as CSV", nameof(SaveCsv), new KeyGesture(Key.S, ModifierKeys.Control));
    public static readonly RoutedUICommand OpenLog =
        Make("Open log", nameof(OpenLog), new KeyGesture(Key.O, ModifierKeys.Control));
    // No gesture for closing a log, matching Unload DBC: of a load/unload pair this file only
    // ever binds the load.

    public static readonly RoutedUICommand RefreshDevices =
        Make("Refresh devices", nameof(RefreshDevices), new KeyGesture(Key.F5));
    public static readonly RoutedUICommand EditChannels =
        Make("Channels", nameof(EditChannels), new KeyGesture(Key.C, ModifierKeys.Control | ModifierKeys.Shift));
    public static readonly RoutedUICommand Connect =
        Make("Connect", nameof(Connect), new KeyGesture(Key.F9));
    public static readonly RoutedUICommand Disconnect =
        Make("Disconnect", nameof(Disconnect), new KeyGesture(Key.F9, ModifierKeys.Shift));

    // Explicit display strings: KeyConverter renders Key.D1 as "D1" and Key.Return as "Return".
    public static readonly RoutedUICommand LayoutSingle =
        Make("Single", nameof(LayoutSingle), new KeyGesture(Key.D1, ModifierKeys.Control, "Ctrl+1"));
    public static readonly RoutedUICommand LayoutSplitH =
        Make("Split horizontally", nameof(LayoutSplitH), new KeyGesture(Key.D2, ModifierKeys.Control, "Ctrl+2"));
    public static readonly RoutedUICommand LayoutSplitV =
        Make("Split vertically", nameof(LayoutSplitV), new KeyGesture(Key.D3, ModifierKeys.Control, "Ctrl+3"));
    public static readonly RoutedUICommand LayoutXcpSplit =
        Make("XCP command / data split", nameof(LayoutXcpSplit),
             new KeyGesture(Key.D4, ModifierKeys.Control, "Ctrl+4"));

    // Both the main row and the numeric keypad, because either is what a hand reaches for.
    public static readonly RoutedUICommand FontLarger =
        Make("Larger text", nameof(FontLarger),
             new KeyGesture(Key.OemPlus, ModifierKeys.Control, "Ctrl++"),
             new KeyGesture(Key.Add, ModifierKeys.Control));
    public static readonly RoutedUICommand FontSmaller =
        Make("Smaller text", nameof(FontSmaller),
             new KeyGesture(Key.OemMinus, ModifierKeys.Control, "Ctrl+-"),
             new KeyGesture(Key.Subtract, ModifierKeys.Control));
    public static readonly RoutedUICommand FontReset =
        Make("Reset text size", nameof(FontReset),
             new KeyGesture(Key.D0, ModifierKeys.Control, "Ctrl+0"),
             new KeyGesture(Key.NumPad0, ModifierKeys.Control));

    public static readonly RoutedUICommand JumpToLive =
        Make("Jump to live", nameof(JumpToLive), new KeyGesture(Key.End));
    public static readonly RoutedUICommand GoToTime =
        Make("Go to time", nameof(GoToTime), new KeyGesture(Key.G, ModifierKeys.Control));
    public static readonly RoutedUICommand TogglePause =
        Make("Pause display", nameof(TogglePause), new KeyGesture(Key.F7));
    public static readonly RoutedUICommand ClearAll =
        Make("Clear all", nameof(ClearAll), new KeyGesture(Key.L, ModifierKeys.Control));

    public static readonly RoutedUICommand SendFrame =
        Make("Send frame", nameof(SendFrame), new KeyGesture(Key.Return, ModifierKeys.Control, "Ctrl+Enter"));
    public static readonly RoutedUICommand StartCyclic =
        Make("Start cyclic TX", nameof(StartCyclic), new KeyGesture(Key.F6));
    public static readonly RoutedUICommand StopCyclic =
        Make("Stop cyclic TX", nameof(StopCyclic), new KeyGesture(Key.F6, ModifierKeys.Shift));

    public static readonly RoutedUICommand Shortcuts =
        Make("Keyboard shortcuts", nameof(Shortcuts), new KeyGesture(Key.OemQuestion, ModifierKeys.Control, "Ctrl+/"));

    private static RoutedUICommand Make(string text, string name, params InputGesture[] gestures)
    {
        var collection = new InputGestureCollection();
        foreach (var g in gestures) collection.Add(g);
        return new RoutedUICommand(text, name, typeof(AppCommands), collection);
    }

    /// <summary>
    /// Every command above, grouped the way the menu bar groups them. The Help dialog renders
    /// this, so a command added to this file appears there by construction — the previous dialog
    /// was a hardcoded copy of the same information, and Ctrl+O and Ctrl+G had already gone
    /// missing from it while the menus carried them. A test holds the two views together.
    /// </summary>
    internal static readonly (string Section, RoutedUICommand[] Commands)[] MenuSections =
    [
        ("File", [OpenLog, LoadDbc, SaveCsv]),
        ("Bus", [RefreshDevices, EditChannels, Connect, Disconnect]),
        ("View", [LayoutSingle, LayoutSplitH, LayoutSplitV, LayoutXcpSplit,
                  FontLarger, FontSmaller, FontReset,
                  GoToTime, JumpToLive, TogglePause, ClearAll]),
        ("Transmit", [SendFrame, StartCyclic, StopCyclic]),
        ("Help", [Shortcuts]),
    ];

    /// <summary>The body of the Help ▸ Keyboard shortcuts dialog.</summary>
    internal static string ShortcutsText()
    {
        var sb = new StringBuilder();
        foreach (var (section, commands) in MenuSections)
        {
            if (sb.Length > 0) sb.AppendLine();
            sb.AppendLine(section);
            foreach (var command in commands)
                sb.AppendLine($"  {GestureText(command),-16}{command.Text}");
        }
        // The one shortcut with no command behind it: the panes raise ZoomRequested themselves.
        sb.AppendLine();
        sb.Append("  Ctrl+wheel      Text size, over either pane");
        return sb.ToString();
    }

    /// <summary>
    /// A command's first gesture as the menu shows it: the explicit display string where one was
    /// given (KeyConverter renders D1 as "D1" and Return as "Return"), the converter's reading
    /// otherwise.
    /// </summary>
    internal static string GestureText(RoutedUICommand command) =>
        command.InputGestures.OfType<KeyGesture>().FirstOrDefault() is not { } g ? ""
        : g.DisplayString.Length > 0 ? g.DisplayString
        : g.GetDisplayStringForCulture(CultureInfo.InvariantCulture);
}
