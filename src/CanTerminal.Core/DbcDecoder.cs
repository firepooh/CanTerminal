using System.Text;
using DbcParserLib;
using DbcParserLib.Model;

namespace CanTerminal.Core;

/// <summary>
/// Decodes frames against a loaded DBC file (classic CAN, payload ≤ 8 bytes).
/// Thread-safe for concurrent Decode calls; Load swaps the lookup table atomically.
/// </summary>
public sealed class DbcDecoder
{
    private volatile Dictionary<(uint Id, bool Ext), Message> _byId = [];

    public string? FilePath { get; private set; }

    public void Load(string path)
    {
        var dbc = Parser.ParseFromPath(path);
        var map = new Dictionary<(uint, bool), Message>();
        foreach (var msg in dbc.Messages)
            map[(msg.ID, msg.IsExtID)] = msg; // DbcParserLib exposes the cleaned ID + IsExtID
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
        _byId.TryGetValue((f.ArbId, f.IsExtended), out var m) ? m.Name : null;

    /// <summary>Returns "MsgName: Sig=val unit, ..." or null when the ID is unknown.</summary>
    public string? Decode(CanFrame f)
    {
        var map = _byId;
        if (!map.TryGetValue((f.ArbId, f.IsExtended), out var msg)) return null;
        if (f.Data.Length == 0 || f.Data.Length > 8) return msg.Name;

        ulong raw = 0;
        for (int i = 0; i < f.Data.Length; i++) raw |= (ulong)f.Data[i] << (8 * i);

        var sb = new StringBuilder(msg.Name).Append(": ");
        bool first = true;
        foreach (var sig in msg.Signals)
        {
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
