using System;
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
    private readonly TextBox message = Ui.Input("双机链路已连接。这是测试提醒，不是 AI 路线。");
    private readonly TextBox model = Ui.Input();
    private readonly TextBox markX = Ui.Input();
    private readonly TextBox markY = Ui.Input();
    private readonly TextBlock status = Ui.Text("接收服务未启动", 17, Ui.Accent);
    private readonly TextBlock stats = Ui.Text("没有接收或保存图像");
    private readonly TextBlock player = Ui.Text("自身 GSI：未知");
    private readonly TextBlock notice = Ui.Text("先启动接收，再复制配对码到游戏端。", 14, Ui.Accent);
    private readonly TextBlock visionText = Ui.Text("自动教练等待模型配置、真实画面和新鲜自身GSI。");
    private readonly TextBlock previewTitle = Ui.Text("LIVE VIEW  /  等待游戏端", 22, Ui.Accent);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas marks = new() { IsHitTestVisible = false };
    private readonly Grid preview = new() { Background = Brushes.Black };
    private readonly CheckBox frozen = new() { Content = "冻结预览（不停止接收）", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(100) };
    private readonly CancellationTokenSource stop = new();
    private CoachHost? host;
    private CancellationTokenSource? starting;
    private FrameView? displayed, visionFrame;
    private VisionObservation? observation;
    private ScreenMark? selected;
    private DeferredLinkProbe? deferredProbe;
    private bool closing, busy, observing;
    private readonly E2ERun? e2e;
    private CloudCoach? cloud;
    private bool deciding;
    private int modelCalls;
    private long nextDecision;
    private readonly string settingsPath;
    private LiveCoachSettings preferences;
    private readonly CheckBox automatic = new() { Content = "自动教练（普通对局＋机器人局）", Foreground = Brushes.White };
    private readonly CheckBox subtitles = new() { Content = "游戏字幕", Foreground = Brushes.White };
    private readonly CheckBox mapMarkers = new() { Content = "小地图目标", Foreground = Brushes.White };
    private readonly TextBox foundryFile = Ui.Input();
    private readonly TextBox reasoningModel = Ui.Input();
    private readonly TextBox interval = Ui.Input();
    private readonly TextBox requestLimit = Ui.Input();
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
    private readonly TextBlock spectatorSetup = Ui.Text("尚未配置。本机需用账号B登录Steam（有Dota Plus，并与游戏账号互为好友）。", 12, Brushes.LightGray);
    private readonly TextBlock spectatorLive = Ui.Text("Plus 观战：未启动", 13);
    private readonly PlusDelayTracker plusDelay = new();
    private SpectatorReceiver? spectator;
    private int spectatorPort = SpectatorReceiver.DefaultPort;
    private bool spectatorStarting;
    private string? delaySession;
    private long delaySequence, nextSpectatorText;

    // Tests instantiate inert windows explicitly; the application entry point uses automaticStartup=true.
    public CoachWindow() : this(null, false) { }
    public CoachWindow(E2ERun? run, bool automaticStartup = true, string? preferencesFile = null)
        : this(run, automaticStartup, preferencesFile, null) { }
    public CoachWindow(E2ERun? run, bool automaticStartup, string? preferencesFile, string? debugRoot)
    {
        e2e = run; debug = new DebugPanel("coach", debugRoot);
        settingsPath = preferencesFile ?? LiveCoachSettings.DefaultPath;
        try { preferences = LiveCoachSettings.Load(settingsPath); }
        catch { preferences = new(); notice.Text = "已保存的设置无效，请检查并重新应用；没有读取或输出密钥内容。"; }
        Title = "Dota Coach · 教练端（3090 Ti）v0.3.1";
        Width = Math.Min(1320, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(940, SystemParameters.WorkArea.Height - 40);
        MinWidth = 1000; MinHeight = 680; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Background; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var root = new Grid { Margin = new Thickness(24) };
        root.ColumnDefinitions.Add(new() { Width = new GridLength(330) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(20) });
        root.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        var left = new StackPanel();
        left.Children.Add(Ui.Text("COACH CLIENT", 26, Ui.Accent));
        left.Children.Add(Ui.Text("后台机器 · 接收 / 视觉 / 提醒回传"));
        left.Children.Add(Ui.Text("配对码中的本机地址"));
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
        left.Children.Add(address);
        left.Children.Add(Ui.Text("127.0.0.1 仅用于同机；跨机请选真实局域网地址，不要误选WSL虚拟网卡。", 12, Brushes.LightGray));
        left.Children.Add(Ui.Text("HTTPS 接收端口（默认38471）")); left.Children.Add(port);
        var controls = new WrapPanel();
        controls.Children.Add(Ui.Button("启动局域网接收", async (_, _) => await Start()));
        controls.Children.Add(Ui.Button("停止", async (_, _) => await Stop()));
        left.Children.Add(controls);
        var pairing = new WrapPanel();
        pairing.Children.Add(Ui.Button("复制配对码", (_, _) => CopyPairing()));
        pairing.Children.Add(Ui.Button("撤销配对", (_, _) => { CancelProbe(); host?.Revoke(); ClearView(); notice.Text = "旧配对已撤销；请重新复制配对码。"; }));
        left.Children.Add(pairing); left.Children.Add(status); left.Children.Add(stats); left.Children.Add(player);
        if (e2e == null) left.Children.Add(new Expander { Header = "Plus 实时观战 · 实验（不接入自动教练）", Content = BuildSpectatorPanel(),
            Foreground = Brushes.White, IsExpanded = true, Margin = new Thickness(0, 0, 0, 8) });
        left.Children.Add(new Expander { Header = "Debug / 抽帧、模型与客户端状态", Content = debug.View, Foreground = Brushes.White });
        if (e2e == null) BuildAutomaticControls(left);
        left.Children.Add(Ui.Text("人工联调（不是 AI 决策）", 19, Ui.Accent));
        message.AcceptsReturn = true; message.Height = 70; message.MaxLength = 120; message.TextWrapping = TextWrapping.Wrap;
        left.Children.Add(message);
        left.Children.Add(Ui.Text("点击预览里的小地图可选一个画面坐标标记。点击会冻结预览；标记不是世界坐标路线。", 12, Brushes.LightGray));
        left.Children.Add(frozen);
        var send = new WrapPanel();
        send.Children.Add(Ui.Button("回传测试字幕 / 标记", (_, _) => Send()));
        send.Children.Add(Ui.Button("清除回传", (_, _) => { CancelProbe(); host?.Mailbox.ClearReminder(); selected = null; marks.Children.Clear(); frozen.IsChecked = false; }));
        left.Children.Add(send);
        left.Children.Add(Ui.Text("同机测试坐标 x / y（0–1，可留空；仅画面测试点，不是地图路线）", 12, Brushes.LightGray));
        var coordinates = new Grid();
        coordinates.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        coordinates.ColumnDefinitions.Add(new() { Width = new GridLength(1, GridUnitType.Star) });
        markX.Margin = new Thickness(0, 0, 8, 8); Grid.SetColumn(markY, 1);
        coordinates.Children.Add(markX); coordinates.Children.Add(markY); left.Children.Add(coordinates);
        left.Children.Add(Ui.Button("5秒后测试回传（请切回Dota）", (_, _) => ScheduleProbe()));
        left.Children.Add(notice);
        var scroll = new ScrollViewer { Content = left, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        root.Children.Add(scroll);

        var right = new Grid();
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        right.RowDefinitions.Add(new() { Height = GridLength.Auto });
        right.Children.Add(previewTitle);
        preview.Children.Add(image); preview.Children.Add(marks); preview.Children.Add(calibrationMarks);
        Grid.SetRow(preview, 1); right.Children.Add(preview);
        var vision = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        vision.Children.Add(Ui.Text(e2e == null ? "AI 自动教练 · 普通模式" : "AI 本地机器人局验收", 20, Ui.Accent));
        vision.Children.Add(visionText);
        var localProbe = new StackPanel();
        localProbe.Children.Add(Ui.Text("仅用于手动诊断，不是自动教练的前置步骤。Ollama 已安装视觉模型名称：", 12, Brushes.LightGray));
        localProbe.Children.Add(model);
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Button("识别最新游戏画面一次", async (_, _) => await Observe()));
        actions.Children.Add(Ui.Button("回传本次观察字幕", (_, _) => SendObservation()));
        localProbe.Children.Add(actions);
        vision.Children.Add(new Expander { Header = "本地 Ollama 视觉诊断（可选）", Content = localProbe,
            Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) });
        vision.Children.Add(Ui.Text(e2e == null ?
            "自动教练默认开启：真实画面＋自身GSI发送到指定Foundry并计费。只提醒，不控制英雄。默认不存图；开启Debug才落盘。" :
            "LOCAL BOT E2E：已显式配置Foundry云模型；真实图像和自身GSI会送往该服务并计费。仅本地局，非最优路线保证。", 12, Brushes.LightGray));
        Grid.SetRow(vision, 2); right.Children.Add(vision); Grid.SetColumn(right, 2); root.Children.Add(right);
        Content = root;
        preview.MouseLeftButtonDown += SelectMark; preview.SizeChanged += (_, _) => { DrawMark(); DrawCalibration(); };
        timer.Tick += (_, _) => Refresh(); timer.Start();
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
            e.Cancel = true; closing = true; stop.Cancel(); timer.Stop(); CancelDecision();
            await Stop();
            if (spectator != null) { await spectator.DisposeAsync(); spectator = null; }
            cloud?.Dispose();
            await debug.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private async Task Start()
    {
        if (busy || host != null || closing) return;
        busy = true;
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
            host.Mailbox.VideoJpegEncoder = Ui.ExtractJpeg;
            port.IsEnabled = false; address.IsEnabled = false;
            if (e2e == null) {
                preferences = preferences with { BindAddress = bind.ToString() };
                try { preferences.Save(settingsPath); } catch { /* Runtime remains usable; no credentials are serialized. */ }
            }
            status.Text = $"HTTPS 接收已开启 · {host.ListeningAddress}:{host.Port}";
            notice.Text = "复制配对码到游戏端。若跨机无法连接，请仅为此程序/端口允许 Windows 专用网络入站。";
        }
        catch (Exception e) { debug.Recorder?.Error("coach-listen", e); notice.Text = e is ArgumentException ? e.Message : "无法启动接收：检查端口是否被占用。未修改防火墙。"; }
        finally { starting?.Dispose(); starting = null; busy = false; }
    }
    private async Task Stop()
    {
        CancelProbe(); CancelDecision();
        starting?.Cancel();
        while (busy) await Task.Delay(25);
        var current = host; host = null;
        if (current != null) await current.DisposeAsync();
        port.IsEnabled = true; address.IsEnabled = true; ClearView(); status.Text = "接收服务已停止";
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
        panel.Children.Add(Ui.Text("本机Dota用账号B对游戏账号“实时观战”（Plus，队伍视野）。这里显示收到的小地图等数据，并测量它比游戏机慢多少；" +
            "结果不送进自动教练。开Debug会把这些数据按秒存盘，供事后分析。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Text("本机 Dota 安装目录", 12)); panel.Children.Add(spectatorInstall);
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("一键配置观战 GSI", async (_, _) => await StartSpectator()));
        buttons.Children.Add(Ui.Button("复制启动参数", (_, _) => spectatorSetup.Text = Ui.CopyLaunchOptions(SteamLaunch.Inspect())));
        panel.Children.Add(buttons); panel.Children.Add(spectatorSetup); panel.Children.Add(spectatorLive);
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
                receiver.Received = s => { plusDelay.Spectator(s); debug.Recorder?.Spectator(s); };
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
            int neutrals = s.Minimap.Count(u => u.Team == 4 && u.Name.StartsWith("npc_dota_neutral_", StringComparison.Ordinal));
            text.Append($"\n游戏 {Clock(s.Clock!.Value)} · {(s.OwnTeam == 2 ? "天辉" : s.OwnTeam == 3 ? "夜魇" : "阵营未知")} · 小地图 {s.Minimap.Length} 个单位\n" +
                $"己方英雄 {allies} · 视野内敌方英雄 {enemies} · 视野内野怪 {neutrals}" +
                (s.GroupedPlayers > 0 ? $" · 10人分组 {s.GroupedPlayers}" : ""));
        }
        text.Append(delay.MedianMs is double median
            ? $"\n比游戏机慢：中位 {median / 1000:F2}s（P10 {delay.P10Ms / 1000:F2} / P90 {delay.P90Ms / 1000:F2}，{delay.Samples} 个样本；可能偏低约0.1–0.2s）"
            : $"\n延迟：{delay.Note}" + (delay.ClockGapSeconds is double gap ? $"（两边游戏时间相差 {gap:F0}s）" : ""));
        spectatorLive.Text = text.ToString();
    }
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
                frame == null ? null : preferences.LayoutFor(frame), GsiAgeMs: frame?.Meta.GsiAgeMs,
                RequestLimit: e2e?.MaxModelCalls ?? preferences.RequestLimit, IntervalSeconds: preferences.IntervalSeconds,
                Model: e2e?.Model ?? preferences.Model, SubtitlesEnabled: preferences.Subtitles, MapMarkersEnabled: preferences.MinimapMarkers));
            }
        } catch { /* Debug must not affect live refresh. */ }
        UpdateSpectator();
        if (host == null) return;
        if (deferredProbe is { } pending)
        {
            string result = pending.Tick(host.Mailbox);
            if (result != "waiting") {
                notice.Text = result == "sent" ? "已针对新的真实游戏画面回传一次测试提醒（非AI决策）。" :
                    "延迟联调已取消/超时；没有新鲜真实画面及自身GSI，不会发送。";
                deferredProbe = null;
            }
        }
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
        status.Text = snapshot.Connected ? "正在接收 · 加密配对连接" : snapshot.Status;
        stats.Text = $"累计 {snapshot.Frames} 帧 / {snapshot.Bytes / 1048576.0:F2} MiB\n最新画面年龄 {snapshot.FrameAgeMs?.ToString() ?? "—"} ms（教练机接收后）";
        player.Text = snapshot.Player is { } p ? $"自身GSI：{(p.Team == "radiant" ? "天辉" : "夜魇")} · 对局 {p.MatchId}\n{p.Hero}\n游戏 {p.Clock:F1}s · 血 {p.Health:F0} · 蓝 {p.Mana:F0}" : "自身 GSI：未知 / 已过期（不从全图补全）";
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
        if (frozen.IsChecked == true) { previewTitle.Text = "预览已冻结 · 传输仍继续 · 提醒最多12秒有效"; return; }
        var latest = host.Mailbox.Latest();
        if (latest == null || displayed?.Meta.SessionId == latest.Meta.SessionId && displayed?.Meta.Sequence == latest.Meta.Sequence) return;
        try
        {
            image.Source = Ui.FrameImage(latest); displayed = latest; selected = null; marks.Children.Clear();
            if (e2e == null) DrawCalibration();
            previewTitle.Text = $"{(latest.Meta.Source == "diagnostic" ? "诊断画面 · 不是游戏" : "DOTA 2")} · {(latest.Video == null ? "旧版JPEG" : "H.264实时视频")} / #{latest.Meta.Sequence}";
        }
        catch { notice.Text = "图像解码失败；该帧未显示。"; }
    }
    private async Task Decide(FrameView frame, CoachHost target)
    {
        deciding = true; modelCalls++;
        nextDecision = Environment.TickCount64 + (e2e == null ? preferences.IntervalSeconds * 1000 : 20000);
        long generation = modelGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        decisionStop = deadline;
        deadline.CancelAfter(11000);
        if (e2e != null)
            File.WriteAllBytes(System.IO.Path.Combine(e2e.Directory, $"model-frame-{frame.Meta.SessionId}-{frame.Meta.Sequence}.jpg"), frame.Jpeg);
        e2e?.Trace("coach", "model-start", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, player = frame.Meta.Player, attempt = modelCalls });
        autoStatus.Text = visionText.Text = $"模型推理中 #{modelCalls} · 来源帧 {frame.Meta.Sequence}";
        try {
            if (e2e == null) {
                var layout = preferences.MinimapMarkers ? preferences.LayoutFor(frame) : null;
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
            target.Mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence,
                e2e == null ? "visible-farming-advice" : "experimental-bot-advice",
                result.Advice.Message, result.Advice.Marker, e2e == null ? activeLayout : null,
                e2e != null || preferences.Subtitles);
            var sent = target.Mailbox.Poll(frame.Meta.SessionId);
            if (sent != null) debug.Recorder?.Reminder("reminder-published", sent);
            e2e?.Trace("coach", "model-published", new { frame = frame.Meta.Sequence, reminder = sent });
            visionText.Text = $"最近模型结果（游戏提醒到期撤回，非最优保证）：{result.Advice.Message}\n" +
                $"后续意图：{string.Join(" → ", result.Advice.NextSteps)}\n取消条件：{result.Advice.CancelIf}\n" +
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
    private void BuildAutomaticControls(StackPanel panel)
    {
        panel.Children.Add(Ui.Text("自动打钱教练 · 默认开启", 20, Ui.Accent));
        automatic.IsChecked = preferences.AutomaticAdvice; subtitles.IsChecked = preferences.Subtitles;
        mapMarkers.IsChecked = preferences.MinimapMarkers;
        automatic.Margin = subtitles.Margin = mapMarkers.Margin = new Thickness(0, 0, 10, 7);
        panel.Children.Add(automatic);
        var features = new WrapPanel(); features.Children.Add(subtitles); features.Children.Add(mapMarkers); panel.Children.Add(features);
        foreach (var toggle in new[] { automatic, subtitles, mapMarkers }) {
            toggle.Checked += (_, _) => SaveFeatureToggles(); toggle.Unchecked += (_, _) => SaveFeatureToggles();
        }
        foundryFile.Text = preferences.FoundryEnvFile; reasoningModel.Text = preferences.Model;
        interval.Text = preferences.IntervalSeconds.ToString(); requestLimit.Text = preferences.RequestLimit.ToString();
        panel.Children.Add(Ui.Text("Foundry 配置文件（仅教练机读取，不把密钥打包/传给游戏机）", 12));
        panel.Children.Add(foundryFile);
        panel.Children.Add(Ui.Button("选择模型配置文件", (_, _) => {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "配置文件|*.env|所有文件|*.*" };
            if (dialog.ShowDialog(this) == true) { foundryFile.Text = dialog.FileName; ConfigureModel(save: true); }
        }));
        panel.Children.Add(Ui.Text("实际部署名")); panel.Children.Add(reasoningModel);
        panel.Children.Add(Ui.Text("调用间隔（秒） / 本次进程请求上限（失败也计数）", 12));
        var limits = new Grid();
        limits.ColumnDefinitions.Add(new()); limits.ColumnDefinitions.Add(new());
        interval.Margin = new Thickness(0, 0, 8, 8); Grid.SetColumn(requestLimit, 1);
        limits.Children.Add(interval); limits.Children.Add(requestLimit); panel.Children.Add(limits);
        panel.Children.Add(Ui.Button("应用模型设置", (_, _) => ConfigureModel(save: true)));
        panel.Children.Add(autoStatus);
        panel.Children.Add(Ui.Text("画面会发送到指定云端并计费；默认20秒一次、每进程最多180次。暂停即取消待返回结果；不会自动控制英雄。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Text("小地图校准 · 原始游戏像素", 18, Ui.Accent));
        panel.Children.Add(Ui.Text("填小地图内部正方形的左/上/边长。可预览左/右侧默认框后调整；确认一次按游戏分辨率保存，换HUD需重校准。", 12));
        var fields = new Grid();
        for (int i = 0; i < 3; i++) fields.ColumnDefinitions.Add(new());
        var inputs = new[] { mapLeft, mapTop, mapSize };
        for (int i = 0; i < inputs.Length; i++) { Grid.SetColumn(inputs[i], i); inputs[i].Margin = new Thickness(0, 0, 6, 8); fields.Children.Add(inputs[i]); }
        panel.Children.Add(fields);
        var presets = new WrapPanel();
        presets.Children.Add(Ui.Button("预览左下框", (_, _) => PreviewCalibration(false)));
        presets.Children.Add(Ui.Button("预览右下框", (_, _) => PreviewCalibration(true))); panel.Children.Add(presets);
        panel.Children.Add(Ui.Button("预览输入框", (_, _) => PreviewCalibration(null)));
        panel.Children.Add(Ui.Button("确认小地图框", (_, _) => ConfirmCalibration()));
        panel.Children.Add(mapStatus);
    }
    private void CancelDecision()
    {
        modelGeneration++; decisionStop?.Cancel();
    }
    private void SaveFeatureToggles()
    {
        CancelDecision(); host?.Mailbox.ClearReminder();
        preferences = preferences with { AutomaticAdvice = automatic.IsChecked == true,
            Subtitles = subtitles.IsChecked == true, MinimapMarkers = mapMarkers.IsChecked == true };
        try { preferences.Save(settingsPath); }
        catch { notice.Text = "设置无法保存，当前开关仅本次有效。"; }
        if (!preferences.AutomaticAdvice) autoStatus.Text = "已暂停自动教练；旧结果不再回传。";
    }
    private CloudCoach CreateLiveModel(MinimapLayout? layout) => new(preferences.FoundryEnvFile,
        preferences.Model, liveLessons, map: liveMap, localBotOnly: false, layout: layout);
    private void ConfigureModel(bool save)
    {
        CancelDecision(); host?.Mailbox.ClearReminder(); modelConfigured = false;
        cloud?.Dispose(); cloud = null; activeLayout = null;
        try {
            if (!int.TryParse(interval.Text, out int seconds) || !int.TryParse(requestLimit.Text, out int limit))
                throw new ArgumentException("请输入整数调用间隔和请求上限。");
            preferences = preferences with { FoundryEnvFile = foundryFile.Text.Trim(), Model = reasoningModel.Text.Trim(),
                IntervalSeconds = seconds, RequestLimit = limit, BindAddress = (string)address.SelectedItem };
            preferences.Validate();
            if (save) preferences.Save(settingsPath);
            string data = System.IO.Path.Combine(AppContext.BaseDirectory, "data");
            liveMap = new CalibratedMap(System.IO.Path.Combine(data, "dota2-map-reference.json"));
            liveLessons = File.ReadAllText(System.IO.Path.Combine(data, "luna-live-lessons.json"));
            cloud = CreateLiveModel(null); modelConfigured = true;
            autoStatus.Text = "模型配置就绪；收到新鲜自身GSI＋真实游戏画面后自动推理。";
        } catch (Exception error) {
            debug.Recorder?.Error("model-configuration", error);
            autoStatus.Text = "自动教练已选择，但模型未就绪：请选择有效Foundry文件，检查部署名和包内data文件。未发出请求。";
        }
    }
    private FrameView CalibrationFrame()
    {
        var frame = host?.Mailbox.Latest();
        if (frame == null || frame.Meta.Source != "dota2") throw new InvalidOperationException("请保持游戏在前台，先接收真实画面。");
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
    private void PreviewCalibration(bool? right)
    {
        try {
            var frame = CalibrationFrame();
            draftLayout = right is bool side ? MinimapLayout.Suggest(frame.Meta.SourceWidth, frame.Meta.SourceHeight, side) : InputCalibration(frame);
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
        var layout = draftLayout?.IsValidFor(f.Meta.SourceWidth, f.Meta.SourceHeight) == true
            ? draftLayout : preferences.LayoutFor(f);
        mapStatus.Text = $"游戏客户区 {f.Meta.SourceWidth}×{f.Meta.SourceHeight}（不是JPEG尺寸）\n" +
            (draftLayout == layout && layout != null ? "蓝框仅预览，尚未确认。" : layout != null ? "已保存此分辨率校准；HUD位置/大小改变时请重新确认。" : "尚未校准：字幕自动运行，地图目标等待确认，绝不沿用720p坐标。");
        if (layout == null) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, f.Meta.SourceWidth, f.Meta.SourceHeight);
        var box = new Rectangle { Width = layout.Size / f.Meta.SourceWidth * rect.Width,
            Height = layout.Size / f.Meta.SourceHeight * rect.Height, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 2 };
        Canvas.SetLeft(box, rect.X + layout.Left / f.Meta.SourceWidth * rect.Width);
        Canvas.SetTop(box, rect.Y + layout.Top / f.Meta.SourceHeight * rect.Height);
        calibrationMarks.Children.Add(box);
    }
    private void ClearView()
    {
        image.Source = null; displayed = null; selected = null; marks.Children.Clear(); frozen.IsChecked = false;
        calibrationMarks.Children.Clear();
        observation = null; visionFrame = null;
    }
    private void SelectMark(object sender, MouseButtonEventArgs e)
    {
        if (displayed == null) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, displayed.Meta.SourceWidth, displayed.Meta.SourceHeight);
        var position = e.GetPosition(preview);
        if (!rect.Contains(position)) return;
        selected = new((position.X - rect.X) / rect.Width, (position.Y - rect.Y) / rect.Height);
        frozen.IsChecked = true; DrawMark();
        notice.Text = "已选择当前画面的测试标记；尽快发送，过期后会拒绝。";
    }
    private void DrawMark()
    {
        marks.Children.Clear();
        if (selected is not { } m || displayed == null) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, displayed.Meta.SourceWidth, displayed.Meta.SourceHeight);
        var circle = new Ellipse { Width = 24, Height = 24, Stroke = Brushes.Gold, StrokeThickness = 3 };
        Canvas.SetLeft(circle, rect.X + m.X * rect.Width - 12); Canvas.SetTop(circle, rect.Y + m.Y * rect.Height - 12);
        marks.Children.Add(circle);
    }
    private void Send()
    {
        try
        {
            if (host == null || displayed == null) throw new InvalidOperationException("没有新鲜画面。");
            host.Mailbox.SetReminder(displayed.Meta.SessionId, displayed.Meta.Sequence, "manual-link-test", message.Text, selected);
            if (host.Mailbox.Poll(displayed.Meta.SessionId) is { } reminder) debug.Recorder?.Reminder("reminder-published", reminder);
            notice.Text = "已回传人工测试提醒；不是模型决策。"; frozen.IsChecked = false; selected = null; marks.Children.Clear();
        }
        catch { notice.Text = "未发送：画面/会话过期、英雄死亡或文本无效。解除冻结，等待新画面后重试。"; }
    }
    private void CancelProbe() { deferredProbe?.Cancel(); deferredProbe = null; }
    private void ScheduleProbe()
    {
        try
        {
            if (host == null) throw new InvalidOperationException("请先启动接收服务。");
            ScreenMark? point = null;
            if (markX.Text.Trim() != "" || markY.Text.Trim() != "")
            {
                if (!double.TryParse(markX.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double x) ||
                    !double.TryParse(markY.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out double y))
                    throw new ArgumentException("坐标需要同时填写0–1之间的数字，或同时留空。");
                point = new(x, y);
            }
            CancelProbe(); deferredProbe = new DeferredLinkProbe(message.Text, point);
            notice.Text = "已安排一次人工联调：请切回Dota。5秒后等待新画面和存活英雄GSI；25秒内没有则取消。不是AI路线。";
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { notice.Text = e.Message; }
    }
    private async Task Observe()
    {
        if (observing) return;
        var targetHost = host; var frame = targetHost?.Mailbox.Latest();
        if (frame == null) { visionText.Text = "先接收真实游戏画面。"; return; }
        observing = true; observation = null; visionFrame = null; visionText.Text = "正在教练机本地识别；无云端回退…";
        try
        {
            using var vision = new LocalVision();
            var result = await vision.Observe(model.Text.Trim(), frame, stop.Token);
            if (host != targetHost || targetHost!.Mailbox.Snapshot().SessionId != frame.Meta.SessionId) return;
            observation = result; visionFrame = frame;
            visionText.Text = $"帧 #{frame.Meta.Sequence} 的观察（非规划）：{result.Summary}\n英雄 {result.Hero} · 阵营 {result.Team} · 时钟 {result.Clock?.ToString() ?? "未知"}\n未知：{string.Join("；", result.Unknowns)}";
        }
        catch (Exception e) { visionText.Text = e is ArgumentException or InvalidOperationException ? e.Message : "本地视觉不可用/超时。没有付费请求，没有生成路线。"; }
        finally { observing = false; }
    }
    private void SendObservation()
    {
        try
        {
            if (host == null || observation == null || visionFrame == null) throw new InvalidOperationException();
            host.Mailbox.SetReminder(visionFrame.Meta.SessionId, visionFrame.Meta.Sequence,
                "local-vision-observation", observation.Summary, null);
            if (host.Mailbox.Poll(visionFrame.Meta.SessionId) is { } reminder) debug.Recorder?.Reminder("reminder-published", reminder);
            notice.Text = "已回传带“视觉观察”标识的实验字幕。";
        }
        catch { notice.Text = "本次观察已过期/换局，或尚无结果；没有回传。"; }
    }
}
