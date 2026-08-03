using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using CanTerminal.Core;

namespace CanTerminal.App;

/// <summary>
/// One view onto the capture: a trace list and an aggregate grid over a single channel (or all
/// of them). MainWindow owns the frame queue and hands frames to every pane, so two panes can
/// show two channels side by side without either one owning the data.
/// </summary>
public partial class ChannelPane : UserControl
{
    /// <summary>Channel combo entry meaning "no filter".</summary>
    public const string AllChannels = "All";

    /// <summary>Cached so the per-frame filter check never touches a dependency property.</summary>
    private string _channelFilter = AllChannels;

    private readonly TraceBuffer _traceRows = new();
    private readonly ObservableCollection<FixedRow> _fixedRows = [];
    // Group is part of the key so XCP splits one CAN ID into per-ODT rows; it is 0 otherwise.
    private readonly Dictionary<(string Chan, uint Id, bool Ext, int Group), FixedRow> _fixedMap = [];

    public ChannelPane()
    {
        InitializeComponent();
        TraceList.ItemsSource = _traceRows;
        FixedGrid.ItemsSource = _fixedRows;
        ChannelCombo.ItemsSource = new[] { AllChannels };
        ChannelCombo.SelectedIndex = 0;
    }

    /// <summary>Raised when the user changes what this pane shows, so the host can relabel.</summary>
    public event Action? SelectionChanged;

    public string SelectedChannel => _channelFilter;

    public bool ShowsTrace => ViewCombo.SelectedIndex == 0;

    /// <summary>True while the aggregate grid is the visible view (fade ticks are pointless otherwise).</summary>
    public bool FixedVisible => FixedGrid.IsVisible;

    public int TraceCount => _traceRows.Count;

    /// <summary>The channel entries this pane can be pointed at, "All" first.</summary>
    public IReadOnlyList<string> ChannelItems =>
        (ChannelCombo.ItemsSource as IEnumerable<string>)?.ToList() ?? [AllChannels];

    /// <summary>Points the pane at a channel. Used by the View menu; ignores names it does not have.</summary>
    public void SelectChannel(string channel)
    {
        int index = ChannelItems.ToList().FindIndex(c => c.Equals(channel, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) ChannelCombo.SelectedIndex = index;
    }

    /// <summary>Switches between the trace list and the aggregate grid.</summary>
    public void SetTraceView(bool trace) => ViewCombo.SelectedIndex = trace ? 0 : 1;

    /// <summary>Scrolls to the newest row. While live the pane is there anyway; this is for
    /// returning after browsing a paused capture.</summary>
    public void JumpToLive()
    {
        if (_traceRows.Last is { } last) TraceList.ScrollIntoView(last);
    }

    /// <summary>Rows currently held, oldest first.</summary>
    public IEnumerable<TraceRow> TraceRows => _traceRows.Cast<TraceRow>();

    /// <summary>How many trace rows this pane keeps before overwriting the oldest.</summary>
    public void SetHistoryCapacity(int capacity) => _traceRows.Resize(capacity);

    /// <summary>
    /// Offers the channel list from the connected adapter. The current selection is kept when it
    /// is still open, so reconnecting does not silently retarget a pane.
    /// </summary>
    public void SetChannels(IReadOnlyList<string> channels, string? prefer = null)
    {
        string previous = SelectedChannel;
        var items = new List<string> { AllChannels };
        items.AddRange(channels);
        ChannelCombo.ItemsSource = items;

        string wanted = prefer is not null && items.Contains(prefer, StringComparer.OrdinalIgnoreCase)
            ? prefer
            : previous;
        int index = items.FindIndex(c => c.Equals(wanted, StringComparison.OrdinalIgnoreCase));
        ChannelCombo.SelectedIndex = index >= 0 ? index : 0;
    }

    public bool Accepts(CanFrame f) =>
        ReferenceEquals(_channelFilter, AllChannels) ||
        f.Channel.Equals(_channelFilter, StringComparison.OrdinalIgnoreCase);

    public void Append(CanFrame f, double now, bool highlight)
    {
        _traceRows.Add(TraceRow.From(f));
        UpdateFixed(f, now, highlight);
    }

    private void UpdateFixed(CanFrame f, double now, bool highlight)
    {
        var key = (f.Channel, f.ArbId, f.IsExtended, f.Annotation?.GroupKey ?? 0);
        if (_fixedMap.TryGetValue(key, out var row))
        {
            row.Update(f, now, highlight);
        }
        else
        {
            row = new FixedRow(f);
            _fixedMap[key] = row;
            _fixedRows.Add(row);
        }
    }

    /// <summary>
    /// Publishes the rows appended during this tick and keeps the view on the newest row.
    ///
    /// While the capture is running the list always sits at the tail — reading back through it
    /// is what Pause is for. That is not only how Vehicle Spy behaves, it is the only honest
    /// option: rows age out of the circular buffer continuously, so a view parked in the middle
    /// of a live capture slides away from whatever the reader was looking at. The alternative,
    /// freezing a snapshot under them, is what this pane used to do — and it cost a memory leak
    /// and a crash to keep working.
    ///
    /// Nothing is published while the list is hidden; <see cref="Pane_Changed"/> resynchronises
    /// the view in full when it comes back.
    /// </summary>
    public void AfterAppend()
    {
        if (!TraceList.IsVisible) return;
        if (!_traceRows.Flush()) return;
        if (_traceRows.Last is { } last) TraceList.ScrollIntoView(last);
    }

    public void TickFade(double now)
    {
        foreach (var row in _fixedRows) row.TickFade(now);
    }

    public void ClearHighlights()
    {
        foreach (var row in _fixedRows) row.ClearHighlight();
    }

    /// <summary>
    /// Drops the aggregate rows. Required whenever the grouping or filtering basis changes: rows
    /// collected under the old basis stop updating and would read as live traffic.
    /// </summary>
    public void ResetFixed()
    {
        _fixedRows.Clear();
        _fixedMap.Clear();
    }

    public void ClearAll()
    {
        _traceRows.Clear();
        ResetFixed();
    }

    public void UpdateStats(string text) => PaneStats.Text = text;

    private void Pane_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (TraceList is null || FixedGrid is null) return; // fires during InitializeComponent
        bool trace = ShowsTrace;
        TraceList.Visibility = trace ? Visibility.Visible : Visibility.Collapsed;
        FixedGrid.Visibility = trace ? Visibility.Collapsed : Visibility.Visible;

        string selected = ChannelCombo.SelectedItem as string ?? AllChannels;
        // Keep the "no filter" sentinel reference-comparable in the per-frame hot path.
        _channelFilter = selected == AllChannels ? AllChannels : selected;

        // The channel filter changes which frames belong here, so the aggregate has to restart.
        if (e.OriginalSource == ChannelCombo) ClearAll();
        // Rows kept arriving while the list was hidden and nothing was published; WPF measures
        // it as soon as it is shown, and it must not be measured against a stale view.
        else if (trace) _traceRows.Resync();
        SelectionChanged?.Invoke();
    }
}
