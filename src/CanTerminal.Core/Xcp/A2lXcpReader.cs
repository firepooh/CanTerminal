using System.Globalization;

namespace CanTerminal.Core.Xcp;

/// <summary>
/// Pulls the CAN IDs out of the IF_DATA XCP_ON_CAN block of an A2L file — the authoritative
/// source when one is available. Only that block is read; this is deliberately not a full A2L
/// parser, so it stays fast on the tens-of-megabytes files real projects have.
/// </summary>
public static class A2lXcpReader
{
    /// <summary>A2L marks an ID as 29-bit by setting the top bit of the value.</summary>
    private const uint ExtendedFlag = 0x80000000;

    public sealed record Result(uint? Master, uint? Slave, uint? Broadcast, bool Extended)
    {
        public bool HasPair => Master is not null && Slave is not null;
    }

    public static Result Read(string path)
    {
        uint? master = null, slave = null, broadcast = null;
        bool extended = false;
        bool inBlock = false;
        bool inComment = false;
        string? pendingKey = null;
        string? pendingBegin = null;

        foreach (var raw in File.ReadLines(path))
        {
            foreach (var token in Tokenize(raw, ref inComment))
            {
                if (pendingBegin is not null)
                {
                    // "/begin XCP_ON_CAN" opens the block we care about.
                    if (pendingBegin == "/begin" && token.Equals("XCP_ON_CAN", StringComparison.OrdinalIgnoreCase))
                        inBlock = true;
                    else if (pendingBegin == "/end" && token.Equals("XCP_ON_CAN", StringComparison.OrdinalIgnoreCase))
                        inBlock = false;
                    pendingBegin = null;
                    if (!inBlock && master is not null) goto done;   // block finished, we have what we came for
                    continue;
                }

                if (token is "/begin" or "/end") { pendingBegin = token; continue; }
                if (!inBlock) continue;

                if (pendingKey is not null)
                {
                    if (TryParseId(token, out uint value))
                    {
                        bool ext = (value & ExtendedFlag) != 0;
                        uint id = ext ? value & 0x1FFFFFFF : value;
                        extended |= ext;
                        switch (pendingKey)
                        {
                            case "CAN_ID_MASTER": master = id; break;
                            case "CAN_ID_SLAVE": slave = id; break;
                            case "CAN_ID_BROADCAST": broadcast = id; break;
                        }
                    }
                    pendingKey = null;
                    continue;
                }

                if (token is "CAN_ID_MASTER" or "CAN_ID_SLAVE" or "CAN_ID_BROADCAST")
                    pendingKey = token;
            }
        }
    done:
        return new Result(master, slave, broadcast, extended);
    }

    private static IEnumerable<string> Tokenize(string line, ref bool inComment)
    {
        var tokens = new List<string>();
        int i = 0;
        var current = new System.Text.StringBuilder();

        while (i < line.Length)
        {
            if (inComment)
            {
                int end = line.IndexOf("*/", i, StringComparison.Ordinal);
                if (end < 0) return tokens;
                inComment = false;
                i = end + 2;
                continue;
            }
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '*')
            {
                Flush(tokens, current);
                inComment = true;
                i += 2;
                continue;
            }
            if (i + 1 < line.Length && line[i] == '/' && line[i + 1] == '/')
            {
                break;  // line comment
            }
            if (char.IsWhiteSpace(line[i]))
            {
                Flush(tokens, current);
                i++;
                continue;
            }
            current.Append(line[i++]);
        }
        Flush(tokens, current);
        return tokens;
    }

    private static void Flush(List<string> tokens, System.Text.StringBuilder sb)
    {
        if (sb.Length == 0) return;
        tokens.Add(sb.ToString());
        sb.Clear();
    }

    private static bool TryParseId(string token, out uint value) =>
        token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? uint.TryParse(token.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : uint.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
