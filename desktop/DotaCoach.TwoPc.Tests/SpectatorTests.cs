using DotaCoach.TwoPc;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

internal static class SpectatorTests
{
    public static async Task Run(Action<bool, string> check)
    {
        void Reject(Action action, string label) {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException or IOException) { rejected = true; }
            check(rejected, label);
        }
        string root = Path.Combine(Path.GetTempPath(), "dotacoach-spectator-" + Guid.NewGuid().ToString("N"));
        try {
            // --- Steam launch options: file reads only.
            string vdf = """
                "UserLocalConfigStore"
                {
                    "Software" { "Valve" { "Steam" {
                        "apps"
                        {
                            "730" { "LaunchOptions" "-novid -gamestateintegration" }
                            "570"
                            {
                                "LastPlayed" "1794800000"
                                "LaunchOptions" "-perfectworld -console \"+exec x\""
                            }
                        }
                    } } }
                }
                """;
            check(SteamLaunch.ReadLaunchOptions(vdf) == "-perfectworld -console \"+exec x\"", "Steam VDF finds Dota launch options, not another app's");
            check(SteamLaunch.ReadLaunchOptions(vdf.Replace("\"570\"", "\"571\"")) == null, "missing Dota entry is not invented");
            check(!SteamLaunch.HasFlag("-perfectworld") && SteamLaunch.HasFlag("-perfectworld  -GameStateIntegration"), "GSI flag detected as a whole option");
            check(!SteamLaunch.HasFlag("-gamestateintegrationx"), "GSI flag prefix is not accepted");
            check(SteamLaunch.WithGsiFlag("-perfectworld") == "-perfectworld -gamestateintegration" &&
                SteamLaunch.WithGsiFlag("-perfectworld -gamestateintegration") == "-perfectworld -gamestateintegration" &&
                SteamLaunch.WithGsiFlag(null) == "-gamestateintegration", "copied launch options keep the user's own and add the flag once");
            string steam = Path.Combine(root, "steam");
            Directory.CreateDirectory(Path.Combine(steam, "userdata", "111", "config"));
            Directory.CreateDirectory(Path.Combine(steam, "userdata", "222", "config"));
            File.WriteAllText(Path.Combine(steam, "userdata", "111", "config", "localconfig.vdf"), vdf);
            File.SetLastWriteTimeUtc(Path.Combine(steam, "userdata", "111", "config", "localconfig.vdf"), DateTime.UtcNow.AddDays(-2));
            File.WriteAllText(Path.Combine(steam, "userdata", "222", "config", "localconfig.vdf"),
                vdf.Replace("-console \\\"+exec x\\\"", "-gamestateintegration"));
            var inspected = SteamLaunch.Inspect(steam);
            check(inspected is { Found: true, HasGsiFlag: true, ActiveAccount: false } && inspected.Options == "-perfectworld -gamestateintegration",
                "without an active Steam login the most recently used account is read");
            check(!SteamLaunch.Inspect(Path.Combine(root, "no-steam")).Found, "absent Steam reports not found instead of guessing");

            // --- Spectator payload parsing.
            object Unit(string name, int team, double x, double y, string image = "minimap_enemyicon", int vision = 1800) =>
                new { xpos = x, ypos = y, image, team, yaw = 90, unitname = name, visionrange = vision };
            var minimap = new Dictionary<string, object> {
                ["o0"] = Unit("npc_dota_hero_troll_warlord", 3, 3592, -6240, "minimap_herocircle_self"),
                ["o1"] = Unit("npc_dota_hero_windrunner", 3, 100, 200, "minimap_herocircle"),
                ["o2"] = Unit("npc_dota_hero_razor", 2, -3962, 461),
                ["o3"] = Unit("npc_dota_neutral_alpha_wolf", 4, 500, 500, "javascript:alert(1)"),
                ["o4"] = Unit("<script>", 2, 1, 1),
                ["o5"] = new { xpos = "NaN", ypos = 1, team = 2, unitname = "npc_dota_hero_lina" },
            };
            string Payload(object player, object hero, object? map, object? units = null, object? buildings = null, string token = "") =>
                JsonSerializer.Serialize(new { auth = new { token }, provider = new { name = "Dota 2" }, map, player, hero,
                    items = new { }, minimap = units, buildings,
                    events = new object[] { new { game_time = 2400, event_type = "bounty_rune_pickup", team_gold = 470 }, new { event_type = "Bad Type!" } } });
            var teamMap = new { matchid = "0", clock_time = 2350, game_state = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", paused = false };
            var team = SpectatorGsi.Parse(JsonDocument.Parse(Payload(new { team_name = "spectator", player_slot = 11 }, new { id = 0 },
                teamMap, minimap, new { dire = new { dota_badguys_fort = new { health = 4500 } } })).RootElement, 42);
            check(team is { Mode: "team-view", OwnTeam: 3, Clock: 2350, MatchId: null, GroupedPlayers: 0, ReceivedAt: 42, Running: true },
                "Plus live layout is team-view with the friend's team from the self circle; matchid 0 is not an ID");
            check(team.Minimap.Length == 5 && team.Heroes.Count() == 3, "minimap units parsed; non-finite coordinates dropped");
            check(team.Minimap.Any(u => u.Name == "?") && team.Minimap.Single(u => u.Team == 4).Image == null,
                "unknown unit names and non-icon images are not passed through");
            check(team.EventTypes.SequenceEqual(["bounty_rune_pickup", "?"]) && team.BuildingTeams.SequenceEqual(["dire"]), "event types and building teams summarized");
            object Players(int from) => Enumerable.Range(from, 5).ToDictionary(i => $"player{i}", i => (object)new { name = $"npc_dota_hero_{i}" });
            var full = SpectatorGsi.Parse(JsonDocument.Parse(Payload(new { team2 = Players(0), team3 = Players(5) },
                new { team2 = Players(0), team3 = Players(5) }, teamMap with { matchid = "8123456789" })).RootElement, 0);
            check(full is { Mode: "full-spectator", GroupedPlayers: 10, MatchId: "8123456789" }, "delayed spectating with 10-player groups is a separate mode");
            var playing = SpectatorGsi.Parse(JsonDocument.Parse(Payload(new { team_name = "radiant" }, new { name = "npc_dota_hero_luna" }, teamMap)).RootElement, 0);
            check(playing is { Mode: "player", OwnTeam: 2 }, "this machine playing a match is not mistaken for spectating");
            check(SpectatorGsi.Parse(JsonDocument.Parse(Payload(new { }, new { }, null)).RootElement, 0) is { Mode: "menu", Running: false }, "menu state");
            var flood = Enumerable.Range(0, SpectatorGsi.MaxUnits + 1).ToDictionary(i => $"o{i}", i => Unit("npc_dota_creep_badguys_melee", 3, i, i));
            Reject(() => SpectatorGsi.Parse(JsonDocument.Parse(Payload(new { }, new { }, teamMap, flood)).RootElement, 0), "unbounded minimap rejected");

            // --- Delay tracker on one monotonic clock.
            OwnState Own(double clock, string match = "8123456789") => new(match, "npc_dota_hero_luna", "radiant", clock, true, 1000, 300, 0, 0, [], []);
            SpectatorSnapshot Spec(double clock, long at, string mode = "team-view", bool paused = false, string? match = null) =>
                new(at, mode, clock, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", paused, match, 2, mode == "full-spectator" ? 10 : 0, [], [], []);
            var tracker = new PlusDelayTracker();
            check(tracker.Stats().Samples == 0 && tracker.Stats().Note.Contains("游戏机"), "no samples before the game side reports");
            for (int t = 600; t < 700; t++) {
                // Game side seen every 33 ms frame; GSI changes once per second with 0–200 ms throttle jitter.
                tracker.Game(Own(t), t * 1000L + (t * 37 % 200));
                tracker.Game(Own(t), t * 1000L + (t * 37 % 200)); // Repeated frames carrying the same packet.
                tracker.Spectator(Spec(t, t * 1000L + 1500 + (t * 53 % 100)));
            }
            var live = tracker.Stats();
            check(live.Samples == 99 && Math.Abs(live.MedianMs!.Value - 1450) < 120 && live.Note.Contains("Plus"), $"live delay recovered ({live.MedianMs:F0} ms)");
            tracker.Spectator(Spec(700, 800_000, paused: true));
            check(tracker.Stats().Samples == 99, "paused spectator packets add no samples");
            for (int t = 580; t < 620; t++) tracker.Spectator(Spec(t, t * 1000L + 120_000, "full-spectator"));
            var delayed = tracker.Stats();
            check(delayed.Samples == 19 && Math.Abs(delayed.MedianMs!.Value - 120_000) < 300 && delayed.Note.Contains("完整"),
                "switching to delayed spectating starts a separate series");
            tracker.Spectator(Spec(601, 1, "full-spectator", match: "999"));
            check(tracker.Stats() is { Samples: 0 } mismatch && mismatch.Note.Contains("不是"), "a different match is reported, not averaged in");

            // --- Real loopback receiver.
            string token = Wire.Secret();
            SpectatorSnapshot? seen = null;
            await using (var receiver = await SpectatorReceiver.Start(0, token)) {
                receiver.Received = s => seen = s;
                check(receiver.Config.Contains($"http://127.0.0.1:{receiver.Port}/spectator") && receiver.Config.Contains("\"minimap\" \"1\"") &&
                    receiver.Config.Contains("\"throttle\" \"0.1\""), "spectator config requests minimap with fine timing");
                using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
                string body = Payload(new { team_name = "spectator" }, new { id = 0 }, teamMap, minimap, token: token);
                check((await http.PostAsync("/spectator", new StringContent(body.Replace(token, Wire.Secret())))).StatusCode == HttpStatusCode.Unauthorized,
                    "spectator receiver requires its token");
                var big = Enumerable.Range(0, 1500).ToDictionary(i => $"o{i}", i => Unit("npc_dota_creep_badguys_melee", 3, i, i));
                string large = Payload(new { team_name = "spectator" }, new { id = 0 }, teamMap, big, token: token);
                check(large.Length > 65536 && (await http.PostAsync("/spectator", new StringContent(large))).IsSuccessStatusCode,
                    "large spectator minimap payloads are accepted");
                check(receiver.Status() is { Packets: 1, Rejected: 1, Latest.Mode: "team-view" } && seen?.Minimap.Length == 1500,
                    "receiver keeps the typed snapshot and notifies observers");
                var hostile = new HttpRequestMessage(HttpMethod.Post, "/spectator") { Content = new StringContent(large) };
                hostile.Headers.Add("Origin", "https://evil.example");
                check((await http.SendAsync(hostile)).StatusCode == HttpStatusCode.Unauthorized, "browser-origin requests rejected");
            }

            // --- Game receiver now asks for minimap and tolerates its size.
            await using (var gsi = await GsiReceiver.Start(0)) {
                string secret = Regex.Match(gsi.Config, "\"token\" \"([A-F0-9]+)\"").Groups[1].Value;
                check(gsi.Config.Contains("\"minimap\" \"1\"") && gsi.Config.Contains("\"throttle\" \"0.2\""), "game GSI config tests player-mode minimap");
                using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{gsi.Port}") };
                string own = JsonSerializer.Serialize(new { auth = new { token = secret },
                    map = new { matchid = "8123456789", clock_time = 700 }, player = new { team_name = "radiant" },
                    hero = new { name = "npc_dota_hero_luna", alive = true, health = 1000, mana = 300, xpos = 1, ypos = 2 },
                    minimap = Enumerable.Range(0, 1200).ToDictionary(i => $"o{i}", i => Unit("npc_dota_creep_goodguys_melee", 2, i, i)) });
                check(own.Length > 65536 && (await http.PostAsync("/gsi", new StringContent(own))).IsSuccessStatusCode &&
                    gsi.Latest().Player?.Hero == "npc_dota_hero_luna" && gsi.LastDiagnostics is { MinimapUnits: 1200, OwnStateAccepted: true },
                    "own state still parsed with a large minimap; only its unit count is kept");
            }

            // --- Installer per role.
            string install = Path.Combine(root, "dota"), state = Path.Combine(root, "state");
            Directory.CreateDirectory(Path.Combine(install, "game", "dota"));
            var first = GsiSetup.Install(install, "spectator-config", state, GsiSetup.SpectatorConfigName);
            check(first.Changed && first.Path.EndsWith(GsiSetup.SpectatorConfigName) && GsiSetup.WasInstalled(state, GsiSetup.SpectatorConfigName) &&
                !GsiSetup.WasInstalled(state, GsiSetup.GameConfigName), "spectator cfg written with its own receipt");
            check(!GsiSetup.Install(install, "spectator-config", state, GsiSetup.SpectatorConfigName).Changed, "unchanged cfg needs no Dota restart");
            check(GsiSetup.Install(install, "spectator-config-v2", state, GsiSetup.SpectatorConfigName).Changed, "owned cfg updates and asks for a restart");
            File.WriteAllText(first.Path, "user edit");
            Reject(() => GsiSetup.Install(install, "spectator-config-v3", state, GsiSetup.SpectatorConfigName), "user-edited cfg preserved");
            Reject(() => GsiSetup.Install(install, "x", state, "autoexec.cfg"), "installer only writes its two known cfg names");
            if (OperatingSystem.IsWindows()) {
                string identity = GsiSetup.Token(state);
                check(GsiSetup.Token(state) == identity && !File.ReadAllText(Path.Combine(state, "gsi-identity.dpapi")).Contains(identity),
                    "coach GSI identity persists through DPAPI");
            }

            // --- Debug: typed, rate-limited, coach only.
            var recorder = new DebugRecorder(Path.Combine(root, "debug"), "coach", "test");
            recorder.Spectator(team); recorder.Spectator(team); recorder.SpectatorDelay(live); recorder.SpectatorDelay(live);
            var gameRecorder = new DebugRecorder(Path.Combine(root, "debug"), "game", "test");
            gameRecorder.Spectator(team);
            await recorder.DisposeAsync(); await gameRecorder.DisposeAsync();
            string events = File.ReadAllText(Path.Combine(recorder.DirectoryPath, "events.jsonl"));
            check(Regex.Matches(events, "spectator-snapshot").Count == 1 && Regex.Matches(events, "spectator-delay").Count == 1 &&
                events.Contains("npc_dota_hero_troll_warlord") && !events.Contains("auth"), "spectator debug is rate-limited and typed");
            check(!File.ReadAllText(Path.Combine(gameRecorder.DirectoryPath, "events.jsonl")).Contains("spectator"), "game role never records spectator data");
        } finally {
            try { Directory.Delete(root, recursive: true); } catch { /* Best-effort temp cleanup. */ }
        }
    }
}
