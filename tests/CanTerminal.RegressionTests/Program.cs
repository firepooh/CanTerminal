using System.Collections.Specialized;
using System.Globalization;
using CanTerminal.App;
using CanTerminal.Core;
using CanTerminal.Core.Logs;
using CanTerminal.Core.Xcp;
// WPF's implicit usings bring in System.Windows.Shapes, whose Path is not this one.
using File = System.IO.File;
using Path = System.IO.Path;

namespace CanTerminal.RegressionTests;

/// <summary>
/// Guards the defects found in the August 2026 review. Every check here failed before its fix,
/// so each one is a specific thing that went wrong rather than a general assertion of health.
/// Plain console assertions, matching CanTerminal.SmokeTest — the repo has no test framework.
///
/// Run: dotnet run --project tests/CanTerminal.RegressionTests
/// </summary>
internal static class Program
{
    private static int _failures;

    [STAThread]
    private static int Main()
    {
        Section("TraceBuffer");
        GranularBatchLargerThanCapacityDoesNotThrow();
        FlushKeepsTheViewInStep();
        IndexOfFindsTheNewestRowFirst();

        Section("Payload limits");
        VirtualBusRejectsAnOversizedPayload();
        FormatDataSurvivesAnOversizedPayload();

        Section("ASC log reader");
        HeaderAndDataLinesBecomeFrames();
        TimestampsMatchDoubleParseExactly();
        BaseDecIsHonoured();
        TrailingMetadataIsNotEatenAsData();
        ASymbolicNameColumnDoesNotHideTheFrame();
        TxRequestsAreNotCountedTwice();
        WhatIsNotUnderstoodIsCounted();
        LineEndingsDoNotChangeTheFrameCount();

        Section("Log into the hub");
        BulkLoadDoesNotNotifyPerFrame();
        ReannotateRunsOncePerFrameInOrder();
        ChannelDbcOverridesTheGlobalOne();

        Section("Device selection");
        TheVirtualBusIsNeverChosenForYou();

        Section("MessageHub");
        ClearReleasesTheRing();

        Section("DBC");
        SignalsPastThePayloadAreOmitted();
        MotorolaSignalsAreNotOverSuppressed();

        Section("XCP");
        GetSlaveIdResponseIsDecoded();
        DownloadMaxHasNoElementCount();
        WordAlignedIdFieldSkipsTheFillByte();
        ErrorNamesTheSubCommand();

        Console.WriteLine();
        Console.WriteLine(_failures == 0 ? "ALL PASS" : $"{_failures} FAILURE(S)");
        return _failures == 0 ? 0 : 1;
    }

    // ---------------- TraceBuffer ----------------

    /// <summary>
    /// A tick that appends more rows than the buffer holds used to index the ring at a negative
    /// offset. Reachable because View > History size… accepts 100 rows, and the granular path
    /// only bails out above 512.
    /// </summary>
    private static void GranularBatchLargerThanCapacityDoesNotThrow()
    {
        foreach (int perTick in new[] { 101, 150, 200, 256, 511 })
        {
            var buffer = new TraceBuffer(100);
            buffer.CollectionChanged += (_, _) => { };
            try
            {
                for (int tick = 0; tick < 120; tick++)
                {
                    for (int i = 0; i < perTick; i++) buffer.Add(Row());
                    buffer.Flush();
                }
                Check($"{perTick} rows/tick into a 100-row buffer", true);
            }
            catch (Exception ex)
            {
                Check($"{perTick} rows/tick into a 100-row buffer", false, ex.GetType().Name);
            }
        }
    }

