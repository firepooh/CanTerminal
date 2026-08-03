using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace CanTerminal.App;

/// <summary>
/// Picks the channel and the CAN ID pair for one XCP session. Validation happens here so the
/// caller never has to deal with a half-valid result.
/// </summary>
public partial class XcpSessionDialog : Window
{
    private IReadOnlyDictionary<string, (uint Req, uint Rsp)> _existing =
        new Dictionary<string, (uint, uint)>();

    public string Channel { get; private set; } = "";
    public uint RequestId { get; private set; }
    public uint ResponseId { get; private set; }

    private XcpSessionDialog() => InitializeComponent();

    /// <summary>Returns the dialog on OK (read Channel/RequestId/ResponseId), or null on cancel.</summary>
    public static XcpSessionDialog? Ask(Window owner, IReadOnlyList<string> channels, string? selected,
                                        IReadOnlyDictionary<string, (uint Req, uint Rsp)> existing)
    {
        var dlg = new XcpSessionDialog { Owner = owner, _existing = existing };
        dlg.ChannelCombo.ItemsSource = channels;
        int index = channels.ToList().FindIndex(c => c.Equals(selected, StringComparison.OrdinalIgnoreCase));
        dlg.ChannelCombo.SelectedIndex = index >= 0 ? index : (channels.Count > 0 ? 0 : -1);
        dlg.ChannelCombo.SelectionChanged += dlg.Channel_Changed;
        dlg.ShowSessionFor(dlg.ChannelCombo.SelectedItem as string);
        return dlg.ShowDialog() == true ? dlg : null;
    }

    /// <summary>
    /// Shows what is already configured on the selected channel. Without this a 2-port master
    /// looks like it has one session, because whatever was typed for CAN1 stays on screen when
    /// the channel switches to CAN2.
    /// </summary>
    private void ShowSessionFor(string? channel)
    {
        if (channel is not null && _existing.TryGetValue(channel, out var ids))
        {
            ReqBox.Text = ids.Req.ToString("X");
            RspBox.Text = ids.Rsp.ToString("X");
        }
        else if (ReqBox.Text.Length == 0)
        {
            ReqBox.Text = "701";
            RspBox.Text = "702";
        }
    }

    private void Channel_Changed(object sender, SelectionChangedEventArgs e) =>
        ShowSessionFor(ChannelCombo.SelectedItem as string);

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        ReqBox.Focus();
        ReqBox.SelectAll();
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (ChannelCombo.SelectedItem is not string channel)
                throw new InvalidOperationException("No channel to configure — connect first.");
            uint req = ParseHexId(ReqBox.Text);
            uint rsp = ParseHexId(RspBox.Text);
            if (req == rsp) throw new InvalidOperationException("Request and response IDs must differ.");

            Channel = channel;
            RequestId = req;
            ResponseId = rsp;
            DialogResult = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "XCP session", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static uint ParseHexId(string text)
    {
        string s = text.Trim();
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) s = s[2..];
        if (!uint.TryParse(s, NumberStyles.HexNumber, null, out uint id))
            throw new InvalidOperationException($"'{text}' is not a hex CAN ID.");
        return id;
    }
}
