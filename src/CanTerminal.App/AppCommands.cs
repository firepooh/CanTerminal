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

    public static readonly RoutedUICommand JumpToLive =
        Make("Jump to live", nameof(JumpToLive), new KeyGesture(Key.End));
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
}