    /// <summary>
    /// The granular path has to leave a listener holding exactly as many rows as the buffer.
    /// A Remove or an Add at the wrong index throws out of WPF's generator instead, which is
    /// how the earlier bug surfaced in the app.
    /// </summary>
    private static void FlushKeepsTheViewInStep()
    {
        var buffer = new TraceBuffer(1000);
        int view = 0;
        buffer.CollectionChanged += (_, e) =>
        {
            view = e.Action switch
            {
                NotifyCollectionChangedAction.Add => view + e.NewItems!.Count,
                NotifyCollectionChangedAction.Remove => view - e.OldItems!.Count,
                _ => buffer.Count,      // Reset: the listener re-reads
            };
        };

        // Straddles every boundary that matters: filling up, exactly full, wrapping.
        foreach (int perTick in new[] { 300, 300, 300, 300, 7, 999, 1, 1500 })
        {
            for (int i = 0; i < perTick; i++) buffer.Add(Row());
            buffer.Flush();
            if (view != buffer.Count)
            {
                Check("view stays in step with the buffer", false, $"after {perTick}: view {view}, buffer {buffer.Count}");
                return;
            }
        }
        Check("view stays in step with the buffer", true);
    }

    private static void IndexOfFindsTheNewestRowFirst()
    {
        var buffer = new TraceBuffer(1000);
        for (int i = 0; i < 1000; i++) buffer.Add(Row());
        Check("IndexOf returns the last row's index", buffer.IndexOf(buffer.Last) == buffer.Count - 1);
    }

    // ---------------- payload limits ----------------

    /// <summary>
    /// The virtual bus used to accept any length the API sent it, and the display then sized a
    /// stack buffer from that length.
    /// </summary>
    private static void VirtualBusRejectsAnOversizedPayload()
    {
        using var adapter = new VirtualAdapter(generateTraffic: false, echoResponder: false);
        adapter.Open([new CanChannelConfig("CAN1")]);

        Check("classic CAN rejects 9 bytes", Throws(() => adapter.Send("CAN1", 0x123, new byte[9])));
        Check("CAN FD rejects 65 bytes", Throws(() => adapter.Send("CAN1", 0x123, new byte[65], fd: true)));
        Check("CAN FD still accepts 64", !Throws(() => adapter.Send("CAN1", 0x123, new byte[64], fd: true)));
        Check("classic CAN still accepts 8", !Throws(() => adapter.Send("CAN1", 0x123, new byte[8])));
    }

    private static void FormatDataSurvivesAnOversizedPayload()
    {
        // 256 KB used to overflow the UI thread's stack. Nothing should reach this any more,
        // which is exactly why the formatter must not be the thing that enforces it.
        string text = TraceRow.FormatData(new byte[256 * 1024]);
        Check("FormatData handles a payload no adapter would pass", text.Length == (256 * 1024 * 3) - 1);
    }

    // ---------------- ASC log reader ----------------

    private const string AscHeader = """
        date Thu Jan 2 09:14:24 2025
        base hex  timestamps absolute
        internal events logged
        // STM32H735G-DK CAN logger
        Begin Triggerblock Thu Jan 2 09:14:24 2025
           0.000000 Start of measurement
        """;

    private static LogFile ReadAsc(string body, string header = AscHeader, string newLine = "\r\n")
    {
        string path = Path.Combine(Path.GetTempPath(), $"canterminal_test_{Guid.NewGuid():N}.asc");
        File.WriteAllText(path, (header + "\n" + body).Replace("\r\n", "\n").Replace("\n", newLine));
        try { return new AscLogReader().Read(path); }
        finally { try { File.Delete(path); } catch { } }
    }

    private static void HeaderAndDataLinesBecomeFrames()
    {
        var log = ReadAsc("""
                 0.012700 1  18DAF110x  Rx   d 8 AD 21 00 00 00 00 00 00
                 0.101600 1  18FFA201x  Tx   d 2 FF 00
                 0.101100 2  123        Rx   d 3 01 02 03
            """);
        Check("three data lines give three frames", log.Frames.Count == 3, log.Frames.Count.ToString());
        if (log.Frames.Count != 3) return;

        var a = log.Frames[0];
        Check("extended id, direction and payload read correctly",
              a.ArbId == 0x18DAF110 && a.IsExtended && a.Direction == FrameDirection.Rx
              && a.Data.Length == 8 && a.DataText == "AD21000000000000", a.DataText);
        Check("Tx is recognised", log.Frames[1].Direction == FrameDirection.Tx);
        // ChannelPalette.Known holds CAN1..CAN4, so anything else would tint unstably.
        Check("channels are named CAN1 / CAN2",
              log.Frames[0].Channel == "CAN1" && log.Frames[2].Channel == "CAN2",
              $"{log.Frames[0].Channel},{log.Frames[2].Channel}");
        Check("a standard id is not marked extended", !log.Frames[2].IsExtended);
        Check("the header date is picked up", log.StartWall == new DateTime(2025, 1, 2, 9, 14, 24));
        Check("nothing was skipped", log.SkippedLines == 0, log.SkippedLines.ToString());
    }

