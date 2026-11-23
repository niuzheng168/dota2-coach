using DotaCoach.TwoPc;

/// <summary>Drop rhythms foretelling creep and tower hits, and red numbers that look ahead by the own hit's delay.</summary>
internal static class LastHitTimingTests
{
    public static void Run(Action<bool, string> check)
    {
        var g4k = new CreepBarGeometry(2160);
        double melee = 550 / g4k.Span; // 3.56 HP per pixel
        CreepHitHistory Bar(params (double At, int Fill)[] frames)
        {
            var h = new CreepHitHistory();
            foreach (var (at, fill) in frames) h.Add(at, fill);
            return h;
        }
        // A melee creep's bar at 60 frames a second: 100 pixels, then a 6-pixel hit every second from one attacker.
        CreepHitHistory Rhythm(double until, params (double At, int Pixels)[] hits)
        {
            var h = new CreepHitHistory();
            int fill = 100;
            for (double t = -.5; t <= until + 1e-9; t += 1 / 60d) {
                foreach (var hit in hits) if (Math.Abs(hit.At - t) < .5 / 60) fill -= hit.Pixels;
                h.Add(t, fill);
            }
            return h;
        }

        check(Bar((0, 100), (.1, 100), (.2, 94)).Drops is [{ Pixels: 6, At: .2 }] && Bar((0, 100), (.1, 94), (.2, 96)).Drops.Count == 1 &&
              Bar((0, 100), (.1, 94), (.2, 100)).Drops.Count == 0, "a lower fill is a drop; a refill beyond regeneration starts the track over");
        check(Bar((0, 100), (.1, 94), (4, 88)).Drops.Count == 0 && Bar((0, 100), (.1, 94), (3, 88)).Drops.Count == 2,
            "a bar unseen for longer than any rhythm starts over");

        var one = Rhythm(1.2, (0, 6), (1, 6));
        check(Math.Abs(one.Foretold(1.2, 2.1, melee) - 6 * melee) < 1e-9 && one.Foretold(1.2, 1.9, melee) == 0 &&
              Math.Abs(one.Foretold(1.2, 3.1, melee) - 12 * melee) < 1e-9,
            "two similar drops a second apart foretell the same hit every second after");
        check(Rhythm(1.2, (.5, 6)).Foretold(1.2, 2.5, melee) == 0 && Rhythm(1.2, (0, 6), (1, 15)).Foretold(1.2, 2.5, melee) == 0 &&
              Rhythm(1.2, (0, 6), (.6, 6)).Foretold(1.2, 2.5, melee) == 0,
            "a single drop, a different size or a different spacing foretells nothing");
        check(Math.Abs(Rhythm(1.2, (.5, 6)).Foretold(1.2, 2.4, melee, singles: true) - 6 * melee) < 1e-9 &&
              Rhythm(1.2, (.5, 15)).Foretold(1.2, 2.5, melee, singles: true) == 0 && Rhythm(1.7, (.5, 6)).Foretold(1.7, 2.5, melee, singles: true) == 0 &&
              Math.Abs(Rhythm(1.7, (.5, 6), (1.5, 6)).Foretold(1.7, 2.6, melee, singles: true) - 6 * melee) < 1e-9,
            "with singles a lone creep-sized drop repeats a second later, once; a hero-sized one does not, an overdue one neither");
        check(Rhythm(2.2, (0, 6), (1, 6)).Foretold(2.2, 3.5, melee) == 0, "an overdue repeat ends the rhythm (the attacker died or turned)");
        check(Math.Abs(Rhythm(2.1, (0, 6), (1, 6), (2, 6)).Foretold(2.1, 3.05, melee) - 6 * melee) < 1e-9,
            "a repeat that came carries the rhythm on, counted once");
        check(Math.Abs(Rhythm(1.5, (0, 6), (.4, 5), (1, 6), (1.4, 5)).Foretold(1.5, 2.5, melee) - 11 * melee) < 1e-9,
            "two attackers out of step are two rhythms");
        check(Math.Abs(Rhythm(1.1, (0, 12), (1, 12)).Foretold(1.1, 2.05, melee) - 12 * melee) < 1e-9,
            "two creeps striking together repeat together");
        // A tier-1 tower takes 78-82 off a melee creep every 0.833 s (25 server ticks): 22-23 pixels.
        var tower = Rhythm(1, (0, 23), (.83, 22));
        check(Math.Abs(tower.Foretold(1, 1.7, melee, .833) - 22 * melee) < 1e-9 && tower.Foretold(1, 1.7, melee) == 0 &&
              Rhythm(1, (0, 6), (.83, 6)).Foretold(1, 1.7, melee, .833) == 0,
            "a tower's big shots repeat at its interval; small drops never take the tower's rhythm");

        // Level-1 Luna on a melee creep: at least 46.4 after armor, 0.28 s swing and 900 projectile speed.
        var table = new HeroAttackTable("data/dota2-hero-attack.json");
        var luna = table.Attack(new("npc_dota_hero_luna", 1, false, 0, new bool[8], []), out _)!.Value;
        check(luna.Projectile == 900 && luna.Range == 330 && Math.Abs(luna.DelayAt(450) - (HeroAttack.Order + luna.Swing + .5)) < 1e-9 &&
              Math.Abs(luna.DelayAt(370) - luna.Delay - HeroAttack.Order) < 1e-9 && table.Attack(new("npc_dota_hero_axe", 1, false, 0, new bool[8], []), out _)!.Value is
                  { Projectile: 0 } axe && axe.DelayAt(500) == HeroAttack.Order + axe.Swing,
            "the delay grows with the distance the projectile flies; melee heroes have none");
        double least = luna.Against("npc_dota_creep_badguys_melee")!.Value.Least;
        // 18 pixels hold up to 64.0 HP, 17.6 above the weakest roll: red only when a foretold 21-HP hit lands first.
        var bar = new CreepHealthBar(0, 0, 12, 18, g4k.Inner, g4k.Span, false, false);
        var timing = new LastHitTiming(.15, .08);
        var hits = Rhythm(1.3, (0, 6), (1, 6));
        double horizon = timing.Horizon(luna, 400);
        check(horizon > .7 && !CreepLabels.Kills(bar, 550, .5, luna, least, hits, 1.3, null, 400, null) &&
              CreepLabels.Kills(bar, 550, .5, luna, least, hits, 1.3, timing, 400, null) &&
              !CreepLabels.Kills(bar, 550, .5, luna, least, hits, 1.0, timing, 400, null),
            "red as soon as a hit foretold before the own one lands brings the creep into one hit");
        check(!CreepLabels.Kills(bar, 550, .5, luna, least, hits, 1.3, timing, luna.Range + HeroAttack.Hulls + CreepLabels.OutOfReach + 1, null) &&
              !CreepLabels.Kills(bar, 550, .5, luna, least, new CreepHitHistory(), 1.3, timing, 400, null),
            "out of range (the hero walks first) or without a rhythm nothing is foretold");
        var close = new CreepHealthBar(0, 0, 12, 13, g4k.Inner, g4k.Span, false, false);
        check(CreepLabels.Kills(close, 550, .5, luna, least, null, 0, null, null, null) == close.MostHp(550) + .5 * luna.Delay <= least,
            "without a timing the rule is this frame's health and the full delay, as before");
        // 5 pixels (14-18 HP) with two 6-pixel hits foretold before the own one lands: the creep dies first.
        var low = new CreepHealthBar(0, 0, 12, 5, g4k.Inner, g4k.Span, false, false);
        var two = Rhythm(1.3, (0, 6), (.2, 6), (1, 6), (1.2, 6));
        check(low.LeastHp(550) > 14 && low.LeastHp(550) < low.MostHp(550) && CreepLabels.Kills(low, 550, .5, luna, least, two, 1.3, timing, 400, null) &&
              !CreepLabels.Kills(low, 550, .5, luna, least, two, 1.3, timing with { Alive = true }, 400, null) &&
              CreepLabels.Kills(bar, 550, .5, luna, least, hits, 1.3, timing with { Alive = true }, 400, null),
            "with Alive no red when the foretold hits kill the creep before the own hit lands, red when it survives them");
        check(Math.Abs(LastHitTiming.Roll((46, 52), timing) - 46) < 1e-9 && Math.Abs(LastHitTiming.Roll((46, 52), timing with { Risk = .5 }) - 49) < 1e-9 &&
              LastHitTiming.Roll((46, 52), null) == 46, "the kill is judged by the weakest roll unless risk is raised");

        // Settings: on by default, reaction 150 ms and ping 50 ms (+30 ms local) give the evaluated 0.15 s lead and 0.08 s latency.
        var settings = new CreepHpSettings();
        check(settings.Timing is { Lead: .15, Patient: true } t && Math.Abs(t.Latency - .08) < 1e-9 && !t.Singles && !t.Alive && (settings with { LookAhead = false }).Timing == null,
            "look-ahead is on by default with the lab's timing; off gives this frame's red");
        string file = Path.Combine(Path.GetTempPath(), $"creep-hp-{Guid.NewGuid():N}.json");
        try {
            (settings with { ReactionMs = 220, PingMs = 90 }).Save(file);
            check(CreepHpSettings.Load(file) is { ReactionMs: 220, PingMs: 90, LookAhead: true } && CreepHpSettings.Load(file + ".missing") == new CreepHpSettings(),
                "settings round-trip; a missing file gives the defaults");
            File.WriteAllText(file, "{\"ReactionMs\":5000}");
            bool rejected = false;
            try { (settings with { PingMs = -1 }).Save(file + ".bad"); } catch (ArgumentException) { rejected = true; }
            check(CreepHpSettings.Load(file) == new CreepHpSettings() && rejected && !File.Exists(file + ".bad"), "out-of-range settings load as defaults and are never saved");
        } finally { File.Delete(file); }
    }
}
