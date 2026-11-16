using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

public static class GsiState
{
    private static readonly Regex Name = new("^[a-zA-Z0-9_]{1,80}$", RegexOptions.CultureInvariant);
    private static string Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static double Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : throw new ArgumentException("Missing GSI number");
    private static double? Position(JsonElement e, string key) => e.TryGetProperty(key, out _) ? Number(e, key) : null;
    private static StateEntry[] Entries(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return [];
        if (value.EnumerateObject().Count() > 40) throw new ArgumentException("Too many GSI entries");
        return value.EnumerateObject().Select(p => p.Value).Where(v => v.ValueKind == JsonValueKind.Object)
            .Where(v => Text(v, "name") is not ("" or "empty")).Select(v => new StateEntry(Text(v, "name"),
                v.TryGetProperty("level", out var level) && level.TryGetInt32(out int l) ? l : null,
                v.TryGetProperty("cooldown", out var cooldown) && cooldown.TryGetDouble(out double cd) ? cd : null)).ToArray();
    }
    public static OwnState Parse(JsonElement root)
    {
        var p = root.GetProperty("player"); var h = root.GetProperty("hero"); var m = root.GetProperty("map");
        // Extract flat player fields only; nested observer groups are not parsed.
        // Shape alone cannot certify ownership or fog semantics for every spectator mode.
        string hero = Text(h, "name");
        string team = Text(p, "team_name") switch {
            "radiant" or "DOTA_TEAM_GOODGUYS" => "radiant",
            "dire" or "DOTA_TEAM_BADGUYS" => "dire", _ => throw new ArgumentException("Missing own team"),
        };
        string match = m.TryGetProperty("matchid", out var id) ? id.ToString() : "";
        var state = new OwnState(match, hero, team, Number(m, "clock_time"), h.GetProperty("alive").GetBoolean(),
            Number(h, "health"), Number(h, "mana"), Position(h, "xpos"), Position(h, "ypos"),
            Entries(root, "items"), Entries(root, "abilities"));
        Validate(state);
        return state;
    }
    public static void Validate(OwnState s)
    {
        if (s.MatchId.Length is < 1 or > 24 || !s.MatchId.All(char.IsAsciiDigit) ||
            !s.Hero.StartsWith("npc_dota_hero_", StringComparison.Ordinal) || !Name.IsMatch(s.Hero) ||
            s.Team is not ("radiant" or "dire") || !Wire.Finite(s.Clock, -3600, 43200) ||
            !Wire.Finite(s.Health, 0, 100000) || !Wire.Finite(s.Mana, 0, 100000) ||
            (s.X is double x && !Wire.Finite(x, -16000, 16000)) || (s.Y is double y && !Wire.Finite(y, -16000, 16000)) ||
            s.Items == null || s.Abilities == null || s.Items.Length > 40 || s.Abilities.Length > 40)
            throw new ArgumentException("Invalid own-player state");
        foreach (var entry in s.Items.Concat(s.Abilities))
            if (entry == null || !Name.IsMatch(entry.Name) || entry.Level is < 0 or > 40 ||
                (entry.Cooldown is double cd && !Wire.Finite(cd, 0, 10000))) throw new ArgumentException("Invalid GSI entry");
    }
}
