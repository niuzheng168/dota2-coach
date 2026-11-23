namespace DotaCoach.TwoPc;

internal sealed record WaveRoute(string Id, double[][] Points);
internal sealed record WaveTowerLandmark(string Id, int X, int Y);
public sealed record LaneContact(int Clock, int WorldX, int WorldY, string Landmark);
public sealed record EmptyLaneMeeting(string Lane, int SpawnClock, LaneContact Meeting);
public sealed record LaneWaveReading(string Lane, int RadiantSeen, int DireSeen, string Basis, int? AssumedSpawnClock, LaneContact? Meeting)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public int? FogAnchorClock { get; init; }
}
public sealed record LaneWaveSample(double ObservedClock, LaneWaveReading[] Lanes);

/// <summary>Short-horizon, unobstructed first approach on six build-6989 directional path_corner chains.
/// A meeting is a zone (midpoint when within 500 world units), not an attack, surviving wave or safe destination.
/// The optional session tracker extrapolates recent team-visible positions, never hidden truth or entity IDs.</summary>
internal static partial class LaneWave742
{
    // npc_units.txt, build 6989: normal/super/mega lane creeps have base MS 325; melee acquisition range 500.
    // The latter is our common *proximity definition*, not a ranged/siege attack or exact engine aggro test.
    private const int Speed = 325, Radius = 500, Period = 30, Horizon = 40;
    internal const int FreshSeconds = 3;
    private const double Corridor = 512, ForkMargin = 128, GroupGap = 900, Step = .25;
    private static readonly string[] Roads = ["top", "mid", "bot"];
    private static readonly Lazy<Lane[]> Map = new(Load);
    private static readonly Dictionary<string, int> Names = CreepNames();
    private readonly record struct Pt(double X, double Y)
    {
        public double Dist(Pt b) => double.Hypot(X - b.X, Y - b.Y);
        public Pt To(Pt b, double f) => new(X + (b.X - X) * f, Y + (b.Y - Y) * f);
    }
    private readonly record struct Snap(double Along, double Miss);
    private sealed class Path(WaveRoute geometry)
    {
        private readonly Pt[] points = geometry.Points.Select(p => new Pt(p[0], p[1])).ToArray();
        private readonly double[] arcs = Lengths(geometry.Points);
        public double Length => arcs[^1];
        private static double[] Lengths(double[][] p) {
            var s = new double[p.Length];
            for (int i = 1; i < p.Length; i++) s[i] = s[i - 1] + double.Hypot(p[i][0] - p[i - 1][0], p[i][1] - p[i - 1][1]);
            return s;
        }
        public Snap Project(Pt p) {
            var best = new Snap(0, double.PositiveInfinity);
            for (int i = 1; i < points.Length; i++) {
                var a = points[i - 1]; var b = points[i]; double len = arcs[i] - arcs[i - 1];
                double f = Math.Clamp(((p.X - a.X) * (b.X - a.X) + (p.Y - a.Y) * (b.Y - a.Y)) / (len * len), 0, 1);
                double d = p.Dist(a.To(b, f));
                if (d < best.Miss) best = new(arcs[i - 1] + f * len, d);
            }
            return best;
        }
        private int Segment(double s) {
            int i = Array.BinarySearch(arcs, s);
            return Math.Clamp(i < 0 ? ~i : i + 1, 1, arcs.Length - 1);
        }
        public Pt At(double s) {
            s = Math.Clamp(s, 0, Length); int i = Segment(s);
            return points[i - 1].To(points[i], (s - arcs[i - 1]) / (arcs[i] - arcs[i - 1]));
        }
        public Front From(Pt real, double along) {
            // First pay the actual distance from the GSI point to the lane ahead; never teleport a
            // pulled/displaced creep onto a polyline or cut an upcoming corner for free.
            double join = Math.Min(along + Speed, arcs[Segment(along)]);
            return new(this, real, along, join, real.Dist(At(join)));
        }
        public Front Scheduled(double s) => new(this, At(s), s, s, 0);
    }
    private readonly record struct Front(Path Path, Pt Start, double Along, double Join, double Connector)
    {
        public Pt At(double distance) => distance < Connector ? Start.To(Path.At(Join), distance / Connector) :
            Path.At(Join + distance - Connector);
        public double Left => Connector + Path.Length - Join;
    }
    private sealed record Lane(string Id, Path Good, Path Bad, WaveTowerLandmark[] Towers)
    {
        public LaneContact Baseline { get; set; } = null!;
        public string Landmark(Pt p) {
            var t = Towers.MinBy(t => p.Dist(new(t.X, t.Y)))!;
            int tier = t.Id.Contains("_tower1_") ? 1 : t.Id.Contains("_tower2_") ? 2 : 3;
            double dx = p.X - t.X, dy = p.Y - t.Y, dist = double.Hypot(dx, dy);
            string dir = dist < 150 ? "附近" : new[]{"东", "东北", "北", "西北", "西", "西南", "南", "东南"}
                [((int)Math.Round(Math.Atan2(dy, dx) * 4 / Math.PI) + 8) % 8] + $"约{Math.Round(dist / 100) * 100:0}";
            return (t.Id.Contains("_goodguys_") ? "天辉" : "夜魇") + "一二三"[tier - 1] + "塔原址" + dir;
        }
        public LaneContact Mark(double clock, Pt p) => new((int)Math.Ceiling(clock), (int)Math.Round(p.X), (int)Math.Round(p.Y), Landmark(p));
    }
    private static Dictionary<string, int> CreepNames() {
        var names = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (side, id) in new[] { ("goodguys", 2), ("badguys", 3) })
            foreach (var suffix in new[] { "", "_upgraded", "_upgraded_mega" }) {
                foreach (var kind in new[] { "melee", "flagbearer", "ranged" }) names[$"npc_dota_creep_{side}_{kind}{suffix}"] = id;
                names[$"npc_dota_{side}_siege{suffix}"] = id;
            }
        return names;
    }
    private static Lane[] Load() {
        var (routes, towers) = MinuteFarmSimulation.WaveGeometry;
        if (routes.Length != 6 || towers.Length != 18) throw new InvalidDataException("Invalid 7.42 lane geometry");
        return Roads.Select(id => {
            Path Route(string side) => new(routes.Single(r => r.Id == $"npc_dota_spawner_{side}_{id}"));
            var l = new Lane(id, Route("good"), Route("bad"), towers.Where(t => t.Id.EndsWith("_" + id, StringComparison.Ordinal)).ToArray());
            l.Baseline = Intercept(l, 0, l.Good.Scheduled(0), l.Bad.Scheduled(0)) ??
                throw new InvalidDataException("Reference waves never meet: " + id);
            return l;
        }).ToArray();
    }
    private static LaneContact? Intercept(Lane l, double clock, Front a, Front b, double aWait = 0, double bWait = 0,
        double speedA = Speed, double speedB = Speed) {
        // Reject waves already past one another, rather than forecasting a contact at a distant base.
        if (a.Path.Project(b.Start).Along + Radius < a.Along || b.Path.Project(a.Start).Along + Radius < b.Along) return null;
        double end = Math.Min(Horizon, Math.Min(speedA > 0 ? aWait + a.Left / speedA : Horizon,
            speedB > 0 ? bWait + b.Left / speedB : Horizon));
        for (double t = Math.Max(aWait, bWait); t <= end; t += Step) {
            var x = a.At((t - aWait) * speedA); var y = b.At((t - bWait) * speedB);
            if (x.Dist(y) <= Radius) return l.Mark(clock + t, x.To(y, .5));
        }
        return null;
    }

    // First still-upcoming contact between two ISOLATED same-spawn waves. Outer lanes can have two
    // batches in flight. Roll over on each lane's own contact time, not blindly at :00 or :30.
    internal static EmptyLaneMeeting[] Baseline(double clock) => Map.Value.Select(l => {
        int spawn = Math.Max(0, (int)Math.Ceiling((clock - l.Baseline.Clock) / Period)) * Period;
        return new EmptyLaneMeeting(l.Id, spawn, l.Baseline with { Clock = spawn + l.Baseline.Clock });
    }).ToArray();

    internal static LaneWaveSample? Observe(MatchWitness? own, SpectatorSnapshot s, long now, Tracker? tracker = null) {
        if (own?.Phase != "playing" || !TeamViewEvidence.Matches(own, s, now) || s.Clock is not double clock || clock < 0 || clock > own.Clock ||
            own.Clock - clock >= FreshSeconds || s.Minimap.Length > SpectatorGsi.MaxUnits) return null;
        var map = Map.Value;
        var units = new List<Front>[3, 2];
        var located = new List<Located>();
        for (int i = 0; i < 3; i++) for (int t = 0; t < 2; t++) units[i,t] = [];
        var distinct = new HashSet<(string, double, double)>();
        foreach (var u in s.Minimap) {
            // Zero-radius creep markers occur even in genuine team packets. Not proof of death *or*
            // fog: simply withhold that ambiguous entry instead of declaring it a current live front.
            if (!Names.TryGetValue(u.Name, out int side) || side != u.Team || u.Vision is not (> 0 and <= 10000) || (u.X == 0 && u.Y == 0) ||
                !Wire.Finite(u.X, -9472, 9472) || !Wire.Finite(u.Y, -9472, 9472) || !distinct.Add((u.Name, u.X, u.Y))) continue;
            if (distinct.Count > 256) return null; // Don't silently process a truncated front.
            var p = new Pt(u.X, u.Y); int road = -1; var near = new Snap(0, double.PositiveInfinity); double next = double.PositiveInfinity;
            for (int i = 0; i < map.Length; i++) {
                var path = side == 2 ? map[i].Good : map[i].Bad; var q = path.Project(p);
                if (q.Miss < near.Miss) { next = near.Miss; road = i; near = q; } else next = Math.Min(next, q.Miss);
            }
            if (near.Miss <= Corridor && next - near.Miss >= ForkMargin) {
                var f = (side == 2 ? map[road].Good : map[road].Bad).From(p, near.Along);
                units[road, side - 2].Add(f);
                located.Add(new(road, side, u.Name, u.X, u.Y, near.Along));
            }
        }
        var sample = new LaneWaveSample(clock, map.Select((l, i) => ReadLane(l, clock, units[i,0], units[i,1])).ToArray());
        return tracker == null ? sample : tracker.Update(own, s.ReceivedAt, located, sample,
            TeamViewEvidence.VisibleHeroes(s).Where(u => u.Team == s.OwnTeam).Select(u => (u.X, u.Y)));
    }
    private static LaneWaveReading ReadLane(Lane l, double clock, List<Front> good, List<Front> bad) {
        LaneWaveReading Read(string basis, LaneContact? contact = null, int? spawn = null) => new(l.Id, good.Count, bad.Count, basis, spawn, contact);
        // The minimum actual separation says something even when path_corners on this bend are offset.
        // Reject separated simultaneous contacts on the same road: this is not one uniquely located front.
        Pt? close = null, first = null; double distance = double.PositiveInfinity;
        foreach (var a in good) foreach (var b in bad) {
            double d = a.Start.Dist(b.Start); if (d > Radius) continue;
            var mid = a.Start.To(b.Start, .5);
            if (first is { } origin && origin.Dist(mid) > GroupGap) return Read("ambiguous");
            first ??= mid;
            if (d < distance) { close = mid; distance = d; }
        }
        if (close is { } pt) return Read("seen-close", l.Mark(Math.Floor(clock), pt));
        // Separate marching columns (a reinforced lane can have several); don't average a front with
        // units just spawning in base. Use the first-to-arrive representative of each column.
        static List<Front> Heads(List<Front> units) {
            var heads = new List<Front>(); double last = double.PositiveInfinity;
            foreach (var u in units.OrderByDescending(u => u.Along)) {
                if (last - u.Along > GroupGap) heads.Add(u);
                last = u.Along;
            }
            return heads;
        }
        var ga = Heads(good); var ba = Heads(bad);
        if (ga.Count + ba.Count == 0) return Read("unseen");
        LaneContact? best = null; int? originClock = null; bool split = false;
        void Try(LaneContact? c, int? origin = null) {
            if (c == null) return;
            if (best == null || c.Clock < best.Clock) { best = c; originClock = origin; split = false; }
            else if (c.Clock == best.Clock && double.Hypot(c.WorldX - best.WorldX, c.WorldY - best.WorldY) > GroupGap) split = true;
        }
        if (ga.Count > 0 && ba.Count > 0) {
            foreach (var a in ga) foreach (var b in ba) Try(Intercept(l, clock, a, b));
            return split ? Read("ambiguous") : best == null ? Read("unresolved") : Read("two-visible-fronts", best);
        }
        // No hidden unit is synthesized as a sighting. Pair a seen front only with a HYPOTHETICAL
        // marching reinforcement, including a future spawn near base. Ignore older hypothetical waves
        // already past this front; never attach a stable wave identity to o0/o1.
        bool radiant = ga.Count > 0; var other = radiant ? l.Bad : l.Good;
        int currentSpawn = (int)Math.Floor(clock / Period) * Period;
        foreach (var a in radiant ? ga : ba) {
            for (int spawn = Math.Max(0, currentSpawn - 2 * Period); spawn <= clock + Horizon; spawn += Period) {
                double along = Math.Max(0, clock - spawn) * Speed; if (along >= other.Length) continue;
                Try(Intercept(l, clock, a, other.Scheduled(along), bWait: Math.Max(0, spawn - clock)), spawn);
            }
        }
        return split ? Read("ambiguous") : best == null ? Read("unresolved") : Read("one-visible-plus-assumed-spawn", best, originClock);
    }
}
