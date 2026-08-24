namespace CanTerminal.Core.Slcan;

/// <summary>
/// The SLCAN wire grammar, kept free of I/O so every rule in it is testable without a device.
///
/// Dialect: WeAct USB2CANFDV2 SLCAN firmware (a CANable 2.0 derivative). Classic frames use the
/// Lawicel letters t/T/r/R; CAN FD adds d/D (no BRS) and b/B (BRS), lowercase for 11-bit and
/// uppercase for 29-bit identifiers. A line is LETTER + ID hex + one DLC hex digit + data hex,
/// terminated by CR. The firmware answers every command with CR (ok) or BELL 0x07 (fail), and
/// for frame transmissions the CR arrives only when the frame has actually left on the bus.
/// </summary>
internal static class SlcanProtocol
{
    /// <summary>Payload length for each DLC code. Codes 0–8 mean themselves; 9–15 are FD sizes.</summary>
    internal static readonly int[] FdLengths = [0, 1, 2, 3, 4, 5, 6, 7, 8, 12, 16, 20, 24, 32, 48, 64];

    /// <summary>Smallest DLC code whose length holds <paramref name="length"/> bytes.</summary>
    internal static int FdCodeFor(int length)
    {
        for (int code = 0; code < FdLengths.Length; code++)
            if (FdLengths[code] >= length) return code;
        throw new ArgumentException($"No CAN FD DLC holds {length} bytes.");
    }

    /// <summary>
    /// Pads a payload up to the next legal CAN FD length with zeroes — the bus cannot carry,
    /// say, 13 bytes, and the driver-based adapters pad the same way. Always returns a copy,
    /// so the frame published as the TX echo never aliases the caller's array.
    /// </summary>
    internal static byte[] PadToFd(byte[] data)
    {
        var result = new byte[FdLengths[FdCodeFor(data.Length)]];
        data.CopyTo(result, 0);
        return result;
    }

    /// <summary>Nominal (arbitration) bitrate presets: the firmware's S0..S8 table.</summary>
    private static readonly (int Bps, char Code)[] NominalRates =
    [
        (10_000, '0'), (20_000, '1'), (50_000, '2'), (100_000, '3'), (125_000, '4'),
        (250_000, '5'), (500_000, '6'), (800_000, '7'), (1_000_000, '8'),
    ];

    /// <summary>CAN FD data bitrate presets: the firmware's Y1..Y5 table.</summary>
    private static readonly (int Bps, char Code)[] DataRates =
    [
        (1_000_000, '1'), (2_000_000, '2'), (3_000_000, '3'), (4_000_000, '4'), (5_000_000, '5'),
    ];

    /// <summary>The CAN controller's clock. Every bitrate this device can produce divides it.</summary>
    private const long ClockHz = 60_000_000;

    // Bit timing the firmware accepts, per phase: a clock divider, then the segments either side
    // of the sample point. One bit takes (1 + seg1 + seg2) time quanta.
    private const int MaxDivider = 255;
    private const int NominalMinSeg1 = 2, NominalMaxSeg1 = 255, NominalMinSeg2 = 2, NominalMaxSeg2 = 128;
    private const int DataMinSeg1 = 1, DataMaxSeg1 = 32, DataMinSeg2 = 1, DataMaxSeg2 = 16;

    /// <summary>Where in the bit the receiver samples. 80% is the usual choice for CAN.</summary>
    private const double PreferredSamplePoint = 0.8;

    /// <summary>Classic CAN tops out here, and so does this device's arbitration phase.</summary>
    private const int MaxNominalBps = 1_000_000;

    /// <summary>What the isolated transceiver on this board is rated for.</summary>
    private const int MaxDataBps = 5_000_000;

    /// <summary>
    /// The command that sets the arbitration bitrate, e.g. "S6" or "S041706".
    ///
    /// A preset when the firmware has one — those carry the vendor's own sample point — and
    /// otherwise timing computed for the requested rate. Only presets used to be offered, which
    /// left the program refusing rates the device is perfectly capable of and, worse, saying the
    /// device did not support them.
    /// </summary>
    internal static string NominalCommand(int bps) => Command(
        'S', bps, MaxNominalBps, NominalRates, "bitrate",
        NominalMinSeg1, NominalMaxSeg1, NominalMinSeg2, NominalMaxSeg2);

    /// <summary>The command that sets the CAN FD data bitrate, e.g. "Y2" or "Y041706".</summary>
    internal static string DataCommand(int bps) => Command(
        'Y', bps, MaxDataBps, DataRates, "CAN FD data bitrate",
        DataMinSeg1, DataMaxSeg1, DataMinSeg2, DataMaxSeg2);

