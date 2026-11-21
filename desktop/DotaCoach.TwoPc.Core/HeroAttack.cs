using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>An item in a slot whose stats count (main inventory and neutral slots), with GSI's item level when it gives one.</summary>
public readonly record struct LoadoutItem(string Name, int? Level);

/// <summary>
/// The player's own hero as player-mode GSI describes it, as far as its attack damage goes. Read on the game machine for the
/// creep HP display; never sent to the coach.
/// </summary>
public sealed record HeroLoadout(string Hero, int Level, bool Broken, int AttributeLevels, bool[] Talents, LoadoutItem[] Items)
{
    private static readonly Regex Name = new("^[a-z0-9_]{1,80}$", RegexOptions.CultureInvariant);
    // Backpack, stash and the TP slot give no stats.
    private static readonly string[] Slots = ["slot0", "slot1", "slot2", "slot3", "slot4", "slot5", "neutral0", "neutral1"];

    /// <returns>Null without a flat player-mode hero block (menus, spectating, malformed packets).</returns>
    public static HeroLoadout? Parse(JsonElement root)
    {
        if (!root.TryGetProperty("hero", out var h) || h.ValueKind != JsonValueKind.Object || !h.TryGetProperty("name", out var n) ||
            n.ValueKind != JsonValueKind.String || n.GetString() is not { } hero || !hero.StartsWith("npc_dota_hero_", StringComparison.Ordinal) ||
            !Name.IsMatch(hero) || Int(h, "level") is not int level || level is < 1 or > 30) return null;
        bool Flag(string key) => h.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.True;
        var items = new List<LoadoutItem>();
        if (root.TryGetProperty("items", out var slots) && slots.ValueKind == JsonValueKind.Object)
            foreach (var slot in Slots)
                if (slots.TryGetProperty(slot, out var item) && item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var i) &&
                    i.ValueKind == JsonValueKind.String && i.GetString() is { } name && name != "empty" && Name.IsMatch(name))
                    items.Add(new(name, Int(item, "item_level") is >= 1 and <= 10 and int l ? l : null));
        return new(hero, level, Flag("break"), Int(h, "attributes_level") is >= 0 and <= 7 and int a ? a : 0,
            Enumerable.Range(1, 8).Select(t => Flag($"talent_{t}")).ToArray(), items.ToArray());
    }

    private static int? Int(JsonElement e, string key) =>
        e.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out int n) ? n : null;
}

/// <summary>
/// One plain attack of the own hero before armor: the least and most of the damage roll (the HUD shows their mean, split into
/// white base and green bonus) and Quelling Blade's extra against creeps. A lower bound, see <see cref="HeroAttackTable"/>.
/// <see cref="Interval"/> is the time between attacks and <see cref="Delay"/> from the order to the hit (attack point, plus
/// the projectile's flight over the hero's full range); both at least as long as in the game.
/// </summary>
public readonly record struct HeroAttack(int Min, int Max, int Quell, double Interval, double Delay)
{
    /// <summary>Share of physical damage that gets through armor.</summary>
    public static double ArmorMultiplier(double armor) => 1 - .06 * armor / (1 + .06 * Math.Abs(armor));

    /// <summary>The least and most one attack takes off a lane creep, or null for other units.</summary>
    public (double Least, double Most)? Against(string unitName) =>
        CreepKinds.HeroHit(unitName) is double share ? ((Min + Quell) * share, (Max + Quell) * share) : null;
}

/// <summary>
/// The own hero's attack damage from GSI and data/dota2-hero-attack.json (the installed client, scripts/client/build-hero-attack.ts):
/// base damage, attributes at the hero's level plus the attribute bonus, generic damage/attribute talents, Luna's Lunar Blessing,
/// and the stats items list as plain bonuses. Everything else is left out on purpose, so the result never exceeds the real damage:
/// other heroes' passives and auras, buffs from allies and runes, percentage base damage, crits and procs, and the attribute
/// Power Treads or Vambrace is switched to (counted where it adds least). Heroes whose plain attack does less are not supported.
/// </summary>
public sealed class HeroAttackTable
{
    private sealed record Hero(int Primary, string Attack, int DamageMin, int DamageMax, double[] Attributes, double[] Gains,
        double AttackRate, double AttackSpeed, double AttackPoint, double ProjectileSpeed, double Range,
        Dictionary<string, double>[] Talents, double PerLevel, int LevelTalent, double LevelTalentBonus, string? Unsupported);
    private readonly Dictionary<string, Hero> heroes = [];
    private readonly Dictionary<string, Dictionary<string, double[]>> items = [];
    private readonly Dictionary<int, TowerStats> towers = [];
    private static readonly Regex TowerName = new(@"^npc_dota_(?:good|bad)guys_tower([1-4])(?:_(?:top|mid|bot))?$", RegexOptions.CultureInvariant);
    private readonly double universal;
    private readonly double[] attributeBonus;
    public int Build { get; }

