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

    /// <summary>
    /// Frozen copy shown while the user is scrolled away from the tail. One instance, reused:
    /// WPF keeps a view per collection handed to ItemsSource, and each view holds its rows
    /// alive, so a fresh collection per scroll would leak the whole history every time.
    /// </summary>
    private readonly TraceBuffer _heldRows = new();
    private bool _held;
    private ScrollViewer? _traceScroll;
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

    /// <summary>True while the user is reading back through history and the view is held still.</summary>
    public bool IsScrolledBack => _held;

    public int TraceCount => _traceRows.Count;

    /// <summary>Rows currently held, oldest first.</summary>
    public IEnumerable<TraceRow> TraceRows => _traceRows.Cast<TraceRow>();

    /// <summary>How many trace rows this pane keeps before overwriting the oldest.</summary>
    public void SetHistoryCapacity(int capacity)
    {
        GoLive();
        _traceRows.Resize(capacity);
    }

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
    /// Publishes the rows appended during this tick. Skipped entirely while the trace list is
    /// hidden — an invisible list still costs a full re-read on every notification — and while
    /// the user is reading back through history.
    /// </summary>
    public void AfterAppend(bool autoScroll)
    {
        if (!TraceList.IsVisible || _held) return;
        if (!_traceRows.Flush()) return;
        if (autoScroll && _traceRows.Last is { } last) TraceList.ScrollIntoView(last);
    }

    // ---------- reading back through history ----------

    /// <summary>
    /// Detects the user leaving the tail of the list and holds the view still while they read.
    /// Refreshing under them is not usable: rows age out of the circular buffer, so whatever
    /// they were looking at slides away a little more on every tick.
    /// </summary>
    private void Trace_ScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        _traceScroll ??= e.OriginalSource as ScrollViewer;

        // A refresh changes the extent; only a change with a stable extent is the user's doing.
        if (e.ExtentHeightChange != 0) return;

        bool atEnd = e.VerticalOffset >= e.ExtentHeight - e.ViewportHeight - 0.5;
        if (atEnd != _held) return;

        if (atEnd) GoLive();
        else EnterScrollback(e.VerticalOffset);
    }

    private void EnterScrollback(double offset)
    {
        // A detached copy: the live buffer keeps being overwritten while they read.
        _heldRows.CopyFrom(_traceRows);
        _held = true;
        TraceList.ItemsSource = _heldRows;
        _traceScroll?.ScrollToVerticalOffset(offset);
        LiveButton.Visibility = Visibility.Visible;
    }

    private void GoLive()
    {
        if (!_held) return;
        _held = false;
        TraceList.ItemsSource = _traceRows;
        _heldRows.Clear();          // release the rows the frozen copy was pinning
        _traceRows.Flush();
        LiveButton.Visibility = Visibility.Collapsed;
        if (_traceRows.Last is { } last) TraceList.ScrollIntoView(last);
    }

    private void Live_Click(object sender, RoutedEventArgs e) => GoLive();

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
        GoLive();               // a snapshot of rows that no longer exist would be a lie
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
        SelectionChanged?.Invoke();
    }
}
