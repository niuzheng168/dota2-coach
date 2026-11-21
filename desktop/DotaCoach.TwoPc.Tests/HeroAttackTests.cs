using System.Text.Json;
using DotaCoach.TwoPc;

/// <summary>The own hero's attack from GSI (data/dota2-hero-attack.json, build 6989) and the red "one hit kills" rule.</summary>
internal static class HeroAttackTests
{
    public static void Run(Action<bool, string> check)
    {
        var table = new HeroAttackTable("data/dota2-hero-attack.json");
        bool[] none = new bool[8];
        HeroAttack? Hit(string hero, int level, params string[] items) => table.Attack(new(hero, level, false, 0, none,
            items.Select(i => new LoadoutItem(i, 1)).ToArray()), out _);

        // The 4K HUD of a level-1 Luna reads "53 +2": 26-32 base + 24 agility, and Lunar Blessing's 2 per level.
        check(Hit("npc_dota_hero_luna", 1) is { Min: 52, Max: 58, Quell: 0 } level1 && (level1.Min + level1.Max) / 2 == 53 + 2,
            "level-1 Luna's attack matches her HUD");
        // Level 6: 24 + 5 x 3.2 = 40 agility, Wraith Band 5 and two branches 2; Power Treads may be on any attribute, so they add nothing.
        check(Hit("npc_dota_hero_luna", 6, "item_wraith_band", "item_branches", "item_branches", "item_power_treads") is { Min: 26 + 47 + 12, Max: 32 + 47 + 12 },
            "attributes grow with level and items; switchable attributes count where they add least");
        check(table.Attack(new("npc_dota_hero_luna", 1, true, 0, none, []), out _) is { Min: 50 } &&
              table.Attack(new("npc_dota_hero_luna", 25, false, 0, [false, false, false, false, false, false, true, false], []), out _) is { } talent &&
              talent.Min == Hit("npc_dota_hero_luna", 25)!.Value.Min + 50,
            "Lunar Blessing stops under break and gains its level-25 talent");
        // Universal: 0.45 per point of all three attributes; Power Treads count wherever they are.
        check(Hit("npc_dota_hero_abaddon", 1) is { Min: 22 + 27, Max: 32 + 27 } && Hit("npc_dota_hero_abaddon", 1, "item_power_treads") is { Min: 22 + 32 },
            "universal heroes get 0.45 damage per attribute point");
        check(table.Attack(new("npc_dota_hero_antimage", 1, false, 2, none, []), out _)!.Value.Min == Hit("npc_dota_hero_antimage", 1)!.Value.Min + 4,
            "two attribute bonus levels add 4 to every attribute");
        check(table.Attack(new("npc_dota_hero_bounty_hunter", 10, false, 0, [false, true, false, false, false, false, false, false], []), out _)!.Value.Min ==
              Hit("npc_dota_hero_bounty_hunter", 10)!.Value.Min + 30, "a generic damage talent counts once taken");
        check(Hit("npc_dota_hero_luna", 1, "item_quelling_blade") is { Quell: 4 } && Hit("npc_dota_hero_axe", 1, "item_quelling_blade", "item_bfury") is { Quell: 10 } &&
              Hit("npc_dota_hero_troll_warlord", 1, "item_quelling_blade") is { Quell: 4 }, "Quelling Blade by attack type, not stacked with Battle Fury");
        check(Hit("npc_dota_hero_luna", 1, "item_rapier")!.Value.Min == 52 + 100 && Hit("npc_dota_hero_luna", 1, "item_enchanted_quiver")!.Value.Min == 52,
            "only always-on stats count, not toggles or next-attack procs");
        check(Hit("npc_dota_hero_luna", 1, "item_philosophers_stone") is { Min: 52 - 30 } &&
              table.Attack(new("npc_dota_hero_luna", 1, false, 0, none, [new("item_philosophers_stone", null)]), out _) == null,
            "a damage penalty without a known item level counts at its strongest (-60), leaving no attack to judge by");
        check(Hit("npc_dota_hero_luna", 1, "item_dagon_3")!.Value.Min == 52 + 8, "a level-3 Dagon counts at level 3 even if GSI says 1");
        check(Hit("npc_dota_hero_jakiro", 1) == null && table.Attack(new("npc_dota_hero_jakiro", 1, false, 0, none, []), out var why) == null && why!.Contains("杰奇洛") &&
              table.Attack(new("npc_dota_hero_new_one", 1, false, 0, none, []), out var unknown) == null && unknown!.Contains("6989"),
            "unsupported and unknown heroes give no attack, with a reason");

        check(Math.Abs(CreepKinds.HeroHit("npc_dota_creep_badguys_melee")!.Value - 1 / 1.12) < 1e-9 && CreepKinds.HeroHit("npc_dota_creep_goodguys_ranged") == 1 &&
              CreepKinds.HeroHit("npc_dota_badguys_siege") == .5 && Math.Abs(CreepKinds.HeroHit("npc_dota_creep_goodguys_melee_upgraded_mega")!.Value - 1 / 1.18) < 1e-9 &&
              Math.Abs(CreepKinds.HeroHit("npc_dota_creep_badguys_ranged_upgraded")!.Value - 1 / 1.06) < 1e-9 &&
              CreepKinds.HeroHit("npc_dota_creep_goodguys_flagbearer") == CreepKinds.HeroHit("npc_dota_creep_goodguys_melee") &&
              CreepKinds.HeroHit("npc_dota_neutral_kobold") == null && CreepKinds.HeroHit("npc_dota_hero_axe") == null,
            "lane creep armor and the siege creep's half damage from heroes");
        // Level-1 Luna on a melee creep: 52 x 0.893 = 46.4. A 4K bar 12 pixels full shows 41 and holds at most 42.7 HP; 13 pixels
        // hold up to 46.2, plus 0.35 regenerated in the 0.69 s before her hit lands.
        var luna = Hit("npc_dota_hero_luna", 1)!.Value;
        check(Math.Abs(luna.Interval - 1.7 / 1.24) < 1e-9 && Math.Abs(luna.Delay - (.35 / 1.24 + 370 / 900d)) < 1e-9 &&
              Hit("npc_dota_hero_luna", 1, "item_gloves")!.Value.Interval < luna.Interval && Hit("npc_dota_hero_axe", 1)!.Value.Delay < .5,
            "attack interval and delay from attack speed, agility and items; melee heroes have no projectile");
        var g4k = new CreepBarGeometry(2160);
        CreepHealthBar Bar(int fill) => new(0, 0, 12, fill, g4k.Inner, g4k.Span, false, false);
        double least = luna.Against("npc_dota_creep_badguys_melee")!.Value.Least;
        check(Math.Abs(least - 52 / 1.12) < 1e-9 && Bar(12).Hp(550) == 41 && Bar(12).MostHp(550) <= least && Bar(13).MostHp(550) <= least &&
              Bar(13).MostHp(550) + .5 * luna.Delay > least, "red only when the most health the pixels allow, regenerating, dies to the weakest roll");
        check(Bar(155).MostHp(550) == 550 && Bar(80).MostHp(550) > Bar(80).Hp(550), "a bar's most health is the top of its pixel");
        var labels = new List<OverlayLabel>();
        var frame = new[] { Bar(100), Bar(60) with { Ally = true }, Bar(12), Bar(12) with { Ally = true }, Bar(13), Bar(40), Bar(30) };
        string?[] names = ["npc_dota_creep_badguys_melee", "npc_dota_creep_goodguys_melee", "npc_dota_creep_badguys_melee",
            "npc_dota_creep_goodguys_melee", "npc_dota_creep_badguys_melee", null, "npc_dota_neutral_kobold"];
        // Positions far from any flagbearer and tower; without a position the flagbearer's aura is assumed.
        MinimapUnit?[] at = names.Select((n, i) => n == null ? null : new MinimapUnit(n, n.Contains("goodguys") ? 2 : 3, i * 50, 0, null, null, null)).ToArray();
        int known = CreepLabels.Build(frame, names, at, new LaneView(0, luna, [], []), labels);
        check(known == 5 && labels.Select(l => (l.Text, l.Rgb)).SequenceEqual([("212", CreepLabels.Ally), ("41", CreepLabels.Lethal), ("41", CreepLabels.Lethal),
                ("44", CreepLabels.Enemy), ("26%", CreepLabels.Enemy), ("19%", CreepLabels.Enemy)]),
            "only creeps below half health are labelled; red for a sure last hit or deny, never for unknown units or neutrals");
        CreepLabels.Build(frame, frame.Select(_ => (string?)"npc_dota_creep_badguys_melee").ToArray(), at, LaneView.Empty, labels);
        check(!labels.Any(l => l.Rgb == CreepLabels.Lethal), "without the hero's attack nothing is red");

        TowerTests(check, table, luna, Bar);

        var gsi = JsonSerializer.SerializeToElement(new {
            hero = new { name = "npc_dota_hero_luna", level = 7, alive = true, @break = false, attributes_level = 1, talent_1 = true, talent_2 = false },
            items = new {
                slot0 = new { name = "item_wraith_band", item_level = 1 }, slot1 = new { name = "empty" }, slot6 = new { name = "item_branches", item_level = 1 },
                stash0 = new { name = "item_branches" }, teleport0 = new { name = "item_tpscroll" }, neutral0 = new { name = "item_enhancement_fierce", item_level = 1 } },
        });
        check(HeroLoadout.Parse(gsi) is { Hero: "npc_dota_hero_luna", Level: 7, Broken: false, AttributeLevels: 1, Talents: [true, false, ..], Items.Length: 2 } l &&
              l.Items.Select(i => i.Name).SequenceEqual(["item_wraith_band", "item_enhancement_fierce"]),
            "GSI loadout: level, attribute bonus, talents, and only items whose stats count");
        check(HeroLoadout.Parse(JsonSerializer.SerializeToElement(new { hero = new { team2 = new { } } })) == null &&
              HeroLoadout.Parse(JsonSerializer.SerializeToElement(new { hero = new { name = "npc_dota_hero_luna", level = 99 } })) == null,
            "observer-shaped or implausible hero blocks give no loadout");
    }

