using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotaCoach.TwoPc;

/// <summary>Current, explicit readings from the flat participant only. Null is unknown; zero is a
/// reading, never a default for a missing stat, a death, or a transition out of the match.</summary>
[JsonNumberHandling(JsonNumberHandling.Strict)]
public sealed record ParticipantStats(int? Level = null, double? MaxHealth = null, double? MaxMana = null,
    int? Gold = null, int? NetWorth = null, int? LastHits = null, int? Denies = null,
    int? Kills = null, int? Deaths = null, int? Assists = null, int? Gpm = null, int? Xpm = null)
{
    // Sanity limits for a bounded projection, not estimates or clamps.
    private const int MaxVital = 100_000, MaxMoney = 10_000_000, MaxCount = 100_000, MaxRate = 10_000;
    private static readonly ParticipantStats Empty = new();
    internal static ParticipantStats? Read(JsonElement player, JsonElement hero, double health, double mana)
    {
        static int? Integer(JsonElement e, string key, int max, int min = 0) =>
            e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) &&
            n >= min && n <= max ? n : null;
        double? Vital(string key, double min) => hero.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out double n) && Wire.Finite(n, min, MaxVital) ? n : null;
        var stats = new ParticipantStats(Level: Integer(hero, "level", 30, 1),
            MaxHealth: Vital("max_health", Math.Max(1, health)), MaxMana: Vital("max_mana", mana),
            Gold: Integer(player, "gold", MaxMoney), NetWorth: Integer(player, "net_worth", MaxMoney),
            LastHits: Integer(player, "last_hits", MaxCount), Denies: Integer(player, "denies", MaxCount),
            Kills: Integer(player, "kills", MaxCount), Deaths: Integer(player, "deaths", MaxCount),
            Assists: Integer(player, "assists", MaxCount), Gpm: Integer(player, "gpm", MaxRate), Xpm: Integer(player, "xpm", MaxRate));
        return stats == Empty ? null : stats;
    }
    internal bool InRange(double health, double mana) =>
        Level is null or (>= 1 and <= 30) && (MaxHealth is not double hp || Wire.Finite(hp, Math.Max(1, health), MaxVital)) &&
        (MaxMana is not double mp || Wire.Finite(mp, mana, MaxVital)) && Gold is null or (>= 0 and <= MaxMoney) &&
        NetWorth is null or (>= 0 and <= MaxMoney) && LastHits is null or (>= 0 and <= MaxCount) &&
        Denies is null or (>= 0 and <= MaxCount) && Kills is null or (>= 0 and <= MaxCount) &&
        Deaths is null or (>= 0 and <= MaxCount) && Assists is null or (>= 0 and <= MaxCount) &&
        Gpm is null or (>= 0 and <= MaxRate) && Xpm is null or (>= 0 and <= MaxRate);
}

