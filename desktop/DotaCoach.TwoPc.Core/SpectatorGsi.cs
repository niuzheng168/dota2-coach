using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

public sealed record MinimapUnit(string Name, int Team, double X, double Y, double? Yaw, double? Vision, string? Image);
/// <summary>
/// One GSI packet from this coach machine's own Dota while it spectates. Mode "team-view" is Dota Plus
/// live spectating (friend team's vision, player-mode layout); "full-spectator" is delayed spectating
/// with teamN/playerN groups. Only a matched team-view projection may enter the live archive;
/// the raw snapshot and full-spectator feeds are never model inputs.
/// </summary>
public sealed record SpectatorSnapshot(long ReceivedAt, string Mode, double? Clock, string? GameState, bool Paused,
    string? MatchId, int? OwnTeam, int GroupedPlayers, MinimapUnit[] Minimap, string[] BuildingTeams, string[] EventTypes)
{
    public bool Running => Clock != null && GameState is "DOTA_GAMERULES_STATE_PRE_GAME" or "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
    public IEnumerable<MinimapUnit> Heroes => Minimap.Where(u => u.Name.StartsWith("npc_dota_hero_", StringComparison.Ordinal));
    /// <summary>Hero kills of each side (map.radiant_score / dire_score), when the packet has them.</summary>
    public (int Radiant, int Dire)? Kills { get; init; }
}

public static class SpectatorGsi
{
    private static readonly Regex Name = new(@"\A[a-z0-9_]{1,100}\z", RegexOptions.CultureInvariant);
    private static readonly Regex Icon = new(@"\Aminimap_[a-z0-9_]{1,60}\z", RegexOptions.CultureInvariant);
    private static readonly Regex TeamGroup = new(@"\Ateam[0-9]+\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    private static readonly Regex PlayerGroup = new(@"\Aplayer[0-9]+\z", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    public const int MaxUnits = 2000;
    private static JsonElement? Obj(JsonElement e, string key) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Object ? v : null;
    private static string? Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    // TryGetDouble throws on non-numbers; one malformed unit must not discard the whole packet.
    private static double? Number(JsonElement e, string key, double limit) => e.TryGetProperty(key, out var v) &&
        v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out double n) && Wire.Finite(n, -limit, limit) ? n : null;
    private static int Grouped(JsonElement? section)
    {
        if (section is not { } s) return 0;
        int n = 0;
        foreach (var team in s.EnumerateObject()) {
            if (TeamGroup.IsMatch(team.Name))
                n += Math.Max(1, team.Value.ValueKind == JsonValueKind.Object ?
                    team.Value.EnumerateObject().Count(p => PlayerGroup.IsMatch(p.Name)) : 0);
            else if (PlayerGroup.IsMatch(team.Name) || team.Name.Equals("allplayers", StringComparison.OrdinalIgnoreCase)) n++;
        }
        return n;
    }
    // A flat player.team_name does not override full-view groups, even a sparse group in a change envelope.
    // Deltas can veto the live view; they never supply its match, side, clock or observed units.
    private static int Groups(JsonElement e) => Math.Max(e.TryGetProperty("allplayers", out _) ? 1 : 0,
        Math.Max(Math.Max(Grouped(Obj(e, "player")), Grouped(Obj(e, "hero"))),
                 Math.Max(Grouped(Obj(e, "items")), Grouped(Obj(e, "abilities")))));
    private static bool NonLive(JsonElement? section) => section is { } e && e.EnumerateObject().Any(p =>
        (p.Name is "replay" or "is_replay" or "in_replay" or "watching_replay" or "is_watching_replay" or
            "playing_demo" or "is_playing_demo" or "omniscient" or "worldsnapshots" or "full_spectator") &&
            p.Value.ValueKind != JsonValueKind.False);
    private static bool NonLiveView(JsonElement e) => NonLive(e) || NonLive(Obj(e, "map")) || NonLive(Obj(e, "player")) || NonLive(Obj(e, "provider"));
    public static SpectatorSnapshot Parse(JsonElement root, long receivedAt)
    {
        var map = Obj(root, "map"); var player = Obj(root, "player");
        int grouped = Groups(root);
        bool nonLive = NonLiveView(root);
        foreach (var key in new[] { "previously", "added", "removed" }) if (Obj(root, key) is { } delta) {
            grouped = Math.Max(grouped, Groups(delta));
            nonLive |= NonLiveView(delta);
        }
        string? teamName = player is { } p ? Text(p, "team_name") : null;
        int? playingSide = teamName switch { "radiant" or "DOTA_TEAM_GOODGUYS" => 2, "dire" or "DOTA_TEAM_BADGUYS" => 3, _ => null };
        string mode = map == null ? "menu" : nonLive ? "unknown" : grouped > 0 ? "full-spectator" : playingSide != null ? "player" :
            teamName == "spectator" ? "team-view" : "unknown";
        var units = new List<MinimapUnit>();
        if (Obj(root, "minimap") is { } minimap) {
            int entries = 0;
            foreach (var entry in minimap.EnumerateObject()) {
                if (++entries > MaxUnits) throw new ArgumentException("Too many minimap units");
                var e = entry.Value;
                if (e.ValueKind != JsonValueKind.Object || Number(e, "xpos", 20000) is not double x ||
                    Number(e, "ypos", 20000) is not double y || Number(e, "team", 16) is not double team || team != Math.Truncate(team)) continue;
                // A unit coming into sight sits at 0,0 (mid lane) for a packet or two before its position arrives: heroes,
                // creeps and wards in the 2026-11-17 and 11-21 Plus games. Such a hero put a ward guess in mid lane.
                if (x == 0 && y == 0) continue;
                string name = Text(e, "unitname") is { } n && Name.IsMatch(n) ? n : "?";
                string? image = Text(e, "image") is { } i && Icon.IsMatch(i) ? i : null;
                units.Add(new(name, (int)team, x, y, Number(e, "yaw", 720), Number(e, "visionrange", 10000), image));
            }
        }
        string[] buildings = Obj(root, "buildings") is { } b ? b.EnumerateObject().Select(t => t.Name)
            .Where(t => t is "radiant" or "dire").ToArray() : [];
        var sides = units.Where(u => u.Image == "minimap_herocircle_self" && u.Name.StartsWith("npc_dota_hero_") && u.Team is 2 or 3)
            .Select(u => u.Team).Concat(buildings.Select(t => t == "radiant" ? 2 : 3)).Distinct().ToArray();
        int? ownTeam = playingSide ?? (sides is [var side] ? side : null);
        string[] events = root.TryGetProperty("events", out var list) && list.ValueKind == JsonValueKind.Array
            ? list.EnumerateArray().Take(200).Where(e => e.ValueKind == JsonValueKind.Object)
                .Select(e => Text(e, "event_type") is { } t && Name.IsMatch(t) ? t : "?").Distinct().ToArray() : [];
        string? match = map is { } m && m.TryGetProperty("matchid", out var id) && TeamViewEvidence.RealMatch(id.ToString()) ? id.ToString() : null;
        bool paused = map is { } mp && (mp.TryGetProperty("paused", out var a) && a.ValueKind != JsonValueKind.False ||
            mp.TryGetProperty("is_paused", out var bPause) && bPause.ValueKind != JsonValueKind.False);
        return new(receivedAt, mode, map is { } mc ? Number(mc, "clock_time", 43200) : null,
            map is { } ms ? Text(ms, "game_state") : null, paused,
            match, ownTeam, grouped, units.ToArray(), buildings, events) {
            Kills = map is { } k && Number(k, "radiant_score", 1000) is double r && Number(k, "dire_score", 1000) is double d &&
                r >= 0 && d >= 0 && r == Math.Truncate(r) && d == Math.Truncate(d) ? ((int)r, (int)d) : null
        };
    }
}

