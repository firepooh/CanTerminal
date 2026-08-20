using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CanTerminal.App;

/// <summary>
/// Asks which channel each of a set of files describes.
///
/// Used for both databases and A2L files, because the question is the same one: two ports of a
/// device commonly run the same protocol on different identifiers, so which file applies is a
/// property of the channel. Guessing it from the order the files were named works for a matched
/// pair and for nothing else, and a wrong pairing is silent in both directions — either nothing
/// decodes, or, where two buses share an identifier, the wrong thing does.
///
/// A caller that can work the answer out from evidence should still put it here as the
/// suggestion, with its reason in <see cref="Row.Note"/>, so the user sees why.
/// </summary>
public partial class ChannelAssignDialog : Window
{
    /// <summary>Combo entry meaning "every channel".</summary>
    private const string AllChannels = "All channels";

    /// <param name="Suggested">Prefilled channel, or null for "all channels".</param>
    /// <param name="Note">Why it was suggested, shown beside the row. Empty for no reason worth giving.</param>
    public sealed record Row(string Path, string? Suggested = null, string Note = "");

    private readonly List<(string Path, ComboBox Combo)> _rows = [];

    /// <summary>Path to channel, where a null channel means the file applies to all of them.</summary>
    public IReadOnlyList<(string Path, string? Channel)> Assignments { get; private set; } = [];

    private ChannelAssignDialog() => InitializeComponent();

    /// <summary>Returns the assignments on OK, or null on cancel.</summary>
    public static IReadOnlyList<(string Path, string? Channel)>? Ask(
        Window owner, string title, string prompt,
        IReadOnlyList<Row> files, IReadOnlyList<string> channels, bool allowShared = true)
    {
        var dialog = new ChannelAssignDialog { Owner = owner, Title = title };
        dialog.PromptText.Text = prompt;

        var entries = new List<string>();
        if (allowShared) entries.Add(AllChannels);
        entries.AddRange(channels);

        for (int i = 0; i < files.Count; i++)
        {
            var file = files[i];
            dialog.Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock
            {
                Text = Path.GetFileName(file.Path),
                ToolTip = file.Path,
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 3, 12, 3),
            };
            Grid.SetRow(name, i);
            Grid.SetColumn(name, 0);
            dialog.Rows.Children.Add(name);

            var note = new TextBlock
            {
                Text = file.Note,
                Foreground = System.Windows.Media.Brushes.Gray,
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 12, 3),
            };
            Grid.SetRow(note, i);
            Grid.SetColumn(note, 1);
            dialog.Rows.Children.Add(note);

            var combo = new ComboBox
            {
                ItemsSource = entries,
                Width = 160,
                Margin = new Thickness(0, 3, 0, 3),
            };
            int index = file.Suggested is null
                ? (allowShared ? 0 : -1)
                : entries.FindIndex(e => e.Equals(file.Suggested, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = index >= 0 ? index : 0;
            Grid.SetRow(combo, i);
            Grid.SetColumn(combo, 2);
            dialog.Rows.Children.Add(combo);

            dialog._rows.Add((file.Path, combo));
        }

        dialog.HintText.Text = channels.Count > 0
            ? $"Channels available: {string.Join(", ", channels)}."
            : "No channels are open yet.";

        return dialog.ShowDialog() == true ? dialog.Assignments : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows
            .Select(r => (r.Path, Channel: r.Combo.SelectedItem as string == AllChannels ? null : r.Combo.SelectedItem as string))
            .ToList();

        // Two files on one channel would mean the second silently replaces the first, and the one
        // that lost is still listed as loaded.
        var clash = chosen.Where(c => c.Channel is not null)
                          .GroupBy(c => c.Channel, StringComparer.OrdinalIgnoreCase)
                          .FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            MessageBox.Show(this,
                $"{clash.Count()} files are assigned to {clash.Key}:\n\n" +
                string.Join("\n", clash.Select(c => "    " + Path.GetFileName(c.Path))) +
                "\n\nOne file per channel.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int shared = chosen.Count(c => c.Channel is null);
        if (shared > 1)
        {
            MessageBox.Show(this,
                $"{shared} files are set to all channels. Only one can be the shared one.",
                Title, MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Assignments = chosen;
        DialogResult = true;
    }
}
