using System.Text;
using DbcParserLib;
using DbcParserLib.Model;

namespace CanTerminal.Core;

/// <summary>
/// Decodes frames against a loaded DBC file (classic CAN, payload ≤ 8 bytes).
/// Multiplexed messages decode only the signals of the currently active mux group.
/// Thread-safe for concurrent Decode calls; Load swaps the lookup table atomically.
/// </summary>
public sealed class DbcDecoder
{
    private sealed record SignalPlan(Signal Signal, MultiplexingRole Role, int Group);
    private sealed record MessagePlan(Message Message, Signal? Multiplexor, SignalPlan[] Signals);

    private volatile Dictionary<(uint Id, bool Ext), MessagePlan> _byId = [];

    public string? FilePath { get; private set; }

    public void Load(string path)
    {
        var dbc = Parser.ParseFromPath(path);
        var map = new Dictionary<(uint, bool), MessagePlan>();
        foreach (var msg in dbc.Messages)
        {
            // Precompute multiplexing info once per signal (parsing it per frame is wasteful).
            var plans = msg.Signals.Select(s =>
            {
                var mux = s.MultiplexingInfo();
                return new SignalPlan(s, mux.Role, mux.Group);
            }).ToArray();
            var muxor = plans.FirstOrDefault(p => p.Role == MultiplexingRole.Multiplexor)?.Signal;
            map[(msg.ID, msg.IsExtID)] = new MessagePlan(msg, muxor, plans); // DbcParserLib exposes the cleaned ID + IsExtID
        }
        _byId = map;
        FilePath = path;
    }

    public void Unload()
    {
        _byId = [];
        FilePath = null;
    }

    public bool IsLoaded => FilePath != null;

    public string? MessageName(CanFrame f) =>
        _byId.TryGetValue((f.ArbId, f.IsExtended), out var m) ? m.Message.Name : null;

    /// <summary>Returns "MsgName: Sig=val unit, ..." or null when the ID is unknown.</summary>
    public string? Decode(CanFrame f)
    {
        var map = _byId;
        if (!map.TryGetValue((f.ArbId, f.IsExtended), out var plan)) return null;
        if (f.Data.Length == 0 || f.Data.Length > 8) return plan.Message.Name;

        ulong raw = 0;
        for (int i = 0; i < f.Data.Length; i++) raw |= (ulong)f.Data[i] << (8 * i);

        int? activeGroup = null;
        if (plan.Multiplexor is not null)
        {
            try { activeGroup = (int)Packer.RxSignalUnpack(raw, plan.Multiplexor); }
            catch { /* leave null: no multiplexed signals will match */ }
        }

        var sb = new StringBuilder(plan.Message.Name).Append(": ");
        bool first = true;
        foreach (var (sig, role, group) in plan.Signals)
        {
            // Skip signals from inactive mux groups; extended multiplexing (role Unknown)
            // can't be resolved reliably, so skip those too rather than show garbage.
            if (role == MultiplexingRole.Multiplexed && group != activeGroup) continue;
            if (role == MultiplexingRole.Unknown) continue;

            double val;
            try { val = Packer.RxSignalUnpack(raw, sig); }
            catch { continue; }
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(sig.Name).Append('=').Append(val.ToString("0.###"));
            if (!string.IsNullOrEmpty(sig.Unit)) sb.Append(' ').Append(sig.Unit);
        }
        return sb.ToString();
    }
}
