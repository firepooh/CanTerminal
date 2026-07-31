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
    private volatile XcpDecoder? _xcp;

    public FrameAnnotator(DbcDecoder dbc) => _dbc = dbc;

    /// <summary>Active protocol profile decoder, or null for "None".</summary>
    public XcpDecoder? Xcp
    {
        get => _xcp;
        set => _xcp = value;
    }

    public string ProfileName => _xcp is null ? "none" : "xcp";

    public FrameAnnotation? Annotate(CanFrame f)
    {
        FrameAnnotation? protocol = null;
        try { protocol = _xcp?.Decode(f); }
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
