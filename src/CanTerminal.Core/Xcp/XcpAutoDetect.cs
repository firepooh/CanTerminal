namespace CanTerminal.Core.Xcp;

/// <summary>
/// Guesses the XCP request/response CAN ID pair from already-captured traffic.
///
/// Nothing is transmitted — this only reads the monitor's ring buffer. Detection therefore
/// depends on what the capture happens to contain, and the result is always a ranked list of
/// candidates with the evidence behind them, never a silent assumption.
/// </summary>
public static class XcpAutoDetect
{
    /// <summary>How long a slave may take to answer before the frames are considered unrelated.</summary>
    private static readonly double ResponseWindowSeconds = 0.2;

    public sealed record Candidate(string Channel, uint RequestId, uint ResponseId, int Score, string Evidence)
    {
        public override string ToString() =>
            $"req 0x{RequestId:X} / rsp 0x{ResponseId:X} on {Channel} — {Evidence}";
    }

    /// <summary>Ranked candidates, best first. Empty when nothing in the capture looks like XCP.</summary>
    public static List<Candidate> Scan(IReadOnlyList<CanFrame> frames)
    {
        var connect = new Dictionary<(string, uint, uint), int>();   // CONNECT + 8-byte reply
        var slaveId = new Dictionary<(string, uint, uint), int>();   // GET_SLAVE_ID exchange
        var generic = new Dictionary<(string, uint, uint), int>();   // any command + RES/ERR reply

        for (int i = 0; i < frames.Count; i++)
        {
            var cmd = frames[i];
            if (cmd.Data.Length == 0 || cmd.Data[0] < XcpTables.CommandPidMin) continue;

            bool isConnect = cmd.Data[0] == 0xFF && cmd.Data.Length == 2;
            bool isGetSlaveId = cmd.Data[0] == 0xF2 && cmd.Data.Length >= 5 &&
                                cmd.Data[1] == 0xFF && cmd.Data[2] == (byte)'X' &&
                                cmd.Data[3] == (byte)'C' && cmd.Data[4] == (byte)'P';

            for (int j = i + 1; j < frames.Count; j++)
            {
                var rsp = frames[j];
                if (rsp.Timestamp - cmd.Timestamp > ResponseWindowSeconds) break;
                if (rsp.Channel != cmd.Channel || rsp.ArbId == cmd.ArbId) continue;
                if (rsp.Data.Length == 0) continue;

                byte p = rsp.Data[0];
                if (p != XcpTables.PidRes && p != XcpTables.PidErr) continue;

                var key = (cmd.Channel, cmd.ArbId, rsp.ArbId);
                // A CONNECT positive response is always exactly 8 bytes — the strongest signature.
                if (isConnect && p == XcpTables.PidRes && rsp.Data.Length == 8) Bump(connect, key);
                else if (isGetSlaveId && p == XcpTables.PidRes) Bump(slaveId, key);
                else Bump(generic, key);
                break;  // only the first plausible reply counts
            }
        }

        var scored = new Dictionary<(string Channel, uint Req, uint Rsp), (int Score, List<string> Why)>();
        Merge(scored, connect, 100, "CONNECT exchange");
        Merge(scored, slaveId, 100, "GET_SLAVE_ID exchange");
        Merge(scored, generic, 1, "command/response pairs");

        return scored
            .Select(kv => new Candidate(kv.Key.Channel, kv.Key.Req, kv.Key.Rsp, kv.Value.Score,
                                        string.Join(", ", kv.Value.Why)))
            .OrderByDescending(c => c.Score)
            .ToList();
    }

    private static void Bump<T>(Dictionary<T, int> map, T key) where T : notnull =>
        map[key] = map.TryGetValue(key, out int n) ? n + 1 : 1;

    private static void Merge(
        Dictionary<(string, uint, uint), (int Score, List<string> Why)> into,
        Dictionary<(string, uint, uint), int> from, int weight, string label)
    {
        foreach (var (key, count) in from)
        {
            if (!into.TryGetValue(key, out var acc)) acc = (0, []);
            acc.Score += count * weight;
            acc.Why.Add($"{count}× {label}");
            into[key] = acc;
        }
    }
}
