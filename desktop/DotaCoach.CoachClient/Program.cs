using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
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
        try { new Application().Run(new CoachWindow(E2ERun.Load(args))); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) {
            MessageBox.Show("无法打开本机对局档案。请关闭同一账户下已运行的教练端，检查档案目录的权限和磁盘空间后重试。\n" +
                MatchArchive.DefaultRoot, "Dota Coach");
        }
    }
}

internal sealed partial class CoachWindow : Window
{
    private readonly ComboBox address = new() { FontSize = 14, Padding = new Thickness(6), Margin = new Thickness(0, 0, 0, 8) };
    private readonly TextBox port = Ui.Input("38471");
    private readonly Button listen;
    private readonly TextBlock status = Ui.Text("接收服务未启动", 17, Ui.Accent);
    private readonly TextBlock detail = Ui.Text("", 12, Brushes.LightGray);
    private readonly TextBlock notice = Ui.Text("先启动接收，再复制配对码到游戏端。", 13, Ui.Accent);
    private readonly TextBlock visionText = Ui.Text("");
    private readonly TextBlock previewTitle = Ui.Text("等待游戏端连接", 14, Brushes.LightGray);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Grid preview = new() { Background = Brushes.Black, Visibility = Visibility.Collapsed };
    private readonly RowDefinition previewRow = new() { Height = GridLength.Auto };
    private readonly AppPages pages;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource stop = new();
    private CoachHost? host;
    private CancellationTokenSource? starting;
    private FrameView? displayed;
    private bool closing, busy;
    private readonly E2ERun? e2e;
    private CloudCoach? cloud;
    private bool deciding;
    private int modelCalls;
    private long nextDecision;
    private readonly string settingsPath;
    private LiveCoachSettings preferences;
    private readonly MatchArchive? matchArchive;
    private readonly LiveMatchState matchState;
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
    private MatchFarmSlice? visionInFlight;
    private long modelGeneration, inputTokens, outputTokens;
    private readonly DebugPanel debug;
    // Dota Plus on THIS machine: raw packets never go to the model; matched team observations
    // enter MatchState, which shares the bounded minute projection only with separate consent.
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
    private readonly CheckBox enemyPanel = new() { Content = "对手等级、装备和属性（游戏左侧面板）", Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 4) };
    private readonly CheckBox enemyVision = new() { Content = "对手离开视野", Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 4) };
    private readonly TextBlock alertStatus = Ui.Text("对手提醒：等待 Plus 队伍视角", 12, Brushes.LightGray);
    private readonly TextBox visionLossDelay = Ui.Input();
    private readonly TextBlock visionLossNote = Ui.Text("", 12, Brushes.LightGray);
    private readonly VisionLossTracker visionLoss = new();
    private readonly AlertComposer alerts = new();
    private HeroNames heroNames = new();
    private string scanFile = ScanFeed.DefaultPath; // instance field so UI tests never read the real scanner output
    private ScanStatus scanStatus = new(false, "视觉扫描未运行");
    private volatile bool panelOn, visionAlertsOn; // also read on the spectator receiver's threads
    private long nextScanPoll;
    private string lastAlert = "", panelText = "";

    // Tests instantiate inert windows explicitly; the application entry point uses automaticStartup=true.
    public CoachWindow() : this(null, false) { }
    public CoachWindow(E2ERun? run, bool automaticStartup = true, string? preferencesFile = null)
        : this(run, automaticStartup, preferencesFile, null) { }
    public CoachWindow(E2ERun? run, bool automaticStartup, string? preferencesFile, string? debugRoot)
        : this(run, automaticStartup, preferencesFile, debugRoot, null) { }
    public CoachWindow(E2ERun? run, bool automaticStartup, string? preferencesFile, string? debugRoot,
        Func<HttpMessageHandler>? modelHandlerFactory)
    {
        this.modelHandlerFactory = modelHandlerFactory;
        e2e = run; debug = new DebugPanel("coach", debugRoot);
        settingsPath = preferencesFile ?? LiveCoachSettings.DefaultPath;
        string historyRoot = run != null ? System.IO.Path.Combine(run.Directory, "match-history") :
            preferencesFile != null ? System.IO.Path.Combine(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(preferencesFile))!, "match-history") :
            MatchArchive.DefaultRoot;
        try { matchArchive = new(historyRoot); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
            // No second writer and no silent temporary replacement. Pairing/filtered game HUD can still
            // operate; the operator gets a persistent banner, and real paid features fail closed.
            matchArchive = null;
        }
        matchState = new(matchArchive);
        if (matchState.StorageWarning is { } initialWarning) detail.Text = initialWarning;
        try { preferences = LiveCoachSettings.Load(settingsPath); }
        catch {
            // An invalid Codey or unknown profile is not consent to read Foundry credentials.
            // Incidental preference saves cannot overwrite the rejected file until an explicit provider is committed.
            preferences = new() { AutomaticAdvice = false, ModelProvider = "" }; modelEditsPending = true;
            notice.Text = "已保存的设置无效；自动教练已暂停。请重新选择模型服务、保存设置后再开启。";
        }
        try { heroNames = new HeroNames(System.IO.Path.Combine(AppContext.BaseDirectory, "data", "dota2-dictionary.json")); }
        catch { /* Alerts fall back to internal hero names. */ }
        Title = "Dota Coach · 教练端 v0.6.6";
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(940, SystemParameters.WorkArea.Height - 40);
        MinWidth = 1000; MinHeight = 640; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Background; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var home = new Grid();
        home.ColumnDefinitions.Add(new() { Width = new GridLength(400) });
        home.ColumnDefinitions.Add(new() { Width = new GridLength(24) });
        home.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Ui.Text("游戏端只传本人 GSI，不传画面。对手装备、眼位、字幕和分钟路线推送到游戏里显示；赛后按时间复盘。", 12, Brushes.LightGray));
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
        // Home: connection, Plus/scanner state and the everyday switches. Everything set once lives in grouped settings.
        listen = Ui.Button("启动接收", async (_, _) => { if (busy || host != null) await Stop(); else await Start(); });
        left.Children.Add(status); left.Children.Add(detail); left.Children.Add(notice);
        var everyday = new WrapPanel();
        everyday.Children.Add(Ui.Button("复制配对码", (_, _) => CopyPairing()));
        if (e2e == null) everyday.Children.Add(BuildMatchReview());
        left.Children.Add(everyday);
        var switches = new StackPanel();
        var connection = new StackPanel();
        connection.Children.Add(Ui.Label("本机地址 / 端口（跨机请选局域网IP，不要选WSL虚拟网卡；127.0.0.1 只用于同机）"));
        port.Width = 90;
        connection.Children.Add(Ui.Row(address, port));
        var connectionButtons = new WrapPanel();
        connectionButtons.Children.Add(listen);
        connectionButtons.Children.Add(Ui.Button("撤销配对", (_, _) => {
            host?.Revoke(); matchState.Disconnect(); CancelDecision(); ResetMinuteFarmConfirmation(); ClearView();
            notice.Text = "旧配对已撤销；请重新复制配对码。";
        }));
        connectionButtons.Children.Add(Ui.Button("发送测试提醒", (_, _) => SendTest()));
        connection.Children.Add(connectionButtons);
        var right = new Grid();
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(previewRow);
        var vision = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        vision.Children.Add(Ui.Text(e2e == null ? "AI 自动教练" : "AI 本地机器人局验收", 20, Ui.Accent));
        vision.Children.Add(switches);
        vision.Children.Add(visionText);
        if (e2e != null) vision.Children.Add(Ui.Text("LOCAL BOT E2E：已显式配置Foundry云模型；真实图像和自身GSI会送往该服务并计费。仅本地局，非最优路线保证。", 12, Brushes.LightGray));
        right.Children.Add(vision);
        Grid.SetRow(previewTitle, 1); right.Children.Add(previewTitle);
        preview.Children.Add(image); preview.Children.Add(calibrationMarks);
        Grid.SetRow(preview, 2); right.Children.Add(preview);
        home.Children.Add(new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Grid.SetColumn(right, 2); home.Children.Add(right);
        pages = new AppPages("教练端", home);
        pages.Add("连接", connection, "接收服务随教练端启动；换地址或端口后要重新复制配对码。");
        if (e2e == null) {
            left.Children.Add(Ui.Text("Plus 观战与对手信息", 17, Ui.Accent));
            var plusSettings = BuildSpectatorPanel(left);
            var modelSettings = new StackPanel(); BuildAutomaticControls(switches, modelSettings);
            var share = new CheckBox { Content = "允许分钟模型读取本队当时已知的装备、视野、推测眼位及兵线",
                IsChecked = preferences.ModelTeamKnowledge, Foreground = Brushes.White };
            share.Checked += (_, _) => SaveMatchSharing(true); share.Unchecked += (_, _) => SaveMatchSharing(false);
            modelSettings.Children.Add(share);
            modelSettings.Children.Add(Ui.Text("仅勾选后，这些信息会随本人状态发给所选模型服务。全图录像永远不进实战模型。", 12, Brushes.LightGray));
            var calibration = new StackPanel(); BuildCalibration(calibration);
            pages.Add("Plus 观战与扫描", plusSettings, "本机 Dota 用另一个账号对好友实时观战；首次使用先在这里配置一次。");
            pages.Add("AI 模型", modelSettings, "模型服务、调用频率，以及 AI 结果推送到游戏的方式。");
            pages.Add("小地图校准", calibration, "分钟路线要先确认当前分辨率的小地图框；眼位和兵线没确认时按游戏端自己的小地图设置画。");
        }
        pages.Add("调试", debug.View, "排查问题时才打开；记录只存本机。");
        Content = new Border { Child = pages, Margin = new Thickness(24) };
        preview.SizeChanged += (_, _) => DrawCalibration();
        timer.Tick += (_, _) => Refresh(); timer.Start();
        // Status / GSI-only sessions need the normal state tick, not a render-rate image poll.
        previewTimer.Tick += (_, _) => UpdatePreview();
        if (e2e == null && automaticStartup) Loaded += async (_, _) => {
            ConfigureModel(save: false);
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
            while (scanBusy || spectatorStarting) await Task.Delay(25);
            await ReleaseScan();
            if (spectator != null) { await spectator.DisposeAsync(); spectator = null; }
            cloud?.Dispose();
            await StopMatchReviewAsync();
            try { matchArchive?.Dispose(); }
            catch (IOException error) {
                debug.Recorder?.Error("match-archive-close", error);
                MessageBox.Show(this, "本机对局档案未能完整写入，请检查剩余磁盘空间；不要把缺失区段当作没有事件。", "时间线写入失败");
            }
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
            // The paired game chooses the transport. This is lazy: neither status ingress nor
            // ticketed GSI metadata has pixels to decode, preview, log or send to an image model.
            host.Mailbox.VideoJpegEncoder = Ui.ExtractJpeg;
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
        matchState.Disconnect();
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
    /// <summary>The Plus settings group. The live state, scan button and the three "push to the game" switches go on
    /// <paramref name="home"/>: they are what the coach looks at and flips during a match.</summary>
    private StackPanel BuildSpectatorPanel(StackPanel home)
    {
        visionLoss.Map = wardTracker.Map;
        home.Children.Add(spectatorLive);
        scanButton = Ui.Button("启动视觉扫描", async (_, _) => await ToggleScan());
        home.Children.Add(Ui.Row(scanState, scanButton));
        home.Children.Add(Ui.Label("推送到游戏机（只用队伍视角看得到的信息）"));
        enemyPanel.IsChecked = panelOn = preferences.EnemyPanel;
        enemyVision.IsChecked = visionAlertsOn = preferences.EnemyVisionAlerts;
        enemyWards.IsChecked = preferences.EnemyWards;
        laneOverlay.IsChecked = preferences.LaneWavesOnMap;
        var toggles = new WrapPanel();
        foreach (var toggle in new[] { enemyPanel, enemyVision, enemyWards }) {
            toggles.Children.Add(toggle);
            toggle.Checked += (_, _) => SaveAlertToggles(); toggle.Unchecked += (_, _) => SaveAlertToggles();
        }
        home.Children.Add(toggles); home.Children.Add(alertStatus);

        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("本机 Dota 用账号 B 对好友“实时观战”。只记录与游戏机同局同阵营的队伍视角；主页的推送开关只控制游戏端显示，不控制记录或模型授权。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Label("本机 Dota 安装目录")); panel.Children.Add(spectatorInstall);
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("一键配置观战 GSI", async (_, _) => await StartSpectator()));
        buttons.Children.Add(Ui.Button("复制启动参数", (_, _) => spectatorSetup.Text = Ui.CopyLaunchOptions(SteamLaunch.Inspect())));
        panel.Children.Add(buttons); panel.Children.Add(spectatorSetup);
        visionLoss.GoneMs = preferences.VisionLossSeconds * 1000;
        visionLossDelay.Text = preferences.VisionLossSeconds.ToString(); visionLossDelay.Width = 90;
        visionLossDelay.TextChanged += (_, _) => SaveVisionLossDelay();
        panel.Children.Add(Ui.Label($"对手离开视野多少秒后提醒（1–{VisionLossTracker.MaxGoneSeconds}，默认 {VisionLossTracker.DefaultGoneMs / 1000}）"));
        visionLossNote.VerticalAlignment = VerticalAlignment.Center; visionLossNote.Margin = new Thickness(12, 0, 0, 0);
        panel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Children = { visionLossDelay, visionLossNote } });
        fogOverlay.IsChecked = preferences.EnemyFogOnMap;
        panel.Children.Add(fogOverlay);
        panel.Children.Add(Ui.Text($"敌方英雄看不到 {FogTracker.FlickerSeconds:0} 秒后，在游戏端小地图标出最后位置和面朝方向；前 {FogTracker.CircleSeconds:0} 秒画出它大概率所在的圆" +
            $"（47 场录像里约七成落在圈内），{FogTracker.MaxSeconds:0} 秒后撤掉。刚被击杀的不画。", 12, Brushes.LightGray));
        panel.Children.Add(laneOverlay);
        foreach (var toggle in new[] { laneOverlay, fogOverlay }) {
            toggle.Checked += (_, _) => SaveAlertToggles(); toggle.Unchecked += (_, _) => SaveAlertToggles();
        }
        panel.Children.Add(Ui.Label("兵线交汇（按游戏秒推演，不发送队友 ping）")); panel.Children.Add(waveStatus);
        panel.Children.Add(Ui.Text("空线基线假设同批小兵一路畅通；卡兵、拉兵、推线、塔火和上一波残兵都会改变实际交汇。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Text("面板一直显示敌方英雄上次读到的等级、装备和读取年龄，到本局结束；新的读数需在本机前台观战并启动视觉扫描。" +
            "守卫数量减少也可能是转交、丢弃或卖掉。7.42 常见眼位只给候选，不当作目击；确认过小地图框时按框画，否则按游戏端自己的小地图设置。" +
            "离开视野时我方击杀数刚好增加的当作阵亡，不提醒（GSI 本身分不出阵亡和进迷雾，偶有误判）；字幕里的“朝…”是消失时面朝的地方，不是确定的去向；这类字幕不会盖住正在显示的分钟路线。" +
            (wardTracker.Map == null ? " 缺少 7.42 地图，无法推测眼位位置。" : ""), 12, Brushes.LightGray));
        AddScanLauncher(panel);
        return panel;
    }
    private async Task StartSpectator()
    {
        if (spectatorStarting || closing) return;
        spectatorStarting = true;
        try {
            if (spectator == null) {
                var receiver = await SpectatorReceiver.Start(spectatorPort, GsiSetup.Token(spectatorState), stop.Token);
                if (closing) { await receiver.DisposeAsync(); return; }
                // Raw spectator diagnostics remain on the coach; the live consumer admits matched team view only.
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
                "full-spectator" => "完整观战（仅本机诊断，不进入实战档案、模型或游戏提醒）", "player" => "本机在打游戏，不是观战", _ => s.Mode };
        var text = new System.Text.StringBuilder($"{head} · 已收 {packets} 包{(rejected > 0 ? $" · 拒绝 {rejected}" : "")}");
        if (s is { Running: true }) {
            int Count(Func<MinimapUnit, bool> f) => s.Heroes.Where(f).Select(u => u.Name).Distinct().Count();
            int allies = Count(u => u.Team == s.OwnTeam), enemies = Count(u => u.Team is 2 or 3 && u.Team != s.OwnTeam);
            var neutrals = s.Minimap.Where(u => u.Team == 4 && u.Name.StartsWith("npc_dota_neutral_", StringComparison.Ordinal)).ToArray();
            // Coach-local diagnostics, never evidence published to the paired participant.
            int camps = wardTracker.Map is { } map ? neutrals.Select(u => map.CampAt(u.X, u.Y, 300)?.Id).OfType<string>().Distinct().Count() : 0;
            text.Append($"\n游戏 {Clock(s.Clock!.Value)} · {(s.OwnTeam == 2 ? "天辉" : s.OwnTeam == 3 ? "夜魇" : "阵营未知")} · 小地图 {s.Minimap.Length} 个单位\n" +
                $"己方英雄 {allies} · 视野内敌方英雄 {enemies} · 视野内野怪 {neutrals.Length}" + (camps > 0 ? $"（{camps} 个营地）" : "") +
                (s.GroupedPlayers > 0 ? $" · 10人分组 {s.GroupedPlayers}" : ""));
        }
        text.Append(delay.MedianMs is double median
            ? $"\n比游戏机慢：中位 {median / 1000:F2}s（P10 {delay.P10Ms / 1000:F2} / P90 {delay.P90Ms / 1000:F2}，{delay.Samples} 个样本；受两端采样和局域网传输延迟影响）"
            : $"\n延迟：{delay.Note}" + (delay.ClockGapSeconds is double gap ? $"（两边游戏时间相差 {gap:F0}s）" : ""));
        spectatorLive.Text = text.ToString();
    }
    /// <summary>Spectator receiver threads: enemies leaving the friend team's vision become pending alerts.</summary>
    private void OnSpectator(SpectatorSnapshot s)
    {
        ObserveTeamSight(s); // queued once; UpdateMatchIntel processes sight before deriving vision loss
    }
    private void SaveAlertToggles()
    {
        panelOn = enemyPanel.IsChecked == true; visionAlertsOn = enemyVision.IsChecked == true;
        alerts.Clear(); // don't deliver an already queued, now disabled class of subtitle
        preferences = preferences with { EnemyPanel = panelOn, EnemyVisionAlerts = visionAlertsOn, EnemyWards = enemyWards.IsChecked == true,
            LaneWavesOnMap = laneOverlay.IsChecked == true, EnemyFogOnMap = fogOverlay.IsChecked == true };
        try { preferences.Save(settingsPath); }
        catch { notice.Text = "设置无法保存，当前开关仅本次有效。"; }
    }
    /// <summary>Applies at once: an absence already being timed is announced once the new delay has passed.</summary>
    private void SaveVisionLossDelay()
    {
        if (!int.TryParse(visionLossDelay.Text.Trim(), out int seconds) || seconds is < 1 or > VisionLossTracker.MaxGoneSeconds) {
            visionLossNote.Text = $"请填 1–{VisionLossTracker.MaxGoneSeconds} 的整数；仍按 {visionLoss.GoneMs / 1000} 秒提醒。"; return;
        }
        visionLoss.GoneMs = seconds * 1000;
        preferences = preferences with { VisionLossSeconds = seconds };
        try { preferences.Save(settingsPath); visionLossNote.Text = $"已保存：看不到满 {seconds} 秒才提醒。"; }
        catch { visionLossNote.Text = "设置无法保存，仅本次有效。"; }
    }
    /// <summary>Point-free Plus subtitles require a matched participant and current team view. MatchState alone supplies board contents.</summary>
    private void UpdateAlerts(FrameView? frame)
    {
        long now = Environment.TickCount64;
        var own = TeamViewEvidence.Participant(frame, now);
        lock (sightGate) {
            if (own == null || own.MatchId != matchState.ActiveMatch || !LegacySubtitles(frame) ||
                sightScope?.Session != frame?.Meta.SessionId || processedGeneration != sightGeneration ||
                !TeamViewEvidence.Matches(own, latestSight, now)) { alerts.Clear(); return; }
        }
        if (host == null || frame == null) { alerts.Clear(); return; }
        try {
            if (host.Mailbox.Poll(frame.Meta.SessionId)?.Kind == MinuteFarmContext.Kind) { alerts.Clear(); return; }
            if (alerts.Next(now, preferences.SubtitleSeconds * 1000) is { } text) {
                host.Mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence, "local-vision-observation", text, null,
                    displayMs: preferences.SubtitleSeconds * 1000);
                if (host.Mailbox.Poll(frame.Meta.SessionId) is { } sent) debug.Recorder?.Reminder("reminder-published", sent);
                lastAlert = "已推送：" + text;
            }
            ShowTeamStatus(true, panelOn && host.Mailbox.Enemies(frame.Meta.SessionId) is { Heroes.Length: > 0 } sentBoard
                ? $" · 已推送 {sentBoard.Heroes.Length} 名敌方英雄" : "");
        } catch (Exception e) when (e is InvalidOperationException or ArgumentException) {
            // Session revoked between Latest and Poll/Enemies, or origin no longer deliverable.
            alerts.Clear(); lastAlert = "未推送（连接、参赛者或画面已变化）";
        }
    }
    private void Refresh()
    {
        if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
        var pairedFrame = host?.Mailbox.Latest();
        try {
            if (debug.Recorder is { } recorder) {
                var current = host?.Mailbox.Snapshot();
                if (pairedFrame is { HasImage: true }) recorder.SampleVideo(pairedFrame);
                recorder.State(new(status.Text, current?.Connected == true, preferences.AutomaticAdvice,
                    modelConfigured, deciding, modelCalls, inputTokens, outputTokens, current?.FrameAgeMs, current?.Player,
                    pairedFrame == null ? null : preferences.EffectiveLayout(pairedFrame), GsiAgeMs: pairedFrame?.Meta.GsiAgeMs,
                    RequestLimit: e2e?.MaxModelCalls ?? preferences.RequestLimit, IntervalSeconds: preferences.IntervalSeconds,
                    Model: e2e?.Model ?? preferences.EffectiveModel, SubtitlesEnabled: preferences.Subtitles, MapMarkersEnabled: preferences.MinimapMarkers));
            }
        } catch { /* Debug must not affect live refresh. */ }
        bool minuteMode = e2e == null && preferences.MinuteFarm742;
        if (e2e == null) {
            if (host?.Mailbox.TakePostgame() is { } terminal) {
                matchState.ObserveFrame(terminal);
                if (terminal.Meta.MatchPhase?.MatchId == knownBoard.Match) knownBoard.Clear();
            }
            matchState.ObserveFrame(pairedFrame);
            if (visionInFlight is { } origin && decisionStop is { IsCancellationRequested: false } && !VisionContinues(origin, pairedFrame))
                CancelDecision();
            UpdateSpectator(); UpdateMatchIntel(pairedFrame);
            matchState.ExpireTeamSources();
            ShowLaneWaves(pairedFrame);
            // The game machine always gets the last-known panel of its match: equipment rows age instead of
            // disappearing. A confirmed minimap box places the wards and waves; without one the game uses its own HUD setting.
            var layout = pairedFrame == null ? null : preferences.LayoutFor(pairedFrame);
            var panel = knownBoard.Board(Environment.TickCount64, preferences.EnemyPanel, preferences.EnemyWards, layout);
            if (preferences.LaneWavesOnMap && pairedFrame != null &&
                matchState.ForGame(pairedFrame, false, false, layout, laneWaves: true, confirmedWaveMap: MinuteFarmArmedFor(pairedFrame)) is { LaneMap: not null } waves &&
                (panel == null || panel.MatchId == waves.MatchId && panel.Team == waves.Team))
                panel = panel == null ? waves : waves with { Heroes = panel.Heroes, Wards = panel.Wards, SeenWards = panel.SeenWards };
            if (preferences.EnemyFogOnMap && fogNow is { Heroes.Length: > 0 } gone && (panel == null || panel.MatchId == gone.Match && panel.Team == gone.Team))
                panel = (panel ?? new EnemyBoard([], null, gone.Match, gone.Team, layout)) with { Fog = gone.Heroes };
            host?.Mailbox.SetEnemies(panel);
            UpdateAlerts(pairedFrame); // Markerless Plus alerts reach the GSI-only game client; a live minute route keeps its slot.
        }
        if (host == null) return;
        var snapshot = host.Mailbox.Snapshot();
        if (e2e != null && File.Exists(System.IO.Path.Combine(e2e.Directory, "enable-model")) &&
            cloud != null && !deciding && modelCalls < e2e.MaxModelCalls &&
            Environment.TickCount64 >= nextDecision && host.Mailbox.Latest() is { } candidate && CloudCoach.Eligible(candidate))
            _ = Decide(candidate, host);
        else if (minuteMode)
            RefreshMinuteFarm(host, pairedFrame);
        else if (e2e == null && preferences.AutomaticAdvice && (preferences.Subtitles || preferences.MinimapMarkers) &&
            modelConfigured && catalogStop == null && cloud != null && !deciding && modelCalls < preferences.RequestLimit &&
            Environment.TickCount64 >= nextDecision && pairedFrame is { } live && CloudCoach.Eligible(live, localBotOnly: false) &&
            matchState.ForVision(live) is { } visionInput)
            _ = Decide(live with { Meta = live.Meta with { Player = visionInput.Own } }, host, visionInput);
        bool metadataOnly = pairedFrame is { HasImage: false };
        if (e2e == null && !minuteMode && modelConfigured && !deciding && catalogStop == null)
            autoStatus.Text = ActiveModelLabel + " · " + (metadataOnly ?
                (pairedFrame!.Meta.Source == "dota2-gsi" ? "本人 GSI；可选 7.42 分钟路线。" : "普通状态连接；需要分钟规划请更新游戏端。") + "没有画面，不调用视觉模型。" :
                !preferences.AutomaticAdvice ? "已暂停自动教练；旧结果不再回传。" :
                modelCalls >= preferences.RequestLimit ? $"本次请求上限已到：{modelCalls}/{preferences.RequestLimit}，已停止模型调用。" :
                $"自动教练已开启 · {modelCalls}/{preferences.RequestLimit}次 · 已记录tokens {inputTokens}+{outputTokens}\n" +
                (snapshot.Player == null ? "等待新鲜自身GSI（不是旁观者全图）。" : "只使用玩家可见画面；过期/失败不回退规则。"));
        status.Text = !snapshot.Connected ? snapshot.Status : metadataOnly ? "游戏端已连接 · 加密配对，不传画面" : "正在接收 · 加密配对连接";
        detail.Text = (metadataOnly ? $"最新状态 {snapshot.FrameAgeMs?.ToString() ?? "—"} ms 前\n" :
            $"累计 {snapshot.Frames} 帧 / {snapshot.Bytes / 1048576.0:F1} MiB · 最新画面 {snapshot.FrameAgeMs?.ToString() ?? "—"} ms 前\n") +
            (snapshot.Player is { } p ? $"自身GSI：{(p.Team == "radiant" ? "天辉" : "夜魇")} · {p.Hero} · 游戏 {p.Clock:F0}s · 血 {p.Health:F0} · 蓝 {p.Mana:F0}"
                : "自身GSI：未知或已过期");
        if (matchState.StorageWarning is { } warning) {
            detail.Text += "\n" + warning; autoStatus.Text = "仅实时显示：档案不可用，自动决策已暂停。";
            // Withdraw only paid suggestions. Live equipment/ward projections remain independently deliverable.
            if (e2e == null && deciding && decisionStop?.IsCancellationRequested == false) CancelDecision();
            if (e2e == null) host.Mailbox.ClearModelReminder();
        }
        if (!snapshot.Connected)
        {
            ClearView(); CancelDecision();
            previewTitle.Text = "等待游戏端连接";
            return;
        }
        if (displayed is { } previous && (previous.Meta.SessionId != snapshot.SessionId ||
            previous.Meta.Source != snapshot.Source || previous.Meta.SourceWidth != snapshot.SourceWidth ||
            previous.Meta.SourceHeight != snapshot.SourceHeight || previous.Meta.Player?.MatchId != snapshot.Player?.MatchId ||
            previous.Meta.Player?.Hero != snapshot.Player?.Hero || previous.Meta.Player?.Team != snapshot.Player?.Team ||
            previous.Meta.Player is { } oldPlayer && snapshot.Player is { } newPlayer && newPlayer.Clock < oldPlayer.Clock))
            ClearView();
        if (pairedFrame is { HasImage: true } && !previewTimer.IsEnabled) previewTimer.Start();
        if (metadataOnly) {
            ShowStatus(pairedFrame!);
            var meta = pairedFrame!.Meta;
            string kind = meta.Source switch { "dota2-gsi" => "本人 GSI", "diagnostic" => "诊断状态", _ => "普通状态" };
            previewTitle.Text = $"{kind} · " + (meta.SourceWidth > 0 ? $"{meta.SourceWidth}×{meta.SourceHeight}" : "游戏窗口不可用") + " · 没有采集游戏画面";
        }
        else if (displayed is { } shown)
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
        if (latest == null || displayed?.Meta.SessionId == latest.Meta.SessionId && displayed?.Meta.Sequence == latest.Meta.Sequence) return;
        if (!latest.HasImage) { ShowStatus(latest); return; }
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
    // Legacy status is labelled "dota2" or "diagnostic"; never use the source string alone to infer pixels.
    private void ShowStatus(FrameView latest)
    {
        previewTimer.Stop();
        bool changed = displayed == null || displayed.HasImage || displayed.Meta.Source != latest.Meta.Source ||
            displayed.Meta.SessionId != latest.Meta.SessionId || displayed.Meta.SourceWidth != latest.Meta.SourceWidth ||
            displayed.Meta.SourceHeight != latest.Meta.SourceHeight;
        if (changed) ClearView(); // Fence an in-flight BGRA conversion before dropping the old bitmap.
        displayed = latest;
        if (changed) DrawCalibration();
    }
    private void ShowPreview(FrameView latest, BitmapSource bitmap)
    {
        bool layoutChanged = displayed is not { HasImage: true } || displayed.Meta.Source != latest.Meta.Source ||
            displayed.Meta.SessionId != latest.Meta.SessionId ||
            displayed.Meta.SourceWidth != latest.Meta.SourceWidth || displayed.Meta.SourceHeight != latest.Meta.SourceHeight;
        if (image.Source != bitmap) image.Source = bitmap;
        displayed = latest;
        if (layoutChanged) DrawCalibration();
        previewShown++;
        if (Environment.TickCount64 - previewRateAt >= 1000) {
            previewFps = previewShown * 1000.0 / Math.Max(1, Environment.TickCount64 - previewRateAt);
            previewShown = 0; previewRateAt = Environment.TickCount64;
        }
    }
    // A real match always needs the reducer's participant even on the way OUT. A status packet
    // is not a new image and postgame/menu/another seat must not inherit an in-flight answer.
    private bool VisionContinues(MatchFarmSlice origin, FrameView? current) => matchState.CanAudit &&
        matchState.ActiveMatch == origin.Own.MatchId && current is { HasImage: true } && current.Meta.SessionId == origin.Session &&
        CloudCoach.Eligible(current, localBotOnly: false) && TeamViewEvidence.Participant(current, Environment.TickCount64) is { Phase: "playing" } w &&
        w.MatchId == origin.Own.MatchId && w.Hero == origin.Own.Hero && w.Team == origin.Own.Team && w.Clock >= origin.Own.Clock;

    private async Task Decide(FrameView frame, CoachHost target, MatchFarmSlice? capturedState = null)
    {
        if (e2e == null && (capturedState == null || !VisionContinues(capturedState, frame))) return;
        visionInFlight = capturedState; deciding = true; modelCalls++;
        nextDecision = Environment.TickCount64 + (e2e == null ? preferences.IntervalSeconds * 1000 : 20000);
        long generation = modelGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        decisionStop = deadline;
        deadline.CancelAfter(e2e == null ? preferences.ModelTimeoutSeconds * 1000 : 11000);
        if (e2e != null)
            File.WriteAllBytes(System.IO.Path.Combine(e2e.Directory, $"model-frame-{frame.Meta.SessionId}-{frame.Meta.Sequence}.jpg"), frame.Jpeg);
        e2e?.Trace("coach", "model-start", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, player = frame.Meta.Player, attempt = modelCalls });
        autoStatus.Text = visionText.Text = $"{(e2e?.Model ?? ActiveModelLabel)} 推理中 #{modelCalls} · 来源帧 {frame.Meta.Sequence}";
        bool inModelRequest = false;
        try {
            if (e2e == null) {
                var layout = preferences.MinimapMarkers ? preferences.EffectiveLayout(frame) : null;
                if (activeLayout != layout) {
                    modelConfigured = false; cloud?.Dispose(); cloud = null; activeLayout = null;
                    cloud = CreateLiveModel(layout); activeLayout = layout; modelConfigured = true;
                }
            }
            debug.Recorder?.ModelStarted(frame, e2e?.Model ?? preferences.EffectiveModel, activeLayout, modelCalls);
            if (capturedState != null) {
                matchState.NoteDecision(capturedState, "image-requested");
                if (!matchState.CanAudit) throw new InvalidOperationException("Match archive unavailable");
            }
            inModelRequest = true;
            var result = await cloud!.Decide(frame, deadline.Token);
            inModelRequest = false;
            inputTokens += result.InputTokens; outputTokens += result.OutputTokens;
            debug.Recorder?.ModelFinished(frame, result);
            e2e?.Trace("coach", "model-result", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, result });
            // Never stamp an old model result with the latest frame; mailbox checks the original capture.
            if (host != target || stop.IsCancellationRequested || deadline.IsCancellationRequested ||
                generation != modelGeneration || e2e == null && (!preferences.AutomaticAdvice || preferences.MinuteFarm742 ||
                    !VisionContinues(capturedState!, target.Mailbox.Latest())))
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
            if (capturedState != null) matchState.NoteDecision(capturedState, "image-published",
                chosen: advice.Route == null ? null : string.Join(" → ", advice.Route));
            if (sent != null) debug.Recorder?.Reminder("reminder-published", sent);
            e2e?.Trace("coach", "model-published", new { frame = frame.Meta.Sequence, reminder = sent });
            visionText.Text = $"{e2e?.Model ?? ActiveModelLabel} · 最近结果（游戏提醒到期撤回，非最优保证）：{advice.Message}\n" +
                $"路线：{(advice.Route is { Length: > 0 } ids ? string.Join(" → ", ids.Select(LandmarkName)) : "无（未给地标或小地图未知）")}\n" +
                $"后续意图：{string.Join(" → ", advice.NextSteps)}\n取消条件：{advice.CancelIf}\n" +
                $"{result.ElapsedMs}ms · tokens {result.InputTokens}+{result.OutputTokens}";
        }
        catch (Exception error) {
            if (capturedState != null) matchState.NoteDecision(capturedState, "image-failed");
            debug.Recorder?.Error("model-request-or-publication", error, frame.Meta,
                stop.IsCancellationRequested ? "shutdown" : generation != modelGeneration ? "context-cancelled" :
                error is OperationCanceledException ? "timeout" : error is ModelProviderException ? "upstream-status" : "rejected-or-network");
            // No provider body or request/credential in diagnostics.
            e2e?.Trace("coach", "model-rejected", new { frame = frame.Meta.Sequence, type = error.GetType().Name,
                httpStatus = (error as ModelProviderException)?.StatusCode });
            if (generation == modelGeneration && host == target && !closing) ReportModelFailure(error, vision: true, inModelRequest);
        }
        finally { if (decisionStop == deadline) decisionStop = null; visionInFlight = null; deciding = false; }
    }
    /// <summary>The AI master switch and minute mode sit with the AI output on the home page; where results go,
    /// the model service and its timing live in the AI settings group.</summary>
    private void BuildAutomaticControls(StackPanel switches, StackPanel settings)
    {
        automatic.IsChecked = preferences.AutomaticAdvice; subtitles.IsChecked = preferences.Subtitles;
        mapMarkers.IsChecked = preferences.MinimapMarkers; autoMinimap.IsChecked = preferences.AutoMinimap;
        foreach (var toggle in new[] { automatic, subtitles, mapMarkers, autoMinimap }) {
            toggle.Checked += (_, _) => SaveFeatureToggles(); toggle.Unchecked += (_, _) => SaveFeatureToggles();
        }
        automatic.Margin = new Thickness(0, 0, 16, 4);
        switches.Children.Add(automatic); BuildMinuteSetup(switches); switches.Children.Add(autoStatus);

        interval.Text = preferences.IntervalSeconds.ToString(); requestLimit.Text = preferences.RequestLimit.ToString();
        subtitleSeconds.Text = preferences.SubtitleSeconds.ToString(); modelTimeout.Text = preferences.ModelTimeoutSeconds.ToString();
        settings.Children.Add(Ui.Label("AI 结果推送到游戏（分钟路线需要「小地图路线」）"));
        var outputs = new WrapPanel();
        foreach (var toggle in new[] { subtitles, mapMarkers }) { toggle.Margin = new Thickness(0, 0, 16, 4); outputs.Children.Add(toggle); }
        settings.Children.Add(outputs);
        BuildProviderControls(settings);
        settings.Children.Add(Ui.Label("普通模式间隔（秒） / 本次启动请求上限（两种模式共用，失败也计数）")); settings.Children.Add(Ui.Columns(interval, requestLimit));
        settings.Children.Add(Ui.Label("字幕显示（秒，1–15） / 模型超时（秒，8–20）")); settings.Children.Add(Ui.Columns(subtitleSeconds, modelTimeout));
        settings.Children.Add(Ui.Button("保存模型设置", (_, _) => ConfigureModel(save: true)));
        settings.Children.Add(Ui.Text("推理可能计费或消耗配额。分钟模式默认只发本人数据和模拟候选，勾选下方授权才加入队伍资料。不控制英雄。", 12, Brushes.LightGray));
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
    private void SaveMatchSharing(bool enabled)
    {
        CancelDecision(); host?.Mailbox.ClearModelReminder();
        preferences = preferences with { ModelTeamKnowledge = enabled };
        try { preferences.Save(settingsPath); } catch { notice.Text = "本次授权已变更，但未能保存到本机。"; }
    }
    private void SaveFeatureToggles()
    {
        CancelDecision(); host?.Mailbox.ClearReminder();
        bool minuteChanged = preferences.MinuteFarm742 != (minuteFarm742.IsChecked == true);
        preferences = preferences with { AutomaticAdvice = automatic.IsChecked == true,
            Subtitles = subtitles.IsChecked == true, MinimapMarkers = mapMarkers.IsChecked == true,
            AutoMinimap = autoMinimap.IsChecked == true, MinuteFarm742 = minuteFarm742.IsChecked == true };
        try { preferences.Save(settingsPath); }
        catch { notice.Text = "设置无法保存，当前开关仅本次有效。"; }
        UpdateMinuteSetup();
        if (minuteChanged) { visionText.Text = ""; ConfigureModel(save: false); }
        if (!preferences.AutomaticAdvice) autoStatus.Text = "已暂停自动教练；旧结果不再回传。";
        else if (!modelConfigured) autoStatus.Text = modelSetupHint;
        DrawCalibration();
    }
    private string LandmarkName(string id)
    {
        var label = liveMap?.Landmarks.FirstOrDefault(l => l.Id == id)?.Label;
        if (label != null && label != id) return label; // 7.42 supplies localized labels for towers as well as camps.
        // Historical bot maps only have tower unit names such as npc_dota_goodguys_tower2_bot.
        var tower = System.Text.RegularExpressions.Regex.Match(id, @"^npc_dota_(goodguys|badguys)_tower([1-4])(?:_(top|mid|bot))?$");
        if (tower.Success)
            return (tower.Groups[1].Value == "goodguys" ? "天辉 " : "夜魇 ") +
                (tower.Groups[3].Value switch { "top" => "上路", "mid" => "中路", "bot" => "下路", _ => "基地" }) +
                "一二三四"[tower.Groups[2].Value[0] - '1'] + "塔";
        return id;
    }
    private FrameView CalibrationFrame()
    {
        var frame = host?.Mailbox.Latest();
        if (frame == null || frame.Meta.IsLegacyStatus || frame.Meta.Source is not ("dota2" or "dota2-gsi") ||
            frame.Meta.SourceWidth <= 0 || frame.Meta.SourceHeight <= 0)
            throw new InvalidOperationException("请先连接游戏端，接收真实的游戏窗口尺寸。");
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
        // The game client sends no pictures: the preview (and the box drawn over it) exists only for developer video.
        bool show = displayed is { HasImage: true };
        SetPreviewVisible(show);
        if (e2e != null) return;
        if (displayed is not { Meta.IsLegacyStatus: false, Meta.Source: "dota2" or "dota2-gsi", Meta.SourceWidth: > 0, Meta.SourceHeight: > 0 } f) {
            mapStatus.Text = "等待实际游戏窗口尺寸；不沿用上一个画面的校准。"; return;
        }
        bool draft = draftLayout?.IsValidFor(f.Meta.SourceWidth, f.Meta.SourceHeight) == true;
        var confirmed = preferences.LayoutFor(f);
        var layout = draft ? draftLayout : preferences.EffectiveLayout(f);
        mapStatus.Text = (!f.HasImage ? "游戏端不传画面：请对照游戏里实际的小地图。 " : "") +
            $"游戏窗口 {f.Meta.SourceWidth}×{f.Meta.SourceHeight}：" +
            (draft ? "蓝框是预览，点“确认校准”保存。" : confirmed != null ? "已保存此分辨率的校准。" :
             layout != null ? "使用自动默认框（虚线）。没对准小地图时请校准。" :
             "没有已确认的小地图框；此模式不会猜测位置。");
        if (layout == null || !show) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, f.Meta.SourceWidth, f.Meta.SourceHeight);
        var box = new Rectangle { Width = layout.Size / f.Meta.SourceWidth * rect.Width,
            Height = layout.Size / f.Meta.SourceHeight * rect.Height, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2,
            StrokeDashArray = draft || confirmed != null ? null : new DoubleCollection { 4, 3 } };
        Canvas.SetLeft(box, rect.X + layout.Left / f.Meta.SourceWidth * rect.Width);
        Canvas.SetTop(box, rect.Y + layout.Top / f.Meta.SourceHeight * rect.Height);
        calibrationMarks.Children.Add(box);
    }
    private void SetPreviewVisible(bool show)
    {
        if ((preview.Visibility == Visibility.Visible) == show) return;
        preview.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        previewRow.Height = show ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
    }
    private void ClearView()
    {
        previewTimer.Stop(); viewGeneration++;
        image.Source = null; previewBitmap = null; displayed = null;
        SetPreviewVisible(false); calibrationMarks.Children.Clear();
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
        catch { notice.Text = "未发送：还没有新鲜画面，或英雄已阵亡。"; }
    }
}