    /// <summary>
    /// Reconstructing a timestamp as whole + fraction/10^d rounds twice and lands a bit away from
    /// what every other reader would produce, so the same instant stops comparing equal.
    /// </summary>
    private static void TimestampsMatchDoubleParseExactly()
    {
        string[] literals = ["0.012700", "1.0131", "1.1523", "300.114100", "480.678500", "17.000001"];
        var log = ReadAsc(string.Join("\n", literals.Select(t => $"   {t} 1  100  Rx   d 1 00")));
        bool same = log.Frames.Count == literals.Length;
        for (int i = 0; same && i < literals.Length; i++)
            same = log.Frames[i].Timestamp == double.Parse(literals[i], CultureInfo.InvariantCulture);
        Check("timestamps are bit-identical to double.Parse", same);
    }

    /// <summary>
    /// Under "base dec" the identifier and the payload are decimal. A reader that assumes hex
    /// reads 256 as 0x256 and turns eight decimal values into eleven bytes, without an error.
    /// </summary>
    private static void BaseDecIsHonoured()
    {
        string header = AscHeader.Replace("base hex", "base dec");
        var log = ReadAsc("  16.000000 1  256  Rx   d 4 17 34 51 68", header);
        Check("base dec reads the id as decimal",
              log.Frames.Count == 1 && log.Frames[0].ArbId == 256,
              log.Frames.Count == 1 ? log.Frames[0].ArbId.ToString() : "no frame");
    }

    private static void TrailingMetadataIsNotEatenAsData()
    {
        var log = ReadAsc("   5.000000 1  100  Rx   d 2 11 22 Length = 123 BitCount = 143");
        Check("exactly DLC bytes are taken",
              log.Frames.Count == 1 && log.Frames[0].DataText == "1122",
              log.Frames.Count == 1 ? log.Frames[0].DataText : "no frame");
    }

    /// <summary>
    /// A database export writes the message name between the identifier and the direction. A
    /// parser that counts columns drops every frame of such a file and reports nothing.
    /// </summary>
    private static void ASymbolicNameColumnDoesNotHideTheFrame()
    {
        var log = ReadAsc("   5.000000 1  100x  EngineData  Rx   d 3 11 22 33");
        Check("a symbolic name column is skipped, not the frame",
              log.Frames.Count == 1 && log.Frames[0].DataText == "112233",
              $"{log.Frames.Count} frames, {log.SkippedLines} skipped");
    }

    /// <summary>TxRq is a transmit request; the controller reports the frame again when it goes out.</summary>
    private static void TxRequestsAreNotCountedTwice()
    {
        var log = ReadAsc("""
                 1.000000 1  100  TxRq   d 1 AA
                 1.000100 1  100  Tx     d 1 AA
            """);
        Check("TxRq makes no frame", log.Frames.Count == 1, log.Frames.Count.ToString());
        Check("TxRq is reported rather than ignored",
              log.SkippedByShape.Keys.Any(k => k.Contains("TxRq")), string.Join(",", log.SkippedByShape.Keys));
    }

    /// <summary>
    /// The counter is the whole safety net for a text format: without it a grammar mismatch is
    /// indistinguishable from a file that simply had none of those events.
    /// </summary>
    private static void WhatIsNotUnderstoodIsCounted()
    {
        var log = ReadAsc("""
                 1.000000 1  100  Rx   d 1 AA
                 2.000000 1  ErrorFrame
                 2.100000 Statistic: D 0 R 0 XD 0 XR 0 E 0 O 0 B 0.0%
                 3.000000 CANFD   1 Rx  18DAF110x  1 0 8 8 11 22 33 44 55 66 77 88  100000 0 0 0 0 0
                 4.000000 1  100  Rx   d 9 11 22 33 44 55 66 77 88 99
            """);
        Check("the good frame still parses", log.Frames.Count == 1, log.Frames.Count.ToString());
        Check("four lines are reported as not understood", log.SkippedLines == 4, log.SkippedLines.ToString());
        Check("they are grouped by shape", log.SkippedByShape.Count >= 3, string.Join(" | ", log.SkippedByShape.Keys));
        Check("verbatim samples are kept", log.SkippedSamples.Count > 0);
        // A single malformed line must not abort a load of half a million good ones.
        Check("an over-long classic line is skipped, not thrown",
              log.SkippedByShape.Values.Sum() == 4);
    }