    /// <summary>Tower last-hit plans: tier-1 towers shoot 88-92 every 0.82 s; level-1 Luna hits 52-58 every 1.37 s, 0.69 s after the order.</summary>
    private static void TowerTests(Action<bool, string> check, HeroAttackTable table, HeroAttack luna, Func<int, CreepHealthBar> bar)
    {
        var t1 = table.Tower("npc_dota_goodguys_tower1_top")!.Value;
        check(t1 is { DamageMin: 88, DamageMax: 92 } && Math.Abs(t1.Interval - .9 / 1.1) < 1e-9 && table.Tower("npc_dota_badguys_tower4") is { DamageMin: 170 } &&
              table.Tower("npc_dota_goodguys_fort") == null, "tower stats by tier from the client");
        TowerPlan? Plan(string creep, double hp, double regen, (double Least, double Most)? hero = null, double? interval = null, double? delay = null,
            bool preppable = true)
        {
            double share = HeroAttack.ArmorMultiplier(CreepKinds.Armor(creep)!.Value);
            return TowerPlans.Fewest(hp, CreepKinds.MaxHp(creep)!.Value / 154.6, regen, hero ?? luna.Against(creep)!.Value, interval ?? luna.Interval,
                delay ?? luna.Delay, (t1.DamageMin * share, t1.DamageMax * share), t1.Interval, preppable);
        }
        const string ranged = "npc_dota_creep_badguys_ranged", melee = "npc_dota_creep_badguys_melee";
        check(Plan(ranged, 300, 2 + 3) == new TowerPlan(0, 3) && Plan(ranged, 120, 2) == new TowerPlan(0, 1),
            "a full ranged creep: the tower shoots three times, then one hit");
        check(Plan(melee, 550, .5 + 3) == new TowerPlan(1, 6) && Plan(melee, 550, .5) == new TowerPlan(1, 6),
            "a full melee creep: one hit now, six tower shots, then the last hit");
        // A weak hitter (26.5-29.5 on a melee creep) needs two prep hits, which take time: too slow an attack and nothing fits.
        check(Plan(melee, 550, .5, (26.5, 29.5)) == new TowerPlan(2, 6) && Plan(melee, 550, .5, (26.5, 29.5), interval: 2.5, delay: .8) == null,
            "fewest prep hits that the hero's attack speed fits in before the tower shots run out");
        check(Plan(melee, 550, .5, preppable: false) == null && Plan(ranged, 300, 2, preppable: false) == new TowerPlan(0, 3),
            "allies above half health cannot be hit yet: only tower-only plans");

        // Our tier-1 tower at the origin; an enemy wave in its range, one of our creeps beside it, an enemy flagbearer far away.
        MinimapUnit Unit(string name, int team, double x) => new(name, team, x, 0, null, null, null);
        var snapshot = new SpectatorSnapshot(0, "player", 0, null, false, null, 2, 0, [Unit("npc_dota_goodguys_tower1_mid", 2, 0),
            Unit("npc_dota_badguys_tower1_mid", 3, 5000), Unit("npc_dota_creep_badguys_flagbearer", 3, 2500)], [], []);
        var lane = LaneView.From(snapshot, luna, table);
        var near = Unit(melee, 3, 700);
        check(lane.Towers.Count == 2 && lane.TowerOver(near) is { Team: 2 } && lane.TowerOver(Unit(melee, 3, 900)) == null &&
              lane.TowerOver(Unit("npc_dota_creep_goodguys_melee", 2, 700)) == null && lane.Regen(near) == .5 &&
              lane.Regen(Unit(ranged, 3, 2000)) == 5 && lane.Regen(Unit("npc_dota_creep_goodguys_ranged", 2, 2000)) == 2,
            "a tower shoots the other side within range plus hulls; the flagbearer's aura reaches its side within 700");
        var bars = new[] { bar(155), bar(155), bar(62), bar(155) with { Ally = true } };
        string?[] names = [melee, ranged, ranged, "npc_dota_creep_goodguys_melee"];
        MinimapUnit?[] units = [Unit(melee, 3, 500), Unit(ranged, 3, 600), Unit(ranged, 3, 650), Unit("npc_dota_creep_goodguys_melee", 2, 400)];
        var labels = new List<OverlayLabel>();
        CreepLabels.Build(bars, names, units, lane, labels);
        // Full melee: one prep hit, in amber right of the bar. Full ranged and the one at 119: the tower alone, so nothing extra.
        check(labels.Select(l => (l.Text, l.Rgb)).SequenceEqual([("1", CreepLabels.Prep), ("119", CreepLabels.Enemy)]) &&
              labels[0].X > bars[0].X + bars[0].Inner + 2 && labels[0].Bottom > bars[0].Y + bars[0].Rows,
            "creeps under the tower show how many prep hits they need, whatever their health; our own creeps under our tower none");
    }

