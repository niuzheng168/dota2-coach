namespace DotaCoach.TwoPc;

public sealed record DrillMark(CreepBar Bar, HitPlan Plan);

/// <summary>First-wave, level-one naked Luna against regular creeps at a single T1. Units are
/// matched only between consecutive pictures; evidence is thrown away on crossings and cuts.</summary>
public sealed class LaneDrill
{
    private sealed record Kind(int Hp, double Regen, HitRange Hero, HitRange Tower);
    // Damage ranges include the normal roll and armor. Fixed training profile, not a live stat feed.
    private static readonly Kind Melee = new(550, .5, new(44, 53), new(78, 83));
    private static readonly Kind Ranged = new(300, 2, new(50, 59), new(88, 92));
    private const double Period = .82, HeroPeriod = 1.37;
    private readonly double flight; // turn-free attack point plus projectile travel, hero already in range
    public LaneDrill(double heroImpact = .58)
    {
        if (!double.IsFinite(heroImpact) || heroImpact < .28 || heroImpact > .70) throw new ArgumentOutOfRangeException(nameof(heroImpact));
        flight = heroImpact;
    }
    // For rehearsing the opening hit before there is enough on-screen evidence of tower aggro.
    // Explicit inputs, not a way to silently declare that an arbitrary on-screen creep is targeted.
    public HitPlan? Rehearse(bool ranged, int hp, double firstTower)
    {
        var k = ranged ? Ranged : Melee;
        if (hp < 1 || hp > k.Hp || firstTower <= 0 || firstTower > 4) return null;
        return TowerLastHit.Plan(new(hp, hp), k.Hero, flight, HeroPeriod, 0, k.Regen, k.Tower, firstTower, Period);
    }
    private sealed class Track(CreepBar bar)
    {
        public CreepBar Bar = bar;
        public int Frames, Hits, StableFill;
        public double TowerAt = double.NegativeInfinity, UnclearAt = double.NegativeInfinity;
        public double PeriodEstimate = Period;
        public Kind? Kind;
        public void Forget() { Kind = null; Hits = 0; TowerAt = double.NegativeInfinity; PeriodEstimate = Period; }
    }
    private List<Track> prev = [];
    private double last = double.NegativeInfinity, ownImpact = double.NegativeInfinity;
    public List<DrillMark> Update(IReadOnlyList<CreepBar> bars, double now)
    {
        if (now <= last || now - last > .22) { prev.Clear(); ownImpact = double.NegativeInfinity; }
        last = now;
        var current = new List<Track>();
        var old = prev.Select(t => (Track: t, Bar: t.Bar)).ToArray();
        static bool Near(CreepBar a, CreepBar b) => a.Ally == b.Ally && Math.Abs(a.Width - b.Width) <= 2
            && Math.Abs(a.X - b.X) <= a.Width * .40 && Math.Abs(a.Y - b.Y) <= a.Width * .30;
        foreach (var b in bars) {
            var matches = old.Where(t => Near(t.Bar, b)).Take(2).ToArray();
            if (matches.Length != 1 || bars.Count(c => Near(matches[0].Bar, c)) != 1) {
                current.Add(new(b)); continue;
            }
            var t = matches[0].Track;
            int delta = t.Bar.Fill - b.Fill;
            t.StableFill = delta == 0 && t.Bar.Width == b.Width ? t.StableFill + 1 : 0;
            // A replacement creep or a discontinuity is not continuous evidence.
            if (delta < -2) { current.Add(new(b)); continue; }
            if (delta > 2) {
                double fraction = (double)delta / (b.Width - 2), error = 1.6 / (b.Width - 2);
                bool Fits(Kind k, HitRange d) => fraction >= d.Min/k.Hp - error && fraction <= d.Max/k.Hp + error;
                Kind? tower = Fits(Melee, Melee.Tower) ? Melee : Fits(Ranged, Ranged.Tower) ? Ranged : null;
                double gap = now - t.TowerAt;
                if (t.Hits >= 2 && t.Kind is { } known && Fits(known, known.Hero)
                    && Math.Abs(gap - t.PeriodEstimate) > TowerLastHit.TimingMargin) {
                    ownImpact = now; // In the isolated drill the only other attacker is the player.
                } else if (tower != null && !b.Ally) {
                    if (tower == t.Kind && Math.Abs(gap - Period) <= .15) {
                        t.Hits++; t.PeriodEstimate = (Period + gap) / 2;
                    } else { t.Forget(); t.Kind = tower; t.Hits = 1; }
                    t.TowerAt = now;
                } else { t.Forget(); t.UnclearAt = now; }
            }
            if (now - t.TowerAt > t.PeriodEstimate + .14) { if (t.Hits > 0) t.UnclearAt = now; t.Forget(); }
            t.Bar = b; t.Frames++; current.Add(t);
        }
        prev = current;
        // Two creeps taking tower-sized hits at once is not a clean single-target drill.
        bool conflict = current.Count(t => t.Hits > 0) > 1;
        var marks = new List<DrillMark>();
        foreach (var t in current) {
            var b = t.Bar;
            if (t.Frames < 2 || now - t.UnclearAt < flight) continue;
            double ready = Math.Max(0, ownImpact + HeroPeriod - flight - now);
            HitPlan? plan = null;
            if (t.Hits > 0) {
                if (conflict || t.Hits < 2 || t.Kind == null) continue;
                double next = t.TowerAt + t.PeriodEstimate - now;
                // A stable fill still quantizes HP to a pixel. Allow extra error at a changing edge.
                double pixelError = b.Width == 80 && t.StableFill > 0 ? 1 : 1.5;
                plan = TowerLastHit.Plan(b.Health(t.Kind.Hp, pixelError), t.Kind.Hero, flight, HeroPeriod, ready, t.Kind.Regen,
                    t.Kind.Tower, next, t.PeriodEstimate);
            } else if (ready == 0) {
                // Without a positive type identification, only light if BOTH ordinary creep types qualify.
                if (b.Health(Melee.Hp).Max + Melee.Regen * flight <= Melee.Hero.Min &&
                    b.Health(Ranged.Hp).Max + Ranged.Regen * flight <= Ranged.Hero.Min &&
                    (!b.Ally || b.Health(Melee.Hp).Max < Melee.Hp * .5))
                    plan = new(b.Ally ? HitAction.Deny : HitAction.Finish, 0, 0, 0);
            }
            if (plan != null) marks.Add(new(b, plan));
        }
        return marks;
    }
}
