using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using CanTerminal.Core;

namespace CanTerminal.App;

/// <summary>
/// One view onto the capture: a trace list and an aggregate grid over a single channel (or all
/// of them). MainWindow owns the frame queue and hands frames to every pane, so two panes can
/// show two channels side by side without either one owning the data.
/// </summary>
public partial class ChannelPane : UserControl
{
    /// <summary>What kind of traffic a pane is narrowed to, on top of its channel.</summary>
    public enum PaneContent { All, XcpCommands, XcpData }

    /// <summary>Channel combo entry meaning "no filter".</summary>
    public const string AllChannels = "All";

    /// <summary>Cached so the per-frame filter check never touches a dependency property.</summary>
    private string _channelFilter = AllChannels;

    /// <summary>
    /// Supplied by the host, which is the side that knows the protocol sessions. Null means
    /// "everything", so the common case costs one null check per frame.
    /// </summary>
    private Func<CanFrame, bool>? _contentFilter;

    /// <summary>How the Time column reads, and the anchor the host set for it.</summary>
    private TimeBase _time = new(TimestampMode.Relative, 0, DateTime.Now);

    /// <summary>Previous row's timestamp, for the delta reading. NaN until the first row.</summary>
    private double _previousTs = double.NaN;

    /// <summary>The size the XAML column widths were laid out for.</summary>
    private const double BaseFontSize = 12;

    private readonly Dictionary<GridViewColumn, double> _baseTraceWidths = [];
    private readonly Dictionary<DataGridColumn, double> _baseFixedWidths = [];

    private readonly TraceBuffer _traceRows = new();
    private readonly ObservableCollection<FixedRow> _fixedRows = [];
    // Group is part of the key so XCP splits one CAN ID into per-ODT rows; it is 0 otherwise.
    private readonly Dictionary<(string Chan, uint Id, bool Ext, int Group), FixedRow> _fixedMap = [];

    public ChannelPane()
    {
        InitializeComponent();
        TraceList.ItemsSource = _traceRows;
        FixedGrid.ItemsSource = _fixedRows;

        // The ID column advertises an ascending sort in XAML; without this the grid was in
        // insertion order and the glyph was simply false — and the first click on the header
        // then "toggled" to descending. SortKey rather than Id so the per-ODT rows XCP splits
        // out of one CAN ID stay grouped underneath it.
        CollectionViewSource.GetDefaultView(_fixedRows).SortDescriptions
            .Add(new SortDescription(nameof(FixedRow.SortKey), ListSortDirection.Ascending));
        ChannelCombo.ItemsSource = new[] { AllChannels };
        ChannelCombo.SelectedIndex = 0;

        // Captured before the Sender column is taken out, so it scales with the rest when it
        // comes back. Column widths are in pixels, so they have to follow the text size or the
        // columns clip the moment you zoom in.
        foreach (var column in ((GridView)TraceList.View).Columns) _baseTraceWidths[column] = column.Width;
        foreach (var column in FixedGrid.Columns)
            if (column.Width.IsAbsolute) _baseFixedWidths[column] = column.Width.Value;

        ShowSenderColumn(false);        // only a two-sided protocol has anything to put in it
    }

    /// <summary>Raised for Ctrl+wheel. The host owns the text size, because both panes share it.</summary>
    public event Action<int>? ZoomRequested;

    private void Pane_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (Keyboard.Modifiers != ModifierKeys.Control) return;
        e.Handled = true;               // otherwise the list scrolls at the same time
        ZoomRequested?.Invoke(Math.Sign(e.Delta));
    }

    /// <summary>Sets the text size of both views, scaling the column widths with it.</summary>
    public void SetFontSize(double size)
    {
        TraceList.FontSize = size;
        FixedGrid.FontSize = size;

        double scale = size / BaseFontSize;
        foreach (var (column, width) in _baseTraceWidths) column.Width = width * scale;
        foreach (var (column, width) in _baseFixedWidths)
            column.Width = new DataGridLength(width * scale);
    }

    /// <summary>
    /// Shows or hides the Sender column. Plain CAN traffic has no sender to name — the column
    /// would be a stripe of blanks — so it only appears while a protocol profile fills it in.
    /// </summary>
    public void ShowSenderColumn(bool show)
    {
        var columns = ((GridView)TraceList.View).Columns;
        bool present = columns.Contains(SenderColumn);
        if (show == present) return;
        if (show) columns.Insert(columns.IndexOf(TypeColumn), SenderColumn);
        else columns.Remove(SenderColumn);
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

    /// <summary>Which kind of traffic this pane is narrowed to.</summary>
    public PaneContent ContentMode => (PaneContent)Math.Max(0, ContentCombo.SelectedIndex);

    public void SetContentMode(PaneContent content) => ContentCombo.SelectedIndex = (int)content;

    /// <summary>
    /// Installs the test behind the current <see cref="ContentMode"/> selection. The pane does not
    /// know what an XCP session is; the host does, so it hands the predicate down.
    /// </summary>
    public void SetContentFilter(Func<CanFrame, bool>? filter) => _contentFilter = filter;

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
        (ReferenceEquals(_channelFilter, AllChannels) ||
         f.Channel.Equals(_channelFilter, StringComparison.OrdinalIgnoreCase)) &&
        (_contentFilter is null || _contentFilter(f));

    /// <summary>
    /// Sets how the Time column reads and re-reads the rows already on screen. Delta is measured
    /// against the previous row *of this pane*, which is why it is the pane and not the row
    /// factory that owns it — two panes filtered differently see different neighbours.
    /// </summary>
    public void SetTimeBase(TimeBase time)
    {
        bool changed = _time != time;
        _time = time;
        TimeColumn.Header = time.ColumnHeader;
        if (changed) _traceRows.Rebuild(row => row.WithTime(time));
    }

    public void Append(CanFrame f, double now, bool highlight)
    {
        _traceRows.Add(TraceRow.From(f, _time, _previousTs));
        _previousTs = f.Timestamp;
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
        _previousTs = double.NaN;   // the next row has nothing to be a delta from
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

        // Channel and content decide which frames belong here at all, so both have to restart
        // the pane: rows collected under the old basis stop updating and read as live traffic.
        if (e.OriginalSource == ChannelCombo || e.OriginalSource == ContentCombo) ClearAll();
        // Rows kept arriving while the list was hidden and nothing was published; WPF measures
        // it as soon as it is shown, and it must not be measured against a stale view.
        else if (trace) _traceRows.Resync();
        SelectionChanged?.Invoke();
    }
}
