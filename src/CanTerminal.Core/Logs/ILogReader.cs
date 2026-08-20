namespace CanTerminal.Core.Logs;

/// <summary>
/// A capture read back from a file.
/// </summary>
/// <param name="Frames">
/// In capture order. Stateful decoders are run over this exactly once, in this order, so a reader
/// that draws from several sources (an MDF4 file has one data group per bus) owes the merge.
/// </param>
/// <param name="StartWall">
/// Wall clock the capture started at, if the file says. <see cref="CanFrame.Timestamp"/> stays on
/// the file's own clock; this is only what turns it into a time of day.
/// </param>
/// <param name="StartWallIsApproximate">
/// True when the wall clock is a header date that does not line up with the first timestamp —
/// a continuation file split off a longer session, say. The reading is still useful; it just
/// must not be presented as exact.
/// </param>
/// <param name="SkippedLines">
/// Lines the reader did not understand. This is the whole safety net for a text format: a
/// grammar mismatch drops frames without raising anything, so the count has to reach the user.
/// A reader that always reports zero here has not earned the trust the number implies.
/// </param>
/// <param name="SkippedByShape">Count per kind of skipped line, so a pattern is visible.</param>
/// <param name="SkippedSamples">A few verbatim lines, so the user can see what was dropped.</param>
public sealed record LogFile(
    string Path,
    IReadOnlyList<CanFrame> Frames,
    IReadOnlyList<string> Channels,
    DateTime? StartWall,
    bool StartWallIsApproximate,
    double FirstTimestamp,
    double LastTimestamp,
    int SkippedLines,
    IReadOnlyDictionary<string, int> SkippedByShape,
    IReadOnlyList<string> SkippedSamples)
{
    public double Duration => Frames.Count == 0 ? 0 : LastTimestamp - FirstTimestamp;
}

/// <summary>
/// Reads a logged capture off disk.
///
/// A reader must decline a file it cannot read completely rather than return what it managed.
/// Half a capture is indistinguishable from a whole one once it is on screen, and this program
/// exists to say what was on the bus. The same rule the DBC decoder follows for a signal that
/// runs past the payload.
/// </summary>
public interface ILogReader
{
    /// <summary>Shown in the open dialog, e.g. "Vector ASCII log".</summary>
    string Description { get; }

    /// <summary>Open-dialog filter fragment, e.g. "Vector ASCII log (*.asc)|*.asc".</summary>
    string Filter { get; }

    bool CanRead(string path);

    /// <summary>
    /// Reads the whole file. Throws <see cref="OperationCanceledException"/> if cancelled —
    /// never a partial <see cref="LogFile"/>.
    /// </summary>
    LogFile Read(string path, IProgress<double>? progress = null, CancellationToken ct = default);
}

/// <summary>
/// The formats this build can open.
///
/// MDF4 is deliberately absent. The one MDF4 sample that carries frames is a byte-for-byte
/// re-encoding of an ASC file that is always present upstream of it, and the other is a
/// signal-only file with no frame channel at all — so a reader would add no readable data today
/// while bringing in a decoder whose failures are silent: MDF4 carries no checksum, and a
/// DZBLOCK read without the transposition step still satisfies every arithmetic invariant the
/// format offers while producing plausible nonsense. When MDF4 does arrive it belongs here as
/// one more entry, and any block it has not implemented must be refused by name rather than
/// decoded on a best effort.
/// </summary>
public static class LogReaders
{
    public static IReadOnlyList<ILogReader> All { get; } = [new AscLogReader()];

    public static ILogReader? For(string path) => All.FirstOrDefault(r => r.CanRead(path));

    public static string DialogFilter =>
        string.Join("|", All.Select(r => r.Filter)) + "|All files|*.*";
}