/// <summary>Allowlisted lifecycle evidence, not combat telemetry. Only a verified postgame for the
/// recording's own match and transport session is end evidence. Absence is never end evidence.</summary>
public sealed record MatchWitness(string MatchId, string Team, string Hero, [property: JsonRequired] double Clock,
    string Phase, [property: JsonRequired] bool Paused)
{
    public const int MaxAgeMs = 3_000;
    /// <summary>Age of the provider's source second at sampling, not time since the last HTTP POST.</summary>
    [JsonRequired] public int AgeMs { get; init; }
    /// <summary>Game PC's verified local installation build at this sample (not a server or GSI claim).
    /// Null is unknown. A different verified build is reported as-is, not as support for the 7.42 layout.</summary>
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull), JsonNumberHandling(JsonNumberHandling.Strict)]
    public int? InstalledBuild { get; init; }

    internal static bool MatchIdOk(string? s) => s is { Length: >= 1 and <= 20 } && s[0] != '0' &&
        s.All(char.IsAsciiDigit) && ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out _);
    internal static bool HeroOk(string? s)
    {
        const string prefix = "npc_dota_hero_";
        return s != null && s.StartsWith(prefix, StringComparison.Ordinal) && s.Length > prefix.Length &&
            s.Length <= prefix.Length + 64 && s.AsSpan(prefix.Length).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_") < 0;
    }
    internal static bool PhaseClockOk(string? phase, double clock) => phase is "pregame" or "playing" or "postgame" &&
        Wire.Finite(clock, phase == "pregame" ? -3600 : 0, 43_200);

    public static bool Valid(MatchWitness? witness) => Valid(witness, 0);
    /// <summary>Wire shape and combined freshness only. Bind separately to the authenticated session/match.
    /// Include transport/queue time using the receiver's monotonic capture-lease bound; do not re-age from zero.</summary>
    public static bool Valid(MatchWitness? witness, long elapsedMs) => witness is { Team: "radiant" or "dire" } &&
        MatchIdOk(witness.MatchId) && HeroOk(witness.Hero) && PhaseClockOk(witness.Phase, witness.Clock) &&
        witness.InstalledBuild is null or (>= 1 and <= 10_000_000) &&
        witness.AgeMs is >= 0 and <= MaxAgeMs && elapsedMs >= 0 && elapsedMs <= MaxAgeMs - witness.AgeMs;
}

// Never serialized or retained as raw JSON. A missing hero is only a candidate, never a public witness.
internal sealed record MatchSample(string MatchId, string Team, string? Hero, double Clock, string Phase, bool Paused,
    ulong User, int Seat, int TeamSeat, int HeroId)
{
    internal bool AgreesWith(MatchSample prior) => Team == prior.Team && (Hero == null || Hero == prior.Hero) &&
        (Phase == "postgame" ? User == prior.User : User == 0 || prior.User == 0 || User == prior.User) &&
        (Seat < 0 || prior.Seat < 0 || Seat == prior.Seat) && (TeamSeat < 0 || prior.TeamSeat < 0 || TeamSeat == prior.TeamSeat) &&
        // If the only hero evidence is a positive numeric ID, it must actually identify the prior hero.
        (HeroId == 0 || HeroId == prior.HeroId || prior.HeroId == 0 && Hero != null);
    internal MatchSample Bind(MatchSample prior) => this with { Hero = Hero ?? prior.Hero, User = User == 0 ? prior.User : User,
        Seat = Seat < 0 ? prior.Seat : Seat, TeamSeat = TeamSeat < 0 ? prior.TeamSeat : TeamSeat,
        HeroId = HeroId == 0 ? prior.HeroId : HeroId };
}

