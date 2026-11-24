using DotaCoach.TwoPc;
using System.Text.Json;

/// <summary>
/// Offline check of what the coach says and draws about enemies leaving the friend team's vision, on replay facts with
/// hero facing (scripts/replays/extract-wards.py since 2026-11-24). Each team in turn is the friend team, its vision as
/// WardEval models it, and its Plus feed one packet per replay sample with both sides' kill counts.
///   dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release -- --fog-eval .tmp/ward-corpus-yaw/*.wards.json
/// Reports: vision-loss subtitles per game minute for 3 s and 5 s delays; how often the hero then reached the place the
/// subtitle says it faced ("朝…", within 15 s of vanishing) against the place behind it; how often the minimap circle
/// held the hero, per second gone; and how many fog marks stood for a hero that had died, with and without kill counts.
/// </summary>
internal static class FogEval
{
    private sealed record Hero(string Name, int Team, double X, double Y, double? Yaw, bool Alive);
    public static void Run(string[] files)
    {
        var map = new CoachMap("data/dota2-coach-map.json");
        var names = new HeroNames();
        double minutes = 0;
        var alerts = new Dictionary<int, int> { [3000] = 0, [5000] = 0, [8000] = 0, [10000] = 0 }; var deadAlerts = alerts.Keys.ToDictionary(g => g, _ => 0);
        int headed = 0, reached = 0, reachedBehind = 0;
        var inside = new int[(int)FogTracker.CircleSeconds + 1]; var drawn = new int[inside.Length];
        int marks = 0, deadMarks = 0, marksNoKills = 0, deadMarksNoKills = 0;
        foreach (string file in files) {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            string match = root.GetProperty("matchId").GetInt64().ToString();
            var samples = root.GetProperty("samples").EnumerateArray().Select(s => (T: s.GetProperty("t").GetDouble(),
                Heroes: s.GetProperty("heroes").EnumerateArray().Where(h => h.GetProperty("hero").ValueKind == JsonValueKind.String).Select(h =>
                    new Hero(h.GetProperty("hero").GetString()!, h.GetProperty("team").GetInt32(), h.GetProperty("x").GetDouble(), h.GetProperty("y").GetDouble(),
                        h.TryGetProperty("yaw", out var y) && y.ValueKind == JsonValueKind.Number ? y.GetDouble() : null,
                        h.GetProperty("alive").GetBoolean())).ToArray())).Where(s => s.T >= 0).ToArray();
            var sources = root.GetProperty("sources").EnumerateArray().Select(s => (T: s.GetProperty("t").GetDouble(),
                Units: s.GetProperty("units").EnumerateArray().Select(u => (Team: u[0].GetInt32(), X: u[1].GetDouble(), Y: u[2].GetDouble(),
                    Tower: u[3].GetString() == "tower")).ToArray())).ToArray();
            var observers = root.GetProperty("wards").EnumerateArray().Where(w => w.GetProperty("kind").GetString() == "observer").Select(w =>
                (Team: w.GetProperty("team").GetInt32(), Placed: w.GetProperty("placed").GetDouble(), X: w.GetProperty("position")[0].GetDouble(),
                 Y: w.GetProperty("position")[1].GetDouble(),
                 Gone: w.GetProperty("gone").ValueKind == JsonValueKind.Number ? w.GetProperty("gone").GetDouble() : double.MaxValue)).ToArray();
            if (samples.Length == 0) continue;
            minutes += 2 * samples[^1].T / 60;
            // Where every hero really was, for the truth.
            var truth = samples.SelectMany(s => s.Heroes.Select(h => (s.T, h))).GroupBy(p => p.h.Name)
                .ToDictionary(g => g.Key, g => g.Select(p => (p.T, H: p.h)).ToArray());
            foreach (int own in new[] { 2, 3 }) {
                var losses = alerts.Keys.ToDictionary(g => g, g => new VisionLossTracker { GoneMs = g, Map = map });
                var fog = new FogTracker(); var blind = new FogTracker();
                int source = 0, ownKills = 0, enemyKills = 0;
                var alive = new Dictionary<string, bool>();
                foreach (var (t, heroes) in samples) {
                    while (source + 1 < sources.Length && sources[source + 1].T <= t) source++;
                    bool night = (int)(t / 300) % 2 == 1;
                    foreach (var h in heroes) {
                        if (alive.TryGetValue(h.Name, out bool was) && was && !h.Alive) { if (h.Team == own) enemyKills++; else ownKills++; }
                        alive[h.Name] = h.Alive;
                    }
                    var eyes = heroes.Where(h => h.Team == own && h.Alive).Select(h => (h.X, h.Y, R: night ? 650.0 : 1350))
                        .Concat(sources[source].Units.Where(u => u.Team == own).Select(u => (u.X, u.Y, R: u.Tower ? (night ? 800.0 : 1400) : 650)))
                        .Concat(observers.Where(w => w.Team == own && w.Placed <= t && t < Math.Min(w.Gone, w.Placed + 360)).Select(w => (w.X, w.Y, R: 1400.0))).ToArray();
                    var seen = heroes.Where(h => h.Team != own && h.Alive && eyes.Any(e => double.Hypot(e.X - h.X, e.Y - h.Y) <= e.R)).ToArray();
                    var units = heroes.Where(h => h.Team == own).Select(h => new MinimapUnit(h.Name, own, h.X, h.Y, h.Yaw, h.Alive ? 1350 : 0, null))
                        .Concat(seen.Select(h => new MinimapUnit(h.Name, h.Team, h.X, h.Y, h.Yaw, 1800, null))).ToArray();
                    var packet = new SpectatorSnapshot((long)(t * 1000), "team-view", Math.Floor(t), "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false,
                        match, own, 0, units, [own == 2 ? "radiant" : "dire"], []) { Kills = own == 2 ? (ownKills, enemyKills) : (enemyKills, ownKills) };
                    foreach (var (gone, tracker) in losses)
                        foreach (var a in tracker.Update(packet, names)) {
                            alerts[gone]++;
                            if (heroes.FirstOrDefault(h => h.Name == a.Hero) is not { Alive: true }) deadAlerts[gone]++;
                            if (gone != VisionLossTracker.DefaultGoneMs) continue;
                            // "…，朝X": did the hero stand anywhere named X within 15 s of vanishing, vs the place behind it?
                            int comma = a.Text.IndexOf("，朝", StringComparison.Ordinal);
                            if (comma < 0) continue;
                            string toward = a.Text[(comma + 2)..];
                            double vanished = t - gone / 1000.0;
                            var path = truth[a.Hero].Where(p => p.T >= vanished && p.T <= vanished + 15 && p.H.Alive).ToArray();
                            if (path.Length == 0) continue;
                            headed++;
                            if (path.Any(p => map.Describe(p.H.X, p.H.Y, own) == toward)) reached++;
                            var last = path[0].H;
                            if (last.Yaw is double yaw && Behind(map, last, yaw, own) is { } behind && path.Any(p => map.Describe(p.H.X, p.H.Y, own) == behind))
                                reachedBehind++;
                        }
                    fog.Sight(packet);
                    blind.Sight(packet with { Kills = null });
                    foreach (var f in fog.Heroes()) {
                        marks++;
                        if (heroes.FirstOrDefault(h => h.Name == f.Hero) is not { Alive: true } now) { deadMarks++; continue; }
                        if (FogTracker.Circle(f) is var (cx, cy, r)) {
                            int s = (int)f.Seconds;
                            drawn[s]++;
                            if (double.Hypot(now.X - cx, now.Y - cy) <= r) inside[s]++;
                        }
                    }
                    foreach (var f in blind.Heroes()) {
                        marksNoKills++;
                        if (heroes.FirstOrDefault(h => h.Name == f.Hero) is not { Alive: true }) deadMarksNoKills++;
                    }
                }
            }
        }
        Console.WriteLine($"{files.Length} games × 2 sides, {minutes:F0} side-minutes");
        foreach (var (gone, n) in alerts) Console.WriteLine($"vision-loss subtitles after {gone / 1000} s: {n} ({n / minutes:F2} a minute per side), hero dead then {100.0 * deadAlerts[gone] / Math.Max(1, n):F0}%");
        Console.WriteLine($"subtitles naming a facing ({VisionLossTracker.DefaultGoneMs / 1000} s): {headed}; hero reached the place it faced within 15 s: " +
            $"{100.0 * reached / Math.Max(1, headed):F0}% (the place behind it: {100.0 * reachedBehind / Math.Max(1, headed):F0}%)");
        for (int s = 1; s < inside.Length; s++)
            Console.WriteLine($"  circle after {s}-{s + 1} s: {drawn[s]} marks, hero inside {100.0 * inside[s] / Math.Max(1, drawn[s]):F0}%");
        Console.WriteLine($"fog marks on a dead hero: {100.0 * deadMarks / Math.Max(1, marks):F1}% of {marks} with kill counts, " +
            $"{100.0 * deadMarksNoKills / Math.Max(1, marksNoKills):F1}% of {marksNoKills} without");
    }
    // The same naming as the subtitle, looking the other way: a control for how often any named neighbour is reached.
    private static string? Behind(CoachMap map, Hero h, double yaw, int own)
    {
        string here = map.Describe(h.X, h.Y, own);
        double a = (yaw + 180) * Math.PI / 180;
        foreach (double d in new[] { 1200.0, 2000, 2800 }) {
            double x = h.X + d * Math.Cos(a), y = h.Y + d * Math.Sin(a);
            if (Math.Abs(x) > 8200 || Math.Abs(y) > 8200) return null;
            if (map.Describe(x, y, own) is var next && next != here) return next;
        }
        return null;
    }
}
