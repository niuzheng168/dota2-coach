namespace DotaCoach.TwoPc;

public sealed record DrillMark(CreepBar Bar, HitPlan Plan)
{
    public LaneCreepType CreepType { get; init; }
    // Only on the first direct decision for a continuously one-hit-eligible track; source clock, not UI time.
    public double? FirstLethalPixelAt { get; init; }
    // Retained on subsequent frames so the dispatcher cannot rebase a delayed first paint onto a newer frame.
    public double? DirectEvidenceAt { get; init; }
}

/// <summary>First-wave, level-one naked Luna against regular creeps at a single T1. Units are
/// matched only between consecutive pictures; evidence is thrown away on crossings and cuts.</summary>
public sealed class LaneDrill
{
    // Unknown: Value is the melee hypothesis and RangedAlternative the ranged hypothesis.
    // Neither hypothesis is reported as the established type, and neither is a percentage.
    public readonly record struct Reading(CreepBar Bar, LaneCreepType Type, int Value, long Revision, double ChangedAt)
    {
        public LaneForecast? Next { get; init; }
        public long LossRevision { get; init; }
        public double LossAt { get; init; }
        public int? RangedAlternative {get;init;}
    }
    private static long nextReadingRevision;
    public (int Melee, int Ranged, int Unknown) VisibleTypes { get; private set; }
    private sealed record Kind(int Hp, double Regen, HitRange Hero, HitRange Tower);
    // Damage ranges include the normal roll and armor. Fixed training profile, not a live stat feed.
    private static readonly Kind Melee = new(LaneHealthProfile.BaseMelee, .5, new(44, 53), new(78, 83));
    private static readonly Kind Ranged = new(LaneHealthProfile.BaseRanged, 2, new(50, 59), new(88, 92));
    private const double Period = .82, HeroPeriod = 1.37;
    public const int DecisionBudgetMs = 50;
    private const double Confirm = .012; // Two different pictures, not two polls of the same pixels.
    private readonly double flight; // turn-free attack point plus projectile travel, hero already in range
    private readonly LaneHealthProfile hpProfile;
    public LaneDrill(double heroImpact = .58,LaneHealthProfile? hpProfile=null)
    {
        if (!double.IsFinite(heroImpact) || heroImpact < .28 || heroImpact > .70) throw new ArgumentOutOfRangeException(nameof(heroImpact));
        flight = heroImpact; this.hpProfile=hpProfile??LaneHealthProfile.Standard;
    }
    // For rehearsing the opening hit before there is enough on-screen evidence of tower aggro.
    // Explicit inputs, not a way to silently declare that an arbitrary on-screen creep is targeted.
    public HitPlan? Rehearse(bool ranged, int hp, double firstTower)
    {
        var k = ranged ? Ranged : Melee;
        if (hp < 1 || hp > k.Hp || firstTower <= 0 || firstTower > 4) return null;
        return TowerLastHit.Plan(new(hp, hp), k.Hero, flight, HeroPeriod, 0, k.Regen, k.Tower, firstTower, Period);
    }
    private sealed class Track(CreepBar bar, double now)
    {
        public CreepBar Bar = bar;
        public readonly double FirstSeen = now;
        public double FillSince = now;
        public int Frames, Hits;
        public double TowerAt = double.NegativeInfinity, UnclearAt = double.NegativeInfinity;
        public double LastDecrease = double.NegativeInfinity;
        public bool IsolatedChip, DirectReported;
        public double? LethalSince, MeleePixelSince, RangedPixelSince;
        public double DamageAt=double.NegativeInfinity, VoteAt;
        public LaneCreepType Vote, Role;
        public bool Disputed;
        public int LastValue = -1;
        public int? LastAlternative;
        public LaneCreepType LastValueType;
        public double ValueSince;
        public long Revision;
        public long LossRevision; public double LossAt;
        public LaneCadence Cadence = new();
        public LaneForecast? LastForecast;
        public void Confirm(LaneCreepType role)
        {
            if (Disputed) return;
            if (Role != LaneCreepType.Unknown && Role != role) { Role=LaneCreepType.Unknown; Disputed=true; }
            else Role=role;
        }
        public double PeriodEstimate = Period;
        public Kind? Kind;
        public Kind? OtherTower;
        public double OtherTowerAt=double.NegativeInfinity;
        public void Forget() { Kind = null; Hits = 0; TowerAt = double.NegativeInfinity; PeriodEstimate = Period; }
    }
    private List<Track> prev = [], current = [];
    private int[] match = [], uses = [];
    private double last = double.NegativeInfinity, ownImpact = double.NegativeInfinity;
    public long DirectCandidates { get; private set; }
    public List<DrillMark> Update(IReadOnlyList<CreepBar> bars, double now)
    {
        var marks = new List<DrillMark>();
        Update(bars, now, marks);
        return marks;
    }

