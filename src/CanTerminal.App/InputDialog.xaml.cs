using System.Windows;

namespace CanTerminal.App;

/// <summary>
/// One modal text prompt, shared by the four settings that moved out of the toolbar
/// (channels, history size, cycle time, API port). Enter confirms, Esc cancels.
/// </summary>
public partial class InputDialog : Window
{
    private InputDialog() => InitializeComponent();

    /// <summary>Returns the entered text, or null when the user cancelled.</summary>
    public static string? Ask(Window owner, string title, string prompt, string value, string? hint = null)
    {
        var dlg = new InputDialog { Owner = owner, Title = title };
        dlg.PromptText.Text = prompt;
        dlg.ValueBox.Text = value;
        dlg.HintText.Text = hint ?? "";
        dlg.HintText.Visibility = string.IsNullOrEmpty(hint) ? Visibility.Collapsed : Visibility.Visible;
        return dlg.ShowDialog() == true ? dlg.ValueBox.Text.Trim() : null;
    }

    /// <summary>Prompts for an integer, re-prompting on anything out of range.</summary>
    public static int? AskInt(Window owner, string title, string prompt, int value, int min, int max, string? hint = null)
    {
        string text = value.ToString();
        while (true)
        {
            string? entered = Ask(owner, title, prompt, text, hint);
            if (entered is null) return null;
            if (int.TryParse(entered, out int parsed) && parsed >= min && parsed <= max) return parsed;
            text = entered;
            MessageBox.Show(owner, $"Enter a whole number between {min:N0} and {max:N0}.",
                            title, MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ValueBox.Focus();
        ValueBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