public static partial class GsiState
{
    // Real flat participants commonly have hero.permanent_buffs: {}. This is an opaque,
    // flat counter bag, not another hero/participant source. Never descend into its values.
    private static bool BenignPermanentBuffs(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in value.EnumerateObject()) {
            string k = entry.Name;
            if (keys.Count >= 32 || k.Length is < 1 or > 64 ||
                k.AsSpan().IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_") >= 0 ||
                Multiplexed.IsMatch(k) || ViewFlag.IsMatch(k) || k is "observer_slot" or "spectator_slot" ||
                !keys.Add(k) || entry.Value.ValueKind != JsonValueKind.Number ||
                !entry.Value.TryGetUInt32(out _)) return false;
        }
        return true;
    }

    // Some packets also carry the 32-bit accountid. It must agree with any Steam64 identity, not replace
    // a contrary one. Keep the resulting continuity key on this machine, including between sparse packets.
    private static bool ReadAccount(JsonElement section, ref ulong user)
    {
        if (!section.TryGetProperty("accountid", out var v)) return true;
        uint id;
        if (v.ValueKind == JsonValueKind.Number) { if (!v.TryGetUInt32(out id)) return false; }
        else if (v.ValueKind != JsonValueKind.String || !uint.TryParse(v.GetString(), NumberStyles.None,
            CultureInfo.InvariantCulture, out id)) return false;
        ulong steam = SteamBase + id;
        if (id == 0 || user != 0 && user != steam) return false;
        user = steam;
        return true;
    }

    // Ingress authenticates first; callers have checked provider/appid/timestamp. Deltas, combat stats,
    // minimap, winner, a "DISCONNECT" state, etc. can never supply missing lifecycle fields.
    internal static MatchSample? ReadMatch(JsonElement root)
    {
        if (!root.TryGetProperty("map", out var m) || m.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("player", out var p) || p.ValueKind != JsonValueKind.Object ||
            Text(m, "name") is not ("dota" or "start") || !CompatibleMode(m) || PlayerTeam(p) is not { } team ||
            !ReadPause(m, out var pause) || pause is not bool paused ||
            !m.TryGetProperty("matchid", out var id) || id.ValueKind is not (JsonValueKind.Number or JsonValueKind.String) ||
            !m.TryGetProperty("clock_time", out var c) || c.ValueKind != JsonValueKind.Number || !c.TryGetDouble(out double clock))
            return null;
        string match = id.ToString();
        string? phase = Text(m, "game_state") switch {
            "DOTA_GAMERULES_STATE_PRE_GAME" => "pregame",
            "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" => "playing",
            "DOTA_GAMERULES_STATE_POST_GAME" => "postgame", _ => null,
        };
        if (phase == null || !MatchWitness.PhaseClockOk(phase, clock) || !MatchWitness.MatchIdOk(match) ||
            !OwnParticipant(root, p, team, out var actor, checkAccountId: true, correlatedEnd: phase == "postgame") ||
            !ParticipantShape(root, strict: true)) return null;

        string? hero = null;
        int heroId = 0;
        if (root.TryGetProperty("hero", out var h)) {
            if (h.ValueKind != JsonValueKind.Object || !SameTeam(h, "team", team) || !SameTeam(h, "team_name", team)) return null;
            if (h.TryGetProperty("name", out var name)) {
                if (name.ValueKind != JsonValueKind.String) return null;
                if (name.GetString() is { Length: > 0 } n) {
                    if (!MatchWitness.HeroOk(n)) return null;
                    hero = n;
                }
            }
            // ParticipantShape validated the one native object (even on a sparse terminal).
            // A bad optional stat is unknown on a named hero; no other group can stand in for a hero.
            if (h.EnumerateObject().Any(f => f.Value.ValueKind is JsonValueKind.Object or JsonValueKind.Array &&
                !f.NameEquals("permanent_buffs") && (hero == null || f.Name is not ("level" or "max_health" or "max_mana")))) return null;
            // id:0 alone is an empty hero, NOT proof of ownership (also common in the Plus view).
            if (h.TryGetProperty("id", out var hid) && (!Int(hid, out heroId) || heroId < 0 || heroId == 0 && hero != null))
                return null;
        }
        return hero == null && phase != "postgame" ? null :
            new(match, team, hero, clock, phase, paused, actor.User, actor.Seat, actor.TeamSeat, heroId);
    }
}

/// <summary>One game-side raw GSI session. All methods called under the receiver gate, never during
/// manifest I/O or JSON parsing. No timeout, menu, new match, or reset manufactures a postgame.</summary>
internal sealed class MatchWitnessStream
{
    private MatchSample? current;
    private int? currentBuild;
    private long newestUnix, sampledAt, withdrawnThrough;
    private string? matchId;
    private readonly Queue<string> retired = new(); // Prevent a late previous match or verified end from reopening.
    private readonly SortedSet<long> pending = new();
    private long issued;
    private bool stopped;