    private static void LineEndingsDoNotChangeTheFrameCount()
    {
        const string body = """
                 1.000000 1  100  Rx   d 1 AA
                 2.000000 2  200x Rx   d 2 BB CC
            """;
        var crlf = ReadAsc(body, newLine: "\r\n");
        var lf = ReadAsc(body, newLine: "\n");
        Check("CRLF and LF give the same frames",
              crlf.Frames.Count == 2 && lf.Frames.Count == 2
              && crlf.Frames[1].DataText == lf.Frames[1].DataText,
              $"crlf={crlf.Frames.Count} lf={lf.Frames.Count}");
    }

    // ---------------- log into the hub ----------------

    private static CanFrame LogFrame(double ts, string channel, uint id) =>
        new() { Timestamp = ts, Channel = channel, ArbId = id, Data = [1, 2, 3] };

    /// <summary>
    /// Every subscriber to FrameObserved is built for bus rates. Half a million frames in one go
    /// would overrun the display queue's backlog guard and flood a subscribed API client.
    /// </summary>
    private static void BulkLoadDoesNotNotifyPerFrame()
    {
        var hub = new MessageHub(capacity: 10);
        int observed = 0;
        hub.FrameObserved += _ => observed++;

        var frames = Enumerable.Range(0, 60_000).Select(i => LogFrame(i * 0.001, "CAN1", 0x100)).ToList();
        hub.SetCapacity(frames.Count);
        hub.PublishBulk(frames);

        Check("FrameObserved is not raised by a bulk load", observed == 0, observed.ToString());
        Check("every frame is held", hub.Snapshot().Length == 60_000, hub.Snapshot().Length.ToString());
        Check("recent() sees them all", hub.GetRecent(int.MaxValue).Count == 60_000);
        Check("the snapshot is oldest first",
              hub.Snapshot()[0].Timestamp == 0 && hub.Snapshot()[^1].Timestamp > 59);
    }

    /// <summary>
    /// A stateful decoder resolves a data frame from the commands before it, so re-reading has to
    /// be one pass in capture order — not whatever order the ring happens to be laid out in.
    /// </summary>
    private static void ReannotateRunsOncePerFrameInOrder()
    {
        var hub = new MessageHub(capacity: 8);
        var frames = Enumerable.Range(0, 2000).Select(i => LogFrame(i, "CAN1", (uint)i)).ToList();
        hub.SetCapacity(frames.Count);
        hub.PublishBulk(frames);

        var seen = new List<uint>();
        hub.Annotator = f => { seen.Add(f.ArbId); return new FrameAnnotation("t", $"#{f.ArbId}"); };
        hub.Reannotate();

        Check("each frame is annotated exactly once", seen.Count == 2000, seen.Count.ToString());
        Check("in capture order", seen.SequenceEqual(Enumerable.Range(0, 2000).Select(i => (uint)i)));
        Check("the annotation is actually replaced",
              hub.Snapshot()[7].Annotation?.Comment == "#7", hub.Snapshot()[7].Annotation?.Comment);
    }

