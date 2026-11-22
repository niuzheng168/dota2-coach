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
            Canvas Draw(EnemyBoard b, Reminder? r = null, bool equipment = true, bool wards = true, int? physicalWidth = null) {
                var c = new Canvas { Width = w / dpi, Height = h / dpi };
                AdviceOverlay.Compose(c, r, new() { ShowEnemies = equipment, ShowWards = wards }, DisplayGeometry.OverlayUnit(h, dpi),
                    b, viewport: (physicalWidth ?? w, h));
                c.Measure(new Size(c.Width, c.Height)); c.Arrange(new Rect(0, 0, c.Width, c.Height)); c.UpdateLayout();
                return c;
            }
            var mixed = Draw(board, route);
            var gear = mixed.Children.OfType<StackPanel>().Single();
            var map = mixed.Children.OfType<Canvas>().Single(c => c.Children.OfType<Polygon>().Any());
            var guess = map.Children.OfType<Ellipse>().Single(); var seen = map.Children.OfType<Polygon>().Single();
            bool Near(double a, double b) => Math.Abs(a - b) < .01;
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
            Check(Draw(board with { Minimap = null }, route).Children.OfType<Canvas>().Count() == 1 &&
                Draw(board with { MatchId = null, Team = null }, route).Children.OfType<Canvas>().Count() == 1 &&
                !Draw(board, physicalWidth: w + 1).Children.OfType<Canvas>().Any() &&
                Draw(board with { Heroes = [], Wards = [board.Wards![0] with { Left = 0 }],
                    SeenWards = [board.SeenWards![0] with { X = 15_000 }] }).Children.Count == 0,
                "no borrowed route layout, unbound ward, stale resolution, expired estimate, or clamped off-map sighting");
            if (!right && dpi == 1) {
                var neutralOnly = board with { Heroes = [board.Heroes[0] with { Items = null, Neutral = "item_pogo_stick" }] };
                var panel = Draw(neutralOnly, wards: false).Children.OfType<StackPanel>().Single();
                Check(panel.Children.OfType<StackPanel>().Single().Children.OfType<Border>().Count() == 2 &&
                    Children(panel).OfType<TextBlock>().Any(t => t.Text.Contains("中立 pogo stick")),
                    "a known neutral item survives unknown main slots and missing icon art without inventing six empty slots");
            }
        }
        await CheckParticipantTransport(Check);
        await CheckVideoTerminal(Check, duringGrace: false);
        await CheckVideoTerminal(Check, duringGrace: true);
    }

    private static async Task CheckParticipantTransport(Action<bool, string> check)
    {
        // No visible client, fake Dota process, capture API, user GSI installation or foreground interaction.
        var temp = Directory.CreateTempSubdirectory("board-gsi-ui-");
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        string secret = Wire.Secret();
        await using var receiver = await GsiReceiver.Start(0, resumeToken: secret);
        using var post = new HttpClient { BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
        var setup = new GameWindowUi(null, temp.FullName, Path.Combine(temp.FullName, "video.json"), Path.Combine(temp.FullName, "style.json"));
        var overlay = Field<AdviceOverlay>(setup, "overlay");
        Member<DispatcherTimer>(overlay, "timer").Stop(); // Only offscreen composition above; never draw on a real game.
        Field<DispatcherTimer>(setup, "reminderTimer").Stop();
        Set(setup, "gsi", receiver); overlay.SetBoardSession(true);
        (int Width, int Height) size = (1280, 720);
        using var stop = new CancellationTokenSource();
        var run = setup.RunParticipantState(host.Pair(IPAddress.Loopback), () => size, stop.Token);
        async Task Post(string phase, int clock, bool paused = false, bool dead = true, bool sparse = false) {
            var packet = new JsonObject {
                ["auth"] = new JsonObject { ["token"] = secret },
                ["provider"] = new JsonObject { ["name"] = "Dota 2", ["appid"] = 570, ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                ["map"] = new JsonObject { ["matchid"] = BoardMatch, ["name"] = "dota", ["game_state"] = "DOTA_GAMERULES_STATE_" + phase,
                    ["clock_time"] = clock, ["paused"] = paused },
                ["player"] = new JsonObject { ["team_name"] = "radiant", ["activity"] = "playing", ["player_slot"] = 0, ["team_slot"] = 0 },
            };
            if (!sparse) packet["hero"] = new JsonObject { ["name"] = BoardHero, ["alive"] = !dead, ["health"] = dead ? 0 : 800, ["mana"] = 450 };
            using var response = await post.PostAsJsonAsync("/gsi", packet); response.EnsureSuccessStatusCode();
        }
        try {
            await Post("GAME_IN_PROGRESS", 381);
            await Wait(() => host.Mailbox.Latest()?.Meta is { Source: "dota2-gsi", Player: { Alive: false, Hero: BoardHero },
                MatchPhase: { Phase: "playing", MatchId: BoardMatch } }, "actual GSI-only loop transmits dead, non-farming participant and witnessed phase");
            var map = new MinimapLayout(size.Width, size.Height, 1080, 530, 180);
            var blink = SolidPng(Colors.Blue, 88, 64);
            host.Mailbox.Icons = key => key == "item_blink" ? blink : null;
            host.Mailbox.SetEnemies(HudBoard(map));
            await Wait(() => Member<EnemyBoard?>(overlay, "enemies") is { Wards.Length: 1, SeenWards.Length: 1, Heroes.Length: 1 } &&
                overlay.Icons["item_blink"] is BitmapSource && host.Mailbox.Latest()?.Jpeg.Length == 0 &&
                Field<Image>(setup, "image").Source == null && Field<Reminder?>(setup, "shown") == null,
                "the real game-client board poll receives the authorized subsets and item icon with no picture or route");
            var receipt = Member<EnemyBoard>(overlay, "enemies");
            var authorized = overlay.BoardForDisplay?.Invoke(receipt, 1280, 720);
            check(authorized?.Minimap == map, "production draw guard admits the paired projection");
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
            check(overlay.BoardForDisplay?.Invoke(receipt, 1280, 720) == null, "local raw-GSI pause withdraws before the next LAN poll");
            await Wait(() => host.Mailbox.Latest()?.Meta is { Player: { Hero: BoardHero, Alive: true }, MatchPhase: { Paused: true } },
                "pause is transmitted as evidence, not dropped or treated as an end");
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
            size = default;
            await Post("POST_GAME", 384, sparse: true); // no hero stats for OwnState, no drawable window
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