    // Health-only mode: no attack threshold, cooldown, projectile prediction or tower plan.
    public int ReadHealth(IReadOnlyList<CreepBar> bars, double now, List<Reading> readings)
        => Read(bars,now,readings,false);

    // No unit stats, clock, game-state feed, hero profile, damage roll or unit ID in this path.
    public int ReadLaneVision(IReadOnlyList<CreepBar> bars, double now, List<Reading> readings)
        => Read(bars,now,readings,true);

    private int Read(IReadOnlyList<CreepBar> bars, double now, List<Reading> readings, bool laneVision)
    {
        TrackBars(bars, now, modelAttack:false, laneVision);
        readings.Clear(); int changes=0;
        foreach (var t in prev) {
            var type=t.Role;
            int maximum=type==LaneCreepType.Ranged ? hpProfile.Ranged : hpProfile.Melee;
            var next=laneVision && type!=LaneCreepType.Unknown ? t.Cadence.Project(t.Bar,now,maximum) : null;
            int value=LaneHealthProfile.Estimate(t.Bar,maximum);
            int? alternative=type==LaneCreepType.Unknown ? LaneHealthProfile.Estimate(t.Bar,hpProfile.Ranged) : null;
            if (t.LastValue!=value || t.LastAlternative!=alternative || t.LastValueType!=type || t.LastForecast!=next) {
                t.Revision=Interlocked.Increment(ref nextReadingRevision);
                if (t.LastValue!=value || t.LastAlternative!=alternative || t.LastValueType!=type) {
                    t.LossRevision=t.LastValueType==type && (value<t.LastValue || alternative<t.LastAlternative) ? t.Revision : 0;
                    t.LossAt=t.LossRevision!=0 ? now : 0;
                }
                t.LastValue=value; t.LastAlternative=alternative; t.LastValueType=type; t.LastForecast=next; t.ValueSince=now;
                changes++;
            }
            readings.Add(new(t.Bar,type,value,t.Revision,t.ValueSince) {RangedAlternative=alternative,Next=next,LossRevision=t.LossRevision,LossAt=t.LossAt});
        }
        return changes;
    }

