namespace DotaCoach.TwoPc;

internal static partial class LaneWave742
{
    internal readonly record struct Located(int Road, int Side, string Name, double X, double Y, double Along);

    /// <summary>Only consumes admitted matched team packets, once per game second. GSI's o0/o1
    /// are NOT entity identities. Same-kind neighbours can swap; a fog dot is never a confirmed survivor.</summary>
    internal sealed class Tracker
    {
        private const int ForecastSeconds = 5;
        private sealed record Track(Located Unit, Front Body, int Seen, double Velocity, double Stop, int? Cohort)
        {
            /// <summary>Where it would be at the observed pace: for matching the next sighting.</summary>
            public Pt At(double t) => Body.At(Math.Min(Stop, Math.Max(0, t - Seen) * Velocity));
            /// <summary>Out of sight a creep marches on at full speed from its last sighting, even if it was fighting then:
            /// on five replays 90-99 % of such creeps were within 600 of this for 10 s (the observed pace: 40-68 %).</summary>
            public Pt Hidden(double t) => Body.At(Math.Min(Body.Left, Math.Max(0, t - Seen) * Speed));
            public int Life => Stop == 0 ? 4 : LaneMap742.Memory; // Last seen in contact: survival is especially uncertain.
        }
        private List<Track> tracks = [];
        private (string Match, string Side)? scope;
        private int last = -1;
        private long received;
        private readonly LaneContact?[] contacts = new LaneContact?[3];
        private LaneWaveSample? snapshot;
        private (int Clock, bool Use, double Ahead, LaneMap742 Map)? cached;
        private (double X, double Y, double R)[] sight = [];
        // Enemy waves this team has seen: they no longer march on schedule, so no theoretical symbol follows them.
        private readonly HashSet<(int Road, int Side, int Spawn)> observed = [];
        public void Clear() { tracks.Clear(); Array.Clear(contacts); scope = null; last = -1; snapshot = null; cached = null; sight = []; observed.Clear(); }
        // A hidden creep or wave cannot stand where this team currently sees: there it would be on the minimap.
        // On the 2026-11-25 games a theoretical wave inside our sight was right 4-6 % of the time.
        private bool InSight(Pt p) => sight.Any(s => p.Dist(new(s.X, s.Y)) <= s.R);
        private static int I(double v) => (int)Math.Round(v);
        private static Path Road(int i, int side) => side == 2 ? Map.Value[i].Good : Map.Value[i].Bad;
        internal LaneWaveSample Update(MatchWitness own, long receipt, List<Located> rows, LaneWaveSample sample, (double X, double Y, double R)[] seeing)
        {
            int t = (int)Math.Floor(sample.ObservedClock);
            var key = (own.MatchId, own.Team);
            if (scope != key || t < last || t - last > FreshSeconds || receipt < received || receipt - received >= TeamViewEvidence.FreshMs) Clear();
            if (t == last && snapshot != null) return snapshot;
            scope = key; received = receipt; last = t; cached = null; sight = seeing;
            tracks.RemoveAll(a => t - a.Seen > a.Life);
            // One-to-one associations by plausible displacement and closest predicted position.
            // Prefer very recent sightings. Never attach an ID from the replay or another lane/side.
            var pairs = new List<(double Cost, int Old, int New)>();
            for (int j = 0; j < rows.Count; j++) {
                var u = rows[j]; var pt = new Pt(u.X, u.Y);
                for (int i = 0; i < tracks.Count; i++) {
                    var a = tracks[i]; int dt = t - a.Seen;
                    if (dt < 1 || a.Unit.Road != u.Road || a.Unit.Name != u.Name || a.Unit.Side != u.Side ||
                        pt.Dist(a.Body.Start) > 460 * dt + 96) continue;
                    double d = pt.Dist(a.At(t));
                    if (d <= 180 + 205 * dt) pairs.Add((d + 70 * (dt - 1), i, j));
                }
            }
            var oldUsed = new bool[tracks.Count]; var matched = Enumerable.Repeat(-1, rows.Count).ToArray();
            foreach (var p in pairs.OrderBy(p => p.Cost).ThenBy(p => p.Old).ThenBy(p => p.New))
                if (!oldUsed[p.Old] && matched[p.New] == -1) { oldUsed[p.Old] = true; matched[p.New] = p.Old; }
            var next = new List<Track>();
            for (int j = 0; j < rows.Count; j++) {
                var u = rows[j]; var pt = new Pt(u.X, u.Y); var f = Road(u.Road, u.Side).From(pt, u.Along);
                Track? old = matched[j] == -1 ? null : tracks[matched[j]];
                bool close = rows.Any(v => v.Side != u.Side && v.Road == u.Road && pt.Dist(new(v.X, v.Y)) <= Radius);
                double velocity = close ? 0 : old != null && t - old.Seen <= 3
                    ? Math.Clamp((u.Along - old.Body.Along) / (t - old.Seen), 0, Speed) : Speed;
                int? cohort = old?.Cohort;
                if (cohort == null) {
                    // Still on schedule: its wave's middle is within about 3 s of walking of where the clock puts it.
                    double walked = (u.Along - Road(u.Road, u.Side).WaveMiddle) / Speed;
                    int spawn = (int)Math.Round((t - walked) / Period) * Period;
                    if (spawn >= 0 && spawn <= t && Math.Abs(t - spawn - walked) <= 3) cohort = spawn;
                }
                if (cohort is int seenWave) observed.Add((u.Road, u.Side, seenWave));
                double stop = f.Left;
                // Never march an unseen creep through a previously witnessed/anticipated engagement as if it won.
                var predicted = sample.Lanes[u.Road] is { Basis: "two-visible-fronts", Meeting: { } meet } ? meet : null;
                if (close) stop = 0;
                else if (predicted != null) {
                    var contact = new Pt(predicted.WorldX, predicted.WorldY);
                    stop = Math.Min(stop, Math.Max(0, f.Path.Project(contact).Along - u.Along));
                }
                next.Add(new(u, f, t, velocity, stop, cohort));
            }
            int ourSide = own.Team == "radiant" ? 2 : 3;
            bool CouldBeInFog(Track a) {
                // Vanishing where it would still be in our sight a moment later is a kill, not a walk into fog.
                // The veto merely WITHHOLDS an individual dot; it does not infer vision through terrain.
                var p = a.Hidden(t);
                return a.Unit.Side != ourSide && !InSight(p) &&
                    !rows.Any(r => r.Side == a.Unit.Side && r.Road == a.Unit.Road && new Pt(r.X,r.Y).Dist(p) <= 130);
            }
            next.AddRange(tracks.Where((a, i) => !oldUsed[i] && CouldBeInFog(a)).OrderByDescending(a => a.Seen).Take(Math.Max(0, 256 - next.Count)));
            tracks = next;
            var lanes = sample.Lanes.Select((r, i) => {
                if (r.Basis == "seen-close") contacts[i] = r.Meeting;
                else if (contacts[i] is { } c && (t - c.Clock > LaneMap742.FrontMemory || r.Basis == "ambiguous" ||
                    rows.Any(u => u.Road == i && u.Along - Road(i, u.Side).Project(new(c.WorldX,c.WorldY)).Along > 750)))
                    contacts[i] = null; // witnessed progress beyond the old fight / competing lines contradict a held front
                var result = Forecast(i, t, r, ourSide);
                if (contacts[i] is not { } front || front.Clock >= t || result.Basis is "seen-close" or "two-visible-fronts" or "recent-fog-fronts" or "ambiguous")
                    return result;
                // A wave which would already have passed the last confirmed fight cannot be assumed
                // to have marched freely through it. Use the visible-front estimate otherwise.
                if (result.Meeting != null && result.AssumedSpawnClock is int spawn) {
                    var hidden = Road(i, 5 - ourSide);
                    if (hidden.WaveFront + (front.Clock - spawn) * Speed < hidden.Project(new(front.WorldX, front.WorldY)).Along + Radius) return result;
                }
                return HeldMeeting(i, t, result, front);
            }).ToArray();
            return snapshot = sample with { Lanes = lanes };
        }
        private static LaneWaveReading HeldMeeting(int i, int t, LaneWaveReading row, LaneContact lastClash) {
            var point = new Pt(lastClash.WorldX, lastClash.WorldY);
            var eta = new[] { 2,3 }.Select(side => {
                var road = Road(i,side);
                int travel = (int)Math.Ceiling(Math.Max(0, road.Project(point).Along - road.WaveFront) / Speed);
                int sp = Math.Max(0, (int)Math.Ceiling((t-travel)/(double)Period)) * Period;
                return (Clock: sp+travel, Spawn: sp);
            }).OrderBy(a => a.Clock).First();
            return row with { Basis = "reinforce-last-contact", FogAnchorClock = lastClash.Clock, AssumedSpawnClock = eta.Spawn,
                Meeting = lastClash with { Clock = eta.Clock } };
        }
        // The next contact on this lane from where everything is now: our fronts (the team always sees its own creeps; the
        // next own wave when none is out) against every enemy hypothesis that held up on replays: enemy creeps in sight,
        // ones marching on in fog, and never-seen waves marching on schedule from their lane's head start, unless our
        // sight shows their place empty. A current contact or an ambiguous lane keeps its own reading.
        private LaneWaveReading Forecast(int road, int t, LaneWaveReading row, int ours)
        {
            if (row.Basis is "seen-close" or "ambiguous") return row;
            var l = Map.Value[road]; int theirs = 5 - ours;
            Path mine = Road(road, ours), path = Road(road, theirs);
            // The first-to-arrive creep of each separated column, not every creep of it. A visible creep keeps the pace it
            // was seen at (about 0 while it hits a tower or a hero); one out of sight marches on.
            static IEnumerable<(Front Front, double Pace, int Seen)> Heads(IEnumerable<(Front Front, double Pace, int Seen)> all) {
                double previous = double.PositiveInfinity;
                foreach (var a in all.OrderByDescending(x => x.Front.Along)) {
                    if (previous - a.Front.Along > GroupGap) yield return a;
                    previous = a.Front.Along;
                }
            }
            IEnumerable<(Front, double, int)> Side(int side, bool visible) {
                var route = side == ours ? mine : path;
                foreach (var a in tracks.Where(a => a.Unit.Road == road && a.Unit.Side == side &&
                    (visible ? a.Seen == t : a.Seen < t && t - a.Seen <= a.Life))) {
                    var p = visible ? a.At(t) : a.Hidden(t);
                    yield return (route.From(p, route.Project(p).Along), visible ? a.Velocity : Speed, a.Seen);
                }
            }
            var own = Heads(Side(ours, true)).Select(a => (a.Front, a.Pace, Wait: 0.0)).ToList();
            if (own.Count == 0)
                for (int sp = (t + Period - 1) / Period * Period; sp <= t + Horizon; sp += Period) own.Add((mine.Scheduled(mine.WaveFront), Speed, sp - t));
            var enemy = new List<(Front Front, double Pace, double Wait, string Basis, int? Anchor, int? Spawn)>();
            foreach (var a in Heads(Side(theirs, true))) enemy.Add((a.Front, a.Pace, 0, "two-visible-fronts", null, null));
            foreach (var a in Heads(Side(theirs, false))) enemy.Add((a.Front, Speed, 0, "recent-fog-fronts", a.Seen, null));
            for (int sp = Math.Max(0, t / Period * Period - 2 * Period); sp <= t + Horizon; sp += Period) {
                double s = path.WaveFront + Math.Max(0, t - sp) * Speed;
                if (s >= path.Length || observed.Contains((road, theirs, sp)) || InSight(path.At(s - path.WaveFront + path.WaveMiddle))) continue;
                enemy.Add((path.Scheduled(s), Speed, Math.Max(0, sp - t), "one-visible-plus-assumed-spawn", null, sp));
            }
            LaneContact? best = null; (string Basis, int? Anchor, int? Spawn) why = default; bool split = false;
            foreach (var a in own) foreach (var b in enemy) {
                var q = a.Wait == 0 && b.Wait == 0 && a.Front.Start.Dist(b.Front.Start) <= Radius ? l.Mark(t, a.Front.Start.To(b.Front.Start, .5))
                    : Intercept(l, t, a.Front, b.Front, a.Wait, b.Wait, a.Pace, b.Pace);
                // Don't promise a contact beyond the useful lifetime of an unseen anchor.
                if (q == null || b.Anchor is int seen && q.Clock - seen > LaneMap742.Memory) continue;
                if (best == null || q.Clock < best.Clock) { best = q; why = (b.Basis, b.Anchor, b.Spawn); split = false; }
                else if (q.Clock == best.Clock && double.Hypot(q.WorldX - best.WorldX, q.WorldY - best.WorldY) > GroupGap) split = true;
            }
            if (best == null) return row.Meeting == null ? row : row with { Basis = "unresolved", Meeting = null, AssumedSpawnClock = null, FogAnchorClock = null };
            if (split) return row with { Basis = "ambiguous", Meeting = null, AssumedSpawnClock = null, FogAnchorClock = null };
            return row with { Basis = why.Basis, Meeting = best, AssumedSpawnClock = why.Spawn, FogAnchorClock = why.Anchor };
        }

