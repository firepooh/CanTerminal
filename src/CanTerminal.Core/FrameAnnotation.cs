using CanTerminal.Core.Xcp;

namespace CanTerminal.Core;

/// <summary>
/// Protocol-level interpretation of a frame: a short frame type and a detail comment.
/// Attached once by <see cref="MessageHub"/> at publish time so the UI, the TCP API and
/// CSV export all show the same text (stateful decoders must see each frame exactly once).
/// </summary>
/// <param name="GroupKey">
/// Distinguishes logical messages that share one CAN ID, so aggregate views can split them
/// into separate rows. XCP needs this: every DAQ-DTO and every CTO response of a session
/// travel on the same response ID, and merging them into one row makes the period and the
/// data column meaningless. 0 (the default) means "do not split".
/// </param>
public sealed record FrameAnnotation(string? Type, string? Comment, int GroupKey = 0);

/// <summary>
/// Combines the DBC decoder with the optional protocol profile (currently XCP).
/// Never throws: a decoder fault degrades to "no annotation" rather than killing the RX path.
/// </summary>
public sealed class FrameAnnotator
{
    private readonly DbcDecoder _dbc;
    private volatile XcpDecoder[] _xcp = [];

    public FrameAnnotator(DbcDecoder dbc) => _dbc = dbc;

    /// <summary>
    /// Active protocol sessions, empty for profile "None". A multi-channel device commonly runs
    /// an independent XCP session per channel (different CAN ID pairs), so this is a list:
    /// each decoder carries its own configuration and its own session state.
    /// </summary>
    public IReadOnlyList<XcpDecoder> XcpSessions
    {
        get => _xcp;
        set => _xcp = value is null ? [] : [.. value];
    }

    public string ProfileName => _xcp.Length == 0 ? "none" : "xcp";

    /// <summary>True when any configured session owns this frame. Used by the display filter.</summary>
    public bool IsProtocolFrame(CanFrame f)
    {
        foreach (var s in _xcp)
            if (s.Matches(f)) return true;
        return false;
    }

    public FrameAnnotation? Annotate(CanFrame f)
    {
        FrameAnnotation? protocol = null;
        try
        {
            // Sessions are disjoint in practice (different channels or ID pairs), so the first
            // one to claim the frame is the one that owns it.
            foreach (var s in _xcp)
            {
                protocol = s.Decode(f);
                if (protocol is not null) break;
            }
        }
        catch { /* a decoder bug must not stop frame capture */ }

        string? dbc = null;
        if (_dbc.IsLoaded)
        {
            try { dbc = _dbc.Decode(f); }
            catch { }
        }

        if (protocol is null) return dbc is null ? null : new FrameAnnotation(null, dbc);
        if (dbc is null) return protocol;
        return protocol with { Comment = Join(protocol.Comment, dbc) };
    }

    private static string Join(string? a, string b) => string.IsNullOrEmpty(a) ? b : $"{a}  |  {b}";
}