    private static string Command(char letter, int bps, int maxBps, (int Bps, char Code)[] presets, string what,
                                  int minSeg1, int maxSeg1, int minSeg2, int maxSeg2)
    {
        if (bps <= 0) throw new ArgumentException($"A {what} of {bps:N0} bit/s makes no sense.");
        if (bps > maxBps)
            throw new ArgumentException(
                $"{bps:N0} bit/s is above what this device runs: the highest {what} it supports is {maxBps:N0}.");

        foreach (var (rate, code) in presets)
            if (rate == bps) return $"{letter}{code}";

        if (Timing(bps, minSeg1, maxSeg1, minSeg2, maxSeg2) is { } t)
            return $"{letter}{t.Divider:X2}{t.Seg1:X2}{t.Seg2:X2}";

        // Not a limit of the device so much as of arithmetic: the bit has to come out as a whole
        // number of clock ticks. Naming the neighbours makes that actionable instead of a wall.
        var near = Reachable(minSeg1, maxSeg1, minSeg2, maxSeg2, maxBps)
            .OrderBy(r => Math.Abs(r - bps)).Take(3).OrderBy(r => r);
        throw new ArgumentException(
            $"A {what} of {bps:N0} bit/s cannot be timed exactly from this device's {ClockHz / 1_000_000} MHz clock. " +
            $"Closest it can do: {string.Join(", ", near.Select(r => $"{r:N0}"))}.");
    }

    /// <summary>Bit timing for a rate, or null when the clock will not divide into it.</summary>
    private static (int Divider, int Seg1, int Seg2)? Timing(int bps, int minSeg1, int maxSeg1, int minSeg2, int maxSeg2)
    {
        int minQuanta = 1 + minSeg1 + minSeg2, maxQuanta = 1 + maxSeg1 + maxSeg2;
        (int Divider, int Seg1, int Seg2)? best = null;
        double bestError = double.MaxValue;
        int bestQuanta = 0;

        for (int divider = 1; divider <= MaxDivider; divider++)
        {
            long ticksPerBit = (long)divider * bps;
            if (ClockHz % ticksPerBit != 0) continue;              // the bit would not be a whole number of ticks
            long quanta = ClockHz / ticksPerBit;
            if (quanta < minQuanta || quanta > maxQuanta) continue;

            // Put the sample point as near 80% as the segment limits allow.
            int seg1 = Math.Clamp((int)Math.Round(quanta * PreferredSamplePoint) - 1, minSeg1, maxSeg1);
            int seg2 = (int)quanta - 1 - seg1;
            if (seg2 < minSeg2) { seg2 = minSeg2; seg1 = (int)quanta - 1 - seg2; }
            if (seg2 > maxSeg2) { seg2 = maxSeg2; seg1 = (int)quanta - 1 - seg2; }
            if (seg1 < minSeg1 || seg1 > maxSeg1 || seg2 < minSeg2 || seg2 > maxSeg2) continue;

            double error = Math.Abs(((1.0 + seg1) / quanta) - PreferredSamplePoint);
            // More quanta per bit is the better tie-break: the sample point lands more precisely.
            if (error < bestError - 1e-9 || (Math.Abs(error - bestError) < 1e-9 && quanta > bestQuanta))
            {
                best = (divider, seg1, seg2);
                bestError = error;
                bestQuanta = (int)quanta;
            }
        }
        return best;
    }

    /// <summary>Every rate this device can time exactly, for naming the neighbours of one it cannot.</summary>
    private static IEnumerable<int> Reachable(int minSeg1, int maxSeg1, int minSeg2, int maxSeg2, int maxBps)
    {
        var rates = new HashSet<int>();
        for (int divider = 1; divider <= MaxDivider; divider++)
            for (long quanta = 1 + minSeg1 + minSeg2; quanta <= 1 + maxSeg1 + maxSeg2; quanta++)
            {
                long ticks = divider * quanta;
                if (ClockHz % ticks != 0) continue;
                long rate = ClockHz / ticks;
                if (rate is >= 1000 && rate <= maxBps) rates.Add((int)rate);
            }
        return rates;
    }

