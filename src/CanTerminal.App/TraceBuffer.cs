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
///
/// Only the read side of IList is real; the list is a display projection, not a collection the
/// view may edit.
/// </summary>
public sealed class TraceBuffer : IList, INotifyCollectionChanged
{
    public const int MinCapacity = 100;
    public const int DefaultCapacity = 50_000;

    private TraceRow?[] _ring;
    private int _start;      // ring position of logical index 0
    private int _count;
    private bool _dirty;

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
            _ring[_start] = row;                       // the oldest slot is also the next free one
            _start = (_start + 1) % _ring.Length;
        }
        else
        {
            _ring[(_start + _count) % _ring.Length] = row;
            _count++;
        }
        _dirty = true;
    }

    /// <summary>
    /// Takes a detached copy of <paramref name="source"/>, so the view can be held still while
    /// the user reads back through history: the live buffer keeps being overwritten underneath,
    /// and anything still pointing at it would shift under them.
    ///
    /// Copying into an existing instance rather than handing back a fresh array is deliberate.
    /// Each distinct collection assigned to ItemsSource gets its own view from WPF, and those
    /// views keep the rows they reference alive; a new one per scroll leaks the whole history
    /// every time.
    /// </summary>
    public void CopyFrom(TraceBuffer source)
    {
        if (_ring.Length < source._count) _ring = new TraceRow?[source._count];
        for (int i = 0; i < source._count; i++)
            _ring[i] = source._ring[(source._start + i) % source._ring.Length];
        Array.Clear(_ring, source._count, _ring.Length - source._count);
        _start = 0;
        _count = source._count;
        _dirty = false;
        Reset();
    }

    /// <summary>Publishes everything appended since the last call. Returns false if nothing changed.</summary>
    public bool Flush()
    {
        if (!_dirty) return false;
        _dirty = false;
        Reset();
        return true;
    }

    public void Clear()
    {
        Array.Clear(_ring);
        _start = 0;
        _count = 0;
        _dirty = false;
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
        _dirty = false;
        Reset();
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