    private static void ChannelDbcOverridesTheGlobalOne()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "probe.dbc");
        if (!File.Exists(path)) { Check("probe.dbc is next to the test binary", false, path); return; }

        var global = new DbcDecoder();
        var bound = new DbcDecoder();
        bound.Load(path);
        var annotator = new FrameAnnotator(global);

        var frame = new CanFrame { Channel = "CAN2", ArbId = 291, Data = [1, 2, 3, 4, 5, 6, 7, 8] };
        Check("with nothing loaded there is no comment", annotator.Annotate(frame) is null);

        annotator.ChannelDbc = new Dictionary<string, DbcDecoder> { ["CAN2"] = bound };
        Check("a bound channel uses its own database",
              annotator.Annotate(frame)?.Comment?.StartsWith("TestMsg") == true,
              annotator.Annotate(frame)?.Comment);

        var other = new CanFrame { Channel = "CAN1", ArbId = 291, Data = [1, 2, 3, 4, 5, 6, 7, 8] };
        Check("an unbound channel falls back to the global one, here empty",
              annotator.Annotate(other) is null, annotator.Annotate(other)?.Comment);
    }

    // ---------------- device selection ----------------

    /// <summary>
    /// A monitor that connects to a traffic generator on its own initiative cannot be trusted:
    /// the frames on screen look exactly like a capture. The virtual bus stays available and
    /// stays chosen once picked, but is never what a scan settles on by itself.
    /// </summary>
    private static void TheVirtualBusIsNeverChosenForYou()
    {
        const string Virtual = "Virtual bus (no hardware)";
        const string Hardware = "ValueCAN/neoVI SN 1878373302";

        // Mirrors what RefreshDevices builds: the virtual bus first, then any real device.
        Device[] withHardware = [new(Virtual, false), new(Hardware, true)];
        Device[] withoutHardware = [new(Virtual, false)];

        static string? Pick(Device[] devices, string? previous) =>
            MainWindow.PreferredDevice(devices, previous, d => d.Label, d => d.Real)?.Label;

        Check("no hardware -> nothing selected", Pick(withoutHardware, null) is null);
        Check("hardware present -> the device, not the virtual bus", Pick(withHardware, null) == Hardware);
        Check("an explicit virtual-bus pick survives a rescan", Pick(withoutHardware, Virtual) == Virtual);
        Check("and survives one that finds hardware too", Pick(withHardware, Virtual) == Virtual);
        Check("a device that went away falls back to another", Pick(withHardware, "ValueCAN/neoVI SN 999") == Hardware);
        Check("a device that went away with none left selects nothing",
              Pick(withoutHardware, "ValueCAN/neoVI SN 999") is null);
    }

    // ---------------- MessageHub ----------------

    private static void ClearReleasesTheRing()
    {
        var hub = new MessageHub(capacity: 64);
        var alive = new WeakReference(null);
        for (int i = 0; i < 64; i++)
        {
            var frame = new CanFrame { Channel = "CAN1", ArbId = 0x100, Data = new byte[8] };
            if (i == 0) alive.Target = frame;
            hub.Publish(frame);
        }
        hub.Clear();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
        Check("Clear drops the frames the ring was holding", !alive.IsAlive);
    }

    // ---------------- DBC ----------------

    private static void SignalsPastThePayloadAreOmitted()
    {
        var decoder = LoadProbeDbc();
        if (decoder is null) return;

        // Volt is 48|16@1 — it needs all 8 bytes. At 7 it used to print 0.119 as though measured.
        string? full = decoder.Decode(Frame([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77, 0x88]));
        string? shortFrame = decoder.Decode(Frame([0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77]));

        Check("full frame decodes Volt", full?.Contains("Volt=") == true, full);
        Check("short frame omits Volt", shortFrame?.Contains("Volt=") == false, shortFrame);
        Check("short frame says how many were omitted", shortFrame?.Contains("past the 7-byte payload") == true, shortFrame);
    }

    /// <summary>
    /// The naive StartBit + Length rule would demand 3 bytes for a 16-bit Motorola signal at
    /// StartBit 7, which actually occupies bytes 0-1. Suppressing valid signals would be its own
    /// kind of lying.
    /// </summary>
    private static void MotorolaSignalsAreNotOverSuppressed()
    {
        var decoder = LoadProbeDbc();
        if (decoder is null) return;

        string? twoBytes = decoder.Decode(Frame([0x11, 0x22]));
        Check("Motorola 7|16 still decodes from 2 bytes", twoBytes?.Contains("MotoLo=4386") == true, twoBytes);
        Check("Motorola 55|16 is omitted from 2 bytes", twoBytes?.Contains("MotoHi=") == false, twoBytes);
    }

    // ---------------- XCP ----------------

    private static XcpDecoder NewSession() => new(new XcpConfig(0x601, 0x701));

    /// <summary>The response branch was unreachable: the request bytes it tests were already cleared.</summary>
    private static void GetSlaveIdResponseIsDecoded()
    {
        var xcp = NewSession();
        xcp.Decode(Frame([0xF2, 0xFF, (byte)'X', (byte)'C', (byte)'P', 0x00], 0x601));
        var reply = xcp.Decode(Frame([0xFF, (byte)'X', (byte)'C', (byte)'P', 0x01, 0x07, 0x00, 0x00], 0x701));
        Check("GET_SLAVE_ID reply is recognised", reply?.Comment?.StartsWith("GET_SLAVE_ID:") == true, reply?.Comment);
        Check("and reports the slave's CAN ID", reply?.Comment?.Contains("0x00000701") == true, reply?.Comment);
    }

    private static void DownloadMaxHasNoElementCount()
    {
        var xcp = NewSession();
        var cto = xcp.Decode(Frame([0xEE, 0x11, 0x22, 0x33, 0x44, 0x55, 0x66, 0x77], 0x601));
        Check("DOWNLOAD_MAX reports no element count", cto?.Comment?.Contains("NUMBER_OF_ELEMENTS") == false, cto?.Comment);
        // The whole CTO past the command byte is payload, 0x11 included.
        Check("DOWNLOAD_MAX keeps its first data byte", cto?.Comment == "DATA = 11223344556677", cto?.Comment);
    }

    /// <summary>Identification field type 3 puts a FILL byte between the PID and the DAQ word.</summary>
    private static void WordAlignedIdFieldSkipsTheFillByte()
    {
        var xcp = NewSession();
        xcp.Decode(Frame([0xFF, 0x00], 0x601));                                    // CONNECT
        xcp.Decode(Frame([0xFF, 0x00, 0x00, 0x08, 0xFF, 0x00, 0x00, 0x00], 0x701));
        xcp.Decode(Frame([0xDA], 0x601));                                          // GET_DAQ_PROCESSOR_INFO
        // DAQ_KEY_BYTE bits 7:6 = 11b -> type 3
        xcp.Decode(Frame([0xFF, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0xC0], 0x701));

        var dto = xcp.Decode(Frame([0x00, 0x00, 0x01, 0x00, 0xAA, 0xBB, 0xCC, 0xDD], 0x701));
        Check("type 3 reads DAQ #1, not #256", dto?.Type?.Contains("DAQ #1|") == true, dto?.Type);
    }

    private static void ErrorNamesTheSubCommand()
    {
        var xcp = NewSession();
        xcp.Decode(Frame([0xF2, 0xFF, (byte)'X', (byte)'C', (byte)'P', 0x00], 0x601));
        var err = xcp.Decode(Frame([0xFE, 0x20], 0x701));
        Check("ERR names GET_SLAVE_ID rather than the generic command",
              err?.Comment?.Contains("GET_SLAVE_ID") == true, err?.Comment);
    }

    // ---------------- helpers ----------------

    private sealed record Device(string Label, bool Real);

    private static DbcDecoder? LoadProbeDbc()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "probe.dbc");
        if (!File.Exists(path)) { Check("probe.dbc is next to the test binary", false, path); return null; }
        var decoder = new DbcDecoder();
        decoder.Load(path);
        return decoder;
    }

    private static CanFrame Frame(byte[] data, uint id = 0x123) =>
        new() { Channel = "CAN1", ArbId = id, Data = data, Timestamp = 1 };

    private static TraceRow Row() => TraceRow.From(
        Frame([0x01]), new TimeBase(TimestampMode.Relative, 0, DateTime.Now), double.NaN);

    private static bool Throws(Action action)
    {
        try { action(); return false; }
        catch { return true; }
    }

    private static void Section(string name) => Console.WriteLine($"{name}:");

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (ok || detail is null ? "" : $" — {detail}"));
        if (!ok) _failures++;
    }
}