public sealed record DelayStats(int Samples, double? MedianMs, double? P10Ms, double? P90Ms, double? ClockGapSeconds, string Note);

/// <summary>
/// How far the coach's Plus view lags the game machine. Both sides are stamped with this coach
/// machine's monotonic clock, so no wall-clock sync is needed. The game side is estimated as
/// frame (or status) arrival minus GSI age, so the result reads low by the transfer latency (video ~0.1–0.2 s).
/// </summary>
public sealed class PlusDelayTracker
{
    private const int Keep = 1800;
    private readonly object gate = new();
    private readonly SortedDictionary<long, long> game = new(), spectator = new();
    private double? gameClock, spectatorClock;
    private string? gameMatch, spectatorMatch, spectatorMode;
    private static void Transition(SortedDictionary<long, long> into, ref double? last, double clock, long at)
    {
        if (last is double previous && clock == previous + 1 && !into.ContainsKey((long)clock)) {
            into[(long)clock] = at;
            while (into.Count > Keep) into.Remove(into.Keys.First());
        }
        if (last != clock) last = clock;
    }
    /// <param name="observedAt">Coach clock: frame ReceivedAt minus the frame's GsiAgeMs.</param>
    public void Game(OwnState player, long observedAt)
    {
        lock (gate) {
            if (gameMatch != player.MatchId) { gameMatch = player.MatchId; game.Clear(); gameClock = null; }
            Transition(game, ref gameClock, player.Clock, observedAt);
        }
    }
    public void Spectator(SpectatorSnapshot s)
    {
        lock (gate) {
            if (!s.Running || s.Paused || s.Mode is not ("team-view" or "full-spectator")) { spectatorClock = null; return; }
            // Delayed and live spectating are different feeds; never mix their samples.
            if (spectatorMode != s.Mode || spectatorMatch != s.MatchId) { spectatorMode = s.Mode; spectatorMatch = s.MatchId; spectator.Clear(); spectatorClock = null; }
            Transition(spectator, ref spectatorClock, s.Clock!.Value, s.ReceivedAt);
        }
    }
    public DelayStats Stats(int recent = 300)
    {
        lock (gate) {
            if (gameMatch != null && spectatorMatch != null && gameMatch != spectatorMatch)
                return new(0, null, null, null, null, "观战的不是游戏机这一局（matchid 不一致）");
            var samples = spectator.Where(s => game.ContainsKey(s.Key)).TakeLast(recent)
                .Select(s => (double)(s.Value - game[s.Key])).Order().ToArray();
            double? gap = gameClock is double g && spectatorClock is double c ? g - c : null;
            if (samples.Length == 0)
                return new(0, null, null, null, gap, gameClock == null ? "等待游戏机画面带自身GSI" : spectatorClock == null ? "等待本机观战对局数据" : "等待两边游戏时间对齐");
            double At(double q) { double i = (samples.Length - 1) * q; int lo = (int)i, hi = Math.Min(lo + 1, samples.Length - 1); return samples[lo] + (samples[hi] - samples[lo]) * (i - lo); }
            return new(samples.Length, At(.5), At(.1), At(.9), gap, spectatorMode == "full-spectator" ? "完整观战（有延迟）" : "Plus 队伍视角");
        }
    }
}
