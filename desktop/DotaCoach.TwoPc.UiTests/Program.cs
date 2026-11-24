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

internal static partial class Program
{
    private static int passed;
    [STAThread] public static void Main(string[] args)
    {
        if (args.FirstOrDefault() == "-I") { ScannerStandIn(args); return; } // child fixture; no WPF, Python or Dota
        bool trial = args.Length == 3 && args[0] == "--panel-trial";
        // The trial draws over the real game like the shipped client: per-monitor DPI aware, hardware rendering.
        if (trial) SetProcessDpiAwarenessContext(-4);
        else RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (args is ["--game-controls"]) {
            CheckDefaultProfiles(); // No visible window, pairing, game or GSI installation.
            Console.WriteLine("PASS fresh and v0.4.x profiles start in participant GSI mode with local HP off and no video or practice worker");
            return;
        }
        // The released game client has no picture mode. These fixtures still drive the video, diagnostic and strict
        // lanes, so they opt in to the developer selector; --game-controls above checks the production window.
        Features.VideoUpload = true;
        CheckDefaultProfiles(production: true);
        if (args is ["--offline-practice"]) { OfflinePractice(); return; }
        if (args is ["--lane-vision-e2e"]) { LaneVisionE2E.Run(); return; }
        if (args is ["--lane-window-e2e"]) { LaneWindowE2E.Run(); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            try {
                if (trial) { await PanelTrial(args[1], args[2]); Console.WriteLine($"Enemy panel trial on the real Dota window: {passed} checks passed."); }
                else if (args.FirstOrDefault() == "--match-full-journey") await MatchFullJourney.Run(args);
                else if (args is ["--participant-board", .. var boardOutput] && boardOutput.Length <= 1) {
                    if (boardOutput is [var dir]) Directory.CreateDirectory(dir);
                    await ParticipantBoardChecks(boardOutput.FirstOrDefault()); Console.WriteLine($"Participant board: {passed} checks passed.");
                }
                else if (args is ["--wave-overlay", var input, var png]) WaveOverlayExample(input,png);
                else if (args.FirstOrDefault() == "--minute-route") {
                    MinuteRouteChecks(args.ElementAtOrDefault(1) ?? ".tmp/minute-route-qa", args.ElementAtOrDefault(2));
                    Console.WriteLine($"Off-screen minute route: {passed} checks passed.");
                }
                else if (args.FirstOrDefault() == "--minute-live-loop") { await MinuteLiveLoopTests(); Console.WriteLine($"WPF minute live loop: {passed} checks passed."); }
                else if (args is ["--model-providers"]) { await ModelProviderChecks(); Console.WriteLine($"Offline WPF model providers: {passed} checks passed."); }
                else if (args is ["--team-intel", var output]) {
                    Directory.CreateDirectory(output);
                    await SpectatorPanel(output, Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!, isolated: true);
                    Console.WriteLine($"Synthetic Plus receiver → coach → match state: {passed} checks passed.");
                }
                else if (args is ["--status-only", var statusOutput]) {
                    await StatusOnly(statusOutput, Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!, typeof(GameWindowUi), isolated: true);
                    Console.WriteLine($"Offline legacy status: {passed} checks passed.");
                }
                else if (args is ["--layout", var layoutOutput]) {
                    await LayoutChecks(layoutOutput, Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!);
                    Console.WriteLine($"Home page and settings groups of both clients: {passed} checks passed.");
                }
                else if (args is ["--real-dota"]) { await RealDota(); Console.WriteLine($"Real Dota window pipeline: {passed} checks passed."); }
                else if (args.FirstOrDefault()?.StartsWith("--", StringComparison.Ordinal) == true)
                    throw new ArgumentException("Unknown UI test option or missing arguments: " + args[0]);
                else { await Run(args.FirstOrDefault() ?? ".tmp/twopc-ui"); Console.WriteLine($"Two native WPF clients: {passed} UI checks passed."); }
                app.Shutdown(0);
            }
            catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(1); }
        };
        Environment.ExitCode = app.Run();
    }
    // Software drawing into memory only. No Window, game client, WGC, input or desktop capture.
    private static void OfflinePractice()
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception(name); Console.WriteLine("PASS " + name); }
        static LaneDrill.Reading At(int x,int y,int fill,bool ally,LaneCreepType kind,long id) {
            var bar=new CreepBar(x,y,80,8,fill,ally);
            return new(bar,kind,LaneHealthProfile.Estimate(bar,kind==LaneCreepType.Ranged?300:550),id,1)
                {RangedAlternative=kind==LaneCreepType.Unknown?LaneHealthProfile.Estimate(bar,300):null};
        }
        LaneDrill.Reading[] wave=[At(5,2,78,true,LaneCreepType.Melee,1), At(940,420,39,true,LaneCreepType.Ranged,2),
            At(940,447,39,false,LaneCreepType.Unknown,3), At(1838,1070,5,false,LaneCreepType.Melee,4)];
        var layout=new HealthLayout(); var ink=new HealthInk(); layout.Arrange(wave,1920,1080);
        var tags=layout.Tiles.SelectMany(tile=>tile.Tags).ToList();
        Check(tags.Count==4 && tags.Select(t=>t.Reading.Revision).Distinct().Count()==4 && wave.Select(HealthInk.Label)
            .SequenceEqual(new[]{"近≈550 HP","远≈150 HP","近≈275 / 远≈150 HP","近≈35 HP"}),"all labels show HP, even before classification, and mark the correct type");
        Check(tags.All(t=>t.Box.X>=0 && t.Box.X+t.Box.Width<=1920 && t.Box.Y>=0 && t.Box.Y+t.Box.Height<=1080) &&
            tags.All(t=>wave.All(r=>!HealthLayout.Touches(t.Box,new(r.Bar.X,r.Bar.Y,r.Bar.Width,r.Bar.Height)))) &&
            !tags.Where((t,i)=>tags.Skip(i+1).Any(u=>HealthLayout.Touches(t.Box,u.Box))).Any(),
            "edge and crowded labels stay on-screen without covering a creep bar or another number");
        var tiles=layout.Tiles.Where(t=>t.Tags.Count>0).ToArray();
        Check(tiles.Length<tags.Count && tiles.All(t=>t.Bounds.Width<220 && t.Bounds.Height<90),
            "adjacent numbers share tight tiles; distant numbers do not require a fullscreen surface");
        var r0=tags[0].Box; var physical=HealthLayout.Screen(r0,1920,1080,-3840,24,3840,2160);
        Check(physical==new Int32Rect(-3840+2*r0.X,24+2*r0.Y,2*r0.Width,2*r0.Height),
            "tile coordinates scale correctly on an offset 4K monitor");
        var visual=new DrawingVisual(); using(var dc=visual.RenderOpen()) ink.Draw(dc,tags,1920,1080,new Size(1920,1080),1);
        var bitmap=new RenderTargetBitmap(1920,1080,96,96,PixelFormats.Pbgra32); bitmap.Render(visual);
        byte[] pix=new byte[1920*1080*4]; bitmap.CopyPixels(pix,1920*4,0);
        bool HasInk(Int32Rect box) {
            int dark=0,light=0;
            for(int y=box.Y;y<box.Y+box.Height;y++) for(int x=box.X;x<box.X+box.Width;x++) {
                int p=4*(y*1920+x); if(pix[p+3]<200) continue;
                if(pix[p]<20) dark++; if(pix[p]>200 && pix[p+1]>200 && pix[p+2]>200) light++;
            }
            return dark>30 && light>10;
        }
        Check(tags.All(t=>HasInk(t.Box)) && wave.All(r=>pix[4*(r.Bar.Y*1920+r.Bar.X+r.Bar.Width/2)+3]==0),
            "software render: each number has white text on a dark backing; the underlying bar remains unobstructed");
        var dual=wave[2]; var absolute=wave[1];
        Check(ReferenceEquals(ink.Glyph(dual,1),ink.Glyph(dual with {Revision=99},1)) &&
            !ReferenceEquals(ink.Glyph(dual,1),ink.Glyph(dual with {RangedAlternative=151},1)) &&
            !ReferenceEquals(ink.Glyph(absolute,1),ink.Glyph(absolute with {Type=LaneCreepType.Melee},1)),
            "glyph cache distinguishes identities and both conditional estimates");
        var packed=Enumerable.Range(0,8).Select(i=>wave[1] with {Revision=i+10}).ToArray();
        layout.Arrange(packed,1920,1080);
        Check(layout.Placed<packed.Length,"unplaceable numbers are omitted instead of being assigned to a distant bar");
        layout.Arrange([],1920,1080); Check(layout.Placed==0 && layout.Tiles.All(t=>t.Tags.Count==0),"empty frame removes every tile");
        const long t0=TimeSpan.TicksPerSecond, ms=TimeSpan.TicksPerMillisecond;
        var timing=new PracticeTimings(); timing.SkipStale();
        timing.Readout(t0+20*ms,t0+50*ms,2); timing.Readout(t0+100*ms,t0+151*ms,1);
        timing.Draw(t0+40*ms,t0+92*ms,[tags[0]]); timing.Draw(t0+100*ms,t0+105*ms,[tags[0]]);
        var report=timing.Read();
        Check(PracticeTimings.Fresh(t0,t0+50*ms) && !PracticeTimings.Fresh(t0,t0+50*ms+1) && !PracticeTimings.Fresh(t0,t0-1) &&
            report.FrameToReadout is {Count:2, P95UpperMs:51, Over50:1} && report.FrameToWpfDraw is {Count:2, Over50:1} &&
            report.ReadingChangeToFirstDraw is {Count:1, P95UpperMs:92, Over50:1} && report.ObservedReadingChanges==3 &&
            report.StaleBeforeReadout==1 && report.StaleAfterReadout==1,
            "coalesced draws retain first-change time; repeated paints, missed changes and stale frames are not successes");
        Check(PracticeTimings.Observation(t0+4*ms,t0,t0+ms,150,out long anchor) && anchor==t0 &&
            !PracticeTimings.Observation(t0,t0+60*ms,t0+61*ms,150,out _) &&
            !PracticeTimings.Observation(t0+30*ms,t0,t0+ms,150,out _),
            "slight source-clock lead uses actual dequeue; genuinely old or implausibly future frames stay rejected");
        var lost=new PracticeTimings();
        lost.Readout(t0,t0+ms,1,1); lost.Readout(t0+20*ms,t0+21*ms,1,1);
        var afterCountdown=tags[0] with {Reading=tags[0].Reading with {Revision=55,ChangedAt=1.04,LossRevision=54,LossAt=1.02}};
        lost.Draw(t0+40*ms,t0+60*ms,[afterCountdown]); lost.Draw(t0+50*ms,t0+65*ms,[afterCountdown]);
        Check(lost.Read() is {ObservedHealthLosses:2,HealthLossToFirstWpfDraw:{Count:1,P95UpperMs:40}},
            "a countdown redraw does not re-date a loss or hide a loss coalesced out of the UI");
        foreach(int fps in new[]{LastHitPractice.EconomyFps,LastHitPractice.FastFps}) {
            const long start=TimeSpan.TicksPerSecond*24L*3600*30; // Large QPC offset, many excess source frames.
            int slots=Enumerable.Range(0,1000).Select(i=>WindowVideoSource.FrameSlot(start+i*10000,fps)).Distinct().Count();
            Check(slots==fps, $"1000 synthetic timestamps allow only {fps} GPU submission slots in one second");
        }
    }
    private static T Field<T>(Window w, string name) => (T)w.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(w)!;
    private static void Set(Window w, string name, object value) => w.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(w, value);
    private static T Member<T>(object o, string name) => (T)o.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(o)!;
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        yield return parent;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is DependencyObject d) foreach (var x in Children(d)) yield return x;
    }
    private static void Click(Window w, string content) => Children(w).OfType<Button>()
        .Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    // AppPages (TwoPc.SharedUi) names its parts; both clients compile their own copy, so tests go by name.
    private static FrameworkElement HomePage(Window w) => Children(w).OfType<FrameworkElement>().Single(e => e.Name == "HomePage");
    private static FrameworkElement SettingsPage(Window w) => Children(w).OfType<FrameworkElement>().Single(e => e.Name == "SettingsPage");
    private static string[] SettingsGroups(Window w) => Children(w).OfType<FrameworkElement>()
        .Where(e => e.Name == "SettingsGroup").Select(e => (string)e.Tag).ToArray();
    private static FrameworkElement SettingsGroup(Window w, string name) => Children(w).OfType<FrameworkElement>()
        .Single(e => e.Name == "SettingsGroup" && Equals(e.Tag, name));
    /// <summary>Opens the settings page on one group through the real buttons.</summary>
    private static void ShowGroup(Window w, string name)
    {
        if (SettingsPage(w).Visibility != Visibility.Visible) Click(w, "设置");
        Children(w).OfType<Button>().Single(b => Equals(b.Tag, "SettingsTab") && Equals(b.Content, name))
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    }
    private static void ShowHome(Window w) { if (HomePage(w).Visibility != Visibility.Visible) Click(w, "返回主页"); }
    private static void SelectSource(Window game, string prefix)
    {
        var source = Field<ComboBox>(game, "source");
        source.SelectedItem = source.Items.Cast<string>().Single(s => s.StartsWith(prefix, StringComparison.Ordinal));
    }
    private static void CheckDefaultProfiles(bool production = true)
    {
        bool developer = Features.VideoUpload;
        Features.VideoUpload = !production && developer;
        var dir = Directory.CreateTempSubdirectory("game-controls-");
        try {
            var video = Path.Combine(dir.FullName, "game-video.json");
            foreach (bool upgrade in new[] { false, true }) {
                // The old file records encoder preferences, not consent to capture the next game.
                if (upgrade) new GameVideoSettings { Fps = 120, Resolution = "native", Encoder = "software" }.Save(video);
                var game = new GameWindowUi(null, dir.FullName, video, Path.Combine(dir.FullName, "overlay.json"));
                try { CheckGameControls(game); } finally { game.Close(); }
            }
        } finally { dir.Delete(true); Features.VideoUpload = developer; }
    }
    private static void CheckGameControls(Window game)
    {
        var controls = Children(game).ToArray();
        var source = Field<ComboBox>(game, "source");
        if (!SettingsGroups(game).SequenceEqual(Features.VideoUpload ? ["游戏内显示", "小兵血量", "Dota 与 GSI", "开发测试", "调试"] :
                ["游戏内显示", "小兵血量", "Dota 与 GSI", "调试"]))
            throw new Exception("The released game client has no connection-mode or video settings: no picture may leave the game machine");
        if (!controls.OfType<Button>().Any(b => Equals(b.Content, "连接")) || !controls.OfType<Button>().Any(b => Equals(b.Content, "显示小兵血量")) ||
            controls.OfType<Button>().Any(b => b.Content is "试算" or "开始本机练习" or "停止显示血量") ||
            typeof(GameWindowUi).Assembly.GetType("DotaCoach.TwoPc.LastHitPractice") != null)
            throw new Exception("The normal client must retain local creep HP but must not ship the experimental practice UI or worker");
        // Production: no connection-mode selector at all, so no picture mode can be picked; UI tests opt in to it.
        if (controls.Contains(source) != Features.VideoUpload || source.SelectedItem is not string mode ||
            !mode.StartsWith("本人 GSI＋可选本机小兵血量", StringComparison.Ordinal) ||
            Field<Grid>(game, "preview").Visibility != Visibility.Collapsed || controls.Contains(Field<ComboBox>(game, "fpsChoice")) ||
            Field<CancellationTokenSource?>(game, "active") != null || Field<GsiReceiver?>(game, "gsi") != null || Field<IAsyncDisposable?>(game, "creepHp") != null)
            throw new Exception("The initial connection must be the photo-free own-GSI route with optional local HP, not video, basic status or a local screen scan");
    }
    private static CheckBox DebugSwitch(Window w) => Children(w).OfType<CheckBox>()
        .Single(c => Equals(c.Content, "开启 Debug 记录"));
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
    private static Window OfflineCoach(Type type, string settings, string debugRoot)
    {
        settings = Path.GetFullPath(settings);
        string root = Path.GetDirectoryName(settings)!;
        new LiveCoachSettings { FoundryEnvFile = Path.Combine(root, "absent-" + Guid.NewGuid().ToString("N") + ".env") }.Save(settings);
        var w = (Window)Activator.CreateInstance(type, [null, false, settings, debugRoot,
            (Func<HttpMessageHandler>)(() => throw new InvalidOperationException("No network provider in the offline UI fixture"))])!;
        Set(w, "scanFile", Path.Combine(root, "no-scan", "live.json"));
        return w;
    }
    private static async Task<CoachHost> FixtureListener(Window coach, int port)
    {
        if (port == 0) { // Isolated subcommand: kernel-assigned port; window takes ownership as in the other WPF fixtures.
            var host = await CoachHost.Start(IPAddress.Loopback, 0);
            string scan = Field<string>(coach, "scanFile");
            host.Mailbox.Icons = key => ScanFeed.Icon(scan, key);
            Set(coach, "host", host); return host;
        }
        Field<TextBox>(coach, "port").Text = port.ToString(); Click(coach, "启动接收");
        await Wait(() => Field<CoachHost?>(coach, "host")?.Port == port && !Field<bool>(coach, "busy"), "coach starts its real loopback listener");
        return Field<CoachHost>(coach, "host");
    }
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        CheckDefaultProfiles();
        await ModelProviderChecks();
        await LockedWriterUi(output);
        MinuteRouteChecks(output);
        await ParticipantBoardChecks(output);
        await MinuteLiveLoopTests();
        var coachType = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!;
        var gameType = Assembly.Load("DotaCoach.GameClient").GetType("DotaCoach.TwoPc.GameWindowUi")!;
        await LayoutChecks(output, coachType);
        string debugRoot = Path.GetFullPath(Path.Combine(output, "debug"));
        var coach = OfflineCoach(coachType, Path.Combine(output, "coach-settings.json"), debugRoot);
        string videoSettings = Path.GetFullPath(Path.Combine(output, "game-video.json"));
        string overlaySettings = Path.GetFullPath(Path.Combine(output, "game-overlay.json"));
        File.Delete(overlaySettings);
        var game = (Window)Activator.CreateInstance(gameType, [null, debugRoot, videoSettings, overlaySettings])!;
        // Inspect the normal client's replacement for the retired practice pane. Opening local HP settings
        // must not start a screen scan, GSI or a connection; the tower calculation lives in LastHitTests.
        coach.Show(); game.Show();
        await Wait(() => HomePage(coach).Visibility == Visibility.Visible && HomePage(game).Visibility == Visibility.Visible &&
            SettingsPage(coach).Visibility == Visibility.Collapsed && SettingsPage(game).Visibility == Visibility.Collapsed,
            "both clients open on their home page");
        CheckGameControls(game);
        ShowGroup(game, "小兵血量"); ShowHome(game);
        await Wait(() => Field<Button>(game, "creepHpButton").IsEnabled && Field<IAsyncDisposable?>(game, "creepHp") == null &&
            Field<CancellationTokenSource?>(game, "active") == null && Field<GsiReceiver?>(game, "gsi") == null,
            "opening the local creep HP help does not start capture, pairing or GSI");
        await Wait(() => DebugSwitch(coach).IsChecked != true && DebugSwitch(game).IsChecked != true &&
            !Directory.Exists(debugRoot), "Debug is opt-in and creates no recording directory while off");
        DebugSwitch(coach).IsChecked = true; DebugSwitch(game).IsChecked = true;
        await Wait(() => Recorder(coach) != null && Recorder(game) != null, "both real UI Debug switches start isolated local sessions");
        string coachDebug = Recorder(coach)!.DirectoryPath, gameDebug = Recorder(game)!.DirectoryPath;
        await Wait(() => Equals(Field<ComboBox>(coach, "address").SelectedItem, "127.0.0.1"),
            "same-machine default is loopback, not a virtual or LAN adapter");
        Field<TextBox>(coach, "port").Text = "38479";
        Click(coach, "启动接收");
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
        Click(coach, "发送测试提醒");
        await Wait(() => Field<TextBlock>(coach, "notice").Text.StartsWith("未发送"), "test reminder is refused before any frame arrives");
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        SelectSource(game, "诊断视频");
        await Wait(() => Field<Grid>(game, "preview").Visibility == Visibility.Visible && Field<ComboBox>(game, "fpsChoice").SelectedItem as string == "60 fps" &&
            Field<ComboBox>(game, "resolutionChoice").SelectedIndex == 2 && Field<ComboBox>(game, "captureChoice").SelectedIndex == 0,
            "game UI defaults to 60 fps, 1080p and background-capable window capture");
        Field<TextBox>(game, "bitrateChoice").Text = "abc";
        Click(game, "连接");
        await Wait(() => Field<TextBlock>(game, "status").Text.Contains("码率") && Field<CancellationTokenSource?>(game, "active") == null,
            "invalid bitrate is rejected before any capture starts");
        Field<TextBox>(game, "bitrateChoice").Text = "0";
        Click(game, "连接");
        await Wait(() => host.Mailbox.Snapshot().Frames >= 3 && host.Mailbox.Latest()?.Video != null,
            "game client continuously sends real H264 diagnostic video through pinned WSS");
        await Wait(() => File.Exists(videoSettings) && GameVideoSettings.Load(videoSettings).Fps == 60 && !Field<ComboBox>(game, "fpsChoice").IsEnabled &&
            Equals(Field<Button>(game, "connect").Content, "断开"),
            "chosen video settings persist and lock while streaming");
        await Wait(() => Field<Image>(coach, "image").Source != null, "coach preview receives decoded bitmap");
        await Wait(() => Field<Image>(game, "image").Source != null, "game preview receives capture bitmap");
        await Wait(() => Recorder(coach)!.Status.Images >= 2 && Recorder(game)!.Status.Images == 0,
            "only coach samples decoded video; game debug does not duplicate images");
        long videoBefore = host.Mailbox.Snapshot().Frames, measuredAt = Environment.TickCount64;
        await Task.Delay(2200);
        long receivedVideo = host.Mailbox.Snapshot().Frames - videoBefore;
        double measuredSeconds = (Environment.TickCount64 - measuredAt) / 1000.0;
        Console.WriteLine($"VIDEO UI: {receivedVideo} received frames over {measuredSeconds:F3}s = {receivedVideo / measuredSeconds:F1} fps " +
            $"(synthetic 1280x720 source at a 60 fps target; not game FPS). {Field<TextBlock>(game, "metrics").Text.Replace("\n", " | ")}");
        await Wait(() => receivedVideo / measuredSeconds > 40, "60 fps target delivers well above the old 30 fps cap end to end");
        var source = host.Mailbox.Latest()!;
        host.Mailbox.SetReminder(source.Meta.SessionId, source.Meta.Sequence, "manual-link-test",
            "双机测试成功：字幕和小地图测试标记已回传。", new ScreenMark(.12, .80));
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机测试成功"), "game UI shows a returned caption");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 1, "UI adds returned normalized marker element");
        Render(coach, Path.Combine(output, "coach.png")); Render(game, Path.Combine(output, "game.png"));
        host.Mailbox.ClearReminder();
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 0 &&
            Field<TextBlock>(game, "caption").Text.Contains("无有效提醒"), "a cleared reminder clears game caption and marker");
        await GameOverlay(output, game, host, overlaySettings);
        await Task.Delay(500);
        Click(coach, "发送测试提醒");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机链路测试") &&
            Field<TextBlock>(coach, "notice").Text.StartsWith("已发送"), "coach Debug test button reaches the game caption");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("无有效提醒"), "reminder expires without renewing on polls", 12000);
        Click(coach, "撤销配对");
        await Wait(() => Field<CancellationTokenSource?>(game, "active") == null &&
            !Field<bool>(game, "transportConnected") && Field<ComboBox>(game, "source").IsEnabled &&
            host.Mailbox.Snapshot().SessionId == null,
            "revocation stops both video workers, releases controls and clears the session");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 0, "revocation leaves no stale marker");
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        Click(game, "连接");
        await Wait(() => host.Mailbox.Snapshot().Connected, "new pairing reconnects with a fresh session");
        Click(game, "断开");
        await Wait(() => host.Mailbox.Snapshot().SessionId == null, "game stop ends capture session on coach");
        await Wait(() => Field<Image>(coach, "image").Source == null, "coach clears disconnected preview");
        Click(game, "连接"); Click(game, "断开");
        await Wait(() => Field<CancellationTokenSource?>(game, "active") == null, "stop interrupts game connection startup");
        Click(coach, "停止接收");
        await Wait(() => Field<CoachHost?>(coach, "host") == null, "coach stop closes listener");
        await Wait(() => Field<ComboBox>(coach, "address").IsEnabled,
            "stopping receiver unlocks address selection");
        Click(coach, "启动接收"); Click(coach, "停止接收");
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
        var emptyCoach = OfflineCoach(coachType, Path.Combine(output, "unused-profile", "settings.json"), debugRoot);
        var emptyGame = (Window)Activator.CreateInstance(gameType)!;
        emptyCoach.Show(); emptyGame.Show(); emptyCoach.Close(); emptyGame.Close();
        await Wait(() => !emptyGame.IsVisible && !emptyCoach.IsVisible, "unused clients close without reentrant-close errors");
        await OccludedWindowCapture();
        await LiveMode(output, coachType, gameType);
        await StatusOnly(output, coachType, gameType);
    }

    /// <summary>Legacy status is still selectable and carries ordinary subtitles, not participant proof.
    /// The default participant GSI transport and the positive board round trip are in ParticipantBoardChecks.</summary>
    private static async Task StatusOnly(string output, Type coachType, Type gameType, bool isolated = false)
    {
        string debugRoot = Path.GetFullPath(Path.Combine(output, "status-debug"));
        var coach = OfflineCoach(coachType, Path.Combine(output, "status-coach.json"), debugRoot);
        var game = (Window)Activator.CreateInstance(gameType, [null, debugRoot, Path.Combine(output, "status-video.json"),
            Path.Combine(output, "status-overlay.json")])!;
        coach.Show(); game.Show();
        CheckGameControls(game);
        SelectSource(game, "基础状态");
        await Wait(() => Field<Grid>(game, "preview").Visibility == Visibility.Collapsed &&
            Field<TextBlock>(game, "metrics").Visibility == Visibility.Collapsed && !Children(game).Contains(Field<ComboBox>(game, "fpsChoice")) &&
            Field<Button>(game, "creepHpButton").IsEnabled, "basic status does not need video controls and does not start local creep recognition");
        var host = await FixtureListener(coach, isolated ? 0 : 38482);
        var pair = host.Pair(IPAddress.Loopback);
        if (DotaCoach.Desktop.GameWindow.Find(out _) == null) {
            // Exercise the real no-window status loop, not Start(): nothing installs a cfg or opens the player's GSI port.
            var runStatus = gameType.GetMethod("RunStatus", BindingFlags.Instance | BindingFlags.NonPublic)!
                .CreateDelegate<Func<Pairing, bool, CancellationToken, Task>>(game);
            using var stop = new CancellationTokenSource();
            var waiting = runStatus(pair, false, stop.Token);
            try {
                await Wait(() => Field<TextBlock>(game, "status").Text.Contains("等待 Dota 2 窗口") &&
                    host.Mailbox.Snapshot() is { SessionId: not null, Connected: false, Frames: 0 },
                    "without a Dota window basic status connects but sends nothing");
            } finally { stop.Cancel(); await waiting; }
            await Wait(() => host.Mailbox.Snapshot().SessionId == null, "disconnecting while waiting for Dota ends the session");
        }
        SelectSource(game, "诊断状态");
        Field<PasswordBox>(game, "pairing").Password = pair.Encode(); Click(game, "连接");
        await Wait(() => host.Mailbox.Snapshot() is { Connected: true, Frames: >= 3, Bytes: 0 } &&
            host.Mailbox.Latest() is { HasImage: false, Jpeg.Length: 0, Video: null, Meta: { Player: null, MatchPhase: null } } &&
            Field<TextBlock>(game, "status").Text.StartsWith("已连接") && Field<GsiReceiver?>(game, "gsi") == null &&
            Field<Image>(coach, "image").Source == null && Field<Image>(game, "image").Source == null && Field<int>(coach, "modelCalls") == 0,
            "diagnostic status keeps presence without GSI, pictures, preview or model attempts");
        Click(coach, "发送测试提醒");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机链路测试") && Field<TextBlock>(coach, "notice").Text.StartsWith("已发送"),
            "the coach's test subtitle reaches the game client without video");
        ShowGroup(coach, "Plus 观战与扫描"); Render(coach, Path.Combine(output, "coach-status-only.png")); ShowHome(coach);
        Render(game, Path.Combine(output, "game-status-only.png"));
        var board = HudBoard(MinimapLayout.Suggest(1280, 720));
        // The coach's tick republishes its own (here empty) last-known panel; hold the fixture panel instead.
        var coachTimer = Field<System.Windows.Threading.DispatcherTimer>(coach, "timer");
        coachTimer.Stop();
        host.Mailbox.SetEnemies(board);
        string session = host.Mailbox.Snapshot().SessionId!;
        var overlay = Field<AdviceOverlay>(game, "overlay");
        await Task.Delay(1600);
        await Wait(() => host.Mailbox.Enemies(session) != null && Member<EnemyBoard?>(overlay, "enemies") == null,
            "the developer status lanes carry subtitles only: they never poll or draw the enemy panel");
        coachTimer.Start();
        Click(game, "断开");
        await Wait(() => host.Mailbox.Snapshot().SessionId == null && Field<CancellationTokenSource?>(game, "active") == null &&
            Field<TextBlock>(game, "status").Text.Contains("已断开"), "disconnecting ends the status session");
        game.Close(); coach.Close();
        await Wait(() => !game.IsVisible && !coach.IsVisible, "status-only clients close cleanly");
    }

    /// <summary>
    /// Styles through the real controls, no board on diagnostic video, then the drawing code with fixture art
    /// off screen. ParticipantBoardChecks exercises the real game-client poll on authenticated participant GSI.
    /// </summary>
    private static async Task GameOverlay(string output, Window game, CoachHost host, string file)
    {
        var panel = Field<OverlayPanel>(game, "overlayPanel");
        var overlay = Field<AdviceOverlay>(game, "overlay");
        await Wait(() => panel.Current == new GameOverlaySettings() && overlay.Settings == panel.Current &&
            Children(panel).OfType<Slider>().Count() == 10 && !File.Exists(file), "overlay panel starts from the default style and writes nothing");
        Member<Slider>(panel, "x").Value = 0; Member<Slider>(panel, "y").Value = 100;
        Member<CheckBox>(panel, "alerts").IsChecked = false;
        Member<CheckBox>(panel, "minimapRight").IsChecked = true; Member<Slider>(panel, "minimapSize").Value = 120;
        var background = Member<ComboBox>(panel, "backgroundColor");
        background.SelectedItem = background.Items.OfType<ComboBoxItem>().Single(i => (string)i.Tag == "#FF5A5A");
        await Wait(() => File.Exists(file) && GameOverlaySettings.Load(file) is
            { X: 0, Y: 100, ShowAlerts: false, BackgroundColor: "#FF5A5A", ShowWards: true, MinimapRight: true, MinimapSize: 120 } s &&
            overlay.Settings == s, "sliders, switches and colours apply to the overlay at once and are saved");
        var latest = host.Mailbox.Latest()!;
        host.Mailbox.SetReminder(latest.Meta.SessionId, latest.Meta.Sequence, "local-vision-observation", "剃刀 离开视野 · 最后在中路", null);
        await Wait(() => Field<TextBlock>(game, "caption").Text is { } t && t.Contains("剃刀 离开视野") && t.Contains("不在游戏里显示"),
            "a kind hidden in game still shows in the client window, marked as hidden");
        host.Mailbox.ClearReminder();
        var icons = new Dictionary<string, byte[]> {
            ["npc_dota_hero_razor"] = SolidPng(Colors.Red, 32, 32), ["item_blink"] = SolidPng(Colors.Blue, 88, 64) };
        int iconQueries = 0;
        host.Mailbox.Icons = key => { Interlocked.Increment(ref iconQueries); return icons.GetValueOrDefault(key); };
        var board = new EnemyBoard([new("npc_dota_hero_razor", 12, ["item_blink", null, null, null, null, null], null, 3),
            new("npc_dota_hero_lion", 9, [null, null, null, null, null, null], null, 75), new("npc_dota_hero_pudge", null, null, null, null)]);
        host.Mailbox.SetEnemies(board);
        await Task.Delay(1000);
        await Wait(() => host.Mailbox.Enemies(latest.Meta.SessionId) == null && Member<EnemyBoard?>(overlay, "enemies") == null &&
            Volatile.Read(ref iconQueries) == 0,
            "diagnostic video cannot deliver an unbound synthetic board or probe scanner icons");
        var styleIcons = new IconCache();
        foreach (var (key, png) in icons) styleIcons.Add(key, png, 0); // Offscreen fixture, never a live board.
        // A release bundles the icons beside the program: those are never asked for; anything else still is.
        string bundle = Path.Combine(output, "bundled-icons");
        Directory.CreateDirectory(bundle);
        File.WriteAllBytes(Path.Combine(bundle, "item_blink.png"), SolidPng(Colors.Lime, 88, 64));
        File.WriteAllBytes(Path.Combine(bundle, "npc_dota_hero_lion.png"), SolidPng(Colors.Lime, 32, 32));
        File.WriteAllText(Path.Combine(bundle, "npc_dota_hero_razor.png"), "not an image");
        var bundled = new IconCache(bundle);
        await Wait(() => bundled["npc_dota_hero_lion"] is BitmapSource { PixelWidth: 32 } &&
            bundled.Missing(board, 0).SequenceEqual(["npc_dota_hero_razor", "npc_dota_hero_pudge"]) && bundled.Count == 2,
            "bundled icons load from the program folder; missing or broken ones are asked from the coach");

        // A saved local preview ruler is NOT a calibration. Render the same four legacy estimate cases
        // only on a match-labelled, confirmed HUD: even the best estimate is hollow, not a sighting.
        WardMark[] marks = [new("npc_dota_hero_lion", "observer", .25, .25, 0, .6, 20, 340), new("npc_dota_hero_lion", "observer", .75, .25, 1, .3, 20, 340),
            new("npc_dota_hero_lion", "sentry", .25, .75, 0, 1, 50, 370), new("npc_dota_hero_pudge", "ward", .75, .75, 0, 1, 380, 40, Rough: true)];
        var confirmed = new MinimapLayout(1280, 720, 10, 410, 300);
        var wards = new EnemyBoard([], marks, BoardMatch, "radiant", confirmed, [new("sentry", "dire", 2368, -2368)]);
        Canvas WardCanvas(EnemyBoard b) {
            var c = new Canvas { Width = 1280, Height = 720 };
            AdviceOverlay.Compose(c, null, panel.Current, 1.5, b, viewport: (1280, 720));
            return c;
        }
        var minimap = WardCanvas(wards).Children.OfType<Canvas>().Single();
        var local = panel.Current.Minimap(1280, 720);
        await Wait(() => Canvas.GetLeft(minimap) == confirmed.Left && Canvas.GetTop(minimap) == confirmed.Top &&
            minimap.Width == confirmed.Size && WardCanvas(wards with { Minimap = null }).Children.OfType<Canvas>().Single() is var fallback &&
            local.Left > 640 && local.Size > 720 * 2 / 9d && Canvas.GetLeft(fallback) == local.Left && Canvas.GetTop(fallback) == local.Top &&
            fallback.Width == local.Size && WardCanvas(wards with { MatchId = null, Team = null }).Children.Count == 0,
            "a confirmed left HUD wins over the local right/120% minimap setting, which places marks only without a confirmed box");
        minimap.Measure(new Size(300, 300)); minimap.Arrange(new Rect(0, 0, 300, 300)); minimap.UpdateLayout();
        var wardBitmap = new RenderTargetBitmap(300, 300, 96, 96, PixelFormats.Pbgra32);
        wardBitmap.Render(minimap);
        var wardPixels = new byte[300 * 300 * 4]; wardBitmap.CopyPixels(wardPixels, 300 * 4, 0);
        using (var stream = File.Create(Path.Combine(output, "ward-marks.png"))) {
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(wardBitmap)); encoder.Save(stream);
        }
        byte[] Pixel(int x, int y) { int i = (y * 300 + x) * 4; return wardPixels[i..(i + 4)]; }
        bool Rim(int x, int y, Func<byte[], bool> color) => Enumerable.Range(x - 20, 41).Any(i =>
            Enumerable.Range(y - 20, 41).Any(j => color(Pixel(i, j))));
        bool Gold(byte[] p) => p is [< 60, > 75, > 100, > 100];
        await Wait(() => new[] { (75, 75), (225, 75), (75, 225), (225, 225) }.All(v => Pixel(v.Item1, v.Item2)[3] == 0) &&
            Rim(75, 75, Gold) && Rim(225, 75, Gold) && Rim(75, 225, p => p is [> 100, > 65, < 60, > 100]) &&
            Rim(225, 225, p => p is [> 80, > 80, > 80, > 80]) && Pixel(187, 187) is [> 160, > 120, < 60, > 180],
            "observer/sentry, lower-ranked and rough guesses have coloured rims and empty centres; only the observed sentry has a filled centre");

        const int W = 1280, H = 720;
        var alert = new Reminder("a", "s", 1, "local-vision-observation", "剃刀 离开视野 · 最后在中路；主宰 新装备 狂战斧", null, W, H, 3000);
        (Canvas Canvas, byte[] Pixels) Draw(GameOverlaySettings style, Reminder? r, string? png = null, EnemyBoard? enemies = null)
        {
            var canvas = new Canvas { Width = W, Height = H };
            AdviceOverlay.Compose(canvas, r, style, 1, enemies, key => styleIcons[key]);
            canvas.Measure(new Size(W, H)); canvas.Arrange(new Rect(0, 0, W, H)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(W, H, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var pixels = new byte[W * H * 4];
            bitmap.CopyPixels(pixels, W * 4, 0);
            if (png != null) {
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var stream = File.Create(Path.Combine(output, png)); encoder.Save(stream);
            }
            return (canvas, pixels);
        }
        Rect Box(Canvas c) => c.Children.OfType<Border>().Select(b => new Rect(Canvas.GetLeft(b), Canvas.GetTop(b), b.ActualWidth, b.ActualHeight)).Single();
        byte[] At(byte[] pixels, double x, double y) { int i = ((int)y * W + (int)x) * 4; return pixels[i..(i + 4)]; }

        var defaults = new GameOverlaySettings();
        var (plain, plainPixels) = Draw(defaults, alert, "caption-default.png");
        var box = Box(plain);
        await Wait(() => Math.Abs(box.Left + box.Width / 2 - W / 2.0) < 1 && Math.Abs(box.Top - (H - box.Height) * .1) < 1 &&
            box.Width < W * .7 && At(plainPixels, box.Left + 4, box.Top + box.Height / 2)[3] is >= 205 and <= 213,
            "default subtitle: centred near the top, box hugs the text, 82% opaque background");
        var custom = defaults with { X = 0, Y = 100, BackgroundColor = "#FF5A5A", BackgroundOpacity = 100, TextColor = "#FFFFFF" };
        var (moved, movedPixels) = Draw(custom, alert, "caption-bottom-left.png");
        var corner = Box(moved);
        await Wait(() => corner.Left == 0 && Math.Abs(corner.Bottom - H) < 1 &&
            At(movedPixels, 4, corner.Top + corner.Height / 2).SequenceEqual(new byte[] { 0x5A, 0x5A, 0xFF, 255 }) &&
            At(movedPixels, W / 2, box.Top + box.Height / 2)[3] == 0,
            "bottom-left style draws an opaque red box flush with the corner and nothing at the old place");
        var big = Box(Draw(defaults with { Size = 200 }, alert).Canvas);
        var narrow = Box(Draw(defaults with { Width = 25 }, alert).Canvas);
        await Wait(() => big.Height > box.Height * 1.8 && narrow.Width <= W * .25 + .5 && narrow.Height > box.Height * 1.5,
            "font size scales the box; a narrow maximum width wraps the text into more lines");
        var clear = Draw(defaults with { BackgroundOpacity = 0 }, alert).Pixels;
        await Wait(() => At(clear, box.Left + 4, box.Top + box.Height / 2)[3] == 0, "background opacity 0 leaves only the text");
        var untitled = Draw(defaults with { ShowTitle = false }, alert).Canvas;
        var advice = new Reminder("b", "s", 1, "visible-farming-advice", "去打野", new ScreenMark(.1, .8), W, H, 3000);
        await Wait(() => ((TextBlock)untitled.Children.OfType<Border>().Single().Child).Inlines.Count == 1 &&
            Draw(defaults with { ShowAlerts = false }, alert).Canvas.Children.Count == 0 &&
            Draw(defaults with { ShowAdvice = false }, advice).Canvas.Children is { Count: > 0 } marks && marks.OfType<Border>().Count() == 0,
            "without the title only the message is drawn; hidden kinds draw no subtitle but keep map marks");
        var (left, leftPixels) = Draw(defaults, null, "enemy-panel.png", board);
        var view = left.Children.OfType<StackPanel>().Single();
        double top = Canvas.GetTop(view), row = 14;
        byte[] hero = At(leftPixels, 4 + row / 2, top + row / 2), item = At(leftPixels, 4 + row + 16 + row * 88 / 64 / 2, top + row / 2);
        string Labels(int line) => string.Join(" ", ((StackPanel)view.Children[line]).Children.OfType<TextBlock>().Select(t => t.Text));
        await Wait(() => Canvas.GetLeft(view) == 4 && Math.Abs(top - (H - view.DesiredSize.Height) * .4) < 1 &&
            hero[2] > 150 && hero[0] < 40 && hero[3] is >= 185 and <= 197 && item[0] > 150 && item[2] < 40 &&
            Labels(0) == "12" && Labels(1) == "9 1分前" && Labels(2) == "? 未见" && ((StackPanel)view.Children[1]).Opacity < 1 &&
            ((StackPanel)view.Children[2]).Children.Count == 3,
            "enemy panel at the left edge: head icon and items as images at 75% opacity, level, age from 30 s, old rows dimmed, unseen heroes without slots");
        // A ticking age must not repaint the panel every few seconds: labels change at 30 s and then once a minute.
        var aged = new EnemyBoard(new[] { 29.9, 30, 59.9, 61, 125 }.Select((age, i) =>
            new EnemyHero(new[] { "npc_dota_hero_razor", "npc_dota_hero_lion", "npc_dota_hero_pudge", "npc_dota_hero_axe", "npc_dota_hero_lina" }[i],
                10, null, null, age)).ToArray());
        var agedView = Draw(defaults, null, null, aged).Canvas.Children.OfType<StackPanel>().Single();
        string Aged(int line) => string.Join(" ", ((StackPanel)agedView.Children[line]).Children.OfType<TextBlock>().Select(t => t.Text));
        await Wait(() => Enumerable.Range(0, 5).Select(Aged).SequenceEqual(["10", "10 30秒前", "10 30秒前", "10 1分前", "10 2分前"]),
            "age labels: none under 30 s, then 30 s and whole minutes");
        var bigPanel = Draw(defaults with { EnemySize = 200, EnemyY = 0 }, null, null, board).Canvas.Children.OfType<StackPanel>().Single();
        await Wait(() => bigPanel.DesiredSize.Height > view.DesiredSize.Height * 1.8 && Canvas.GetTop(bigPanel) == 0,
            "panel size and position follow the settings");
        // For looking at: the default panel at 1080p with Dota's own icons, when this machine has extracted them.
        string assets = Path.GetFullPath(".tmp/vision-assets");
        if (File.Exists(Path.Combine(assets, "manifest.json"))) {
            using var manifest = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(assets, "manifest.json")));
            var real = new IconCache();
            var sample = new EnemyBoard([
                new("npc_dota_hero_razor", 12, ["item_power_treads", "item_black_king_bar", "item_blink", "item_magic_wand", null, null], "item_pogo_stick", 2),
                new("npc_dota_hero_juggernaut", 14, ["item_phase_boots", "item_bfury", "item_manta", "item_ward_observer", null, null], null, 35),
                new("npc_dota_hero_lion", 9, ["item_tranquil_boots", "item_blink", null, null, null, null], null, 95),
                new("npc_dota_hero_pudge", null, null, null, null)]);
            foreach (string key in real.Missing(sample, 0))
                if (manifest.RootElement.GetProperty(key.StartsWith("item_") ? "items" : "heroes").TryGetProperty(key, out var entry) &&
                    entry.TryGetProperty("icon", out var icon) && icon.GetString() is { } relative)
                    real.Add(key, File.ReadAllBytes(Path.Combine(assets, relative)), 0);
            var canvas = new Canvas { Width = 1920, Height = 1080 };
            AdviceOverlay.Compose(canvas, null, defaults, 1.5, sample, key => real[key]);
            canvas.Measure(new Size(1920, 1080)); canvas.Arrange(new Rect(0, 0, 1920, 1080)); canvas.UpdateLayout();
            var bitmap = new RenderTargetBitmap(1920, 1080, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(canvas);
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var stream = File.Create(Path.Combine(output, "enemy-panel-1080p-real-icons.png")); encoder.Save(stream);
        }

        ShowGroup(game, "游戏内显示");
        await Wait(() => SettingsGroup(game, "游戏内显示").IsVisible && Children(SettingsGroup(game, "游戏内显示")).Contains(panel),
            "the overlay style panel is the visible 游戏内显示 settings group");
        Render(game, Path.Combine(output, "game-overlay.png")); ShowHome(game);
        Click(game, "恢复默认");
        await Wait(() => GameOverlaySettings.Load(file) == new GameOverlaySettings() && overlay.Settings == new GameOverlaySettings() &&
            Member<Slider>(panel, "x").Value == 50, "恢复默认 restores and saves the default style");
    }

    private static byte[] SolidPng(Color color, int width, int height)
    {
        var pixels = Enumerable.Repeat(new[] { color.B, color.G, color.R, color.A }, width * height).SelectMany(p => p).ToArray();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4)));
        using var stream = new MemoryStream(); encoder.Save(stream); return stream.ToArray();
    }

    /// <summary>
    /// Opt-in (--real-dota): the shipped game-side classes against the RUNNING Dota window, over real pinned TLS to a
    /// loopback coach that decodes every frame. Phase 2 covers Dota with another window: frames must keep coming.
    /// No GSI files are written, no model is called and no image is saved.
    /// </summary>
    private static async Task RealDota()
    {
        var surface = DotaCoach.Desktop.GameWindow.Find(out bool minimized) ??
            throw new Exception(minimized ? "Dota is minimised" : "Dota window not found");
        var settings = new GameVideoSettings();
        bool gpuEncoder = HardwareH264.Available().Count > 0;
        var size = settings.EncodedSize(surface.Width, surface.Height, gpuEncoder);
        int fps = GameVideoSettings.EffectiveFps(size.Width, size.Height, settings.Fps);
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        host.Mailbox.VideoJpegEncoder = Ui.ExtractJpeg;
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        await link.Connect(default); await link.OpenVideo(default);
        var sender = new VideoSender(await link.GetVideoTicket(default), fps);
        using var stop = new CancellationTokenSource();
        long sequence = 0;
        var capture = sender.StartCapture(() => new WindowVideoSource(surface, size.Width, size.Height, fps), false,
            () => Interlocked.Increment(ref sequence), () => (null, null), stop.Token);
        var send = Task.Run(() => sender.SendLoop(link, gpuEncoder, settings.Bitrate(size.Width, size.Height, fps), stop.Token));
        async Task<double> Measure(int ms)
        {
            long before = host.Mailbox.Snapshot().Frames, started = Environment.TickCount64;
            while (Environment.TickCount64 - started < ms) {
                await Task.Delay(750); // same renewal cadence as the game client
                sender.Renew(await link.GetVideoTicket(default));
                if (capture.IsFaulted || send.IsFaulted) throw (capture.Exception ?? send.Exception)!;
            }
            return (host.Mailbox.Snapshot().Frames - before) * 1000.0 / (Environment.TickCount64 - started);
        }
        await Measure(1500); // encoder start-up
        double visible = await Measure(4000);
        var cover = new Window { Title = "covering app", Left = surface.Left - 20, Top = surface.Top - 20, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Brushes.Magenta };
        double dpi = VisualTreeHelper.GetDpi(cover).DpiScaleX;
        cover.Left = (surface.Left - 20) / dpi; cover.Top = (surface.Top - 20) / dpi;
        cover.Width = (surface.Width + 40) / dpi; cover.Height = (surface.Height + 40) / dpi;
        cover.Show(); cover.Activate();
        double covered = await Measure(4000);
        var latest = host.Mailbox.Latest();
        cover.Close();
        stop.Cancel(); link.AbortVideo();
        try { await Task.WhenAll(capture, send); } catch when (stop.IsCancellationRequested) { }
        await link.End(default);
        var picture = latest?.Video;
        int bright = 0, magenta = 0;
        if (picture != null) {
            var bgra = picture.ToBgra(160, 90);
            for (int i = 0; i < bgra.Length; i += 4) {
                if (bgra[i] + bgra[i + 1] + bgra[i + 2] > 60) bright++;
                if (bgra[i] > 200 && bgra[i + 1] < 60 && bgra[i + 2] > 200) magenta++;
            }
        }
        Console.WriteLine($"REAL DOTA: client {surface.Width}x{surface.Height} → {size.Width}x{size.Height} @ target {fps}fps, {sender.SourceName} / {sender.EncoderName}; " +
            $"coach decoded {visible:F1} fps visible, {covered:F1} fps while fully covered; capture {sender.CaptureMs:F2} ms, encode {sender.EncodeMs:F2} ms, dropped {sender.Dropped}; " +
            $"last covered-phase frame: {bright}/{160 * 90} non-dark, {magenta} covering-window pixels");
        await Wait(() => visible > 20 && covered > 20, "real Dota window streams continuously, also while another window covers it");
        await Wait(() => picture != null && picture.Width == size.Width && bright > 160 * 90 / 4 && magenta == 0,
            "frames decoded during the covered phase show Dota content, not black and not the covering window");
    }

    /// <summary>
    /// The v0.3 "black screen when switching windows" case: a window completely covered by another one must
    /// still be captured with ITS OWN pixels (never the covering window's) through the real GPU path.
    /// </summary>
    private static async Task OccludedWindowCapture()
    {
        var target = new Window { Title = "WGC target", Width = 640, Height = 400, Left = 80, Top = 80, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Background = new SolidColorBrush(Color.FromRgb(180, 100, 25)) };
        var cover = new Window { Title = "covering app", Width = 900, Height = 700, Left = 20, Top = 20, WindowStyle = WindowStyle.None,
            ResizeMode = ResizeMode.NoResize, ShowInTaskbar = false, Topmost = true, Background = Brushes.White };
        target.Show(); cover.Show(); cover.Activate();
        await Task.Delay(600);
        nint handle = new System.Windows.Interop.WindowInteropHelper(target).Handle;
        byte[]? pixels = null; string? failure = null;
        await Task.Run(() => {
            try {
                using var gpu = new GpuDevice();
                using var capture = new WindowCapture(gpu, handle);
                using var scaler = new GpuNv12Scaler(gpu, capture.Width, capture.Height, 320, 200, 30);
                for (int i = 0; i < 100 && pixels == null; i++) {
                    using var frame = capture.Latest();
                    if (frame != null) pixels = scaler.Convert(frame, new Rect32 { Right = frame.ContentWidth, Bottom = frame.ContentHeight }).ToBgra();
                    else Thread.Sleep(30);
                }
            } catch (Exception e) { failure = e.GetType().Name + ": " + e.Message; }
        });
        cover.Close(); target.Close();
        if (failure != null) throw new Exception("Window capture failed: " + failure);
        int centre = (100 * 320 + 160) * 4;
        await Wait(() => pixels != null && Math.Abs(pixels[centre] - 25) <= 12 && Math.Abs(pixels[centre + 1] - 100) <= 12 &&
            Math.Abs(pixels[centre + 2] - 180) <= 12,
            "window capture keeps the covered game window's own pixels (not the covering app) through the GPU scaler");
    }

    private static async Task LiveMode(string output, Type coachType, Type gameType)
    {
        string env = Path.Combine(output, "fake-model.env");
        File.WriteAllText(env, "AOAI_ENDPOINT=https://unit-test.openai.azure.com/\nFOUNDRY_KEY=unit-test-only\n");
        var w = OfflineCoach(coachType, Path.Combine(output, "live-settings.json"), Path.Combine(output, "live-debug"));
        w.Show();
        DebugSwitch(w).IsChecked = true;
        string debugDirectory = Recorder(w)!.DirectoryPath;
        await Wait(() => Field<CheckBox>(w, "automatic").IsChecked == true &&
            Field<CheckBox>(w, "subtitles").IsChecked == true && Field<CheckBox>(w, "mapMarkers").IsChecked == true,
            "normal UI defaults automatic model, subtitles and map features to enabled");
        var preference = new LiveCoachSettings { FoundryEnvFile = Path.GetFullPath(env), RequestLimit = 1, AutoMinimap = false };
        Set(w, "preferences", preference);
        Field<CheckBox>(w, "autoMinimap").IsChecked = false;
        var map = new CalibratedMap("data/dota2-map-reference.json");
        Set(w, "liveMap", map);
        using var fake = new UiModel();
        var cloud = new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", fake, map, false);
        Set(w, "cloud", cloud); Set(w, "modelConfigured", true);
        Field<TextBox>(w, "port").Text = "38480"; Click(w, "启动接收");
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
                    [new("item_power_treads", null, 0)], [new("luna_moon_glaive", 4, 0)]), 10,
                new MatchWitness("9876543210", "radiant", "npc_dota_hero_luna", 720, "playing", false)), jpeg);
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
        Click(w, "预设：左下");
        await Wait(() => Field<TextBox>(w, "mapTop").Text == "1680" && Field<TextBox>(w, "mapSize").Text == "480",
            "4K calibration UI proposes source-pixel bounds, not 720p or JPEG bounds");
        Click(w, "确认校准");
        await Wait(() => LiveCoachSettings.Load(Path.Combine(output, "live-settings.json")).Calibrations.Length == 1,
            "actual calibration handler persists the confirmed 4K rectangle");
        var layout = new MinimapLayout(3840, 2160, 0, 1680, 480);
        using var marked = new UiModel { Route = ["neutralcamp_good_11", "npc_dota_goodguys_tower2_bot"] };
        Set(w, "preferences", Field<LiveCoachSettings>(w, "preferences") with { RequestLimit = 4 });
        Set(w, "cloud", new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", marked, map, false, layout));
        Set(w, "activeLayout", layout);
        Set(w, "nextDecision", 0L); Frame(); Field<CheckBox>(w, "automatic").IsChecked = true;
        await Wait(() => host.Mailbox.Poll(session) is { Kind: "visible-farming-advice", Marker: not null },
            "normal automatic UI publishes a model-selected 4K landmark after calibration");
        var result = host.Mailbox.Poll(session)!;
        await Wait(() => result.Minimap == layout && layout.Contains(result.Marker!), "normal map advice carries checked source-resolution geometry");
        await Wait(() => result.Route is { Length: 2 } route && route[0] == result.Marker && route.All(layout.Contains) &&
            result.NextSteps is { Length: 2 }, "model route is published as numbered minimap stops with next steps");
        await Wait(() => result.RemainingMs is > 2000 and <= 3000, "AI subtitles use the 3-second default display time");
        await Wait(() => Field<TextBlock>(w, "visionText").Text.Contains("天辉") && Field<TextBlock>(w, "visionText").Text.Contains("→"),
            "coach panel shows the route by landmark names");
        Render(w, Path.Combine(output, "coach-live-4k-controls.png"));

        using var delayed = new UiModel { Delayed = true, Route = ["neutralcamp_good_11"] };
        Set(w, "cloud", new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", delayed, map, false, layout));
        Set(w, "nextDecision", 0L); Frame();
        await Wait(() => delayed.Calls == 1 && Field<bool>(w, "deciding"), "normal inference is in flight before pause test");
        Field<CheckBox>(w, "automatic").IsChecked = false;
        delayed.Release.TrySetResult(true);
        await Wait(() => !Field<bool>(w, "deciding") && host.Mailbox.Poll(session) == null,
            "paused normal UI discards even a provider reply that ignores cancellation");

        // No confirmed calibration: the automatic default box must be the layout the live model is built for.
        var auto = MinimapLayout.Suggest(3840, 2160);
        Set(w, "preferences", Field<LiveCoachSettings>(w, "preferences") with { Calibrations = [], AutoMinimap = true, RequestLimit = 10 });
        Field<CheckBox>(w, "autoMinimap").IsChecked = true;
        using var autoModel = new UiModel { Route = ["neutralcamp_good_11"] };
        Set(w, "cloud", new CloudCoach(Path.GetFullPath(env), "gpt-6-astra", "", autoModel, map, false, auto));
        Set(w, "activeLayout", auto);
        Set(w, "nextDecision", 0L); Frame(); Field<CheckBox>(w, "automatic").IsChecked = true;
        await Wait(() => autoModel.Calls == 1 && host.Mailbox.Poll(session) is { Marker: not null, Minimap: { } m } && m == auto,
            "without calibration the automatic bottom-left box still yields a minimap route");
        await Wait(() => Field<TextBlock>(w, "mapStatus").Text.Contains("自动默认框"), "coach explains the automatic minimap box");
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
    private static async Task SpectatorPanel(string output, Type coachType, bool isolated = false)
    {
        // This simulated match starts at 2:05 on Dire. The separate image-model fixture in the
        // parent directory intentionally uses the same dummy ID at 12:00. Do not mix perspectives
        // or reuse its durable high-water mark; a real archive must reject that rewind.
        output = Directory.CreateDirectory(Path.Combine(output, "spectator-profile-" + Guid.NewGuid().ToString("N"))).FullName;
        var w = OfflineCoach(coachType, Path.Combine(output, "spectator-settings.json"), Path.Combine(output, "spectator-debug"));
        string live = Path.GetFullPath(Path.Combine(output, "scan-live.json"));
        Set(w, "scanFile", live); // including the initial delay test: never poll the user's real live.json
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
            map = new { clock_time = 125, matchid = "9876543210", game_state = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", paused = false },
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
        var host = await FixtureListener(w, isolated ? 0 : 38481);
        string session = host.Mailbox.Begin().SessionId;
        var bitmap = BitmapSource.Create(1280, 720, 96, 96, PixelFormats.Bgr24, null, new byte[1280 * 720 * 3], 1280 * 3);
        var encoder = new JpegBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var jpegStream = new MemoryStream(); encoder.Save(jpegStream); byte[] jpeg = jpegStream.ToArray();
        // Bare player strings were enough for the v0.4.x panel, but are not participant proof for a live archive.
        static MatchWitness Witness(double c) => new("9876543210", "dire", "npc_dota_hero_luna", c, "playing", false) { AgeMs = 10 };
        for (int clock = 126; clock <= 133; clock++) {
            var ticket = host.Mailbox.Issue(session);
            host.Mailbox.Accept(new(session, ticket.Id, clock, "dota2", 3840, 2160, new OwnState("9876543210", "npc_dota_hero_luna",
                "dire", clock, true, 1200, 500, 1, 2, [], []), 10, Witness(clock)), jpeg);
            await Task.Delay(150);
            await http.PostAsync("/spectator", new StringContent(body.Replace("\"clock_time\":125", $"\"clock_time\":{clock}")));
            await Task.Delay(150);
        }
        await Wait(() => Field<TextBlock>(w, "spectatorLive").Text is { } t &&
            System.Text.RegularExpressions.Regex.Match(t, @"比游戏机慢：中位 (\d+\.\d+)s") is { Success: true } m &&
            double.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) is >= .10 and <= .40,
            "real window measures Plus lag from game-machine frames on the coach clock");
        // Plus alerts: an enemy seen 4 s and then gone, and an item change from the scanner's live file, become the
        // game machine's subtitle. The live file is a test copy, never the user's scanner output.
        string without = body.Replace(",\"o1\":{\"xpos\":3,\"ypos\":4,\"team\":2,\"unitname\":\"npc_dota_hero_razor\",\"image\":\"minimap_enemyicon\",\"visionrange\":1800}", "");
        if (without == body) throw new InvalidOperationException("test body layout changed");
        long sequence = 134;
        for (int step = 0; step < 18; step++) {
            var ticket = host.Mailbox.Issue(session);
            host.Mailbox.Accept(new(session, ticket.Id, sequence++, "dota2", 3840, 2160, new OwnState("9876543210", "npc_dota_hero_luna",
                "dire", 134 + step / 2, true, 1200, 500, 1, 2, [], []), 10, Witness(134 + step / 2)), jpeg);
            await http.PostAsync("/spectator", new StringContent((step < 8 ? body : without).Replace("\"clock_time\":125", $"\"clock_time\":{134 + step / 2}")));
            await Task.Delay(500);
        }
        await Wait(() => host.Mailbox.Poll(session) is { Kind: "local-vision-observation", Marker: null } r &&
            r.Message.Contains("离开视野 · 最后在中路") && Field<TextBlock>(w, "alertStatus").Text.Contains("已推送"),
            "an enemy leaving the friend team's vision is pushed as a subtitle with its last area");
        File.WriteAllText(live, JsonSerializer.Serialize(new { version = 2, run = "a1", written = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0,
            stopped = false, mode = "team-view", matchId = "9876543210", ownTeam = 3, clock = 143, gsiAge = 0, selecting = true,
            enemies = new[] { new { hero = "npc_dota_hero_razor", level = 11, age = 1.5,
                items = new string?[] { "item_blink", null, null, null, null, null }, neutral = (string?)null } } }));
        Directory.CreateDirectory(Path.Combine(output, "icons"));
        File.WriteAllBytes(Path.Combine(output, "icons", "item_blink.png"), SolidPng(Colors.Blue, 88, 64));
        var keepAlive = host.Mailbox.Issue(session);
        host.Mailbox.Accept(new(session, keepAlive.Id, sequence++, "dota2", 3840, 2160, new OwnState("9876543210", "npc_dota_hero_luna",
            "dire", 143, true, 1200, 500, 1, 2, [], []), 10, Witness(143)), jpeg);
        await Wait(() => host.Mailbox.Enemies(session) is { Heroes: [{ Hero: "npc_dota_hero_razor", Level: 11 }] } &&
            host.Mailbox.Icon(session, "item_blink") is { Length: > 0 } && host.Mailbox.Icon(session, "item_bfury") == null &&
            Field<TextBlock>(w, "alertStatus").Text.Contains("已推送 1 名敌方英雄"),
            "the scanner's enemy panel and its icons are published to the game machine");
        // v0.6.1 regression (match 9101186642: the panel withdrawn 378 times): whatever the scanner and Plus do, the
        // game machine keeps every enemy's last reading until the match ends. Poll the mailbox like the game client.
        async Task Holds(string why, int level, Action change) {
            change();
            long until = Environment.TickCount64 + 3000;
            bool updated = false;
            while (Environment.TickCount64 < until) {
                var t = host.Mailbox.Issue(session);
                host.Mailbox.Accept(new(session, t.Id, sequence++, "dota2", 3840, 2160, new OwnState("9876543210", "npc_dota_hero_luna",
                    "dire", 143, true, 1200, 500, 1, 2, [], []), 10, Witness(143)), jpeg);
                for (int i = 0; i < 2; i++) {
                    var shown = host.Mailbox.Enemies(session);
                    if (shown is not { Heroes: [{ Hero: "npc_dota_hero_razor", Level: var l, Items: ["item_blink", ..] }] } || l < 11 || updated && l != level)
                        throw new Exception("Enemy panel withdrawn or rolled back: " + why);
                    updated |= l == level;
                    await Task.Delay(250);
                }
            }
            if (!updated) throw new Exception("Enemy panel not updated: " + why);
            passed++; Console.WriteLine("PASS " + why);
        }
        string Scan(int clock, int level, bool stopped = false) => JsonSerializer.Serialize(new { version = 2, run = "a1",
            written = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0, stopped, mode = "team-view", matchId = "9876543210", ownTeam = 3,
            clock, gsiAge = 0, selecting = true, enemies = new[] { new { hero = "npc_dota_hero_razor", level, age = 0.5,
                items = new string?[] { "item_blink", null, null, null, null, null }, neutral = (string?)null } } });
        await Holds("a scanner write a second ahead of the player's GSI updates the panel instead of withdrawing it", 12,
            () => File.WriteAllText(live, Scan(144, 12)));
        await Holds("a stopped scanner and a silent Plus feed leave the last reading on the game machine", 12,
            () => File.WriteAllText(live, Scan(144, 12, stopped: true)));
        await Holds("a deleted scanner file leaves the last reading on the game machine", 12, () => File.Delete(live));
        Field<CheckBox>(w, "enemyPanel").IsChecked = false;
        await Wait(() => host.Mailbox.Enemies(session) == null && Field<TextBlock>(w, "alertStatus").Text.Contains("对手面板已关") &&
            LiveCoachSettings.Load(Path.Combine(output, "spectator-settings.json")).EnemyPanel == false,
            "switching the enemy panel off hides it on the game machine and is saved");
        await TeamIntelWithDeliveryOff(w, host, session, live, http, token, sequence);
        string fakeScanner = await ScanLauncherChecks(w, output, fakeInstall);
        w.Close(); await Wait(() => !w.IsVisible && Field<SpectatorReceiver?>(w, "spectator") == null, "closing releases the spectator port");
        await Wait(() => File.ReadAllLines(Path.Combine(fakeScanner, "stopped.txt")).Length == 2 && Field<HeroScanProcess?>(w, "scan") == null,
            "closing the coach client stops the second scan it started");
        string realState = GsiSetup.StateDirectoryFor("Coach");
        await Wait(() => File.Exists(Path.Combine(state, "installed-spectator-gsi.json")) &&
            (!File.Exists(Path.Combine(realState, "dota-install.txt")) ||
             !File.ReadAllText(Path.Combine(realState, "dota-install.txt")).Contains(output)),
            "UI test keeps spectator identity/receipt in its own folder, not the user's coach state");
    }

    private sealed class UiModel : HttpMessageHandler
    {
        public int Calls;
        public string[]? Route;
        public bool Delayed;
        public readonly TaskCompletionSource<bool> Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Calls++;
            if (Delayed) await Release.Task; // Intentional hostile/late provider for cancellation regression.
            var advice = new ModelAdvice("npc_dota_hero_luna", "radiant", "合成UI测试画面，非实际游戏验收",
                ["测试自身状态"], ["营地存量未知"], "先确认兵线和附近野点，未知不当安全。",
                Route is { Length: > 1 } ? ["确认11号营地有怪再清", "清完回二塔附近接线"] : ["确认可见资源再决定"],
                "出现威胁或状态变化取消", null, Route == null ? "" : "小地图框内的己方地标", Route);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                id = "test-ui-response", status = "completed", usage = new { input_tokens = 100, output_tokens = 80 },
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = JsonSerializer.Serialize(advice, Wire.Json) } } } }
            })) };
        }
    }
}
