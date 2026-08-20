using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

namespace CanTerminal.Core.Logs;

/// <summary>
/// Reads an ASAM MDF 4.x file written as a CAN bus log — the shape where each data group holds a
/// "time" master channel and a "CAN_DataFrame" structure whose sub-channels carry the identifier,
/// length, direction and payload.
///
/// The layout is read out of the file rather than assumed: a writer is free to order the
/// sub-channels differently, and a reader that hard-codes offsets produces frames that look
/// entirely plausible and are wrong. Everything this reader has not implemented is refused by
/// name — MDF4 carries no checksum, so a block decoded on a guess still satisfies every
/// arithmetic invariant the format offers while yielding nonsense. Refusing is the only failure
/// mode a reader of this format can make visible.
///
/// Out of scope, and named as such when met: transposed deflate, variable-length signal data,
/// unsorted data groups, and files whose channels are decoded signals rather than bus frames.
/// </summary>
public sealed class Mdf4LogReader : ILogReader
{
    public string Description => "ASAM MDF4 CAN bus log";

    public string Filter => "MDF4 CAN bus log (*.mf4;*.mdf)|*.mf4;*.mdf";

    public bool CanRead(string path)
    {
        string ext = System.IO.Path.GetExtension(path);
        return ext.Equals(".mf4", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".mdf", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Seconds between the MDF epoch (1970-01-01 UTC) and <see cref="DateTime"/>'s.</summary>
    private static readonly DateTime UnixEpoch = new(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    public LogFile Read(string path, IProgress<double>? progress = null, CancellationToken ct = default)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16);
        ReadIdentification(stream);

        var header = Block.At(stream, 64, "##HD");
        DateTime? startWall = HeaderTime(header);

        var groups = new List<FrameGroup>();
        var skippedByShape = new Dictionary<string, int>(StringComparer.Ordinal);
        var samples = new List<string>();
        int skipped = 0;

        long totalCycles = 0;
        for (long dg = header.Link(0); dg != 0;)
        {
            ct.ThrowIfCancellationRequested();
            var group = Block.At(stream, dg, "##DG");

            // A record id in front of every record means several channel groups share one data
            // block. Demultiplexing that is not implemented, and guessing would silently mix
            // groups together.
            byte recordIdSize = group.Data[0];
            if (recordIdSize != 0)
                throw new InvalidDataException(
                    $"This file has an unsorted data group (record id size {recordIdSize}), which this reader does not implement.");

            for (long cg = group.Link(1); cg != 0;)
            {
                var channelGroup = Block.At(stream, cg, "##CG");
                var frames = TryReadFrameGroup(stream, group, channelGroup, groups.Count, ct);
                if (frames is not null)
                {
                    groups.Add(frames);
                    totalCycles += frames.Frames.Count;
                }
                else
                {
                    // Not a bus-frame group. Counted the way the ASC reader counts a line it does
                    // not understand, so "nothing was found" is never silent.
                    string name = channelGroup.Text(stream, 2) ?? "unnamed channel group";
                    long cycles = BinaryPrimitives.ReadInt64LittleEndian(channelGroup.Data.AsSpan(8));
                    skipped++;
                    const string shape = "channel group without a CAN_DataFrame channel";
                    skippedByShape[shape] = skippedByShape.GetValueOrDefault(shape) + 1;
                    if (samples.Count < 8) samples.Add($"{name} ({cycles:N0} records)");
                }
                cg = channelGroup.Link(0);
            }
            dg = group.Link(0);
        }

        if (groups.Count == 0)
            throw new InvalidDataException(
                "No CAN_DataFrame channel was found. This file holds decoded signals rather than bus frames, " +
                "so there are no frames to show." +
                (skipped > 0 ? $" ({skipped} channel group(s) of signals.)" : ""));

        progress?.Report(0.5);
        var merged = Merge(groups, ct);
        progress?.Report(1);

        var channels = merged.Select(f => f.Channel).Distinct()
                             .OrderBy(c => c, StringComparer.OrdinalIgnoreCase).ToArray();

        return new LogFile(
            path, merged, channels, startWall,
            StartWallIsApproximate: false,
            FirstTimestamp: merged.Count == 0 ? 0 : merged[0].Timestamp,
            LastTimestamp: merged.Count == 0 ? 0 : merged[^1].Timestamp,
            skipped, skippedByShape, samples);
    }

    // ---------------- file scaffolding ----------------

    private static void ReadIdentification(Stream stream)
    {
        stream.Position = 0;
        var id = new byte[64];
        stream.ReadExactly(id);
        string magic = Encoding.ASCII.GetString(id, 0, 8);
        if (magic != "MDF     ")
            throw new InvalidDataException("Not an MDF file: the identification block does not say \"MDF\".");

        string version = Encoding.ASCII.GetString(id, 8, 8).Trim();
        if (!version.StartsWith('4'))
            throw new InvalidDataException($"MDF version {version} is not supported; this reader handles 4.x.");
    }

    /// <summary>
    /// Wall clock the file claims the measurement started at.
    ///
    /// Worth knowing that this is whatever the writer put there. A converter that builds an MDF4
    /// out of an earlier recording commonly stamps the time of the conversion, not of the
    /// capture, and nothing in the file distinguishes the two.
    /// </summary>
    private static DateTime? HeaderTime(Block header)
    {
        ulong ns = BinaryPrimitives.ReadUInt64LittleEndian(header.Data);
        if (ns == 0) return null;
        short tzMinutes = BinaryPrimitives.ReadInt16LittleEndian(header.Data.AsSpan(8));
        short dstMinutes = BinaryPrimitives.ReadInt16LittleEndian(header.Data.AsSpan(10));
        byte flags = header.Data[12];

        var utc = UnixEpoch.AddSeconds(ns / 1e9);
        // Bit 0: the stamp is already local time. Bit 1: the offsets above are meaningful.
        if ((flags & 0x01) != 0) return DateTime.SpecifyKind(utc, DateTimeKind.Local);
        if ((flags & 0x02) != 0) return DateTime.SpecifyKind(utc.AddMinutes(tzMinutes + dstMinutes), DateTimeKind.Local);
        return utc.ToLocalTime();
    }

    // ---------------- one bus-frame channel group ----------------

    private sealed record FrameGroup(List<CanFrame> Frames);

    /// <summary>Where one sub-channel of CAN_DataFrame sits inside a record.</summary>
    private readonly record struct Field(int ByteOffset, int BitOffset, int BitCount, byte DataType)
    {
        public bool Present => BitCount > 0;
    }

    private static FrameGroup? TryReadFrameGroup(Stream stream, Block dataGroup, Block channelGroup, int ordinal, CancellationToken ct)
    {
        long cycles = BinaryPrimitives.ReadInt64LittleEndian(channelGroup.Data.AsSpan(8));
        int dataBytes = BinaryPrimitives.ReadInt32LittleEndian(channelGroup.Data.AsSpan(24));
        int invalidBytes = BinaryPrimitives.ReadInt32LittleEndian(channelGroup.Data.AsSpan(28));
        int recordLength = dataBytes + invalidBytes;

        Field time = default;
        var fields = new Dictionary<string, Field>(StringComparer.OrdinalIgnoreCase);
        bool sawFrameChannel = false;

        for (long cn = channelGroup.Link(1); cn != 0;)
        {
            var channel = Block.At(stream, cn, "##CN");
            byte channelType = channel.Data[0];
            byte syncType = channel.Data[1];
            string? name = channel.Text(stream, 2);

            if (channelType == 2 && syncType == 1)
            {
                time = FieldOf(channel);
            }
            else if (name is "CAN_DataFrame")
            {
                sawFrameChannel = true;
                if (channelType == 1)
                    throw new InvalidDataException(
                        "CAN_DataFrame is stored as variable-length signal data (VLSD), which this reader does not implement.");
                // The structure's members carry the actual layout.
                for (long sub = channel.Link(1); sub != 0;)
                {
                    var member = Block.At(stream, sub, "##CN");
                    if (member.Data[0] == 1)
                        throw new InvalidDataException(
                            $"{member.Text(stream, 2)} is variable-length signal data (VLSD), which this reader does not implement.");
                    string memberName = member.Text(stream, 2) ?? "";
                    int dot = memberName.LastIndexOf('.');
                    fields[dot >= 0 ? memberName[(dot + 1)..] : memberName] = FieldOf(member);
                    sub = member.Link(0);
                }
            }
            cn = channel.Link(0);
        }

        if (!sawFrameChannel || !time.Present) return null;

        var id = Look(fields, "ID");
        var payload = Look(fields, "DataBytes");
        if (!id.Present || !payload.Present) return null;

        var busChannel = Look(fields, "BusChannel");
        var extended = Look(fields, "IDE");
        var length = Look(fields, "DataLength");
        var dlc = Look(fields, "DLC");
        var direction = Look(fields, "Dir");
        var fd = Look(fields, "EDL");
        var bitRateSwitch = Look(fields, "BRS");

        byte[] records = ReadData(stream, dataGroup.Link(2), ct);
        long available = recordLength == 0 ? 0 : records.LongLength / recordLength;
        if (available < cycles) cycles = available;      // trust the data, not the claim

        var frames = new List<CanFrame>((int)cycles);
        for (long i = 0; i < cycles; i++)
        {
            if ((i & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            var record = records.AsSpan((int)(i * recordLength), recordLength);

            int payloadBytes = payload.BitCount / 8;
            int actual = length.Present ? (int)Unsigned(record, length)
                       : dlc.Present ? (int)Unsigned(record, dlc)
                       : payloadBytes;
            actual = Math.Clamp(actual, 0, payloadBytes);

            var data = new byte[actual];
            record.Slice(payload.ByteOffset, actual).CopyTo(data);

            // Some writers carry the extended flag in bit 31 of the identifier instead of a
            // channel of its own, so the identifier is masked either way.
            uint arbId = (uint)Unsigned(record, id);
            bool isExtended = extended.Present ? Unsigned(record, extended) != 0 : (arbId & 0x8000_0000) != 0;
            arbId &= 0x1FFF_FFFF;

            // BusChannel is 1-based where it is filled in at all; a writer that leaves it zero
            // still gives one bus per data group, so the group's own position names it.
            long bus = busChannel.Present ? Unsigned(record, busChannel) : 0;
            if (bus <= 0) bus = ordinal + 1;

            frames.Add(new CanFrame
            {
                Timestamp = Real(record, time),
                Channel = "CAN" + bus.ToString(System.Globalization.CultureInfo.InvariantCulture),
                ArbId = arbId,
                IsExtended = isExtended,
                IsFd = fd.Present && Unsigned(record, fd) != 0,
                IsBrs = bitRateSwitch.Present && Unsigned(record, bitRateSwitch) != 0,
                Direction = direction.Present && Unsigned(record, direction) != 0 ? FrameDirection.Tx : FrameDirection.Rx,
                Data = data,
            });
        }
        return new FrameGroup(frames);
    }

    private static Field Look(Dictionary<string, Field> fields, string name) =>
        fields.TryGetValue(name, out var f) ? f : default;

    private static Field FieldOf(Block channel) => new(
        ByteOffset: BinaryPrimitives.ReadInt32LittleEndian(channel.Data.AsSpan(4)),
        BitOffset: channel.Data[3],
        BitCount: BinaryPrimitives.ReadInt32LittleEndian(channel.Data.AsSpan(8)),
        DataType: channel.Data[2]);

    // ---------------- record fields ----------------

    private static long Unsigned(ReadOnlySpan<byte> record, Field f)
    {
        // Whole bytes is the overwhelming case and worth not going through the bit path for.
        if (f.BitOffset == 0 && f.BitCount % 8 == 0 && f.BitCount <= 64)
        {
            var bytes = record.Slice(f.ByteOffset, f.BitCount / 8);
            long value = 0;
            if (f.DataType is 1 or 3)                       // big endian
                foreach (byte b in bytes) value = (value << 8) | b;
            else
                for (int i = bytes.Length - 1; i >= 0; i--) value = (value << 8) | bytes[i];
            return value;
        }

        long bits = 0;
        for (int i = 0; i < f.BitCount; i++)
        {
            int bit = f.BitOffset + i;
            int b = record[f.ByteOffset + (bit >> 3)];
            if ((b & (1 << (bit & 7))) != 0) bits |= 1L << i;
        }
        return bits;
    }

    private static double Real(ReadOnlySpan<byte> record, Field f)
    {
        var bytes = record.Slice(f.ByteOffset, f.BitCount / 8);
        return f.DataType switch
        {
            4 when bytes.Length == 8 => BinaryPrimitives.ReadDoubleLittleEndian(bytes),
            4 when bytes.Length == 4 => BinaryPrimitives.ReadSingleLittleEndian(bytes),
            5 when bytes.Length == 8 => BinaryPrimitives.ReadDoubleBigEndian(bytes),
            5 when bytes.Length == 4 => BinaryPrimitives.ReadSingleBigEndian(bytes),
            _ => Unsigned(record, f),
        };
    }

    // ---------------- data blocks ----------------

    /// <summary>
    /// Resolves whatever a data link points at into one span of records: a plain block, a
    /// compressed one, or a list of either.
    /// </summary>
    private static byte[] ReadData(Stream stream, long address, CancellationToken ct)
    {
        if (address == 0) return [];
        var pieces = new List<byte[]>();
        Collect(stream, address, pieces, ct, depth: 0);
        if (pieces.Count == 1) return pieces[0];

        long total = pieces.Sum(p => p.LongLength);
        var all = new byte[total];
        int at = 0;
        foreach (var piece in pieces) { piece.CopyTo(all, at); at += piece.Length; }
        return all;
    }

    private static void Collect(Stream stream, long address, List<byte[]> into, CancellationToken ct, int depth)
    {
        if (depth > 8) throw new InvalidDataException("The data block list is nested more deeply than this reader follows.");
        ct.ThrowIfCancellationRequested();

        var block = Block.At(stream, address, null);
        switch (block.Kind)
        {
            case "##DT":
            case "##DV":
            case "##RD":
                into.Add(block.Data);
                break;

            case "##DZ":
                into.Add(Decompress(block));
                break;

            case "##DL":
                // Link 0 continues the list; the rest are the blocks themselves.
                for (int i = 1; i < block.Links.Length; i++)
                    if (block.Links[i] != 0) Collect(stream, block.Links[i], into, ct, depth + 1);
                if (block.Link(0) != 0) Collect(stream, block.Link(0), into, ct, depth + 1);
                break;

            case "##HL":
                Collect(stream, block.Link(0), into, ct, depth + 1);
                break;

            default:
                throw new InvalidDataException($"Data block {block.Kind} is not one this reader implements.");
        }
    }

    private static byte[] Decompress(Block block)
    {
        byte zipType = block.Data[2];
        ulong originalLength = BinaryPrimitives.ReadUInt64LittleEndian(block.Data.AsSpan(8));

        // zip_type 1 transposes the records column-wise before deflating. Undoing deflate without
        // undoing the transposition yields exactly the expected number of bytes, in the wrong
        // order — and with no checksum in the format, nothing downstream can tell. Refused rather
        // than attempted.
        if (zipType != 0)
            throw new InvalidDataException(
                $"This file uses compressed blocks of zip type {zipType} (transposed deflate), which this reader does not implement.");

        var payload = block.Data.AsSpan(24).ToArray();
        var output = new byte[originalLength];
        using var source = new MemoryStream(payload);
        // asammdf and the ASAM examples write a zlib wrapper; a bare deflate stream is also legal.
        Stream decoder = payload.Length >= 2 && payload[0] == 0x78
            ? new ZLibStream(source, CompressionMode.Decompress)
            : new DeflateStream(source, CompressionMode.Decompress);
        using (decoder) decoder.ReadExactly(output);
        return output;
    }

    // ---------------- merge ----------------

    /// <summary>
    /// One data group per bus, so the file holds several already-sorted streams. Stateful
    /// decoding downstream needs them as one sequence in capture order.
    /// </summary>
    private static List<CanFrame> Merge(List<FrameGroup> groups, CancellationToken ct)
    {
        if (groups.Count == 1) return groups[0].Frames;

        int total = groups.Sum(g => g.Frames.Count);
        var merged = new List<CanFrame>(total);
        var at = new int[groups.Count];
        for (int n = 0; n < total; n++)
        {
            if ((n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            int best = -1;
            double bestTime = 0;
            for (int g = 0; g < groups.Count; g++)
            {
                if (at[g] >= groups[g].Frames.Count) continue;
                double t = groups[g].Frames[at[g]].Timestamp;
                if (best < 0 || t < bestTime) { best = g; bestTime = t; }
            }
            if (best < 0) break;
            merged.Add(groups[best].Frames[at[best]++]);
        }
        return merged;
    }

    // ---------------- block reader ----------------

    /// <summary>An MDF4 block: a four-character kind, a run of links, then its own data.</summary>
    private readonly struct Block
    {
        public required string Kind { get; init; }
        public required long[] Links { get; init; }
        public required byte[] Data { get; init; }

        public long Link(int index) => (uint)index < (uint)Links.Length ? Links[index] : 0;

        public static Block At(Stream stream, long address, string? expected)
        {
            stream.Position = address;
            Span<byte> header = stackalloc byte[24];
            stream.ReadExactly(header);

            string kind = Encoding.ASCII.GetString(header[..4]);
            if (expected is not null && kind != expected)
                throw new InvalidDataException($"Expected an {expected} block at 0x{address:X} but found {kind}.");

            long length = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
            long linkCount = BinaryPrimitives.ReadInt64LittleEndian(header[16..]);
            if (length < 24 || linkCount < 0 || 24 + linkCount * 8 > length)
                throw new InvalidDataException($"Block {kind} at 0x{address:X} declares a length that cannot be right.");

            var links = new long[linkCount];
            var linkBytes = new byte[linkCount * 8];
            stream.ReadExactly(linkBytes);
            for (int i = 0; i < linkCount; i++)
                links[i] = BinaryPrimitives.ReadInt64LittleEndian(linkBytes.AsSpan(i * 8));

            var data = new byte[length - 24 - linkCount * 8];
            stream.ReadExactly(data);
            return new Block { Kind = kind, Links = links, Data = data };
        }

        /// <summary>Text of the TX/MD block a link points at, or null.</summary>
        public string? Text(Stream stream, int linkIndex)
        {
            long address = Link(linkIndex);
            if (address == 0) return null;
            var block = At(stream, address, null);
            int end = Array.IndexOf(block.Data, (byte)0);
            return Encoding.UTF8.GetString(block.Data, 0, end < 0 ? block.Data.Length : end);
        }
    }
}
