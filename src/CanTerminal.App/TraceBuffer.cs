using System.Collections;
using System.Collections.Specialized;

namespace CanTerminal.App;

/// <summary>
/// Fixed-capacity circular buffer of trace rows, bound directly to the trace list.
///
/// Two things make this different from an ObservableCollection, and both matter at bus speed:
///
/// * Rows are overwritten in place once the buffer is full. There is no reallocation and the
///   ItemsSource is never swapped, so the list never tears down and rebuilds itself.
/// * Appending is silent. The view is told once per UI tick via <see cref="Flush"/> instead of
///   once per frame — a per-frame notification storm is what actually stalls the UI, and no
///   one can read a list that redraws thousands of times a second anyway.
/// * That tick reports what actually changed (Remove from the front, Add at the tail) rather
///   than a Reset. A Reset means "assume nothing", so WPF throws away every realised container
///   and rebuilds the viewport from scratch — measured at roughly 15 ms each, 20 times a second,
///   whether five rows arrived or five thousand. Reporting the real change keeps container
///   recycling alive and makes the cost proportional to the new rows again.
///
/// Only the read side of IList is real; the list is a display projection, not a collection the
/// view may edit.
/// </summary>
public sealed class TraceBuffer : IList, INotifyCollectionChanged
{
    public const int MinCapacity = 100;
    public const int DefaultCapacity = 50_000;

    /// <summary>
    /// How many appended rows are still worth reporting one at a time. Past this the whole
    /// viewport is being replaced anyway, so the single Reset that a granular batch was meant
    /// to avoid becomes the cheaper of the two.
    /// </summary>
    private const int GranularLimit = 512;

    private TraceRow?[] _ring;
    private int _start;      // ring position of logical index 0
    private int _count;

    // Pending since the last Flush. Every Add counts; once the ring is full an Add also drops
    // the oldest row, and the view has to be told about that too.
    private int _added;
    private int _evicted;
    private readonly List<TraceRow> _evictedRows = [];

    public TraceBuffer(int capacity = DefaultCapacity) => _ring = new TraceRow?[Math.Max(MinCapacity, capacity)];

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    public int Capacity => _ring.Length;

    public int Count => _count;

    public TraceRow? Last => _count == 0 ? null : (TraceRow?)this[_count - 1];

    /// <summary>Appends a row, dropping the oldest once full. Raises nothing — see <see cref="Flush"/>.</summary>
    public void Add(TraceRow row)
    {
        if (_count == _ring.Length)
        {
            var outgoing = _ring[_start];              // the oldest slot is also the next free one
            _ring[_start] = row;
            _start = (_start + 1) % _ring.Length;
            _evicted++;
            // Kept only while a granular batch is still possible; the Remove event has to carry
            // the row that left, and the ring no longer holds it.
            if (outgoing is not null && _evictedRows.Count < GranularLimit) _evictedRows.Add(outgoing);
        }
        else
        {
            _ring[(_start + _count) % _ring.Length] = row;
            _count++;
        }
        _added++;
    }

    /// <summary>Publishes everything appended since the last call. Returns false if nothing changed.</summary>
    public bool Flush()
    {
        if (_added == 0) return false;
        int added = _added, evicted = _evicted;
        _added = 0;
        _evicted = 0;

        var handler = CollectionChanged;
        if (handler is null)
        {
            _evictedRows.Clear();
            return true;
        }

        // Fall back to Reset when a granular batch would be the more expensive of the two, or
        // when Add stopped recording the evicted rows because the batch had already grown past
        // the point of being worth reporting one by one.
        if (added > GranularLimit || _evictedRows.Count < evicted)
        {
            _evictedRows.Clear();
            Reset();
            return true;
        }

        // Removals first, each at index 0: the rows behind shift down by one, which is exactly
        // what dropping the oldest row of a full ring does.
        for (int i = 0; i < evicted; i++)
            handler(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Remove, _evictedRows[i], 0));
        _evictedRows.Clear();

        // Then the new tail. After the removals the view holds _count - added rows, so these
        // indices land exactly where the appended rows now are.
        for (int i = _count - added; i < _count; i++)
            handler(this, new NotifyCollectionChangedEventArgs(
                NotifyCollectionChangedAction.Add, _ring[(_start + i) % _ring.Length], i));
        return true;
    }

    public void Clear()
    {
        Array.Clear(_ring);
        _start = 0;
        _count = 0;
        ClearPending();
        Reset();
    }

    /// <summary>
    /// Changes how much history is kept, preserving the newest rows. The instance stays the
    /// same so the binding does not have to be re-established.
    /// </summary>
    public void Resize(int capacity)
    {
        capacity = Math.Max(MinCapacity, capacity);
        if (capacity == _ring.Length) return;

        var kept = new TraceRow?[capacity];
        int keep = Math.Min(_count, capacity);
        for (int i = 0; i < keep; i++)
            kept[i] = _ring[(_start + _count - keep + i) % _ring.Length];

        _ring = kept;
        _start = 0;
        _count = keep;
        ClearPending();
        Reset();
    }

    /// <summary>
    /// Replaces every row with the result of <paramref name="map"/> and re-publishes. Used when
    /// a column has to be re-read on a different basis — the rows are pre-formatted, so there
    /// is nothing to recompute except the one field that changed.
    /// </summary>
    public void Rebuild(Func<TraceRow, TraceRow> map)
    {
        for (int i = 0; i < _count; i++)
        {
            int slot = (_start + i) % _ring.Length;
            if (_ring[slot] is { } row) _ring[slot] = map(row);
        }
        ClearPending();
        Reset();
    }

    /// <summary>
    /// Re-publishes the collection in full and drops any unreported batch. Needed wherever the
    /// view may have stopped tracking the buffer — while the list was hidden, or while it was
    /// bound to the held snapshot instead. Reporting only the pending batch there would be
    /// wrong twice over: the view has missed changes it was never told about, and rebinding may
    /// already have re-read some of them.
    /// </summary>
    public void Resync()
    {
        ClearPending();
        Reset();
    }

    /// <summary>Drops the unreported batch — the wholesale Reset that follows supersedes it.</summary>
    private void ClearPending()
    {
        _added = 0;
        _evicted = 0;
        _evictedRows.Clear();
    }

    private void Reset() =>
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));

    // ---------- IList (read side) ----------

    public object? this[int index]
    {
        get => (uint)index < (uint)_count
            ? _ring[(_start + index) % _ring.Length]
            : throw new ArgumentOutOfRangeException(nameof(index));
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => true;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot => this;

    public bool Contains(object? value) => IndexOf(value) >= 0;

    public int IndexOf(object? value)
    {
        for (int i = 0; i < _count; i++)
            if (ReferenceEquals(_ring[(_start + i) % _ring.Length], value)) return i;
        return -1;
    }

    public void CopyTo(Array array, int index)
    {
        for (int i = 0; i < _count; i++)
            array.SetValue(_ring[(_start + i) % _ring.Length], index + i);
    }

    public IEnumerator GetEnumerator()
    {
        for (int i = 0; i < _count; i++)
            yield return _ring[(_start + i) % _ring.Length];
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
