using System.Windows;

namespace CanTerminal.App;

/// <summary>
/// How to point Claude at this program, on one screen.
///
/// <para>Registration <b>scope</b> (user / local / project) is a Claude Code concept that this
/// program gives no hint of anywhere, and it is the part people get wrong — registering into one
/// project folder and then wondering where the tools went. So it is explained here rather than
/// left to be found.</para>
///
/// <para>The address and the command are filled from the <b>live settings</b>, never from a
/// constant in the text: printing 5400 to someone who moved the port would be telling them
/// something untrue on the screen that exists to inform them.</para>
/// </summary>
public partial class McpGuideDialog : Window
{
    private readonly string _command;

    /// <param name="endpoint">The running endpoint, or null when it is switched off.</param>
    /// <param name="port">The configured port, used when nothing is listening yet.</param>
    public McpGuideDialog(Window? owner, string? endpoint, int port)
    {
        InitializeComponent();
        Owner = owner;

        // The guide has to read correctly before the endpoint is switched on — that is when
        // somebody is most likely to be reading it.
        UrlBox.Text = endpoint ?? $"http://127.0.0.1:{port}/mcp";
        _command = $"claude mcp add --transport http canterminal http://127.0.0.1:{port}/mcp";
        CommandBox.Text = _command;

        // Grows with its content up to the screen, then scrolls.
        MaxHeight = SystemParameters.WorkArea.Height - 80;
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_command); }
        catch (Exception ex)
        {
            MessageBox.Show(this, ex.Message, "MCP 사용 안내", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }
}