    /// <summary>
    /// The rate a computed command actually produces, and where it samples the bit. Only the
    /// tests use this — it is how they check the timing rather than trusting the same arithmetic
    /// that produced it.
    /// </summary>
    internal static (int Bps, double SamplePoint) Decode(string command)
    {
        if (command.Length != 7) throw new ArgumentException($"'{command}' is not a computed timing command.");
        int divider = Convert.ToInt32(command.Substring(1, 2), 16);
        int seg1 = Convert.ToInt32(command.Substring(3, 2), 16);
        int seg2 = Convert.ToInt32(command.Substring(5, 2), 16);
        long quanta = 1 + seg1 + seg2;
        return ((int)(ClockHz / (divider * quanta)), (1.0 + seg1) / quanta);
    }

    /// <summary>
    /// Builds the ASCII transmit command for one frame, without the trailing CR.
    /// FD payloads must already be padded to a legal length (<see cref="PadToFd"/>).
    /// </summary>
    internal static string BuildTxLine(uint id, byte[] payload, bool extended, bool fd, bool brs)
    {
        uint maxId = extended ? 0x1FFF_FFFFu : 0x7FFu;
        if (id > maxId)
            throw new ArgumentException($"ID 0x{id:X} does not fit an {(extended ? "29" : "11")}-bit identifier.");
        if (!fd && payload.Length > 8)
            throw new ArgumentException($"Classic CAN carries at most 8 bytes, not {payload.Length}.");

        char letter = fd
            ? brs ? (extended ? 'B' : 'b') : (extended ? 'D' : 'd')
            : extended ? 'T' : 't';
        int dlc = fd ? FdCodeFor(payload.Length) : payload.Length;
        if (fd && FdLengths[dlc] != payload.Length)
            throw new ArgumentException($"{payload.Length} bytes is not a legal CAN FD length — pad first.");

        return letter
            + id.ToString(extended ? "X8" : "X3")
            + dlc.ToString("X1")
            + Convert.ToHexString(payload);
    }

    /// <summary>One received frame line, decoded. <c>Data</c> for a remote frame is DLC zeroes,
    /// which is what the driver-based adapters surface for an RTR as well.</summary>
    internal readonly record struct RxFrame(uint Id, bool Extended, bool Fd, bool Brs, bool Remote, byte[] Data);

    /// <summary>
    /// Parses one CR-stripped line from the device. Returns false for anything that is not a
    /// well-formed frame line — the caller counts those rather than guessing at them.
    /// </summary>
    internal static bool TryParseRxLine(string line, out RxFrame frame)
    {
        frame = default;
        if (line.Length < 5) return false;

        bool extended, fd, brs, remote;
        switch (line[0])
        {
            case 't': (extended, fd, brs, remote) = (false, false, false, false); break;
            case 'T': (extended, fd, brs, remote) = (true, false, false, false); break;
            case 'r': (extended, fd, brs, remote) = (false, false, false, true); break;
            case 'R': (extended, fd, brs, remote) = (true, false, false, true); break;
            case 'd': (extended, fd, brs, remote) = (false, true, false, false); break;
            case 'D': (extended, fd, brs, remote) = (true, true, false, false); break;
            case 'b': (extended, fd, brs, remote) = (false, true, true, false); break;
            case 'B': (extended, fd, brs, remote) = (true, true, true, false); break;
            default: return false;
        }

        int idDigits = extended ? 8 : 3;
        if (line.Length < 1 + idDigits + 1) return false;
        if (!uint.TryParse(line.AsSpan(1, idDigits), System.Globalization.NumberStyles.HexNumber, null, out uint id))
            return false;

        int dlc = HexDigit(line[1 + idDigits]);
        if (dlc < 0) return false;
        if (!fd && dlc > 8) return false;               // classic DLC is 0..8
        int length = fd ? FdLengths[dlc] : dlc;

        int dataAt = 1 + idDigits + 1;
        if (remote)
        {
            if (line.Length != dataAt) return false;    // an RTR carries no data bytes
            frame = new RxFrame(id, extended, fd, brs, remote, new byte[length]);
            return true;
        }

        if (line.Length != dataAt + (length * 2)) return false;
        var data = new byte[length];
        for (int i = 0; i < length; i++)
        {
            int hi = HexDigit(line[dataAt + (i * 2)]);
            int lo = HexDigit(line[dataAt + (i * 2) + 1]);
            if (hi < 0 || lo < 0) return false;
            data[i] = (byte)((hi << 4) | lo);
        }
        frame = new RxFrame(id, extended, fd, brs, remote, data);
        return true;
    }

    private static int HexDigit(char c) => c switch
    {
        >= '0' and <= '9' => c - '0',
        >= 'A' and <= 'F' => c - 'A' + 10,
        >= 'a' and <= 'f' => c - 'a' + 10,
        _ => -1,
    };
}
