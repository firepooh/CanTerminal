namespace CanTerminal.Core;

/// <summary>
/// Central pub/sub point for all observed frames. Adapters publish here; the UI,
/// the TCP API server, and loggers subscribe. Keeps a bounded ring buffer so late
/// consumers (MCP "recent" queries) can look back without their own storage.
/// </summary>
public sealed class MessageHub
{
    private readonly object _lock = new();
    private CanFrame[] _ring;
    private readonly Dictionary<string, long> _byChannel = [];
    private int _head;      // next write index
    private int _count;     // filled slots
    private long _total;

    public MessageHub(int capacity = 200_000)
    {
        _ring = new CanFrame[capacity];
    }

    public event Action<CanFrame>? FrameObserved;

    /// <summary>
    /// Optional protocol annotator. Runs under the ring lock so that stateful decoders (XCP)
    /// see every frame exactly once, in the same order they are stored and dispatched.
    /// </summary>
    public Func<CanFrame, FrameAnnotation?>? Annotator { get; set; }

    public long TotalFrames => Interlocked.Read(ref _total);

    public void Publish(CanFrame frame)
    {
        lock (_lock)
        {
            frame.Annotation = Annotator?.Invoke(frame);
            _ring[_head] = frame;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
            _byChannel[frame.Channel] = _byChannel.TryGetValue(frame.Channel, out long n) ? n + 1 : 1;
        }
        Interlocked.Increment(ref _total);
        FrameObserved?.Invoke(frame);
    }

    public void Clear()
    {
        lock (_lock)
        {
            // Wiping the slots as well as the indices. Resetting _count alone hides the frames
            // from every reader but keeps the whole ring — up to 200,000 frames and their
            // payloads — reachable from this array, so the memory Clear is expected to give
            // back was still held until the ring wrapped all the way round again.
            Array.Clear(_ring);
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>
    /// Snapshot of frames observed per channel. Like <see cref="TotalFrames"/> these count
    /// everything seen since start and are not reset by <see cref="Clear"/>, so callers can
    /// difference them to get a rate.
    /// </summary>
    public Dictionary<string, long> ChannelTotals()
    {
        lock (_lock) return new Dictionary<string, long>(_byChannel);
    }

    /// <summary>
    /// Resizes the ring, discarding whatever it held.
    ///
    /// Sized to a file's frame count, the ring never wraps — which is what lets a whole log live
    /// here without a second storage path, and lets every reader (GetRecent, Snapshot, the TCP
    /// API's recent) go on working unchanged.
    /// </summary>
    public void SetCapacity(int capacity)
    {
        if (capacity < 1) capacity = 1;
        lock (_lock)
        {
            _ring = new CanFrame[capacity];
            _head = 0;
            _count = 0;
        }
    }

    /// <summary>Everything held, oldest first.</summary>
    public CanFrame[] Snapshot()
    {
        lock (_lock)
        {
            var result = new CanFrame[_count];
            for (int i = 0; i < _count; i++)
                result[i] = _ring[(_head - _count + i + _ring.Length) % _ring.Length];
            return result;
        }
    }

    /// <summary>
    /// Loads a whole capture at once — a file being opened, not a bus delivering.
    ///
    /// <see cref="FrameObserved"/> is deliberately not raised. Every subscriber to it is built
    /// for bus rates: the display queue would take half a million frames in one go and its
    /// backlog guard would silently drop most of them, and a subscribed API client would be sent
    /// the entire file as individual pushes. The caller shows the result once the load is done.
    /// </summary>
    public void PublishBulk(IEnumerable<CanFrame> frames)
    {
        lock (_lock)
        {
            foreach (var frame in frames)
            {
                frame.Annotation = Annotator?.Invoke(frame);
                _ring[_head] = frame;
                _head = (_head + 1) % _ring.Length;
                if (_count < _ring.Length) _count++;
                _byChannel[frame.Channel] = _byChannel.TryGetValue(frame.Channel, out long n) ? n + 1 : 1;
                Interlocked.Increment(ref _total);
            }
        }
    }

    /// <summary>
    /// Runs the annotator over everything held again, oldest first — for when the database or the
    /// protocol profile changed after the frames arrived.
    ///
    /// The order is not a preference. A stateful decoder resolves a DAQ frame's identifier from
    /// the configuration commands that came before it, so a single pass in capture order is the
    /// only one that produces the same reading the live path would have.
    /// </summary>
    public void Reannotate()
    {
        if (Annotator is not { } annotator) return;
        lock (_lock)
        {
            for (int i = 0; i < _count; i++)
            {
                var frame = _ring[(_head - _count + i + _ring.Length) % _ring.Length];
                if (frame is not null) frame.Annotation = annotator(frame);
            }
        }
    }

    /// <summary>Newest-last snapshot of up to <paramref name="count"/> frames matching the filter.</summary>
    public List<CanFrame> GetRecent(int count, Func<CanFrame, bool>? filter = null)
    {
        var result = new List<CanFrame>(Math.Min(count, 4096));
        lock (_lock)
        {
            // walk backwards from newest until we have enough
            for (int i = 0; i < _count && result.Count < count; i++)
            {
                int idx = (_head - 1 - i + _ring.Length) % _ring.Length;
                var f = _ring[idx];
                if (filter is null || filter(f)) result.Add(f);
            }
        }
        result.Reverse();
        return result;
    }

    /// <summary>Wait until a frame matching <paramref name="predicate"/> is observed.</summary>
    public async Task<CanFrame?> WaitForAsync(Func<CanFrame, bool> predicate, TimeSpan timeout, CancellationToken ct = default)
    {
        var tcs = new TaskCompletionSource<CanFrame>(TaskCreationOptions.RunContinuationsAsynchronously);

        void Handler(CanFrame f)
        {
            if (predicate(f)) tcs.TrySetResult(f);
        }

        FrameObserved += Handler;
        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(timeout);
            try
            {
                return await tcs.Task.WaitAsync(cts.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return null;
            }
        }
        finally
        {
            FrameObserved -= Handler;
        }
    }
}
