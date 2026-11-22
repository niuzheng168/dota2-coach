using DotaCoach.TwoPc;
using System.Text.Json;

/// <summary>
/// Offline check of the ward guesses against real 7.42 games (scripts/replays/extract-wards.py output). Each team in turn
/// plays the friend team: an enemy counts as seen when a friendly hero, creep, tower or observer ward is close enough
/// (vision radii cut for trees and cliffs, which are not modelled), and its wards are read while seen as hero_scan.py
/// does since 2026-11-22: an enemy carrying wards every 0.5 s, others every 3.4 s, timed to a fraction of a second by
/// the arrival of each second's first packet, one entry per change of the counts. --whole-seconds reads every enemy every
/// 3.4 s with the whole-second clock, as the scanner did before. The friend team's eyes and own observer wards reach the
/// tracker as its minimap would show them. The guesses are compared with the wards that enemy really placed in the window.
///   dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release -- --ward-eval [--spots HELD-OUT.json] [--list] [--whole-seconds] .tmp/ward-eval/*.wards.json
/// Pass spots built without the evaluated matches (build-ward-spots.ts --exclude), or their own wards are in the prior.
/// Live recordings (scripts/vision/ward_live.py) bring each unit's own vision radius and the scanner's real HUD reads,
/// used instead of the true counts unless --truth-counts. --list prints every guess with the wards it is matched to.
/// --features FILE writes each guess's spots, their features per part of the window (WardModel.Parts) and which spots
/// lie within 400 of a ward really placed then, for scripts/replays/fit-ward-model.py.
/// </summary>
internal static class WardEval
{
    private const double ReadEvery = 3.4, CarrierReadEvery = 0.5;
    public static void Run(string[] args)
    {
        var map = new CoachMap("data/dota2-coach-map.json");
        string spotsFile = "data/dota2-ward-spots-742.json";
        bool list = false, truthCounts = false, whole = false;
        string? featuresFile = null;
        var files = new List<string>();
        for (int i = 0; i < args.Length; i++)
            switch (args[i]) {
                case "--spots": spotsFile = args[++i]; break;
                case "--list": list = true; break; // every guess with the real ward, one line each
                case "--truth-counts": truthCounts = true; break; // live recordings: the true counts instead of the scanner's
                case "--whole-seconds": whole = true; break; // reads as hero_scan.py took them before 2026-11-22
                case "--features": featuresFile = args[++i]; break;
                default: files.Add(args[i]); break;
            }
        var spots = new WardSpots(spotsFile);
        var top = new List<double>(); var any = new List<double>(); var common = new List<double>(); var uncommon = new List<double>();
        var windows = new List<(double Seconds, double Top)>();
        int guesses = 0, matched = 0, falseAlarms = 0, rough = 0, placed = 0, detected = 0;
        using var features = featuresFile == null ? null : new StreamWriter(featuresFile);
        foreach (string file in files) {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
            var root = doc.RootElement;
            string match = root.GetProperty("matchId").GetInt64().ToString();
            var samples = root.GetProperty("samples").EnumerateArray().Select(s => (T: s.GetProperty("t").GetDouble(),
                Heroes: s.GetProperty("heroes").EnumerateArray().Where(h => h.GetProperty("hero").ValueKind == JsonValueKind.String).Select(h =>
                    (Hero: h.GetProperty("hero").GetString()!, Team: h.GetProperty("team").GetInt32(), X: h.GetProperty("x").GetDouble(),
                     Y: h.GetProperty("y").GetDouble(), Alive: h.GetProperty("alive").GetBoolean(),
                     Observer: h.GetProperty("observer").GetInt32(), Sentry: h.GetProperty("sentry").GetInt32())).ToArray())).ToArray();
            // Replays name the unit (tower or creep; radii below); live recordings give the vision radius Dota reported.
            var sources = root.GetProperty("sources").EnumerateArray().Select(s => (T: s.GetProperty("t").GetDouble(),
                Units: s.GetProperty("units").EnumerateArray().Select(u => (Team: u[0].GetInt32(), X: u[1].GetDouble(), Y: u[2].GetDouble(),
                    Tower: u[3].ValueKind == JsonValueKind.String && u[3].GetString() == "tower",
                    Radius: u[3].ValueKind == JsonValueKind.Number ? u[3].GetDouble() : 0)).ToArray())).ToArray();
            // Live recordings also carry the scanner's own HUD reads (game clock, whole seconds); replays use the true counts.
            var hudReads = root.TryGetProperty("reads", out var readList) && !truthCounts ? readList.EnumerateArray().Select(r =>
                (Hero: r.GetProperty("hero").GetString()!, Read: new WardReading(r.GetProperty("clock").GetDouble(),
                    r.GetProperty("observer").GetInt32(), r.GetProperty("sentry").GetInt32(), r.GetProperty("unknown").GetInt32()))).ToArray() : null;
            var real = root.GetProperty("wards").EnumerateArray().Where(w => w.GetProperty("hero").ValueKind == JsonValueKind.String).Select(w =>
                (Kind: w.GetProperty("kind").GetString()!, Team: w.GetProperty("team").GetInt32(), Hero: w.GetProperty("hero").GetString()!,
                 Placed: w.GetProperty("placed").GetDouble(), X: w.GetProperty("position")[0].GetDouble(), Y: w.GetProperty("position")[1].GetDouble(),
                 Gone: w.GetProperty("gone").ValueKind == JsonValueKind.Number ? w.GetProperty("gone").GetDouble() : double.MaxValue)).ToArray();
            foreach (int own in new[] { 2, 3 }) {
                var tracker = new WardTracker(map, spots);
                var explained = new List<(WardGuess Guess, WardSpot[] Pool, WardModel.Part[] Parts)>();
                if (features != null) tracker.Explain = (g, pool, parts) => explained.Add((g, pool, parts));
                var reads = new Dictionary<string, List<WardReading>>();
                var lastRead = new Dictionary<string, double>();
                var found = new List<WardGuess>();
                var scanned = hudReads?.GroupBy(r => r.Hero).ToDictionary(g => g.Key, g => new Queue<WardReading>(g.Select(r => r.Read).OrderBy(r => r.Clock)));
                int source = 0;
                double second = double.NaN, secondStart = 0;
                foreach (var (t, heroes) in samples) {
                    if (t < 0) continue;
                    while (source + 1 < sources.Length && sources[source + 1].T <= t) source++;
                    bool night = (int)(t / 300) % 2 == 1;
                    var observers = real.Where(w => w.Team == own && w.Kind == "observer" && w.Placed <= t && t < Math.Min(w.Gone, w.Placed + 360)).ToArray();
                    var others = sources[source].Units.Where(u => u.Team == own).Select(u => (u.X, u.Y,
                        R: u.Radius > 0 ? u.Radius : u.Tower ? (night ? 800.0 : 1400) : 650, Reported: u.Radius)).ToArray();
                    var eyes = heroes.Where(h => h.Team == own && h.Alive).Select(h => (h.X, h.Y, R: night ? 650.0 : 1350))
                        .Concat(others.Select(u => (u.X, u.Y, u.R))).Concat(observers.Select(w => (w.X, w.Y, R: 1400.0))).ToArray();
                    var seen = heroes.Where(h => h.Team != own && h.Alive && eyes.Any(e => double.Hypot(e.X - h.X, e.Y - h.Y) <= e.R)).ToArray();
                    // The tracker cuts reported radii to WardTracker.VisionShare: hand it the radius that gives these eyes.
                    var units = heroes.Where(h => h.Team == own).Select(h => new MinimapUnit(h.Hero, own, h.X, h.Y, 0,
                            h.Alive ? (night ? 650 : 1350) / WardTracker.VisionShare : 0, null))
                        .Concat(seen.Select(h => new MinimapUnit(h.Hero, h.Team, h.X, h.Y, 0, 1800, null)))
                        .Concat(others.Select(u => new MinimapUnit("npc_dota_creep", own, u.X, u.Y, 0,
                            u.Reported > 0 ? u.Reported : u.R / WardTracker.VisionShare, null)))
                        .Concat(observers.Select(w => new MinimapUnit("npc_dota_observer_wards", own, w.X, w.Y, 0, 1400 / WardTracker.VisionShare, null)))
                        .ToArray();
                    tracker.Sight(new((long)(t * 1000), "team-view", Math.Floor(t), "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, match, own, 0,
                        units, [own == 2 ? "radiant" : "dire"], []));
                    // The scanner times reads as the tracker does its packets: the second's first packet starts it.
                    if (Math.Floor(t) != second) { second = Math.Floor(t); secondStart = t; }
                    double readClock = whole ? second : second + Math.Min(t - secondStart, 0.99);
                    bool fresh = false;
                    void Keep(string hero, WardReading read)
                    {
                        var list = reads.TryGetValue(hero, out var l) ? l : reads[hero] = [];
                        if (!whole && list.Count > 0 && list[^1].First != null && (list[^1].Observer, list[^1].Sentry, list[^1].Unknown) ==
                                (read.Observer, read.Sentry, read.Unknown))
                            list[^1] = list[^1] with { Clock = read.Clock }; // the same counts again: the entry's latest read
                        else list.Add(read);
                        fresh = true;
                        if (list.Count > 4) list.RemoveAt(0);
                    }
                    if (scanned != null) {
                        // A scanner read counts only if the friend team saw that hero then: in team view it is read only in sight.
                        foreach (var (hero, queue) in scanned) {
                            WardReading? last = null;
                            while (queue.Count > 0 && queue.Peek().Clock <= Math.Floor(t)) last = queue.Dequeue();
                            if (last != null && last.Clock >= Math.Floor(t) - 1 && seen.Any(h => h.Hero == hero)) Keep(hero, last);
                        }
                    }
                    else foreach (var h in seen) {
                        bool carrying = !whole && reads.TryGetValue(h.Hero, out var mine) && mine[^1].Total > 0;
                        if (lastRead.TryGetValue(h.Hero, out double at) && t - at < (carrying ? CarrierReadEvery : ReadEvery)) continue;
                        lastRead[h.Hero] = t;
                        Keep(h.Hero, new(readClock, h.Observer, h.Sentry, 0, whole ? null : readClock));
                    }
                    if (fresh) found.AddRange(tracker.Readings(match, own, reads.ToDictionary(r => r.Key, r => r.Value.ToArray())));
                }
                var enemyWards = real.Where(w => w.Team != own && w.Placed >= 0).ToArray();
                foreach (var (g, pool, parts) in explained) {
                    var truth = enemyWards.Where(w => w.Hero == g.Hero && (g.Kind == "ward" || w.Kind == g.Kind) &&
                        w.Placed > g.After - 1 && w.Placed <= g.Before + 1).ToArray();
                    // A guess counts as anchored when its first mark is no common spot (WardTracker puts the hero's own place first).
                    bool anchored = g.Candidates is [var c0, ..] && !c0.Rough && !pool.Any(s => s.X == c0.X && s.Y == c0.Y);
                    features!.WriteLine(JsonSerializer.Serialize(new { match, own, g.Kind, g.Unseen, g.After, g.Before, anchored,
                        spots = pool.Select(s => new[] { s.X, s.Y }), truth = truth.Select(w => new[] { w.X, w.Y }),
                        top = truth.Length == 0 || g.Candidates.Length == 0 ? (double?)null : truth.Min(w => double.Hypot(w.X - g.Candidates[0].X, w.Y - g.Candidates[0].Y)),
                        parts = parts.Select(p => new { p.Gap, p.Seconds, p.Reach, f = p.Features.Select(f => f?.Select(v => Math.Round(v, 4))) }) }));
                }
                placed += enemyWards.Length;
                detected += enemyWards.Count(w => found.Any(g => g.Hero == w.Hero && w.Placed > g.After - 1 && w.Placed <= g.Before + 1));
                foreach (var g in found) {
                    guesses++;
                    if (g.Candidates.FirstOrDefault()?.Rough == true) rough++;
                    var truth = enemyWards.Where(w => w.Hero == g.Hero && (g.Kind == "ward" || w.Kind == g.Kind) &&
                        w.Placed > g.After - 1 && w.Placed <= g.Before + 1).ToArray();
                    if (list)
                        Console.WriteLine("GUESS " + JsonSerializer.Serialize(new { file = Path.GetFileName(file), own, g.Hero, g.Kind, g.Count, g.After, g.Before,
                            g.Unseen, candidates = g.Candidates.Select(k => new { k.X, k.Y, k.Share, k.Place, k.Rough }),
                            truth = truth.Select(w => new { w.Kind, w.Placed, w.X, w.Y }) },
                            new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                    if (truth.Length == 0 || g.Candidates.Length == 0) { falseAlarms += truth.Length == 0 ? 1 : 0; continue; }
                    matched++;
                    top.Add(truth.Min(w => double.Hypot(w.X - g.Candidates[0].X, w.Y - g.Candidates[0].Y)));
                    // Whether the real ward is at one of the common spots at all: the prior cannot point anywhere else.
                    string enemy = own == 2 ? "dire" : "radiant";
                    bool usual = truth.Any(w => spots.For(enemy, w.Kind).Any(s => double.Hypot(s.X - w.X, s.Y - w.Y) <= 400));
                    (usual ? common : uncommon).Add(top[^1]);
                    windows.Add((g.Unseen, top[^1]));
                    any.Add(truth.Min(w => g.Candidates.Min(k => double.Hypot(w.X - k.X, w.Y - k.Y))));
                }
            }
        }
        string Share(List<double> d, double limit) => d.Count == 0 ? "-" : $"{100.0 * d.Count(v => v <= limit) / d.Count:F0}%";
        double Median(List<double> d) => d.Count == 0 ? double.NaN : d.Order().ElementAt(d.Count / 2);
        Console.WriteLine($"{files.Count} games × 2 sides: enemy wards placed {placed}, inside a guess window {detected} ({100.0 * detected / Math.Max(1, placed):F0}%)");
        Console.WriteLine($"guesses {guesses}: matched to a real ward {matched}, no ward placed by that hero then {falseAlarms}, rough {rough}");
        Console.WriteLine($"likeliest mark to the real ward: median {Median(top):F0}, ≤300 {Share(top, 300)}, ≤600 {Share(top, 600)}, ≤1000 {Share(top, 1000)}, ≤1500 {Share(top, 1500)}");
        Console.WriteLine($"real ward within 400 of a common spot: {common.Count}/{common.Count + uncommon.Count}; likeliest mark ≤600 there {Share(common, 600)}, elsewhere {Share(uncommon, 600)}");
        foreach (var (low, high) in new[] { (-1.0, 0.0), (0.0, 5.0), (5.0, 15.0), (15.0, 60.0), (60.0, 1e9) }) {
            var part = windows.Where(w => w.Seconds > low && w.Seconds <= high).Select(w => w.Top).ToList();
            string span = low < 0 ? "never" : $"{low:F0}-{(high > 1e8 ? "∞" : high.ToString("F0"))} s";
            Console.WriteLine($"  out of sight {span}: {part.Count} guesses, likeliest mark ≤600 {Share(part, 600)}, ≤1000 {Share(part, 1000)}");
        }
        var drawn = windows.Where(w => w.Seconds < WardTracker.UnsureSeconds).Select(w => w.Top).ToList();
        Console.WriteLine($"drawn on the minimap (out of sight under {WardTracker.UnsureSeconds:F0} s): {drawn.Count} guesses, likeliest mark ≤300 {Share(drawn, 300)}, ≤600 {Share(drawn, 600)}");
        Console.WriteLine($"nearest of up to {WardTracker.MaxCandidates} marks: median {Median(any):F0}, ≤600 {Share(any, 600)}, ≤1000 {Share(any, 1000)}, ≤1500 {Share(any, 1500)}");
    }
}
