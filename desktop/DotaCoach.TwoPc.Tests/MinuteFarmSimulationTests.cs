using System.IO.Compression;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class MinuteFarmSimulationTests
{
    // Fictional own-player inputs, exact build-6989 spawner XY. Neither position nor these panels imply live creeps.
    internal static OwnState RadiantPanel(double clock = 600) => new("742000001", "npc_dota_hero_luna", "radiant",
        clock, true, 900, 350, 3978.452881, -5026.539062, [], [],
        new(true, true, false, MoveSpeed: 370, AttackDamage: 120, AttackSpeed: 180, Level: 10, InstalledBuild: 6989));
    internal static OwnState DirePanel(double clock = 600) => RadiantPanel(clock) with {
        MatchId = "742000002", Team = "dire", Hero = "npc_dota_hero_troll_warlord", X = -3910.783691, Y = 4829.345215,
    };
    private static FarmOption Ids(params string[] ids) => new("untrusted",
        ids.Select(id => new FarmStop(id, "spoofed", "spoofed", 0, 0, -10, -1)).ToArray());
    private static FarmStop[] Price(OwnState p, params string[] ids) => MinuteFarmSimulation.Reprice(p, Ids(ids));
    private static bool Is(FarmOption o, params string[] ids) => o.Stops.Select(s => s.Id).SequenceEqual(ids);
    private static bool Rejected(Action action) { try { action(); return false; } catch (ArgumentException) { return true; } }

    public static void Run(Action<bool, string> check)
    {
        var p = RadiantPanel(); var f = p.Farm!;
        var strong = p with { Farm = f with { AttackDamage = 300, AttackSpeed = 330, Level = 15 } };
        var normal = MinuteFarmSimulation.Build(p)!;
        var high = MinuteFarmSimulation.Build(strong)!;
        var dire = MinuteFarmSimulation.Build(DirePanel())!;
        check(normal is { Clock: 600, Minute: 10, Team: "radiant", Hero: "npc_dota_hero_luna" } &&
            normal.Timing is { MoveSpeed: 370, DamagePerHit: 114, AttackSpeed: 180, Level: 10, BaseAttackTime: 1.7 } t &&
            t.DamageBasis.Contains("GSI") && t.MoveBasis.Contains("GSI"), "the GSI panel is a conditional own-hero input, not a simulated average hero");
        var mixed = normal.Options.Single(o => Is(o, "good_1", "lane_good_bot_3"));
        check(mixed.Stops is [{ Kind: "camp", ArrivalSeconds: 0, FinishSeconds: 23, WalkUnits: 0, PackWindowSeconds: 23,
                ModeledRosters: 6, CatalogRosters: 6 }, { Kind: "lane", ArrivalSeconds: 30, FinishSeconds: 30,
                WalkUnits: 1629, X: 3226, Y: -6195 }] &&
            dire.Options.Single(o => Is(o, "evil_2", "lane_bad_top_3")).Stops is [
                { ArrivalSeconds: 0, FinishSeconds: 23 }, { ArrivalSeconds: 29, FinishSeconds: 29, WalkUnits: 1347 }],
            "integration fixtures: relative arrival/finish from exact Radiant and Dire spawner origins to actual lane anchors");
        check(high.Options.Any(o => o.Stops is [{ Kind: "lane" }]) &&
            high.Options.Any(o => Is(o, "good_1", "good_2") && o.Stops[^1].FinishSeconds == 30) &&
            high.Options.Any(o => Is(o, "good_1", "good_2", "good_15") && o.Stops[^1].FinishSeconds == 57 &&
                o.Stops.All(s => s.CostBasis == "modeled-roster-planning-envelope")) &&
            high.Options.Any(o => o.Stops is [{ Kind: "camp" }, { Kind: "camp" }, { Kind: "lane" }]),
            "a strong synthetic panel admits distinct pure lane, camp pair, three conditional ordinary-pack windows and mixed returns");
        var noLane = p with { Clock = 610, X = -8313.316406, Y = -552.892578,
            Farm = f with { MoveSpeed = 55, AttackDamage = 600, AttackSpeed = 600 } };
        var deep = MinuteFarmSimulation.Build(noLane)!;
        check(deep.Options.All(o => o.Stops.All(s => s.Kind == "camp")) &&
            deep.Options.Any(o => Is(o, "good_13", "good_16") && o.Stops is [
                { ArrivalSeconds: 0, FinishSeconds: 8 }, { ArrivalSeconds: 41, FinishSeconds: 50, WalkUnits: 1506 }]),
            "deep in the own jungle with no reachable lane this minute, a fully budgeted two-camp-only option still exists");
        check(!MinuteFarmSimulation.Build(noLane with { Clock = 610.01 })!.Options.Any(o => Is(o, "good_13", "good_16")),
            "fractional starting clock cannot grant another fraction of a second at the minute boundary");

        foreach (var own in new[] { p, strong, DirePanel(), noLane, p with { Clock = 619.75 }, p with { Clock = 1230, Farm = null } }) {
            var s = MinuteFarmSimulation.Build(own)!; int budget = 60 - s.Clock % 60;
            check(s.Options is { Length: >= 1 and <= 6 } && s.Options.Select(o => o.Id).Distinct().Count() == s.Options.Length &&
                s.Options.Select(o => string.Join("/", o.Stops.Select(t => t.Id))).Distinct().Count() == s.Options.Length &&
                s.Options.All(o => o.Stops is { Length: >= 1 and <= 3 } && o.Stops.Select(t => t.Id).Distinct().Count() == o.Stops.Length &&
                    o.Stops.All(t => t.Id.StartsWith(own.Team == "radiant" ? "good_" : "evil_") ||
                        t.Id.StartsWith(own.Team == "radiant" ? "lane_good_" : "lane_bad_")) &&
                    o.Stops.Select((t, i) => (i == 0 ? 0 : o.Stops[i - 1].FinishSeconds) <= t.ArrivalSeconds &&
                        t.ArrivalSeconds <= t.FinishSeconds && t.FinishSeconds <= budget).All(b => b) &&
                    MinuteFarmSimulation.Reprice(own, o).SequenceEqual(o.Stops)),
                "every option is distinct, bounded, ordered, canonical and reproducible on the same own state");
        }
        var naked = p with { X = 1100, Y = -5400, Farm = null };
        var bare = MinuteFarmSimulation.Build(naked)!;
        check(bare.Timing is { Level: 1, LevelBasis: "assumed-level-1", DamagePerHit: 50, AttackSpeed: 124, MoveSpeed: 325 } &&
            MinuteFarmSimulation.Build(DirePanel() with { Farm = null })!.Timing is { DamagePerHit: 51, MoveSpeed: 290 } &&
            MinuteFarmSimulation.Build(p with { Farm = new(true, true, false, Level: 10) })!.Timing is
                { DamagePerHit: 78, AttackSpeed: 152, MoveSpeed: 325 },
            "missing stats use labeled 6989 hero/level KV assumptions, no inferred equipment, stance, splash or manual sightings");
        check(bare.Options.Any(o => Is(o, "good_4", "lane_good_bot_2") && o.Stops[0] is { ArrivalSeconds: 6,
                FinishSeconds: 31, PackWindowSeconds: 120, CostBasis: "visit-deadline-not-a-clear" } &&
                o.Stops[0].Label.Contains("限时探营")) &&
            bare.Options.Select(o => o.Stops[0].Label).Distinct().Count() >= 3,
            "no-panel play remains useful: a 25-second limited visit is not stated to clear a 120-second baseline pack");
        check(Price(p, "good_2") is [{ ModeledRosters: 3, CatalogRosters: 6, PackWindowSeconds: 26,
                ArrivalSeconds: 7, FinishSeconds: 32, CostBasis: "visit-deadline-not-a-clear" }] &&
            Price(strong, "good_2") is [{ PackWindowSeconds: 11, ArrivalSeconds: 7, FinishSeconds: 18 }] &&
            Price(p with { Farm = f with { MoveSpeed = 185 } }, "good_2") is [{ ArrivalSeconds: 12, PackWindowSeconds: 26 }],
            "damage/IAS change work, speed changes travel, unsupported rosters are not silently priced as exact");
        // Regenerated independently by build-minute-farm-742.ts --check with the TS event simulator:
        // 10:10, plain physical, fixed low roll, BAT 1.7. Compare its longest supported first-hit
        // span to our full-interval/acquisition reserve, NOT to a real player's time to empty the camp.
        foreach (var (attacker, site, exactSpan, pad) in new[] {
            (p, "good_1", 15.111, 7.889), (p, "good_2", 18.889, 7.111), (strong, "good_2", 4.121, 6.879) })
            check(Price(attacker, site).Single().PackWindowSeconds is int upper && Math.Abs(upper - exactSpan - pad) < 1e-6,
                "the uncapped native work envelope includes the independent matched TS first-hit scenario plus explicit headroom");
        check(Price(p with { Clock = 1200 }, "good_1") is [
                { CostBasis: "visit-deadline-outside-timing-study", PackWindowSeconds: null, ModeledRosters: 0 }] &&
            Price(p with { Farm = f with { AttackDamage = 1 } }, "good_1") is [{ PackWindowSeconds: null, ModeledRosters: 0 }] &&
            MinuteFarmSimulation.Build(p with { Clock = 0 })!.Options.All(o => o.Stops.All(t => t.Kind == "lane")),
            "no first-spawn kills before 1:00, no fitted late-game clear or numerical upper when regen beats the attacker");

        // The two physical 6989 gate legs below are NOT the same kind of distance. The TS smoothed gate
        // route is 2495.192 wu; this model charges the traversable 64/91 graph (2631), never the 2302-wu chord.
        var gate = p with { X = 4520, Y = -8336 };
        var highGround = Price(gate, "good_20").Single();
        check(highGround is { WalkUnits: 2631, ArrivalSeconds: 10, FinishSeconds: 35 } &&
            highGround.ArrivalSeconds == Math.Ceiling(1.15 * highGround.WalkUnits / 370) + 1, "gate high ground uses a real swept walk plus named planning allowances");
        // A different unconstrained TS walk from good_1 to good_20 is ~4069 wu through (5440,-5984).
        // Both endpoint names are Radiant, but that corridor is outside the home interior. The native
        // walker must price the 5451-wu in-domain detour, not merely whitelist the two endpoints.
        check(Price(p, "good_20") is [{ WalkUnits: 5451, ArrivalSeconds: 18 }] &&
            MinuteFarmSimulation.Build(p with { X = 5440, Y = -5984 }) == null &&
            Price(DirePanel() with { X = -4823.879883, Y = 3914.800049 }, "evil_20") is [{ WalkUnits: 5353, ArrivalSeconds: 18 }],
            "side masks restrict travel through intermediate terrain on both sides, including otherwise walkable corridors");
        foreach (var bad in new[] {
            p with { X = -1215, Y = -6624 }, // GNV-open centre, actual off-centre hull touches a blocked neighbour
            p with { X = -2800, Y = -500 },  // standing tree despite traversable GNV below
            p with { X = -7456, Y = -3488 }, // own-side but disconnected ground pocket
            p with { X = -7456, Y = -6938 }, // fountain structure
            p with { X = 0, Y = 0 }, p with { X = DirePanel().X, Y = DirePanel().Y }, p with { X = double.MaxValue },
            p with { X = null }, p with { Y = double.NaN }, p with { Alive = false }, p with { Health = 0 },
            p with { Clock = -.5 }, p with { Clock = double.NaN }, p with { Team = "observer" },
            p with { Hero = "npc_dota_hero_unknown" }, p with { Farm = f with { MoveSpeed = 0 } },
            p with { Farm = f with { AttackSpeed = double.NaN } }, p with { Farm = f with { InstalledBuild = 6975 } },
            p with { Farm = f with { OwnParticipant = false } }, p with { Farm = f with { Paused = true } },
        }) check(MinuteFarmSimulation.Build(bad) == null && MinuteFarmSimulation.Reprice(bad, mixed).Length == 0,
            "invalid/inapplicable, disconnected or outside-domain inputs cannot use a nearest-node or straight-line fallback");

        check(MinuteFarmSimulation.Reprice(RadiantPanel(630), mixed).Length == 2 &&
            MinuteFarmSimulation.Reprice(RadiantPanel(635), mixed) is [{ Id: "good_1", ArrivalSeconds: 0, FinishSeconds: 23 }] &&
            MinuteFarmSimulation.Reprice(RadiantPanel(640), mixed).Length == 0,
            "after model latency, return exactly the longest affordable prefix; never switch to the cheaper lane or alternate camp");
        var moved = strong with { X = -720.000061, Y = -7696 };
        var original = high.Options.Single(o => Is(o, "good_1", "lane_good_bot_3"));
        check(MinuteFarmSimulation.Build(moved)!.Options.All(o => o.Id != original.Id) &&
            MinuteFarmSimulation.Reprice(moved, original) is [
                { Id: "good_1", ArrivalSeconds: 22, FinishSeconds: 34 }, { Id: "lane_good_bot_3", ArrivalSeconds: 41, FinishSeconds: 41 }],
            "a still-feasible selected itinerary survives a change of position even when a fresh shortlist would omit it");
        check(Price(p, "good_1", "evil_1", "good_2") is [{ Id: "good_1", X: 3978.452881, Y: -5026.539062, Kind: "camp" }] &&
            Price(p, "good_1", "good_1", "good_2").Length == 1 && Price(p, "made_up", "good_1").Length == 0 &&
            Price(p, "good_1", "good_2", "good_5", "good_9").Length == 0, "reprice canonicalizes from local assets and never skips a forbidden, duplicated or unknown leg");

        var left = MinimapLayout.Suggest(1280, 720); var right = MinimapLayout.Suggest(1280, 720, true);
        var mark = MinuteFarmSimulation.Project(highGround, left); var r = MinuteFarmSimulation.Project(highGround, right);
        check(Math.Abs(mark.X * 1280 - (6712 + 9472) * 160d / 18944) < 1e-8 &&
            Math.Abs(mark.Y * 720 - (560 + (9472 + 7632) * 160d / 18944)) < 1e-8 &&
            Math.Abs((r.X - mark.X) * 1280 - 1120) < 1e-8 && r.Y == mark.Y && left.Contains(mark) && right.Contains(r),
            "native 6989 overview projects positive Y north; right-side HUD translates without mirroring the world");
        check(Rejected(() => MinuteFarmSimulation.Project(highGround with { X = 0 }, left)) &&
            Rejected(() => MinuteFarmSimulation.Project(highGround with { Id = "made_up" }, left)) &&
            Rejected(() => MinuteFarmSimulation.Project(highGround, new(1280, 720, 400, 100, 160))),
            "off-catalog coordinates and unsupported layouts cannot be projected");
        CheckEmbeddedGraph(check);
    }

    private static void CheckEmbeddedGraph(Action<bool, string> check)
    {
        var asm = typeof(MinuteFarmSimulation).Assembly;
        using var stream = asm.GetManifestResourceStream("DotaCoach.MinuteFarm.Map742")!;
        using var gz = new GZipStream(stream, CompressionMode.Decompress);
        using var doc = JsonDocument.Parse(gz); var d = doc.RootElement;
        int w = d.GetProperty("width").GetInt32();
        var side = d.GetProperty("sides").GetBytesFromBase64(); var edge = d.GetProperty("edges").GetBytesFromBase64();
        int[] dx = [1, 1, 0, -1, -1, -1, 0, 1], dy = [0, 1, 1, 1, 0, -1, -1, -1];
        int traversals = 0; bool valid = true;
        for (int i = 0; i < edge.Length; i++) for (int k = 0; k < 8; k++) if ((edge[i] & 1 << k) != 0) {
            int j = i + dx[k] + w * dy[k];
            valid &= side[i] is 1 or 2 && side[i] == side[j] && (edge[j] & (1 << ((k + 4) % 8))) != 0;
            if ((k & 1) != 0) valid &= side[i] == side[i + dx[k]] && side[i] == side[i + w * dy[k]];
            traversals++;
        }
        check(valid && traversals > 180_000 && d.GetProperty("build").GetInt32() == 6989 &&
            d.GetProperty("sites").GetArrayLength() == 46 && asm.GetManifestResourceNames().Contains("DotaCoach.MinuteFarm.Priors742"),
            "both native resources ship in Core; every directed graph edge and diagonal shoulder stays on one side and has a reverse");
    }
}
