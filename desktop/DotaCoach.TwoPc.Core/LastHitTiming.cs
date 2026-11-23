namespace DotaCoach.TwoPc;

/// <summary>
/// The health drops of one tracked creep bar as the screen shows them, and the hits they foretell. Every lane creep attacks
/// once a second (npc_units.txt: AttackRate 1; siege creeps every 3 s) and a tower every <see cref="LaneTower.Interval"/>,
/// and an attacker keeps that rhythm until it switches targets or dies. So a drop that follows a similar drop by exactly one
/// such period is an attacker that will hit again one period later. A single drop (a hero, a new attacker) foretells nothing.
/// Drops are kept in bar pixels; health per pixel comes in when hits are foretold, once the unit is known.
/// </summary>
public sealed class CreepHitHistory
{
    /// <summary>A drop of <see cref="Pixels"/> fill pixels, first shown at <see cref="At"/> (seconds).</summary>
    public readonly record struct Drop(double At, int Pixels);
    /// <summary>Lane creeps' and siege creeps' seconds between attacks.</summary>
    public const double CreepPeriod = 1, SiegePeriod = 3;
    /// <summary>How far a repeat may stray from its period (display frames, network jitter, the server's 30 Hz ticks).</summary>
    public const double Tolerance = .07;
    /// <summary>Smaller drops are pixel jitter or the bar's top pixel after regeneration, not a hit.</summary>
    public const double MinHit = 8;
    private const double Keep = SiegePeriod + 2 * Tolerance;
    private readonly List<Drop> drops = [];
    private int fill = -1;
    private double seen = double.NegativeInfinity;

    public IReadOnlyList<Drop> Drops => drops;

    /// <summary>One frame's reading of the bar at <paramref name="at"/> seconds.</summary>
    public void Add(double at, int fill)
    {
        // Gone for longer than a hit's rhythm, or refilled by more than regeneration could (another creep's bar took this
        // track): start over.
        if (at - seen > Keep || this.fill >= 0 && fill > this.fill + 2) { drops.Clear(); this.fill = -1; }
        if (this.fill >= 0 && fill < this.fill) drops.Add(new(at, this.fill - fill));
        this.fill = fill; seen = at;
        int old = 0;
        while (old < drops.Count && at - drops[old].At > Keep) old++;
        drops.RemoveRange(0, old);
    }

    /// <summary>The most one lane creep's hit takes off (ranged creeps 21-26 on an unarmored creep), before pixel error.</summary>
    public const double MaxCreepHit = 28;

    /// <summary>
    /// The health the foretold hits take off from <paramref name="now"/> until <paramref name="until"/>: every rhythm seen in
    /// the drops repeats with its last amount. A rhythm is two similar drops one period apart; it ends when a repeat is overdue.
    /// With <paramref name="singles"/> a lone drop the size of one creep's hit counts as a rhythm already (in the lab an
    /// attacker hit the same creep again 93-97% of the time).
    /// </summary>
    /// <param name="perPixel">Health per bar pixel (maximum health over the bar's span).</param>
    /// <param name="towerInterval">Seconds between the shots of a tower that can shoot this creep, or null.</param>
    /// <param name="beats">How many earlier similar drops, a period apart each, make a rhythm.</param>
    public double Foretold(double now, double until, double perPixel, double? towerInterval = null, bool singles = false, int beats = 1) =>
        Echoes(now, until, perPixel, towerInterval, singles, beats).Sum(e => e.Amount);

    /// <summary>When the first foretold hit comes until <paramref name="until"/> (not before <paramref name="now"/>), or null.</summary>
    public double? Next(double now, double until, double perPixel, double? towerInterval = null, bool singles = false, int beats = 1) =>
        Echoes(now, until, perPixel, towerInterval, singles, beats).Select(e => (double?)Math.Max(now, e.At)).Min();

    /// <summary>Every foretold hit until <paramref name="until"/>: when (a repeat overdue by up to <see cref="Tolerance"/> is still due) and how much.</summary>
    private IEnumerable<(double At, double Amount)> Echoes(double now, double until, double perPixel, double? towerInterval, bool singles, int beats)
    {
        for (int j = 0; j < drops.Count; j++) {
            var drop = drops[j];
            double amount = drop.Pixels * perPixel;
            if (amount < MinHit) continue;
            double? rhythm = null;
            foreach (double period in Periods(amount, towerInterval))
                if (Enumerable.Range(1, beats).All(k => Repeats(j, -k * period, perPixel))) { rhythm = period; break; }
            if (rhythm == null && singles && amount <= MaxCreepHit + 1.5 * perPixel) rhythm = CreepPeriod;
            // Its next repeat came already (that later drop carries the rhythm on) or is overdue (the attacker stopped).
            if (rhythm is not double p || Repeats(j, p, perPixel) || drop.At + p < now - Tolerance) continue;
            for (double at = drop.At + p; at <= until; at += p) yield return (at, amount);
        }
    }