    public long BeginRequest()
    {
        if (stopped) return 0;
        pending.Add(++issued);
        return issued;
    }
    public void FinishRequest(long request) => pending.Remove(request);
    private bool Withdraw(long unix)
    {
        current = null; currentBuild = null;
        // Integer source seconds cannot distinguish a replay of the withdrawn packet from a resume.
        withdrawnThrough = Math.Max(withdrawnThrough, Math.Max(unix, newestUnix));
        return false;
    }
    public void Reset(long utc, bool stop = false)
    {
        pending.Clear(); retired.Clear(); matchId = null; stopped |= stop;
        Withdraw(utc / 1000 * 1000);
    }
    private void Retire(string id)
    {
        if (retired.Contains(id)) return;
        if (retired.Count == 32) retired.Dequeue();
        retired.Enqueue(id);
    }
    private static bool Fresh(long at, long now) => now >= at && now - at <= MatchWitness.MaxAgeMs;
    public int? Age(long now) => current != null && Fresh(sampledAt, now) ? (int)(now - sampledAt) : null;
    public MatchWitness? Latest(long now) => current is { Hero: { } hero } p && Age(now) is int age ?
        new(p.MatchId, p.Team, hero, p.Clock, p.Phase, p.Paused) { AgeMs = age, InstalledBuild = currentBuild } : null;

    // Preflight only to avoid unnecessary manifest I/O. Observe rechecks after I/O; no proof is
    // visible and no session is advanced by preflight alone.
    public bool NeedsInstall(long request, MatchSample? sample, long? stamp, long utc, long received, long now, bool farmEligible) =>
        pending.Contains(request) && (farmEligible || sample != null && stamp.HasValue &&
            CanAccept(sample, stamp.Value, pending.Min != request, utc, received, now, out _, out _));
    private bool CanAccept(MatchSample sample, long stamp, bool gap, long utc, long received, long now,
        out long at, out MatchSample? prior)
    {
        prior = null;
        at = received - Math.Max(0, utc - stamp);
        if (stamp == newestUnix) at = Math.Min(at, sampledAt); // Duplicate / slight future skew cannot renew a lease.
        if (stamp < newestUnix || stamp <= withdrawnThrough || !Fresh(at, now)) return false;
        // An older, as yet unauthenticated body may contain an intervening menu/observer packet.
        // A full participant may start afresh after a gap; a terminal may never borrow across it.
        prior = !gap && Fresh(sampledAt, now) && current?.MatchId == sample.MatchId ? current : null;
        bool terminal = sample.Phase == "postgame";
        if (terminal && prior?.Phase is not ("playing" or "postgame") ||
            retired.Contains(sample.MatchId) && !(terminal && prior?.Phase == "postgame")) return false;
        if (prior != null) {
            if (!sample.AgreesWith(prior) || sample.Clock < prior.Clock || sample.Phase == "pregame" && prior.Phase != "pregame")
                return false;
            // A duplicate pre-pause packet is indistinguishable from this particular "resume".
            if (!terminal && prior.Paused && !sample.Paused && stamp == newestUnix && sample.Clock == prior.Clock) return false;
            // A forward seek cannot be the end of the game we observed just seconds ago.
            if (terminal && sample.Clock - prior.Clock > (stamp - newestUnix) / 1000d + 3) return false;
        }
        return true;
    }
    // Null means this handler was superseded: it must not write *either* projection.
    public bool? Observe(long request, MatchSample? sample, long? sourceUnix, long utc, long received, long now,
        int? installedBuild, out bool gap, out bool firstPostgame)
    {
        gap = false; firstPostgame = false;
        if (!pending.Contains(request)) return null; // Reset, or an already committed newer raw request.
        gap = pending.Min != request;
        while (pending.Count != 0 && pending.Min <= request) pending.Remove(pending.Min);
        if (sample == null || sourceUnix is not long stamp || !CanAccept(sample, stamp, gap, utc, received, now, out long at, out var prior))
            return Withdraw(sourceUnix ?? utc / 1000 * 1000);
        if (prior != null) sample = sample.Bind(prior);
        if (matchId != null && matchId != sample.MatchId) Retire(matchId);
        if (sample.Phase == "postgame") {
            firstPostgame = prior!.Phase == "playing"; // A heartbeat reaffirms current, but cannot originate another receipt.
            Retire(sample.MatchId);
        }
        matchId = sample.MatchId;
        current = sample; currentBuild = installedBuild; newestUnix = stamp; sampledAt = at;
        return true;
    }
}
