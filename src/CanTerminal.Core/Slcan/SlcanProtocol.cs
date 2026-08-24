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

    internal static char NominalBitrateCode(int bps) => Code(NominalRates, bps, "bitrate");

    internal static char DataBitrateCode(int bps) => Code(DataRates, bps, "FD data bitrate");

    private static char Code((int Bps, char Code)[] table, int bps, string what)
    {
        foreach (var (rate, code) in table)
            if (rate == bps) return code;
        throw new ArgumentException(
            $"This device does not support a {what} of {bps:N0} bit/s. " +
            $"Supported: {string.Join(", ", table.Select(t => $"{t.Bps:N0}"))}.");
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
