using System.Collections.Specialized;
using CanTerminal.App;
using CanTerminal.Core;
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
