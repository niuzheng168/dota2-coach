namespace DotaCoach.TwoPc;

/// <summary>
/// Lane-creep health bar geometry of the default HUD. Measured on Dota build 6976 at 1080p, 1440p and 4K
/// against engine HP (hero demo, every lane creep kind): the bar scales with the game height, every lane
/// creep (melee, ranged, siege, flagbearer, super, mega) shares it, and the game draws ceil(fraction × span)
/// fill pixels inside a ceil(span)-pixel interior, span = 77.3 px at 1080p.
/// </summary>
public readonly record struct CreepBarGeometry(int Height)
{
    public double Span => 77.3 * Height / 1080;
    public int Inner => (int)Math.Ceiling(Span - 1e-9);
    /// <summary>Interior rows that read as a bar: about 5 at 1080p, 12 at 4K; fewer means a fragment.</summary>
    public int MinRows => Math.Max(3, (int)Math.Round(Height * 4 / 1080d));
    public int MaxRows => (int)Math.Round(Height * 9 / 1080d) + 2;
}

/// <summary>A creep health bar in game client pixels: left rim at X, top rim at Y, <see cref="Rows"/> interior rows.</summary>
public readonly record struct CreepHealthBar(int X, int Y, int Rows, int Fill, int Inner, double Span, bool Ally, bool Covered)
{
    /// <summary>Health fraction: the midpoint of the interval that draws exactly <see cref="Fill"/> pixels.</summary>
    public double Fraction => Fill >= Inner ? 1 : Math.Clamp((Fill - .5) / Span, 0, 1);
    public int Hp(int maximum) => Fill >= Inner ? maximum : Math.Clamp((int)Math.Round(Fraction * maximum), 1, maximum);
    /// <summary>The most health that still draws only <see cref="Fill"/> pixels.</summary>
    public double MostHp(int maximum) => Fill >= Inner ? maximum : Math.Min(maximum, Fill / Span * maximum);
    public int CenterX => X + (Inner + 2) / 2;
}

/// <summary>One interior row of a bar as the GPU scan saw it, right of a dark left rim.</summary>
public readonly record struct CreepBarRow(int X, int Y, int Fill, int Run, bool Ally, bool TopRim, bool Cut, bool FillHidden);

public static class CreepHealthBars
{
    /// <summary>Groups scanned rows into bars: same left rim, consecutive rows, same side, a dark top rim, and
    /// as many rows as a real bar. The fill is the median body row (bevelled bottom rows read short).</summary>
    public static List<CreepHealthBar> FromRows(IEnumerable<CreepBarRow> rows, CreepBarGeometry geometry)
    {
        var sorted = rows.OrderBy(r => r.X).ThenBy(r => r.Ally).ThenBy(r => r.Y).ToList();
        var bars = new List<CreepHealthBar>();
        for (int start = 0; start < sorted.Count;) {
            int end = start + 1;
            while (end < sorted.Count && sorted[end].X == sorted[start].X && sorted[end].Ally == sorted[start].Ally &&
                   sorted[end].Y - sorted[end - 1].Y <= 1) end++;
            Add(sorted.GetRange(start, end - start));
            start = end;
        }
        return bars;

        void Add(List<CreepBarRow> group)
        {
            int rows = group[^1].Y - group[0].Y + 1;
            if (rows < geometry.MinRows || rows > geometry.MaxRows || !group.Take(2).Any(r => r.TopRim)) return;
            // The first row can be the fill's darker top shading; it never decides the reading.
            var body = group.Skip(1).ToList();
            var fills = body.Select(r => r.Fill).Order().ToArray();
            int fill = fills[fills.Length / 2];
            // Agreeing rows only: a bar crossed by another bar's edge is not read from a minority of rows.
            if (body.Count(r => Math.Abs(r.Fill - fill) <= 1) * 2 < body.Count || body.Count(r => r.FillHidden) * 2 >= body.Count) return;
            bars.Add(new(group[0].X, group[0].Y - 1, rows, fill, geometry.Inner, geometry.Span, group[0].Ally,
                body.Count(r => r.Cut) * 2 >= body.Count));
        }
    }
}

public static class CreepKinds
{
    /// <summary>Game-clock seconds per lane creep health upgrade.</summary>
    public const int UpgradeSeconds = 450;

    /// <summary>
    /// Health of lane creeps. Spawn values from npc_units.txt (build 6976): melee and flagbearer 550, ranged 300, siege 935;
    /// super/mega 700 and 475. Melee, flagbearer and ranged creeps then gain 12 per 7.5 minutes of game clock, measured in a
    /// local bot match up to 40 minutes (melee 550, 562 … 610; ranged 300 … 360; siege stays 935). A creep gets its upgrade
    /// about 30 s after spawning, so just-spawned creeps near their base, and for a minute after each step the older
    /// waves, still have 12 less. Super/mega creeps are assumed to grow the same way (not measured).
    /// Null for neutrals (they grow differently), event variants and unknown units.
    /// </summary>
    /// <param name="clock">GSI map clock in seconds; null counts as the start of the game.</param>
    public static int? MaxHp(string unitName, double? clock = null)
    {
        if (unitName.EndsWith("_diretide", StringComparison.Ordinal)) return null;
        if (unitName.StartsWith("npc_dota_goodguys_siege", StringComparison.Ordinal) || unitName.StartsWith("npc_dota_badguys_siege", StringComparison.Ordinal)) return 935;
        if (!unitName.StartsWith("npc_dota_creep_goodguys_", StringComparison.Ordinal) && !unitName.StartsWith("npc_dota_creep_badguys_", StringComparison.Ordinal)) return null;
        bool upgraded = unitName.Contains("_upgraded", StringComparison.Ordinal);
        int growth = 12 * (int)Math.Max(0, Math.Floor((clock ?? 0) / UpgradeSeconds));
        if (unitName.Contains("_ranged", StringComparison.Ordinal)) return (upgraded ? 475 : 300) + growth;
        if (unitName.Contains("_melee", StringComparison.Ordinal) || unitName.Contains("_flagbearer", StringComparison.Ordinal)) return (upgraded ? 700 : 550) + growth;
        return null;
    }

