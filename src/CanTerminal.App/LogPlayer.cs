using CanTerminal.Core;

namespace CanTerminal.App;

/// <summary>
/// Plays a loaded capture back on its own clock.
///
/// Loading a file and showing every row at once answers "what is in this file". Replaying it
/// answers a different question — what the bus was doing, in the order and at the pace it
/// happened. Several things the views do only mean something under a moving clock: the aggregate
/// view's message count climbs, its period settles, and a byte that changes lights up as it
/// changes rather than being already faded by the time anyone looks.
///
/// The clock is virtual. It advances by real elapsed time times <see cref="Speed"/>, so the decay
/// of a change highlight is measured against replay time and a 10x replay fades highlights 10x
/// faster in wall time — which is what makes them still readable.
/// </summary>
public sealed class LogPlayer
{
    /// <summary>Speeds the transport offers. 0 means "as fast as the frames can be drawn".</summary>
    public static readonly double[] Speeds = [0.1, 0.25, 0.5, 1, 2, 5, 10, 50, 100, 0];

    private readonly CanFrame[] _frames;

    /// <summary>Index of the next frame to emit; equals Count once the file has played out.</summary>
    private int _next;

    public LogPlayer(IReadOnlyList<CanFrame> frames)
    {
        _frames = frames as CanFrame[] ?? frames.ToArray();
        Start = _frames.Length == 0 ? 0 : _frames[0].Timestamp;
        End = _frames.Length == 0 ? 0 : _frames[^1].Timestamp;
        Position = Start;
    }

    /// <summary>First and last timestamp in the file, on the file's own clock.</summary>
    public double Start { get; }

    public double End { get; }

    public double Duration => End - Start;

    /// <summary>Where the replay has got to, on the file's clock.</summary>
    public double Position { get; private set; }

    public bool IsPlaying { get; private set; }

    /// <summary>Multiplier on real time; 0 replays as fast as frames can be handed over.</summary>
    public double Speed { get; set; } = 1;

    public bool AtEnd => _next >= _frames.Length;

    public int EmittedCount => _next;

    public int TotalCount => _frames.Length;

    public void Play()
    {
        if (AtEnd) Rewind();
        IsPlaying = true;
    }

    public void Pause() => IsPlaying = false;

    public void Rewind()
    {
        _next = 0;
        Position = Start;
    }

    /// <summary>
    /// Moves to a moment on the file's clock. Returns the frames that have to be added to what is
    /// already shown, or null when the caller has to rebuild from nothing — seeking backwards
    /// cannot be done by appending.
    /// </summary>
    public ArraySegment<CanFrame>? SeekTo(double seconds, out bool rebuild)
    {
        seconds = Math.Clamp(seconds, Start, End);
        int target = FrameIndexAt(seconds);
        rebuild = target < _next;
        Position = seconds;

        if (rebuild)
        {
            _next = target;
            return null;
        }
        var gap = new ArraySegment<CanFrame>(_frames, _next, target - _next);
        _next = target;
        return gap;
    }

    /// <summary>Frames from the beginning up to the current position — what a rebuild has to replay.</summary>
    public ArraySegment<CanFrame> Played() => new(_frames, 0, _next);

    /// <summary>
    /// Advances the clock by <paramref name="realSeconds"/> of wall time and returns the frames
    /// that fall due.
    ///
    /// <paramref name="budget"/> caps how many are handed over in one go. Without it a replay at
    /// 100x hands the UI thread tens of thousands of frames per tick and the window stops
    /// answering — and the clock is then held back to match what was actually emitted, so the
    /// position never claims to be somewhere the display has not reached.
    /// </summary>
    public ArraySegment<CanFrame> Advance(double realSeconds, int budget)
    {
        if (!IsPlaying || AtEnd) return ArraySegment<CanFrame>.Empty;

        int from = _next;
        if (Speed <= 0)
        {
            // As fast as possible: the budget alone decides, and the clock follows the frames.
            int take = Math.Min(budget, _frames.Length - _next);
            _next += take;
            Position = _next > 0 ? _frames[_next - 1].Timestamp : Start;
        }
        else
        {
            double target = Position + (realSeconds * Speed);
            int limit = Math.Min(_frames.Length, _next + budget);
            while (_next < limit && _frames[_next].Timestamp <= target) _next++;

            // Held back when the budget ran out, so the position stays honest about what is shown.
            Position = _next < limit || _next >= _frames.Length
                ? Math.Min(target, End)
                : _frames[_next - 1].Timestamp;
        }

        if (AtEnd)
        {
            Position = End;
            IsPlaying = false;
        }
        return new ArraySegment<CanFrame>(_frames, from, _next - from);
    }

    /// <summary>Index of the first frame at or after <paramref name="seconds"/>.</summary>
    private int FrameIndexAt(double seconds)
    {
        int low = 0, high = _frames.Length;
        while (low < high)
        {
            int mid = (low + high) / 2;
            if (_frames[mid].Timestamp < seconds) low = mid + 1;
            else high = mid;
        }
        return low;
    }
}