    private bool TrackBars(IReadOnlyList<CreepBar> bars, double now, bool modelAttack, bool laneVision=false)
    {
        if (now <= last || now - last > .22) { prev.Clear(); ownImpact = double.NegativeInfinity; }
        else if (now - last > DecisionBudgetMs / 1000d + 1e-8) prev.Clear(); // Never confirm across a capture stall.
        last = now; current.Clear();
        if (match.Length < bars.Count) Array.Resize(ref match, Math.Max(16, bars.Count * 2));
        if (uses.Length < prev.Count) Array.Resize(ref uses, Math.Max(16, prev.Count * 2));
        Array.Fill(match, -1, 0, bars.Count); Array.Clear(uses, 0, prev.Count);
        static bool Near(CreepBar a, CreepBar b) => a.Ally == b.Ally && Math.Abs(a.Width - b.Width) <= 2
            && Math.Abs(a.X - b.X) <= a.Width * .40 && Math.Abs(a.Y - b.Y) <= a.Width * .30;
        // Match both directions before changing any track. Crossings must never inherit earlier evidence.
        for (int i = 0; i < bars.Count; i++)
            for (int j = 0; j < prev.Count; j++) {
                if (!Near(prev[j].Bar, bars[i])) continue;
                match[i] = match[i] == -1 ? j : -2; uses[j]++;
            }
        for (int i = 0; i < bars.Count; i++) {
            var b = bars[i];
            int j = match[i];
            if (j < 0 || uses[j] != 1) { current.Add(new(b, now)); continue; }
            var t = prev[j];
            int delta = t.Bar.Fill - b.Fill;
            bool sameWidth = t.Bar.Width == b.Width;
            if (laneVision) {
                if (!sameWidth || delta < -2) { current.Add(new(b,now)); continue; }
                if (!b.Ally) t.Cadence.Observe(delta,b.Width-2,now);
                if(delta>=2) t.DamageAt=now;
                t.Bar=b; t.Frames++; current.Add(t); continue;
            }
            if (delta != 0 || !sameWidth) { t.FillSince = now; t.IsolatedChip = false; }
            // A replacement creep or a discontinuity is not continuous evidence.
            if (delta < -2) { current.Add(new(b, now)); continue; }
            if (delta > 2) {
                double fraction = (double)delta / (b.Width - 2), error = 1.6 / (b.Width - 2);
                bool Fits(Kind k, HitRange d) => fraction >= d.Min/k.Hp - error && fraction <= d.Max/k.Hp + error;
                Kind? tower = Fits(Melee, Melee.Tower) ? Melee : Fits(Ranged, Ranged.Tower) ? Ranged : null;
                bool contraryTower=(t.Role==LaneCreepType.Melee && tower==Ranged) || (t.Role==LaneCreepType.Ranged && tower==Melee);
                // One matching percentage isn't a type switch. A corroborated opposing tower
                // signature invalidates the track's identity instead of trusting the larger threshold.
                if (contraryTower && !b.Ally) {
                    if (tower==t.OtherTower && Math.Abs(now-t.OtherTowerAt-Period)<=.15)
                        t.Confirm(tower==Ranged ? LaneCreepType.Ranged : LaneCreepType.Melee);
                    t.OtherTower=tower; t.OtherTowerAt=now; tower=null;
                }
                double gap = now - t.TowerAt;
                if (modelAttack && t.Hits >= 2 && t.Kind is { } known && Fits(known, known.Hero)
                    && Math.Abs(gap - t.PeriodEstimate) > TowerLastHit.TimingMargin) {
                    ownImpact = now;
                } else if (tower != null && !b.Ally) {
                    if (tower == t.Kind && Math.Abs(gap - Period) <= .15) {
                        t.Hits++; t.PeriodEstimate = (Period + gap) / 2;
                    } else { t.Forget(); t.Kind = tower; t.Hits = 1; }
                    t.TowerAt = now;
                } else {
                    // A small isolated step that is too small to be our hit OR a tower hit
                    // need not stall a full projectile flight. Recheck the post-hit pixels.
                    // Recent damage, changing width and possible hero/tower damage stay uncertain.
                    t.IsolatedChip = modelAttack && !contraryTower && t.Hits == 0 && sameWidth && now - t.FirstSeen >= .080 &&
                        now - t.LastDecrease >= flight && now - t.UnclearAt >= flight &&
                        fraction + error < (t.Role==LaneCreepType.Ranged ? Ranged.Hero.Min/Ranged.Hp : Melee.Hero.Min/Melee.Hp);
                    t.Forget(); t.UnclearAt = now;
                }
            }
            if (delta > 0) t.LastDecrease = now;
            if (delta >= 2 && sameWidth) t.DamageAt=now;
            if (now - t.TowerAt > t.PeriodEstimate + .14) {
                if (t.Hits > 0) { t.UnclearAt = now; t.IsolatedChip = false; }
                t.Forget();
            }
            t.Bar = b; t.Frames++; current.Add(t);
        }
        (prev, current) = (current, prev);
        if (laneVision) {
            int candidates=0;
            foreach (var t in prev) if(t.Cadence.Recent(now)) candidates++;
            if (candidates>1) foreach (var t in prev) t.Cadence.Reset();
            else foreach(var t in prev) {
                if(!t.Cadence.Armed) continue;
                bool melee=t.Cadence.FitsBoth(hpProfile.Melee,Melee.Tower,t.Bar.Width-2);
                bool ranged=t.Cadence.FitsBoth(hpProfile.Ranged,Ranged.Tower,t.Bar.Width-2);
                if(melee!=ranged) t.Confirm(melee ? LaneCreepType.Melee : LaneCreepType.Ranged);
            }
            UpdateRoles(now,candidates>1);
            CountRoles(); return candidates>1;
        }
        // Two creeps taking tower-sized hits at once is not a clean single-target drill.
        int towerTargets = 0;
        foreach (var t in prev) if (t.Hits > 0) towerTargets++;
        bool conflict = towerTargets > 1;
        UpdateRoles(now,conflict);
        CountRoles(); return conflict;
    }
    private void CountRoles()
    {
        int melee=0,ranged=0;
        foreach (var t in prev)
            if (t.Role==LaneCreepType.Melee) melee++; else if(t.Role==LaneCreepType.Ranged) ranged++;
        VisibleTypes=(melee,ranged,prev.Count-melee-ranged);
    }

