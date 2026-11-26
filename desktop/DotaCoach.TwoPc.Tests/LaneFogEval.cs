using System.Text.Json;
using DotaCoach.TwoPc;

// Offline only: never issue a live model call, load an archive in the coach, match by nearest hidden
// creep or give the estimator entity identities. Forecast all samples BEFORE opening the oracle.
internal static class LaneFogEval
{
    private sealed record Truth(string Id, int Team, string Lane, int X, int Y, string Radiant, string Dire);
    private sealed record Sample(int Team, LaneMap742 Map);
    private sealed class Metrics
    {
        public int Attempts, Dead, Unidentified, NotHidden, NoFuture, Already;
        public List<double> Distance = [], Other = [];
    }
    private static int N(JsonElement e, int k) => e[k].GetInt32();
    private static string S(JsonElement e, int k) => e[k].GetString()!;
    public static void Run(string root)
    {
        var fog = new SortedDictionary<string, Metrics>(); var meetings = new SortedDictionary<string, Metrics>();
        bool savedExample = false;
        static Metrics Bucket(SortedDictionary<string, Metrics> dict, string key) {
            if (!dict.TryGetValue(key, out var b)) dict.Add(key, b = new()); return b;
        }
        foreach (var file in Directory.GetFiles(root, "*.views.jsonl").Order()) {
            string match = Path.GetFileName(file).Split('.')[0];
            var trackers = new[] { new LaneWave742.Tracker(), new LaneWave742.Tracker() };
            var samples = new List<Sample>();
            foreach (var line in File.ReadLines(file)) {
                using var doc = JsonDocument.Parse(line); var r = doc.RootElement; int t = N(r, 0); long ms = t * 1000L;
                for (int i = 0; i < 2; i++) {
                    var own = new MatchWitness(match, i == 0 ? "radiant" : "dire", "npc_dota_hero_luna", t, "playing", false) { InstalledBuild = 6989 };
                    var units = r[i+1].EnumerateArray().Select(u => new MinimapUnit(S(u,0), N(u,1), N(u,2), N(u,3), null, 750, null)).ToArray();
                    var packet = new SpectatorSnapshot(ms, "team-view", t, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, match, i+2, 0, units, [], []);
                    if (LaneWave742.Observe(own, packet, ms, trackers[i]) == null) throw new InvalidDataException("Unadmitted test input");
                    if (t < 300 || t % 5 != 0) continue;
                    var output = trackers[i].Project(t, true);
                    if (output.Marks.Length > 0 && !output.Valid()) throw new InvalidDataException("Invalid map at " + t);
                    samples.Add(new(i+2, output));
                    if (!savedExample && t >= 1200 && output.Marks.Any(m=>m.Kind=="fog") && output.Marks.Any(m=>m.Kind=="forecast")) {
                        File.WriteAllText(Path.Combine(root,"overlay-example.json"),JsonSerializer.Serialize(new {team=own.Team,map=output},Wire.Json));
                        savedExample=true;
                    }
                }
            }
            var frames = new Dictionary<int, Truth[]>();
            var hits = new Dictionary<(int, string), (int X, int Y)>();
            foreach (var line in File.ReadLines(Path.Combine(root, match + ".oracle.jsonl"))) {
                using var d = JsonDocument.Parse(line); var r = d.RootElement; int t = N(r,0);
                var rows = frames[t] = r[1].EnumerateArray().Select(u => new Truth(S(u,0), N(u,1), S(u,2), N(u,3), N(u,4), S(u,5), S(u,6))).ToArray();
                foreach (var g in rows.Where(x => x.Lane != "?").GroupBy(x => x.Lane)) {
                    double best = 501;
                    foreach (var a in g.Where(x => x.Team==2)) foreach (var b in g.Where(x => x.Team==3)) {
                        double gap = double.Hypot(a.X-b.X, a.Y-b.Y); if (gap > 500 || gap >= best) continue;
                        best = gap; hits[(t,g.Key)] = ((a.X+b.X)/2,(a.Y+b.Y)/2);
                    }
                }
            }
            foreach (var (side, map) in samples) {
                int t = map.Clock; string period = t < 900 ? "05-15" : t < 1800 ? "15-30" : "30+";
                foreach (var m in map.Marks) {
                    if (m.Kind=="fog" && m.Team != side) {
                        var b = Bucket(fog,period); b.Attempts++;
                        string Vision(Truth a) => side==2 ? a.Radiant : a.Dire;
                        var anchors = frames[m.SeenAt!.Value].Where(a => a.Team==m.Team && a.X==m.LastX && a.Y==m.LastY && Vision(a)=="visible").ToArray();
                        if (anchors.Length!=1) { b.Unidentified++; continue; }
                        var actual = frames[t].SingleOrDefault(a => a.Id==anchors[0].Id);
                        if (actual == null) { b.Dead++; continue; }
                        if (Vision(actual)!="hidden") { b.NotHidden++; continue; }
                        b.Distance.Add(double.Hypot(m.X-actual.X,m.Y-actual.Y)); b.Other.Add(double.Hypot(anchors[0].X-actual.X,anchors[0].Y-actual.Y));
                    } else if (t%30==0 && m.Kind is "forecast" or "baseline" or "held" && frames.ContainsKey(t+40)) {
                        var b = Bucket(meetings, m.Kind); b.Attempts++;
                        if (hits.ContainsKey((t,m.Lane))) { b.Already++; continue; }
                        int first = Enumerable.Range(t+1,40).FirstOrDefault(s => hits.ContainsKey((s,m.Lane)));
                        if (first==0) { b.NoFuture++; continue; }
                        var p = hits[(first,m.Lane)]; b.Distance.Add(double.Hypot(m.X-p.X,m.Y-p.Y)); b.Other.Add(Math.Abs(m.Meeting!.Value-first));
                    }
                }
            }
            Console.WriteLine($"{match}: 0..{frames.Keys.Max()} seconds, {samples.Count} mid/late team maps");
        }
        static string P(List<double> v, double q) => v.Count == 0 ? "n/a" : v.Order().ElementAt((int)Math.Round((v.Count-1)*q)).ToString("0.0");
        foreach (var (k,b) in fog) Console.WriteLine($"enemy fog {k}min: attempts={b.Attempts}, alive+hidden={b.Distance.Count}, gone={b.Dead}, unmatched={b.Unidentified}, " +
            $"not-hidden={b.NotHidden}; world error median/P90 {P(b.Distance,.5)}/{P(b.Distance,.9)}, stationary comparison {P(b.Other,.5)}/{P(b.Other,.9)}");
        foreach (var (k,b) in meetings) Console.WriteLine($"future meeting {k}: issued={b.Attempts}, already-close={b.Already}, no contact within 40s={b.NoFuture}, scored={b.Distance.Count}; " +
            $"world error median/P90 {P(b.Distance,.5)}/{P(b.Distance,.9)}, time error median/P90 {P(b.Other,.5)}/{P(b.Other,.9)}s");
    }
}
