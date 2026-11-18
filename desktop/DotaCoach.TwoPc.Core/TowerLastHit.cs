namespace DotaCoach.TwoPc;

/// <summary>Damage after armor, in HP (not the attack number shown on the hero portrait).</summary>
public readonly record struct HitRange(double Min, double Max)
{
    public bool Valid => double.IsFinite(Min) && double.IsFinite(Max) && Min > 0 && Max >= Min;
}

public enum HitAction { Finish, Deny, Prepare, Wait }
public sealed record HitPlan(HitAction Action, int PrepHits, int TowerHits, double ClickIn);

/// <summary>Small event-time search for one isolated creep in a tower drill. All times are seconds from
/// the observation, and tower times are IMPACTS, not launches. An overlapping damage roll is not a kill.</summary>
public static class TowerLastHit
{
    public const double TimingMargin = .10;

    public static HitPlan? Plan(HitRange hp, HitRange hero, double heroImpact, double attackInterval,
        double readyIn, double regen, HitRange tower, double nextTower, double towerInterval)
    {
        if (!hp.Valid || !hero.Valid || !tower.Valid || !double.IsFinite(heroImpact) || heroImpact < 0 ||
            !double.IsFinite(attackInterval) || attackInterval <= 0 || !double.IsFinite(readyIn) || readyIn < 0 ||
            !double.IsFinite(regen) || regen < 0 || !double.IsFinite(nextTower) || nextTower < 0 ||
            !double.IsFinite(towerInterval) || towerInterval <= 2 * TimingMargin) return null;

        // Prefer leaving the creep alone if tower shots alone create a safe window. For equal effort
        // take the first safe window. Include the following tower shot even if it would kill the creep.
        for (int prep = 0; prep <= 4; prep++)
        for (int shots = 0; shots <= 6; shots++) {
            if (shots == 0 && prep != 0) continue;
            double lastTower = nextTower + (shots - 1) * towerInterval;
            double next = nextTower + shots * towerInterval;
            double first = readyIn + heroImpact;
            if (prep > 0 && first + (prep - 1) * attackInterval >= lastTower - TimingMargin) continue;
            double finish = Math.Max(first + prep * attackInterval, shots == 0 ? 0 : lastTower + TimingMargin);
            if (finish >= next - TimingMargin) continue;
            double least = hp.Min - prep * hero.Max - shots * tower.Max;
            double most = hp.Max + regen * finish - prep * hero.Min - shots * tower.Min;
            if (least <= 0 || most > hero.Min) continue;
            double click = prep > 0 ? readyIn : Math.Max(0, finish - heroImpact);
            return new(click > .07 ? HitAction.Wait : prep > 0 ? HitAction.Prepare : HitAction.Finish, prep, shots, click);
        }
        return null;
    }
}
