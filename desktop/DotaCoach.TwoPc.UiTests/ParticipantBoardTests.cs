using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;
using DotaCoach.TwoPc;
using Path = System.IO.Path;

internal static partial class Program
{
    private const string BoardMatch = "9134567123", BoardHero = "npc_dota_hero_crystal_maiden";
    private static EnemyBoard HudBoard(MinimapLayout m) => new(
        [new("npc_dota_hero_lion", 11, [null, "item_blink", null, null, null, null], null, 35)],
        [new("npc_dota_hero_lion", "observer", .25, .75, 0, 1, 12, 40, Rough: true)],
        BoardMatch, "radiant", m, [new("sentry", "dire", -4736, -4736)]);

    private static async Task ParticipantBoardChecks()
    {
        void Check(bool ok, string why) { if (!ok) throw new Exception(why); passed++; Console.WriteLine("PASS " + why); }
        foreach (var (w, h, dpi) in new[] { (1280, 720, 1d), (3840, 2160, 2d) })
        foreach (bool right in new[] { false, true }) {
            double side = h * .21;
            var m = new MinimapLayout(w, h, right ? w - side - 8 : 8, h - side - 14, side);
            var board = HudBoard(m);
            var target = new ScreenMark((m.Left + m.Size * .7) / w, (m.Top + m.Size * .4) / h);
            var route = new Reminder("minute", "session", 4, MinuteFarmContext.Kind, "空点跳过", target,
                w, h, 5000, m, false, [target], ["有野再打"], FarmMinute: 10);
            Canvas Draw(EnemyBoard b, Reminder? r = null, bool equipment = true, bool wards = true, int? physicalWidth = null, bool waves = true) {
                var c = new Canvas { Width = w / dpi, Height = h / dpi };
                AdviceOverlay.Compose(c, r, new() { ShowEnemies = equipment, ShowWards = wards, ShowLaneWaves = waves }, DisplayGeometry.OverlayUnit(h, dpi),
                    b, viewport: (physicalWidth ?? w, h));
                c.Measure(new Size(c.Width, c.Height)); c.Arrange(new Rect(0, 0, c.Width, c.Height)); c.UpdateLayout();
                return c;
            }
            var mixed = Draw(board, route);
            var gear = mixed.Children.OfType<StackPanel>().Single();
            var map = mixed.Children.OfType<Canvas>().Single(c => c.Children.OfType<Polygon>().Any());
            var guess = map.Children.OfType<Ellipse>().Single(); var seen = map.Children.OfType<Polygon>().Single();
            bool Near(double a, double b) => Math.Abs(a - b) < .01;
            var dots = new LaneMap742(1213, [new("top",3,"fog",-4736,-4736,455,1210,-4700,-3900),
                new("mid",2,"seen",400,520,0,1213), new("bot",2,"march",6000,-5500,700,Spawn:1200),
                new("mid",0,"forecast",450,460,420,1213,Meeting:1220)],1900);
            var withWaves = board with { LaneMap=dots };
            var wl = Draw(withWaves,equipment:false,wards:false).Children.OfType<Canvas>().Single();
            var gap = wl.Children.OfType<Ellipse>().Single(e=>Equals(e.Tag,"wave-fog"));
            Check(Near(Canvas.GetLeft(wl)*dpi,m.Left) && Near(Canvas.GetTop(wl)*dpi,m.Top) &&
                Near((Canvas.GetLeft(gap)+gap.Width/2)*dpi,m.Size*.25) && Near((Canvas.GetTop(gap)+gap.Height/2)*dpi,m.Size*.75) &&
                gap.Fill == Brushes.Transparent && wl.Children.OfType<Ellipse>().Single(e=>Equals(e.Tag,"wave-seen")).Fill != Brushes.Transparent &&
                wl.Children.OfType<Ellipse>().Any(e=>Equals(e.Tag,"wave-uncertainty") && e.StrokeDashArray.Count>0) &&
                wl.Children.OfType<Polygon>().Any(e=>Equals(e.Tag,"wave-march")) && wl.Children.OfType<TextBlock>().Any(e=>e.Text=="~7s"),
                $"actual game compositor: separate observed/fog/march/ETA shapes at confirmed {(right?"right":"left")} {h}p/{dpi}x");
            Check(Draw(withWaves,equipment:false,wards:false,waves:false).Children.Count==0 &&
                Draw(withWaves,equipment:false,wards:false,physicalWidth:w+10).Children.Count==0 &&
                Draw(withWaves,route,wards:false).Children.OfType<Canvas>().Count()==2,
                "local switch, uncalibrated viewport and route/board separation also apply to wave-only maps");
            Check(Near(Canvas.GetLeft(gear), 4 * DisplayGeometry.OverlayUnit(h, dpi)) &&
                mixed.Children.OfType<Canvas>().Count() == 2 && Near(Canvas.GetLeft(map) * dpi, m.Left) &&
                Near(Canvas.GetTop(map) * dpi, m.Top) && Near(map.Width * dpi, m.Size) && map.ClipToBounds && !map.IsHitTestVisible,
                $"left equipment, independent route and confirmed {(right ? "right" : "left")} custom ward HUD at {h}p/{dpi}x");
            Check(guess.StrokeDashArray is { Count: > 0 } && seen.StrokeDashArray is null or { Count: 0 } &&
                Children(map).OfType<TextBlock>().Any(t => t.Text == "?") &&
                Children(map).OfType<TextBlock>().Any(t => t.Text.Contains("目击") && t.Text.Contains("推测")) &&
                new FrameworkElement[] { guess, seen }.All(e => Near((Canvas.GetLeft(e) + e.Width / 2) * dpi, .25 * m.Size) &&
                    Near((Canvas.GetTop(e) + e.Height / 2) * dpi, .75 * m.Size)),
                "coincident sighting and estimated region stay distinct, at their own unmirrored source coordinates");
            Check(Draw(board, equipment: false).Children.Cast<UIElement>().Single() is Canvas &&
                Draw(board, wards: false).Children.Cast<UIElement>().Single() is StackPanel &&
                Draw(board, route, false, false).Children.Cast<UIElement>().Single() is Canvas &&
                Draw(board with { Heroes = [], Wards = null }).Children.OfType<Canvas>().Single().Children.OfType<Polygon>().Count() == 1 &&
                Draw(board with { Heroes = [], SeenWards = null }).Children.OfType<Canvas>().Single().Children.OfType<Ellipse>().Count() == 1,
                "turning off route, equipment or wards does not suppress or synthesize another subset");
            var local = new GameOverlaySettings().Minimap(w, h);
            var unconfirmed = Draw(board with { Minimap = null }, route).Children.OfType<Canvas>().Single(c => c.Children.OfType<Polygon>().Any());
            var otherSize = Draw(board, physicalWidth: w + 1).Children.OfType<Canvas>().Single();
            Check(Near(Canvas.GetLeft(unconfirmed) * dpi, local.Left) && Near(Canvas.GetTop(unconfirmed) * dpi, local.Top) &&
                Near(unconfirmed.Width * dpi, local.Size) && Near(Canvas.GetTop(otherSize) * dpi, local.Top) &&
                Draw(board with { MatchId = null, Team = null }, route).Children.OfType<Canvas>().Count() == 1 &&
                Draw(board with { Heroes = [], Wards = [board.Wards![0] with { Left = 0 }],
                    SeenWards = [board.SeenWards![0] with { X = 15_000 }] }).Children.Count == 0,
                "without a confirmed box for this size the marks use this machine's minimap setting (not the route's box); " +
                "no unbound ward, expired estimate, or clamped off-map sighting");
            if (!right && dpi == 1) {
                var neutralOnly = board with { Heroes = [board.Heroes[0] with { Items = null, Neutral = "item_pogo_stick" }] };
                var panel = Draw(neutralOnly, wards: false).Children.OfType<StackPanel>().Single();
                Check(panel.Children.OfType<StackPanel>().Single().Children.OfType<Border>().Count() == 2 &&
                    Children(panel).OfType<TextBlock>().Any(t => t.Text.Contains("中立 pogo stick")),
                    "a known neutral item survives unknown main slots and missing icon art without inventing six empty slots");
            }
        }
        await CheckParticipantTransport(Check, localHp: false);
        await CheckParticipantTransport(Check, localHp: true);
        await CheckVideoTerminal(Check, duringGrace: false);
        await CheckVideoTerminal(Check, duringGrace: true);
    }