    // Timing research/manual drill; deliberately not used to generate live HP labels.
    public void Update(IReadOnlyList<CreepBar> bars, double now, List<DrillMark> marks)
    {
        bool conflict=TrackBars(bars,now,modelAttack:true);
        marks.Clear();
        foreach (var t in prev) {
            var b = t.Bar;
            bool CanSecure(Kind k) => b.Health(k.Hp).Max + k.Regen*flight <= k.Hero.Min;
            bool meleeHp=t.Hits==0 && CanSecure(Melee), rangedHp=t.Hits==0 && CanSecure(Ranged);
            t.MeleePixelSince=meleeHp ? t.MeleePixelSince ?? now : null;
            t.RangedPixelSince=rangedHp ? t.RangedPixelSince ?? now : null;
            bool oneHit=t.Role switch { LaneCreepType.Ranged => rangedHp, LaneCreepType.Melee => meleeHp, _ => meleeHp && rangedHp };
            if (!oneHit) { t.LethalSince = null; t.DirectReported = false; }
            else if (!t.LethalSince.HasValue) {
                // Include time spent waiting for type evidence when an already-low ranged creep is recognized.
                t.LethalSince=t.Role switch { LaneCreepType.Ranged=>t.RangedPixelSince, LaneCreepType.Melee=>t.MeleePixelSince,
                    _=>Math.Max(t.MeleePixelSince!.Value,t.RangedPixelSince!.Value) };
                DirectCandidates++;
            }
            if (t.Frames < 1 || now - t.FirstSeen < Confirm - 1e-8) continue;
            if (now - t.UnclearAt < flight && !(t.IsolatedChip && now - t.FillSince >= Confirm - 1e-8)) continue;
            double ready = Math.Max(0, ownImpact + HeroPeriod - flight - now);
            HitPlan? plan = null;
            if (t.Hits > 0) {
                if (conflict || t.Disputed || t.Hits < 2 || t.Kind == null) continue;
                double next = t.TowerAt + t.PeriodEstimate - now;
                // A stable fill still quantizes HP to a pixel. Allow extra error at a changing edge.
                double pixelError = b.Width == 80 && now - t.FillSince >= .040 - 1e-8 ? 1 : 1.5;
                plan = TowerLastHit.Plan(b.Health(t.Kind.Hp, pixelError), t.Kind.Hero, flight, HeroPeriod, ready, t.Kind.Regen,
                    t.Kind.Tower, next, t.PeriodEstimate, immediateOnly:true);
            } else if (ready == 0) {
                // The one-hit upper bound is already below the deny HP limit for both ordinary types.
                if (oneHit)
                    plan = new(b.Ally ? HitAction.Deny : HitAction.Finish, 0, 0, 0);
            }
            if (plan?.Action is HitAction.Finish or HitAction.Deny) {
                marks.Add(new(b, plan) { CreepType=t.Role, DirectEvidenceAt = oneHit ? t.LethalSince : null,
                    FirstLethalPixelAt = oneHit && !t.DirectReported ? t.LethalSince : null });
                if (oneHit) t.DirectReported = true;
            }
        }
    }

    private void UpdateRoles(double now, bool towerConflict)
    {
        Span<LaneFormation.Sample> samples=stackalloc LaneFormation.Sample[8];
        Span<LaneCreepType> roles=stackalloc LaneCreepType[8];
        bool complete=prev.Count is >=6 and <=8 && !towerConflict;
        if (complete) for(int i=0;i<prev.Count;i++) {
            var t=prev[i];
            if(t.Frames<1 || t.Hits!=0) { complete=false; break; }
            samples[i]=new(t.Bar,now-t.DamageAt<=.65);
        }
        bool vote=complete && LaneFormation.TryRoles(samples[..prev.Count],roles[..prev.Count]);
        for(int i=0;i<prev.Count;i++) {
            var t=prev[i];
            if (!towerConflict && t.Hits>=2 && t.Kind!=null)
                t.Confirm(t.Kind==Ranged ? LaneCreepType.Ranged : LaneCreepType.Melee);
            if (!vote || roles[i]==LaneCreepType.Unknown) { t.Vote=LaneCreepType.Unknown; continue; }
            var proposed=roles[i];
            if(t.Role!=LaneCreepType.Unknown && proposed!=t.Role) { t.Confirm(proposed); continue; }
            if(t.Vote!=proposed) { t.Vote=proposed; t.VoteAt=now; }
            else if(now-t.VoteAt>=.30) t.Confirm(proposed);
        }
    }
}
