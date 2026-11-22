using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotaCoach.TwoPc;

// dotnet run --project desktop/DotaCoach.MatchReview.Tests [-c Release] [-- --screenshot <new.png> --bundle <staged folder>]
// Only synthetic archives/worker children. No network, capture, Steam, installed scanner, DEM, or paid model.
internal static class ReviewChecks
{
    private const string Ended = "7000000001", Interrupted = "7000000002";
    private static int checks;
    private static void Check(bool okay, string message)
    {
        if (!okay) throw new Exception(message);
        checks++; Console.WriteLine("PASS " + message);
    }

    [STAThread] public static int Main(string[] args)
    {
        if (args.Contains("--match-id")) return WorkerChecks.Child(args);
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            var root = Directory.CreateTempSubdirectory("coach review fixture ");
            int code = 0;
            try {
                TimelineEdges(); await WindowAndRestart(root.FullName, args);
                await RecoveryChecks.Run(root.FullName, Check);
                await StaleReads(root.FullName); await WorkerChecks.Run(root.FullName, Check);
                if (Option(args, "--bundle") is { } tools) await WorkerChecks.PackagedRejection(root.FullName, tools, Check);
                Console.WriteLine($"{checks} synthetic review checks passed.");
            } catch (Exception e) { Console.Error.WriteLine(e); code = 1; }
            try {
                foreach (var view in app.Windows.OfType<MatchReviewWindow>().ToArray()) await view.StopAsync();
                foreach (var owner in app.Windows.OfType<CoachWindow>().ToArray()) {
                    owner.Close(); await Wait(() => !owner.IsVisible, "release fixture archive");
                }
                root.Delete(recursive: true);
            } catch (Exception e) { Console.Error.WriteLine("Fixture teardown: " + e.Message); code = 1; }
            app.Shutdown(code);
        };
        return app.Run();
    }
    private static string? Option(string[] args, string key) =>
        Array.IndexOf(args, key) is int i && i >= 0 ? args.ElementAtOrDefault(i + 1) : null;

    private static MatchFact Fact(long seq, double clock, double known, string source, string kind, object data) =>
        new(seq, Ended, clock, known, source, kind, JsonSerializer.SerializeToElement(data));
    private static object Equipment(string item) => new { available = true, heroes = new[] {
        new { hero = "npc_dota_hero_luna", level = 9, items = new[] { item }, age = 1.0 } } };
    private static MatchFact[] History() => [
        Fact(1, -3, -3, "own-gsi", "phase", new { phase = "playing" }),
        Fact(2, 4, 6, "inference", "ward-estimate", new { available = true, coordinates = "minimap-0..1",
            wards = new[] { new { hero = "npc_dota_hero_luna", kind = "observer", x = .4, y = .3, rank = 1, share = .6, age = 2, left = 10, rough = true } } }),
        Fact(3, 2, 8, "team-vision", "equipment", Equipment("item_boots")),
        Fact(4, 11, 14, "team-vision", "ward-observed", new { coordinates = "world-xy", wards = new[] { new { kind = "observer", team = "dire", x = -4, y = 5 } } }),
        Fact(5, 16, 18, "coach", "gap", new { reason = "connection-lost" }),
        Fact(6, 17, 20, "team-vision", "equipment", Equipment("item_blink")),
        Fact(7, 22, 22, "inference", "ward-estimate", new { available = false, wards = Array.Empty<object>() }),
        Fact(8, 25, 25, "team-vision", "equipment", new { available = false, heroes = Array.Empty<object>() }),
        Fact(9, 26, 26, "own-gsi", "own-state", new { hero = "npc_dota_hero_luna", team = "radiant", health = 501, maxHealth = 700,
            x = 25, y = -30, level = 6, gold = 0, netWorth = (int?)null, lastHits = 9, denies = 2, kills = 1, deaths = 2, gpm = 400, items = Array.Empty<object>() }),
        Fact(10, 27, 27, "team-vision", "ward-charges", new { hero = "npc_dota_hero_luna", observer = 2, sentry = 3, unknown = 1,
            firstReadClock = 22, lastReadClock = 25 }),
        Fact(11, 30, 30, "own-gsi", "phase", new { phase = "postgame" })
    ];
    internal static string ReplayJson(string id) => """
        {"schemaVersion":1,"matchId":"MATCH","complete":true,"source":"postgame-replay","replaySha256":"HASH","events":[
        {"clock":1,"kind":"replay-hero","data":{"tick":30,"playerId":4,"hero":null,"team":null,"x":null,"y":null,"level":null,"hp":null,"maxHp":null,"alive":null,"items":null,"terminal":false}},
        {"clock":3,"kind":"replay-ward","data":{"tick":50,"wardId":"2:5:50","action":"placed","evidence":"first-observed-alive","type":"observer","team":3,"ownerPlayerId":null,"x":-100,"y":20,"visibleRadiant":false,"visibleDire":null}},
        {"clock":9,"kind":"replay-hero","data":{"tick":290,"playerId":4,"hero":"npc_dota_hero_drow_ranger","team":3,"x":3713,"y":-2914,"level":9,"hp":900,"maxHp":1000,"alive":true,"items":["item_butterfly","empty",null],"terminal":false}},
        {"clock":9,"kind":"replay-hero","data":{"tick":290,"playerId":8,"hero":"npc_dota_hero_razor","team":2,"x":-4370,"y":3920,"level":8,"hp":430,"maxHp":900,"alive":true,"items":["item_blade_mail"],"terminal":false}},
        {"clock":16,"kind":"replay-hero","data":{"tick":490,"playerId":4,"hero":null,"team":null,"x":null,"y":null,"level":null,"hp":null,"maxHp":null,"alive":null,"items":null,"terminal":false}},
        {"clock":26,"kind":"replay-hero","data":{"tick":810,"playerId":4,"hero":"npc_dota_hero_drow_ranger","team":3,"x":5213,"y":2114,"level":10,"hp":800,"maxHp":1100,"alive":true,"items":["item_rapier"],"terminal":false}},
        {"clock":120,"kind":"replay-end","data":{"tick":3800,"winnerTeam":2,"gameBuild":null,"parser":"gem-dota/0.14.0","hornTick":10,"playbackTicks":4000,"playbackFrames":800,"pauseIntervals":0,"clockBasis":"horn-relative","coverage":{"hero":{"sampleSeconds":5,"frames":1,"knownRows":{},"basis":"fixture"},"ward":{"openAtEnd":1,"basis":"fixture","observed":{"placed":1}},"kill":{"basis":"fixture","excluded":{}},"objective":{"basis":"fixture"},"observedEvents":{"replay-ward":1}}}}]}
        """.Replace("MATCH", id).Replace("HASH", new string('a', 64));

    private static void TimelineEdges()
    {
        var facts = Enumerable.Range(1, 137).Select(i => Fact(i, i, i, "coach", "gap", new { reason = "fixture" })).ToList();
        facts.Add(Fact(150, 20, 2, "team-vision", "equipment", Equipment("item_forged_time")));
        facts.Add(Fact(151, 20, 20, "other", "equipment", Equipment("item_other_source")));
        facts.Add(Fact(152, 20, 20, "team-vision", "equipment", Equipment("item_wrong_game")) with { MatchId = Interrupted });
        facts.Add(Fact(153, 25, 150, "postgame-replay", "replay-ward", new { action = "placed" }));
        var no = MatchReviewTimeline.Build(Ended, facts, false, default);
        Check(no.Known.Page(19, ReviewFilter.Equipment, 0).Count == 0 && no.Known.Page(20, ReviewFilter.Equipment, 0).Count == 1 &&
              no.Replay.Page(200, ReviewFilter.All, 0).Count == 0, "wrong match/source excluded; Clock and KnownAt both gate the known lane; opt-out never indexes replay");
        var page = no.Known.Page(76, ReviewFilter.Other, 1);
        Check(page.Count == 76 && page.Rows.Length == ReviewLane.PageSize && page.Rows[0].Fact.Clock == 44 &&
            no.Known.Page(76, ReviewFilter.Other, int.MaxValue).Rows[^1].Fact.Clock == 1,
            "bounded newest-first pages count only reached events, with no discarded early prefix");
        var model = Fact(12, 601, 601, "coach", "farm-input", new { inputSequence = 8, inputClock = 600.625, includesTeamKnowledge = false,
            input = new { self = new { hero = "npc_dota_hero_luna", health = 813, x = 100, y = -120 }, horizonSeconds = 60,
                options = new[] { new { id = "op-a", stops = new[] { new { label = "小野营地", arrivalSeconds = 3 } } } } } });
        var auditLane = MatchReviewTimeline.Build(Ended, [model, model with { Source = "postgame-replay" }], true, default);
        var text = new MatchReviewText(new HeroNames());
        var row = text.Row(auditLane.Known.Page(601, ReviewFilter.Plan, 0).Rows.Single().Fact);
        Check(auditLane.Known.Page(600.625, ReviewFilter.Plan, 0).Count == 0 && auditLane.Replay.Page(999, ReviewFilter.All, 0).Count == 0 &&
            row.Preview.Contains("#8") && row.Preview.Contains("10:00.625") && row.Preview.Contains("1 个候选") && row.Preview.Contains("仅本人") &&
            row.Preview.Contains("请求目标：未记录") && row.Detail.Contains("小野营地") && row.Detail.Contains("813") && !row.Detail.Contains("instructions"),
            "minute input is a known-lane planning attempt at send time, with exact snapshot, consent, candidate count and stop labels");
        foreach (var (provider, id) in new (string, string?)[] { (CopilotGateway.ProviderId, CopilotGateway.Astra),
            (CopilotGateway.ProviderId, CopilotGateway.Sol), (LiveCoachSettings.FoundryProvider, CopilotGateway.Astra),
            (LiveCoachSettings.FoundryProvider, null) }) {
            var data = JsonNode.Parse(model.Data.GetRawText())!.AsObject();
            data["providerId"] = provider; if (id != null) data["modelId"] = id;
            var attempt = text.Row(model with { Data = JsonSerializer.SerializeToElement(data) });
            Check(attempt.Preview.Contains(provider) && attempt.Preview.Contains(id ?? "部署名未记录") &&
                attempt.Detail.Contains(attempt.Preview) && attempt.Detail.Contains("不证明服务已收到"),
                "planning review identifies the recorded request target, not a confirmed response or a guessed model");
        }
    }

    private static async Task Wait(Func<bool> test, string what)
    {
        long start = Environment.TickCount64;
        while (!test()) {
            if (Environment.TickCount64 - start > 12000) throw new TimeoutException(what);
            await Task.Delay(15);
        }
    }
    private static IEnumerable<DependencyObject> Tree(DependencyObject node)
    {
        yield return node;
        foreach (var d in LogicalTreeHelper.GetChildren(node).OfType<DependencyObject>())
            foreach (var e in Tree(d)) yield return e;
    }
    private static string Rows(MatchReviewPane pane) => string.Join("\n", pane.Events.Items.Cast<ReviewRow>().Select(r => r.Detail));

    private static async Task WindowAndRestart(string temp, string[] args)
    {
        string settings = Path.Combine(temp, "prefs.json"), debug = Path.Combine(temp, "unused-debug");
        new LiveCoachSettings { AutomaticAdvice = false, EnemyPanel = false, EnemyVisionAlerts = false, FoundryEnvFile = Path.Combine(temp, "absent.env") }.Save(settings);
        using (var archive = new MatchArchive(Path.Combine(temp, "match-history"))) {
            foreach (var f in History()) archive.Append(f);
            archive.Append(History()[0] with { MatchId = Interrupted });
            archive.Seal(Ended, 30);
            string file = Path.Combine(temp, "synthetic.json"); File.WriteAllText(file, ReplayJson(Ended)); archive.ImportReplay(Ended, file);
        }
        static Button Launcher(CoachWindow w) => Tree(w).OfType<Button>().Single(b => Equals(b.Content, "打开对局复盘窗口"));
        static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        async Task<(CoachWindow, MatchReviewWindow)> Open()
        {
            var owner = new CoachWindow(null, false, settings, debug) { ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000 };
            owner.Show(); Click(Launcher(owner));
            var w = owner.OwnedWindows.OfType<MatchReviewWindow>().Single();
            await Wait(() => w.KnownMode.IsEnabled, "archive catalog and selection");
            return (owner, w);
        }
        var (coach, window) = await Open();
        Check(window.MatchStatus.Text.Contains(Interrupted) && window.MatchStatus.Text.Contains("中断") &&
              !window.LocalReplay.IsEnabled && !window.DownloadReplay.IsEnabled && !window.CompareMode.IsEnabled,
            "reopened interrupted match is not verified ended; both worker actions and all-map view are locked");
        Click(Launcher(coach)); Check(coach.OwnedWindows.Count == 1 && coach.IsEnabled, "review is a single non-modal window; live controls stay available");
        window.SearchId.Text = Ended;
        await Wait(() => window.CompareMode.IsEnabled, "ended match after search");
        Check(window.MatchStatus.Text.Contains("已验证结束") && window.Scrubber.Maximum == 30 &&
            window.ReplayPane.Events.Items.Count == 0, "known-only initial view uses no replay duration, rows, or cached details");
        window.KnownPane.SnapshotBox.IsExpanded = true;
        window.Scrubber.Value = 21;
        Check(window.KnownPane.SnapshotText.Text.Contains("item_blink"), "the late equipment update becomes available at its knowledge clock");
        window.Scrubber.Value = 8;
        Check(window.KnownPane.SnapshotText.Text.Contains("item_boots") && !Rows(window.KnownPane).Contains("item_blink") &&
              Rows(window.KnownPane).Contains("粗略") && !window.KnownPane.DetailText.Text.Contains("item_blink"),
            "rewind clears later details, candidates and inventory; the earlier last-known item is restored");
        window.Scrubber.Value = 18;
        Check(window.KnownPane.SnapshotText.Text.Contains("中断") && !window.KnownPane.SnapshotText.Text.Contains("item_boots"),
            "a disconnect invalidates previously displayed equipment, without asserting a ward was destroyed");
        window.Scrubber.Value = 14;
        Check(window.KnownPane.SnapshotText.Text.Contains("已无未到期候选") && Rows(window.KnownPane).Contains("ward-estimate"),
            "an estimate expires by game-clock TTL even before a withdrawal; the original historical observation is retained");
        window.Scrubber.Value = 25;
        Check(window.KnownPane.SnapshotText.Text.Contains("撤回") && !window.KnownPane.SnapshotText.Text.Contains("item_blink"),
            "explicit unavailable/empty snapshots withdraw state rather than carrying old items across them");
        window.Scrubber.Value = 26;
        Check(window.KnownPane.DetailText.Text.Contains("金钱 0 · 净资产 —") && window.KnownPane.DetailText.Text.Contains("生命 501/700") &&
            window.KnownPane.DetailText.Text.Contains("击杀/死亡/助攻 1/2/—") && window.KnownPane.SnapshotText.Text.Contains("世界位置 (25, -30)"),
            "own position and available economy/stats are readable at the cursor; unknown values do not turn into zeros");
        window.Scrubber.Value = 27;
        window.Filter.SelectedIndex = Array.FindIndex(MatchReviewTimeline.Filters, f => f.Key == ReviewFilter.Equipment);
        Check(window.KnownPane.DetailText.Text.Contains("ward-charges") &&
            window.KnownPane.DetailText.Text.Contains("不证明放置"), "observed ward carrier counts are inventory evidence under Equipment, not a placement");
        window.Filter.SelectedIndex = 0;
        window.Scrubber.Value = 5;
        window.CompareMode.IsChecked = true;
        await Wait(() => window.CompareMode.IsEnabled && window.Scrubber.Maximum == 120, "comparison loaded");
        Check(!Rows(window.KnownPane).Contains("item_boots") && !Rows(window.KnownPane).Contains("replay-") &&
              Rows(window.ReplayPane).Contains("first-observed-alive") && !Rows(window.ReplayPane).Contains("winnerTeam") &&
              window.ReplayPane.Events.Items.Cast<ReviewRow>().Single(r => r.Origin.Contains("replay-ward")).Title.Contains("录像首次记录存活实体"),
            "same cursor, distinct lanes: only the replay can see the verified ward; neither leaks the future winner");
        Check(window.ReplayPane.SnapshotBox.IsExpanded && window.ReplayPane.SnapshotText.Text.Contains("槽 9") &&
            !window.ReplayPane.SnapshotText.Text.Contains("item_butterfly") && window.ReplayPane.Sketch.Dots.All(d => d.Ward),
            "all ten replay slots are inspectable by default; pre-selection nulls invent neither identities, positions nor inventories");
        window.Scrubber.Value = 26;
        Check(window.ReplayPane.SnapshotText.Text.Contains("item_rapier"), "later replay frame updates the roster without selecting any event");
        window.Scrubber.Value = 16;
        Check(!window.ReplayPane.SnapshotText.Text.Contains("item_") && window.ReplayPane.Sketch.Dots.All(d => d.Ward),
            "missing slot and explicit null sample both clear old items and coordinate dots; no interpolation");
        window.Scrubber.Value = 10; window.ReplayPane.HeroSlot.SelectedIndex = 5;
        Check(window.ReplayPane.SnapshotText.Text.Contains("item_butterfly") && window.ReplayPane.SnapshotText.Text.Contains("(3713, -2914)") &&
              !window.ReplayPane.SnapshotText.Text.Contains("item_blade_mail") && !window.ReplayPane.SnapshotText.Text.Contains("item_rapier") &&
              window.ReplayPane.Sketch.Dots.Count(d => !d.Ward) == 2 && window.KnownPane.Sketch.Dots.Length == 0 &&
              !window.KnownPane.SnapshotText.Text.Contains("item_butterfly"),
            "rewind and slot selector show the precise earlier position/equipment; no postgame roster/dot leaks to the known pane");
        window.ReplayPane.HeroSlot.SelectedIndex = 0;
        window.Scrubber.Value = 14;
        window.Filter.SelectedIndex = Array.FindIndex(MatchReviewTimeline.Filters, f => f.Key == ReviewFilter.EstimatedWard);
        Check(window.KnownPane.Events.Items.Count == 1 && window.ReplayPane.Events.Items.Count == 0 &&
              window.KnownPane.DetailText.Text.Contains("inference / ward-estimate"), "inferred ward filter never buckets observed or verified ward placement");
        window.Filter.SelectedIndex = Array.FindIndex(MatchReviewTimeline.Filters, f => f.Key == ReviewFilter.SeenWard);
        Check(window.KnownPane.Events.Items.Count == 1 && window.KnownPane.DetailText.Text.Contains("ward-observed") &&
            window.ReplayPane.Events.Items.Count == 0, "team-seen wards are not presented as replay placement");
        window.Filter.SelectedIndex = 0;
        window.ReplayMode.IsChecked = true;
        await Wait(() => window.ReplayMode.IsEnabled && window.KnownPane.Visibility == Visibility.Collapsed, "replay-only");
        Check(window.KnownPane.Events.Items.Count == 0 && window.ReplayPane.Events.Items.Count > 0 && window.ReplayPane.SnapshotText.Text.Contains("item_butterfly"),
            "replay-only view clears the hidden live lane");
        window.CompareMode.IsChecked = true;
        await Wait(() => window.CompareMode.IsEnabled, "restore comparison");
        if (Option(args, "--screenshot") is { } path) SaveScreenshot(window, path);
        window.Width = window.MinWidth; window.Height = window.MinHeight;
        await Wait(() => Math.Abs(window.ActualHeight - window.MinHeight) < 2, "resize to small screen");
        await System.Windows.Threading.Dispatcher.Yield(System.Windows.Threading.DispatcherPriority.ApplicationIdle); window.UpdateLayout();
        if (Option(args, "--screenshot") is { } small) SaveScreenshot(window, Path.ChangeExtension(small, ".compact.png"));
        Check(window.KnownPane.Events.ActualHeight >= 36 && window.ReplayPane.Events.ActualHeight >= 36,
            $"both event lists remain reachable at the minimum review window size with snapshots expanded ({window.KnownPane.Events.ActualHeight:F0} / {window.ReplayPane.Events.ActualHeight:F0} in {window.ReplayPane.ActualHeight:F0})");
        coach.Close(); await Wait(() => !coach.IsVisible && !window.IsVisible, "owner and child shutdown");
        var (again, restored) = await Open();
        Check(restored.MatchStatus.Text.Contains(Ended) && restored.KnownMode.IsChecked == true && restored.Scrubber.Maximum == 30 &&
              restored.Matches.Items.Count == 2, "fresh coach/archive reopens the last selected historical match from disk, always in known-only mode");
        again.Close(); await Wait(() => !again.IsVisible && !restored.IsVisible, "second shutdown");
    }

    private static async Task StaleReads(string temp)
    {
        var ui = Application.Current.Dispatcher;
        using var started = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        bool OnDisk() => !ui.CheckAccess() ? true : throw new Exception("archive access blocked the live dispatcher");
        bool ended = false; int replayReads = 0;
        var store = new ReviewStore(() => OnDisk() ? [new(Interrupted, false, false), new(Ended, true, true)] : [],
            id => OnDisk() && ended, (id, replay) => {
                OnDisk(); if (replay) Interlocked.Increment(ref replayReads);
                if (id == Interrupted) { started.Set(); release.Wait(8000); }
                return [Fact(1, 1, 1, "coach", "gap", new { reason = id }) with { MatchId = id }];
            }, (_, _) => throw new Exception("unexpected import"));
        var w = new MatchReviewWindow(store, () => Interrupted, new HeroNames(), Path.Combine(temp, "read-race.json")) {
            ShowInTaskbar = false, WindowStartupLocation = WindowStartupLocation.Manual, Left = -9000 };
        w.Show();
        await Wait(() => started.IsSet, "slow disk read initiated off dispatcher");
        w.SearchId.Text = Ended;
        Check(w.KnownPane.Events.Items.Count == 0 && !w.DownloadReplay.IsEnabled, "a new selection synchronously clears the previous lane while disk I/O is pending");
        release.Set();
        await Wait(() => w.MatchStatus.Text.Contains(Ended) && w.KnownMode.IsEnabled, "new read won race");
        w.ReplayMode.IsChecked = true;
        Check(w.KnownMode.IsChecked == true && !w.DownloadReplay.IsEnabled && !w.LocalReplay.IsEnabled && replayReads == 0 &&
              !Rows(w.KnownPane).Contains(Interrupted), "optimistic manifest/stale read cannot bypass IsSealed or repaint another match");
        w.SearchId.Text = Interrupted;
        await Wait(() => w.MatchStatus.Text.Contains("正在记录"), "active id recognized");
        Check(!w.DownloadReplay.IsEnabled, "active recording is labeled separately from an interrupted recording and is never downloadable");
        started.Reset(); release.Reset(); w.RefreshMatches.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        await Wait(() => started.IsSet, "read running during close");
        var closing = w.StopAsync(); await Task.Delay(50);
        Check(!closing.IsCompleted, "owner shutdown waits for an in-flight archive read before disposing its archive");
        release.Set(); await closing;
    }

    private static void SaveScreenshot(Window w, string path)
    {
        var root = (FrameworkElement)w.Content; w.UpdateLayout();
        var size = new Size(root.ActualWidth, root.ActualHeight);
        w.Content = null;
        var margin = root.Margin; root.Margin = new Thickness(0);
        root.Measure(size); root.Arrange(new Rect(size)); root.UpdateLayout();
        var image = new RenderTargetBitmap((int)size.Width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var drawing = new DrawingVisual();
        using (var dc = drawing.RenderOpen()) { dc.DrawRectangle(UiBrush(), null, new Rect(size)); dc.DrawRectangle(new VisualBrush(root), null, new Rect(size)); }
        image.Render(drawing); var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var output = new FileStream(path, FileMode.CreateNew); encoder.Save(output);
        root.Margin = margin; w.Content = root;
        static Brush UiBrush() => new SolidColorBrush(Color.FromRgb(15, 21, 30));
    }
}
