using System.Buffers;
using System.IO.Compression;
using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>Arrival and finish are cumulative whole seconds from scenario.Clock, not game times.
/// At a camp, Finish is a leave-by budget, never a claim of a kill. Lane stops are arrival only.</summary>
public sealed record FarmStop(string Id, string Label, string Kind, double X, double Y, int ArrivalSeconds, int FinishSeconds)
{
    public int WalkUnits { get; init; } // This leg's conservatively rounded, traversable graph distance (not a chord).
    public string CostBasis { get; init; } = "";
    public int? PackWindowSeconds { get; init; } // Uncapped reference work window, only for the modeled packs/assumptions.
    public int ModeledRosters { get; init; } // Finite scenarios for this panel within the sampled type/flood family.
    public int CatalogRosters { get; init; } // Not identification of this camp's pack, a spawn probability or live coverage.
}

public sealed record FarmOption(string Id, FarmStop[] Stops);
public sealed record FarmTimingBasis(int Level, string LevelBasis, double MoveSpeed, string MoveBasis,
    double DamagePerHit, string DamageBasis, double AttackSpeed, string AttackSpeedBasis, double BaseAttackTime);
public sealed record FarmMinuteScenario(int Minute, int Clock, string Hero, string Team, FarmOption[] Options)
{
    public FarmTimingBasis? Timing { get; init; }
    public string MapBasis => "7.42/build 6989 initial-map own-interior ground graph; markers are destinations, not straight-line legs.";
    public string Conditions => "Clock offsets are planning/leave-by budgets, not live ETAs. Inspect first; skip empty, stacked, " +
        "dangerous or costly camps. Roster, wave, safety and income are unknown. A lane stop is a static return point only.";
}

public static class MinuteFarmSimulation
{
    private const string MapResource = "DotaCoach.MinuteFarm.Map742";
    private const string PackEnvelope = "modeled-roster-planning-envelope";
    private const int CampVisitLimit = 25; // A decision deadline, NOT a truncated estimate of time to clear.
    private const double WalkSlack = 1.15; // Explicit policy margin on distance/speed, not a measured engine multiplier.
    private static readonly Lazy<FarmMap> Map = new(() => new FarmMap());

    /// <summary>Up to six distinct, time-bounded choices of at most three stops. A pure jungle itinerary does
    /// not borrow time for, or require, a return to lane. A mixed itinerary pays for its actual return leg.</summary>
    public static FarmMinuteScenario? Build(OwnState own)
    {
        if (Prepare(own) is not { } s) return null;
        var singles = new List<FarmStop[]>(); var pairs = new List<FarmStop[]>(); var triples = new List<FarmStop[]>();
        var returns = new List<FarmStop[]>(); var doubleReturns = new List<FarmStop[]>();
        foreach (var first in s.Camps) {
            if (s.Stop(null, first, s.StartOffset) is not { } a) continue;
            singles.Add([a]);
            if (s.NearestLane(first, a.FinishSeconds) is { } back) returns.Add([a, back]);
            foreach (var second in s.Camps) {
                if (second == first || s.Stop(first, second, a.FinishSeconds) is not { } b) continue;
                pairs.Add([a, b]); // May be two explicitly time-boxed visits, never present them as two clears.
                if (s.NearestLane(second, b.FinishSeconds) is { } end) doubleReturns.Add([a, b, end]);
                if (a.CostBasis != PackEnvelope || b.CostBasis != PackEnvelope) continue;
                foreach (var third in s.Camps)
                    if (third != first && third != second &&
                        s.Stop(second, third, b.FinishSeconds) is { CostBasis: PackEnvelope } c) triples.Add([a, b, c]);
            }
        }
        static FarmStop[]? Best(IEnumerable<FarmStop[]> plans) => plans
            .OrderBy(p => p.Count(t => t.Kind == "camp" && t.CostBasis != PackEnvelope)).ThenBy(p => p[^1].FinishSeconds)
            .ThenBy(p => p.Sum(v => v.WalkUnits)).ThenBy(p => p[0].Id, StringComparer.Ordinal)
            .ThenBy(p => p.Length > 1 ? p[1].Id : "", StringComparer.Ordinal).ThenBy(p => p[^1].Id, StringComparer.Ordinal)
            .FirstOrDefault();
        var options = new List<FarmOption>(6);
        void Add(FarmStop[]? stops) {
            if (stops == null || options.Count >= 6) return;
            var id = string.Join("__", stops.Select(t => t.Id));
            if (!options.Any(o => o.Id == id)) options.Add(new(id, stops));
        }
        var quick = Best(returns);
        Add(quick);
        Add(Best(pairs));
        if (s.NearestLane(null, s.StartOffset) is { } lane) Add([lane]);
        Add(Best(triples));
        Add(Best(doubleReturns.Where(p => p[0].Id != quick?[0].Id)) ?? Best(doubleReturns));
        // Always retain a shorter pure-camp choice if any camp is feasible. Prefer an alternative first
        // destination to checking the same possibly-empty camp in every option.
        bool NewFirst(FarmStop[] p) => options.All(o => o.Stops[0].Id != p[0].Id);
        Add(Best(singles.Where(NewFirst)) ?? Best(singles));
        Add(Best(returns.Where(NewFirst)));
        Add(Best(singles.Where(NewFirst)));
        if (options.Count == 0) return null;
        return new(s.Clock / 60, s.Clock, own.Hero, own.Team, options.ToArray()) { Timing = s.Stats };
    }

