using System.Collections.Specialized;
using System.Globalization;
using System.Text;
using CanTerminal.App;
using CanTerminal.Core;
using CanTerminal.Core.Logs;
using CanTerminal.Core.Slcan;
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

        Section("MDF4 log reader");
        Mdf4RefusesWhatItDoesNotImplement();
        Mdf4ReadsLayoutFromTheFile();

        Section("Replay");
        ReplayFollowsItsOwnClock();
        ReplayBudgetHoldsTheClockBack();
        SeekingForwardAppendsAndBackRebuilds();

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

        Section("Shortcuts dialog");
        EveryCommandAppearsInTheShortcutsDialog();
        TheTwoMissingShortcutsAreBack();

        Section("Settings");
        SessionSettingsRoundTrip();
        AGarbageFileYieldsDefaults();
        AHandEditedFileIsClamped();

        Section("Log size estimate");
        EstimatesAreFormatSpecific();

        Section("SLCAN");
        SlcanTxLinesMatchTheWireFormat();
        SlcanFdPaddingLandsOnALegalLength();
        SlcanRxLinesRoundTrip();
        SlcanRejectsWhatItCannotCarry();
        SlcanBitratesMapToTheFirmwareCodes();
        SlcanTimesRatesThatHaveNoPreset();
        SlcanRefusesRatesItCannotTime();

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

        // The panes offer exactly this list, so a channel missing from it cannot be filtered to
        // at all — the frames are there and no menu reaches them.
        Check("every channel that carries a frame is listed",
              log.Frames.Select(f => f.Channel).Distinct().OrderBy(c => c)
                 .SequenceEqual(log.Channels.OrderBy(c => c)),
              string.Join(",", log.Channels));
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

    // ---------------- MDF4 log reader ----------------

    /// <summary>
    /// Builds just enough of an MDF 4.10 file to exercise the reader. Deliberately lays the
    /// CAN_DataFrame members out in an order and at offsets no real writer uses, so a reader that
    /// assumed the common layout instead of reading the composition would decode nonsense here
    /// and still produce the right number of frames.
    /// </summary>
    private static class Mdf4
    {
        public static byte[] Build(byte recordIdSize = 0, bool compressed = false, byte zipType = 1)
        {
            var file = new List<byte>();
            file.AddRange(Encoding.ASCII.GetBytes("MDF     "));
            file.AddRange(Encoding.ASCII.GetBytes("4.10    "));
            file.AddRange(Encoding.ASCII.GetBytes("regtest "));
            file.AddRange(new byte[4]);
            file.AddRange(BitConverter.GetBytes((ushort)410));
            while (file.Count < 64) file.Add(0);

            // The header block must sit at 64, so its space is reserved before anything else is
            // appended — writing it last and copying it down would land on top of the blocks it
            // points at.
            const int HeaderLength = 24 + (6 * 8) + 32;
            file.AddRange(new byte[HeaderLength]);

            // time f64 @0, then the frame members in a deliberately unusual order.
            var members = new (string Name, byte Offset, byte Bits)[]
            {
                ("CAN_DataFrame.DataBytes", 8, 64),
                ("CAN_DataFrame.ID", 16, 32),
                ("CAN_DataFrame.DLC", 20, 8),
                ("CAN_DataFrame.IDE", 21, 8),
                ("CAN_DataFrame.Dir", 22, 8),
                ("CAN_DataFrame.BusChannel", 23, 8),
                ("CAN_DataFrame.DataLength", 24, 8),
            };
            const int RecordLength = 25;

            long next = 0;
            var subs = new List<long>();
            foreach (var m in members.Reverse())
            {
                long name = Add(file, "##TX", [], Encoding.UTF8.GetBytes(m.Name + "\0"));
                byte dataType = (byte)(m.Name.EndsWith("DataBytes") ? 10 : 0);
                next = Add(file, "##CN", [next, 0, name, 0, 0, 0, 0, 0], Channel(0, 0, dataType, m.Offset, m.Bits));
                subs.Add(next);
            }
            long composition = next;

            long frameName = Add(file, "##TX", [], Encoding.UTF8.GetBytes("CAN_DataFrame\0"));
            long frameChannel = Add(file, "##CN", [0, composition, frameName, 0, 0, 0, 0, 0],
                                    Channel(0, 0, 10, 8, (RecordLength - 8) * 8));
            long timeName = Add(file, "##TX", [], Encoding.UTF8.GetBytes("time\0"));
            long timeChannel = Add(file, "##CN", [frameChannel, 0, timeName, 0, 0, 0, 0, 0],
                                   Channel(2, 1, 4, 0, 64));

            var records = new List<byte>();
            (double Ts, uint Id, byte Bus, byte Ide, byte Dir, byte[] Data)[] rows =
            [
                (0.0127, 0x18DAF110, 1, 1, 0, [0xAD, 0x21, 0, 0, 0, 0, 0, 0]),
                (0.1016, 0x18FFA201, 1, 1, 1, [0xFF, 0x00]),
                (0.2000, 0x123,      2, 0, 0, [0x01, 0x02, 0x03]),
            ];
            foreach (var r in rows)
            {
                var rec = new byte[RecordLength];
                BitConverter.GetBytes(r.Ts).CopyTo(rec, 0);
                r.Data.CopyTo(rec, 8);
                BitConverter.GetBytes(r.Id).CopyTo(rec, 16);
                rec[20] = (byte)r.Data.Length;
                rec[21] = r.Ide;
                rec[22] = r.Dir;
                rec[23] = r.Bus;
                rec[24] = (byte)r.Data.Length;
                records.AddRange(rec);
            }

            long data;
            if (compressed)
            {
                var payload = new List<byte>();
                payload.AddRange(BitConverter.GetBytes((ushort)0x5444));      // "DT"
                payload.Add(zipType);
                payload.Add(0);
                payload.AddRange(BitConverter.GetBytes((uint)0));
                payload.AddRange(BitConverter.GetBytes((ulong)records.Count));
                payload.AddRange(BitConverter.GetBytes((ulong)0));
                data = Add(file, "##DZ", [], payload.ToArray());
            }
            else
            {
                data = Add(file, "##DT", [], records.ToArray());
            }

            long channelGroup = Add(file, "##CG", [0, timeChannel, 0, 0, 0, 0], Group(rows.Length, RecordLength));
            var dgData = new byte[8];
            dgData[0] = recordIdSize;
            long dataGroup = Add(file, "##DG", [0, channelGroup, data, 0], dgData);

            var hd = new byte[32];
            BitConverter.GetBytes(1_735_000_000_000_000_000UL).CopyTo(hd, 0);
            hd[12] = 0x01;                                                    // stamp is local time

            var bytes = file.ToArray();
            var header = new List<byte>();
            header.AddRange(Encoding.ASCII.GetBytes("##HD"));
            header.AddRange(new byte[4]);
            header.AddRange(BitConverter.GetBytes((long)HeaderLength));
            header.AddRange(BitConverter.GetBytes(6L));
            foreach (long link in new[] { dataGroup, 0L, 0L, 0L, 0L, 0L })
                header.AddRange(BitConverter.GetBytes(link));
            header.AddRange(hd);
            header.CopyTo(0, bytes, 64, HeaderLength);
            return bytes;
        }

        private static byte[] Channel(byte type, byte sync, byte dataType, int byteOffset, int bits)
        {
            var d = new byte[72];
            d[0] = type; d[1] = sync; d[2] = dataType; d[3] = 0;
            BitConverter.GetBytes(byteOffset).CopyTo(d, 4);
            BitConverter.GetBytes(bits).CopyTo(d, 8);
            return d;
        }

        private static byte[] Group(int cycles, int recordBytes)
        {
            var d = new byte[32];
            BitConverter.GetBytes((long)cycles).CopyTo(d, 8);
            BitConverter.GetBytes((ushort)0x06).CopyTo(d, 16);               // bus event
            BitConverter.GetBytes(recordBytes).CopyTo(d, 24);
            return d;
        }

        private static long Add(List<byte> file, string kind, long[] links, byte[] data)
        {
            while (file.Count % 8 != 0) file.Add(0);
            long at = file.Count;
            file.AddRange(Encoding.ASCII.GetBytes(kind));
            file.AddRange(new byte[4]);
            file.AddRange(BitConverter.GetBytes((long)(24 + links.Length * 8 + data.Length)));
            file.AddRange(BitConverter.GetBytes((long)links.Length));
            foreach (long l in links) file.AddRange(BitConverter.GetBytes(l));
            file.AddRange(data);
            return at;
        }
    }

    private static LogFile ReadMdf(byte[] bytes)
    {
        string path = Path.Combine(Path.GetTempPath(), $"canterminal_test_{Guid.NewGuid():N}.mf4");
        File.WriteAllBytes(path, bytes);
        try { return new Mdf4LogReader().Read(path); }
        finally { try { File.Delete(path); } catch { } }
    }

    /// <summary>
    /// The whole safety argument for reading this format: MDF4 carries no checksum, so a block
    /// decoded on a guess passes every consistency check the format offers. Anything not
    /// implemented has to be refused, and the message has to say which block it was.
    /// </summary>
    private static void Mdf4RefusesWhatItDoesNotImplement()
    {
        string Refusal(Func<LogFile> read)
        {
            try { read(); return ""; }
            catch (Exception ex) { return ex.Message; }
        }

        string unsorted = Refusal(() => ReadMdf(Mdf4.Build(recordIdSize: 1)));
        Check("an unsorted data group is refused, by name",
              unsorted.Contains("unsorted", StringComparison.OrdinalIgnoreCase), unsorted);

        string transposed = Refusal(() => ReadMdf(Mdf4.Build(compressed: true, zipType: 1)));
        Check("transposed deflate is refused rather than guessed at",
              transposed.Contains("zip type 1", StringComparison.OrdinalIgnoreCase)
              || transposed.Contains("transposed", StringComparison.OrdinalIgnoreCase), transposed);

        string notMdf = Refusal(() => ReadMdf(Encoding.ASCII.GetBytes(new string('x', 200))));
        Check("a file that is not MDF at all is refused",
              notMdf.Contains("MDF", StringComparison.Ordinal), notMdf);
    }

    /// <summary>
    /// The members are laid out at offsets no real writer uses, so this only passes if the reader
    /// took the layout from the file's own composition block.
    /// </summary>
    private static void Mdf4ReadsLayoutFromTheFile()
    {
        var log = ReadMdf(Mdf4.Build());
        Check("three records give three frames", log.Frames.Count == 3, log.Frames.Count.ToString());
        if (log.Frames.Count != 3) return;

        var a = log.Frames[0];
        Check("identifier, extended flag and payload come out right",
              a.ArbId == 0x18DAF110 && a.IsExtended && a.DataText == "AD21000000000000", a.DataText);
        Check("the timestamp is the master channel's",
              a.Timestamp == 0.0127, a.Timestamp.ToString(CultureInfo.InvariantCulture));
        Check("Dir marks a transmitted frame", log.Frames[1].Direction == FrameDirection.Tx);
        Check("DataLength trims the payload to its real length",
              log.Frames[1].Data.Length == 2 && log.Frames[2].Data.Length == 3,
              $"{log.Frames[1].Data.Length},{log.Frames[2].Data.Length}");
        Check("BusChannel names the channel",
              log.Frames[0].Channel == "CAN1" && log.Frames[2].Channel == "CAN2",
              $"{log.Frames[0].Channel},{log.Frames[2].Channel}");
        Check("a standard identifier is not marked extended", !log.Frames[2].IsExtended);
        Check("nothing was skipped", log.SkippedLines == 0, log.SkippedLines.ToString());
    }

    // ---------------- replay ----------------

    /// <summary>A thousand frames per second of file time, sixty seconds of it.</summary>
    private static CanFrame[] Recording() =>
        [.. Enumerable.Range(0, 60_000).Select(i => new CanFrame
        {
            Timestamp = i * 0.001, Channel = "CAN1", ArbId = 0x100, Data = [(byte)i],
        })];

    private static void ReplayFollowsItsOwnClock()
    {
        var player = new LogPlayer(Recording());
        Check("it starts at the beginning, stopped", !player.IsPlaying && player.EmittedCount == 0);

        player.Play();
        int emitted = 0;
        for (int i = 0; i < 20; i++) emitted += player.Advance(0.05, 4000).Count;   // one second of wall time
        Check("one second at 1x is one second of the file",
              Math.Abs(player.Position - 1.0) < 0.01, player.Position.ToString("F3"));
        Check("and hands over the frames that fall in it",
              Math.Abs(emitted - 1000) <= 2, emitted.ToString());

        player.Speed = 10;
        for (int i = 0; i < 20; i++) player.Advance(0.05, 40_000);
        Check("10x covers ten seconds in the same wall time",
              Math.Abs(player.Position - 11.0) < 0.02, player.Position.ToString("F3"));

        player.Pause();
        double held = player.Position;
        player.Advance(0.05, 4000);
        Check("a paused replay does not move", player.Position == held);

        player.Speed = 0;
        player.Play();
        while (!player.AtEnd) player.Advance(0.05, 40_000);
        Check("it stops itself at the end", !player.IsPlaying && player.Position == player.End);
        Check("every frame was handed over", player.EmittedCount == 60_000, player.EmittedCount.ToString("N0"));

        player.Play();
        Check("playing from the end starts over", player.EmittedCount == 0 && player.IsPlaying);
    }

    /// <summary>
    /// The budget is what stops a fast replay handing the UI thread more frames than it can draw.
    /// The position has to be held back with it — a clock that ran on regardless would claim to be
    /// somewhere the display has not reached.
    /// </summary>
    private static void ReplayBudgetHoldsTheClockBack()
    {
        var player = new LogPlayer(Recording()) { Speed = 100 };
        player.Play();
        var due = player.Advance(0.05, 100);
        Check("no more than the budget is handed over", due.Count == 100, due.Count.ToString());
        Check("and the clock stops where the frames did",
              player.Position < 0.2, player.Position.ToString("F3"));
    }

    private static void SeekingForwardAppendsAndBackRebuilds()
    {
        var player = new LogPlayer(Recording());

        var gap = player.SeekTo(10, out bool rebuild);
        Check("seeking forward asks for no rebuild", !rebuild);
        Check("and returns just the frames in between",
              gap is { Count: 10_000 }, gap?.Count.ToString() ?? "null");
        Check("the position is where it was asked for", Math.Abs(player.Position - 10) < 1e-9);

        var back = player.SeekTo(4, out rebuild);
        Check("seeking back asks for a rebuild", rebuild && back is null);
        Check("and the frames to replay are those up to it",
              player.Played().Count == 4000, player.Played().Count.ToString());

        player.SeekTo(-5, out _);
        Check("seeking before the start clamps to it", player.Position == player.Start);
        player.SeekTo(1e9, out _);
        Check("and past the end clamps to that", player.Position == player.End);
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

    // ---------------- Shortcuts dialog ----------------

    /// <summary>
    /// The dialog is generated from AppCommands.MenuSections; this holds the two together. The
    /// previous dialog was a hardcoded copy of the same information and had already lost Ctrl+O
    /// and Ctrl+G while the menus carried them. The exact formatted line is required, not just
    /// the substrings — "Connect" is contained in "Disconnect", and "F9" in "Shift+F9".
    /// </summary>
    private static void EveryCommandAppearsInTheShortcutsDialog()
    {
        string text = AppCommands.ShortcutsText();
        var commands = typeof(AppCommands)
            .GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)
            .Where(f => f.FieldType == typeof(System.Windows.Input.RoutedUICommand))
            .Select(f => (f.Name, Command: (System.Windows.Input.RoutedUICommand)f.GetValue(null)!))
            .ToList();
        Check("AppCommands declares commands", commands.Count > 0);
        foreach (var (name, command) in commands)
        {
            string gesture = AppCommands.GestureText(command);
            string line = $"  {gesture,-16}{command.Text}";
            Check($"{name} ({gesture}) is in the dialog", gesture.Length > 0 && text.Contains(line));
        }
    }

    /// <summary>The two entries the hardcoded dialog had actually lost.</summary>
    private static void TheTwoMissingShortcutsAreBack()
    {
        string text = AppCommands.ShortcutsText();
        Check("Ctrl+O Open log is listed", text.Contains("Ctrl+O") && text.Contains("Open log"));
        Check("Ctrl+G Go to time is listed", text.Contains("Ctrl+G") && text.Contains("Go to time"));
    }

    // ---------------- Settings ----------------

    /// <summary>Points AppSettings at a scratch file so the user's real settings stay untouched.</summary>
    private static string ScratchSettings()
    {
        string path = Path.Combine(Path.GetTempPath(), $"canterminal-test-{Guid.NewGuid():N}.json");
        AppSettings.SettingsPath = path;
        return path;
    }

    /// <summary>Every persisted session setting must survive Save → Load unchanged.</summary>
    private static void SessionSettingsRoundTrip()
    {
        string path = ScratchSettings();
        try
        {
            var saved = new AppSettings
            {
                Channels = "CAN1@250000,CAN2",
                Bitrate = 250_000,
                FdBitrate = 5_000_000,
                FdEnabled = true,
                Layout = 2,
                FontSize = 15,
                Timestamps = nameof(TimestampMode.Delta),
                HistoryCapacity = 123_456,
                ApiServer = false,
                ApiPort = 4242,
                CycleMs = 250,
                WindowWidth = 1600,
                WindowHeight = 900,
                WindowLeft = 10,
                WindowTop = 20,
                WindowMaximized = true,
            };
            saved.Save();
            var loaded = AppSettings.Load();
            Check("channels round-trip", loaded.Channels == saved.Channels);
            Check("bitrates round-trip", loaded.Bitrate == 250_000 && loaded.FdBitrate == 5_000_000 && loaded.FdEnabled);
            Check("layout and font round-trip", loaded.Layout == 2 && loaded.FontSize == 15);
            Check("timestamp mode round-trips", loaded.Timestamps == nameof(TimestampMode.Delta));
            Check("history round-trips", loaded.HistoryCapacity == 123_456);
            Check("api server round-trips", !loaded.ApiServer && loaded.ApiPort == 4242);
            Check("cycle time round-trips", loaded.CycleMs == 250);
            Check("window placement round-trips",
                  loaded is { WindowWidth: 1600, WindowHeight: 900, WindowLeft: 10, WindowTop: 20, WindowMaximized: true });
        }
        finally { File.Delete(path); }
    }

    /// <summary>A corrupt file must read as "no settings", not fail startup.</summary>
    private static void AGarbageFileYieldsDefaults()
    {
        string path = ScratchSettings();
        try
        {
            File.WriteAllText(path, "{ this is not json");
            var loaded = AppSettings.Load();
            Check("garbage file falls back to defaults",
                  loaded.Bitrate == 500_000 && loaded.FontSize == 12 && loaded.ApiServer);
        }
        finally { File.Delete(path); }
    }

    /// <summary>
    /// Out-of-range values in a hand-edited file are clamped to what the dialogs allow, so the
    /// program cannot start somewhere its own UI cannot reach — a 0 pt font, layout 7.
    /// </summary>
    private static void AHandEditedFileIsClamped()
    {
        string path = ScratchSettings();
        try
        {
            File.WriteAllText(path,
                """{ "FontSize": 0, "HistoryCapacity": -5, "ApiPort": 99999, "Layout": 7, "Timestamps": "Sideways" }""");
            var loaded = AppSettings.Load();
            Check("font size clamped", loaded.FontSize == 8);
            Check("history clamped", loaded.HistoryCapacity == TraceBuffer.MinCapacity);
            Check("port clamped", loaded.ApiPort == 65535);
            Check("layout clamped", loaded.Layout == 2);
            Check("unknown timestamp mode dropped", loaded.Timestamps is null);
        }
        finally { File.Delete(path); }
    }

    // ---------------- Log size estimate ----------------

    /// <summary>
    /// The large-file warning fires off these, before the read it warns about. The ASC figure is
    /// measured (56.8 B/frame on a real 483,621-frame capture); MDF4 deliberately overestimates
    /// (27 B/record measured, divisor 16) so a ##DZ-compressed file still warns.
    /// </summary>
    private static void EstimatesAreFormatSpecific()
    {
        var asc = new AscLogReader();
        var mdf = new Mdf4LogReader();
        Check("ASC estimate lands near the measured capture",
              asc.EstimateFrames(27_470_143) is > 450_000 and < 520_000);
        Check("MDF4 counts more frames per byte than ASC",
              mdf.EstimateFrames(1_000_000) > asc.EstimateFrames(1_000_000));
        Check("MDF4 overestimates the uncompressed case",
              mdf.EstimateFrames(13_063_392) >= 483_621);
    }

    // ---------------- SLCAN ----------------

    /// <summary>The exact ASCII the WeAct firmware documents, one case per frame letter.</summary>
    private static void SlcanTxLinesMatchTheWireFormat()
    {
        Check("classic standard",
              SlcanProtocol.BuildTxLine(0x123, [0xDE, 0xAD, 0xBE, 0xEF], extended: false, fd: false, brs: false)
              == "t1234DEADBEEF");
        Check("classic extended",
              SlcanProtocol.BuildTxLine(0x18DAF110, [0x01, 0x02], extended: true, fd: false, brs: false)
              == "T18DAF1102" + "0102");
        Check("FD without BRS uses d and the FD DLC code",
              SlcanProtocol.BuildTxLine(0x123, new byte[12], extended: false, fd: true, brs: false)
              == "d1239" + new string('0', 24));
        Check("FD with BRS on an extended id uses B",
              SlcanProtocol.BuildTxLine(0x123, new byte[64], extended: true, fd: true, brs: true)
              == "B00000123F" + new string('0', 128));
    }

    /// <summary>13 bytes cannot go on a CAN FD bus; the next legal length is 16, zero-filled.</summary>
    private static void SlcanFdPaddingLandsOnALegalLength()
    {
        byte[] original = [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13];
        var padded = SlcanProtocol.PadToFd(original);
        Check("13 bytes pad to 16", padded.Length == 16);
        Check("payload survives, tail is zero",
              padded.Take(13).SequenceEqual(original) && padded.Skip(13).All(b => b == 0));
        Check("a legal length is copied, not aliased",
              !ReferenceEquals(SlcanProtocol.PadToFd(new byte[8]), null) &&
              SlcanProtocol.PadToFd(original) is not null && !ReferenceEquals(SlcanProtocol.PadToFd(padded), padded));
    }

    /// <summary>Every TX line the builder emits must come back identical through the RX parser.</summary>
    private static void SlcanRxLinesRoundTrip()
    {
        foreach (var (id, data, ext, fd, brs) in new (uint, byte[], bool, bool, bool)[]
        {
            (0x123, [0xDE, 0xAD], false, false, false),
            (0x7FF, [], false, false, false),
            (0x1FFFFFFF, [1, 2, 3, 4, 5, 6, 7, 8], true, false, false),
            (0x100, new byte[24], false, true, false),
            (0x18FF50E5, new byte[48], true, true, true),
        })
        {
            string line = SlcanProtocol.BuildTxLine(id, data, ext, fd, brs);
            bool ok = SlcanProtocol.TryParseRxLine(line, out var rx)
                      && rx.Id == id && rx.Extended == ext && rx.Fd == fd && rx.Brs == brs
                      && !rx.Remote && rx.Data.SequenceEqual(data);
            Check($"round-trip {line[..Math.Min(12, line.Length)]}…", ok);
        }

        Check("lowercase hex is accepted on the way in",
              SlcanProtocol.TryParseRxLine("t1232dead", out var lower) && lower.Data is [0xDE, 0xAD]);
        Check("remote frame carries its DLC as zeroes",
              SlcanProtocol.TryParseRxLine("r1238", out var rtr) && rtr.Remote && rtr.Data.Length == 8);
        Check("extended remote frame",
              SlcanProtocol.TryParseRxLine("R000001234", out var xr) && xr.Remote && xr.Extended
              && xr.Id == 0x123 && xr.Data.Length == 4);
    }

    /// <summary>Malformed lines are refused, never guessed at.</summary>
    private static void SlcanRejectsWhatItCannotCarry()
    {
        foreach (var bad in new[]
        {
            "t123", "x1230", "t123G00", "t1239" + new string('0', 18),   // classic DLC 9
            "t1232AABBCC",                                               // data longer than DLC
            "d1239" + new string('0', 22),                               // FD data shorter than DLC
            "r12380",                                                    // RTR with data bytes
            "",
        })
            Check($"rejects '{(bad.Length > 12 ? bad[..12] + "…" : bad)}'",
                  !SlcanProtocol.TryParseRxLine(bad, out _));

        Check("11-bit id over 0x7FF is refused",
              Throws(() => SlcanProtocol.BuildTxLine(0x800, [], extended: false, fd: false, brs: false)));
        Check("9 bytes on classic CAN is refused",
              Throws(() => SlcanProtocol.BuildTxLine(0x123, new byte[9], extended: false, fd: false, brs: false)));
    }

    /// <summary>Presets where the firmware has one.</summary>
    private static void SlcanBitratesMapToTheFirmwareCodes()
    {
        Check("500k -> S6", SlcanProtocol.NominalCommand(500_000) == "S6");
        Check("125k -> S4", SlcanProtocol.NominalCommand(125_000) == "S4");
        Check("1M -> S8", SlcanProtocol.NominalCommand(1_000_000) == "S8");
        Check("2M data -> Y2", SlcanProtocol.DataCommand(2_000_000) == "Y2");
        Check("5M data -> Y5", SlcanProtocol.DataCommand(5_000_000) == "Y5");
    }

    /// <summary>
    /// Rates with no preset are timed rather than refused. The device can produce far more than
    /// the preset tables list — 500 kbit/s of CAN FD data among them — and the program used to
    /// turn those away while blaming the hardware.
    ///
    /// The check decodes the command back into a rate rather than trusting the arithmetic that
    /// built it, and requires the sample point to land where CAN wants it.
    /// </summary>
    private static void SlcanTimesRatesThatHaveNoPreset()
    {
        foreach (int bps in new[] { 500_000, 1_000_000, 250_000, 125_000, 1_500_000, 4_000_000 })
        {
            string command = SlcanProtocol.DataCommand(bps);
            if (command.Length == 2) { Check($"FD data {bps:N0} uses a preset ({command})", true); continue; }
            var (produced, samplePoint) = SlcanProtocol.Decode(command);
            Check($"FD data {bps:N0} -> {command} = {produced:N0} @ {samplePoint:P0}",
                  produced == bps && samplePoint is >= 0.6 and <= 0.9);
        }

        foreach (int bps in new[] { 300_000, 400_000, 600_000, 750_000 })
        {
            string command = SlcanProtocol.NominalCommand(bps);
            var (produced, samplePoint) = SlcanProtocol.Decode(command);
            Check($"arbitration {bps:N0} -> {command} = {produced:N0} @ {samplePoint:P0}",
                  produced == bps && samplePoint is >= 0.6 and <= 0.9);
        }

        Check("500k FD data is a computed command, not a preset",
              SlcanProtocol.DataCommand(500_000).Length == 7);
    }

    /// <summary>What genuinely cannot be done is refused — and the message no longer blames the device.</summary>
    private static void SlcanRefusesRatesItCannotTime()
    {
        string tooFast = Message(() => SlcanProtocol.DataCommand(8_000_000));
        Check("8M data is refused as above what the device runs", tooFast.Contains("highest"), tooFast);

        string odd = Message(() => SlcanProtocol.NominalCommand(499_999));
        Check("an untimeable rate says why and names neighbours",
              odd.Contains("cannot be timed exactly") && odd.Contains("Closest"), odd);
        Check("the refusal does not claim the device lacks the feature", !odd.Contains("does not support"), odd);

        Check("2M arbitration is refused (classic CAN tops out at 1M)",
              Throws(() => SlcanProtocol.NominalCommand(2_000_000)));
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

    /// <summary>The message an action fails with, so a test can hold the wording to account.</summary>
    private static string Message(Action action)
    {
        try { action(); return ""; }
        catch (Exception ex) { return ex.Message; }
    }

    private static void Section(string name) => Console.WriteLine($"{name}:");

    private static void Check(string name, bool ok, string? detail = null)
    {
        Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {name}" + (ok || detail is null ? "" : $" — {detail}"));
        if (!ok) _failures++;
    }
}
