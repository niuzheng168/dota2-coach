using DotaCoach.TwoPc;
using System.Text.Json;

internal static class PlusAlertTests
{
    public static void Run(Action<bool, string> check)
    {
        // --- Areas, named from the friend team's side.
        check(MapArea.Name(-5719, 5371, 2) == "上路" && MapArea.Name(6500, -2000, 2) == "下路" && MapArea.Name(200, 100, 2) == "中路",
            "lanes by map edge and diagonal");
        check(MapArea.Name(-2800, 2300, 2) == "河道" && MapArea.Name(-3000, -500, 2) == "我方野区" && MapArea.Name(-3000, -500, 3) == "敌方野区",
            "river and jungle sides follow the friend team");
        check(MapArea.Name(-6500, -6000, 2) == "我方高地" && MapArea.Name(6000, 5500, 2) == "敌方高地", "high grounds");

        // --- Enemies leaving vision, from Plus team-view packets (own team radiant).
        var names = new HeroNames();
        SpectatorSnapshot Snap(long at, params (string Name, int Team, double X, double Y, double Vision)[] units) =>
            new(at, "team-view", 600 + at / 1000.0, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, "9000000001", 2, 0,
                units.Select(u => new MinimapUnit(u.Name, u.Team, u.X, u.Y, 0, u.Vision, "minimap_enemyicon"))
                    .Append(new MinimapUnit("npc_dota_hero_luna", 2, -6000, -6000, 0, 1800, "minimap_herocircle_self")).ToArray(), ["radiant"], []);
        var razor = ("npc_dota_hero_razor", 3, 200.0, 100.0, 1800.0);
        List<PlusAlert> Feed(VisionLossTracker tracker, long from, long to, bool visible) {
            var found = new List<PlusAlert>();
            for (long t = from; t <= to; t += 500) found.AddRange(tracker.Update(visible ? Snap(t, razor) : Snap(t), names));
            return found;
        }
        var tracker = new VisionLossTracker();
        check(Feed(tracker, 0, 4000, true).Count == 0 && Feed(tracker, 4500, 6500, false).Count == 0, "no alert before the hero has been gone 3 s");
        var gone = Feed(tracker, 7000, 12000, false);
        check(gone.Count == 1 && gone[0].Text == "razor 离开视野 · 最后在中路" && gone[0].At == 7000, "one alert per absence, with the last area");

        tracker = new VisionLossTracker();
        Feed(tracker, 0, 4000, true); Feed(tracker, 4500, 6000, false);
        check(Feed(tracker, 6500, 9000, true).Count == 0 && Feed(tracker, 9500, 11500, false).Count == 0, "a flicker at the edge of vision is not an absence");
        tracker = new VisionLossTracker();
        Feed(tracker, 0, 1500, true);
        check(Feed(tracker, 2000, 8000, false).Count == 0, "a glimpse shorter than 3 s is not announced");

        tracker = new VisionLossTracker();
        Feed(tracker, 0, 4000, true); Feed(tracker, 4500, 8000, false);   // announced at 7000
        Feed(tracker, 8500, 12000, true);
        check(Feed(tracker, 12500, 16000, false).Count == 0, "the same hero is not announced again within 20 s");
        Feed(tracker, 16500, 30000, true);
        check(Feed(tracker, 30500, 34000, false).Count == 1, "after the cooldown the next absence is announced");

        tracker = new VisionLossTracker();
        for (long t = 0; t <= 4000; t += 500) tracker.Update(Snap(t, ("npc_dota_hero_razor", 3, 200, 100, 1800), ("npc_dota_hero_lion", 2, 0, 0, 1800)), names);
        var zero = new List<PlusAlert>();
        for (long t = 4500; t <= 8000; t += 500) zero.AddRange(tracker.Update(Snap(t, ("npc_dota_hero_razor", 3, 200, 100, 0)), names));
        check(zero.Count == 1 && zero[0].Hero == "npc_dota_hero_razor", "allies never alert; a zero-vision entry is not a sighting");
        var full = new VisionLossTracker();
        for (long t = 0; t <= 9000; t += 500)
            full.Update(Snap(t, t <= 4000 ? [razor] : []) with { Mode = "full-spectator" }, names);
        var paused = new VisionLossTracker();
        Feed(paused, 0, 4000, true);
        check(full.Update(Snap(9500) with { Mode = "full-spectator" }, names).Count == 0 &&
              paused.Update(Snap(9000) with { Paused = true }, names).Count == 0, "delayed/full spectating and a paused game never alert");

        // --- The enemy panel from the scanner's live file.
        string dir = Path.Combine(Path.GetTempPath(), "dotacoach-plus-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        string file = Path.Combine(dir, "live.json");
        try {
            long now = 1_800_000_000_000;
            object Row(string hero, int? level, string?[]? items, double? age) => new { hero, level, age, items, neutral = (string?)null };
            object[] rows = [Row("npc_dota_hero_razor", 12, ["item_blink", null, null, null, null, null], 4.5), Row("npc_dota_hero_pudge", null, null, null)];
            void Write(string mode, string match, double written, bool stopped, object[] enemies, int version = 2) =>
                File.WriteAllText(file, JsonSerializer.Serialize(new { version, run = "ab12", written, stopped, mode, matchId = match, ownTeam = 2,
                    clock = 900, selecting = true, enemies }));
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: false, Board: null }, "missing file: scanner not running");
            Write("team-view", "9000000001", now / 1000.0, false, rows);
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: true, Board.Heroes: [
                { Hero: "npc_dota_hero_razor", Level: 12, Age: 4.5, Items: ["item_blink", null, null, null, null, null] }, { Level: null, Items: null }] },
                "the panel is taken from Plus team view of the spectated match");
            Write("team-view", "9000000002", now / 1000.0, false, rows);
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: true, Board: null }, "a scan of another match is ignored");
            Write("full-spectator", "9000000001", now / 1000.0, false, rows);
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: true, Board: null }, "full-spectator scans never reach the game machine");
            Write("team-view", "9000000001", now / 1000.0 - 30, false, rows);
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: false, Board: null }, "a file not updated for 10 s means the scanner stopped");
            Write("team-view", "9000000001", now / 1000.0 + 1, false, rows);
            check(ScanFeed.Poll(file, "9000000001", now).Board == null, "a future file is not fresh");
            Write("team-view", "0", now / 1000.0, false, rows);
            check(ScanFeed.Poll(file, "0", now).Board == null, "matching placeholders never identify the game");
            Write("team-view", "9000000001", now / 1000.0, false, rows);
            string sample = File.ReadAllText(file);
            foreach (string malformed in new[] { sample.Replace("\"ownTeam\":2", "\"ownTeam\":0"), sample.Replace("\"ownTeam\":2,", ""),
                sample.Replace("\"clock\":900,", ""), sample.Replace("\"clock\":900", "\"clock\":900,\"gsiAge\":3.1") }) {
                File.WriteAllText(file, malformed);
                check(ScanFeed.Poll(file, "9000000001", now).Board == null, "missing side or sample clock and stale scanner GSI cannot supply equipment");
            }
            Write("team-view", "9000000001", now / 1000.0, true, []);
            check(ScanFeed.Poll(file, "9000000001", now).Status.Text.Contains("已停止"), "a stopped scanner says so");
            Write("team-view", "9000000001", now / 1000.0, false, rows, version: 1);
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: false, Board: null, Status.Text: var old } && old.Contains("版本"),
                "an old scanner's file is not read");
            Write("team-view", "9000000001", now / 1000.0, false, [Row("npc_dota_hero_razor", 12, ["../../secrets", null, null, null, null, null], 1)]);
            check(ScanFeed.Poll(file, "9000000001", now) is { Board: null, Status.Text: var invalid } && invalid.Contains("无效"), "an invalid item name drops the panel");
            File.WriteAllText(file, "{ not json");
            check(ScanFeed.Poll(file, "9000000001", now) is { Status.Running: false, Board: null }, "a malformed file is not trusted");

            Directory.CreateDirectory(Path.Combine(dir, "icons"));
            byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13, 0x49, 0x48, 0x44, 0x52, 0, 0, 0, 88, 0, 0, 0, 64, .. new byte[40]];
            File.WriteAllBytes(Path.Combine(dir, "icons", "item_blink.png"), png);
            File.WriteAllBytes(Path.Combine(dir, "icons", "item_huge.png"), [.. png, .. new byte[Wire.MaxIconBytes]]);
            File.WriteAllText(Path.Combine(dir, "icons", "item_text.png"), "not an image");
            check(ScanFeed.Icon(file, "item_blink")?.Length == png.Length && ScanFeed.Icon(file, "item_none") == null &&
                  ScanFeed.Icon(file, "item_huge") == null && ScanFeed.Icon(file, "item_text") == null && ScanFeed.Icon(file, "../live") == null,
                "icons are read from icons/ beside the live file, by Dota name only, small PNGs only");
        }
        finally { Directory.Delete(dir, true); }

        // --- Subtitles from alerts.
        var composer = new AlertComposer();
        PlusAlert A(string text) => new(0, "vision-lost", "npc_dota_hero_razor", text);
        composer.Add([A("甲 离开视野 · 最后在中路")], 1000);
        check(composer.Next(1300, 3000) == null, "alerts gather briefly before publishing");
        composer.Add([A("乙 新装备 闪烁匕首")], 1400);
        check(composer.Next(1700, 3000) == "甲 离开视野 · 最后在中路；乙 新装备 闪烁匕首", "alerts close together share one subtitle");
        composer.Add([A("丙 离开视野 · 最后在上路")], 2500);
        check(composer.Next(3300, 3000) == "甲 离开视野 · 最后在中路；乙 新装备 闪烁匕首；丙 离开视野 · 最后在上路",
            "alerts still on screen stay in front of a new one");
        composer.Add([A("丁 离开视野 · 最后在下路")], 5000);
        check(composer.Next(5800, 3000) == "丙 离开视野 · 最后在上路；丁 离开视野 · 最后在下路", "expired alerts are not repeated");
        composer.Add(Enumerable.Range(0, 8).Select(i => A($"英雄{i} 新装备 阿哈利姆神杖、黑皇杖")), 10000);
        string? crowded = composer.Next(20000, 3000);
        check(crowded != null && crowded.Length <= AlertComposer.MaxText && crowded.EndsWith("英雄7 新装备 阿哈利姆神杖、黑皇杖"),
            "a crowded subtitle drops the oldest alerts and stays within 120 characters");
    }
}