    /// <summary>
    /// Offline look at one real frame (int32 width, int32 height, BGRA pixels) with the player GSI packet of that moment: scans the
    /// bars, matches units, labels them like the game client and blends the labels in as the overlay draws them. Writes the frame in
    /// the same format and prints the hero's attack and one JSON line per label (with the matched unit's position).
    /// </summary>
    public static void Preview(string framePath, string gsiPath, string outPath)
    {
        var bytes = File.ReadAllBytes(framePath);
        int width = BitConverter.ToInt32(bytes, 0), height = BitConverter.ToInt32(bytes, 4);
        using var gpu = new GpuDevice();
        using var scanner = new GpuCreepScanner(gpu);
        var bars = scanner.ScanBgra(bytes[8..], width, height);
        using var doc = JsonDocument.Parse(File.ReadAllBytes(gsiPath));
        var snapshot = SpectatorGsi.Parse(doc.RootElement, 0);
        var units = new CreepUnitMatcher().Match(bars, snapshot.Minimap, snapshot.OwnTeam ?? 2, width, height);
        var loadout = HeroLoadout.Parse(doc.RootElement);
        var table = new HeroAttackTable("data/dota2-hero-attack.json");
        var attack = loadout == null ? null : table.Attack(loadout, out _);
        var lane = LaneView.From(snapshot, attack, table);
        Console.WriteLine(JsonSerializer.Serialize(new { loadout?.Hero, loadout?.Level, items = loadout?.Items.Select(i => i.Name), attack?.Min, attack?.Max, attack?.Quell,
            attack?.Interval, attack?.Delay, melee = attack?.Against("npc_dota_creep_goodguys_melee")?.Least, ranged = attack?.Against("npc_dota_creep_goodguys_ranged")?.Least,
            towers = lane.Towers.Count, bars = bars.Count }));
        var labels = new List<OverlayLabel>();
        CreepLabels.Build(bars, units.Select(u => u?.Name).ToArray(), units, lane, labels);
        var atlas = new GlyphAtlas(Math.Max(12, (int)Math.Round(height * .0125)));
        var pixels = bytes.AsSpan(8);
        foreach (var label in labels) {
            bool prep = label.Rgb == CreepLabels.Prep;
            int i = bars.FindIndex(b => prep ? label.X > b.X + b.Inner && label.X < b.X + b.Inner + b.Span * .2 && Math.Abs(label.Bottom - b.Y) < b.Span * .2
                : b.CenterX == label.X && b.Y - 1 == label.Bottom);
            Console.WriteLine(JsonSerializer.Serialize(new { label.Text, red = label.Rgb == CreepLabels.Lethal, prep, label.X, label.Bottom,
                unit = units[i]?.Name, unitX = units[i]?.X, unitY = units[i]?.Y, fill = bars[i].Fill, most = units[i] is { } u && CreepKinds.MaxHp(u.Name, snapshot.Clock) is int m ? bars[i].MostHp(m) : (double?)null }));
            // As the overlay's shader: premultiplied glyph tinted by the label colour, blended over the frame.
            double r = (label.Rgb >> 16 & 0xFF) / 255d, g = (label.Rgb >> 8 & 0xFF) / 255d, b = (label.Rgb & 0xFF) / 255d;
            int x = label.X - atlas.Measure(label.Text) / 2, y = label.Bottom - atlas.Height;
            foreach (char c in label.Text) {
                if (!atlas.Glyphs.TryGetValue(c, out var glyph)) continue;
                for (int gy = 0; gy < glyph.Height; gy++)
                for (int gx = 0; gx < glyph.Width; gx++) {
                    int px = x + gx, py = y + gy, a = ((glyph.Y + gy) * atlas.Width + glyph.X + gx) * 4;
                    if (px < 0 || py < 0 || px >= width || py >= height) continue;
                    var dst = pixels.Slice((py * width + px) * 4, 4);
                    double alpha = atlas.Bgra[a + 3] / 255d;
                    dst[0] = (byte)Math.Round(atlas.Bgra[a] * b + dst[0] * (1 - alpha));
                    dst[1] = (byte)Math.Round(atlas.Bgra[a + 1] * g + dst[1] * (1 - alpha));
                    dst[2] = (byte)Math.Round(atlas.Bgra[a + 2] * r + dst[2] * (1 - alpha));
                }
                x += glyph.Advance;
            }
        }
        File.WriteAllBytes(outPath, bytes);
    }
}
