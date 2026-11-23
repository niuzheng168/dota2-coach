namespace DotaCoach.TwoPc;

/// <summary>A tower from the player's GSI minimap with its tier's stats; it shoots creeps of the other side within <see cref="Reach"/>.</summary>
public readonly record struct LaneTower(int Team, double X, double Y, double DamageMin, double DamageMax, double Interval, double Reach);

/// <summary>
/// The rest of one GSI packet that the creep labels use: game clock, the own hero's attack and where it stands, the standing
/// towers and the flagbearers (their aura makes nearby creeps regenerate faster).
/// </summary>
public sealed class LaneView(double? clock, HeroAttack? attack, IReadOnlyList<LaneTower> towers, IReadOnlyList<MinimapUnit> flagbearers,
    (double X, double Y)? heroAt = null)
{
    /// <summary>Tower and creep hull radii (144 + 16): a tower's range counts from hull edge to hull edge.</summary>
    public const double TowerHulls = 160;
    public static readonly LaneView Empty = new(null, null, [], []);
    public double? Clock { get; } = clock;
    public HeroAttack? Attack { get; } = attack;
    public IReadOnlyList<LaneTower> Towers { get; } = towers;
    public (double X, double Y)? HeroAt { get; } = heroAt;

    /// <param name="hero">The own hero's unit name, to find it on the minimap.</param>
    public static LaneView From(SpectatorSnapshot? snapshot, HeroAttack? attack, HeroAttackTable? table, string? hero = null)
    {
        if (snapshot == null) return new(null, attack, [], []);
        var towers = new List<LaneTower>();
        foreach (var u in snapshot.Minimap)
            if (u.Team is 2 or 3 && table?.Tower(u.Name) is { } t)
                towers.Add(new(u.Team, u.X, u.Y, t.DamageMin, t.DamageMax, t.Interval, t.Range + TowerHulls));
        var own = hero == null ? null : snapshot.Minimap.FirstOrDefault(u => u.Name == hero && u.Team == snapshot.OwnTeam);
        return new(snapshot.Clock, attack, towers,
            snapshot.Minimap.Where(u => u.Name.Contains("_flagbearer", StringComparison.Ordinal)).ToArray(), own == null ? null : (own.X, own.Y));
    }

    /// <summary>The nearest tower of the other side that can shoot this creep, or null.</summary>
    public LaneTower? TowerOver(MinimapUnit creep)
    {
        LaneTower? best = null;
        double nearest = double.MaxValue;
        foreach (var t in Towers) {
            double d = double.Hypot(t.X - creep.X, t.Y - creep.Y);
            if (t.Team != creep.Team && d <= t.Reach && d < nearest) { best = t; nearest = d; }
        }
        return best;
    }

    /// <summary>Health regeneration of a lane creep, with the aura when a flagbearer of its side stands near.</summary>
    public double Regen(MinimapUnit creep) => CreepKinds.Regen(creep.Name) + (flagbearers.Any(f => f.Team == creep.Team &&
        double.Hypot(f.X - creep.X, f.Y - creep.Y) <= CreepKinds.FlagbearerRadius) ? CreepKinds.FlagbearerRegen : 0);
}

/// <summary>
/// How to last-hit (or deny) a creep a tower shoots: <see cref="Preps"/> hits of your own now, then after <see cref="Shots"/>
/// more tower shots one hit kills it.
/// </summary>
public readonly record struct TowerPlan(int Preps, int Shots);

/// <summary>
/// The tower last-hit plan with the fewest prep hits, from the creep's health now. Every hit counts with its mean damage; the
/// health left before the last hit must stay, by two standard deviations of everything still to be rolled (tower and hero damage,
/// the bar's pixel, when exactly the tower fires), above zero and below the weakest last hit. Regeneration only works against the
/// last hit (the flagbearer may die meanwhile). Prep hits come one attack interval apart and the last one an interval later, so
/// the hero's attack speed decides whether they fit before the window closes; the tower is taken to fire right away.
/// Other units hitting the creep are not foreseen: the labels re-plan every frame from the health then.
/// </summary>
public static class TowerPlans
{
    public const int MaxPreps = 3, MaxShots = 10;
    /// <summary>Seconds of slack for the last hit to land before the next tower shot.</summary>
    public const double Leeway = .1;

    /// <param name="hp">Health now, as read from the bar; <paramref name="pixel"/> is one bar pixel's worth of health.</param>
    /// <param name="regen">The creep's health regeneration per second.</param>
    /// <param name="hero">One hit of the own hero on this creep after armor; then its seconds between attacks and from order to hit.</param>
    /// <param name="tower">One tower shot on this creep after armor; then the seconds between shots.</param>
    /// <param name="preppable">Whether the creep may be hit now (allies only below half health).</param>
    /// <returns>Fewest prep hits, then fewest tower shots; null when no plan within <see cref="MaxPreps"/> and <see cref="MaxShots"/>.</returns>
    public static TowerPlan? Fewest(double hp, double pixel, double regen, (double Least, double Most) hero, double heroInterval, double heroDelay,
        (double Least, double Most) tower, double towerInterval, bool preppable)
    {
        double heroMean = (hero.Least + hero.Most) / 2, towerMean = (tower.Least + tower.Most) / 2;
        static double Variance(double width) => width * width / 12; // of a uniform spread
        for (int preps = 0; preps <= (preppable ? MaxPreps : 0); preps++)
            for (int shots = 1; shots <= MaxShots; shots++) {
                double left = hp - preps * heroMean - shots * towerMean;
                double margin = 2 * Math.Sqrt(shots * Variance(tower.Most - tower.Least) + preps * Variance(hero.Most - hero.Least) +
                    Variance(pixel) + Variance(regen * towerInterval));
                if (left - margin < 1) break; // the tower may kill it first; more shots only make it worse
                if (left + regen * shots * towerInterval + margin > hero.Least) continue;
                if (preps > 0 && heroDelay + preps * heroInterval + Leeway >= shots * towerInterval) continue;
                return new(preps, shots);
            }
        return null;
    }
}
