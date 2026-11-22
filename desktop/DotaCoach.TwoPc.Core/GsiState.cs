using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

public static partial class GsiState
{
    private static readonly Regex Name = new("^[a-zA-Z0-9_]{1,80}$", RegexOptions.CultureInvariant);
    private static readonly Regex ItemSlot = new(@"\A(?:slot|stash|teleport|neutral|preserved_neutral)[0-9]{1,2}\z");
    private static readonly Regex AbilitySlot = new(@"\Aability[0-9]{1,2}\z");
    private static readonly Regex Multiplexed = new(@"\A(?:team[0-9]+|player[0-9]+|allplayers|worldsnapshots|omniscient|spectator|observer|coach|replay)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex ViewFlag = new(@"\A(?:is_?)?(?:spectat(?:ing|or)|observer|observing|coach|coaching|replay|in_?replay|watching_?replay|playing_?demo)\z",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly string[] Disabled = ["stunned", "hexed", "disarmed", "rooted", "command_restricted",
        "channeling", "channelling", "is_channeling", "is_channelling", "teleporting", "taunted", "feared", "nightmared",
        "sleeping", "out_of_game"];
    private static readonly string[] Nonstandard = ["is_custom_game", "is_arcade", "is_turbo", "is_hero_demo", "is_demo"];
    private static string Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";
    private static double Number(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.TryGetDouble(out var n) && double.IsFinite(n) ? n : throw new ArgumentException("Missing GSI number");
    private static double? Position(JsonElement e, string key) => e.TryGetProperty(key, out _) ? Number(e, key) : null;
    private static StateEntry[] Entries(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Object) return [];
        if (value.EnumerateObject().Count() > 40) throw new ArgumentException("Too many GSI entries");
        var slots = name == "items" ? ItemSlot : AbilitySlot;
        return value.EnumerateObject().Where(p => slots.IsMatch(p.Name) && p.Value.ValueKind == JsonValueKind.Object &&
            Text(p.Value, "name") is not ("" or "empty")).Select(p => new StateEntry(Text(p.Value, "name"),
                p.Value.TryGetProperty("level", out var level) && level.TryGetInt32(out int l) ? l : null,
                p.Value.TryGetProperty("cooldown", out var cooldown) && cooldown.TryGetDouble(out double cd) ? cd : null, p.Name)).ToArray();
    }
    // Inspect keys in the own sections AND change envelopes, but never project from deltas, minimap,
    // buildings, events etc. A flat hero can coexist with observer groups or a replay/Plus view.
    private static bool ParticipantShape(JsonElement root, bool strict = false)
    {
        int budget = strict ? 32_768 : 4096; // The lifecycle witness also checks keys inside discarded minimap units.
        bool Inspect(JsonElement e, bool top = false, bool ownHero = false)
        {
            if (e.ValueKind == JsonValueKind.Array) {
                foreach (var v in e.EnumerateArray()) if (--budget < 0 || !Inspect(v)) return false;
            } else if (e.ValueKind == JsonValueKind.Object) {
                var keys = new HashSet<string>(strict ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
                foreach (var prop in e.EnumerateObject()) {
                    string k = prop.Name;
                    if (--budget < 0 || !keys.Add(k) || Multiplexed.IsMatch(k) || k is "spectator_slot" or "observer_slot") return false;
                    if (ViewFlag.IsMatch(k)) { if (prop.Value.ValueKind != JsonValueKind.False) return false; continue; }
                    if (strict && ownHero && k == "permanent_buffs") {
                        if (!BenignPermanentBuffs(prop.Value)) return false;
                        continue;
                    }
                    if (strict && top && prop.Value.ValueKind != JsonValueKind.Object &&
                        !(k == "events" && prop.Value.ValueKind == JsonValueKind.Array)) return false;
                    if (k == "auth") { if (strict && !Inspect(prop.Value)) return false; continue; }
                    if (k is "minimap" or "buildings" or "wearables" or "couriers" or "neutralitems" or "roshan" or "events" or "league") {
                        if (strict) { if (!Inspect(prop.Value)) return false; }
                        else if (prop.Value.ValueKind == JsonValueKind.Object)
                            foreach (var field in prop.Value.EnumerateObject())
                                if (--budget < 0 || Multiplexed.IsMatch(field.Name)) return false;
                        continue;
                    }
                    if (top && k is not ("provider" or "map" or "player" or "hero" or "abilities" or "items" or
                        "draft" or "previously" or "added" or "removed")) return false;
                    if (!Inspect(prop.Value, ownHero: top && k == "hero")) return false;
                }
            }
            return true;
        }
        return Inspect(root, top: true);
    }
    private static bool Int(JsonElement x, out int n)
    {
        n = 0; return x.ValueKind == JsonValueKind.Number && x.TryGetInt32(out n);
    }
    internal static long? PacketUnixMs(JsonElement root, long utcNow, int maxAgeMs = MinuteFarmContext.FreshMs - 1)
    {
        if (!root.TryGetProperty("provider", out var p) || p.ValueKind != JsonValueKind.Object ||
            Text(p, "name") != "Dota 2" || !p.TryGetProperty("appid", out var app) || !Int(app, out int id) || id != 570 ||
            !p.TryGetProperty("timestamp", out var t)) return null;
        long sec;
        if (t.ValueKind == JsonValueKind.Number) { if (!t.TryGetInt64(out sec)) return null; }
        else if (t.ValueKind != JsonValueKind.String || !long.TryParse(t.GetString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out sec)) return null;
        if (sec is < 1 or > 99_999_999_999) return null;
        long age = utcNow - sec * 1000;
        return age >= -500 && age <= maxAgeMs ? sec * 1000 : null;
    }
    private const ulong SteamBase = 76561197960265728;
    private static bool SteamId(JsonElement id, out ulong n)
    {
        n = 0;
        return (id.ValueKind == JsonValueKind.String && ulong.TryParse(id.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out n) ||
            id.ValueKind == JsonValueKind.Number && id.TryGetUInt64(out n)) &&
            n > SteamBase && n <= SteamBase + uint.MaxValue;
    }
    // Local-only continuity key after participant validation. Never part of OwnState or diagnostics.
    internal static (ulong User, int Seat, int TeamSeat) ParticipantKey(JsonElement root)
    {
        var p = root.GetProperty("player");
        return (p.TryGetProperty("steamid", out var id) && SteamId(id, out ulong user) ? user : 0,
            p.TryGetProperty("player_slot", out var s) && Int(s, out int seat) ? seat : -1,
            p.TryGetProperty("team_slot", out var t) && Int(t, out int teamSeat) ? teamSeat : -1);
    }
    private static bool SameTeam(JsonElement e, string key, string team) => !e.TryGetProperty(key, out var v) ||
        (v.ValueKind == JsonValueKind.Number && Int(v, out int n) && n == (team == "radiant" ? 2 : 3)) ||
        (v.ValueKind == JsonValueKind.String && (v.GetString() switch {
            "radiant" or "DOTA_TEAM_GOODGUYS" => "radiant", "dire" or "DOTA_TEAM_BADGUYS" => "dire", _ => null }) == team);
    private static double? Stat(JsonElement hero, double max, params ReadOnlySpan<string> names)
    {
        double? reading = null;
        foreach (string key in names) if (hero.TryGetProperty(key, out var v)) {
            if (v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out double n) || !Wire.Finite(n, 1, max) ||
                reading.HasValue && reading.Value != n) return null;
            reading = n;
        }
        return reading;
    }
    private static bool FalseOrAbsent(JsonElement e, string key) =>
        !e.TryGetProperty(key, out var v) || v.ValueKind == JsonValueKind.False;
    private static bool TryOptionalBool(JsonElement e, string key, out bool? value)
    {
        value = null;
        if (!e.TryGetProperty(key, out var v)) return true;
        if (v.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return false;
        value = v.GetBoolean(); return true;
    }
    private static bool CompatibleMode(JsonElement m)
    {
        if (m.TryGetProperty("name", out var name) && (name.ValueKind != JsonValueKind.String ||
                name.GetString() is not ("dota" or "start" or "")) || Nonstandard.Any(k => !FalseOrAbsent(m, k))) return false;
        foreach (var key in new[] { "customgamename", "custom_game_name" })
            if (m.TryGetProperty(key, out var custom) && (custom.ValueKind != JsonValueKind.String || custom.GetString() != "")) return false;
        foreach (var key in new[] { "game_mode", "gamemode" }) if (m.TryGetProperty(key, out var mode)) {
            if (mode.ValueKind == JsonValueKind.Number) {
                // Explicit custom, ability draft, event, turbo or mutation. Absence is NOT proof of standard mode.
                if (!Int(mode, out int code) || code is 15 or 18 or 19 or 23 or 24) return false;
            } else if (mode.ValueKind != JsonValueKind.String ||
                Regex.IsMatch(mode.GetString()!, "arcade|custom|replay|demo|turbo|ability.?draft|mutation|event|^(15|18|19|23|24)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)) return false;
        }
        return true;
    }
    private static bool OwnParticipant(JsonElement root, JsonElement p, string team, out (ulong User, int Seat, int TeamSeat) actor,
        bool checkAccountId = false, bool correlatedEnd = false)
    {
        actor = default;
        bool ReadUser(JsonElement e, out ulong user)
        {
            user = 0;
            return (!e.TryGetProperty("steamid", out var id) || SteamId(id, out user)) &&
                (!checkAccountId || ReadAccount(e, ref user));
        }
        bool activity = p.TryGetProperty("activity", out var a);
        if (activity && (a.ValueKind != JsonValueKind.String || a.GetString() != "playing")) return false;
        // A player ID by itself is NOT proof of a playing participant. Only a terminal candidate
        // may omit both activity and provider ID; Observe must then match its exact previously trusted user.
        if (!ReadUser(p, out ulong self) || !ReadUser(root.GetProperty("provider"), out ulong local) ||
            local != 0 && local != self || !activity && local == 0 && !(correlatedEnd && self != 0)) return false;
        // player_slot is an ID, not a seat on a five-player team or proof of ownership. Keep the
        // separate playable team_slot check and the previously accepted packed Dire representation.
        int slot = -1, teamSlot = -1;
        if (p.TryGetProperty("player_slot", out var ps) && (!Int(ps, out slot) ||
                !(slot is >= 0 and < 64 || team == "dire" && slot is >= 128 and <= 132)) ||
            p.TryGetProperty("team_slot", out var ts) && (!Int(ts, out teamSlot) || teamSlot is < 0 or > 4) ||
            !SameTeam(p, "team", team)) return false;
        actor = (self, slot, teamSlot);
        return true;
    }
    private static bool ReadPause(JsonElement m, out bool? paused)
    {
        paused = null;
        if (!TryOptionalBool(m, "paused", out var a) || !TryOptionalBool(m, "is_paused", out var b) ||
            a.HasValue && b.HasValue && a != b) return false;
        paused = a ?? b;
        return true;
    }
    private static FarmPlayerTelemetry? Farm(JsonElement root, JsonElement p, JsonElement h, JsonElement m, string team)
    {
        // Silence and mute restrict actives, not walking or attacking. Report them without fencing the route.
        if (PacketUnixMs(root, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) == null || !ParticipantShape(root) ||
            !CompatibleMode(m) || Disabled.Any(k => !FalseOrAbsent(h, k)) ||
            !TryOptionalBool(h, "silenced", out var silenced) || !TryOptionalBool(h, "muted", out var muted)) return null;
        foreach (var key in new[] { "abilities", "items" }) if (root.TryGetProperty(key, out var section)) {
            if (section.ValueKind != JsonValueKind.Object) return null;
            foreach (var entry in section.EnumerateObject()) if ((key == "items" ? ItemSlot : AbilitySlot).IsMatch(entry.Name) &&
                (entry.Value.ValueKind != JsonValueKind.Object || !FalseOrAbsent(entry.Value, "channeling") ||
                 !FalseOrAbsent(entry.Value, "is_channeling"))) return null;
        }
        if (!OwnParticipant(root, p, team, out _) || !SameTeam(h, "team", team) || !SameTeam(h, "team_name", team) ||
            h.TryGetProperty("id", out var hid) && (!Int(hid, out int heroId) || heroId <= 0) ||
            !ReadPause(m, out var paused)) return null;
        double? Max(string key, double min) => h.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out double n) && Wire.Finite(n, min, 100_000) ? n : null;
        var maxHp = Max("max_health", Math.Max(1, Number(h, "health"))); var maxMana = Max("max_mana", Number(h, "mana"));
        if (maxHp == null && h.TryGetProperty("max_health", out _) || maxMana == null && h.TryGetProperty("max_mana", out _)) return null;
        return new(true, Text(m, "game_state") == "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", paused,
            Stat(h, 2_000, "move_speed", "movement_speed", "movespeed"), Stat(h, 10_000, "attack_damage", "attackdamage"),
            Stat(h, 2_000, "attack_speed", "attackspeed"), h.TryGetProperty("level", out var l) && Int(l, out int level) &&
                level is >= 1 and <= 30 ? level : null,
            MaxHealth: maxHp, MaxMana: maxMana,
            LastHits: p.TryGetProperty("last_hits", out var lh) && Int(lh, out int hits) && hits is >= 0 and <= 100_000 ? hits : null,
            Silenced: silenced, Muted: muted);
    }
    private static string? PlayerTeam(JsonElement p) => Text(p, "team_name") switch {
        "radiant" or "DOTA_TEAM_GOODGUYS" => "radiant",
        "dire" or "DOTA_TEAM_BADGUYS" => "dire", _ => null,
    };
    // Without a receiver session even a complete POST_GAME cannot establish ownership for new stats.
    public static OwnState Parse(JsonElement root) => Parse(root,
        PacketUnixMs(root, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), MatchWitness.MaxAgeMs) != null &&
        ReadMatch(root) is { Phase: "pregame" or "playing" });
    internal static OwnState Parse(JsonElement root, bool participantStats)
    {
        var p = root.GetProperty("player"); var h = root.GetProperty("hero"); var m = root.GetProperty("map");
        // Extract flat player fields only; nested observer groups are not parsed.
        // Shape alone cannot certify ownership or fog semantics for every spectator mode.
        string hero = Text(h, "name");
        string team = PlayerTeam(p) ?? throw new ArgumentException("Missing own team");
        string match = m.TryGetProperty("matchid", out var id) ? id.ToString() : "";
        double health = Number(h, "health"), mana = Number(h, "mana");
        var state = new OwnState(match, hero, team, Number(m, "clock_time"), h.GetProperty("alive").GetBoolean(),
            health, mana, Position(h, "xpos"), Position(h, "ypos"), Entries(root, "items"), Entries(root, "abilities"),
            Farm(root, p, h, m, team), Stats: participantStats ? ParticipantStats.Read(p, h, health, mana) : null);
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
            s.Items == null || s.Abilities == null || s.Items.Length > 40 || s.Abilities.Length > 40 ||
            s.Farm != null && (!MinuteFarmContext.ValidStats(s.Farm) || s.Health > s.Farm.MaxHealth || s.Mana > s.Farm.MaxMana))
            throw new ArgumentException("Invalid own-player state");
        foreach (var entry in s.Items.Concat(s.Abilities))
            if (entry == null || !Name.IsMatch(entry.Name) || entry.Level is < 0 or > 40 ||
                (entry.Cooldown is double cd && !Wire.Finite(cd, 0, 10000))) throw new ArgumentException("Invalid GSI entry");
        if (s.Items.Any(x => x.Slot != null && !ItemSlot.IsMatch(x.Slot)) ||
            s.Abilities.Any(x => x.Slot != null && !AbilitySlot.IsMatch(x.Slot))) throw new ArgumentException("Invalid GSI slot");
        if (s.Stats is { } stats && !stats.InRange(s.Health, s.Mana)) throw new ArgumentException("Invalid participant stats");
    }
}