    public HeroAttackTable(string path)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(path));
        var root = doc.RootElement;
        static double[] Numbers(JsonElement e) => e.EnumerateArray().Select(v => v.GetDouble()).ToArray();
        Build = root.GetProperty("sources").GetProperty("client").GetProperty("build").GetInt32();
        universal = root.GetProperty("universalDamagePerAttribute").GetDouble();
        attributeBonus = Numbers(root.GetProperty("attributeBonus"));
        foreach (var h in root.GetProperty("heroes").EnumerateObject()) {
            var v = h.Value;
            var damage = Numbers(v.GetProperty("damage"));
            var talents = v.GetProperty("talents").EnumerateArray()
                .Select(t => t.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.GetDouble())).ToArray();
            var level = v.TryGetProperty("levelDamage", out var l) ? l : (JsonElement?)null;
            heroes[h.Name] = new(Array.IndexOf<string?>(["str", "agi", "int", "all"], v.GetProperty("primary").GetString()), v.GetProperty("attack").GetString()!,
                (int)damage[0], (int)damage[1], Numbers(v.GetProperty("attributes")), Numbers(v.GetProperty("gains")),
                v.GetProperty("attackRate").GetDouble(), v.GetProperty("attackSpeed").GetDouble(), v.GetProperty("attackPoint").GetDouble(),
                v.TryGetProperty("projectileSpeed", out var p) ? p.GetDouble() : 0, v.TryGetProperty("range", out var r) ? r.GetDouble() : 0, talents,
                level?.GetProperty("perLevel").GetDouble() ?? 0, level?.GetProperty("talent").GetInt32() ?? 0, level?.GetProperty("talentBonus").GetDouble() ?? 0,
                v.TryGetProperty("unsupported", out var u) ? u.GetString() : null);
            if (heroes[h.Name] is { Primary: < 0 } or { Attributes.Length: not 3 } or { Gains.Length: not 3 } or { Talents.Length: not 8 } or
                { AttackRate: <= 0 } or { AttackSpeed: <= 0 } || heroes[h.Name].Attack is not ("melee" or "ranged" or "both"))
                throw new ArgumentException($"Bad hero entry {h.Name}");
        }
        foreach (var i in root.GetProperty("items").EnumerateObject())
            items[i.Name] = i.Value.EnumerateObject().ToDictionary(p => p.Name, p => Numbers(p.Value));
        foreach (var t in root.GetProperty("towers").EnumerateObject()) {
            var v = t.Value;
            var damage = Numbers(v.GetProperty("damage"));
            towers[int.Parse(t.Name)] = new(damage[0], damage[1], v.GetProperty("attackRate").GetDouble() * 100 / v.GetProperty("attackSpeed").GetDouble(),
                v.GetProperty("range").GetDouble());
        }
        if (heroes.Count < 100 || attributeBonus.Length == 0 || universal is <= 0 or >= 1 || towers.Count != 4)
            throw new ArgumentException("Incomplete hero attack data");
    }

    /// <summary>Stats of a tower by its unit name (npc_dota_goodguys_tower1_top ...), or null for other units.</summary>
    public TowerStats? Tower(string unitName) =>
        TowerName.Match(unitName) is { Success: true } m && towers.TryGetValue(m.Groups[1].Value[0] - '0', out var t) ? t : null;

    /// <summary>The table next to the program, or null when it is missing or unreadable.</summary>
    public static HeroAttackTable? Bundled()
    {
        try { return new HeroAttackTable(Path.Combine(AppContext.BaseDirectory, "data", "dota2-hero-attack.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
                                   KeyNotFoundException or InvalidOperationException or FormatException or IndexOutOfRangeException) { return null; }
    }

    /// <returns>Null with a reason for heroes the table does not know or cannot estimate.</returns>
    public HeroAttack? Attack(HeroLoadout loadout, out string? unsupported)
    {
        unsupported = null;
        if (!heroes.TryGetValue(loadout.Hero, out var hero)) { unsupported = $"数据里没有 {loadout.Hero}（游戏版本 {Build}）"; return null; }
        if (hero.Unsupported != null) { unsupported = hero.Unsupported; return null; }
        var attributes = new double[3];
        for (int a = 0; a < 3; a++)
            attributes[a] = hero.Attributes[a] + hero.Gains[a] * (loadout.Level - 1) +
                (loadout.AttributeLevels > 0 ? attributeBonus[Math.Min(loadout.AttributeLevels, attributeBonus.Length) - 1] : 0);
        double flat = 0, primary = 0, universalAll = 0, selected = 0, speed = hero.AttackSpeed;
        int quell = 0;
        var percent = new double[3];
        void Add(Func<string, double> value)
        {
            flat += value("damage"); speed += value("attackSpeed");
            attributes[0] += value("str") + value("all"); attributes[1] += value("agi") + value("all"); attributes[2] += value("int") + value("all");
            primary += value("primary"); universalAll += value("universal"); selected += value("selected");
            percent[0] += value("strPct"); percent[1] += value("agiPct"); percent[2] += value("intPct");
        }
        for (int t = 0; t < 8; t++)
            if (loadout.Talents[t]) Add(hero.Talents[t].GetValueOrDefault);
        foreach (var item in loadout.Items) {
            if (!items.TryGetValue(item.Name, out var bonus)) continue;
            // By item level; without one the weakest level (for penalties the strongest), which stays a lower bound.
            int level = Math.Max(item.Level ?? 0, bonus.TryGetValue("baseLevel", out var b) ? (int)b[0] : 0);
            double Value(string kind) => !bonus.TryGetValue(kind, out var v) ? 0 : level >= 1 ? v[Math.Min(level, v.Length) - 1] : v.Min();
            Add(Value);
            if (bonus.TryGetValue("quell", out var q)) // Quelling Blade and Battle Fury do not stack
                quell = Math.Max(quell, (int)(hero.Attack == "melee" ? q[0] : hero.Attack == "ranged" ? q[1] : Math.Min(q[0], q[1])));
        }
        // Game attributes are fractional; flooring every part keeps this at or below the engine's sum.
        var whole = new double[3];
        for (int a = 0; a < 3; a++) whole[a] = Math.Floor(attributes[a] * (1 + Math.Min(0, percent[a]) / 100));
        double main = hero.Primary == 3 ? Math.Floor(universal * (whole.Sum() + 3 * universalAll + selected)) : whole[hero.Primary] + primary;
        if (hero.PerLevel > 0 && !loadout.Broken)
            flat += hero.PerLevel * loadout.Level + (hero.LevelTalent >= 1 && loadout.Talents[hero.LevelTalent - 1] ? hero.LevelTalentBonus : 0);
        int least = (int)Math.Floor(hero.DamageMin + main + flat), most = (int)Math.Floor(hero.DamageMax + main + flat);
        // One attack speed per point of agility; buffs left out make the attack only slower than in the game.
        double rate = Math.Clamp(speed + whole[1], 20, 700) / 100;
        double flight = hero.ProjectileSpeed > 0 ? (hero.Range + RangeHulls) / hero.ProjectileSpeed : 0;
        return least < 1 ? null : new HeroAttack(least, Math.Max(least, most), quell, hero.AttackRate / rate, hero.AttackPoint / rate + flight);
    }

    /// <summary>Hero and creep hull radii (24 + 16): attack range counts from hull edge to hull edge.</summary>
    private const double RangeHulls = 40;
}

/// <summary>One tier of towers: damage roll, seconds between shots and attack range.</summary>
public readonly record struct TowerStats(double DamageMin, double DamageMax, double Interval, double Range);