    /// <summary>Re-walk just the selected IDs in order. Never trust the caller's coordinates, timing or labels,
    /// substitute another target, skip an invalid leg, or require the selection to win a fresh global ranking.
    /// The caller owns match/hero/minute binding. Offsets in the result start at floor(latest.Clock).</summary>
    public static FarmStop[] Reprice(OwnState latest, FarmOption selected)
    {
        if (selected?.Stops is not { Length: >= 1 and <= 3 } targets || Prepare(latest) is not { } s) return [];
        var result = new List<FarmStop>(targets.Length); var seen = new HashSet<string>(StringComparer.Ordinal);
        Site? from = null; int elapsed = s.StartOffset;
        foreach (var choice in targets) {
            if (choice?.Id == null || !seen.Add(choice.Id) || !Map.Value.ById.TryGetValue(choice.Id, out var to) ||
                to.Side != s.Side || s.Stop(from, to, elapsed) is not { } stop) break;
            result.Add(stop); from = to; elapsed = stop.FinishSeconds;
        }
        return result.ToArray();
    }

    public static ScreenMark Project(FarmStop stop, MinimapLayout layout)
    {
        var map = Map.Value;
        if (layout == null || !layout.IsValidFor(layout.SourceWidth, layout.SourceHeight) || stop?.Id == null ||
            !map.ById.TryGetValue(stop.Id, out var site) || site.X != stop.X || site.Y != stop.Y || site.Kind != stop.Kind)
            throw new ArgumentException("A registered 7.42 stop and valid minimap calibration are required.");
        var ov = map.Data.Overview;
        return new((layout.Left + (site.X - ov[0]) / ov[2] * layout.Size) / layout.SourceWidth,
            (layout.Top + (ov[1] - site.Y) / ov[2] * layout.Size) / layout.SourceHeight);
    }

