using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal static class Program
{
    [STAThread] public static void Main(string[] args)
    {
        // Unsupported switches (including the retired live practice UI) must never launch a window or a worker.
        if (args.Length != 0 && args is not ["--local-bot-e2e", _] && args is not ["--creep-hp-log", _, ..]) {
            Console.Error.WriteLine("不支持此游戏端启动参数。"); Environment.ExitCode = 2; return;
        }
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            MessageBox.Show("双机游戏端首版需要 Windows 11 x64。"); return;
        }
        // --creep-hp-log <file>: start the creep HP display at once and append its per-second rates (on-site measurement).
        // Lab only (scripts/lasthit/lab.ts --loop): [--lead S --latency S [--singles] [--patient]] looks ahead with that timing, [--cues PORT]
        // sends every number turning red to that local UDP port, [--frames DIR] saves pictures of the Dota window there (LabFrames).
        if (args is ["--creep-hp-log", var log, .. var lab]) {
            string? Option(string name) { int i = Array.IndexOf(lab, name); return i >= 0 && i + 1 < lab.Length ? lab[i + 1] : null; }
            var timing = Option("--lead") is { } lead && Option("--latency") is { } latency
                ? new LastHitTiming(double.Parse(lead, System.Globalization.CultureInfo.InvariantCulture), double.Parse(latency, System.Globalization.CultureInfo.InvariantCulture), lab.Contains("--singles"), Patient: lab.Contains("--patient")) : null;
            new Application().Run(new GameWindowUi(null) { CreepHpLog = log, CreepTiming = timing, CreepCuePort = Option("--cues") is { } port ? int.Parse(port) : null,
                CreepFrames = Option("--frames") });
            return;
        }
        new Application().Run(new GameWindowUi(E2ERun.Load(args)));
    }
}

