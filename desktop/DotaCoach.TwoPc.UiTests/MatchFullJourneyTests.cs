using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static partial class Program
{
    // Explicit opt-in only. The left lane is a synthetic Radiant participant and a synthetic Plus/scanner
    // feed, never a reconstruction from the DEM. Only the right lane comes from the real completed replay.
    private static class MatchFullJourney
    {
        private const string Hero = "npc_dota_hero_luna", Enemy = "npc_dota_hero_lion";
        private const string Playing = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", Terminal = "DOTA_GAMERULES_STATE_POST_GAME";
        private const int Observed = 1485, Learned = 1486, End = 1487; // synthetic game seconds
        private const string ReplaySource = "postgame-replay";
        private const string GoldenMatch = "9097559249";
        // Complete economic projection, independently checked against the CLI reference fixture.
        private const string GoldenTimeline = "8BCD9CA84B345E8FF2FBAA54FAA714645D9B9BDA29D637AA9C2604FD42A69D9F";

        private static void Check(bool ok, string what)
        {
            if (!ok) throw new InvalidOperationException("Match journey: " + what);
            passed++; Console.WriteLine("PASS " + what);
        }

        private static T Handler<T>(object owner, string name) where T : Delegate =>
            (owner.GetType().GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic)
                ?? throw new MissingMethodException(owner.GetType().Name, name)).CreateDelegate<T>(owner);

        private static T Named<T>(FrameworkElement scope, string name) where T : FrameworkElement =>
            scope.FindName(name) as T ?? throw new InvalidOperationException($"Missing {scope.GetType().Name}.{name}");

        private static string Hash(string path)
        {
            using var input = File.OpenRead(path);
            return Convert.ToHexString(SHA256.HashData(input));
        }

        public static async Task Run(string[] args)
        {
            if (args is not [_, var toolsFolder, var realDem, var id, var outputDir])
                throw new ArgumentException("Usage: --match-full-journey <toolsFolder> <realDem> <matchId> <output-dir>");
            string tools = Path.TrimEndingDirectorySeparator(Path.GetFullPath(toolsFolder)), dem = Path.GetFullPath(realDem);
            if (!TeamViewEvidence.RealMatch(id) || !File.Exists(dem) || !dem.EndsWith(".dem", StringComparison.OrdinalIgnoreCase) ||
                !File.Exists(Path.Combine(tools, "PostgameReplay.exe"))) throw new ArgumentException("Expected staged local tools, a completed local .dem and its match ID.");
            string bundled = Path.Combine(AppContext.BaseDirectory, "replay-tools");
            if (Directory.Exists(bundled) && !bundled.Equals(tools, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("A different replay-tools directory would take precedence over the requested staging folder.");

            string output = Path.GetFullPath(outputDir);
            Directory.CreateDirectory(output);
            string? savedTools = Environment.GetEnvironmentVariable("DOTA_COACH_REPLAY_TOOLS");
            var temp = Directory.CreateTempSubdirectory("dota-coach-SYNTHETIC-journey-");
            try {
                Environment.SetEnvironmentVariable("DOTA_COACH_REPLAY_TOOLS", tools); // process only; normal pin verification stays on
                string prefs = Path.Combine(temp.FullName, "prefs.json"), scan = Path.Combine(temp.FullName, "synthetic-scan.json");
                new LiveCoachSettings { AutomaticAdvice = false, ModelTeamKnowledge = false, EnemyPanel = false, EnemyVisionAlerts = false,
                    FoundryEnvFile = Path.Combine(temp.FullName, "absent.env"), ScanFolder = temp.FullName, ScanPython = Path.Combine(temp.FullName, "absent.exe") }.Save(prefs);
                var type = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow", true)!;
                var coach = (Window)Activator.CreateInstance(type, [null, false, prefs, Path.Combine(temp.FullName, "unused-debug")])!;
                var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                coach.Closed += (_, _) => closed.TrySetResult();
                try {
                    Set(coach, "scanFile", scan);
                    var archive = Field<MatchArchive>(coach, "matchArchive");
                    Check(archive.Root == Path.Combine(temp.FullName, "match-history") && archive.List().Count == 0,
                        "owner has a fresh private archive before any synthetic packets are sent");
                    coach.ShowInTaskbar = false; coach.ShowActivated = false;
                    coach.WindowStartupLocation = WindowStartupLocation.Manual; coach.Left = -16000; coach.Show();
                    Click(coach, "打开对局复盘窗口"); // on the home page
                    var review = coach.OwnedWindows.Cast<Window>().Single();
                    await Wait(() => Named<TextBlock>(review, "MatchStatus").Text.Contains("尚无本机档案"), "real review opens on the empty isolated catalog");
                    await Record(coach, review, archive, scan, id);
                    await VerifyReview(coach, review, archive, tools, dem, id, output, temp.FullName);
                } finally {
                    coach.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(15)); // joins the child window/job and releases the archive writer
                }
            } finally {
                Environment.SetEnvironmentVariable("DOTA_COACH_REPLAY_TOOLS", savedTools);
                if (!string.Equals(temp.Parent?.FullName, Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())), StringComparison.OrdinalIgnoreCase) ||
                    !temp.Name.StartsWith("dota-coach-SYNTHETIC-journey-", StringComparison.Ordinal)) throw new IOException("Unexpected scratch path; not deleting it.");
                temp.Delete(recursive: true);
            }
            Console.WriteLine($"WPF full match journey: {passed} checks passed. Artifacts: {output}");
        }

        private static async Task Record(Window coach, Window review, MatchArchive archive, string scan, string id)
        {
            // Ephemeral listeners. No Dota cfg installation, real scanner, pixels, game process or credentials.
            var host = await CoachHost.Start(IPAddress.Loopback, 0);
            Set(coach, "host", host); // owned and disposed by the real window
            using var link = new GameLink(host.Pair(IPAddress.Loopback));
            await link.Connect(default);
            string participantToken = Wire.Secret(), plusToken = Wire.Secret();
            await using var gsi = await GsiReceiver.Start(0, resumeToken: participantToken);
            await using var plus = await SpectatorReceiver.Start(0, plusToken);
            plus.Received = Handler<Action<SpectatorSnapshot>>(coach, "OnSpectator"); // same sink as StartSpectator, without installing a cfg
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
            long sequence = 0;
            async Task Participant(int clock, bool end = false) {
                var packet = new Dictionary<string, object> {
                    ["auth"] = new { token = participantToken },
                    ["provider"] = new { name = "Dota 2", appid = 570, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                    ["map"] = new { name = "dota", matchid = id, game_state = end ? Terminal : Playing, clock_time = clock, paused = false },
                    ["player"] = new { team_name = "radiant", activity = "playing", player_slot = 0, team_slot = 0, gold = 431, last_hits = 70 }
                };
                if (!end) {
                    packet["hero"] = new { name = Hero, alive = true, level = 12, health = 877, max_health = 1100, mana = 333, xpos = 1248, ypos = -2656 };
                    packet["items"] = new { slot0 = new { name = "item_power_treads" } };
                }
                using var response = await http.PostAsJsonAsync($"http://127.0.0.1:{gsi.Port}/gsi", packet);
                response.EnsureSuccessStatusCode();
                var lease = await link.GetTicket(default);
                long now = Environment.TickCount64;
                var sample = GameGsiSample.Read(gsi, participantOnly: true);
                Check(sample.Phase is { Team: "radiant", Hero: Hero } w && w.MatchId == id && w.Clock == clock &&
                    w.Phase == (end ? "postgame" : "playing") && (end ? sample.Player == null : sample.Player?.Stats?.Gold == 431),
                    $"authenticated synthetic raw GSI yielded a {(end ? "correlated sparse terminal" : "playing participant")} witness at {clock}s");
                await link.UploadMinuteState(new(link.SessionId!, lease.Id, ++sequence, "dota2-gsi", 1280, 720,
                    sample.Player, sample.AgeMs, sample.Phase), now, default);
            }

            await Participant(Observed);
            await Wait(() => Field<LiveMatchState>(coach, "matchState").ActiveMatch == id, "coach timer admitted the paired participant");
            Click(review, "刷新档案");
            await Wait(() => Named<TextBlock>(review, "MatchStatus").Text.Contains("正在记录"), "review recognizes the active recording");
            Check(new[] { "LocalReplay", "DownloadReplay", "ReplayMode", "CompareMode" }.All(n => !Named<Control>(review, n).IsEnabled) &&
                !archive.IsSealed(id), "both import actions and all-map modes are locked before the witnessed end");

            await Participant(Learned);
            // Publish the 24:45 team evidence only AFTER the participant is at 24:46. Clock != KnownAt.
            using var sight = await http.PostAsJsonAsync($"http://127.0.0.1:{plus.Port}/spectator", new {
                auth = new { token = plusToken }, player = new { team_name = "spectator" }, hero = new { id = 0 }, buildings = new { radiant = new { } },
                map = new { matchid = id, clock_time = Observed, game_state = Playing, paused = false },
                minimap = new { h = new { unitname = Enemy, team = 3, xpos = -2500, ypos = 1200, visionrange = 1800 },
                    w = new { unitname = "npc_dota_sentry_wards", team = 3, xpos = 2500, ypos = -1200, visionrange = 0 } }
            });
            sight.EnsureSuccessStatusCode();
            File.WriteAllText(scan, JsonSerializer.Serialize(new { version = 2, run = "synthetic-journey", mode = "team-view", stopped = false,
                matchId = id, ownTeam = 2, clock = Observed, gsiAge = 0, written = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
                enemies = new[] { new { hero = Enemy, level = 11, age = 0, items = new string?[] { "item_blink", null, null, null, null, null },
                    wards = new[] { new { first = Observed - .3, clock = Observed - .2, observer = 1, sentry = 0, unknown = 0 },
                        new { first = (double)Observed, clock = (double)Observed, observer = 0, sentry = 0, unknown = 0 } } } } }));
            await Wait(() => archive.Read(id).Any(f => f.Kind == "ward-estimate" && f.KnownAt == Learned &&
                f.Data.GetProperty("wards").GetArrayLength() > 0), "native tracker produced a located estimate from synthetic sight and a charge drop");
            var facts = archive.Read(id);
            Check(new[] { ("team-vision", "ward-observed"), ("team-vision", "equipment"), ("inference", "ward-estimate") }
                .All(k => facts.Any(f => (f.Source, f.Kind) == k && f.Clock == Observed && f.KnownAt == Learned)) &&
                facts.Count(f => f.Kind == "ward-charges") == 2 && facts.Any(f => f.Kind == "ward-alert"), "observed ward, equipment and inference retained separate dated provenance");
            await Participant(End, end: true);
            await Wait(() => archive.IsSealed(id), "coach sealed from the same-match, same-session terminal frame");
            Check(archive.Read(id)[^1] is { Kind: "phase", Source: "own-gsi", Clock: End } f && f.Data.GetProperty("phase").GetString() == "postgame" &&
                archive.List() is [{ Ended: true, HasReplay: false }] && host.Mailbox.Latest()?.Jpeg.Length == 0, "seal precedes replay and used no game image");
            await link.End(default);
        }

        // Read the public native controls, not a duplicate timeline/formatter. Read all visible-page row
        // details through selection, restoring it afterwards (the synthetic lane fits on one page).
        private static string[] Details(FrameworkElement pane)
        {
            var list = Named<ListBox>(pane, "Events"); var detail = Named<TextBox>(pane, "DetailText");
            int selected = list.SelectedIndex;
            var rows = new string[list.Items.Count];
            try {
                for (int i = 0; i < rows.Length; i++) { list.SelectedIndex = i; rows[i] = detail.Text; }
                return rows;
            } finally { list.SelectedIndex = selected; }
        }
        private static string Lane(FrameworkElement pane) => string.Join('\n', new[] { Named<TextBlock>(pane, "PageText").Text,
            Named<TextBox>(pane, "SnapshotText").Text, Named<TextBox>(pane, "DetailText").Text }.Concat(Details(pane)));

        private static string WorldPoint(JsonElement d) =>
            FormattableString.Invariant($"({d.GetProperty("x").GetDouble():0.###}, {d.GetProperty("y").GetDouble():0.###})");

        private static async Task VerifyReview(Window coach, Window w, MatchArchive archive, string tools, string dem, string id, string output, string scratch)
        {
            var known = Named<FrameworkElement>(w, "KnownPane"); var replay = Named<FrameworkElement>(w, "ReplayPane");
            var scrubber = Named<Slider>(w, "Scrubber"); var compare = Named<RadioButton>(w, "CompareMode");
            var local = Named<Button>(w, "LocalReplay"); var filter = Named<ComboBox>(w, "Filter"); var notice = Named<TextBox>(w, "Notice");
            void Seek(double t) {
                scrubber.Value = t;
                if (scrubber.Value != t) throw new InvalidOperationException($"Native cursor clamped {t} to {scrubber.Value}.");
            }
            string LiveAt(double t) { Seek(t); return Lane(known); }
            Click(w, "刷新档案"); await Wait(() => local.IsEnabled, "verified end unlocks the native local import action");
            Check(!compare.IsEnabled && Named<RadioButton>(w, "KnownMode").IsChecked == true, "the sealed match has no replay yet");
            Named<Expander>(known, "SnapshotBox").IsExpanded = true;
            var before = new[] { 0d, Observed, Learned, End }.ToDictionary(t => t, LiveAt);
            Check(before[Observed].Contains("生命 877/1100") && before[Observed].Contains("item_power_treads") && !before[Observed].Contains("item_blink") &&
                before[Learned].Contains("item_blink") && before[Learned].Contains("(2500, -1200)") && before[Learned].Contains("候选") &&
                before[Learned].Contains("24:45") && before[Learned].Contains("24:46"), "native known rows and snapshots reveal the delayed data only at its knowledge clock");
            string dir = Path.Combine(archive.Root, id), live = Path.Combine(dir, "live.jsonl"), sidecar = Path.Combine(dir, "replay.json");
            string beforeHash = Hash(live);
            Check(!File.Exists(sidecar), "no cached timeline in the isolated archive");

            // Bypass only OpenFileDialog. Mirror StartJobAsync's ownership of cancellation/progress/close,
            // then run its actual continuation with this window's real ReviewStore and the production pin.
            var cancel = CancellationTokenSource.CreateLinkedTokenSource(Field<CancellationTokenSource>(w, "lifetime").Token);
            Set(w, "jobStop", cancel); Handler<Action>(w, "Controls")();
            var elapsed = Stopwatch.StartNew();
            var job = Handler<Func<string, string?, CancellationTokenSource, Task>>(w, "RunJobAsync")(id, dem, cancel);
            Set(w, "job", job);
            Check(!local.IsEnabled && Named<Button>(w, "CancelReplay").IsVisible, "real local review job is in flight");
            await job; elapsed.Stop();
            Check(notice.Text.Contains("已核验并导入") && compare.IsEnabled && File.Exists(sidecar), "pinned worker and atomic native import: " + notice.Text);
            var liveOnly = archive.Read(id); var all = archive.Read(id, includeReplay: true);
            var real = all.Where(f => f.Source == ReplaySource).ToArray();
            Check(Hash(live) == beforeHash && liveOnly.All(f => f.Source != ReplaySource) && all.Count == liveOnly.Count + real.Length &&
                before.All(kv => LiveAt(kv.Key) == kv.Value) && !replay.IsVisible && Named<ListBox>(replay, "Events").Items.Count == 0,
                "sealed live bytes, live-only reads, and known-only UI at all four cursors remain unchanged");
            string demHash = Hash(dem), replayHash = Hash(sidecar);
            using (var doc = JsonDocument.Parse(File.ReadAllBytes(sidecar))) {
                Check(doc.RootElement.GetProperty("replaySha256").GetString()!.Equals(demHash, StringComparison.OrdinalIgnoreCase) &&
                    real[^1].Kind == "replay-end" && real.All(f => f.MatchId == id), "import identifies this DEM and contains the same match's terminal replay evidence");
            }
            if (id == GoldenMatch) Check(replayHash == GoldenTimeline, "entire new timeline matches the reference fixture SHA-256");

            compare.IsChecked = true;
            await Wait(() => compare.IsEnabled && replay.IsVisible && Named<ListBox>(replay, "Events").Items.Count != 0, "comparison loads both native panes");
            Check(before.All(kv => LiveAt(kv.Key) == kv.Value), "comparison does not backfill even one known row, selected detail or snapshot");
            var wards = real.Where(f => f.Kind == "replay-ward").ToArray();
            string WardId(MatchFact f) => f.Data.GetProperty("wardId").GetString()!;
            string ActionOf(MatchFact f) => f.Data.GetProperty("action").GetString()!;
            int? TeamOf(MatchFact f) => f.Data.GetProperty("team") is { ValueKind: JsonValueKind.Number } side ? side.GetInt32() : null;
            var removed = wards.Where(f => ActionOf(f) == "removed").ToDictionary(WardId);
            var fog = wards.FirstOrDefault(f => ActionOf(f) == "placed" && f.Data.GetProperty("type").GetString() == "observer" &&
                f.Data.GetProperty("visibleRadiant").ValueKind == JsonValueKind.False && f.Data.GetProperty("visibleDire").ValueKind == JsonValueKind.True &&
                TeamOf(f) == 3 && removed.ContainsKey(WardId(f)))
                ?? throw new InvalidDataException("This journey needs a Dire observer hidden to Radiant at placement with a recorded removal.");
            filter.SelectedItem = "眼位 · 录像记录";
            Check(filter.SelectedIndex >= 0, "native replay-ward filter selected");
            foreach (var ev in new[] { fog, removed[WardId(fog)] }) {
                bool Shown() => Details(replay).Any(d => d.Contains(WardId(ev)) && d.Contains(ev.Data.GetProperty("evidence").GetString()!));
                double after = Math.Ceiling(ev.Clock), prior = after - 1; // reachable whole-second ticks straddling the actual event
                Seek(prior); bool early = Shown(); Seek(after);
                Check(!early && Shown() && Named<ListBox>(known, "Events").Items.Count == 0, $"ward {WardId(ev)} {ActionOf(ev)} at {ev.Clock}s: absent at {prior}s, replay-only at {after}s");
                if (ev == fog) Check(Details(replay).Any(d => d.Contains(WardId(fog)) && d.Contains(WorldPoint(fog.Data)) && d.Contains("天辉 否 / 夜魇 是")),
                    "the native placement detail shows its DEM coordinates and the opposing visibility bits");
                Seek(prior); Check(!Shown(), $"rewind hides that {ActionOf(ev)} evidence again");
            }

            filter.SelectedIndex = 0; Seek(Learned);
            var heroes = real.Where(f => f.Kind == "replay-hero").ToArray();
            int farmRows = heroes.Count(f => f.Data.TryGetProperty("economy", out var e) &&
                e.TryGetProperty("netWorth", out _) && e.TryGetProperty("lastHits", out _) && e.TryGetProperty("currentXP", out _));
            if (id == GoldenMatch) Check(farmRows == 3170 && heroes.Length - farmRows == 250 &&
                heroes.Where(f => f.Data.GetProperty("hero").ValueKind == JsonValueKind.Null).All(f => !f.Data.TryGetProperty("economy", out _)),
                "every resolved 5-second hero sample includes independently joined farming counters; preselection stays unknown");
            var frames = heroes.GroupBy(f => (f.Clock, Tick: f.Data.GetProperty("tick").GetInt64())).ToArray();
            var frame = frames.Last(g => g.Key.Clock <= scrubber.Value);
            var bySide = frame.GroupBy(f => TeamOf(f) ?? 0).ToDictionary(g => g.Key, g => g.Count());
            var picker = Named<ComboBox>(replay, "HeroSlot"); var roster = Named<TextBox>(replay, "SnapshotText");
            Check(bySide.GetValueOrDefault(2) == 5 && bySide.GetValueOrDefault(3) == 5 && frame.Select(f => f.Data.GetProperty("playerId").GetInt32()).Distinct().Count() == 10 &&
                Named<TextBlock>(replay, "FrameCaption").Text.Contains($"tick {frame.Key.Tick}"), "at the shared cursor, the replay pane shows the latest eligible 5+5 sample");
            foreach (var f in frame) {
                var d = f.Data; int slot = d.GetProperty("playerId").GetInt32(); picker.SelectedIndex = slot + 1;
                var items = d.GetProperty("items").EnumerateArray().Select(s => s.GetString()).Where(s => s?.StartsWith("item_", StringComparison.Ordinal) == true);
                Check(roster.Text.Contains($"槽 {slot}　") && roster.Text.Contains(d.GetProperty("team").GetInt32() == 2 ? "天辉" : "夜魇") &&
                    roster.Text.Contains(WorldPoint(d)) && items.Any() && items.All(item => roster.Text.Contains(item!)), $"replay slot {slot}: side, world position and held items match its DEM sample");
                if (id == GoldenMatch) {
                    var economy = d.GetProperty("economy");
                    Check(roster.Text.Contains($"净资产 {economy.GetProperty("netWorth").GetInt32()}") &&
                        roster.Text.Contains($"补刀/反补 {economy.GetProperty("lastHits").GetInt32()}/{economy.GetProperty("denies").GetInt32()}"),
                        $"replay slot {slot}: displayed net worth and last hits/denies match this slot at the cursor");
                }
            }

            // Illustration only: annotate the real WPF headings so this cannot be mistaken for real live telemetry.
            picker.SelectedIndex = 6; // one Dire inventory, all map dots, and the most recent replay ward
            w.Width = 1280; w.Height = 880;
            foreach (var (pane, origin) in new[] { (known, "team-vision / ward-observed"), (replay, "postgame-replay / replay-ward") }) {
                int i = Array.FindIndex(Details(pane), r => r.Contains(origin));
                if (i >= 0) { var list = Named<ListBox>(pane, "Events"); list.SelectedIndex = i; list.ScrollIntoView(list.SelectedItem); }
            }
            Named<TextBlock>(known, "Heading").Text += "　·　合成示例（非实战采集）";
            Named<TextBlock>(replay, "Heading").Text += "　·　真实已完成 DEM";
            notice.Text = "隔离测试 / ISOLATED TEST    左：模拟玩家、队伍视野及眼位推测。    右：本机解析的真实赛后录像。";
            string png = Path.Combine(scratch, "comparison.png");
            Render(w, png);
            var replayOnly = Named<RadioButton>(w, "ReplayMode"); replayOnly.IsChecked = true;
            await Wait(() => replayOnly.IsEnabled && !known.IsVisible && Named<ListBox>(known, "Events").Items.Count == 0 &&
                Named<TextBlock>(replay, "FrameCaption").Text.Contains($"tick {frame.Key.Tick}"), "replay-only retains the selected sample and clears the known pane");
            Named<RadioButton>(w, "KnownMode").IsChecked = true;
            await Wait(() => compare.IsEnabled && !replay.IsVisible, "returned to the native known-only mode");
            Check(LiveAt(Learned) == before[Learned] && roster.Text.Length == 0 && Named<ListBox>(replay, "Events").Items.Count == 0,
                "returning to known-only clears the hidden replay and restores the unchanged known view");

            var counts = real.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count());
            int calls = Field<int>(coach, "modelCalls");
            Check(calls == 0 && Field<CloudCoach?>(coach, "cloud") == null, "no model configured or called");
            var report = new {
                matchId = id, syntheticLive = new { label = "Illustrative only; NOT observed in this match", source = "authenticated loopback GSI / paired TLS / synthetic Plus and scanner",
                    events = liveOnly.Count, counts = liveOnly.GroupBy(f => f.Kind).ToDictionary(g => g.Key, g => g.Count()), observedClock = Observed, knownAt = Learned,
                    witnessedPostgameClock = End, bytes = new FileInfo(live).Length, sha256Before = beforeHash, sha256After = Hash(live) },
                realReplay = new { tools = Path.GetRelativePath(Environment.CurrentDirectory, tools), manifestSha256 = Hash(Path.Combine(tools, "sha256.json")),
                    dem = Path.GetRelativePath(Environment.CurrentDirectory, dem), demBytes = new FileInfo(dem).Length, demSha256 = demHash,
                    timelineBytes = new FileInfo(sidecar).Length, timelineSha256 = replayHash, events = real.Length, counts, heroFrames = frames.Length, farmRows,
                    wardPlaced = wards.Count(f => ActionOf(f) == "placed"), wardRemoved = removed.Count,
                    hiddenToOpponentAtPlacement = wards.Count(f => ActionOf(f) == "placed" && TeamOf(f) is int side &&
                        f.Data.GetProperty(side == 2 ? "visibleDire" : "visibleRadiant").ValueKind == JsonValueKind.False),
                    startClock = real[0].Clock, endClock = real[^1].Clock,
                    jobSeconds = Math.Round(elapsed.Elapsed.TotalSeconds, 3) },
                nativeUi = new { cursor = Learned, sampleClock = frame.Key.Clock, tick = frame.Key.Tick, radiantSlots = bySide[2], direSlots = bySide[3],
                    foggedDireObserver = WardId(fog), placedClock = fog.Clock, removedClock = removed[WardId(fog)].Clock,
                    paidModelCalls = calls, screenshot = File.Exists(png) ? "comparison.png" : null }
            };
            if (File.Exists(png)) File.Copy(png, Path.Combine(output, "comparison.png"), overwrite: true);
            File.WriteAllText(Path.Combine(output, "journey.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }));
            Console.WriteLine($"SYNTHETIC live: {liveOnly.Count} facts. REAL DEM: {real.Length} events, {frames.Length} hero frames, {wards.Length} ward events, {elapsed.Elapsed.TotalSeconds:F2}s check/parse/import.");
        }
    }
}