    /// <summary>
    /// One attacker's hits as the drops show them: <see cref="Count"/> similar drops one <see cref="Period"/> apart, the last at
    /// <see cref="Last"/> taking <see cref="Pixels"/>. A lone drop's period is not known yet (0).
    /// </summary>
    public readonly record struct Run(double Last, double Period, int Pixels, int Count);

    /// <summary>
    /// The drops (oldest first) chained into runs: each drop of a hit's size extends the run whose next beat it falls on, the
    /// closest one, or starts a run of its own. <paramref name="runOf"/> gets every drop's run, -1 for drops too small to be a hit.
    /// </summary>
    public static List<Run> Chain(IReadOnlyList<Drop> drops, double perPixel, double? towerInterval, List<int>? runOf = null)
    {
        var runs = new List<Run>();
        runOf?.Clear();
        foreach (var drop in drops) {
            double amount = drop.Pixels * perPixel;
            if (amount < MinHit) { runOf?.Add(-1); continue; }
            int best = -1; double bestOff = Tolerance, bestPeriod = 0;
            for (int r = 0; r < runs.Count; r++) {
                var run = runs[r];
                if (!Similar(run.Pixels, drop.Pixels, perPixel)) continue;
                foreach (double period in run.Count >= 2 ? new[] { run.Period } : Periods(amount, towerInterval)) {
                    double off = Math.Abs(drop.At - run.Last - period);
                    if (off <= bestOff) { best = r; bestOff = off; bestPeriod = period; }
                }
            }
            if (best < 0) { runs.Add(new(drop.At, 0, drop.Pixels, 1)); runOf?.Add(runs.Count - 1); }
            else { runs[best] = new(drop.At, bestPeriod, drop.Pixels, runs[best].Count + 1); runOf?.Add(best); }
        }
        return runs;
    }

    /// <summary>Tower shots take 70-80 off a lane creep, so only big drops can be a tower's; creep hits can be any size (several at once).</summary>
    private static IEnumerable<double> Periods(double amount, double? towerInterval)
    {
        if (towerInterval is double tower && amount >= 50) yield return tower;
        yield return CreepPeriod;
        yield return SiegePeriod;
    }

    /// <summary>Whether another drop of about the same size came <paramref name="offset"/> seconds from drop <paramref name="j"/>.</summary>
    private bool Repeats(int j, double offset, double perPixel)
    {
        var drop = drops[j];
        foreach (var other in drops)
            if (Math.Abs(other.At - drop.At - offset) <= Tolerance && Similar(other.Pixels, drop.Pixels, perPixel)) return true;
        return false;
    }

    /// <summary>One attacker's rolls differ by up to a quarter (creeps 19-23, towers 88-92), and each reading by a pixel.</summary>
    private static bool Similar(int a, int b, double perPixel) => Math.Abs(a - b) * perPixel <= .25 * Math.Max(a, b) * perPixel + 1.5 * perPixel;
}

/// <summary>
/// When the own hit lands, for red numbers that look ahead: the player sees the number turn red, reacts (<see cref="Lead"/>),
/// the order travels to the server and the picture back (<see cref="Latency"/>: ping, the client's interpolation, capture and
/// drawing), then the hero swings and the projectile flies (<see cref="HeroAttack.DelayAt"/>). <see cref="Singles"/>: lone
/// creep-sized drops foretell a repeat too (<see cref="CreepHitHistory.Foretold"/>). <see cref="Risk"/>: the roll the kill is
/// judged by, from the weakest (0) to the strongest (1). <see cref="Alive"/>: no red when the foretold hits kill the creep
/// even from the least health its bar allows, before the own hit can land (it would come too late). <see cref="Beats"/>: earlier
/// drops that make a rhythm. <see cref="Patient"/>: when only foretold hits make the kill and there is time, wait until the
/// first of them shows (<see cref="CreepLabels.Kills"/>).
/// </summary>
public sealed record LastHitTiming(double Lead, double Latency, bool Singles = false, double Risk = 0, bool Alive = false, int Beats = 1, bool Patient = false)
{
    /// <summary>Seconds from this frame until the own hit, ordered in reaction to it, shows on a creep this far from the hero.</summary>
    public double Horizon(HeroAttack attack, double? distance) => Lead + Latency + (distance is double d ? attack.DelayAt(d) : attack.Delay);

    /// <summary>The roll the kill is judged by.</summary>
    public static double Roll((double Least, double Most) hit, LastHitTiming? timing) => hit.Least + (timing?.Risk ?? 0) * (hit.Most - hit.Least);
}