internal sealed partial class GameWindowUi : Window
{
    private readonly PasswordBox pairing = new() { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), FontSize = 14 };
    private readonly Button connect;
    private enum LinkMode { Video, DiagnosticVideo, ParticipantGsi, Status, DiagnosticStatus, ParticipantWithLocalHp }
    private readonly ComboBox source = Ui.Choice();
    // No picture ever leaves this machine. Without the developer switch (UI tests) or the bot E2E harness there is
    // one connection: own GSI out; subtitles, enemy panel, wards and minute routes in; local creep HP independent.
    private readonly bool developerModes;
    private LinkMode Mode => !developerModes ? LinkMode.ParticipantWithLocalHp :
        source.SelectedIndex is >= 0 and <= (int)LinkMode.ParticipantWithLocalHp ? (LinkMode)source.SelectedIndex : LinkMode.ParticipantWithLocalHp;
    private static bool UsesParticipantLane(LinkMode mode) => mode is LinkMode.ParticipantGsi or LinkMode.ParticipantWithLocalHp;
    private static bool SendsVideo(LinkMode mode) => mode is LinkMode.Video or LinkMode.DiagnosticVideo;
    private readonly TextBlock modeNote = Ui.Text("", 12, Brushes.LightGray);
    private readonly AppPages pages;
    private readonly TextBlock status = Ui.Text("未连接 · 不采集画面", 16, Ui.Accent);
    private readonly TextBlock metrics = Ui.Text("", 12, Brushes.LightGray);
    private readonly ComboBox fpsChoice = Ui.Choice(), resolutionChoice = Ui.Choice(), encoderChoice = Ui.Choice(), captureChoice = Ui.Choice();
    private readonly TextBox bitrateChoice = Ui.Input();
    private readonly TextBlock videoNote = Ui.Text("", 12, Brushes.LightGray);
    private string videoHint = "";
    private readonly string videoSettingsPath;
    private GameVideoSettings videoSettings;
    private static readonly (string Value, string Label)[] ResolutionLabels = [
        ("native", "原生分辨率（游戏机不缩放，交给教练机）"), ("1440", "最高 1440p"), ("1080", "最高 1080p（推荐）"),
        ("900", "最高 900p"), ("720", "最高 720p")];
    private static readonly string GsiState = GsiSetup.StateDirectoryFor("Game");
    private readonly TextBox dotaInstall = Ui.Input(GsiSetup.FindInstall(GsiState));
    private readonly TextBlock caption = Ui.Text("教练提醒会显示在这里，游戏内同时显示悬浮提醒。", 18, Ui.Accent);
    // One block for own GSI: live state, player-mode minimap counts and the Steam launch-option check.
    private readonly TextBlock gsiStatus = Ui.Text("", 13);
    private string gsiText = "自身GSI：未启动（连接时自动启动）", minimapText = "", launchText = "";
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas marks = new() { IsHitTestVisible = false };
    private readonly Grid preview = new() { Background = Brushes.Black, MinHeight = 240 };
    private readonly AdviceOverlay overlay;
    private readonly OverlayPanel? overlayPanel;
    private readonly DebugPanel debug;
    private readonly DispatcherTimer reminderTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? active;
    private Task? running;
    private GsiReceiver? gsi;
    private bool closed, closing, busy;
    private long localSequence;
    private Reminder? shown;
    private GameLink? shownMinuteLink;
    private long reminderDeadline;
    private readonly E2ERun? e2e;
    private string? lastReceivedId;
    private bool exported;
    private bool receiverOmitsMinimap;
    private bool transportConnected;
    private long? lastCaptureAt;
    private readonly TextBlock creepHpStatus = Ui.Text("默认关闭。开启后由游戏机本地识别小兵血条、用显卡在 Dota 上画出血量数字；画面不编码、不上传、不经过教练机。", 12);
    private readonly Button creepHpButton;
    private readonly CreepTimingPanel? creepTiming;
    private IAsyncDisposable? creepHp;
    private bool creepHpBusy;
    private volatile bool participantConnection; // Both ticketed modes use only participant data in the link and its debug view.
    private bool gsiOnlyConnection; // The stricter local no-capture promise; also blocks a programmatic HP toggle.
    public string? CreepHpLog { get; init; }
    public LastHitTiming? CreepTiming { get; init; }
    public int? CreepCuePort { get; init; }
    public string? CreepFrames { get; init; }

    public GameWindowUi() : this(null) { }
    public GameWindowUi(E2ERun? run) : this(run, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot) : this(run, debugRoot, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot, string? videoSettingsFile) : this(run, debugRoot, videoSettingsFile, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot, string? videoSettingsFile, string? overlaySettingsFile)
    {
        e2e = run; debug = new DebugPanel("game", debugRoot); overlay = new AdviceOverlay(run, () => debug.Recorder);
        developerModes = Features.VideoUpload || run != null;
        overlay.MinuteRouteValid = MinuteRouteValid;
        // E2E evidence keeps the default subtitle style, whatever this machine's player chose.
        if (e2e == null) overlayPanel = new OverlayPanel(overlaySettingsFile ?? GameOverlaySettings.DefaultPath, overlay);
        videoSettingsPath = videoSettingsFile ?? GameVideoSettings.DefaultPath;
        videoSettings = GameVideoSettings.Load(videoSettingsPath);
        Title = "Dota Coach · 游戏端 v0.6.7";
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        MinWidth = 760; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        preview.MinHeight = 140;
        Background = Ui.Background; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        // Home: pairing, connection state, the latest reminder and the everyday switches. The rest is grouped settings.
        var home = new Grid();
        home.RowDefinitions.Add(new() { Height = GridLength.Auto });
        home.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        var top = new StackPanel();
        top.Children.Add(modeNote);
        top.Children.Add(Ui.Label("配对码（在教练端点“复制配对码”；含密钥，勿外传）"));
        connect = Ui.Button("连接", async (_, _) => { if (busy || active != null) await Stop(); else await Start(); });
        connect.MinWidth = 90; connect.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(Ui.Row(pairing, connect));
        top.Children.Add(status); top.Children.Add(metrics); top.Children.Add(gsiStatus);
        if (e2e == null) ShowLaunchOptions(); else ShowGsi();
        top.Children.Add(caption);
        creepHpButton = Ui.Button("显示小兵血量", async (_, _) => await ToggleCreepHp());
        if (e2e == null) {
            top.Children.Add(Ui.Text("小兵血量（只在本机识别，不上传）", 17, Ui.Accent));
            top.Children.Add(Ui.Row(creepHpStatus, creepHpButton));
            Loaded += async (_, _) => { if (CreepHpLog != null) await ToggleCreepHp(); };
        }
        if (overlayPanel != null) {
            top.Children.Add(Ui.Text("游戏里显示", 17, Ui.Accent));
            top.Children.Add(overlayPanel.Switches);
        }
        home.Children.Add(top);
        preview.Children.Add(image); preview.Children.Add(marks); Grid.SetRow(preview, 1); home.Children.Add(preview);
        var (developer, dota) = BuildSettings(home.RowDefinitions[1]);
        pages = new AppPages("游戏端", home);
        if (overlayPanel != null) pages.Add("游戏内显示", overlayPanel, "字幕、对手面板、眼位和兵线小地图。改动立即生效。");
        if (e2e == null) {
            var hp = new StackPanel();
            hp.Children.Add(Ui.Text("只给半血以下的兵标血量；你现在出手、这一刀落地时必能打死（正补、反补）时数字变红。塔下的兵需要先垫刀时，" +
                "血条右边的黄字是要垫几刀，垫完等塔打到变红再补。默认血条、无边框或窗口模式。Dota 画面只在本机显卡上识别，不传给教练端；显卡有空闲时每帧刷新，显卡被游戏占满时限 60 帧以免拖慢游戏；切出 Dota 时隐藏。", 12));
            hp.Children.Add(creepTiming = new CreepTimingPanel(CreepHpSettings.DefaultPath));
            pages.Add("小兵血量", hp, "在主页开关；这里调红字什么时候出现。");
            pages.Add("Dota 与 GSI", dota, "连接时自动写入本人 GSI 配置。");
        }
        if (developerModes) pages.Add("开发测试", developer, "只在 UI 测试和本地机器人局验收中出现；正式版没有画面模式。");
        pages.Add("调试", debug.View, "排查问题时才打开；记录只存本机。");
        Content = new Border { Child = pages, Margin = new Thickness(24) };
        preview.SizeChanged += (_, _) => DrawMark();
        reminderTimer.Tick += (_, _) => {
            if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
            if (shown != null && (Environment.TickCount64 >= reminderDeadline ||
                shown.Kind == MinuteFarmContext.Kind && !MinuteRouteValid(shown))) ClearReminder();
            try {
                if (debug.Recorder is { } recorder) {
                    var own = GameGsiSample.Read(gsi, participantOnly: participantConnection);
                    recorder.State(new(status.Text, transportConnected, false, false, false, 0, 0, 0,
                        lastCaptureAt is long at ? Environment.TickCount64 - at : null, own.Player, GsiAgeMs: own.AgeMs));
                }
            } catch { /* Debug must not affect the UI timer. */ }
            if (!participantConnection && gsi?.LastDiagnostics is { OwnStateAccepted: true } d) {
                string text = d.MinimapUnits > 0 ? $"小地图：{d.MinimapUnits} 个单位，其中敌方英雄 {d.EnemyHeroes} 个（只在本机统计）"
                    : "小地图：对局中没有收到";
                if (text != minimapText) { minimapText = text; ShowGsi(); }
            }
        };
        reminderTimer.Start();
        if (e2e != null) Loaded += async (_, _) => {
            try {
                for (int i = 0; i < 80 && !File.Exists(e2e.PairingPath); i++) await Task.Delay(250);
                pairing.Password = File.ReadAllText(e2e.PairingPath);
                await Start();
                await Task.Delay(TimeSpan.FromMinutes(10));
                if (!closing) Close();
            } catch (Exception error) { e2e.Trace("game", "startup-error", new { type = error.GetType().Name }); status.Text = "E2E启动失败"; }
        };
        Closing += async (_, e) => {
            if (closed) return;
            e.Cancel = true;
            if (closing) return;
            closing = true;
            overlayPanel?.Flush();
            creepTiming?.Flush();
            while (creepHpBusy) await Task.Delay(25);
            await StopCreepCapture();
            await Stop();
            if (gsi != null) { await gsi.DisposeAsync(); gsi = null; }
            closed = true; reminderTimer.Stop(); overlay.Close();
            await debug.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    // Strict mode doesn't parse the diagnostic minimap or retain a local scanner's callbacks. Keep the
    // same cfg/token when switching; the next local HP/ordinary connection again gets its own minimap.
    private async Task EnsureGsi(bool omitLocalMinimap, CancellationToken cancellation = default)
    {
        if (gsi != null && receiverOmitsMinimap == omitLocalMinimap) return;
        if (gsi != null) { var old = gsi; gsi = null; await old.DisposeAsync(); }
        cancellation.ThrowIfCancellationRequested();
        string? identity = e2e != null ? e2e.ResumeGsiToken() : GsiSetup.Token(GsiState);
        var receiver = await GsiReceiver.Start(cancellation: cancellation, resumeToken: identity,
            dotaInstall: e2e == null ? dotaInstall.Text.Trim() : null, diagnostics: !omitLocalMinimap);
        if (!omitLocalMinimap) {
            receiver.Diagnostics = d => { if (!participantConnection) debug.Recorder?.Gsi(d); };
            receiver.Snapshot = s => { if (!participantConnection) debug.Recorder?.PlayerMinimap(s); };
        }
        gsi = receiver; receiverOmitsMinimap = omitLocalMinimap;
    }
    private async Task ToggleCreepHp()
    {
        if (closing || creepHpBusy || gsiOnlyConnection || busy) return;
        creepHpBusy = true;
        creepHpButton.IsEnabled = false;
        try {
            if (creepHp != null) {
                await StopCreepCapture();
                creepHpButton.Content = "显示小兵血量"; creepHpStatus.Text = "已关闭。";
                return;
            }
            // Unit kinds come from the player's own GSI minimap: keep a receiver for the rest of this session.
            string gsiNote = "";
            try { await EnsureGsi(omitLocalMinimap: false); }
            catch { gsiNote = "自身GSI启动失败（3219端口被占用？），只能显示百分比。"; }
            if (gsi != null) {
                try {
                    if (GsiSetup.Install(dotaInstall.Text.Trim(), gsi.Config, GsiState).Changed)
                        gsiNote = "已写入自身GSI：带 -gamestateintegration 完全重启 Dota 一次后，才能区分近战/远程/攻城兵。";
                } catch { gsiNote = "自动写入GSI配置失败（检查「设置 → Dota 与 GSI」里的 Dota 目录），只能显示百分比。"; }
            }
            if (closing || gsiOnlyConnection) return;
            string? log = CreepHpLog;
            CreepHpDisplay? started = null;
            started = new CreepHpDisplay(s => {
                if (log != null) try { File.AppendAllText(log, System.Text.Json.JsonSerializer.Serialize(new { at = DateTimeOffset.Now, s.Status,
                    fps = Math.Round(s.Fps, 1), scanMs = Math.Round(s.ScanMs, 2), s.Bars, s.Known, s.Shown, s.GpuLoad, s.Limited, s.Kill }) + "\n"); } catch { }
                Dispatcher.BeginInvoke(() => {
                    if (creepHp != started) return;
                    string rate = s.Fps > 0 ? $"\n{s.Fps:F0} 帧/秒{(s.Limited ? $"（显卡繁忙，限 {GpuPacing.LimitedFps} 以免拖慢游戏）" : "")} · 识别 {s.ScanMs:F1} ms · " +
                        $"{s.Bars} 根血条，{s.Known} 根已认出兵种，{s.Shown} 根半血以下标了数字" : "";
                    creepHpStatus.Text = s.Status + rate + (s.Kill.Length > 0 ? "\n" + s.Kill : "") + (gsiNote.Length > 0 ? "\n" + gsiNote : "");
                });
            }, () => gsi?.LatestMinimap(), () => gsi?.LatestLoadout(), excludeFromCapture: log == null,
                // The lab's switches fix the timing; otherwise the panel's, read every frame.
                CreepTiming != null || CreepCuePort != null ? () => CreepTiming : creepTiming == null ? null : () => creepTiming.Current.Timing, CueSender(),
                CreepFrames is { } directory ? new LabFrames(directory) : null);
            creepHp = started;
            creepHpButton.Content = "关闭小兵血量";
        } finally { creepHpBusy = false; creepHpButton.IsEnabled = !closing && !gsiOnlyConnection; }
    }
    /// <summary>Lab only: each red number as a JSON datagram to <see cref="CreepCuePort"/> on this machine.</summary>
    private Action<CreepHpDisplay.Cue>? CueSender()
    {
        if (CreepCuePort is not int port) return null;
        var udp = new System.Net.Sockets.UdpClient();
        udp.Connect(System.Net.IPAddress.Loopback, port);
        return cue => { try { var data = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(cue); udp.Send(data, data.Length); } catch { } };
    }
    private async Task StopCreepCapture()
    {
        var previous = creepHp; creepHp = null;
        if (previous != null) await previous.DisposeAsync();
    }
    private async Task EnterMinutePrivacy()
    {
        // Local creep recognition is also a window capture, even though those pixels never leave the PC.
        // Close the start race and wait for disposal BEFORE starting the metadata-only session.
        gsiOnlyConnection = true; creepHpButton.IsEnabled = false;
        overlay.Enemies(null);
        while (creepHpBusy) await Task.Delay(20);
        await StopCreepCapture();
        creepHpButton.Content = "显示小兵血量";
        creepHpStatus.Text = "仅本人 GSI 连接：已关闭本机屏幕识别，断开后才能重新开启。";
    }
    // Called after the operator confirms the locked selection, before binding any participant session.
    private async Task PrepareCaptureMode(CancellationToken cancel)
    {
        participantConnection = UsesParticipantLane(Mode);
        if (participantConnection) { minimapText = ""; ShowGsi(); }
        if (Mode == LinkMode.ParticipantGsi) await EnterMinutePrivacy();
        else while (creepHpBusy) await Task.Delay(20, cancel); // Share the receiver; leave the independently enabled HP worker alone.
    }
    private async Task Start()
    {
        if (busy || active != null || closing) return;
        busy = true;
        CancellationTokenSource? starting = null;
        try
        {
            var pair = Pairing.Decode(pairing.Password.Trim());
            var mode = Mode;
            bool diagnostic = mode is LinkMode.DiagnosticVideo or LinkMode.DiagnosticStatus;
            bool participant = UsesParticipantLane(mode);
            bool strict = mode == LinkMode.ParticipantGsi;
            var chosen = SendsVideo(mode) ? ReadVideoSettings() : null;
            if (participant && MessageBox.Show(this,
                "把自己的对局状态和游戏窗口尺寸传到配对的教练端？\n\n" +
                (strict ? "不采集、编码或传送游戏画面，已开启的小兵血量识别也会停止。" :
                    "不传游戏画面。连接不会开关小兵血量：它只在本机显卡上识别，随时可在「小兵血量」里开关。") +
                "游戏里显示教练端推送的字幕、对手装备面板和眼位。教练端开启分钟路线时可能调用已配置的云模型并计费。不会控制英雄。",
                strict ? "确认仅传本人 GSI" : "确认连接教练端", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (chosen != null && !diagnostic && e2e == null && MessageBox.Show(this,
                "开始传送 Dota 2 游戏画面并启用自动教练？\n\n画面含主视图、小地图、HUD，也可能含游戏内聊天。\n" +
                (chosen.Capture == "window"
                    ? "窗口采集：切到其他窗口时仍持续传送 Dota 窗口自身的画面；其他应用的内容不会被采集。\n"
                    : "前台模式：只在 Dota 位于前台时采集屏幕区域，可能含系统通知。\n") +
                "教练端默认将真实画面＋自身GSI发送到指定云模型，会计费。\n不会传音频或控制英雄；可随时停止。",
                "确认游戏画面传输", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No) != MessageBoxResult.Yes) return;
            if (closing) return;
            if (chosen != null) {
                try { chosen.Save(videoSettingsPath); } catch { /* Settings still apply to this run. */ }
                videoSettings = chosen;
            }
            starting = new CancellationTokenSource(); active = starting;
            SetIdle(false);
            await PrepareCaptureMode(starting.Token);
            starting.Token.ThrowIfCancellationRequested();
            if (!diagnostic) {
                try {
                    await EnsureGsi(omitLocalMinimap: strict, starting.Token);
                    ShowGsi("自身GSI：本机端口 3219 已启动");
                }
                catch (OperationCanceledException) { throw; }
                catch { ShowGsi("自身GSI启动失败：检查3219端口及本地凭据存储权限。" +
                    (participant ? "等待本人的新鲜对局状态；不显示装备、眼位或分钟路线。" :
                    chosen != null ? "视频仍可传，但没有自身状态时不调用AI。" : "只能保持状态连接。")); }
            }
            if (e2e == null && !diagnostic && gsi != null) {
                try {
                    gsi.UseDotaInstall(dotaInstall.Text.Trim());
                    bool changed = GsiSetup.Install(dotaInstall.Text.Trim(), gsi.Config, GsiState).Changed;
                    gsiText = changed ? "自身GSI配置已写入/更新：请完全退出并重启 Dota 一次。" : "自身GSI配置已就绪，等待对局数据";
                } catch { gsiText = "自动写入GSI配置失败（目录/权限，或同名文件被改过；不会覆盖），请检查「设置 → Dota 与 GSI」里的 Dota 目录。"; }
                ShowLaunchOptions();
            }
            if (e2e != null && !exported) {
                if (gsi == null) throw new InvalidOperationException("Local bot E2E requires GSI");
                e2e.ExportGsi(gsi.Config); exported = true;
                e2e.Trace("game", "ready", new { gsiPort = gsi.Port, localBotOnly = true, captureOverlayEvidence = e2e.CaptureOverlayEvidence });
            }
            starting.Token.ThrowIfCancellationRequested();
            var cancellation = starting.Token;
            running = participant ? RunMinuteState(pair, cancellation) : chosen != null ? Run(pair, diagnostic, chosen, cancellation) :
                RunStatus(pair, diagnostic, cancellation);
            _ = FinishRun(running);
        }
        catch (OperationCanceledException) { status.Text = "启动已取消 · 不采集画面"; }
        catch (Exception e) { debug.Recorder?.Error("game-start", e); status.Text = e is ArgumentException ? e.Message : "启动失败，请检查配对码。"; }
        finally
        {
            busy = false;
            if (running == null && starting != null && active == starting) {
                active.Dispose(); active = null; SetIdle(true);
            }
        }
    }
    private async Task FinishRun(Task task)
    {
        try { await task; } catch { }
        if (running != task) return;
        active?.Dispose(); active = null; running = null;
        SetIdle(true); ClearReminder();
    }
    /// <summary>The developer group (connection modes and video) and the Dota folder group.</summary>
    private (StackPanel Developer, StackPanel Dota) BuildSettings(RowDefinition previewRow)
    {
        var panel = new StackPanel();
        source.Items.Add("Dota 2 游戏视频（采集并传送画面）");
        source.Items.Add("诊断视频（合成画面，不采集桌面）");
        source.Items.Add("仅本人 GSI（严格不采集画面；连接时停用本机小兵血量）");
        source.Items.Add("基础状态（只传 GSI，允许手动开启本机小兵血量识别）");
        source.Items.Add("诊断状态（零画面，无需 Dota）");
        source.Items.Add("本人 GSI＋可选本机小兵血量（可接收装备、眼位和分钟路线；不上传画面）");
        // The selector, and with it every picture mode, exists only for UI tests and the bot E2E harness.
        if (developerModes) { panel.Children.Add(Ui.Label("连接模式 / 画面来源（开发测试）")); panel.Children.Add(source); }
        var videoSlot = new ContentControl(); panel.Children.Add(videoSlot);
        void UpdateMode() {
            var mode = Mode;
            bool video = SendsVideo(mode);
            modeNote.Text = mode switch {
                LinkMode.Video => "将 Dota 画面和自身 GSI 加密传给教练端；画面可能含游戏内聊天。连接前需确认。",
                LinkMode.DiagnosticVideo => "仅向教练端发送合成诊断视频，不传 Dota 画面或真实 GSI。",
                LinkMode.Status => "只传自身 GSI 与窗口尺寸，接收普通字幕，不传视频、不启用分钟模型。本机小兵血量可单独开启。",
                LinkMode.ParticipantWithLocalHp => "把自身 GSI 和游戏窗口尺寸加密传给教练端，不传画面。游戏里显示教练端推送的字幕、对手装备面板、眼位和分钟路线；" +
                    "小兵血量只在本机识别，随时可开关。不读内存、不控制英雄。",
                LinkMode.DiagnosticStatus => "只发诊断心跳以测试连接；无需 Dota，不传视频或真实 GSI。",
                _ => "仅本人 GSI：连接后不采集或传送画面，不启用本机血量识别。可接收本局授权的装备、眼位和分钟路线。",
            };
            // No encoder enumeration, empty black preview or video controls in either metadata-only mode.
            if (video && videoSlot.Content == null) videoSlot.Content = BuildVideoSettings();
            videoSlot.Visibility = preview.Visibility = video ? Visibility.Visible : Visibility.Collapsed;
            previewRow.Height = video ? new GridLength(1, GridUnitType.Star) : GridLength.Auto;
            metrics.Visibility = video || UsesParticipantLane(mode) ? Visibility.Visible : Visibility.Collapsed;
        }
        source.SelectionChanged += (_, _) => UpdateMode();
        source.SelectedIndex = (int)(e2e == null ? LinkMode.ParticipantWithLocalHp : LinkMode.Video);
        source.IsEnabled = developerModes && e2e == null;
        UpdateMode(); // Also when the selector is absent and SelectionChanged never fires.
        var dota = new StackPanel();
        if (e2e == null) {
            dota.Children.Add(Ui.Label("Dota 安装目录（连接时自动写入自身GSI配置）"));
            dota.Children.Add(dotaInstall);
            dota.Children.Add(Ui.Button("复制启动参数", (_, _) => CopyLaunchOptions()));
        }
        return (panel, dota);
    }
    private StackPanel BuildVideoSettings()
    {
        foreach (int rate in VideoWire.FrameRates) fpsChoice.Items.Add($"{rate} fps");
        foreach (var (_, label) in ResolutionLabels) resolutionChoice.Items.Add(label);
        encoderChoice.Items.Add("自动（优先显卡硬件编码）"); encoderChoice.Items.Add("CPU 软件编码");
        captureChoice.Items.Add("窗口采集（切到其他窗口也继续传 Dota 画面）"); captureChoice.Items.Add("前台屏幕复制（旧版，切出即暂停）");
        fpsChoice.SelectedIndex = Array.IndexOf(VideoWire.FrameRates, videoSettings.Fps);
        resolutionChoice.SelectedIndex = Array.FindIndex(ResolutionLabels, r => r.Value == videoSettings.Resolution);
        encoderChoice.SelectedIndex = videoSettings.Encoder == "software" ? 1 : 0;
        captureChoice.SelectedIndex = videoSettings.Capture == "foreground" ? 1 : 0;
        bitrateChoice.Text = videoSettings.BitrateMbps.ToString();
        var grid = new Grid();
        for (int i = 0; i < 4; i++) grid.ColumnDefinitions.Add(new() { Width = new GridLength(i == 1 ? 2 : 1, GridUnitType.Star) });
        void Cell(int column, string label, Control control) {
            var cell = new StackPanel { Margin = new Thickness(0, 0, 8, 0) };
            cell.Children.Add(Ui.Text(label, 12, Brushes.LightGray)); cell.Children.Add(control);
            Grid.SetColumn(cell, column); grid.Children.Add(cell);
        }
        Cell(0, "帧率", fpsChoice); Cell(1, "传输分辨率", resolutionChoice); Cell(2, "编码器", encoderChoice); Cell(3, "码率 Mbps（0=自动）", bitrateChoice);
        var panel = new StackPanel();
        panel.Children.Add(grid);
        panel.Children.Add(Ui.Text("采集方式", 12, Brushes.LightGray)); panel.Children.Add(captureChoice);
        var gpuEncoders = HardwareH264.Available();
        videoHint = (gpuEncoders.Count > 0 ? "显卡编码器：" + string.Join(" / ", gpuEncoders.Distinct()) : "未发现显卡 H.264 编码器，改用 CPU 编码（最高 1080p）") +
            "。Dota 请用“无边框窗口”：最小化时没有画面可传。";
        videoNote.Text = videoHint;
        panel.Children.Add(videoNote);
        return panel;
    }
    /// <summary>Settings are locked while a session runs; the single connect button flips to disconnect.</summary>
    private void SetIdle(bool idle)
    {
        overlay.SetBoardSession(!idle);
        if (idle) participantConnection = false;
        if (idle && gsiOnlyConnection) { gsiOnlyConnection = false; creepHpStatus.Text = "已关闭。"; }
        creepHpButton.IsEnabled = !closing && !creepHpBusy && !gsiOnlyConnection;
        foreach (var control in new Control[] { pairing, fpsChoice, resolutionChoice, encoderChoice, captureChoice, bitrateChoice })
            control.IsEnabled = idle;
        source.IsEnabled = idle && developerModes && e2e == null;
        connect.Content = idle ? "连接" : "断开";
    }
    private void ShowGsi(string? text = null)
    {
        if (text != null) gsiText = text;
        gsiStatus.Text = string.Join("\n", new[] { gsiText, minimapText, launchText }.Where(t => t.Length > 0));
    }
    private GameVideoSettings ReadVideoSettings()
    {
        if (!int.TryParse(bitrateChoice.Text.Trim(), out int mbps)) throw new ArgumentException("码率请填整数 Mbps（0 = 自动）。");
        var chosen = new GameVideoSettings {
            Fps = VideoWire.FrameRates[Math.Max(0, fpsChoice.SelectedIndex)],
            Resolution = ResolutionLabels[Math.Max(0, resolutionChoice.SelectedIndex)].Value,
            Encoder = encoderChoice.SelectedIndex == 1 ? "software" : "auto",
            Capture = captureChoice.SelectedIndex == 1 ? "foreground" : "window",
            BitrateMbps = mbps,
        };
        chosen.Validate();
        return chosen;
    }
    private IVideoSource OpenWindowSource(GameSurface surface, (int Width, int Height) size, int fps, Action<string> note)
    {
        try { return new WindowVideoSource(surface, size.Width, size.Height, fps); }
        catch (Exception e) {
            note("窗口采集不可用（" + e.GetType().Name + "），本次改用前台屏幕复制：切出 Dota 会暂停。");
            return new ForegroundVideoSource(surface, size.Width, size.Height);
        }
    }
    private async Task Run(Pairing pair, bool diagnostic, GameVideoSettings settings, CancellationToken cancellation)
    {
        // Preserve the explicitly opted-in historical bot/pixel-evidence harness, not as a live fallback.
        if (e2e != null) { await RunLegacyEvidence(pair, diagnostic, cancellation); return; }
        using var link = new GameLink(pair);
        bool windowMode = settings.Capture == "window";
        bool gpuEncoder = settings.Encoder == "auto" && HardwareH264.Available().Count > 0;
        string? captureNote = null;
        try {
            while (!cancellation.IsCancellationRequested) {
                GameSurface? surface = null;
                if (!diagnostic) {
                    bool minimized = false;
                    surface = windowMode ? GameWindow.Find(out minimized) : GameWindow.Foreground();
                    if (surface == null) {
                        status.Text = !windowMode ? "已暂停视频：请切回 Dota 2（前台模式不采集其他应用）" :
                            minimized ? "Dota 已最小化，没有画面可传。独占全屏切出时会最小化：建议在 Dota 视频设置中改为“无边框窗口”。" :
                            "等待 Dota 2 窗口…";
                        await Task.Delay(250, cancellation); continue;
                    }
                }
                var size = diagnostic ? (Width: 1280, Height: 720) : settings.EncodedSize(surface!.Width, surface.Height, gpuEncoder);
                int fps = GameVideoSettings.EffectiveFps(size.Width, size.Height, settings.Fps);
                int bitrate = settings.Bitrate(size.Width, size.Height, fps);
                await link.Connect(cancellation);
                if (!diagnostic) link.BindPostgame(gsi, "dota2", () => Interlocked.Increment(ref localSequence));
                await link.OpenVideo(cancellation);
                var lease = await link.GetVideoTicket(cancellation);
                transportConnected = true;
                var stream = new VideoSender(lease, fps);
                using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(streamStop.Token);
                var endTask = diagnostic ? Task.CompletedTask : link.DeliverPostgames(streamStop.Token);
                Func<IVideoSource> open = diagnostic ? () => new DiagnosticVideoSource() :
                    windowMode ? () => OpenWindowSource(surface!, size, fps, n => captureNote = n) :
                    () => new ForegroundVideoSource(surface!, size.Width, size.Height);
                var captureTask = stream.StartCapture(open, diagnostic,
                    () => Interlocked.Increment(ref localSequence), () => GameGsiSample.ReadForUpload(gsi, participantOnly: false), captureStop.Token);
                var sendTask = Task.Run(() => stream.SendLoop(link, gpuEncoder, bitrate, streamStop.Token));
                var boardTask = diagnostic ? Task.CompletedTask : PollBoards(link, streamStop.Token);
                long renewAt = Environment.TickCount64 + 750, previewAt = 0, rateAt = Environment.TickCount64, rateSent = 0, rateBytes = 0;
                double actualFps = 0, actualMbps = 0;
                bool background = false;
                try {
                    while (!cancellation.IsCancellationRequested) {
                        if (captureTask.IsCompleted) { await captureTask; break; }
                        if (sendTask.IsCompleted) {
                            // A failing GPU encoder ends only this session; the next one uses the CPU encoder.
                            try { await sendTask; } catch when (stream.HardwareFailed && !cancellation.IsCancellationRequested) { }
                            break;
                        }
                        if (!diagnostic) {
                            if (windowMode) {
                                // Window capture keeps streaming behind other windows; only a new/resized/closed window restarts it.
                                var current = GameWindow.Find(out _);
                                if (current == null || current.Handle != surface!.Handle ||
                                    current.Width != surface.Width || current.Height != surface.Height) break;
                                background = !GameWindow.IsForeground(current);
                            } else if (GameWindow.Foreground() != surface) break;
                        }
                        if (Environment.TickCount64 >= renewAt) {
                            stream.Renew(await link.GetVideoTicket(cancellation));
                            renewAt = Environment.TickCount64 + 750;
                        }
                        if (stream.Latest is { } frame) {
                            // Only a NEW picture proves the game is still being captured.
                            if (lastCaptureAt != frame.CapturedAt) overlay.FrameArrived(frame.Meta.Player?.MatchId == "0");
                            lastCaptureAt = frame.CapturedAt;
                            if (Environment.TickCount64 >= previewAt) {
                                if (Environment.TickCount64 - rateAt >= 1000) {
                                    double seconds = (Environment.TickCount64 - rateAt) / 1000.0;
                                    actualFps = (stream.Sent - rateSent) / seconds; actualMbps = (stream.Bytes - rateBytes) * 8 / seconds / 1_000_000;
                                    rateAt = Environment.TickCount64; rateSent = stream.Sent; rateBytes = stream.Bytes;
                                }
                                image.Source = Ui.PreviewImage(frame.Pixels, 640, 360); DrawMark();
                                metrics.Text = $"{frame.Meta.SourceWidth}×{frame.Meta.SourceHeight} → {frame.Pixels.Width}×{frame.Pixels.Height} · " +
                                    $"目标 {fps}fps / {bitrate / 1_000_000.0:F0}Mbps" + (fps < settings.Fps ? $"（{settings.Fps}fps 超出H.264上限，已降）" : "") +
                                    $" · 实际 {actualFps:F1}fps / {actualMbps:F1}Mbps · 丢帧 {stream.Dropped}\n" +
                                    $"{stream.SourceName} · {stream.EncoderName} · 采集 {stream.CaptureMs:F1}ms · 编码 {stream.EncodeMs:F1}ms";
                                status.Text = diagnostic ? "已连接 · 持续发送诊断视频（非游戏）" :
                                    background ? "已连接 · Dota 在后台，继续传送 Dota 窗口画面" : "已连接 · 持续发送 Dota 2 视频";
                                videoNote.Text = string.Join("\n", new[] { captureNote, stream.EncoderNote, videoHint }.Where(n => n != null));
                                ShowGsi(frame.Meta.Player is { } p ? $"自身GSI：{p.Team} · {p.Hero} · {p.Clock:F1}s" : "自身GSI：暂无新鲜数据（视频继续，AI不推测装备/阵营）");
                                debug.Recorder?.Video(new(stream.SourceName, stream.EncoderName, frame.Meta.SourceWidth, frame.Meta.SourceHeight,
                                    frame.Pixels.Width, frame.Pixels.Height, fps, Math.Round(actualFps, 1), Math.Round(actualMbps, 2), stream.Dropped,
                                    Math.Round(stream.CaptureMs, 2), Math.Round(stream.EncodeMs, 2), background, captureNote ?? stream.EncoderNote));
                                previewAt = Environment.TickCount64 + 250;
                            }
                        }
                        ApplyReminder(await link.Poll(cancellation), diagnostic, link);
                        await Task.Delay(100, cancellation);
                    }
                } finally {
                    captureStop.Cancel(); // No new capture during the metadata-only viewport-loss grace.
                    if (!diagnostic) await link.DrainVideoPostgame(cancellation);
                    streamStop.Cancel(); link.AbortVideo();
                    ClearReminder(); transportConnected = false;
                    try { await Task.WhenAll(captureTask, sendTask, boardTask, endTask); } catch when (streamStop.IsCancellationRequested) { }
                    overlay.Enemies(null); image.Source = null; lastCaptureAt = null;
                    using var timeout = new CancellationTokenSource(1500);
                    try { await link.End(timeout.Token); } catch { }
                    if (stream.HardwareFailed) gpuEncoder = false; // Later sessions use the CPU limits, not a retry loop.
                }
                if (stream.Starved) status.Text = "Dota 窗口暂时没有新画面（最小化或卡住）；恢复后自动继续。";
            }
        } catch (Exception error) {
            debug.Recorder?.Error("video-transport", error);
            status.Text = LinkErrors.Message(error, cancellation.IsCancellationRequested);
        } finally {
            transportConnected = false; lastCaptureAt = null; ClearReminder(); image.Source = null;
            using var timeout = new CancellationTokenSource(1500);
            try { await link.End(timeout.Token); } catch { }
        }
    }
    /// <summary>
    /// The coach's enemy panel, polled beside the main transport: a slow reply or missing icons never stall GSI uploads,
    /// video leases or the route poll. Each reply stays on screen until the next one replaces it; a failed poll keeps the
    /// last panel (the main transport notices a lost coach and ends the session), only a long silence withdraws it.
    /// </summary>
    private async Task PollBoards(GameLink link, CancellationToken cancel)
    {
        const int KeepMs = 15_000;
        long answeredAt = Environment.TickCount64;
        overlay.BoardForDisplay = (board, w, h) => link.DisplayEnemies(board, gsi?.LatestMatch(), w, h);
        try {
            while (!cancel.IsCancellationRequested) {
                try {
                    var board = overlay.Settings.ShowEnemies || overlay.Settings.ShowWards || overlay.Settings.ShowLaneWaves || overlay.Settings.ShowFog
                        ? await link.Enemies(cancel) : null;
                    answeredAt = Environment.TickCount64;
                    // Only this machine's own GSI can veto a panel: one positively naming another match is never drawn.
                    if (board != null && gsi?.LatestMatch() is { } local && local.MatchId != board.MatchId) board = null;
                    overlay.Enemies(board);
                    if (board != null && (overlay.Settings.ShowEnemies || overlay.Settings.ShowFog)) {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        budget.CancelAfter(500);
                        try {
                            foreach (string key in overlay.Icons.Missing(board, Environment.TickCount64).Take(8))
                                overlay.Icons.Add(key, await link.Icon(key, budget.Token), Environment.TickCount64);
                        } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { /* Retry icons on a later poll. */ }
                    }
                } catch (Exception ex) when (!cancel.IsCancellationRequested && ex is System.Net.Http.HttpRequestException or
                                               OperationCanceledException or System.IO.IOException or InvalidOperationException) {
                    if (Environment.TickCount64 - answeredAt >= KeepMs) overlay.Enemies(null);
                }
                await Task.Delay(750, cancel);
            }
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { overlay.BoardForDisplay = null; overlay.Enemies(null); }
    }
    private bool MinuteRouteValid(Reminder r)
    {
        if (!transportConnected || active?.IsCancellationRequested != false || shownMinuteLink == null) return false;
        var own = GameGsiSample.ReadForUpload(gsi, participantOnly: participantConnection);
        return shownMinuteLink.CanRenderMinuteRoute(r, own.Player, own.AgeMs);
    }
    private void ApplyReminder(Reminder? reminder, bool diagnostic, GameLink? link = null)
    {
        shownMinuteLink = reminder?.Kind == MinuteFarmContext.Kind && !diagnostic ? link : null;
        if (reminder == null || reminder.Kind == MinuteFarmContext.Kind && !MinuteRouteValid(reminder)) { ClearReminder(); return; }
        long deadline = Environment.TickCount64 + reminder.RemainingMs;
        reminderDeadline = shown?.Id == reminder.Id ? Math.Min(reminderDeadline, deadline) : deadline;
        shown = reminder;
        caption.Text = !reminder.ShowSubtitle ? "地图路线提示（字幕已关闭）" :
            AdviceOverlay.Label(reminder) + "： " + reminder.Message +
            (reminder.NextSteps is { Length: > 0 } steps ? "\n下一步：" + string.Join(" → ", steps) : "") +
            (overlay.Settings.Shows(reminder.Kind) ? "" : "\n（按本机“游戏内字幕”设置，这类字幕不在游戏里显示）");
        if (lastReceivedId != reminder.Id) {
            debug.Recorder?.Reminder("reminder-received", reminder); lastReceivedId = reminder.Id;
        }
        if (!diagnostic) overlay.Accept(reminder);
        DrawMark();
    }
    private async Task RunLegacyEvidence(Pairing pair, bool diagnostic, CancellationToken cancellation)
    {
        using var link = new GameLink(pair);
        long nextCapture = 0;
        FrameMeta? lastFrame = null;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var surface = diagnostic ? null : GameWindow.Foreground();
                if (!diagnostic && (surface == null || e2e != null && gsi?.Latest().Player is not { MatchId: "0" }))
                {
                    ClearReminder(); image.Source = null;
                    if (link.SessionId != null) await link.End(cancellation);
                    transportConnected = false; lastCaptureAt = null;
                    status.Text = e2e != null ? "本地验收：等待前台Dota及本地对局GSI（其他对局不采集）" :
                        "已暂停画面传输：请切回 Dota 2（其他应用不采集）";
                    await Task.Delay(500, cancellation); continue;
                }
                if (link.SessionId == null) await link.Connect(cancellation);
                transportConnected = link.SessionId != null;
                if (Environment.TickCount64 >= nextCapture)
                {
                    var ticket = await link.GetTicket(cancellation);
                    long started = Environment.TickCount64, seq = ++localSequence;
                    var visibleReminder = overlay.IsVisible ? shown : null;
                    var capture = await Task.Run(() => diagnostic ? Capture.Diagnostic(seq) :
                        Capture.Game(surface!, e2e?.CaptureOverlayEvidence == true), cancellation);
                    if (capture == null) { ClearReminder(); await link.End(cancellation); transportConnected = false; lastCaptureAt = null; await Task.Delay(500, cancellation); continue; }
                    var own = diagnostic ? default : GameGsiSample.ReadForUpload(gsi, participantOnly: false);
                    if (!diagnostic && e2e != null && own.Player is not { MatchId: "0" }) {
                        // The bot match can end while the capture was in flight. Never export those pixels.
                        ClearReminder(); image.Source = null; await link.End(cancellation); transportConnected = false; lastCaptureAt = null; continue;
                    }
                    var meta = new FrameMeta(link.SessionId!, ticket.Id, seq, diagnostic ? "diagnostic" : "dota2",
                        capture.Width, capture.Height, own.Player, own.AgeMs, own.Phase);
                    lastFrame = meta;
                    lastCaptureAt = started;
                    debug.Recorder?.Frame("frame-captured", meta, capture.Jpeg, Environment.TickCount64 - started);
                    await link.Upload(meta, capture.Jpeg, started, cancellation);
                    debug.Recorder?.Transfer(meta, Environment.TickCount64 - started);
                    if (!diagnostic && GameWindow.Foreground() != surface) { ClearReminder(); await link.End(cancellation); transportConnected = false; lastCaptureAt = null; continue; }
                    image.Source = Ui.Image(capture.Jpeg); overlay.FrameArrived(own.Player?.MatchId == "0");
                    if (e2e?.CaptureOverlayEvidence == true && own.Player?.MatchId == "0" &&
                        visibleReminder != null && shown?.Id == visibleReminder.Id) {
                        // The actual transmitted screen capture, not a recreated/mock overlay render.
                        string artifact = $"overlay-screen-{visibleReminder.Id}.jpg";
                        string destination = System.IO.Path.Combine(e2e.Directory, artifact);
                        if (!File.Exists(destination)) {
                            File.WriteAllBytes(destination, capture.Jpeg);
                            e2e.Trace("game", "overlay-screen-captured", new { id = visibleReminder.Id, frame = seq, artifact });
                        }
                    }
                    if (e2e != null && (seq == 1 || seq % 10 == 0))
                        e2e.Trace("game", "frame-uploaded", new { frame = seq, source = meta.Source,
                            capture.Width, capture.Height, own.Player, elapsedMs = Environment.TickCount64 - started });
                    status.Text = diagnostic ? "已连接 · 正在发送明确标记的诊断画面" : "已连接 · 仅发送 Dota 2 前台画面";
                    var encoded = Jpeg.Dimensions(capture.Jpeg);
                    metrics.Text = $"原始 {capture.Width}×{capture.Height} → JPEG {encoded.Width}×{encoded.Height} · #{seq} · {capture.Jpeg.Length / 1024.0:F0} KiB\n采集+上传 {Environment.TickCount64 - started} ms（不是完整教练延迟）";
                    if (own.Player is { } p) ShowGsi($"自身 GSI：{p.Team} · {p.Hero} · 游戏 {p.Clock:F1}s · 观测年龄 {own.AgeMs}ms");
                    else if (!diagnostic) ShowGsi("当前无新鲜自身 GSI；不推测装备/阵营，不使用旁观者全图数据。");
                    nextCapture = Environment.TickCount64 + 1000;
                }
                var reminder = await link.Poll(cancellation);
                if (reminder == null) ClearReminder();
                else
                {
                    long deadline = Environment.TickCount64 + reminder.RemainingMs;
                    reminderDeadline = shown?.Id == reminder.Id ? Math.Min(reminderDeadline, deadline) : deadline;
                    shown = reminder;
                    caption.Text = !reminder.ShowSubtitle ? "地图目标提示（字幕已关闭）" :
                        (reminder.Kind == "manual-link-test" ? "链路测试（非AI）： " :
                        reminder.Kind is "experimental-bot-advice" or "visible-farming-advice" ? "AI打钱建议（非最优保证）： " : "视觉观察（未核验）： ") + reminder.Message;
                    if (lastReceivedId != reminder.Id) {
                        e2e?.Trace("game", "reminder-received", new { reminder });
                        debug.Recorder?.Reminder("reminder-received", reminder);
                        lastReceivedId = reminder.Id;
                    }
                    if (!diagnostic) overlay.Accept(reminder);
                    DrawMark();
                }
                await Task.Delay(250, cancellation);
            }
        }
        catch (Exception error) {
            debug.Recorder?.Error("game-transport", error, lastFrame);
            e2e?.Trace("game", "transport-ended", new { type = error.GetType().Name, cancelled = cancellation.IsCancellationRequested });
            status.Text = LinkErrors.Message(error, cancellation.IsCancellationRequested);
        }
        finally
        {
            transportConnected = false; lastCaptureAt = null;
            ClearReminder(); image.Source = null;
            using var timeout = new CancellationTokenSource(1500);
            try { await link.End(timeout.Token); } catch { }
        }
    }
    private void DrawMark()
    {
        marks.Children.Clear();
        if (shown is { Kind: MinuteFarmContext.Kind } minute && !MinuteRouteValid(minute)) { ClearReminder(); return; }
        if (shown?.Marker is not { } point || image.Source == null) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, shown.SourceWidth, shown.SourceHeight);
        foreach (var p in shown.Route ?? [point]) {
            var ring = new Ellipse { Width = 24, Height = 24, Stroke = Brushes.Gold, StrokeThickness = 3 };
            Canvas.SetLeft(ring, rect.Left + p.X * rect.Width - 12); Canvas.SetTop(ring, rect.Top + p.Y * rect.Height - 12);
            marks.Children.Add(ring);
        }
    }
    private void ClearReminder()
    {
        if (shown != null) debug.Recorder?.Reminder("reminder-cleared", shown);
        shown = null; shownMinuteLink = null; overlay.Clear(); marks.Children.Clear();
        caption.Text = "无有效提醒（断线、过期或阵亡时自动清除）";
    }
    private async Task Stop()
    {
        active?.Cancel(); overlay.Enemies(null); ClearReminder();
        while (busy) await Task.Delay(25);
        var task = running; if (task != null) try { await task; } catch { }
        status.Text = SendsVideo(Mode) ? "已停止 · 不再采集或传输画面" : "已停止 · 已断开教练端";
        metrics.Text = "";
    }
    private void ShowLaunchOptions() { launchText = Ui.LaunchOptionsText(SteamLaunch.Inspect()); ShowGsi(); }
    private void CopyLaunchOptions() { launchText = Ui.CopyLaunchOptions(SteamLaunch.Inspect()); ShowGsi(); }
}