        // Direct late join is O(recent waves), not a replay from 0:00. Pure clock mode has NO path
        // to a track. A schedule dot represents a wave, never a fabricated list of individual creeps.
        // Hypotheses (fog dots, theoretical waves) are placed `ahead` seconds after the map's clock: the game client shows
        // a map about a second after the participant second it was made for. A seen dot stays where it was seen.
        internal LaneMap742 Project(int t, bool allowSight, double ahead = 0)
        {
            bool use = allowSight && snapshot != null && t >= last && t - last < FreshSeconds;
            if (cached is { } hit && hit.Clock == t && hit.Use == use && hit.Ahead == ahead) return hit.Map;
            double at = t + ahead;
            int ours = scope?.Side == "radiant" ? 2 : 3;
            var marks = new List<LaneMark>(); var infantry = new List<LaneMark>();
            var baselines = Baseline(t);
            for (int i = 0; i < 3; i++) {
                var l = Map.Value[i]; var baseline = baselines[i]; var current = use ? snapshot!.Lanes[i] : null;
                if (use && t > last && current is { } r) {
                    // A measured contact remains useful as a bounded hypothesis, not a refreshed sighting.
                    if (r.Basis == "seen-close" && r.Meeting is { } m) current = r with { Basis = "recent-fog-fronts", FogAnchorClock = last, Meeting = m with { Clock = t } };
                    else if (r.Meeting?.Clock < t || t - r.FogAnchorClock > (r.Basis == "reinforce-last-contact" ? LaneMap742.FrontMemory : LaneMap742.Memory)) current = null;
                }
                var mt = current?.Meeting ?? baseline.Meeting;
                string kind = current?.Meeting == null ? "baseline" : current.Basis switch {
                    "seen-close" => "contact", "reinforce-last-contact" => "held", _ => "forecast" };
                int? source = kind == "baseline" ? null : current!.FogAnchorClock ?? last;
                // With the team's view, only measured contacts and near forecasts. On the 2026-11-25 games a forecast up to
                // 5 s ahead put the meeting within 600 of the real one 78-82 % of the time, 6-10 s ahead 46-47 %, further
                // about 30 %; the empty-lane baseline was right 3-11 % and the held reinforcement 20-24 %.
                if (!use || kind == "contact" || kind == "forecast" && mt.Clock - t <= ForecastSeconds)
                    marks.Add(new(l.Id, 0, kind, mt.WorldX, mt.WorldY, kind == "contact" ? 0 : kind == "baseline" ? 900 : 420,
                        source, Spawn: kind == "baseline" ? baseline.SpawnClock : current!.AssumedSpawnClock, Meeting: mt.Clock));
                // Distinguish "where the next wave should meet" from "the previous wave could still
                // be fighting here". Never keep walking a theoretical survivor through the whole map.
                if (use && kind != "contact" && contacts[i] is { } c && t > c.Clock && t-c.Clock <= LaneMap742.FrontMemory) {
                    marks.Add(new(l.Id, 0, "last-clash", c.WorldX, c.WorldY, 320 + 45*(t-c.Clock), SeenAt:c.Clock, Meeting:c.Clock));
                } else if (!use && kind == "baseline" && t >= l.Baseline.Clock) {
                    int sp = (t - l.Baseline.Clock) / Period * Period, onset = sp + l.Baseline.Clock;
                    marks.Add(new(l.Id, 0, "contested", l.Baseline.WorldX, l.Baseline.WorldY, 580 + 36*(t-onset), Spawn: sp, Meeting: onset));
                }
                foreach (int side in new[] { 2, 3 }) {
                    var path = Road(i, side);
                    var group = use ? tracks.Where(a => a.Unit.Side == side && a.Unit.Road == i && t - a.Seen <= a.Life).ToArray() : [];
                    foreach (var a in group) {
                        int age = t - a.Seen; var p = age == 0 ? a.At(t) : a.Hidden(at);
                        // Next to a creep of its side in sight it says nothing new; on the 2026-11-25 games such a ○ was
                        // mostly that creep seen again (dropping them: ○ right 86.6/83.2 → 90.5/85.3 %, coverage -0.3).
                        if (age > 0 && (InSight(p) || group.Any(b => b.Seen == t && b.At(t).Dist(p) <= 250))) continue;
                        infantry.Add(new(l.Id, side, age == 0 ? "seen" : "fog", I(p.X), I(p.Y), age == 0 ? 0 : 140 + 105 * age,
                            a.Seen, age == 0 ? null : I(a.Body.Start.X), age == 0 ? null : I(a.Body.Start.Y)));
                    }
                    // The team always sees its own waves; an enemy wave it has seen no longer marches on schedule.
                    if (use && side == ours) continue;
                    double contactS = path.Project(new(mt.WorldX, mt.WorldY)).Along, gap = path.WaveFront - path.WaveMiddle;
                    for (int spawn = t / Period * Period; spawn >= 0 && t - spawn <= 90; spawn -= Period) {
                        double middle = path.WaveMiddle + (at - spawn) * Speed;
                        // Without the team's view a wave stops at the expected meeting; with it, it marches until our sight would show it.
                        if (group.Any(a => a.Cohort == spawn) || (use ? middle >= path.Length || observed.Contains((i, side, spawn))
                            : middle + gap > contactS + 4 * Speed)) continue;
                        var p = path.At(use ? middle : Math.Min(contactS - gap, middle));
                        if (group.Any(a => a.At(t).Dist(p) <= GroupGap) || use && InSight(p)) continue;
                        marks.Add(new(l.Id, side, "march", I(p.X), I(p.Y), Math.Min(1900, 160 + 36 * (t - spawn)), Spawn: spawn));
                    }
                }
            }
            // Preserve meetings/reinforcements and representation on every road under a crowded map.
            var buckets = infantry.OrderBy(a => a.Kind != "seen").ThenByDescending(a => a.SeenAt)
                .GroupBy(a => (a.Lane, a.Team, a.Kind)).Select(g => new Queue<LaneMark>(g)).ToArray();
            while (marks.Count < LaneMap742.MaxMarks && buckets.Any(q => q.Count > 0))
                foreach (var q in buckets) if (marks.Count < LaneMap742.MaxMarks && q.TryDequeue(out var m)) marks.Add(m);
            var value = new LaneMap742(t, [.. marks], LaneMap742.Lease);
            cached = (t, use, ahead, value); return value;
        }
    }
}