    private static async Task CheckParticipantTransport(Action<bool, string> check, bool localHp)
    {
        // No visible client, fake Dota process, capture API, user GSI installation or foreground interaction.
        var temp = Directory.CreateTempSubdirectory("board-gsi-ui-");
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        string secret = Wire.Secret();
        var install = Directory.CreateDirectory(Path.Combine(temp.FullName, "game", "dota"));
        File.WriteAllText(Path.Combine(install.FullName, "steam.inf"), "appID=570\nClientVersion=6989\nServerVersion=6989\n");
        await using var receiver = await GsiReceiver.Start(0, resumeToken: secret, dotaInstall: temp.FullName, diagnostics: localHp);
        using var post = new HttpClient { BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
        var setup = new GameWindowUi(null, temp.FullName, Path.Combine(temp.FullName, "video.json"), Path.Combine(temp.FullName, "style.json"));
        var overlay = Field<AdviceOverlay>(setup, "overlay");
        Member<DispatcherTimer>(overlay, "timer").Stop(); // Only offscreen composition above; never draw on a real game.
        Field<DispatcherTimer>(setup, "reminderTimer").Stop();
        if (!localHp) await MinuteLocalCaptureChecks(setup);
        // An earlier independent HP opt-in is represented by a disposable stand-in, never a real WGC worker.
        var hp = new FakeLocalCapture(); hp.Stopped.SetResult();
        Set(setup, "creepHp", hp);
        SelectSource(setup, localHp ? "本人 GSI＋可选本机小兵血量" : "仅本人 GSI");
        SetGameIdle(setup, false);
        using var stop = new CancellationTokenSource();
        await PrepareSelectedMode(setup, stop.Token);
        var hpButton = Field<Button>(setup, "creepHpButton");
        check(Field<bool>(setup, "participantConnection") && Field<bool>(setup, "gsiOnlyConnection") == !localHp &&
            hpButton.IsEnabled == localHp && hp.StopRequested == !localHp && ReferenceEquals(Field<IAsyncDisposable?>(setup, "creepHp"), localHp ? hp : null) &&
            !Field<ComboBox>(setup, "source").IsEnabled && Field<Grid>(setup, "preview").Visibility == Visibility.Collapsed,
            localHp ? "optional local HP preserves the opted-in scanner while locking in participant GSI, not video" :
                "strict participant GSI disposes even an already opted-in scanner before connecting");
        Set(setup, "gsi", receiver);
        (int Width, int Height) size = (1280, 720);
        var run = setup.RunParticipantState(host.Pair(IPAddress.Loopback), () => size, stop.Token);
        async Task Post(string phase, int clock, bool paused = false, bool dead = true, bool sparse = false) {
            var packet = new JsonObject {
                ["auth"] = new JsonObject { ["token"] = secret },
                ["provider"] = new JsonObject { ["name"] = "Dota 2", ["appid"] = 570, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                ["map"] = new JsonObject { ["matchid"] = BoardMatch, ["name"] = "dota", ["game_state"] = "DOTA_GAMERULES_STATE_" + phase,
                    ["clock_time"] = clock, ["paused"] = paused },
                ["player"] = new JsonObject { ["team_name"] = "radiant", ["activity"] = "playing", ["player_slot"] = 0, ["team_slot"] = 0 },
            };
            if (!sparse) {
                packet["hero"] = new JsonObject { ["name"] = BoardHero, ["level"] = 9, ["alive"] = !dead, ["health"] = dead ? 0 : 800, ["mana"] = 450 };
                packet["minimap"] = new JsonObject { ["creep"] = new JsonObject { ["xpos"] = 100, ["ypos"] = 200,
                    ["team"] = 2, ["unitname"] = "npc_dota_creep_goodguys_melee", ["visionrange"] = 850 } };
            }
            using var response = await post.PostAsJsonAsync("/gsi", packet); response.EnsureSuccessStatusCode();
        }
        try {
            await Post("GAME_IN_PROGRESS", 381);
            await Wait(() => host.Mailbox.Latest() is { HasImage: false, Jpeg.Length: 0, Video: null, Meta: { Source: "dota2-gsi", Player: { Alive: false, Hero: BoardHero },
                MatchPhase: { Phase: "playing", MatchId: BoardMatch, InstalledBuild: 6989 } } }, "actual GSI-only loop transmits dead, non-farming participant and witnessed phase/build without pixels");
            var received = host.Mailbox.Latest()!.Meta;
            check((localHp ? receiver.LatestMinimap()?.Snapshot.Minimap.Any(u => u.Name == "npc_dota_creep_goodguys_melee") == true &&
                    receiver.LatestLoadout() is { Level: 9, Hero: BoardHero } : receiver.LatestMinimap() == null) &&
                !System.Text.Json.JsonSerializer.Serialize(received, Wire.Json).Contains("npc_dota_creep_goodguys_melee"),
                localHp ? "the opted-in HP scanner can use the local minimap and loadout; the paired metadata never includes creep observations" :
                    "strict participant mode has no local diagnostic minimap or downlinked creep observations");
            var map = new MinimapLayout(size.Width, size.Height, 1080, 530, 180);
            var blink = SolidPng(Colors.Blue, 88, 64);
            host.Mailbox.Icons = key => key == "item_blink" ? blink : null;
            await Task.Delay(1100); await Post("GAME_IN_PROGRESS", 381); // A new provider second: the local witness stays fresh below.
            host.Mailbox.SetEnemies(HudBoard(map) with { MatchId = "9134567999" });
            await Task.Delay(1600); // Two board polls.
            check(Member<EnemyBoard?>(overlay, "enemies") == null && overlay.Icons["item_blink"] == null,
                "a panel naming another match than this machine's own GSI is neither drawn nor completed with icons");
            await Post("GAME_IN_PROGRESS", 381); // Fresh evidence after waiting through the wrong-match polls.
            await Wait(() => host.Mailbox.Latest()?.Meta.MatchPhase is { Clock: 381, AgeMs: < 800 }, "fresh local clock for the two-second wave lease");
            host.Mailbox.SetEnemies(HudBoard(map) with { LaneMap = new(381, [new("top",3,"fog",-6040,1800,455,378,-6150,2450)], 2000) });
            await Wait(() => Member<EnemyBoard?>(overlay, "enemies") is { Wards.Length: 1, SeenWards.Length: 1, Heroes.Length: 1, LaneMap: not null } &&
                overlay.Icons["item_blink"] is BitmapSource && host.Mailbox.Latest()?.Jpeg.Length == 0 &&
                Field<Image>(setup, "image").Source == null && Field<Reminder?>(setup, "shown") == null,
                "the real game-client board poll receives the authorized subsets and item icon with no picture or route");
            var authorized = Member<EnemyBoard>(overlay, "enemies");
            check(authorized.Minimap == map && authorized.MatchId == BoardMatch, "the drawn panel carries the coach's confirmed box and the player's match");
            check(overlay.BoardForDisplay?.Invoke(authorized, size.Width, size.Height)?.LaneMap?.Marks[0].Kind == "fog",
                "game-client poll and draw guard admit the same dated wave symbols in GSI-only mode");
            if (localHp) {
                // User can turn an opted-in HP display OFF via the actual game button while the scoped overlay stays paired.
                hpButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                await Wait(() => hp.StopRequested && Field<IAsyncDisposable?>(setup, "creepHp") == null && hpButton.IsEnabled &&
                    Field<bool>(setup, "transportConnected") && Member<EnemyBoard?>(overlay, "enemies")?.MatchId == BoardMatch &&
                    host.Mailbox.Latest() is { HasImage: false, Video: null, Meta.Source: "dota2-gsi" },
                    "switching off only the local HP worker preserves the authenticated photo-free match overlay");
            }
            // Exercise the actual small HWNDs using the admitted board at a rect *outside* the virtual desktop.
            // Do not spoof the foreground gate, locate/activate Dota, sample pixels or make the overlay capturable.
            var offscreen = new DotaCoach.Desktop.GameSurface(IntPtr.Zero, -26000, -26000, 1280, 720);
            var drawBoards = typeof(AdviceOverlay).GetMethod("DrawBoards", BindingFlags.Instance | BindingFlags.NonPublic)!;
            try {
                drawBoards.Invoke(overlay, [offscreen, authorized]);
                var gearWindow = Member<Window>(overlay, "equipmentWindow"); var wardWindow = Member<Window>(overlay, "wardWindow");
                gearWindow.UpdateLayout(); wardWindow.UpdateLayout();
                nint gear = new WindowInteropHelper(gearWindow).Handle, ward = new WindowInteropHelper(wardWindow).Handle;
                check(GetWindowRect(gear, out var g) && GetWindowRect(ward, out var r) && g.Left == offscreen.Left + 4 &&
                    g.Right - g.Left is > 50 and < 400 && r.Left == offscreen.Left + (int)map.Left && r.Top == offscreen.Top + (int)map.Top &&
                    r.Right - r.Left == map.Size && r.Bottom - r.Top == map.Size &&
                    ((FrameworkElement)wardWindow.Content).ActualWidth > 0 && !gearWindow.IsHitTestVisible && !wardWindow.ShowActivated &&
                    GetForegroundWindow() != gear && GetForegroundWindow() != ward && !overlay.IsVisible,
                    "actual passive native windows cover only the left panel and calibrated right HUD while the reminder HWND stays hidden");
            } finally { drawBoards.Invoke(overlay, [offscreen, null]); }
            await Post("GAME_IN_PROGRESS", 382, paused: true, dead: false);
            await Wait(() => host.Mailbox.Latest()?.Meta is { Player: { Hero: BoardHero, Alive: true }, MatchPhase: { Paused: true } },
                "pause is transmitted as evidence, not dropped or treated as an end");
            host.Mailbox.SetEnemies(HudBoard(map)); await Task.Delay(1600);
            check(Member<EnemyBoard?>(overlay, "enemies") is { Heroes.Length: 1 }, "a pause keeps the last-known enemy panel on screen");
            await Post("GAME_IN_PROGRESS", 383);
            await Wait(() => host.Mailbox.Latest()?.Meta.MatchPhase is { Paused: false, Clock: 383 }, "resumed participant reaches the original paired session");
            // Verify the VIDEO producer too, using one in-memory image, no codec, real process or display capture.
            var lease = new Ticket(Guid.NewGuid().ToString("N"), "fixture");
            foreach (bool diagnostic in new[] { false, true }) {
                var sender = new VideoSender(lease, 30);
                await sender.StartCapture(() => new BoardVideoFixture(), diagnostic, () => 1,
                    () => GameGsiSample.Read(receiver, participantOnly: false), stop.Token);
                var meta = sender.Latest!.Meta;
                check(diagnostic ? meta.MatchPhase == null && meta.Player == null : meta is { MatchPhase.Phase: "playing", Player.Alive: false },
                    "video metadata follows authenticated GSI without attaching it to diagnostic pixels");
            }
            await Post("GAME_IN_PROGRESS", 384);
            await Wait(() => host.Mailbox.Latest()?.Meta.MatchPhase?.Clock == 384, "fresh participant before the ward-only update");
            host.Mailbox.SetEnemies(HudBoard(map) with { Heroes = [] });
            await Wait(() => Member<EnemyBoard?>(overlay, "enemies") is { Heroes: [], Wards: [{ Kind: "observer", Rank: 0, Rough: true }],
                SeenWards: [{ Kind: "sentry" }] } only && only.Minimap == map,
                "the game client admits ward guesses and sightings on the confirmed HUD without an equipment panel or a picture");
            size = default;
            await Post("POST_GAME", 385, sparse: true); // no hero stats for OwnState, no drawable window
            string session = host.Mailbox.Snapshot().SessionId!;
            await Task.Delay(200); // Shorter than a GSI-only tick. No waiting for an ordinary terminal upload.
            using var menu = await post.PostAsJsonAsync("/gsi", new { auth = new { token = secret } });
            await Wait(() => host.Mailbox.Latest()?.Meta is { SourceWidth: 0, Player: null, MatchPhase: null } && Member<EnemyBoard?>(overlay, "enemies") == null,
                "unavailable GSI withdraws live layers and sends an explicit empty heartbeat");
            var end = host.Mailbox.TakePostgame(session);
            check(end?.Meta is { Player: null, SourceWidth: 0, MatchPhase: { Hero: BoardHero, Phase: "postgame" } } && end.Jpeg.Length == 0 &&
                receiver.LatestMatch() == null && host.Mailbox.TakePostgame(session) == null,
                "actual GSI-only loop delivers the sparse terminal once independently of viewport, farm and live menu heartbeat");
        } finally {
            stop.Cancel(); await run.WaitAsync(TimeSpan.FromSeconds(6));
            Set(setup, "gsi", null!);
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            setup.Closed += (_, _) => closed.TrySetResult();
            setup.Close(); await closed.Task.WaitAsync(TimeSpan.FromSeconds(6));
            temp.Delete(true);
        }
    }

    private sealed class BoardVideoFixture : IVideoSource
    {
        private int reads;
        public string Name => "fixture";
        public int SourceWidth => 1280;
        public int SourceHeight => 720;
        public int Width => 32;
        public int Height => 18;
        public bool Ended => reads > 1;
        public VideoPixels? Grab() => ++reads == 1 ? new(Width, Height, new byte[Width * Height * 3 / 2]) : null;
        public void Dispose() { }
    }
}
