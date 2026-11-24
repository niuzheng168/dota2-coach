using System.Text.Json;
using DotaCoach.TwoPc;

/// <summary>
/// Labels for the creep-attack pictures of the last-hit lab (scripts/lasthit/lab.ts --frames DIR): which unit owns each bar
/// the game client found in each picture, and where every unit stands on screen, at the picture's server time.
///   --vision-label TRACE.jsonl DIR   (reads TRACE.jsonl, TRACE.sync.jsonl, DIR/frames.jsonl; writes DIR/labels.jsonl)
/// Server time: the sync file pairs each server frame with when its console line arrived on the pictures' QPC clock; the
/// least difference over the next few seconds of arrivals is the offset (the console's delay is then near zero). It steps up
/// when the server stalls (one recording lost 0.75 s of game time once), so it is taken per picture, from arrivals after it. How much later the picture shows that state
/// (the client's interpolation and drawing) is fitted: the shift at which the most matched bars show exactly the fill the
/// engine's health draws. Bars are matched like the product matches them to GSI units (<see cref="CreepUnitMatcher"/>),
/// with the engine's positions in place of GSI's; screen positions are bar centres (<see cref="CameraModel.Screen"/>).
/// The attacks themselves (swing, launch, dmg, kill) stay in the trace.
/// </summary>
internal static class VisionLabel
{
    private sealed record Unit(int Id, string Name, int Team);
    private sealed record Frame(int I, string File, double At, int Width, int Height, int[][] Bars);

