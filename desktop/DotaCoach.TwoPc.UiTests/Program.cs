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
        if (args is ["--game-controls"]) {
            var game = new GameWindowUi(); // Inspect the logical tree without showing a window or connecting.
            CheckGameControls(game);
            if (Field<CancellationTokenSource?>(game,"active") != null || Field<GsiReceiver?>(game,"gsi") != null)
                throw new Exception("Idle game client must not start a connection or GSI receiver");
            Console.WriteLine("PASS idle client: normal controls and creep HP switch present, lane controls and overlay worker absent");
            return;
        }
        if (args is ["--offline-practice"]) { OfflinePractice(); return; }
        if (args is ["--lane-vision-e2e"]) { LaneVisionE2E.Run(); return; }
        if (args is ["--lane-window-e2e"]) { LaneWindowE2E.Run(); return; }
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            try {
                if (args.Contains("--real-dota")) { await RealDota(); Console.WriteLine($"Real Dota window pipeline: {passed} checks passed."); }
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
    private static IEnumerable<DependencyObject> Children(DependencyObject parent)
    {
        yield return parent;
        foreach (object child in LogicalTreeHelper.GetChildren(parent))
            if (child is DependencyObject d) foreach (var x in Children(d)) yield return x;
    }
    private static void Click(Window w, string content) => Children(w).OfType<Button>()
        .Single(b => Equals(b.Content, content)).RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    private static void CheckGameControls(Window game)
    {
        var controls = Children(game).ToArray();
        var sections = controls.OfType<Expander>().Select(e => e.Header as string).ToArray();
        // The creep HP numbers (CreepHpDisplay) are back as their own switch; the old lane overlay and its shortcuts are not.
        if (!sections.SequenceEqual(new[]{"小兵血量：本机实时显示", "设置：画面来源、视频、Dota 目录", "Debug"}) ||
            !controls.OfType<Button>().Any(b => Equals(b.Content,"连接")) ||
            !controls.OfType<Button>().Any(b => Equals(b.Content,"显示小兵血量")) ||
            controls.OfType<Button>().Any(b => b.Content is "停止显示血量" or "试算") ||
            typeof(GameWindowUi).Assembly.GetType("DotaCoach.TwoPc.LastHitPractice") != null)
            throw new Exception("Game client should ship only its normal controls and the creep HP switch, without the experimental lane overlay");
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
    private static async Task Run(string output)
    {
        Directory.CreateDirectory(output);
        var coachType = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!;
        var gameType = Assembly.Load("DotaCoach.GameClient").GetType("DotaCoach.TwoPc.GameWindowUi")!;
        string debugRoot = Path.GetFullPath(Path.Combine(output, "debug"));
        var coach = (Window)Activator.CreateInstance(coachType, [null, false, Path.Combine(output, "coach-settings.json"), debugRoot])!;
        string videoSettings = Path.GetFullPath(Path.Combine(output, "game-video.json"));
        var game = (Window)Activator.CreateInstance(gameType, [null, debugRoot, videoSettings])!;
        // Exercise packaged UI handlers without desktop input, clipboard, real game capture or AI.
        coach.Show(); game.Show();
        await Wait(() => Children(coach).Concat(Children(game)).OfType<Expander>().All(e => !e.IsExpanded) &&
            Children(coach).OfType<Expander>().Count() == 4 && Children(game).OfType<Expander>().Count() == 3,
            "rarely used settings start collapsed (game: creep HP, settings and Debug)");
        CheckGameControls(game);
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
        Field<ComboBox>(game, "source").SelectedIndex = 1;
        await Wait(() => Field<ComboBox>(game, "fpsChoice").SelectedItem as string == "60 fps" &&
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
        var emptyCoach = (Window)Activator.CreateInstance(coachType)!;
        var emptyGame = (Window)Activator.CreateInstance(gameType)!;
        emptyCoach.Show(); emptyGame.Show(); emptyCoach.Close(); emptyGame.Close();
        await Wait(() => !emptyGame.IsVisible && !emptyCoach.IsVisible, "unused clients close without reentrant-close errors");
        await OccludedWindowCapture();
        await LiveMode(output, coachType, gameType);
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
        var w = (Window)Activator.CreateInstance(coachType, [null, false, Path.Combine(output, "live-settings.json"),
            Path.Combine(output, "live-debug")])!;
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
        Field<TextBox>(w, "port").Text = "38481"; Click(w, "启动接收");
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
