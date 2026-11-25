using DotaCoach.TwoPc;

internal static class OverlaySettingsTests
{
    public static void Run(Action<bool, string> check)
    {
        var defaults = new GameOverlaySettings();
        check(defaults is { ShowAdvice: true, ShowAlerts: true, ShowTitle: true, X: 50, Y: 10, Size: 100, Width: 70, TextColor: "#FFD700" },
            "default caption: every kind shown, centred near the top, gold text");
        check(defaults is { ShowEnemies: true, EnemyY: 40, EnemySize: 100, EnemyOpacity: 75 } && defaults.EnemyTop(720, 120) == 240 &&
              (defaults with { EnemyY = 100 }).EnemyTop(720, 120) == 600, "the enemy panel shows by default at the left, a little above the middle");
        check(defaults.Place(1280, 720, 400, 100) == (440, 62), "default box sits centred, a tenth of the free height down");
        check(defaults is { MapMarkSize: 100, MapMarkOpacity: 60 }, "minimap marks are see-through by default (the user could not read the map under them)");
        var corner = defaults with { X = 0, Y = 100 };
        check(corner.Place(1280, 720, 400, 100) == (0, 620) && (defaults with { X = 100, Y = 0 }).Place(1280, 720, 400, 100) == (880, 0),
            "0 and 100 hug the edges, so a growing box never leaves the game area");
        check(corner.Place(1280, 720, 1400, 800) == (0, 0), "a box larger than the game starts at its corner");

        var quiet = defaults with { ShowAlerts = false };
        check(!quiet.Shows("local-vision-observation") && quiet.Shows("visible-farming-advice") && quiet.Shows("manual-link-test"),
            "hiding vision-loss alerts leaves AI advice and link tests");
        var noAdvice = defaults with { ShowAdvice = false };
        check(!noAdvice.Shows("visible-farming-advice") && !noAdvice.Shows("experimental-bot-advice") && noAdvice.Shows("local-vision-observation"),
            "hiding AI advice leaves vision-loss alerts");
        check(GameOverlaySettings.Rgb("#0A2540") == (10, 37, 64), "colours are #RRGGBB");

        foreach (var bad in new[] { defaults with { X = 101 }, defaults with { Y = -1 }, defaults with { Size = 40 }, defaults with { Width = 99 },
                     defaults with { TextOpacity = 5 }, defaults with { BackgroundOpacity = 120 }, defaults with { TextColor = "gold" },
                     defaults with { BackgroundColor = "#12345" }, defaults with { TitleColor = null! }, defaults with { EnemyY = 101 },
                     defaults with { EnemySize = 251 }, defaults with { EnemyOpacity = 10 }, defaults with { MapMarkSize = 40 },
                     defaults with { MapMarkSize = 151 }, defaults with { MapMarkOpacity = 10 }, defaults with { MapMarkOpacity = 101 } }) {
            bool rejected = false;
            try { bad.Validate(); } catch (ArgumentException) { rejected = true; }
            check(rejected, $"invalid overlay setting rejected: {bad}");
        }

        string dir = Path.Combine(Path.GetTempPath(), "dotacoach-overlay-" + Guid.NewGuid().ToString("N"));
        string file = Path.Combine(dir, "game-overlay.json");
        try {
            check(GameOverlaySettings.Load(file) == defaults, "no file: defaults");
            var chosen = defaults with { ShowAlerts = false, X = 0, Y = 72, Size = 140, Width = 40, TextColor = "#FFFFFF",
                BackgroundColor = "#000000", TextOpacity = 90, BackgroundOpacity = 0, ShowEnemies = false, EnemyY = 5, EnemySize = 60, EnemyOpacity = 40,
                MapMarkSize = 70, MapMarkOpacity = 35 };
            chosen.Save(file);
            check(GameOverlaySettings.Load(file) == chosen, "saved style is loaded back unchanged");
            File.WriteAllText(file, "{\"x\":0,\"textColor\":\"#123ABC\"}");
            check(GameOverlaySettings.Load(file) == defaults with { X = 0, TextColor = "#123ABC" }, "missing fields keep their defaults");
            File.WriteAllText(file, "{\"size\":999}");
            check(GameOverlaySettings.Load(file) == defaults, "an out-of-range file falls back to defaults");
            File.WriteAllText(file, "{ not json");
            check(GameOverlaySettings.Load(file) == defaults, "a malformed file falls back to defaults");
        }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir, true); }
    }
}
