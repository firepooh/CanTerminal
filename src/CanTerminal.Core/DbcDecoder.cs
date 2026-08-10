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
    private sealed record SignalPlan(Signal Signal, MultiplexingRole Role, int Group, int NeedsBytes);
    private sealed record MessagePlan(Message Message, Signal? Multiplexor, int MuxNeedsBytes, SignalPlan[] Signals);

    private volatile Dictionary<(uint Id, bool Ext), MessagePlan> _byId = [];

    public string? FilePath { get; private set; }

    public void Load(string path)
    {
        var dbc = Parser.ParseFromPath(path);
        var map = new Dictionary<(uint, bool), MessagePlan>();
        foreach (var msg in dbc.Messages)
        {
            // Precompute multiplexing info and the payload length each signal needs, once per
            // signal (parsing it per frame is wasteful).
            var plans = msg.Signals.Select(s =>
            {
                var mux = s.MultiplexingInfo();
                return new SignalPlan(s, mux.Role, mux.Group, RequiredBytes(s));
            }).ToArray();
            var muxorPlan = plans.FirstOrDefault(p => p.Role == MultiplexingRole.Multiplexor);
            map[(msg.ID, msg.IsExtID)] = new MessagePlan(   // DbcParserLib exposes the cleaned ID + IsExtID
                msg, muxorPlan?.Signal, muxorPlan?.NeedsBytes ?? 0, plans);
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
        int length = f.Data.Length;
        if (length == 0 || length > 8) return plan.Message.Name;

        ulong raw = 0;
        for (int i = 0; i < length; i++) raw |= (ulong)f.Data[i] << (8 * i);

        int? activeGroup = null;
        // A multiplexor read out of the zero fill selects a group at random, so a short frame
        // must not resolve one at all.
        if (plan.Multiplexor is not null && length >= plan.MuxNeedsBytes)
        {
            try { activeGroup = (int)Packer.RxSignalUnpack(raw, plan.Multiplexor); }
            catch { /* leave null: no multiplexed signals will match */ }
        }

        var sb = new StringBuilder(plan.Message.Name).Append(": ");
        bool first = true;
        int omitted = 0;
        foreach (var (sig, role, group, needs) in plan.Signals)
        {
            // Skip signals from inactive mux groups; extended multiplexing (role Unknown)
            // can't be resolved reliably, so skip those too rather than show garbage.
            if (role == MultiplexingRole.Multiplexed && group != activeGroup) continue;
            if (role == MultiplexingRole.Unknown) continue;

            // The frame is shorter than the DBC says this signal is. RxSignalUnpack reads the
            // zero fill above without complaint and returns a number that looks exactly like a
            // measurement — for a signal straddling the end of the payload it is not even a
            // suspicious zero. Saying nothing is the only honest answer.
            if (length < needs) { omitted++; continue; }

            double val;
            try { val = Packer.RxSignalUnpack(raw, sig); }
            catch { continue; }
            if (!first) sb.Append(", ");
            first = false;
            sb.Append(sig.Name).Append('=').Append(val.ToString("0.###"));
            if (!string.IsNullOrEmpty(sig.Unit)) sb.Append(' ').Append(sig.Unit);
        }
        // Reported rather than silent: "this frame is shorter than the database expects" is
        // itself worth seeing — it is usually a truncated or misconfigured sender.
        if (omitted > 0)
            sb.Append(first ? "" : ", ").Append($"[{omitted} signal(s) past the {length}-byte payload]");
        return sb.ToString();
    }

    /// <summary>
    /// Payload length, in bytes, a signal needs before its value means anything.
    ///
    /// The two byte orders spill in opposite directions and cannot share a formula. Intel
    /// numbers from the LSB upward, so the span simply ends at StartBit + Length. Motorola
    /// numbers from the MSB and walks *down* through the bits of the start byte before
    /// continuing at the top of the next one — which is why a 16-bit Motorola signal at
    /// StartBit 7 occupies bytes 0-1, while StartBit + Length would claim it needs three.
    /// </summary>
    private static int RequiredBytes(Signal s)
    {
        if (s.ByteOrder == 1) return (s.StartBit + s.Length + 7) / 8;
        int startByte = s.StartBit / 8;
        int bitsInStartByte = (s.StartBit % 8) + 1;
        return s.Length <= bitsInStartByte
            ? startByte + 1
            : startByte + 1 + ((s.Length - bitsInStartByte + 7) / 8);
    }
}
