namespace DotaCoach.TwoPc;

internal static partial class LaneWave742
{
    internal readonly record struct Located(int Road, int Side, string Name, double X, double Y, double Along);

    /// <summary>Only consumes admitted matched team packets, once per game second. GSI's o0/o1
    /// are NOT entity identities. Same-kind neighbours can swap; a fog dot is never a confirmed survivor.</summary>
    internal sealed class Tracker
    {
        private sealed record Track(Located Unit, Front Body, int Seen, double Velocity, double Stop, int? Cohort)
        {
            public Pt At(int t) => Body.At(Math.Min(Stop, Math.Max(0, t - Seen) * Velocity));
            public int Life => Stop == 0 ? 4 : LaneMap742.Memory; // Last seen in contact: survival is especially uncertain.
        }
        private List<Track> tracks = [];
        private (string Match, string Side)? scope;
        private int last = -1;
        private long received;
        private readonly LaneContact?[] contacts = new LaneContact?[3];
        private LaneWaveSample? snapshot;
        private (int Clock, bool Use, LaneMap742 Map)? cached;
        public void Clear() { tracks.Clear(); Array.Clear(contacts); scope = null; last = -1; snapshot = null; cached = null; }
        private static int I(double v) => (int)Math.Round(v);
        private static Path Road(int i, int side) => side == 2 ? Map.Value[i].Good : Map.Value[i].Bad;
        internal LaneWaveSample Update(MatchWitness own, long receipt, List<Located> rows, LaneWaveSample sample, IEnumerable<(double X, double Y)> heroes)
        {
            int t = (int)Math.Floor(sample.ObservedClock);
            var key = (own.MatchId, own.Team);
            if (scope != key || t < last || t - last > FreshSeconds || receipt < received || receipt - received >= TeamViewEvidence.FreshMs) Clear();
            if (t == last && snapshot != null) return snapshot;
            scope = key; received = receipt; last = t; cached = null;
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
                if (cohort == null && u.Along < 2200) {
                    int spawn = (int)Math.Round((t - u.Along / Speed) / Period) * Period;
                    if (spawn >= 0 && spawn <= t && Math.Abs(t - spawn - u.Along / Speed) <= 5) cohort = spawn;
                }
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
            var allies = rows.Where(r => r.Side == ourSide).Select(r => new Pt(r.X, r.Y)).Concat(heroes.Select(h => new Pt(h.X,h.Y))).ToArray();
            bool CouldBeInFog(Track a) {
                // Missing near several clearly visible creeps is often a kill, not a walk into fog.
                // A proximity veto merely WITHHOLDS an individual dot. It does not infer actual vision through terrain or a death.
                var p = a.At(t);
                return a.Unit.Side != ourSide && !allies.Any(q => q.Dist(p) <= 480 || q.Dist(a.Body.Start) <= 350) &&
                    !rows.Any(r => r.Side == a.Unit.Side && r.Road == a.Unit.Road && new Pt(r.X,r.Y).Dist(p) <= 130);
            }
            next.AddRange(tracks.Where((a, i) => !oldUsed[i] && CouldBeInFog(a)).OrderByDescending(a => a.Seen).Take(Math.Max(0, 256 - next.Count)));
            tracks = next;
            var lanes = sample.Lanes.Select((r, i) => {
                if (r.Basis == "seen-close") contacts[i] = r.Meeting;
                else if (contacts[i] is { } c && (t - c.Clock > LaneMap742.FrontMemory || r.Basis == "ambiguous" ||
                    rows.Any(u => u.Road == i && u.Along - Road(i, u.Side).Project(new(c.WorldX,c.WorldY)).Along > 750)))
                    contacts[i] = null; // witnessed progress beyond the old fight / competing lines contradict a held front
                var result = WithMemory(i, t, r);
                if (contacts[i] is not { } front || front.Clock >= t || result.Basis is "seen-close" or "two-visible-fronts" or "recent-fog-fronts" or "ambiguous")
                    return result;
                // A wave which would already have passed the last confirmed fight cannot be assumed
                // to have marched freely through it. Use the visible-front estimate otherwise.
                if (result.Meeting != null && result.AssumedSpawnClock is int spawn) {
                    var hidden = Road(i, result.RadiantSeen > 0 ? 3 : 2);
                    if ((front.Clock - spawn) * Speed < hidden.Project(new(front.WorldX, front.WorldY)).Along + Radius) return result;
                }
                return HeldMeeting(i, t, result, front);
            }).ToArray();
            return snapshot = sample with { Lanes = lanes };
        }
        private static LaneWaveReading HeldMeeting(int i, int t, LaneWaveReading row, LaneContact lastClash) {
            var point = new Pt(lastClash.WorldX, lastClash.WorldY);
            var eta = new[] { 2,3 }.Select(side => {
                int travel = (int)Math.Ceiling(Road(i,side).Project(point).Along / Speed);
                int sp = Math.Max(0, (int)Math.Ceiling((t-travel)/(double)Period)) * Period;
                return (Clock: sp+travel, Spawn: sp);
            }).OrderBy(a => a.Clock).First();
            return row with { Basis = "reinforce-last-contact", FogAnchorClock = lastClash.Clock, AssumedSpawnClock = eta.Spawn,
                Meeting = lastClash with { Clock = eta.Clock } };
        }
        private LaneWaveReading WithMemory(int road, int t, LaneWaveReading row)
        {
            if (row.Basis is "seen-close" or "ambiguous") return row;
            var local = tracks.Where(a => a.Unit.Road == road && t - a.Seen <= LaneMap742.Memory).ToArray();
            if (!local.Any(a => a.Seen != t)) return row;
            // Multiple reinforcements may occupy different stretches at once. Use the front of each
            // separated column; recent hidden fronts compete with visible ones, but never acquire a sighting label.
            List<(Track Track, Front Front)> Heads(int side) {
                var result = new List<(Track, Front)>(); double previous = double.PositiveInfinity;
                var path = Road(road, side);
                var all = local.Where(a => a.Unit.Side == side).Select(a => {
                    var p = a.At(t); return (Track: a, Front: path.From(p, path.Project(p).Along)); });
                foreach (var a in all.OrderByDescending(x => x.Front.Along)) {
                    if (previous - a.Front.Along > GroupGap) result.Add(a);
                    previous = a.Front.Along;
                }
                return result;
            }
            var good = Heads(2); var bad = Heads(3); LaneContact? best = null; bool ambiguous = false;
            int? basisClock = null, spawnClock = null;
            double V(Track k) => k.Seen == t ? Speed : (t - k.Seen) * k.Velocity >= k.Stop ? 0 : k.Velocity;
            void Offer(LaneContact? q, int at, int? spawn = null) {
                if (q == null || q.Clock - at > LaneMap742.Memory) return;
                if (best == null || q.Clock < best.Clock) { best = q; basisClock = at; spawnClock = spawn; ambiguous = false; }
                else if (q.Clock == best.Clock && double.Hypot(q.WorldX - best.WorldX, q.WorldY - best.WorldY) > GroupGap) ambiguous = true;
            }
            foreach (var a in good) foreach (var b in bad) {
                if (a.Track.Seen == t && b.Track.Seen == t) continue;
                var l = Map.Value[road]; var q = a.Front.Start.Dist(b.Front.Start) <= Radius ?
                    l.Mark(t, a.Front.Start.To(b.Front.Start, .5)) : Intercept(l, t, a.Front, b.Front, speedA: V(a.Track), speedB: V(b.Track));
                // Don't promise a contact beyond the useful lifetime of its oldest unseen anchor.
                Offer(q, Math.Min(a.Track.Seen, b.Track.Seen));
            }
            if ((good.Count == 0 || bad.Count == 0) && row.Meeting == null) {
                var other = Road(road, good.Count == 0 ? 2 : 3);
                foreach (var a in good.Concat(bad)) for (int sp = Math.Max(0, t / Period * Period - 2 * Period); sp <= t + LaneMap742.Memory; sp += Period) {
                    double s = Math.Max(0, t - sp) * Speed; if (s >= other.Length) continue;
                    Offer(Intercept(Map.Value[road], t, a.Front, other.Scheduled(s), bWait: Math.Max(0, sp - t), speedA: V(a.Track)), a.Track.Seen, sp);
                }
            }
            if (best == null || row.Basis == "two-visible-fronts" && row.Meeting!.Clock <= best.Clock) return row;
            return row with { Basis = ambiguous ? "ambiguous" : "recent-fog-fronts", AssumedSpawnClock = ambiguous ? null : spawnClock,
                FogAnchorClock = ambiguous ? null : basisClock, Meeting = ambiguous ? null : best };
        }