    private static Pricing? Prepare(OwnState own)
    {
        if (own is not { Alive: true, X: double x, Y: double y } || own.Hero == null ||
            own.Team is not ("radiant" or "dire") || !Wire.Finite(own.Clock, 0, 43200) ||
            !double.IsFinite(own.Health) || own.Health <= 0 || !double.IsFinite(x) || !double.IsFinite(y)) return null;
        var map = Map.Value; int side = own.Team == "radiant" ? 1 : 2;
        if (!map.Data.Heroes.TryGetValue(own.Hero, out var hero) || map.Connector(x, y, side) is not { } start) return null;
        var f = own.Farm;
        if (f is { OwnParticipant: false } or { InProgress: false } or { Paused: true } ||
            (f?.InstalledBuild is int build && build != 6989)) return null;
        // A reported zero/invalid speed is NOT permission to replace it with a normal moving hero.
        if ((f?.MoveSpeed is double m && !Wire.Finite(m, 1, 2000)) ||
            (f?.AttackDamage is double d && !Wire.Finite(d, 0, 100000)) ||
            (f?.AttackSpeed is double a && !Wire.Finite(a, 20, 700)) || f?.Level is < 1 or > 30) return null;
        int level = f?.Level ?? 1;
        // See heroColumns in the reproducible client supplement. No unknown main/backpack slot, stance,
        // proc, aura, splash or activated item contributes a free bonus. Interpret only scalars in the
        // 100-based IAS model domain, not an ambiguous attacks/second or seconds/attack reading.
        double dmg = f?.AttackDamage is double panel ? Math.Max(0, panel - hero[3])
            : hero[2] + (hero[7] < 0 ? 0 : hero[7] + Math.Floor((level - 1) * hero[8]));
        var stats = new FarmTimingBasis(level, f?.Level == null ? "assumed-level-1" : "own-GSI",
            f?.MoveSpeed ?? hero[0], f?.MoveSpeed == null ? "6989-hero-base-no-boots-or-effects" : "own-GSI-assumed-held",
            dmg, f?.AttackDamage != null ? "own-GSI-scalar-minus-KV-roll-width" :
                hero[7] < 0 ? "6989-min-weapon-only-universal-bonus-omitted" : "6989-min-weapon-plus-base-primary-at-level",
            f?.AttackSpeed ?? (hero[4] + hero[5] + Math.Floor((level - 1) * hero[6])),
            f?.AttackSpeed == null ? "6989-base-IAS-plus-AGI-at-level" : "own-GSI-assumed-100-based-IAS-held", hero[1]);
        return new(map, side, own.Clock, x, y, start, stats);
    }

    private sealed record Work(int Seconds, string Basis, int? PackWindow = null, int Modeled = 0, int Catalog = 0);
    private static readonly Work LaneArrival = new(0, "static-lane-arrival-no-wave-work");

    private sealed class Pricing
    {
        private readonly FarmMap map;
        private readonly int budget;
        private readonly int[] initialCosts;
        private readonly Site[] lanes;
        private readonly Dictionary<string, Work> work = new(StringComparer.Ordinal);
        public Site[] Camps { get; }
        public int Side { get; }
        public int Clock { get; }
        public int StartOffset { get; }
        public FarmTimingBasis Stats { get; }

        internal Pricing(FarmMap map, int side, double now, double x, double y, int node, FarmTimingBasis stats)
        {
            this.map = map; Side = side; Stats = stats; Clock = (int)Math.Floor(now);
            // Charge any fractional observation time to the first leg (never silently grant up to an extra second).
            StartOffset = (int)Math.Ceiling(now) - Clock;
            budget = 60 - Clock % 60;
            initialCosts = map.Costs(x, y, node, side, (int)Math.Floor((budget - StartOffset) * stats.MoveSpeed / WalkSlack));
            Camps = map.Data.Sites.Where(t => t.Side == side && t.Group != null).ToArray();
            lanes = map.Data.Sites.Where(t => t.Side == side && t.Group == null).ToArray();
        }

        internal FarmStop? NearestLane(Site? from, int elapsed)
        {
            FarmStop? best = null;
            foreach (var lane in lanes)
                if (Stop(from, lane, elapsed) is { } s && (best == null || s.WalkUnits < best.WalkUnits ||
                    (s.WalkUnits == best.WalkUnits && string.CompareOrdinal(s.Id, best.Id) < 0))) best = s;
            return best;
        }

        internal FarmStop? Stop(Site? from, Site to, int elapsed)
        {
            int units = (from == null ? initialCosts : map.FromSite(from))[to.Index];
            if (units == int.MaxValue || (to.Group != null && Clock < 60)) return null;
            double travel = units == 0 ? 0 : Math.Ceiling(WalkSlack * units / Stats.MoveSpeed) + 1;
            if (travel > budget - elapsed) return null;
            int arrival = elapsed + (int)travel;
            var w = to.Group == null ? LaneArrival : Cost(to.Group);
            if (w.Seconds > budget - arrival) return null;
            string label = to.Group == null ? to.Label : to.Label + (w.Basis == PackEnvelope ? "·普攻预算" : $"·限时探营{w.Seconds}秒");
            return new(to.Id, label, to.Kind, to.X, to.Y, arrival, arrival + w.Seconds) {
                WalkUnits = units, CostBasis = w.Basis, PackWindowSeconds = w.PackWindow,
                ModeledRosters = w.Modeled, CatalogRosters = w.Catalog,
            };
        }

