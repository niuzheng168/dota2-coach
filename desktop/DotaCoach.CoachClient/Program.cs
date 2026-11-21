using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DotaCoach.TwoPc;

internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            MessageBox.Show("双机教练端首版需要 Windows 11 x64。"); return;
        }
        new Application().Run(new CoachWindow(E2ERun.Load(args)));
    }
}

internal sealed class CoachWindow : Window
{
    private readonly ComboBox address = new() { FontSize = 14, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox port = Ui.Input("38471");
    private readonly Button listen;
    private readonly TextBlock status = Ui.Text("接收服务未启动", 17, Ui.Accent);
    private readonly TextBlock detail = Ui.Text("", 12, Brushes.LightGray);
    private readonly TextBlock notice = Ui.Text("先启动接收，再复制配对码到游戏端。", 13, Ui.Accent);
    private readonly TextBlock visionText = Ui.Text("");
    private readonly TextBlock previewTitle = Ui.Text("等待游戏端画面", 20, Ui.Accent);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Grid preview = new() { Background = Brushes.Black };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource stop = new();
    private CoachHost? host;
    private CancellationTokenSource? starting;
    private FrameView? displayed;
    private bool closing, busy;
    private readonly E2ERun? e2e;
    // Video off (the default, see Features): no preview, model or minimap calibration; Plus alerts and the enemy panel only.
    private readonly bool video;
    private CloudCoach? cloud;
    private bool deciding;
    private int modelCalls;
    private long nextDecision;
    private readonly string settingsPath;
    private LiveCoachSettings preferences;
    private readonly CheckBox automatic = new() { Content = "自动教练", Foreground = Brushes.White };
    private readonly CheckBox subtitles = new() { Content = "游戏字幕", Foreground = Brushes.White };
    private readonly CheckBox mapMarkers = new() { Content = "小地图路线", Foreground = Brushes.White };
    private readonly TextBox foundryFile = Ui.Input();
    private readonly TextBox reasoningModel = Ui.Input();
    private readonly TextBox interval = Ui.Input();
    private readonly TextBox requestLimit = Ui.Input();
    private readonly TextBox subtitleSeconds = Ui.Input();
    private readonly TextBox modelTimeout = Ui.Input();
    private readonly CheckBox autoMinimap = new() { Content = "未校准时使用默认左下小地图框", Foreground = Brushes.White, Margin = new Thickness(0, 0, 0, 7) };
    // Preview runs at the stream rate (bounded by the WPF frame clock), converted off the UI thread.
    private readonly DispatcherTimer previewTimer = new(DispatcherPriority.Render) { Interval = TimeSpan.FromMilliseconds(1) };
    private WriteableBitmap? previewBitmap;
    private bool previewBusy;
    private long viewGeneration, previewShown, previewRateAt;
    private double previewFps;
    private readonly TextBlock autoStatus = Ui.Text("自动教练等待配置", 14, Ui.Accent);
    private readonly TextBox mapLeft = Ui.Input(), mapTop = Ui.Input(), mapSize = Ui.Input();
    private readonly TextBlock mapStatus = Ui.Text("等待实际游戏尺寸；未校准只发字幕，不猜地图点。", 12, Brushes.LightGray);
    private readonly Canvas calibrationMarks = new() { IsHitTestVisible = false };
    private MinimapLayout? draftLayout, activeLayout;
    private CalibratedMap? liveMap;
    private string liveLessons = "";
    private bool modelConfigured;
    private CancellationTokenSource? decisionStop;
    private long modelGeneration, inputTokens, outputTokens;
    private readonly DebugPanel debug;
    // Dota Plus live spectating on THIS machine: observed and measured only, never fed to the model.
    // Instance field so isolated UI tests never touch the real %LOCALAPPDATA% state.
    private string spectatorState = GsiSetup.StateDirectoryFor("Coach");
    private readonly TextBox spectatorInstall = Ui.Input(GsiSetup.FindInstall(GsiSetup.StateDirectoryFor("Coach")));
    private readonly TextBlock spectatorSetup = Ui.Text("尚未配置。", 12, Brushes.LightGray);
    private readonly TextBlock spectatorLive = Ui.Text("Plus 观战：未启动", 13);
    private readonly PlusDelayTracker plusDelay = new();
    private SpectatorReceiver? spectator;
    private int spectatorPort = SpectatorReceiver.DefaultPort;
    private bool spectatorStarting;
    private string? delaySession;
    private long delaySequence, nextSpectatorText;
    // Plus spectating on the game machine: a panel of enemy levels and items (hero_scan.py on this machine) and
    // subtitles when enemies leave the friend team's vision (this machine's own Plus GSI). Team-view data only.
    private readonly CheckBox enemyPanel = new() { Content = "对手等级和装备（游戏左侧面板）", Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 4) };
    private readonly CheckBox enemyVision = new() { Content = "对手离开视野", Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 4) };
    private readonly CheckBox enemyWards = new() { Content = "对手插眼（小地图标记可能眼位）", Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 4) };
    private readonly TextBlock alertStatus = Ui.Text("对手提醒：等待 Plus 队伍视角", 12, Brushes.LightGray);
    // The 7.42 map (place names, minimap projection) and common ward spots, bundled in data/. Without the map there
    // are no ward guesses and alerts fall back to coarse areas.
    private readonly CoachMap? coachMap = CoachMap.Bundled();
    private readonly WardTracker? wardTracker;
    private readonly VisionLossTracker visionLoss;
    private readonly AlertComposer alerts = new();
    private HeroNames heroNames = new();
    private string scanFile = ScanFeed.DefaultPath; // instance field so UI tests never read the real scanner output
    private ScanStatus scanStatus = new(false, "视觉扫描未运行");
    private volatile bool panelOn, visionAlertsOn, wardsOn; // also read on the spectator receiver's threads
    private long nextScanPoll;
    private string lastAlert = "", panelText = "", wardText = "";
    // The vision scan writing scanFile, started and stopped with the button beside 启动接收 (or by hand in a terminal).
    private readonly Button? scanButton;
    private readonly TextBlock scanState = Ui.Text("视觉扫描：未启动", 13);
    private readonly TextBox scanFolder = Ui.Input(), scanPython = Ui.Input();
    private HeroScanProcess? scan;
    private bool scanBusy, scanStopped;
    private string scanNote = "";

    // Tests instantiate inert windows explicitly; the application entry point uses automaticStartup=true.
    public CoachWindow() : this(null, false) { }
    public CoachWindow(E2ERun? run, bool automaticStartup = true, string? preferencesFile = null)
        : this(run, automaticStartup, preferencesFile, null) { }
    public CoachWindow(E2ERun? run, bool automaticStartup, string? preferencesFile, string? debugRoot)
    {
        e2e = run; debug = new DebugPanel("coach", debugRoot);
        video = Features.VideoUpload || e2e != null; // the local-bot E2E evidence is built on uploaded pictures
        visionLoss = new VisionLossTracker { Map = coachMap };
        if (coachMap != null) wardTracker = new WardTracker(coachMap, WardSpots.Bundled());
        settingsPath = preferencesFile ?? LiveCoachSettings.DefaultPath;
        try { preferences = LiveCoachSettings.Load(settingsPath); }
        catch { preferences = new(); notice.Text = "已保存的设置无效，请检查并重新应用；没有读取或输出密钥内容。"; }
        try { heroNames = new HeroNames(System.IO.Path.Combine(AppContext.BaseDirectory, "data", "dota2-dictionary.json")); }
        catch { /* Alerts fall back to internal hero names. */ }
        Title = "Dota Coach · 教练端 v0.4.7";
        Width = Math.Min(video ? 1320 : 760, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(940, SystemParameters.WorkArea.Height - 40);
        MinWidth = video ? 1000 : 560; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Background; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new() { Width = video ? new GridLength(340) : new GridLength(1, GridUnitType.Star) });
        if (video) {
            root.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
            root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        }
        var left = new StackPanel();
        left.Children.Add(Ui.Text("教练端", 26, Ui.Accent));
        left.Children.Add(Ui.Text(video ? "接收游戏机的画面和自身GSI，调用模型，把提醒发回游戏机。" :
            "接收游戏机的自身GSI（不传画面），把本机 Plus 观战得到的对手面板和离开视野提醒推送到游戏机。", 12, Brushes.LightGray));
        var ips = NetworkInterface.GetAllNetworkInterfaces().Where(n => n.OperationalStatus == OperationalStatus.Up)
            .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
            .Where(a => Wire.IsLan(a) && !IPAddress.IsLoopback(a)).Select(a => a.ToString()).Distinct().Order().ToList();
        // A virtual WSL adapter can sort before the real LAN. Never silently default to it.
        ips.Insert(0, "127.0.0.1");
        foreach (var ip in ips) address.Items.Add(ip);
        address.SelectedIndex = 0;
        if (automaticStartup && e2e == null) {
            string? preferred = ips.Contains(preferences.BindAddress) ? preferences.BindAddress :
                NetworkInterface.GetAllNetworkInterfaces()
                    .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is NetworkInterfaceType.Ethernet or NetworkInterfaceType.Wireless80211 &&
                        n.GetIPProperties().GatewayAddresses.Any(g => !g.Address.Equals(IPAddress.Any)))
                    .SelectMany(n => n.GetIPProperties().UnicastAddresses).Select(a => a.Address)
                    .Where(a => Wire.IsLan(a) && !IPAddress.IsLoopback(a)).Select(a => a.ToString()).FirstOrDefault();
            if (preferred != null && ips.Contains(preferred)) address.SelectedItem = preferred;
        }
        left.Children.Add(Ui.Label("本机地址 / 端口（跨机请选局域网IP，不要选WSL虚拟网卡；127.0.0.1 只用于同机）"));
        port.Width = 90;
        left.Children.Add(Ui.Row(address, port));
        listen = Ui.Button("启动接收", async (_, _) => { if (busy || host != null) await Stop(); else await Start(); });
        var connection = new WrapPanel();
        connection.Children.Add(listen);
        connection.Children.Add(Ui.Button("复制配对码", (_, _) => CopyPairing()));
        connection.Children.Add(Ui.Button("撤销配对", (_, _) => { host?.Revoke(); ClearView(); notice.Text = "旧配对已撤销；请重新复制配对码。"; }));
        if (e2e == null) connection.Children.Add(scanButton = Ui.Button("启动视觉扫描", async (_, _) => await ToggleScan()));
        left.Children.Add(connection);
        left.Children.Add(status); left.Children.Add(detail); left.Children.Add(notice);
        if (e2e == null) left.Children.Add(scanState);
        var switches = new StackPanel();
        if (e2e == null) {
            if (video) {
                var modelSettings = new StackPanel(); BuildAutomaticControls(switches, modelSettings);
                left.Children.Add(Ui.Section("模型设置", modelSettings));
                var calibration = new StackPanel(); BuildCalibration(calibration);
                left.Children.Add(Ui.Section("小地图校准", calibration));
            }
            left.Children.Add(Ui.Section("Plus 实时观战（实验）", BuildSpectatorPanel()));
        }
        var diagnostics = new StackPanel();
        diagnostics.Children.Add(debug.View);
        diagnostics.Children.Add(Ui.Button("发送测试提醒", (_, _) => SendTest()));
        left.Children.Add(Ui.Section("Debug", diagnostics));
        var scroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);

        if (video) {
            var right = new Grid();
            right.RowDefinitions.Add(new() { Height = GridLength.Auto });
            right.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
            right.RowDefinitions.Add(new() { Height = GridLength.Auto });
            right.Children.Add(previewTitle);
            preview.Children.Add(image); preview.Children.Add(calibrationMarks);
            Grid.SetRow(preview, 1); right.Children.Add(preview);
            var vision = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
            vision.Children.Add(Ui.Text(e2e == null ? "AI 自动教练" : "AI 本地机器人局验收", 20, Ui.Accent));
            vision.Children.Add(switches);
            vision.Children.Add(visionText);
            if (e2e != null) vision.Children.Add(Ui.Text("LOCAL BOT E2E：已显式配置Foundry云模型；真实图像和自身GSI会送往该服务并计费。仅本地局，非最优路线保证。", 12, Brushes.LightGray));
            Grid.SetRow(vision, 2); right.Children.Add(vision); Grid.SetColumn(right, 2); root.Children.Add(right);
        }
        Content = root;
        preview.SizeChanged += (_, _) => DrawCalibration();
        timer.Tick += (_, _) => Refresh(); timer.Start();
        previewTimer.Tick += (_, _) => UpdatePreview(); if (video) previewTimer.Start();
        if (e2e == null && automaticStartup) Loaded += async (_, _) => {
            if (video) ConfigureModel(save: false); // video off: no model is ever configured or called
            await Start();
            // One click the first time; afterwards the spectator feed reconnects on every start.
            if (GsiSetup.WasInstalled(spectatorState, GsiSetup.SpectatorConfigName)) await StartSpectator();
        };
        if (e2e != null) Loaded += async (_, _) => {
            try {
                cloud = new CloudCoach(e2e.FoundryEnvFile, e2e.Model, File.ReadAllText(e2e.LessonsPath),
                    map: string.IsNullOrEmpty(e2e.MapReferencePath) ? null : new CalibratedMap(e2e.MapReferencePath));
                await Start();
                if (host == null) throw new InvalidOperationException("Listener did not start");
                File.WriteAllText(e2e.PairingPath, host.Pair(IPAddress.Loopback).Encode());
                e2e.Trace("coach", "ready", new { model = e2e.Model, maxCalls = e2e.MaxModelCalls, localBotOnly = true });
                visionText.Text = $"本地机器人局实验：{e2e.Model} 图像＋GSI推理，最多{e2e.MaxModelCalls}次；只发送到指定Foundry。";
                await Task.Delay(TimeSpan.FromMinutes(10), stop.Token); Close();
            } catch (OperationCanceledException) { }
            catch (Exception error) { notice.Text = "E2E启动失败：" + error.GetType().Name; e2e.Trace("coach", "startup-error", new { type = error.GetType().Name }); }
        };
        Closing += async (_, e) => {
            if (closing) return;
            e.Cancel = true; closing = true; stop.Cancel(); timer.Stop(); previewTimer.Stop(); CancelDecision();
            await Stop();
            while (scanBusy) await Task.Delay(25);
            if (scan != null) { await scan.DisposeAsync(); scan = null; }
            if (spectator != null) { await spectator.DisposeAsync(); spectator = null; }
            cloud?.Dispose();
            await debug.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private async Task Start()
    {
        if (busy || host != null || closing) return;
        busy = true; listen.Content = "停止接收";
        try
        {
            if (!int.TryParse(port.Text, out int number) || number is < 1024 or > 65535)
                throw new ArgumentException("端口必须在1024–65535之间。");
            starting = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            // Same-machine E2E must not open LAN/public interfaces or request firewall access.
            // A real two-PC run explicitly selects the machine's LAN address before starting.
            var bind = IPAddress.Parse((string)address.SelectedItem);
            host = await CoachHost.Start(bind, number, starting.Token);
            host.Mailbox.DebugSink = () => debug.Recorder;
            // Video off: without a decoder /v2/video refuses streams, so not even an old game client can push video.
            if (video) host.Mailbox.VideoJpegEncoder = Ui.ExtractJpeg;
            host.Mailbox.Icons = key => ScanFeed.Icon(scanFile, key);
            port.IsEnabled = false; address.IsEnabled = false;
            if (e2e == null) {
                preferences = preferences with { BindAddress = bind.ToString() };
                try { preferences.Save(settingsPath); } catch { /* Runtime remains usable; no credentials are serialized. */ }
            }
            status.Text = $"HTTPS 接收已开启 · {host.ListeningAddress}:{host.Port}";
            notice.Text = "复制配对码到游戏端。若跨机无法连接，请仅为此程序/端口允许 Windows 专用网络入站。";
        }
        catch (Exception e) {
            debug.Recorder?.Error("coach-listen", e); listen.Content = "启动接收";
            notice.Text = e is ArgumentException ? e.Message : "无法启动接收：检查端口是否被占用。未修改防火墙。";
        }
        finally { starting?.Dispose(); starting = null; busy = false; }
    }
    private async Task Stop()
    {
        CancelDecision();
        starting?.Cancel();
        while (busy) await Task.Delay(25);
        var current = host; host = null;
        if (current != null) await current.DisposeAsync();
        port.IsEnabled = true; address.IsEnabled = true; ClearView();
        status.Text = "接收服务已停止"; detail.Text = ""; listen.Content = "启动接收";
    }
    private void CopyPairing()
    {
        if (host == null) { notice.Text = "先启动接收服务。"; return; }
        Clipboard.SetText(host.Pair(IPAddress.Parse((string)address.SelectedItem)).Encode());
        notice.Text = "配对码已复制（含密钥）。只粘贴到你的游戏端；换地址/重启后重新复制。";
    }
    private StackPanel BuildSpectatorPanel()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("本机 Dota 用账号 B（有 Dota Plus，与游戏账号互为好友）对游戏账号“实时观战”时，这里显示收到的小地图并测量比游戏机慢多少。观战数据不进自动教练。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Label("本机 Dota 安装目录")); panel.Children.Add(spectatorInstall);
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("一键配置观战 GSI", async (_, _) => await StartSpectator()));
        buttons.Children.Add(Ui.Button("复制启动参数", (_, _) => spectatorSetup.Text = Ui.CopyLaunchOptions(SteamLaunch.Inspect())));
        panel.Children.Add(buttons); panel.Children.Add(spectatorSetup); panel.Children.Add(spectatorLive);
        panel.Children.Add(Ui.Label("推送到游戏机（只用队伍视角看得到的信息）"));
        enemyPanel.IsChecked = panelOn = preferences.EnemyPanel;
        enemyVision.IsChecked = visionAlertsOn = preferences.EnemyVisionAlerts;
        enemyWards.IsChecked = wardsOn = preferences.EnemyWards && wardTracker != null;
        enemyWards.IsEnabled = wardTracker != null;
        var toggles = new WrapPanel();
        foreach (var toggle in new[] { enemyPanel, enemyVision, enemyWards }) {
            toggles.Children.Add(toggle);
            toggle.Checked += (_, _) => SaveAlertToggles(); toggle.Unchecked += (_, _) => SaveAlertToggles();
        }
        panel.Children.Add(toggles); panel.Children.Add(alertStatus);
        // Found beside this client when it runs from a checkout; a packaged client needs them filled in once.
        string? folder = preferences.ScanFolder.Length > 0 ? preferences.ScanFolder : HeroScanProcess.FindFolder(AppContext.BaseDirectory);
        scanFolder.Text = folder ?? "";
        scanPython.Text = preferences.ScanPython.Length > 0 ? preferences.ScanPython : folder == null ? "" : HeroScanProcess.FindPython(folder) ?? "";
        panel.Children.Add(Ui.Label(@"视觉扫描（窗口上方“启动视觉扫描”）的代码仓库目录，含 scripts\vision\hero_scan.py"));
        panel.Children.Add(scanFolder);
        panel.Children.Add(Ui.Label(@"装了视觉依赖的 Python，一般是仓库里的 .tmp\vision-tools\Scripts\python.exe"));
        panel.Children.Add(scanPython);
        if (!video) {
            // With video on this lives in the model settings, which are hidden now.
            subtitleSeconds.Text = preferences.SubtitleSeconds.ToString();
            panel.Children.Add(Ui.Label("离开视野字幕显示（秒，1–15）"));
            panel.Children.Add(Ui.Row(subtitleSeconds, Ui.Button("保存", (_, _) => SaveSubtitleSeconds())));
        }
        panel.Children.Add(Ui.Text("对手面板一直显示在游戏画面左侧：敌方英雄最近一次被看到时的等级和装备，旧了会标出几秒前；" +
            "需要本机同时运行视觉扫描（窗口上方“启动视觉扫描”；观战的 Dota 要在前台）。离开视野是字幕，只用观战GSI；" +
            "被击杀和走进迷雾分不出来，都会提示" + (video ? "，字幕时长同模型设置。" : "。") +
            "对手插眼：视觉扫描读到的敌方守卫数比上一次少了，就按它这段时间的行踪和 7.42 常见眼位，在游戏机小地图上圈出可能的眼位" +
            "（最多 3 处，最可能的实心），并发一条字幕；守卫交给队友、丢弃也会被当成插眼。" +
            (wardTracker == null ? "\n本机缺少 data/dota2-coach-map.json，插眼推测不可用。" : ""), 12, Brushes.LightGray));
        return panel;
    }
    private async Task StartSpectator()
    {
        if (spectatorStarting || closing) return;
        spectatorStarting = true;
        try {
            if (spectator == null) {
                var receiver = await SpectatorReceiver.Start(spectatorPort, GsiSetup.Token(spectatorState), stop.Token);
                // Called on server threads; both sinks are thread-safe.
                receiver.Received = s => { plusDelay.Spectator(s); debug.Recorder?.Spectator(s); OnSpectator(s); };
                spectator = receiver;
            }
            bool changed = GsiSetup.Install(spectatorInstall.Text.Trim(), spectator.Config, spectatorState, GsiSetup.SpectatorConfigName).Changed;
            spectatorSetup.Text = (changed ? "观战GSI配置已写入：请完全退出并重启本机Dota一次。" : "观战GSI配置已就绪，本机端口 " + spectator.Port + "。") +
                "\n" + Ui.LaunchOptionsText(SteamLaunch.Inspect());
        } catch (Exception e) {
            debug.Recorder?.Error("spectator-setup", e);
            spectatorSetup.Text = e is IOException ? "配置失败：" + e.Message + "（不会覆盖你改过的同名文件）" :
                $"无法启动本机观战接收：检查端口 {spectatorPort} 是否被占用。";
        } finally { spectatorStarting = false; }
    }
    private void UpdateSpectator()
    {
        // Game side of the delay: when the game machine's own GSI showed each new second (coach clock).
        if (host?.Mailbox.Latest() is { Meta: { Player: { } own, GsiAgeMs: int age } meta } frame &&
            (meta.SessionId != delaySession || meta.Sequence != delaySequence)) {
            delaySession = meta.SessionId; delaySequence = meta.Sequence;
            plusDelay.Game(own, frame.ReceivedAt - age);
        }
        if (spectator == null || Environment.TickCount64 < nextSpectatorText) return;
        nextSpectatorText = Environment.TickCount64 + 500;
        var (s, packets, rejected) = spectator.Status();
        var delay = plusDelay.Stats();
        debug.Recorder?.SpectatorDelay(delay);
        string Clock(double c) => $"{(c < 0 ? "-" : "")}{(int)Math.Abs(c) / 60}:{(int)Math.Abs(c) % 60:00}";
        string head = s == null ? "还没收到本机Dota数据：确认启动参数，并在配置后重启过Dota" :
            Environment.TickCount64 - s.ReceivedAt > 3000 ? "本机Dota数据已中断（Dota关闭或卡住）" :
            s.Mode switch { "menu" => "本机Dota在菜单，尚未观战", "team-view" => "Plus 实时观战 · 队伍视角",
                "full-spectator" => "完整观战（有延迟，含10人数据；只记录）", "player" => "本机在打游戏，不是观战", _ => s.Mode };
        var text = new System.Text.StringBuilder($"{head} · 已收 {packets} 包{(rejected > 0 ? $" · 拒绝 {rejected}" : "")}");
        if (s is { Running: true }) {
            int Count(Func<MinimapUnit, bool> f) => s.Heroes.Where(f).Select(u => u.Name).Distinct().Count();
            int allies = Count(u => u.Team == s.OwnTeam), enemies = Count(u => u.Team is 2 or 3 && u.Team != s.OwnTeam);
            var neutrals = s.Minimap.Where(u => u.Team == 4 && u.Name.StartsWith("npc_dota_neutral_", StringComparison.Ordinal)).ToArray();
            // Neutrals inside a 7.42 camp's spawn box (a little beyond, as they wander) tell how many camps are in sight.
            int camps = coachMap == null ? 0 : neutrals.Select(u => coachMap.CampAt(u.X, u.Y, 300)?.Id).OfType<string>().Distinct().Count();
            text.Append($"\n游戏 {Clock(s.Clock!.Value)} · {(s.OwnTeam == 2 ? "天辉" : s.OwnTeam == 3 ? "夜魇" : "阵营未知")} · 小地图 {s.Minimap.Length} 个单位\n" +
                $"己方英雄 {allies} · 视野内敌方英雄 {enemies} · 视野内野怪 {neutrals.Length}" + (camps > 0 ? $"（{camps} 个营地）" : "") +
                (s.GroupedPlayers > 0 ? $" · 10人分组 {s.GroupedPlayers}" : ""));
        }
        text.Append(delay.MedianMs is double median
            ? $"\n比游戏机慢：中位 {median / 1000:F2}s（P10 {delay.P10Ms / 1000:F2} / P90 {delay.P90Ms / 1000:F2}，{delay.Samples} 个样本；{(video ? "可能偏低约0.1–0.2s" : "略偏低一次局域网传输的时间")}）"
            : $"\n延迟：{delay.Note}" + (delay.ClockGapSeconds is double gap ? $"（两边游戏时间相差 {gap:F0}s）" : ""));
        spectatorLive.Text = text.ToString();
    }
    /// <summary>Spectator receiver threads: enemies leaving the friend team's vision become pending alerts.</summary>
    private void OnSpectator(SpectatorSnapshot s)
    {
        List<PlusAlert> found;
        lock (visionLoss) found = visionLoss.Update(s, heroNames);
        if (visionAlertsOn && found.Count > 0) alerts.Add(found, Environment.TickCount64);
        wardTracker?.Sight(s); // tracks are kept even while switched off, so switching on has the recent past
    }
    private void SaveSubtitleSeconds()
    {
        if (!int.TryParse(subtitleSeconds.Text, out int seconds) || seconds is < 1 or > Wire.MaxReminderMs / 1000) {
            notice.Text = "字幕时长请填 1–15 的整数秒。"; return;
        }
        preferences = preferences with { SubtitleSeconds = seconds };
        try { preferences.Save(settingsPath); notice.Text = $"离开视野字幕显示 {seconds} 秒，已保存。"; }
        catch { notice.Text = "设置无法保存，当前字幕时长仅本次有效。"; }
    }
    private void SaveAlertToggles()
    {
        panelOn = enemyPanel.IsChecked == true; visionAlertsOn = enemyVision.IsChecked == true; wardsOn = enemyWards.IsChecked == true;
        if (!visionAlertsOn && !wardsOn) alerts.Clear();
        preferences = preferences with { EnemyPanel = panelOn, EnemyVisionAlerts = visionAlertsOn, EnemyWards = wardsOn };
        try { preferences.Save(settingsPath); }
        catch { notice.Text = "设置无法保存，当前开关仅本次有效。"; }
    }
    /// <summary>The 启动视觉扫描 button: starts hero_scan.py, or stops the one started here.</summary>
    private async Task ToggleScan()
    {
        if (scanBusy || closing) return;
        scanBusy = true; scanNote = "";
        try {
            if (scan is { ExitCode: null } running) { scanStopped = true; UpdateScan(); await running.StopAsync(); }
            else scanNote = await StartScan();
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException) {
            debug.Recorder?.Error("hero-scan", e);
            scanNote = "无法启动视觉扫描：" + e.Message;
        }
        finally { scanBusy = false; UpdateScan(); }
    }
    /// <returns>Why it did not start, or what the user has to do for it to work; "" when nothing.</returns>
    private async Task<string> StartScan()
    {
        string folder = scanFolder.Text.Trim(), python = scanPython.Text.Trim(), install = spectatorInstall.Text.Trim();
        if (!System.IO.Path.IsPathFullyQualified(folder) || !File.Exists(System.IO.Path.Combine(folder, HeroScanProcess.Script)))
            return @"仓库目录里没有 scripts\vision\hero_scan.py：在“Plus 实时观战”里填本机代码仓库的目录。";
        if (!HeroScanProcess.Supported(folder))
            return @"这个仓库里的 scripts\vision\hero_scan.py 是旧版，不能由教练端启动：先更新这个仓库（git pull），或在“Plus 实时观战”里改填新的仓库目录。";
        if (python.Length == 0) python = HeroScanProcess.FindPython(folder) ?? "";
        if (!System.IO.Path.IsPathFullyQualified(python) || !File.Exists(python))
            return @"找不到 Python：在“Plus 实时观战”里填装了视觉依赖的 python.exe（一般是仓库里的 .tmp\vision-tools\Scripts\python.exe）。";
        if (!System.IO.Path.IsPathFullyQualified(install) || !Directory.Exists(System.IO.Path.Combine(install, "game", "dota")))
            return "本机 Dota 安装目录不对：在“Plus 实时观战”里改好再启动。";
        if (scan == null && ScanFeed.Poll(scanFile, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Status.Running)
            return "已有一个视觉扫描在运行（不是从这里启动的）：先关掉它，再点启动。";
        scanFolder.Text = folder; scanPython.Text = python;
        preferences = preferences with { ScanFolder = folder, ScanPython = python };
        try { preferences.Save(settingsPath); } catch { /* The paths then last only this run. */ }
        string note = "";
        if (!HeroScanProcess.Configured(install, scanFile)) {
            var (code, output) = await HeroScanProcess.Setup(python, folder, install, scanFile, stop.Token);
            if (code != 0) return "视觉扫描配置失败：" + output.Trim().Split('\n')[^1].Trim();
            if (output.Contains("Wrote ")) note = "已写入视觉扫描的 GSI 配置：本机 Dota 要完全退出再启动一次才能扫描（只需这一次）。";
        }
        if (closing) return "";
        if (scan != null) await scan.DisposeAsync();
        scanStopped = false;
        scan = HeroScanProcess.Start(python, folder, scanFile,
            System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(scanFile))!, "coach-scan.log"));
        return note;
    }
    private void UpdateScan()
    {
        if (scanButton == null) return;
        bool running = scan is { ExitCode: null };
        scanButton.Content = running ? "停止视觉扫描" : "启动视觉扫描";
        scanState.Text = "视觉扫描：" + (scan == null ? (scanBusy ? "启动中…" : "未启动") :
            running ? (scanStopped ? "正在停止…" : scan.LastOutput switch {
                "" => "启动中（加载识别模型要十几秒）",
                var l when l.StartsWith("Waiting for the Dota window", StringComparison.Ordinal) => "等待本机 Dota 启动",
                var l when l.StartsWith("Dota window changed", StringComparison.Ordinal) => "Dota 窗口变了（最小化或改了分辨率），等它恢复",
                _ => "运行中 · Dota 在前台时轮流选中敌方英雄，读等级和装备",
            }) :
            scanBusy ? "启动中…" : scanStopped ? "已停止" :
            $"已退出（代码 {scan.ExitCode}）" + (scan.LastError.Length > 0 ? "：" + scan.LastError : "")) +
            (scanNote.Length > 0 ? "\n" + scanNote : "");
    }
    /// <summary>Publishes the scanner's enemy panel and waiting vision alerts (as a subtitle) to the game machine.</summary>
    private void UpdateAlerts()
    {
        var plus = spectator?.Status().Latest;
        long now = Environment.TickCount64;
        var frame = host?.Mailbox.Latest();
        string? gameMatch = frame?.Meta.Player?.MatchId;
        bool sameGame = plus?.MatchId == null || gameMatch == null || plus.MatchId == gameMatch;
        if (now >= nextScanPoll) {
            nextScanPoll = now + 500;
            UpdateScan();
            EnemyBoard? board = null;
            IReadOnlyDictionary<string, WardReading[]> wards = new Dictionary<string, WardReading[]>();
            if (panelOn || wardsOn) (scanStatus, board, wards) = ScanFeed.Poll(scanFile, plus?.MatchId, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            WardMark[]? marks = null;
            if (wardTracker != null && board != null && plus is { Mode: "team-view", OwnTeam: 2 or 3 }) {
                // Reads are taken in even while switched off: switching on must not mistake the last hour's wards for new ones.
                var guessed = wardTracker.Readings(plus.MatchId, plus.OwnTeam.Value, wards);
                if (wardsOn) {
                    if (guessed.Count > 0) alerts.Add(guessed.Select(g => new PlusAlert(now, "ward-guess", g.Hero, WardText(g))), now);
                    marks = wardTracker.Marks(wardTracker.Clock);
                    wardText = marks.Length == 0 ? "插眼推测：暂无" : $"插眼推测：小地图 {marks.Count(m => m.Rank == 0)} 处" +
                        (guessed.Count > 0 ? " · 新：" + WardText(guessed[^1]) : "");
                }
            }
            else if (wardsOn) wardText = "插眼推测：等待 Plus 队伍视角和视觉扫描";
            if (!wardsOn) wardText = wardTracker == null ? "" : "插眼推测已关";
            bool deliver = board != null && frame != null && sameGame;
            host?.Mailbox.SetEnemies(deliver && (panelOn || marks is { Length: > 0 }) ? new EnemyBoard(panelOn ? board!.Heroes : [], marks) : null);
            panelText = !panelOn ? "对手面板已关" : board == null ? "对手面板：" + scanStatus.Text :
                deliver ? $"对手面板：已推送 {board.Heroes.Length} 名敌方英雄" :
                $"对手面板：未推送（{(frame == null ? "游戏机未连接" : "观战的不是游戏机这一局")}）";
        }
        if (alerts.Next(now, preferences.SubtitleSeconds * 1000) is { } text) {
            try {
                if (host == null || frame == null || !sameGame) throw new InvalidOperationException("Not deliverable");
                host.Mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence, "local-vision-observation", text, null,
                    displayMs: preferences.SubtitleSeconds * 1000);
                if (host.Mailbox.Poll(frame.Meta.SessionId) is { } sent) debug.Recorder?.Reminder("reminder-published", sent);
                lastAlert = "已推送：" + text;
            } catch (Exception e) when (e is InvalidOperationException or ArgumentException) {
                lastAlert = $"未推送（{(frame == null ? "游戏机未连接" : !sameGame ? "观战的不是游戏机这一局" : "英雄阵亡或游戏机已断开")}）：{text}";
            }
        }
        alertStatus.Text = (panelOn || visionAlertsOn || wardsOn ? "" : "已关闭 · ") +
            (plus is { Mode: "team-view" } && now - plus.ReceivedAt <= 3000 ? "Plus 队伍视角" : "等待 Plus 队伍视角（完整观战不推送）") +
            "\n" + panelText + (wardText.Length > 0 ? "\n" + wardText : "") + (lastAlert.Length > 0 ? "\n" + lastAlert : "");
    }
    /// <summary>
    /// Subtitle for a ward guess, e.g. "莱恩 少了1个假眼 · 可能在敌方上路一塔大野（小地图已标）". Out of sight for long, the
    /// marks are mostly wrong (in replays the likeliest was within 600 units for 71 % of guesses when the hero stayed in sight,
    /// 27 % after 15–60 s unseen): then only that it warded, and no marks (WardTracker.Marks).
    /// </summary>
    private string WardText(WardGuess g) =>
        $"{heroNames[g.Hero]} 少了{g.Count}个{g.Kind switch { "observer" => "假眼", "sentry" => "真眼", _ => "守卫" }}" +
        (g.Unseen >= WardTracker.UnsureSeconds ? $" · {g.Unseen:F0}秒不在视野，位置不确定（小地图不标）" :
            g.Candidates is [var top, ..] ? $" · 可能在{top.Place}{(top.Rough ? "一带" : "")}（小地图已标）" : " · 位置不明");
    private void Refresh()
    {
        if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
        try {
            if (debug.Recorder is { } recorder) {
            var current = host?.Mailbox.Snapshot();
            var frame = host?.Mailbox.Latest();
            if (frame != null) recorder.SampleVideo(frame);
            recorder.State(new(status.Text, current?.Connected == true, preferences.AutomaticAdvice,
                modelConfigured, deciding, modelCalls, inputTokens, outputTokens, current?.FrameAgeMs, current?.Player,
                frame == null ? null : preferences.EffectiveLayout(frame), GsiAgeMs: frame?.Meta.GsiAgeMs,
                RequestLimit: e2e?.MaxModelCalls ?? preferences.RequestLimit, IntervalSeconds: preferences.IntervalSeconds,
                Model: e2e?.Model ?? preferences.Model, SubtitlesEnabled: preferences.Subtitles, MapMarkersEnabled: preferences.MinimapMarkers));
            }
        } catch { /* Debug must not affect live refresh. */ }
        UpdateSpectator();
        UpdateAlerts();
        if (host == null) return;
        var snapshot = host.Mailbox.Snapshot();
        if (e2e != null && File.Exists(System.IO.Path.Combine(e2e.Directory, "enable-model")) &&
            cloud != null && !deciding && modelCalls < e2e.MaxModelCalls &&
            Environment.TickCount64 >= nextDecision && host.Mailbox.Latest() is { } candidate && CloudCoach.Eligible(candidate))
            _ = Decide(candidate, host);
        else if (e2e == null && preferences.AutomaticAdvice && (preferences.Subtitles || preferences.MinimapMarkers) &&
            modelConfigured && cloud != null && !deciding && modelCalls < preferences.RequestLimit &&
            Environment.TickCount64 >= nextDecision && host.Mailbox.Latest() is { } live &&
            CloudCoach.Eligible(live, localBotOnly: false))
            _ = Decide(live, host);
        if (e2e == null && modelConfigured && !deciding)
            autoStatus.Text = !preferences.AutomaticAdvice ? "已暂停自动教练；旧结果不再回传。" :
                modelCalls >= preferences.RequestLimit ? $"本次请求上限已到：{modelCalls}/{preferences.RequestLimit}，已停止付费调用。" :
                $"自动教练已开启 · {modelCalls}/{preferences.RequestLimit}次 · 已记录tokens {inputTokens}+{outputTokens}\n" +
                (snapshot.Player == null ? "等待新鲜自身GSI（不是旁观者全图）。" : "只使用玩家可见画面；过期/失败不回退规则。");
        status.Text = !video ? (snapshot.Connected ? "游戏端已连接 · 加密配对，不传画面" : "等待游戏端连接") :
            snapshot.Connected ? "正在接收 · 加密配对连接" : snapshot.Status;
        detail.Text = (video ? $"累计 {snapshot.Frames} 帧 / {snapshot.Bytes / 1048576.0:F1} MiB · 最新画面 {snapshot.FrameAgeMs?.ToString() ?? "—"} ms 前\n"
                : $"最新状态 {snapshot.FrameAgeMs?.ToString() ?? "—"} ms 前\n") +
            (snapshot.Player is { } p ? $"自身GSI：{(p.Team == "radiant" ? "天辉" : "夜魇")} · {p.Hero} · 游戏 {p.Clock:F0}s · 血 {p.Health:F0} · 蓝 {p.Mana:F0}"
                : "自身GSI：未知或已过期");
        if (!snapshot.Connected)
        {
            ClearView(); CancelDecision();
            previewTitle.Text = "等待新画面 · 不显示过期截图";
            return;
        }
        if (displayed is { } previous && (previous.Meta.SessionId != snapshot.SessionId ||
            previous.Meta.Source != snapshot.Source || previous.Meta.SourceWidth != snapshot.SourceWidth ||
            previous.Meta.SourceHeight != snapshot.SourceHeight || previous.Meta.Player?.MatchId != snapshot.Player?.MatchId ||
            previous.Meta.Player?.Hero != snapshot.Player?.Hero || previous.Meta.Player?.Team != snapshot.Player?.Team ||
            previous.Meta.Player is { } oldPlayer && snapshot.Player is { } newPlayer && newPlayer.Clock < oldPlayer.Clock))
            ClearView();
        if (displayed is { } shown)
            previewTitle.Text = $"{(shown.Meta.Source == "diagnostic" ? "诊断画面 · 不是游戏" : "DOTA 2")} · " +
                (shown.Video is { } v ? $"H.264 {v.Width}×{v.Height} · 预览 {previewFps:F0}fps" : "旧版JPEG") + $" / #{shown.Meta.Sequence}";
    }
    /// <summary>
    /// Shows every new decoded frame (up to the WPF frame clock). The coach scales to the preview size on a worker
    /// thread, so a native 4K stream costs the same on the UI thread as 1080p.
    /// </summary>
    private void UpdatePreview()
    {
        if (host is not { } owner || closing || previewBusy) return;
        var latest = owner.Mailbox.Latest();
        if (latest == null || !latest.HasImage || displayed?.Meta.SessionId == latest.Meta.SessionId && displayed?.Meta.Sequence == latest.Meta.Sequence) return;
        long generation = viewGeneration;
        if (latest.Video is not { } video) {
            try { ShowPreview(latest, Ui.FrameImage(latest)); } catch { notice.Text = "图像解码失败；该帧未显示。"; }
            return;
        }
        double dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var size = Ui.Fit(video.Width, video.Height, Math.Max(320, preview.ActualWidth * dpi), Math.Max(180, preview.ActualHeight * dpi));
        previewBusy = true;
        Task.Run(() => video.ToBgra(size.Width, size.Height)).ContinueWith(t => {
            previewBusy = false;
            if (t.IsFaulted || host != owner || closing || generation != viewGeneration) return;
            if (previewBitmap == null || previewBitmap.PixelWidth != size.Width || previewBitmap.PixelHeight != size.Height)
                previewBitmap = new WriteableBitmap(size.Width, size.Height, 96, 96, PixelFormats.Bgra32, null);
            previewBitmap.WritePixels(new Int32Rect(0, 0, size.Width, size.Height), t.Result, size.Width * 4, 0);
            ShowPreview(latest, previewBitmap);
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }
    private void ShowPreview(FrameView latest, BitmapSource bitmap)
    {
        bool layoutChanged = displayed == null || displayed.Meta.SessionId != latest.Meta.SessionId ||
            displayed.Meta.SourceWidth != latest.Meta.SourceWidth || displayed.Meta.SourceHeight != latest.Meta.SourceHeight;
        if (image.Source != bitmap) image.Source = bitmap;
        displayed = latest;
        if (e2e == null && layoutChanged) DrawCalibration();
        previewShown++;
        if (Environment.TickCount64 - previewRateAt >= 1000) {
            previewFps = previewShown * 1000.0 / Math.Max(1, Environment.TickCount64 - previewRateAt);
            previewShown = 0; previewRateAt = Environment.TickCount64;
        }
    }
    private async Task Decide(FrameView frame, CoachHost target)
    {
        deciding = true; modelCalls++;
        nextDecision = Environment.TickCount64 + (e2e == null ? preferences.IntervalSeconds * 1000 : 20000);
        long generation = modelGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        decisionStop = deadline;
        deadline.CancelAfter(e2e == null ? preferences.ModelTimeoutSeconds * 1000 : 11000);
        if (e2e != null)
            File.WriteAllBytes(System.IO.Path.Combine(e2e.Directory, $"model-frame-{frame.Meta.SessionId}-{frame.Meta.Sequence}.jpg"), frame.Jpeg);
        e2e?.Trace("coach", "model-start", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, player = frame.Meta.Player, attempt = modelCalls });
        autoStatus.Text = visionText.Text = $"模型推理中 #{modelCalls} · 来源帧 {frame.Meta.Sequence}";
        try {
            if (e2e == null) {
                var layout = preferences.MinimapMarkers ? preferences.EffectiveLayout(frame) : null;
                if (activeLayout != layout) {
                    cloud?.Dispose(); activeLayout = layout;
                    cloud = CreateLiveModel(layout);
                }
            }
            debug.Recorder?.ModelStarted(frame, e2e?.Model ?? preferences.Model, activeLayout, modelCalls);
            var result = await cloud!.Decide(frame, deadline.Token);
            inputTokens += result.InputTokens; outputTokens += result.OutputTokens;
            debug.Recorder?.ModelFinished(frame, result);
            e2e?.Trace("coach", "model-result", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, result });
            // Never stamp an old model result with the latest frame; mailbox checks the original capture.
            if (host != target || stop.IsCancellationRequested || deadline.IsCancellationRequested ||
                generation != modelGeneration || e2e == null && !preferences.AutomaticAdvice)
                throw new InvalidOperationException("Context ended");
            var advice = result.Advice;
            var steps = advice.NextSteps.Where(s => !string.IsNullOrWhiteSpace(s)).Take(Wire.MaxRouteSteps)
                .Select(s => s.Trim().Length <= Wire.MaxStepText ? s.Trim() : s.Trim()[..Wire.MaxStepText]).ToArray();
            target.Mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence,
                e2e == null ? "visible-farming-advice" : "experimental-bot-advice",
                advice.Message, advice.Marker, e2e == null ? activeLayout : null,
                e2e != null || preferences.Subtitles,
                e2e == null ? preferences.SubtitleSeconds * 1000 : Wire.DefaultReminderMs,
                advice.RouteMarks, steps.Length == 0 ? null : steps);
            var sent = target.Mailbox.Poll(frame.Meta.SessionId);
            if (sent != null) debug.Recorder?.Reminder("reminder-published", sent);
            e2e?.Trace("coach", "model-published", new { frame = frame.Meta.Sequence, reminder = sent });
            visionText.Text = $"最近模型结果（游戏提醒到期撤回，非最优保证）：{advice.Message}\n" +
                $"路线：{(advice.Route is { Length: > 0 } ids ? string.Join(" → ", ids.Select(LandmarkName)) : "无（未给地标或小地图未知）")}\n" +
                $"后续意图：{string.Join(" → ", advice.NextSteps)}\n取消条件：{advice.CancelIf}\n" +
                $"{result.ElapsedMs}ms · tokens {result.InputTokens}+{result.OutputTokens}";
        }
        catch (Exception error) {
            debug.Recorder?.Error("model-request-or-publication", error, frame.Meta,
                stop.IsCancellationRequested ? "shutdown" : generation != modelGeneration ? "context-cancelled" :
                error is OperationCanceledException ? "timeout" : error is ModelProviderException ? "upstream-status" : "rejected-or-network");
            // No provider body or request/credential in diagnostics.
            e2e?.Trace("coach", "model-rejected", new { frame = frame.Meta.Sequence, type = error.GetType().Name,
                reason = error is OperationCanceledException ? "timeout/cancelled" :
                    error is InvalidOperationException or ArgumentException ? error.Message : "request failed" });
            visionText.Text = "模型失败/过期，未回传；没有规则回退。";
        }
        finally { if (decisionStop == deadline) decisionStop = null; deciding = false; }
    }
    /// <summary>On/off switches sit with the AI output; model and timing details live in a collapsed section.</summary>
    private void BuildAutomaticControls(StackPanel switches, StackPanel settings)
    {
        automatic.IsChecked = preferences.AutomaticAdvice; subtitles.IsChecked = preferences.Subtitles;
        mapMarkers.IsChecked = preferences.MinimapMarkers; autoMinimap.IsChecked = preferences.AutoMinimap;
        var toggles = new WrapPanel();
        foreach (var toggle in new[] { automatic, subtitles, mapMarkers }) { toggle.Margin = new Thickness(0, 0, 16, 4); toggles.Children.Add(toggle); }
        foreach (var toggle in new[] { automatic, subtitles, mapMarkers, autoMinimap }) {
            toggle.Checked += (_, _) => SaveFeatureToggles(); toggle.Unchecked += (_, _) => SaveFeatureToggles();
        }
        switches.Children.Add(toggles); switches.Children.Add(autoStatus);
        switches.Children.Add(Ui.Text("开启后把画面和自身GSI发到云端模型并计费；只提醒，不控制英雄。", 12, Brushes.LightGray));

        foundryFile.Text = preferences.FoundryEnvFile; reasoningModel.Text = preferences.Model;
        interval.Text = preferences.IntervalSeconds.ToString(); requestLimit.Text = preferences.RequestLimit.ToString();
        subtitleSeconds.Text = preferences.SubtitleSeconds.ToString(); modelTimeout.Text = preferences.ModelTimeoutSeconds.ToString();
        var browse = Ui.Button("选择…", (_, _) => {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "配置文件|*.env|所有文件|*.*" };
            if (dialog.ShowDialog(this) == true) { foundryFile.Text = dialog.FileName; ConfigureModel(save: true); }
        });
        browse.Margin = new Thickness(0, 0, 0, 8);
        settings.Children.Add(Ui.Label("Foundry 配置文件（密钥只在教练机读取）")); settings.Children.Add(Ui.Row(foundryFile, browse));
        settings.Children.Add(Ui.Label("部署名")); settings.Children.Add(reasoningModel);
        settings.Children.Add(Ui.Label("调用间隔（秒） / 每次启动的请求上限（失败也计数）")); settings.Children.Add(Ui.Columns(interval, requestLimit));
        settings.Children.Add(Ui.Label("字幕显示（秒，1–15） / 模型超时（秒，8–20）")); settings.Children.Add(Ui.Columns(subtitleSeconds, modelTimeout));
        settings.Children.Add(Ui.Button("保存模型设置", (_, _) => ConfigureModel(save: true)));
    }
    private void BuildCalibration(StackPanel panel)
    {
        panel.Children.Add(autoMinimap);
        panel.Children.Add(Ui.Text("默认 HUD 不用校准。镜像 HUD 或超大小地图：点预设或填小地图正方形的左、上、边长（原始游戏像素），再确认。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Label("左 / 上 / 边长")); panel.Children.Add(Ui.Columns(mapLeft, mapTop, mapSize));
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("预设：左下", (_, _) => PreviewCalibration(false)));
        buttons.Children.Add(Ui.Button("预设：右下", (_, _) => PreviewCalibration(true)));
        buttons.Children.Add(Ui.Button("确认校准", (_, _) => ConfirmCalibration()));
        panel.Children.Add(buttons); panel.Children.Add(mapStatus);
    }
    private void CancelDecision()
    {
        modelGeneration++; decisionStop?.Cancel();
    }
    private void SaveFeatureToggles()
    {
        CancelDecision(); host?.Mailbox.ClearReminder();
        preferences = preferences with { AutomaticAdvice = automatic.IsChecked == true,
            Subtitles = subtitles.IsChecked == true, MinimapMarkers = mapMarkers.IsChecked == true,
            AutoMinimap = autoMinimap.IsChecked == true };
        try { preferences.Save(settingsPath); }
        catch { notice.Text = "设置无法保存，当前开关仅本次有效。"; }
        if (!preferences.AutomaticAdvice) autoStatus.Text = "已暂停自动教练；旧结果不再回传。";
        DrawCalibration();
    }
    private CloudCoach CreateLiveModel(MinimapLayout? layout) => new(preferences.FoundryEnvFile,
        preferences.Model, liveLessons, map: liveMap, localBotOnly: false, layout: layout,
        timeout: TimeSpan.FromSeconds(preferences.ModelTimeoutSeconds));
    private string LandmarkName(string id)
    {
        // Camps carry Chinese labels; towers are unit names such as npc_dota_goodguys_tower2_bot.
        var tower = System.Text.RegularExpressions.Regex.Match(id, @"^npc_dota_(goodguys|badguys)_tower([1-4])(?:_(top|mid|bot))?$");
        if (tower.Success)
            return (tower.Groups[1].Value == "goodguys" ? "天辉 " : "夜魇 ") +
                tower.Groups[3].Value switch { "top" => "上路", "mid" => "中路", "bot" => "下路", _ => "基地" } +
                "一二三四"[tower.Groups[2].Value[0] - '1'] + "塔";
        return liveMap?.Landmarks.FirstOrDefault(l => l.Id == id)?.Label ?? id;
    }
    private void ConfigureModel(bool save)
    {
        CancelDecision(); host?.Mailbox.ClearReminder(); modelConfigured = false;
        cloud?.Dispose(); cloud = null; activeLayout = null;
        try {
            if (!int.TryParse(interval.Text, out int seconds) || !int.TryParse(requestLimit.Text, out int limit) ||
                !int.TryParse(subtitleSeconds.Text, out int display) || !int.TryParse(modelTimeout.Text, out int timeoutSeconds))
                throw new ArgumentException("请输入整数调用间隔、请求上限、字幕秒数和模型超时。");
            var candidate = preferences with { FoundryEnvFile = foundryFile.Text.Trim(), Model = reasoningModel.Text.Trim(),
                IntervalSeconds = seconds, RequestLimit = limit, SubtitleSeconds = display, ModelTimeoutSeconds = timeoutSeconds,
                BindAddress = (string)address.SelectedItem };
            candidate.Validate(); // Invalid input never replaces the working settings.
            preferences = candidate;
            if (save) preferences.Save(settingsPath);
            string data = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            liveMap = new CalibratedMap(System.IO.Path.Combine(data, "dota2-coach-map.json")); // 7.42 camps and towers
            liveLessons = File.ReadAllText(System.IO.Path.Combine(data, "luna-live-lessons.json"));
            cloud = CreateLiveModel(null); modelConfigured = true;
            autoStatus.Text = "模型配置就绪；收到新鲜自身GSI＋真实游戏画面后自动推理。";
        } catch (Exception error) {
            debug.Recorder?.Error("model-configuration", error);
            autoStatus.Text = error is ArgumentException && error.Message.Any(c => c > 127) ? error.Message + " 未发出请求。" :
                "自动教练已选择，但模型未就绪：请选择有效Foundry文件，检查部署名和包内data文件。未发出请求。";
        }
    }
    private FrameView CalibrationFrame()
    {
        var frame = host?.Mailbox.Latest();
        if (frame is not { HasImage: true, Meta.Source: "dota2" }) throw new InvalidOperationException("请保持游戏在前台，先接收真实画面。");
        return frame;
    }
    private MinimapLayout InputCalibration(FrameView frame)
    {
        if (!double.TryParse(mapLeft.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
            !double.TryParse(mapTop.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double y) ||
            !double.TryParse(mapSize.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double size))
            throw new ArgumentException("请填写小地图左、上、边长的原始游戏像素数。");
        var layout = new MinimapLayout(frame.Meta.SourceWidth, frame.Meta.SourceHeight, x, y, size);
        if (!layout.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight)) throw new ArgumentException("小地图框需位于画面左下或右下，不可超出客户区。");
        return layout;
    }
    private void PreviewCalibration(bool right)
    {
        try {
            var frame = CalibrationFrame();
            draftLayout = MinimapLayout.Suggest(frame.Meta.SourceWidth, frame.Meta.SourceHeight, right);
            mapLeft.Text = draftLayout.Left.ToString("0.##", CultureInfo.InvariantCulture);
            mapTop.Text = draftLayout.Top.ToString("0.##", CultureInfo.InvariantCulture);
            mapSize.Text = draftLayout.Size.ToString("0.##", CultureInfo.InvariantCulture);
            DrawCalibration();
        } catch (Exception e) { mapStatus.Text = e.Message; }
    }
    private void ConfirmCalibration()
    {
        try {
            var frame = CalibrationFrame(); var layout = InputCalibration(frame);
            preferences = preferences with { Calibrations = preferences.Calibrations
                .Where(c => c.SourceWidth != layout.SourceWidth || c.SourceHeight != layout.SourceHeight).Append(layout).TakeLast(16).ToArray() };
            preferences.Save(settingsPath);
            CancelDecision(); host?.Mailbox.ClearReminder(); draftLayout = null;
            DrawCalibration();
        } catch (Exception e) { mapStatus.Text = e is ArgumentException or InvalidOperationException ? e.Message : "无法保存校准。"; }
    }
    private void DrawCalibration()
    {
        calibrationMarks.Children.Clear();
        if (displayed is not { Meta.Source: "dota2" } f) return;
        bool draft = draftLayout?.IsValidFor(f.Meta.SourceWidth, f.Meta.SourceHeight) == true;
        var confirmed = preferences.LayoutFor(f);
        var layout = draft ? draftLayout : preferences.EffectiveLayout(f);
        mapStatus.Text = $"游戏画面 {f.Meta.SourceWidth}×{f.Meta.SourceHeight}：" +
            (draft ? "蓝框是预览，点“确认校准”保存。" : confirmed != null ? "已保存此分辨率的校准。" :
             layout != null ? "使用自动默认框（虚线）。没对准小地图时请校准。" :
             "未校准且未启用默认框：只发字幕，不画路线。");
        if (layout == null) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, f.Meta.SourceWidth, f.Meta.SourceHeight);
        var box = new Rectangle { Width = layout.Size / f.Meta.SourceWidth * rect.Width,
            Height = layout.Size / f.Meta.SourceHeight * rect.Height, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2,
            StrokeDashArray = draft || confirmed != null ? null : new DoubleCollection { 4, 3 } };
        Canvas.SetLeft(box, rect.X + layout.Left / f.Meta.SourceWidth * rect.Width);
        Canvas.SetTop(box, rect.Y + layout.Top / f.Meta.SourceHeight * rect.Height);
        calibrationMarks.Children.Add(box);
    }
    private void ClearView()
    {
        viewGeneration++;
        image.Source = null; displayed = null;
        calibrationMarks.Children.Clear();
    }
    /// <summary>Link check without the model: one fixed caption on the newest frame.</summary>
    private void SendTest()
    {
        try {
            if (host?.Mailbox.Latest() is not { } frame) throw new InvalidOperationException();
            host.Mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence, "manual-link-test", "双机链路测试：这不是 AI 建议。", null);
            if (host.Mailbox.Poll(frame.Meta.SessionId) is { } reminder) debug.Recorder?.Reminder("reminder-published", reminder);
            notice.Text = "已发送测试提醒，请在游戏机上确认字幕。";
        }
        catch { notice.Text = "未发送：游戏端未连接，或英雄已阵亡。"; }
    }
}
