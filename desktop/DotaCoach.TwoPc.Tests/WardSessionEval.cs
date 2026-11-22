using DotaCoach.TwoPc;
using System.Text.Json;

/// <summary>
/// A live Plus session replayed through the coach's ward guesses: where the scanner's own team-view GSI saw each enemy
/// (its timeline) and the scanner's complete HUD ward reads, against the wards the match replay shows were placed
/// (scripts/replays/extract-wards.py). Lists every guess with the real wards in its window.
///   dotnet run --project desktop/DotaCoach.TwoPc.Tests -c Release -- --ward-session [--whole-seconds] [--as-scanned] .tmp/hero-scan/SESSION REPLAY.wards.json
/// Reads and sightings are timed as hero_scan.py does since 2026-11-22: each logged row within its game second by the
/// first row that showed that second (rows come every ~0.15 s), the reads merged into one entry per change of the counts.
/// --whole-seconds keeps the GSI's whole-second clock of each read, as the live file carried it before.
/// --as-scanned keeps the reads of enemies out of sight, as hero_scan.py took them before 2026-11-22 (implies --whole-seconds).
/// </summary>
internal static class WardSessionEval
{
    public static void Run(string session, string truthFile, bool asScanned, bool whole)
    {
        whole |= asScanned;
        var map = new CoachMap("data/dota2-coach-map.json");
        var spots = new WardSpots("data/dota2-ward-spots-742.json");
        var tracker = new WardTracker(map, spots);
        // Where each game second began on the scanner's clock: the first logged row that showed it.
        var started = new Dictionary<double, double>();
        var rows = File.ReadLines(Path.Combine(session, "scan.jsonl")).Select(line => JsonDocument.Parse(line).RootElement.Clone()).ToList();
        foreach (var r in rows)
            if (r.TryGetProperty("clock", out var c) && c.ValueKind == JsonValueKind.Number) started.TryAdd(c.GetDouble(), r.GetProperty("t").GetDouble());
        double Time(double t, double clock) => whole ? clock : clock + Math.Clamp(t - started.GetValueOrDefault(clock, t), 0, 0.99);
        // The scanner's reads that reach the live file: team view, every main, backpack and neutral slot known.
        var reads = new List<(double T, string Hero, WardReading Read)>();
        foreach (var r in rows) {
            if (!r.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Object ||
                r.GetProperty("hero").ValueKind != JsonValueKind.String || !r.TryGetProperty("mode", out var mode) || mode.GetString() != "team-view" ||
                r.GetProperty("clock").ValueKind != JsonValueKind.Number || !items.TryGetProperty("wards", out var w) || w.ValueKind != JsonValueKind.Object)
                continue;
            if (Enumerable.Range(0, 9).Select(i => $"slot{i}").Append("neutral0").Any(s => items.GetProperty(s).ValueKind == JsonValueKind.String &&
                    items.GetProperty(s).GetString() == "?")) continue;
            double t = r.GetProperty("t").GetDouble(), time = Time(t, r.GetProperty("clock").GetDouble());
            reads.Add((t, r.GetProperty("hero").GetString()!, new(time, w.GetProperty("observer").GetInt32(), w.GetProperty("sentry").GetInt32(),
                w.GetProperty("unknown").GetInt32(), whole ? null : time)));
        }
        var latest = new Dictionary<string, List<WardReading>>();
        var found = new List<WardGuess>();
        var seenAt = new Dictionary<string, List<double>>(); // enemy -> clocks it was in sight
        string? match = null;
        int own = 0, next = 0;
        foreach (string line in File.ReadLines(Path.Combine(session, "timeline.jsonl"))) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (r.GetProperty("type").GetString() != "snapshot" || r.GetProperty("mode").GetString() != "team-view" ||
                r.GetProperty("clock").ValueKind != JsonValueKind.Number || r.GetProperty("ownTeam").ValueKind != JsonValueKind.Number) continue;
            double t = r.GetProperty("t").GetDouble(), clock = r.GetProperty("clock").GetDouble();
            match = r.GetProperty("matchId").GetString(); own = r.GetProperty("ownTeam").GetInt32();
            var teams = new Dictionary<string, int>();
            var units = new List<MinimapUnit>();
            var visible = new HashSet<string>();
            foreach (var hero in r.GetProperty("heroes").EnumerateObject()) {
                if (hero.Value.GetProperty("team").ValueKind == JsonValueKind.Number) teams[hero.Name] = hero.Value.GetProperty("team").GetInt32();
                if (hero.Value.GetProperty("seen") is { ValueKind: JsonValueKind.Object } seen && seen.GetProperty("visible").GetBoolean() &&
                    teams.TryGetValue(hero.Name, out int team)) {
                    visible.Add(hero.Name);
                    double x = seen.GetProperty("x").GetDouble(), y = seen.GetProperty("y").GetDouble();
                    if (x == 0 && y == 0) continue; // placeholder position, dropped by SpectatorGsi.Parse in the coach
                    units.Add(new(hero.Name, team, x, y, null, 1800, null));
                    if (team != own) (seenAt.TryGetValue(hero.Name, out var l) ? l : seenAt[hero.Name] = []).Add(clock);
                }
            }
            // A clock with its fraction keeps the tracker on the reads' timing (its own would start each second at a snapshot).
            tracker.Sight(new((long)(t * 1000), "team-view", Time(t, clock), "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, match, own, 0, units.ToArray(), [], []));
            bool fresh = false;
            for (; next < reads.Count && reads[next].T <= t; next++) {
                var (_, hero, read) = reads[next];
                if (!teams.TryGetValue(hero, out int team) || team == own) continue; // the live file holds enemies only
                if (asScanned == false && !visible.Contains(hero)) continue; // hero_scan.py now drops reads of an enemy out of sight
                var list = latest.TryGetValue(hero, out var l) ? l : latest[hero] = [];
                if (list.Count > 0 && list[^1].Clock >= read.Clock) continue;
                if (!whole && list.Count > 0 && (list[^1].Observer, list[^1].Sentry, list[^1].Unknown) == (read.Observer, read.Sentry, read.Unknown))
                    list[^1] = list[^1] with { Clock = read.Clock }; // the same counts again: the entry's latest read
                else list.Add(read);
                fresh = true;
                if (list.Count > 4) list.RemoveAt(0);
            }
            if (fresh) found.AddRange(tracker.Readings(match, own, latest.ToDictionary(p => p.Key, p => p.Value.ToArray())));
        }

        using var truthDoc = JsonDocument.Parse(File.ReadAllBytes(truthFile));
        var real = truthDoc.RootElement.GetProperty("wards").EnumerateArray().Where(w => w.GetProperty("hero").ValueKind == JsonValueKind.String).Select(w =>
            (Kind: w.GetProperty("kind").GetString()!, Team: w.GetProperty("team").GetInt32(), Hero: w.GetProperty("hero").GetString()!,
             Placed: w.GetProperty("placed").GetDouble(), X: w.GetProperty("position")[0].GetDouble(), Y: w.GetProperty("position")[1].GetDouble()))
            .Where(w => w.Team != own && w.Placed >= 0).OrderBy(w => w.Placed).ToArray();
        var options = new JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
        var top = new List<double>(); var any = new List<double>(); var byUnseen = new List<(double Unseen, double Top)>();
        int wrong = 0;
        foreach (var g in found) {
            var truth = real.Where(w => w.Hero == g.Hero && (g.Kind == "ward" || w.Kind == g.Kind) && w.Placed > g.After - 1 && w.Placed <= g.Before + 1).ToArray();
            double? first = truth.Length == 0 || g.Candidates.Length == 0 ? null : truth.Min(w => double.Hypot(w.X - g.Candidates[0].X, w.Y - g.Candidates[0].Y));
            double? nearest = first == null ? null : truth.Min(w => g.Candidates.Min(k => double.Hypot(w.X - k.X, w.Y - k.Y)));
            if (first is double d) { top.Add(d); any.Add(nearest!.Value); byUnseen.Add((g.Unseen, d)); } else wrong++;
            Console.WriteLine("GUESS " + JsonSerializer.Serialize(new { g.Hero, g.Kind, g.Count, g.After, g.Before, g.Unseen, top = first?.ToString("F0"), nearest = nearest?.ToString("F0"),
                candidates = g.Candidates.Select(k => $"{k.Place} ({k.X:F0},{k.Y:F0}) {k.Share:P0}{(k.Rough ? " rough" : "")}"),
                truth = truth.Select(w => $"{w.Kind} {w.Placed:F1}s ({w.X:F0},{w.Y:F0}) {map.Describe(w.X, w.Y, own)}") }, options));
        }
        int covered = real.Count(w => found.Any(g => g.Hero == w.Hero && w.Placed > g.After - 1 && w.Placed <= g.Before + 1));
        string enemy = own == 2 ? "dire" : "radiant";
        int usual = real.Count(w => spots.For(enemy, w.Kind).Any(s => double.Hypot(s.X - w.X, s.Y - w.Y) <= 400));
        Console.WriteLine($"enemy wards placed {real.Length} ({usual} within 400 of a common spot), inside a guess window {covered}; " +
            $"guesses {found.Count}, no real ward in the window {wrong}");
        string Share(IEnumerable<double> d, double limit) => d.Any() ? $"{100.0 * d.Count(v => v <= limit) / d.Count():F0}%" : "-";
        Console.WriteLine($"likeliest mark ≤600 {Share(top, 600)}, ≤1000 {Share(top, 1000)}; nearest of the marks ≤600 {Share(any, 600)}, ≤1000 {Share(any, 1000)}");
        var drawn = byUnseen.Where(u => u.Unseen < WardTracker.UnsureSeconds).Select(u => u.Top).ToList();
        Console.WriteLine($"drawn on the minimap (out of sight under {WardTracker.UnsureSeconds:F0} s): {drawn.Count} guesses, likeliest ≤300 {Share(drawn, 300)}, ≤600 {Share(drawn, 600)}");
        foreach (var (low, high) in new[] { (-1.0, 0.0), (0.0, 15.0), (15.0, 60.0), (60.0, 1e9) }) {
            var part = byUnseen.Where(u => u.Unseen > low && u.Unseen <= high).Select(u => u.Top).ToList();
            Console.WriteLine($"  out of sight {(low < 0 ? "never" : $"{low:F0}-{(high > 1e8 ? "∞" : high.ToString("F0"))} s")}: {part.Count} guesses, likeliest ≤600 {Share(part, 600)}");
        }
        foreach (var hero in reads.Select(r => r.Hero).Distinct().Where(seenAt.ContainsKey)) {
            var clocks = reads.Where(r => r.Hero == hero).Select(r => r.Read.Clock).Distinct().Order().ToArray();
            var gaps = clocks.Zip(clocks.Skip(1), (a, b) => b - a).Order().ToArray();
            Console.WriteLine($"READS {hero}: {clocks.Length} complete reads, in sight {seenAt[hero].Distinct().Count()} s; gap between reads median " +
                $"{(gaps.Length > 0 ? gaps[gaps.Length / 2] : double.NaN):F1} s, P90 {(gaps.Length > 0 ? gaps[gaps.Length * 9 / 10] : double.NaN):F1} s; " +
                $"wards placed {real.Count(w => w.Hero == hero)}");
        }
    }
}