        private Work Cost(string group)
        {
            if (work.TryGetValue(group, out var cached)) return cached;
            var pool = map.Data.Groups[group]; var up = map.Data.Upgrade;
            // Later side of any upgrade boundary in this minute. Work is ONLY for ordinary, unstacked, full-HP
            // catalogued groups supported by camp-742.ts under uninterrupted single-target physical attacks.
            // Never extrapolate the 1..20 minute timing study as a late-game clear time.
            int? upper = null; int modeled = 0;
            if (Clock < 1200 && pool.Packs.Length > 0) {
                int tier = (int)((Clock + budget) / up[0]);
                double interval = Stats.BaseAttackTime * 100 / Stats.AttackSpeed, longest = 0;
                foreach (var pack in pool.Packs) {
                    double sec = 3; // Explicit planning allowance for approach-to-first-impact/disengage, not measured latency.
                    foreach (var u in pack) {
                        double hp = u[0] + tier * up[1], armor = u[1] + tier * up[2];
                        double multiplier = 1 - .06 * armor / (1 + .06 * Math.Abs(armor));
                        // Charge a full attack interval AND regeneration before every hit (including the opener);
                        // round hits per unit so overkill never carries. Add one second per target for acquisition.
                        double net = Stats.DamagePerHit * multiplier - u[2] * interval;
                        if (net <= 0) { sec = double.PositiveInfinity; break; }
                        sec += Math.Ceiling(hp / net) * interval + 1;
                    }
                    if (sec <= int.MaxValue) modeled++;
                    longest = Math.Max(longest, sec);
                }
                if (longest <= int.MaxValue) upper = (int)Math.Ceiling(longest);
            }
            // A time box is a *partial visit with a hard leave-by instruction*, not an invented upper bound on
            // clearing a stack/unknown group. Keep the uncapped pack scenario visible when one is supported.
            var result = upper is int uSeconds && uSeconds <= CampVisitLimit
                ? new Work(uSeconds, PackEnvelope, upper, modeled, pool.Catalog)
                : new Work(CampVisitLimit, Clock >= 1200 ? "visit-deadline-outside-timing-study" : "visit-deadline-not-a-clear",
                    upper, modeled, pool.Catalog);
            return work[group] = result;
        }
    }

    private sealed record Site(string Id, string Label, int Side, double X, double Y, string? Group)
    {
        internal string Kind => Group == null ? "lane" : "camp";
        internal int Index, Node, Tail;
    }
    private sealed record PackGroup(int Catalog, double[][][] Packs);
    private sealed record MapData(int Format, int Build, double[] Overview, int Width, int Height, int X0, int Y0,
        int CellSize, int Hull, byte[] Sides, byte[] Edges, Site[] Sites, Dictionary<string, PackGroup> Groups,
        Dictionary<string, double[]> Heroes, double[] Upgrade);

    private sealed class FarmMap
    {
        public MapData Data { get; }
        public Dictionary<string, Site> ById { get; }
        private readonly Lazy<int[]>[] matrix; // Only site-to-site results persist, not per-frame fields or live player state.
        private readonly int[] delta;
        internal FarmMap()
        {
            using var stream = typeof(MinuteFarmSimulation).Assembly.GetManifestResourceStream(MapResource)
                ?? throw new InvalidDataException("Missing embedded 7.42 minute-farm map.");
            using var gz = new GZipStream(stream, CompressionMode.Decompress);
            Data = JsonSerializer.Deserialize<MapData>(gz, new JsonSerializerOptions(JsonSerializerDefaults.Web))
                ?? throw new InvalidDataException("Empty 7.42 minute-farm map.");
            if (Data is not { Format: 1, Build: 6989, Width: 320, Height: 328, CellSize: 64, Hull: 24 } ||
                Data.Sides.Length != Data.Width * Data.Height || Data.Edges.Length != Data.Sides.Length ||
                Data.Overview is not [-9472, 9472, 18944]) throw new InvalidDataException("Wrong minute-farm map release.");
            int w = Data.Width;
            delta = [1, 1 + w, w, w - 1, -1, -1 - w, -w, 1 - w];
            ById = Data.Sites.ToDictionary(s => s.Id, StringComparer.Ordinal);
            foreach (var (s, i) in Data.Sites.Select((s, i) => (s, i))) {
                s.Index = i; s.Node = Connector(s.X, s.Y, s.Side) ?? throw new InvalidDataException("Blocked farm landmark.");
                s.Tail = Tail(s.X, s.Y, s.Node);
            }
            matrix = Data.Sites.Select(s => new Lazy<int[]>(() => Costs(s.X, s.Y, s.Node, s.Side, int.MaxValue))).ToArray();
        }

