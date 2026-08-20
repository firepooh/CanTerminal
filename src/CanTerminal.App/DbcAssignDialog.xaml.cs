using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace CanTerminal.App;

/// <summary>
/// Asks which channel each selected database describes.
///
/// Two ports of one device commonly run the same protocol on different identifiers, and nothing
/// stops two buses using one identifier for unrelated messages. Which database applies is
/// therefore a property of the channel. Guessing it from the order the files were named worked
/// for a matched pair and for nothing else — and bound the wrong way round it decodes every frame
/// against the wrong database while looking entirely plausible, so it is asked rather than
/// inferred.
/// </summary>
public partial class DbcAssignDialog : Window
{
    /// <summary>Combo entry meaning "every channel", i.e. the single shared database.</summary>
    private const string AllChannels = "All channels";

    private readonly List<(string Path, ComboBox Combo)> _rows = [];

    /// <summary>Path to channel, where a null channel means the database applies to all of them.</summary>
    public IReadOnlyList<(string Path, string? Channel)> Assignments { get; private set; } = [];

    private DbcAssignDialog() => InitializeComponent();

    /// <summary>
    /// Returns the assignments on OK, or null on cancel. <paramref name="suggested"/> prefills a
    /// row when the caller already has a reasonable guess, so a matched pair is one click.
    /// </summary>
    public static IReadOnlyList<(string Path, string? Channel)>? Ask(
        Window owner, IReadOnlyList<string> paths, IReadOnlyList<string> channels,
        IReadOnlyDictionary<string, string?>? suggested = null)
    {
        var dialog = new DbcAssignDialog { Owner = owner };
        var entries = new List<string> { AllChannels };
        entries.AddRange(channels);

        for (int i = 0; i < paths.Count; i++)
        {
            dialog.Rows.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

            var name = new TextBlock
            {
                Text = Path.GetFileName(paths[i]),
                ToolTip = paths[i],
                FontFamily = new System.Windows.Media.FontFamily("Consolas"),
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(0, 3, 12, 3),
            };
            Grid.SetRow(name, i);
            Grid.SetColumn(name, 0);
            dialog.Rows.Children.Add(name);

            var combo = new ComboBox
            {
                ItemsSource = entries,
                Width = 160,
                Margin = new Thickness(0, 3, 0, 3),
            };
            string? prefill = suggested is not null && suggested.TryGetValue(paths[i], out var s) ? s : null;
            int index = prefill is null ? 0 : entries.FindIndex(e => e.Equals(prefill, StringComparison.OrdinalIgnoreCase));
            combo.SelectedIndex = index >= 0 ? index : 0;
            Grid.SetRow(combo, i);
            Grid.SetColumn(combo, 1);
            dialog.Rows.Children.Add(combo);

            dialog._rows.Add((paths[i], combo));
        }

        dialog.HintText.Text = channels.Count > 0
            ? $"Channels available: {string.Join(", ", channels)}."
            : "No channels are open yet, so a database can only be shared by all of them.";

        return dialog.ShowDialog() == true ? dialog.Assignments : null;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var chosen = _rows
            .Select(r => (r.Path, Channel: r.Combo.SelectedItem as string == AllChannels ? null : r.Combo.SelectedItem as string))
            .ToList();

        // Two databases on one channel would mean the second silently replaces the first, and the
        // file that lost is still listed as loaded.
        var clash = chosen.Where(c => c.Channel is not null)
                          .GroupBy(c => c.Channel, StringComparer.OrdinalIgnoreCase)
                          .FirstOrDefault(g => g.Count() > 1);
        if (clash is not null)
        {
            MessageBox.Show(this,
                $"{clash.Count()} files are assigned to {clash.Key}:\n\n" +
                string.Join("\n", clash.Select(c => "    " + Path.GetFileName(c.Path))) +
                "\n\nOne database per channel.",
                "Assign DBC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        int shared = chosen.Count(c => c.Channel is null);
        if (shared > 1)
        {
            MessageBox.Show(this,
                $"{shared} files are set to all channels. Only one database can be the shared one.",
                "Assign DBC", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        Assignments = chosen;
        DialogResult = true;
    }
}