        // Direct late join is O(recent waves), not a replay from 0:00. Pure clock mode has NO path
        // to a track. A schedule dot represents a wave, never a fabricated list of individual creeps.
        internal LaneMap742 Project(int t, bool allowSight)
        {
            bool use = allowSight && snapshot != null && t >= last && t - last < FreshSeconds;
            if (cached is { } hit && hit.Clock == t && hit.Use == use) return hit.Map;
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
                marks.Add(new(l.Id, 0, kind, mt.WorldX, mt.WorldY, kind == "contact" ? 0 : kind == "baseline" ? 900 : 420,
                    source, Spawn: kind == "baseline" ? baseline.SpawnClock : current!.AssumedSpawnClock, Meeting: mt.Clock));
                // Distinguish "where the next wave should meet" from "the previous wave could still
                // be fighting here". Never keep walking a theoretical survivor through the whole map.
                if (use && kind != "contact" && contacts[i] is { } c && t > c.Clock && t-c.Clock <= LaneMap742.FrontMemory) {
                    marks.Add(new(l.Id, 0, "last-clash", c.WorldX, c.WorldY, 320 + 45*(t-c.Clock), SeenAt:c.Clock, Meeting:c.Clock));
                } else if (kind == "baseline" && current?.Basis is not ("ambiguous" or "unresolved") && t >= l.Baseline.Clock) {
                    int sp = (t - l.Baseline.Clock) / Period * Period, onset = sp + l.Baseline.Clock;
                    marks.Add(new(l.Id, 0, "contested", l.Baseline.WorldX, l.Baseline.WorldY, 580 + 36*(t-onset), Spawn: sp, Meeting: onset));
                }
                foreach (int side in new[] { 2, 3 }) {
                    var path = Road(i, side);
                    var group = use ? tracks.Where(a => a.Unit.Side == side && a.Unit.Road == i && t - a.Seen <= a.Life).ToArray() : [];
                    foreach (var a in group) {
                        var p = a.At(t); int age = t - a.Seen;
                        infantry.Add(new(l.Id, side, age == 0 ? "seen" : "fog", I(p.X), I(p.Y), age == 0 ? 0 : 140 + 105 * age,
                            a.Seen, age == 0 ? null : I(a.Body.Start.X), age == 0 ? null : I(a.Body.Start.Y)));
                    }
                    double contactS = path.Project(new(mt.WorldX, mt.WorldY)).Along;
                    for (int spawn = t / Period * Period; spawn >= 0 && t - spawn <= 90; spawn -= Period) {
                        int age = t - spawn;
                        if (age > contactS / Speed + 4 || group.Any(a => a.Cohort == spawn)) continue;
                        var p = path.At(Math.Min(contactS, age * Speed));
                        if (group.Any(a => a.At(t).Dist(p) <= GroupGap)) continue;
                        marks.Add(new(l.Id, side, "march", I(p.X), I(p.Y), Math.Min(1900, 160 + 36 * age), Spawn: spawn));
                    }
                }
            }
            // Preserve meetings/reinforcements and representation on every road under a crowded map.
            var buckets = infantry.OrderBy(a => a.Kind != "seen").ThenByDescending(a => a.SeenAt)
                .GroupBy(a => (a.Lane, a.Team, a.Kind)).Select(g => new Queue<LaneMark>(g)).ToArray();
            while (marks.Count < LaneMap742.MaxMarks && buckets.Any(q => q.Count > 0))
                foreach (var q in buckets) if (marks.Count < LaneMap742.MaxMarks && q.TryDequeue(out var m)) marks.Add(m);
            var value = new LaneMap742(t, [.. marks], LaneMap742.Lease);
            cached = (t, use, value); return value;
        }
    }
}