    public static bool IsSiege(string unitName) => unitName.StartsWith("npc_dota_goodguys_siege", StringComparison.Ordinal) ||
        unitName.StartsWith("npc_dota_badguys_siege", StringComparison.Ordinal);

    /// <summary>
    /// Armor of a lane creep (npc_units.txt, build 6989): 2 for melee and flagbearers, 0 for ranged, one more for super/mega,
    /// 0 for siege. Null wherever <see cref="MaxHp"/> is null.
    /// </summary>
    public static double? Armor(string unitName)
    {
        if (MaxHp(unitName) == null) return null;
        if (IsSiege(unitName)) return 0;
        bool upgraded = unitName.Contains("_upgraded", StringComparison.Ordinal);
        return unitName.Contains("_ranged", StringComparison.Ordinal) ? upgraded ? 1 : 0 : upgraded ? 3 : 2;
    }

    /// <summary>Share of a hero's attack damage a lane creep takes: its armor, and siege creeps take half damage from heroes (creep_siege).</summary>
    public static double? HeroHit(string unitName) =>
        Armor(unitName) is double armor ? HeroAttack.ArmorMultiplier(armor) * (IsSiege(unitName) ? .5 : 1) : null;

    /// <summary>Health regeneration per second (npc_units.txt, build 6989): 0.5 melee and flagbearer, 2 ranged, 0 siege.</summary>
    public static double Regen(string unitName) => IsSiege(unitName) ? 0 : unitName.Contains("_ranged", StringComparison.Ordinal) ? 2 : .5;

    /// <summary>The flagbearer's aura (flagbearer_creep_aura_effect): this much more regeneration for its side's creeps within <see cref="FlagbearerRadius"/>.</summary>
    public const double FlagbearerRegen = 3, FlagbearerRadius = 700;
}

/// <summary>
/// What the creep HP display writes over one frame's bars. Only creeps below half health get a number (where last hits come,
/// and allies can be denied): their health, or a percentage without a known unit. It is red when one attack of the own hero
/// surely kills: the most health the bar's pixels allow, plus what the creep regenerates until the hit lands, against the
/// weakest roll after the creep's armor. A creep a tower can shoot that needs prep hits before the tower brings it into
/// last-hit range (<see cref="TowerPlans"/>) also gets their count, in amber right of its bar, whatever its health; then
/// the tower's shots bring it down until the number turns red.
/// </summary>
public static class CreepLabels
{
    public const uint Ally = 0xA8FF8C, Enemy = 0xFFFFFF, Lethal = 0xFF3B30, Prep = 0xFFC83C;

    /// <param name="names">Per bar, the unit it is tracked as (null when unknown).</param>
    /// <param name="units">Per bar, the GSI unit matched in this frame (its position), or null.</param>
    /// <returns>How many bars belong to a lane creep of known maximum health.</returns>
    public static int Build(IReadOnlyList<CreepHealthBar> bars, IReadOnlyList<string?> names, IReadOnlyList<MinimapUnit?> units, LaneView lane,
        List<OverlayLabel> labels)
    {
        labels.Clear();
        int known = 0;
        for (int i = 0; i < bars.Count; i++) {
            var bar = bars[i];
            string? name = names[i];
            int? maximum = name != null ? CreepKinds.MaxHp(name, lane.Clock) : null;
            if (maximum != null) known++;
            bool half = bar.Fraction < .5;
            var unit = units[i] is { } u && u.Name == name ? u : null;
            TowerPlan? plan = null;
            bool lethal = false;
            if (maximum is int max && lane.Attack is { } attack && attack.Against(name!) is { } hit) {
                // Without a position the flagbearer may be near.
                double regen = unit != null ? lane.Regen(unit) : CreepKinds.Regen(name!) + CreepKinds.FlagbearerRegen;
                lethal = bar.MostHp(max) + regen * attack.Delay <= hit.Least;
                if (!lethal && unit != null && lane.TowerOver(unit) is { } tower && !CreepKinds.IsSiege(name!) &&
                    CreepKinds.Armor(name!) is double armor) {
                    double share = HeroAttack.ArmorMultiplier(armor);
                    plan = TowerPlans.Fewest(bar.Fraction * max, max / bar.Span, regen, hit, attack.Interval, attack.Delay,
                        (tower.DamageMin * share, tower.DamageMax * share), tower.Interval, preppable: !bar.Ally || half);
                }
            }
            if (half)
                labels.Add(new(bar.CenterX, bar.Y - 1, maximum is int m ? bar.Hp(m).ToString() : $"{Math.Round(bar.Fraction * 100)}%",
                    lethal ? Lethal : bar.Ally ? Ally : Enemy));
            // Glyphs are about a sixth of the bar's span high: just right of the bar, centred on it.
            if (plan is { Preps: > 0 } p)
                labels.Add(new(bar.X + bar.Inner + 2 + (int)Math.Round(bar.Span * .1), bar.Y + (bar.Rows + 2) / 2 + (int)Math.Round(bar.Span * .1),
                    p.Preps.ToString(), Prep));
        }
        return known;
    }
}
