using System.Net;
using System.IO;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Net.Http;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotaCoach.TwoPc;

internal static class Program
{
    private static int passed;
    [STAThread] public static void Main(string[] args)
    {
        RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            try { await Run(args.FirstOrDefault() ?? ".tmp/twopc-ui"); Console.WriteLine($"Two native WPF clients: {passed} UI checks passed."); app.Shutdown(0); }
            catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(1); }
        };
        Environment.ExitCode = app.Run();
    }
    private static T Field<T>(Window w, string name) => (T)w.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
    private static void Set(Window w, string name, object value) => w.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, value);
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        yield return parent;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is DependencyObject d) foreach (var x in Children(d)) yield return x;
    }
    private static void Click(Window w, string content) => Children(w).OfType<Button>()
        .Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static CheckBox DebugSwitch(Window w) => Children(w).OfType<CheckBox>()
        .Single(c => Equals(c.Content, "Debug：保存抽帧图片与运行日志"));
    private static DebugRecorder? Recorder(Window w)
    {
        object panel = Field<object>(w, "debug");
        return (DebugRecorder?)panel.GetType().GetProperty("Recorder")!.GetValue(panel);
    }
    private static async Task Wait(Func<bool> condition, string name, int ms = 8000)
    {
        long start = Environment.TickCount64;
        while (!condition() && Environment.TickCount64 - start < ms) await Task.Delay(50);
        if (!condition()) throw new Exception("Timed out: " + name);
        passed++; Console.WriteLine("PASS " + name);
    }
    private static void Render(Window w, string destination)
    {
        w.UpdateLayout();
        var content = (FrameworkElement)w.Content;
        int width = (int)content.ActualWidth, height = (int)content.ActualHeight;
        // Detach for software rendering: a locked/non-interactive Windows session can
        // suppress rendering of visuals still attached to a native window.
        w.Content = null;
        content.Measure(new Size(width, height)); content.Arrange(new Rect(0, 0, width, height)); content.UpdateLayout();
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen()) {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(15, 21, 30)), null, new Rect(0, 0, width, height));
            drawing.DrawRectangle(new VisualBrush(content), null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        if (pixels.Any(b => b != 0))
        {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(destination); encoder.Save(stream);
        }
        else Console.WriteLine("SKIP visual pixel QA: Windows session returned a transparent render; no screenshot claimed.");
        w.Content = content;
    }
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var coachType = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!;
        var gameType = Assembly.Load("DotaCoach.GameClient").GetType("DotaCoach.TwoPc.GameWindowUi")!;
        string debugRoot = Path.GetFullPath(Path.Combine(output, "debug"));
        var coach = (Window)Activator.CreateInstance(coachType, [null, false, Path.Combine(output, "coach-settings.json"), debugRoot])!;
        var game = (Window)Activator.CreateInstance(gameType, [null, debugRoot])!;
        // Exercise packaged UI handlers without desktop input, clipboard, real game capture or AI.
        coach.Show(); game.Show();
        await Wait(() => DebugSwitch(coach).IsChecked != true && DebugSwitch(game).IsChecked != true &&
            !Directory.Exists(debugRoot), "Debug is opt-in and creates no recording directory while off");
        DebugSwitch(coach).IsChecked = true; DebugSwitch(game).IsChecked = true;
        await Wait(() => Recorder(coach) != null && Recorder(game) != null, "both real UI Debug switches start isolated local sessions");
        string coachDebug = Recorder(coach)!.DirectoryPath, gameDebug = Recorder(game)!.DirectoryPath;
        await Wait(() => Equals(Field<ComboBox>(coach, "address").SelectedItem, "127.0.0.1"),
            "same-machine default is loopback, not a virtual or LAN adapter");
        Field<TextBox>(coach, "port").Text = "38479";
        Click(coach, "启动局域网接收");
        await Wait(() => Field<CoachHost?>(coach, "host") != null, "coach starts its own TLS listener");
        await Wait(() => !Field<bool>(coach, "busy") && !Field<ComboBox>(coach, "address").IsEnabled,
            "listening address is locked until receiver stops");
        var host = Field<CoachHost>(coach, "host");
        await Wait(() => host.ListeningAddress.Equals(IPAddress.Loopback) && host.Port == 38479,
            "Kestrel reports its actual loopback-only binding");
        using (var probe = new GameLink(host.Pair(IPAddress.Loopback))) {
            await probe.Connect(default);
            await Wait(() => probe.SessionId != null, "reported loopback binding accepts a real pinned-TLS session");
            await probe.End(default);
        }
        var lan = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .FirstOrDefault(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork &&
                Wire.IsLan(a) && !IPAddress.IsLoopback(a));
        if (lan != null) {
            bool rejected = false;
            using var lanProbe = new GameLink(host.Pair(lan));
            try { await lanProbe.Connect(default); } catch (HttpRequestException) { rejected = true; }
            catch (OperationCanceledException) { rejected = true; }
            await Wait(() => rejected, "loopback-only listener rejects pinned-TLS access through the non-loopback adapter");
        }
        Field<TextBox>(coach, "markX").Text = "invalid";
        Click(coach, "5秒后测试回传（请切回Dota）");
        await Wait(() => Field<DeferredLinkProbe?>(coach, "deferredProbe") == null &&
            Field<TextBlock>(coach, "notice").Text.Contains("坐标"),
            "invalid delayed-probe coordinates are rejected by actual UI handler");
        Field<TextBox>(coach, "markX").Text = ".065"; Field<TextBox>(coach, "markY").Text = ".86";
        Click(coach, "5秒后测试回传（请切回Dota）");
        await Wait(() => Field<DeferredLinkProbe?>(coach, "deferredProbe") != null,
            "explicit delayed-probe button schedules a single manual test");
        Click(coach, "清除回传");
        await Wait(() => Field<DeferredLinkProbe?>(coach, "deferredProbe") == null,
            "clear cancels a pending delayed probe");
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        Field<ComboBox>(game, "source").SelectedIndex = 1;
        Click(game, "连接并开始");
        await Wait(() => host.Mailbox.Snapshot().Frames >= 3 && host.Mailbox.Latest()?.Video != null,
            "game client continuously sends real H264 diagnostic video through pinned WSS");
        await Wait(() => Field<Image>(coach, "image").Source != null, "coach preview receives decoded bitmap");
        await Wait(() => Field<Image>(game, "image").Source != null, "game preview receives capture bitmap");
        await Wait(() => Recorder(coach)!.Status.Images >= 2 && Recorder(game)!.Status.Images == 0,
            "only coach samples decoded video; game debug does not duplicate images");
        long videoBefore = host.Mailbox.Snapshot().Frames, measuredAt = Environment.TickCount64;
        await Task.Delay(2200);
        long receivedVideo = host.Mailbox.Snapshot().Frames - videoBefore;
        Console.WriteLine($"VIDEO UI: {receivedVideo} received frames over {(Environment.TickCount64 - measuredAt) / 1000.0:F3}s (synthetic source, not game FPS).");
        await Wait(() => receivedVideo >= 20, "continuous video is independent of one-second debug sampling");
        long beforeFreeze = Recorder(coach)!.Status.Images;
        Field<CheckBox>(coach, "frozen").IsChecked = true;
        await Wait(() => Recorder(coach)!.Status.Images > beforeFreeze, "Debug records received frames even while the preview is frozen");
        Field<CheckBox>(coach, "frozen").IsChecked = true;
        Field<TextBox>(coach, "message").Text = "双机测试成功：字幕和小地图测试标记已回传。";
        Set(coach, "selected", new ScreenMark(.12, .80));
        Click(coach, "回传测试字幕 / 标记");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机测试成功"), "actual UI reminder handler returns caption");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 1, "UI adds returned normalized marker element");
        Render(coach, Path.Combine(output, "coach.png")); Render(game, Path.Combine(output, "game.png"));
        Click(coach, "清除回传");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 0 &&
            Field<TextBlock>(game, "caption").Text.Contains("无有效提醒"), "clear command clears game caption and marker");
        await Task.Delay(500);
        Click(coach, "回传测试字幕 / 标记");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机测试成功"), "second reminder received");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("无有效提醒"), "reminder expires without renewing on polls", 12000);
        Field<CheckBox>(coach, "frozen").IsChecked = true;
        await Task.Delay(12500);
        Click(coach, "回传测试字幕 / 标记");
        await Wait(() => Field<TextBlock>(coach, "notice").Text.StartsWith("未发送"), "expired frozen preview cannot publish a reminder");
        Click(coach, "撤销配对");
        await Wait(() => Field<CancellationTokenSource?>(game, "active") == null &&
            !Field<bool>(game, "transportConnected") && Field<ComboBox>(game, "source").IsEnabled &&
            host.Mailbox.Snapshot().SessionId == null,
            "revocation stops both video workers, releases controls and clears the session");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 0, "revocation leaves no stale marker");
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        Click(game, "连接并开始");
        await Wait(() => host.Mailbox.Snapshot().Connected, "new pairing reconnects with a fresh session");
        Click(game, "停止 / 断开");
        await Wait(() => host.Mailbox.Snapshot().SessionId == null, "game stop ends capture session on coach");
        await Wait(() => Field<Image>(coach, "image").Source == null, "coach clears disconnected preview");
        Click(game, "连接并开始"); Click(game, "停止 / 断开");
        await Wait(() => Field<CancellationTokenSource?>(game, "active") == null, "stop interrupts game connection startup");
        Click(coach, "停止");
        await Wait(() => Field<CoachHost?>(coach, "host") == null, "coach stop closes listener");
        await Wait(() => Field<ComboBox>(coach, "address").IsEnabled,
            "stopping receiver unlocks address selection");
        Click(coach, "启动局域网接收"); Click(coach, "停止");
        await Wait(() => !Field<bool>(coach, "busy") && Field<CoachHost?>(coach, "host") == null, "stop interrupts coach listener startup");
        game.Close(); coach.Close();
        await Wait(() => !game.IsVisible && !coach.IsVisible, "both clients shut down cleanly");
        await Wait(() => File.Exists(Path.Combine(coachDebug, "summary.json")) && File.Exists(Path.Combine(gameDebug, "summary.json")),
            "client shutdown flushes Debug queues and summaries");
        string recordedGame = File.ReadAllText(Path.Combine(gameDebug, "events.jsonl"));
        string recordedCoach = File.ReadAllText(Path.Combine(coachDebug, "events.jsonl"));
        await Wait(() => recordedGame.Contains("reminder-received") && recordedGame.Contains("client-state") &&
            !recordedGame.Contains("frame-captured") && recordedCoach.Contains("reminder-published") && recordedCoach.Contains("frame-sampled") &&
            !recordedGame.Contains("\"ticket\"") && !recordedCoach.Contains("DCP1."),
            "Debug correlates received/published reminders without leaking pairing or capture tickets");
        var emptyCoach = (Window)Activator.CreateInstance(coachType)!;
        var emptyGame = (Window)Activator.CreateInstance(gameType)!;
        emptyCoach.Show(); emptyGame.Show(); emptyCoach.Close(); emptyGame.Close();
        await Wait(() => !emptyGame.IsVisible && !emptyCoach.IsVisible, "unused clients close without reentrant-close errors");
        await LiveMode(output, coachType, gameType);
    }

    private static async Task LiveMode(string output, Type coachType, Type gameType)
    {
        string env = Path.Combine(output, "fake-model.env");
        File.WriteAllText(env, "AOAI_ENDPOINT=https://unit-test.openai.azure.com/\nFOUNDRY_KEY=unit-test-only\n");
        var w = (Window)Activator.CreateInstance(coachType, [null, false, Path.Combine(output, "live-settings.json"),
            Path.Combine(output, "live-debug")])!;
        w.Show();
        DebugSwitch(w).IsChecked = true;
        string debugDirectory = Recorder(w)!.DirectoryPath;
        await Wait(() => Field<CheckBox>(w, "automatic").IsChecked == true &&
            Field<CheckBox>(w, "subtitles").IsChecked == true && Field<CheckBox>(w, "mapMarkers").IsChecked == true,
            "normal UI defaults automatic model, subtitles and map features to enabled");
        var preference = new LiveCoachSettings { FoundryEnvFile = Path.GetFullPath(env), RequestLimit = 1 };
        Set(w, "preferences", preference);
        var map = new CalibratedMap("data/dota2-map-reference.json");
        Set(w, "liveMap", map);
        using var fake = new UiModel();
        var cloud = new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", fake, map, false);
        Set(w, "cloud", cloud); Set(w, "modelConfigured", true);
        Field<TextBox>(w, "port").Text = "38480"; Click(w, "启动局域网接收");
        await Wait(() => Field<CoachHost?>(w, "host") != null && !Field<bool>(w, "busy"), "normal live UI starts paired service");
        var host = Field<CoachHost>(w, "host");
        string session = host.Mailbox.Begin().SessionId;
        long sequence = 0;
        var bitmap = BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Bgr24, null, new byte[1280 * 720 * 3], 1280 * 3);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream(); encoder.Save(stream); byte[] jpeg = stream.ToArray();
        void Frame() {
            var ticket = host.Mailbox.Issue(session);
            host.Mailbox.Accept(new(session, ticket.Id, ++sequence, "dota2", 3840, 2160,
                new OwnState("9876543210", "npc_dota_hero_luna", "radiant", 720, true, 1200, 500, -2000, -3000,
                    [new("item_power_treads", null, 0)], [new("luna_moon_glaive", 4, 0)]), 10), jpeg);
        }
        Frame();
        await Wait(() => host.Mailbox.Poll(session)?.Kind == "visible-farming-advice",
            "ordinary match 4K frame automatically invokes model and publishes subtitles without E2E flags");
        await Wait(() => fake.Calls == 1 && host.Mailbox.Poll(session)?.Marker == null,
            "uncalibrated normal 4K does not fabricate a map point");
        Set(w, "nextDecision", 0L); Frame(); await Task.Delay(800);
        await Wait(() => fake.Calls == 1, "normal UI request budget prevents extra paid requests");
        Field<CheckBox>(w, "automatic").IsChecked = false;
        await Wait(() => host.Mailbox.Poll(session) == null, "normal pause clears the active model reminder");

        Frame(); await Wait(() => Field<FrameView?>(w, "displayed")?.Meta.SourceWidth == 3840, "4K source dimensions reach actual preview controls");
        Click(w, "预览左下框");
        await Wait(() => Field<TextBox>(w, "mapTop").Text == "1680" && Field<TextBox>(w, "mapSize").Text == "480",
            "4K calibration UI proposes source-pixel bounds, not 720p or JPEG bounds");
        Click(w, "确认小地图框");
        await Wait(() => LiveCoachSettings.Load(Path.Combine(output, "live-settings.json")).Calibrations.Length == 1,
            "actual calibration handler persists the confirmed 4K rectangle");
        var layout = new MinimapLayout(3840, 2160, 0, 1680, 480);
        using var marked = new UiModel { Target = "neutralcamp_good_11" };
        Set(w, "preferences", Field<LiveCoachSettings>(w, "preferences") with { RequestLimit = 3 });
        Set(w, "cloud", new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", marked, map, false, layout));
        Set(w, "activeLayout", layout);
        Set(w, "nextDecision", 0L); Frame(); Field<CheckBox>(w, "automatic").IsChecked = true;
        await Wait(() => host.Mailbox.Poll(session) is { Kind: "visible-farming-advice", Marker: not null },
            "normal automatic UI publishes a model-selected 4K landmark after calibration");
        var result = host.Mailbox.Poll(session)!;
        await Wait(() => result.Minimap == layout && layout.Contains(result.Marker!), "normal map advice carries checked source-resolution geometry");
        Render(w, Path.Combine(output, "coach-live-4k-controls.png"));

        using var delayed = new UiModel { Delayed = true, Target = "neutralcamp_good_11" };
        Set(w, "cloud", new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", delayed, map, false, layout));
        Set(w, "nextDecision", 0L); Frame();
        await Wait(() => delayed.Calls == 1 && Field<bool>(w, "deciding"), "normal inference is in flight before pause test");
        Field<CheckBox>(w, "automatic").IsChecked = false;
        delayed.Release.TrySetResult(true);
        await Wait(() => !Field<bool>(w, "deciding") && host.Mailbox.Poll(session) == null,
            "paused normal UI discards even a provider reply that ignores cancellation");
        w.Close(); await Wait(() => !w.IsVisible, "live UI closes cleanly after model and calibration work");
        string modelTrace = File.ReadAllText(Path.Combine(debugDirectory, "events.jsonl"));
        await Wait(() => modelTrace.Contains("model-input") && modelTrace.Contains("model-result") &&
            modelTrace.Contains("model-request-or-publication") && !modelTrace.Contains("unit-test-only"),
            "normal model Debug saves exact input references, results and rejected replies without API credentials");

        // Isolated Windows DPAPI / GSI installer tests; never touch the user's Dota cfg directory.
        string state = Path.GetFullPath(Path.Combine(output, "gsi-private"));
        string identity = GsiSetup.Token(state);
        await Wait(() => GsiSetup.Token(state) == identity && !Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(state, "gsi-identity.dpapi"))).Contains(identity),
            "game GSI identity persists through user-bound DPAPI, not plaintext settings");
        string fakeInstall = Path.GetFullPath(Path.Combine(output, "fake-dota"));
        Directory.CreateDirectory(Path.Combine(fakeInstall, "game/dota"));
        string Install(string content) => GsiSetup.Install(fakeInstall, content, state).Path;
        string configPath = Install("test-only-GSI");
        await Wait(() => Install("test-only-GSI") == configPath, "unchanged owned GSI config is reused across client starts");
        File.WriteAllText(configPath, "user-modification");
        bool rejected = false; try { Install("replacement"); } catch (IOException) { rejected = true; }
        await Wait(() => rejected && File.ReadAllText(configPath) == "user-modification", "auto GSI setup preserves user-modified files");
        File.Delete(env);
        await SpectatorPanel(output, coachType);
    }

    /// <summary>The real coach panel against a fake Dota folder and a synthetic Plus feed; never the user's cfg.</summary>
    private static async Task SpectatorPanel(string output, Type coachType)
    {
        var w = (Window)Activator.CreateInstance(coachType, [null, false, Path.Combine(output, "spectator-settings.json"),
            Path.Combine(output, "spectator-debug")])!;
        w.Show();
        string fakeInstall = Path.GetFullPath(Path.Combine(output, "fake-dota-coach"));
        Directory.CreateDirectory(Path.Combine(fakeInstall, "game/dota"));
        Field<TextBox>(w, "spectatorInstall").Text = fakeInstall;
        string state = Path.GetFullPath(Path.Combine(output, "spectator-private"));
        Set(w, "spectatorState", state); Set(w, "spectatorPort", 0);
        Click(w, "一键配置观战 GSI");
        await Wait(() => Field<SpectatorReceiver?>(w, "spectator") != null && Field<TextBlock>(w, "spectatorSetup").Text.Contains("重启"),
            "one-click setup starts the local spectator receiver and writes its cfg");
        string cfg = File.ReadAllText(Path.Combine(fakeInstall, "game/dota/cfg/gamestate_integration", GsiSetup.SpectatorConfigName));
        var receiver = Field<SpectatorReceiver>(w, "spectator");
        string token = System.Text.RegularExpressions.Regex.Match(cfg, "\"token\" \"([A-F0-9]+)\"").Groups[1].Value;
        await Wait(() => cfg.Contains($"127.0.0.1:{receiver.Port}/spectator") && token.Length == 64, "written cfg points at the running receiver");
        Click(w, "一键配置观战 GSI");
        await Wait(() => Field<TextBlock>(w, "spectatorSetup").Text.Contains("已就绪") && Field<SpectatorReceiver>(w, "spectator") == receiver,
            "second click reuses the receiver and does not ask for another Dota restart");
        using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
        string body = JsonSerializer.Serialize(new { auth = new { token },
            map = new { clock_time = 125, game_state = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", paused = false },
            player = new { team_name = "spectator" }, hero = new { id = 0 },
            minimap = new {
                o0 = new { xpos = 1, ypos = 2, team = 3, unitname = "npc_dota_hero_troll_warlord", image = "minimap_herocircle_self", visionrange = 1800 },
                o1 = new { xpos = 3, ypos = 4, team = 2, unitname = "npc_dota_hero_razor", image = "minimap_enemyicon", visionrange = 1800 },
                o2 = new { xpos = 5, ypos = 6, team = 4, unitname = "npc_dota_neutral_alpha_wolf", visionrange = 400 } } });
        await http.PostAsync("/spectator", new StringContent(body));
        await Wait(() => Field<TextBlock>(w, "spectatorLive").Text is { } t && t.Contains("队伍视角") && t.Contains("2:05") &&
            t.Contains("夜魇") && t.Contains("视野内敌方英雄 1") && t.Contains("视野内野怪 1") && t.Contains("等待游戏机"),
            "panel shows the Plus view, its team, visible enemies/camps and waits for the game side");
        // Game side: own GSI clock riding on accepted frames; Plus side lags each second by ~150 ms.
        Field<TextBox>(w, "port").Text = "38481"; Click(w, "启动局域网接收");
        await Wait(() => Field<CoachHost?>(w, "host") != null && !Field<bool>(w, "busy"), "coach listener for delay wiring test");
        var host = Field<CoachHost>(w, "host");
        string session = host.Mailbox.Begin().SessionId;
        var bitmap = BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Bgr24, null, new byte[1280 * 720 * 3], 1280 * 3);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var jpegStream = new MemoryStream(); encoder.Save(jpegStream); byte[] jpeg = jpegStream.ToArray();
        for (int clock = 126; clock <= 133; clock++) {
            var ticket = host.Mailbox.Issue(session);
            host.Mailbox.Accept(new(session, ticket.Id, clock, "dota2", 3840, 2160, new OwnState("9876543210", "npc_dota_hero_luna",
                "dire", clock, true, 1200, 500, 1, 2, [], []), 10), jpeg);
            await Task.Delay(150);
            await http.PostAsync("/spectator", new StringContent(body.Replace("\"clock_time\":125", $"\"clock_time\":{clock}")));
            await Task.Delay(150);
        }
        await Wait(() => Field<TextBlock>(w, "spectatorLive").Text is { } t &&
            System.Text.RegularExpressions.Regex.Match(t, @"比游戏机慢：中位 (\d+\.\d+)s") is { Success: true } m &&
            double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is >= .10 and <= .40,
            "real window measures Plus lag from game-machine frames on the coach clock");
        w.Close(); await Wait(() => !w.IsVisible && Field<SpectatorReceiver?>(w, "spectator") == null, "closing releases the spectator port");
        string realState = GsiSetup.StateDirectoryFor("Coach");
        await Wait(() => File.Exists(Path.Combine(state, "installed-spectator-gsi.json")) &&
            (!File.Exists(Path.Combine(realState, "dota-install.txt")) ||
             !File.ReadAllText(Path.Combine(realState, "dota-install.txt")).Contains(output)),
            "UI test keeps spectator identity/receipt in its own folder, not the user's coach state");
    }

    private sealed class UiModel : HttpMessageHandler
    {
        public int Calls;
        public string? Target;
        public bool Delayed;
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Delayed) await Release.Task; // Intentional hostile/late provider for cancellation regression.
            var advice = new ModelAdvice("npc_dota_hero_luna", "radiant", "合成UI测试画面，非实际游戏验收",
                ["测试自身状态"], ["营地存量未知"], "先确认兵线和附近野点，未知不当安全。",
                ["确认可见资源再决定"], "出现威胁或状态变化取消", null, Target == null ? "" : "用户确认的小地图框", Target);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                id = "test-ui-response", status = "completed", usage = new { input_tokens = 100, output_tokens = 80 },
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(advice, Wire.Json) } } } }
            })) };
        }
    }
}
