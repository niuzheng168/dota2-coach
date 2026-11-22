using DotaCoach.TwoPc;
using System.Text.Json;

/// <summary>The 7.42 coach map, the common ward spots and the ward guesses built on them (data/ is read from the repository).</summary>
internal static class WardTests
{
    public static void Run(Action<bool, string> check)
    {
        // --- The 7.42 map read from the installed client.
        var map = new CoachMap("data/dota2-coach-map.json");
        check(map is { Patch: "7.42", Camps.Length: 28, Left: -9472, Top: 9472, Span: 18944 } &&
              map.Camps.Count(c => c.Pool == "amphibian") == 6 && map.Camps.Count(c => c.Kind == "ancient") == 2,
            "28 camps of 7.42 with six amphibian camps and two ancients, on the usual minimap square");
        check(map.Camps.Single(c => c.Id == "neutralcamp_good_20") is { X: 6712, Y: -7632, Kind: "medium", Pool: "radiant" } &&
              map.Camps.Single(c => c.Id == "neutralcamp_evil_11") is { Kind: "small", Pool: "amphibian" },
            "7.42 moves: the rim medium camp sits on the old twin-gate high ground, the small flooded camp nearer the stream end");
        check(map.Minimap(-9472, 9472) == (0, 0) && map.Minimap(9472, -9472) == (1, 1) && map.Minimap(0, 0) == (.5, .5),
            "world positions map onto the minimap square from its top-left corner");
        check(map.NoWard(-7456, -6938) && map.NoWard(2860, -2765) && !map.NoWard(0, 0), "no wards in a fountain or a Roshan pit");
        check(map.BaseOf(-7456, -6938) == "radiant" && map.BaseOf(5528, 5000) == "dire" && map.BaseOf(-4640, -4144) == "radiant" &&
              map.BaseOf(0, 0) == null && map.BaseOf(-6501, -872) == null, "bases end a little outside the tier-3 towers");
        string[] named = [map.Describe(-7000, -6500, 2), map.Describe(-7000, -6500, 3), map.Describe(4649, -3699, 2), map.Describe(4649, -3699, 3),
            map.Describe(2860, -2765, 2), map.Describe(-4096, -448, 3), map.Describe(-6336, 1856, 2)];
        check(named.SequenceEqual(["我方高地", "敌方高地", "我方下路一塔大野", "敌方下路一塔大野", "下河道肉山", "敌方前哨", "我方上路一塔附近"]),
            "places are named from the friend team's side: bases, camps, objectives, towers (" + string.Join("/", named) + ")");
        named = [map.Describe(200, 100, 2), map.Describe(2000, -2300, 2), map.Describe(-3000, -500, 2), map.Describe(3000, 300, 2)];
        check(named.SequenceEqual(["中路", "河道", "我方野区", "敌方野区"]), "elsewhere lanes, the river and each side's jungle (" + string.Join("/", named) + ")");
        check(map.CampAt(4649, -3699)?.Id == "neutralcamp_good_2" && map.CampAt(0, 0) == null, "a neutral inside a spawn box belongs to that camp");
        var tracker = new VisionLossTracker { Map = map };

        // --- Common 7.42 ward spots.
        var spots = new WardSpots("data/dota2-ward-spots-742.json");
        var direObservers = spots.For("dire", "observer");
        check(spots.Matches >= 200 && direObservers.Length >= 30 && spots.For("radiant", "sentry").Length >= 30 &&
              spots.For("dire", "ward").Length == direObservers.Length + spots.For("dire", "sentry").Length,
            "each side's observer and sentry spots come from a few hundred 7.42 matches");
        check(direObservers.Concat(spots.For("radiant", "observer")).All(s => !map.NoWard(s.X, s.Y) && Math.Abs(s.X) < 8500 && Math.Abs(s.Y) < 8500),
            "no common spot lies where wards cannot go or off the map");

        // --- Guesses: own team radiant, Lion (dire) seen with wards, unseen for a while, seen again with fewer.
        const string Lion = "npc_dota_hero_lion";
        // The spot weighted highest at 10:09 (phase 10–25 min): no other spot the walk allows can outrank it.
        var spot = direObservers.MaxBy(s => s.Phases[1] + 0.25 * s.Count)!;
        long tick = 0;
        SpectatorSnapshot Snap(double clock, string mode = "team-view", string match = "9000000001", params (string Name, int Team, double X, double Y)[] units) =>
            new(tick, mode, clock, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, match, 2, 0,
                units.Select(u => new MinimapUnit(u.Name, u.Team, u.X, u.Y, 0, u.Name.EndsWith("_wards") ? 0 : 1800, null))
                    .Append(new MinimapUnit("npc_dota_hero_luna", 2, -6000, -6000, 0, 1800, "minimap_herocircle_self")).ToArray(), ["radiant"], []);
        void Walk(WardTracker t, double from, double to, double x0, double y0, double x1, double y1)
        {
            for (double c = from; c <= to + 1e-9; c += 0.5) {
                double f = to > from ? (c - from) / (to - from) : 0;
                tick = (long)(c * 1000); t.Sight(Snap(Math.Floor(c), units: (Lion, 3, x0 + (x1 - x0) * f, y0 + (y1 - y0) * f)));
            }
        }
        void Fog(WardTracker t, double from, double to) { for (double c = from; c <= to; c += 0.5) { tick = (long)(c * 1000); t.Sight(Snap(Math.Floor(c))); } }
        Dictionary<string, WardReading[]> Reads(params WardReading[] reads) => new() { [Lion] = reads };
        static WardReading R(double clock, int observer, int sentry, int unknown) => new(clock, observer, sentry, unknown);

        var wards = new WardTracker(map, spots);
        Walk(wards, 600, 604, spot.X + 900, spot.Y, spot.X + 450, spot.Y);
        check(wards.Readings("9000000001", 2, Reads(R(601, 2, 0, 0))).Count == 0, "the first read only sets the count");
        Fog(wards, 604.5, 608);
        Walk(wards, 608.5, 610, spot.X - 450, spot.Y, spot.X - 700, spot.Y);
        var found = wards.Readings("9000000001", 2, Reads(R(601, 2, 0, 0), R(609, 1, 0, 0)));
        check(found is [{ Hero: Lion, Kind: "observer", Count: 1, After: 601, Before: 609, Expires: 969 }] &&
              found[0].Candidates.Length is >= 1 and <= WardTracker.MaxCandidates && !found[0].Candidates[0].Rough &&
              double.Hypot(found[0].Candidates[0].X - spot.X, found[0].Candidates[0].Y - spot.Y) < 700 &&
              Math.Abs(found[0].Candidates.Sum(k => k.Share) - 1) <= 1 + 1e-9 && found[0].Candidates.All(k => k.Place.Length > 0),
            "one observer fewer after a short walk out of sight: the common spot on its way comes first, until it would expire");
        check(wards.Readings("9000000001", 2, Reads(R(609, 1, 0, 0))).Count == 0 &&
              wards.Readings("9000000001", 2, Reads(R(615, 0, 0, 1))).Count == 0 &&
              wards.Readings("9000000001", 2, Reads(R(620, 0, 0, 3))).Count == 0,
            "a read already seen, a ward turned grey (same total) and bought wards add nothing");
        var marks = wards.Marks(700);
        check(marks.Length == found[0].Candidates.Length && marks[0] is { Hero: Lion, Kind: "observer", Rank: 0, Age: 91, Left: 269 } &&
              marks.All(m => m.X is > 0 and < 1 && m.Y is > 0 and < 1) && Wire.ValidBoard(new([], marks)),
            "marks carry the guess to the game machine's minimap: ranked, placed on the square, with age and time left");
        check(wards.Marks(970).Length == 0, "a guessed ward is gone once it would have expired");

        // Many wards at once is a misread; a ward kind that dropped while another rose keeps the total.
        var noisy = new WardTracker(map, spots);
        check(noisy.Readings("9000000001", 2, Reads(R(100, 3, 3, 0), R(103, 0, 0, 0))).Count == 0 &&
              noisy.Readings("9000000001", 2, Reads(R(106, 0, 1, 0))).Count == 0, "more than four gone at once is not believed");
        var mixed = new WardTracker(map, spots);
        Walk(mixed, 100, 112, -2000, 2000, -1000, 1000);
        var both = mixed.Readings("9000000001", 2, Reads(R(100, 1, 2, 0), R(110, 0, 1, 1)));
        check(both is [{ Kind: "observer", Count: 1 }] && both[0].Candidates.Length > 0, "observer 1→0 and sentry 2→1 with one grey: one ward gone, the observer");

        // Without common spots, the place it vanished, marked roughly.
        var rough = new WardTracker(map, null);
        Walk(rough, 600, 604, 3000, -1000, 3200, -1200);
        Fog(rough, 604.5, 620);
        Walk(rough, 620.5, 622, 5000, -2000, 5000, -2000);
        var vague = rough.Readings("9000000001", 2, Reads(R(601, 0, 2, 0), R(621, 0, 1, 0)));
        check(vague is [{ Kind: "sentry", Candidates: [{ Rough: true } k] }] && double.Hypot(k.X - 3200, k.Y + 1200) < 1 && k.Share == 1,
            "with no common spot to fit, the place it went out of sight is rough");
        check(vague[0].Unseen >= WardTracker.UnsureSeconds && rough.Active(630).Length == 1 && rough.Marks(630).Length == 0,
            "out of sight 15 s or more: the guess stands (subtitle) but puts no marks on the minimap");
        // Read as it left sight, gone 20 s, seen again with fewer: the unseen start of the window counts.
        var late = new WardTracker(map, spots);
        Walk(late, 590, 595, spot.X + 900, spot.Y, spot.X + 450, spot.Y);
        late.Readings("9000000001", 2, Reads(R(600, 2, 0, 0)));
        Fog(late, 595.5, 619.5);
        Walk(late, 620, 622, spot.X - 450, spot.Y, spot.X - 700, spot.Y);
        check(late.Readings("9000000001", 2, Reads(R(621, 1, 0, 0))) is [{ Unseen: >= 15 } edge] &&
              double.Hypot(edge.Candidates[0].X - spot.X, edge.Candidates[0].Y - spot.Y) < 700,
            "a window opening out of sight counts as unseen; the walk from the last sighting still finds the spot on the way");

        // Scanners since 2026-11-22: one entry per change of the counts, timed within the second. In sight all along,
        // the mark goes a cast range ahead of where Lion walked at the time.
        var exact = new WardTracker(map, spots);
        for (double c = 600; c <= 606; c += 0.2) { tick = (long)(c * 1000); exact.Sight(Snap(Math.Floor(c), units: (Lion, 3, -3000 + 300 * (c - 600), 1000))); }
        static WardReading E(double first, double latest, int observer) => new(latest, observer, 0, 0, first);
        check(exact.Readings("9000000001", 2, Reads(E(600.4, 601.2, 2))).Count == 0 &&
              exact.Readings("9000000001", 2, Reads(E(600.4, 602.6, 2))).Count == 0,
            "the same counts read again (the entry's latest read moves on) add nothing");
        var anchored = exact.Readings("9000000001", 2, Reads(E(600.4, 602.6, 2), E(603.0, 603.3, 1)));
        check(anchored is [{ Kind: "observer", After: 602.6, Before: 603.0, Unseen: 0, Candidates: [{ Rough: false, Share: 0.9 } at, ..] }] &&
              double.Hypot(at.X - (-3000 + 300 * 2.8 + 350), at.Y - 1000) < 60 && exact.Marks(610) is [{ Rank: 0 }, ..],
            "a drop between the entry's latest read and the next one's first, in sight: the mark is ahead of Lion halfway through");
        // Out of sight, a common spot the friend team's eyes covered all along is unlikely, and so is one behind the
        // way the hero vanished.
        WardSpot[] twins = [new(0, 2000, 50, [10, 20, 20]), new(0, -2000, 50, [10, 20, 20])];
        var fog = new List<WardModel.Gap> { new((-1500, 0), (1500, 0), 100, 20, 20) };
        List<(double, double, double)[]> NoEyes(double a, double b) => [];
        var walk = new List<(double Clock, double X, double Y)> { (99, -1900, 0), (100, -1500, 0) };
        double[] Model(WardModel.Scene scene) => WardModel.Likelihood(WardModel.Parts(twins, [], fog, scene), twins.Length);
        double[] Likely((double X, double Y)[] eyes, List<(double, double, double)> track) =>
            Model(new("observer", 1, 0.25, [.. track], [], [], (a, b) => [eyes.Select(e => (e.X, e.Y, 900.0)).ToArray()]));
        var even = Model(new("observer", 1, 0.25, [.. walk], [], [], NoEyes));
        var watched = Likely([(0, 2000)], [.. walk]);
        check(Math.Abs(even[0] - even[1]) < 1e-9 && watched[1] > 0.6 && Math.Abs(watched.Sum() - 1) < 1e-9,
            "two spots alike: the one the friend team watched through the gap falls behind");
        var turning = Model(new("observer", 1, 0.25, [(99, -1900, -400), (100, -1500, 0)], [], [], NoEyes));
        check(turning[0] > 0.55, "the spot the hero was heading for as it vanished comes first");

        // The friend team sees the enemy ward: the player sees it too, the guess is done. A new match starts afresh.
        var seen = new WardTracker(map, spots);
        Walk(seen, 600, 604, spot.X + 900, spot.Y, spot.X + 450, spot.Y); Fog(seen, 604.5, 608); Walk(seen, 608.5, 610, spot.X - 450, spot.Y, spot.X - 700, spot.Y);
        seen.Readings("9000000001", 2, Reads(R(601, 2, 0, 0), R(609, 1, 0, 0)));
        var top = seen.Active(620)[0].Candidates[0];
        tick = 620_000; seen.Sight(Snap(620, units: ("npc_dota_observer_wards", 3, top.X + 100, top.Y)));
        check(seen.Active(621).Length == 0, "an enemy ward in the friend team's sight ends the guess near it");
        // Found and dewarded while Lion is in the fog, before the scanner reads its lower count.
        var dewarded = new WardTracker(map, spots);
        Walk(dewarded, 600, 604, spot.X + 900, spot.Y, spot.X + 450, spot.Y);
        dewarded.Readings("9000000001", 2, Reads(R(601, 2, 0, 0)));
        for (double c = 604.5; c <= 608; c += 0.5) {
            tick = (long)(c * 1000);
            dewarded.Sight(c is >= 606 and < 608 ? Snap(Math.Floor(c), units: ("npc_dota_observer_wards", 3, spot.X + 150, spot.Y)) : Snap(Math.Floor(c)));
        }
        Walk(dewarded, 608.5, 610, spot.X - 450, spot.Y, spot.X - 700, spot.Y);
        check(dewarded.Readings("9000000001", 2, Reads(R(609, 1, 0, 0))).Count == 0 && dewarded.Marks(620).Length == 0,
            "a ward found and dewarded before the lower count is read gets no guess: no mark, no subtitle");
        var older = new WardTracker(map, spots);
        tick = 590_000; older.Sight(Snap(590, units: ("npc_dota_observer_wards", 3, spot.X + 150, spot.Y)));
        Walk(older, 600, 604, spot.X + 900, spot.Y, spot.X + 450, spot.Y);
        older.Readings("9000000001", 2, Reads(R(601, 2, 0, 0)));
        Fog(older, 604.5, 608); Walk(older, 608.5, 610, spot.X - 450, spot.Y, spot.X - 700, spot.Y);
        check(older.Readings("9000000001", 2, Reads(R(609, 1, 0, 0))).Count == 1, "a ward seen before the earlier read is an older one: the guess stands");
        var placeholder = SpectatorGsi.Parse(JsonDocument.Parse("""
            {"map": {"clock_time": 600, "matchid": "9000000001", "game_state": "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"}, "player": {},
             "minimap": {"o0": {"xpos": 0, "ypos": 0, "team": 3, "unitname": "npc_dota_hero_lion", "visionrange": 1800},
                         "o1": {"xpos": 0, "ypos": 0, "team": 3, "unitname": "npc_dota_sentry_wards", "visionrange": 0},
                         "o2": {"xpos": 0, "ypos": 120, "team": 3, "unitname": "npc_dota_hero_lina", "visionrange": 1800}}}
            """).RootElement, 0);
        check(placeholder.Minimap is [{ Name: "npc_dota_hero_lina" }], "units at 0,0 while coming into sight are left out: no mid-lane track points or wards");
        var other = new WardTracker(map, spots);
        Walk(other, 600, 604, 0, 0, 100, 100);
        other.Readings("9000000001", 2, Reads(R(601, 2, 0, 0)));
        tick = 700_000; other.Sight(Snap(5, match: "9000000002", units: (Lion, 3, 0, 0)));
        check(other.Readings("9000000002", 2, Reads(R(6, 1, 0, 0))).Count == 0, "reads of another match never compare with the last one");
        tick = 701_000; other.Sight(Snap(6, mode: "full-spectator", units: (Lion, 3, 0, 0)));
        check(other.Clock is >= 5 and < 6 && double.IsNaN(new WardTracker(map, spots).Clock),
            "delayed (full) spectating is not tracked; a new tracker has no clock yet");

        // --- The board carries the marks; the mailbox ages them and drops expired ones.
        var mark = new WardMark(Lion, "observer", .4, .6, 0, .7, 10, 30);
        check(Wire.ValidBoard(new([], [mark])) && !Wire.ValidBoard(new([], [mark with { Kind = "smoke" }])) &&
              !Wire.ValidBoard(new([], [mark with { X = 1.2 }])) && !Wire.ValidBoard(new([], [mark with { Hero = "../x" }])) &&
              !Wire.ValidBoard(new([], [mark with { Rank = 3 }])) && !Wire.ValidBoard(new([], [mark with { Left = 999 }])) &&
              !Wire.ValidBoard(new([], Enumerable.Repeat(mark, Wire.MaxWardMarks + 1).ToArray())),
            "ward marks are checked: kind, the minimap square, rank, time left, how many");
        long now = 1000;
        var mailbox = new CoachMailbox(() => now);
        var hello = mailbox.Begin();
        mailbox.AcceptStatus(new(hello.SessionId, "", 1, "dota2", 1920, 1080, null, null));
        mailbox.SetEnemies(new([], [mark, mark with { Rank = 1, Left = 2 }]));
        now += 2500;
        check(mailbox.Enemies(hello.SessionId) is { Heroes: [], Wards: [{ Age: 12.5, Left: 27.5 }] },
            "the game machine gets the marks aged by the time held, the expired one left out");
        string json = JsonSerializer.Serialize(new { enemies = new EnemyBoard([], [mark]) }, Wire.Json);
        check(json.Contains("\"wards\":[{\"hero\":\"npc_dota_hero_lion\"") &&
              JsonSerializer.Deserialize<EnemyHero[]>("[{\"hero\":\"npc_dota_hero_lion\",\"wards\":[{\"clock\":1}]}]", Wire.Json)![0].Hero == Lion,
            "marks travel as JSON; a scanner row's own ward reads never reach the panel record");

        // --- The scanner's live file: each enemy's last ward reads, malformed lists left out.
        string dir = Path.Combine(Path.GetTempPath(), "dotacoach-wards-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try {
            string file = Path.Combine(dir, "live.json");
            long unix = 1_800_000_000_000;
            object Row(string hero, object? reads) => new { hero, level = 9, age = 1.0, items = (string?[]?)null, neutral = (string?)null, wards = reads };
            File.WriteAllText(file, JsonSerializer.Serialize(new { version = 2, run = "ab", written = unix / 1000.0, stopped = false, mode = "team-view",
                matchId = "9000000001", ownTeam = 2, clock = 900, selecting = true, enemies = new[] {
                    Row(Lion, new[] { new { clock = 601, observer = 2, sentry = 1, unknown = 0 }, new { clock = 609, observer = 1, sentry = 1, unknown = 0 } }),
                    Row("npc_dota_hero_pudge", new[] { new { clock = 601, observer = -1, sentry = 0, unknown = 0 } }),
                    Row("npc_dota_hero_razor", null),
                    Row("npc_dota_hero_lina", new[] { new { first = 600.25, clock = 604.5, observer = 1, sentry = 0, unknown = 0 } }),
                    Row("npc_dota_hero_zuus", new[] { new { first = 605.5, clock = 604.5, observer = 1, sentry = 0, unknown = 0 } }) } }));
            var (status, board, reads) = ScanFeed.Poll(file, "9000000001", unix);
            check(status.Running && board?.Heroes.Length == 5 && reads.Count == 2 &&
                  reads[Lion] is [{ Clock: 601, Observer: 2, Sentry: 1, Unknown: 0, First: null }, { Clock: 609, Observer: 1, Total: 2 }] &&
                  reads["npc_dota_hero_lina"] is [{ First: 600.25, Clock: 604.5 }],
                "ward reads are taken per enemy from the live file, with the first read of each since 2026-11-22; a negative count " +
                "or a first read after the latest drops that enemy's list only");
        }
        finally { Directory.Delete(dir, true); }

        // --- Leaving vision is named with the 7.42 map.
        var names = new HeroNames();
        List<PlusAlert> alerts = [];
        for (long t = 0; t <= 9000; t += 500) {
            tick = t;
            var units = t <= 4000 ? new[] { new MinimapUnit("npc_dota_hero_razor", 3, 4649, -3699, 0, 1800, null) } : [];
            alerts.AddRange(tracker.Update(new(t, "team-view", 600 + t / 1000.0, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, "9000000001", 2, 0,
                units, ["radiant"], []), names));
        }
        check(alerts is [{ Text: "razor 离开视野 · 最后在我方下路一塔大野" }], "a vision alert names the 7.42 camp it was last seen at");

        // --- Where the game machine draws the marks.
        var settings = new GameOverlaySettings();
        check(settings is { ShowWards: true, MinimapRight: false, MinimapSize: 100 } &&
              settings.Minimap(1920, 1080) == new MinimapLayout(1920, 1080, 0, 840, 240) &&
              (settings with { MinimapRight = true }).Minimap(1920, 1080) == new MinimapLayout(1920, 1080, 1680, 840, 240) &&
              (settings with { MinimapSize = 150 }).Minimap(1280, 720) == new MinimapLayout(1280, 720, 0, 480, 240),
            "the default HUD's minimap box, flipped right or enlarged by the local settings");
        bool rejected = false;
        try { (settings with { MinimapSize = 50 }).Validate(); } catch (ArgumentException) { rejected = true; }
        check(rejected, "a minimap size outside 70–160 % is refused");
    }
}