        private (double X, double Y) Center(int i) => (Data.X0 + (i % Data.Width + .5) * Data.CellSize,
            Data.Y0 + (i / Data.Width + .5) * Data.CellSize);
        private int Tail(double x, double y, int i)
        {
            var c = Center(i);
            return (int)Math.Ceiling(double.Hypot(c.X - x, c.Y - y));
        }

        internal int? Connector(double x, double y, int side)
        {
            // Check floating point coordinates *before* conversion. Never snap a blocked/off-map start.
            double fx = (x - Data.X0) / Data.CellSize, fy = (y - Data.Y0) / Data.CellSize;
            if (!(fx >= 0 && fx < Data.Width && fy >= 0 && fy < Data.Height)) return null;
            int cx = (int)fx, cy = (int)fy, i = cy * Data.Width + cx;
            if (Data.Sides[i] != side || Data.Edges[i] == 0) return null;
            var c = Center(i);
            // Within one tile, moving towards its centre can only increase clearance from a neighbouring
            // blocked/other-side tile that could touch the initial hull (halfHull 24 < halfCell 32).
            for (int dy = -1; dy <= 1; dy++) for (int dx = -1; dx <= 1; dx++) {
                if (Math.Abs(x - c.X - dx * Data.CellSize) > Data.CellSize / 2 + Data.Hull ||
                    Math.Abs(y - c.Y - dy * Data.CellSize) > Data.CellSize / 2 + Data.Hull) continue;
                int nx = cx + dx, ny = cy + dy;
                if (nx < 0 || ny < 0 || nx >= Data.Width || ny >= Data.Height || Data.Sides[ny * Data.Width + nx] != side)
                    return null;
            }
            return i;
        }

        internal int[] FromSite(Site site) => matrix[site.Index].Value;
        internal int[] Costs(double x, double y, int node, int side, int cutoff)
        {
            // One bounded field from the off-centre hero, not 46 separate A* calls. Every undirected edge
            // was swept in BOTH directions on the 6989 terrain, and diagonal shoulders are on the same side.
            // Cardinal = 64; diagonal = ceil(64*sqrt(2)) = 91. No smoothing, teleport, flying or air-distance fallback.
            var edges = Data.Edges; var d = ArrayPool<int>.Shared.Rent(edges.Length);
            try {
                Array.Fill(d, int.MaxValue, 0, edges.Length);
                var q = new PriorityQueue<int, int>(); d[node] = Tail(x, y, node); q.Enqueue(node, d[node]);
                while (q.TryDequeue(out int i, out int cost)) {
                    if (cost > cutoff) break;
                    if (cost != d[i]) continue;
                    int mask = edges[i];
                    for (int k = 0; mask != 0; k++, mask >>= 1) {
                        if ((mask & 1) == 0) continue;
                        int j = i + delta[k], next = cost + ((k & 1) == 0 ? 64 : 91);
                        if (next >= d[j] || next > cutoff) continue;
                        d[j] = next; q.Enqueue(j, next);
                    }
                }
                var outCosts = new int[Data.Sites.Length];
                foreach (var s in Data.Sites) {
                    int cost = s.Side != side || d[s.Node] == int.MaxValue ? int.MaxValue :
                        s.X == x && s.Y == y ? 0 : d[s.Node] + s.Tail;
                    outCosts[s.Index] = cost <= cutoff ? cost : int.MaxValue;
                }
                return outCosts;
            } finally { ArrayPool<int>.Shared.Return(d); }
        }
    }
}