    public static void Run(string trace, string directory)
    {
        // Entity indices are reused by later waves: per index every entity that held it from its description on, and null from
        // its death or removal (older recordings did not describe the next entity at the same index).
        var units = new Dictionary<int, List<(double T, Unit? Unit)>>();
        void Held(int id, double t, Unit? unit) { if (!units.TryGetValue(id, out var list)) units[id] = list = []; list.Add((t, unit)); }
        Unit? UnitAt(int id, double t)
        {
            Unit? held = null;
            foreach (var (since, unit) in units.GetValueOrDefault(id) ?? []) {
                if (since > t || since == t && unit == null) break;
                held = unit;
            }
            return held;
        }
        var server = new List<(double T, List<(int Id, int Hp, int Max, double X, double Y)> Creeps)>();
        var heroes = new SortedList<double, (int Id, string Name, int Team, double X, double Y)>();
        var towers = new List<(int Id, string Name, int Team, double X, double Y)>();
        int ownTeam = 2;
        foreach (var line in File.ReadLines(trace)) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (r.GetProperty("k").GetString()) {
                case "begin": ownTeam = r.GetProperty("team").GetInt32(); break;
                case "unit": Held(r.GetProperty("id").GetInt32(), r.GetProperty("t").GetDouble(), new(r.GetProperty("id").GetInt32(), r.GetProperty("name").GetString()!, r.GetProperty("team").GetInt32())); break;
                case "kill": Held(r.GetProperty("v").GetInt32(), r.GetProperty("t").GetDouble(), null); break;
                case "gone": Held(r.GetProperty("id").GetInt32(), r.GetProperty("t").GetDouble(), null); break;
                case "tower": towers.Add((r.GetProperty("id").GetInt32(), r.GetProperty("name").GetString()!, r.GetProperty("team").GetInt32(), r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble())); break;
                case "f": {
                    double t = r.GetProperty("t").GetDouble();
                    if (server.Count == 0 || server[^1].T != t) server.Add((t, []));
                    foreach (var c in r.GetProperty("c").EnumerateArray())
                        server[^1].Creeps.Add((c[0].GetInt32(), c[1].GetInt32(), c[2].GetInt32(), c[3].GetDouble(), c[4].GetDouble()));
                    break;
                }
                case "h":
                    foreach (var h in r.GetProperty("h").EnumerateArray())
                        heroes[r.GetProperty("t").GetDouble()] = (h[0].GetInt32(), h[1].GetString()!, h[2].GetInt32(), h[3].GetDouble(), h[4].GetDouble());
                    break;
            }
        }
        string syncFile = Path.ChangeExtension(trace, null) + ".sync.jsonl";
        var arrivals = File.ReadLines(syncFile).Select(l => JsonDocument.Parse(l).RootElement)
            .Select(r => (Q: r.GetProperty("q").GetDouble(), D: r.GetProperty("q").GetDouble() - r.GetProperty("t").GetDouble())).OrderBy(a => a.Q).ToArray();
        // The least difference among arrivals in the 3 s after q (the last ones' for pictures after them).
        double Offset(double q)
        {
            int lo = 0, hi = arrivals.Length;
            while (lo < hi) { int mid = (lo + hi) / 2; if (arrivals[mid].Q < q) lo = mid + 1; else hi = mid; }
            double least = double.MaxValue;
            for (int k = Math.Min(lo, arrivals.Length - 1); k < arrivals.Length && (arrivals[k].Q <= q + 3 || least == double.MaxValue); k++) least = Math.Min(least, arrivals[k].D);
            return least;
        }
        double offset = arrivals.Min(a => a.D);
        var steps = arrivals.Where((a, k) => k > 0 && Offset(a.Q) - Offset(arrivals[k - 1].Q) > .02).Select(a => $"{a.Q - arrivals[0].Q:F0}s:+{Offset(a.Q) - offset:F3}");
        var frames = File.ReadLines(Path.Combine(directory, "frames.jsonl")).Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.TryGetProperty("file", out _))
            .Select(r => new Frame(r.GetProperty("i").GetInt32(), r.GetProperty("file").GetString()!, r.GetProperty("at").GetDouble(), r.GetProperty("width").GetInt32(),
                r.GetProperty("height").GetInt32(), r.GetProperty("bars").EnumerateArray().Select(b => b.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray()))
            .OrderBy(f => f.I).ToList();
        Console.WriteLine($"{server.Count} server frames, {frames.Count} pictures, offset {offset:F4} s; server stalls (offset steps) at " + string.Join(" ", steps));

        int State(double t)
        {
            int lo = 0, hi = server.Count - 1, found = 0;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (server[mid].T <= t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            return found;
        }
        // Matches every bar of a picture at server time t: per bar the unit id (-1), and the matcher's look-at point.
        (int[] Ids, (double X, double Y)? LookAt) Match(CreepUnitMatcher matcher, Frame f, double t)
        {
            var geometry = new CreepBarGeometry(f.Height);
            var bars = f.Bars.Select(b => new CreepHealthBar(b[0], b[1], b[2], b[3], geometry.Inner, geometry.Span, b[4] == 1, b[5] == 1)).ToList();
            var state = server[State(t)].Creeps;
            var minimap = state.Select(c => (c.Id, c.X, c.Y, Held: UnitAt(c.Id, t))).Where(c => c.Held != null)
                .Select(c => (c.Id, Unit: new MinimapUnit(c.Held!.Name, c.Held.Team, c.X, c.Y, null, null, null))).ToList();
            var owners = minimap.ToDictionary(m => m.Unit, m => m.Id, (IEqualityComparer<MinimapUnit>)ReferenceEqualityComparer.Instance);
            var matched = matcher.Match(bars, minimap.Select(m => m.Unit).ToList(), ownTeam, f.Width, f.Height);
            return (matched.Select(m => m != null && owners.TryGetValue(m, out int id) ? id : -1).ToArray(), matcher.LookAt);
        }
        // Agreement of matched bars with the engine's health shifted by lag.
        (int Agree, int Damaged) Agree(double lag, IEnumerable<Frame> sample)
        {
            var matcher = new CreepUnitMatcher();
            int agree = 0, damaged = 0;
            foreach (var f in sample) {
                double t = f.At - Offset(f.At) - lag;
                var (ids, _) = Match(matcher, f, t);
                var state = server[State(t)].Creeps.ToDictionary(c => c.Id);
                var geometry = new CreepBarGeometry(f.Height);
                for (int i = 0; i < ids.Length; i++)
                    if (ids[i] >= 0 && state.TryGetValue(ids[i], out var c) && c.Hp < c.Max) {
                        damaged++;
                        if ((int)Math.Ceiling(c.Hp / (double)c.Max * geometry.Span - 1e-9) == f.Bars[i][3]) agree++;
                    }
            }
            return (agree, damaged);
        }
        var sample = frames.Where((f, k) => k % 7 == 0).Take(1500).ToList();
        var curve = new List<(double Lag, int Agree, int Damaged)>();
        for (double lag = -.1; lag <= .2001; lag += 1 / 120d) { var (a, d) = Agree(lag, sample); curve.Add((lag, a, d)); }
        var best = curve.MaxBy(c => c.Agree);
        double bestLag = best.Lag;
        Console.WriteLine($"picture lag behind the server {bestLag:F3} s: {best.Agree} of {best.Damaged} damaged bars in {sample.Count} pictures show the engine's fill; " +
            "by lag " + string.Join(" ", curve.Where(c => Math.Abs(c.Lag - bestLag) < .07).Select(c => $"{c.Lag:F3}:{100d * c.Agree / Math.Max(1, c.Damaged):F0}%")));

        var all = new CreepUnitMatcher();
        int matchedBars = 0, totalBars = 0;
        using var output = new StreamWriter(Path.Combine(directory, "labels.jsonl"));
        foreach (var f in frames) {
            double t = f.At - Offset(f.At) - bestLag;
            var (ids, look) = Match(all, f, t);
            matchedBars += ids.Count(i => i >= 0); totalBars += ids.Length;
            // Every unit's bar centre on screen at the look-at point (null without one): creeps, heroes, towers.
            var onScreen = new List<object>();
            if (look is { } at) {
                void Add(int id, double x, double y)
                {
                    var p = CameraModel.Default.Screen(x - at.X, y - at.Y, f.Width, f.Height);
                    if (p.X > -200 && p.X < f.Width + 200 && p.Y > -200 && p.Y < f.Height + 200) onScreen.Add(new[] { id, Math.Round(p.X, 1), Math.Round(p.Y, 1) });
                }
                foreach (var c in server[State(t)].Creeps) Add(c.Id, c.X, c.Y);
                int h = heroes.Keys.Count == 0 ? -1 : Math.Max(0, LowerBound(heroes.Keys, t));
                if (h >= 0) { var hero = heroes.Values[h]; Add(hero.Id, hero.X, hero.Y); }
                foreach (var tower in towers) Add(tower.Id, tower.X, tower.Y);
            }
            output.WriteLine(JsonSerializer.Serialize(new { i = f.I, f.File, t = Math.Round(t, 4), look = look is { } l ? new[] { Math.Round(l.X), Math.Round(l.Y) } : null,
                bars = ids, units = onScreen }));
        }
        Console.WriteLine($"bars matched to a unit: {matchedBars} of {totalBars} ({100d * matchedBars / Math.Max(1, totalBars):F1}%); wrote {Path.Combine(directory, "labels.jsonl")}");
    }

    /// <summary>Index of the last key at or before <paramref name="t"/>.</summary>
    private static int LowerBound(IList<double> keys, double t)
    {
        int lo = 0, hi = keys.Count - 1, found = 0;
        while (lo <= hi) { int mid = (lo + hi) / 2; if (keys[mid] <= t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
        return found;
    }
}
