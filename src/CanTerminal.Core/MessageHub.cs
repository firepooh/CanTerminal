namespace CanTerminal.Core;

/// <summary>
/// Central pub/sub point for all observed frames. Adapters publish here; the UI,
/// the TCP API server, and loggers subscribe. Keeps a bounded ring buffer so late
/// consumers (MCP "recent" queries) can look back without their own storage.
/// </summary>
public sealed class MessageHub
{
    private readonly object _lock = new();
    private readonly CanFrame[] _ring;
    private int _head;      // next write index
    private int _count;     // filled slots
    private long _total;

    public MessageHub(int capacity = 200_000)
    {
        _ring = new CanFrame[capacity];
    }

    public event Action<CanFrame>? FrameObserved;

    public long TotalFrames => Interlocked.Read(ref _total);

    public void Publish(CanFrame frame)
    {
        lock (_lock)
        {
            _ring[_head] = frame;
            _head = (_head + 1) % _ring.Length;
            if (_count < _ring.Length) _count++;
        }
        Interlocked.Increment(ref _total);
        FrameObserved?.Invoke(frame);
    }

    public void Clear()
    {
        lock (_lock)
        {
            _head = 0;
            _count = 0;
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
