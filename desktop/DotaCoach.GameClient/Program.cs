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
        if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) {
            MessageBox.Show("双机游戏端首版需要 Windows 11 x64。"); return;
        }
        // --creep-hp-log <file>: start the creep HP display at once and append its per-second rates (on-site measurement).
        if (args is ["--creep-hp-log", var log]) { new Application().Run(new GameWindowUi(null) { CreepHpLog = log }); return; }
        new Application().Run(new GameWindowUi(E2ERun.Load(args)));
    }
}

internal sealed partial class GameWindowUi : Window
{
    private readonly PasswordBox pairing = new() { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), FontSize = 14 };
    private readonly Button connect;
    private readonly ComboBox source = Ui.Choice();
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
    private bool persistentGsi;
    private bool transportConnected;
    private long? lastCaptureAt;
    private readonly TextBlock practiceStatus = Ui.Text("仅限官方露娜试玩，默认关闭。不用配对、不上传、不控制英雄。", 12);
    private readonly ComboBox practiceDistance = Ui.Choice();
    private readonly Button practiceButton;
    private LastHitPractice? practice;
    private bool practiceBusy, practiceOwnsGsi;
    private readonly TextBlock creepHpStatus = Ui.Text("默认关闭。开启后由游戏机本地识别小兵血条、用显卡在 Dota 上画出血量数字；画面不编码、不上传、不经过教练机。", 12);
    private readonly Button creepHpButton;
    private IAsyncDisposable? creepHp;
    private bool creepHpBusy;
    private volatile bool gsiOnlyConnection;
    public string? CreepHpLog { get; init; }

    public GameWindowUi() : this(null) { }
    public GameWindowUi(E2ERun? run) : this(run, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot) : this(run, debugRoot, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot, string? videoSettingsFile) : this(run, debugRoot, videoSettingsFile, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot, string? videoSettingsFile, string? overlaySettingsFile)
    {
        e2e = run; debug = new DebugPanel("game", debugRoot); overlay = new AdviceOverlay(run, () => debug.Recorder);
        overlay.MinuteRouteValid = MinuteRouteValid;
        // E2E evidence keeps the default subtitle style, whatever this machine's player chose.
        if (e2e == null) overlayPanel = new OverlayPanel(overlaySettingsFile ?? GameOverlaySettings.DefaultPath, overlay);
        videoSettingsPath = videoSettingsFile ?? GameVideoSettings.DefaultPath;
        videoSettings = GameVideoSettings.Load(videoSettingsPath);
        Title = "Dota Coach · 游戏端 v0.6.1";
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        MinWidth = 760; MinHeight = 600; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        preview.MinHeight = 140;
        Background = Ui.Background; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(Ui.Text("游戏端", 26, Ui.Accent));
        top.Children.Add(Ui.Text("把 Dota 窗口画面和自身GSI加密传给教练端，并显示教练提醒。不读内存、不控制英雄；画面可能含游戏内聊天。", 12, Brushes.LightGray));
        top.Children.Add(Ui.Label("配对码（在教练端点“复制配对码”；含密钥，勿外传）"));
        connect = Ui.Button("连接", async (_, _) => { if (busy || active != null) await Stop(); else await Start(); });
        connect.MinWidth = 90; connect.Margin = new Thickness(0, 0, 0, 8);
        top.Children.Add(Ui.Row(pairing, connect));
        top.Children.Add(status); top.Children.Add(metrics); top.Children.Add(gsiStatus);
        if (e2e == null) ShowLaunchOptions(); else ShowGsi();
        root.Children.Add(top);
        preview.Children.Add(image); preview.Children.Add(marks); Grid.SetRow(preview, 1); root.Children.Add(preview);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(caption);
        practiceButton = Ui.Button("开始本机练习", async (_, _) => await TogglePractice());
        practiceDistance.Items.Add("近身（约 0.37 秒命中）"); practiceDistance.Items.Add("中距离（约 0.53 秒命中）");
        practiceDistance.Items.Add("接近最远射程（约 0.65 秒命中）"); practiceDistance.SelectedIndex = 1;
        if (e2e == null) {
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("一级空装露娜、默认血条、普通兵 / 一塔；面向目标，站在所选距离内。塔刀仅用于单塔练习，混战不要按提示垫刀。首次会配置本机 GSI。", 12));
            panel.Children.Add(Ui.Row(practiceDistance, practiceButton)); panel.Children.Add(practiceStatus);
            panel.Children.Add(Ui.Section("塔还没开火？按当前血量试算", BuildPracticePreview()));
            bottom.Children.Add(Ui.Section("本机练习：正补、反补、塔下垫刀", panel));
        }
        creepHpButton = Ui.Button("显示小兵血量", async (_, _) => await ToggleCreepHp());
        if (e2e == null) {
            var panel = new StackPanel();
            panel.Children.Add(Ui.Text("默认血条、无边框或窗口模式。Dota 画面只在本机显卡上识别，不传给教练端；显卡有空闲时每帧刷新，显卡被游戏占满时限 60 帧以免拖慢游戏；切出 Dota 时隐藏。", 12));
            panel.Children.Add(creepHpButton); panel.Children.Add(creepHpStatus);
            bottom.Children.Add(Ui.Section("小兵血量：本机实时显示", panel));
            Loaded += async (_, _) => { if (CreepHpLog != null) await ToggleCreepHp(); };
        }
        if (overlayPanel != null) bottom.Children.Add(Ui.Section("游戏内显示：字幕和对手面板的开关、位置、大小、颜色、透明度", overlayPanel));
        bottom.Children.Add(Ui.Section("设置：画面来源、视频、Dota 目录", BuildSettings()));
        bottom.Children.Add(Ui.Section("Debug", debug.View));
        Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
        preview.SizeChanged += (_, _) => DrawMark();
        reminderTimer.Tick += (_, _) => {
            if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
            if (shown != null && (Environment.TickCount64 >= reminderDeadline ||
                shown.Kind == MinuteFarmContext.Kind && !MinuteRouteValid(shown))) ClearReminder();
            try {
                if (debug.Recorder is { } recorder) {
                    var own = GameGsiSample.Read(gsi, participantOnly: gsiOnlyConnection);
                    recorder.State(new(status.Text, transportConnected, false, false, false, 0, 0, 0,
                        lastCaptureAt is long at ? Environment.TickCount64 - at : null, own.Player, GsiAgeMs: own.AgeMs));
                }
            } catch { /* Debug must not affect the UI timer. */ }
            if (!gsiOnlyConnection && gsi?.LastDiagnostics is { OwnStateAccepted: true } d) {
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
            while (practiceBusy || creepHpBusy) await Task.Delay(25);
            await StopPractice();
            await StopCreepCapture();
            await Stop();
            if (gsi != null) { await gsi.DisposeAsync(); gsi = null; }
            closed = true; reminderTimer.Stop(); overlay.Close();
            await debug.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private double PracticeImpact => practiceDistance.SelectedIndex switch { 0 => .37, 2 => .65, _ => .53 };
    private StackPanel BuildPracticePreview()
    {
        var type = Ui.Choice(); type.Items.Add("近战兵"); type.Items.Add("远程兵"); type.SelectedIndex = 0;
        var hp = Ui.Input("150"); var seconds = Ui.Input("1.4"); var answer = Ui.Text("", 13, Ui.Accent);
        var calculate = Ui.Button("试算", (_, _) => {
            bool ranged = type.SelectedIndex == 1;
            if (!int.TryParse(hp.Text.Trim(), out int health) || health < 1 || health > (ranged ? 300 : 550) ||
                !double.TryParse(seconds.Text.Trim(), out double t) || !double.IsFinite(t) || t <= 0 || t > 4) {
                answer.Text = $"请填 1–{(ranged ? 300 : 550)} HP，首发塔弹命中时间须大于 0、至多 4 秒。"; return;
            }
            var p = new LaneDrill(PracticeImpact).Rehearse(ranged, health, t);
            answer.Text = p == null ? "此时机没有稳妥方案：不要硬垫，换一个目标或更早准备。" :
                $"先垫 {p.PrepHits} 刀，塔累计打 {p.TowerHits} 下后由你收尾。" +
                (p.ClickIn > .07 ? $" 约 {p.ClickIn:F1} 秒后出手（已计弹道）。" : " 现在出手。");
        });
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("依次选兵种、输入当前 HP、距第一发塔弹命中的秒数。假设只有塔和你输出，普攻现在可用。", 12));
        panel.Children.Add(Ui.Columns(type, hp, seconds, calculate)); panel.Children.Add(answer);
        return panel;
    }
    private async Task TogglePractice()
    {
        if (practiceBusy || busy || active != null || closing) return;
        practiceBusy = true; connect.IsEnabled = false; practiceButton.IsEnabled = false;
        try {
            if (practice != null) { await StopPractice(); return; }
            if (gsi != null && !persistentGsi) { await gsi.DisposeAsync(); gsi = null; }
            if (gsi == null) {
                gsi = await GsiReceiver.Start(resumeToken: GsiSetup.Token(GsiState), dotaInstall: dotaInstall.Text.Trim());
                practiceOwnsGsi = true;
            }
            bool changed = GsiSetup.Install(dotaInstall.Text.Trim(), gsi.Config, GsiState).Changed;
            var receiver = gsi;
            var message = changed ? "已写入自身GSI，请带 -gamestateintegration 完全重启 Dota 一次。" :
                "请启用 -gamestateintegration，进入英雄 → 露娜 → 试玩。";
            practice = new LastHitPractice(() => receiver.CanPractice, PracticeImpact,
                text => practiceStatus.Text = message + "\n" + text);
            practiceButton.Content = "停止本机练习"; practiceDistance.IsEnabled = false;
        } catch (Exception ex) {
            await StopPractice();
            practiceStatus.Text = $"练习启动失败：请检查 Dota 目录及本机 3219 端口（{ex.GetType().Name}）。";
        } finally {
            practiceBusy = false; connect.IsEnabled = practice == null; practiceButton.IsEnabled = !closing;
        }
    }
    private async Task StopPractice()
    {
        if (practice != null) { await practice.DisposeAsync(); practice = null; }
        if (practiceOwnsGsi && gsi != null) { await gsi.DisposeAsync(); gsi = null; }
        practiceOwnsGsi = false; practiceDistance.IsEnabled = true; practiceButton.Content = "开始本机练习";
        practiceStatus.Text = "已停止本机练习。";
    }
    private async Task ToggleCreepHp()
    {
        if (closing || creepHpBusy || gsiOnlyConnection || busy && source.SelectedIndex == 2) return;
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
            if (gsi == null) {
                try {
                    gsi = await GsiReceiver.Start(resumeToken: GsiSetup.Token(GsiState), dotaInstall: dotaInstall.Text.Trim());
                    persistentGsi = true;
                } catch { gsiNote = "自身GSI启动失败（3219端口被占用？），只能显示百分比。"; }
            }
            if (gsi != null) {
                try {
                    if (GsiSetup.Install(dotaInstall.Text.Trim(), gsi.Config, GsiState).Changed)
                        gsiNote = "已写入自身GSI：带 -gamestateintegration 完全重启 Dota 一次后，才能区分近战/远程/攻城兵。";
                } catch { gsiNote = "自动写入GSI配置失败（检查“设置”里的 Dota 目录），只能显示百分比。"; }
            }
            if (closing || gsiOnlyConnection) return;
            string? log = CreepHpLog;
            CreepHpDisplay? started = null;
            started = new CreepHpDisplay(s => {
                if (log != null) try { File.AppendAllText(log, System.Text.Json.JsonSerializer.Serialize(new { at = DateTimeOffset.Now, s.Status,
                    fps = Math.Round(s.Fps, 1), scanMs = Math.Round(s.ScanMs, 2), s.Bars, s.Known, s.GpuLoad, s.Limited }) + "\n"); } catch { }
                Dispatcher.BeginInvoke(() => {
                    if (creepHp != started) return;
                    string rate = s.Fps > 0 ? $"\n{s.Fps:F0} 帧/秒{(s.Limited ? $"（显卡繁忙，限 {GpuPacing.LimitedFps} 以免拖慢游戏）" : "")} · 识别 {s.ScanMs:F1} ms · " +
                        $"{s.Bars} 根血条，{s.Known} 根已认出兵种" : "";
                    creepHpStatus.Text = s.Status + rate + (gsiNote.Length > 0 ? "\n" + gsiNote : "");
                });
            }, () => gsi?.LatestMinimap(), excludeFromCapture: log == null);
            creepHp = started;
            creepHpButton.Content = "关闭小兵血量";
        } finally { creepHpBusy = false; creepHpButton.IsEnabled = !closing && !gsiOnlyConnection; }
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
        minimapText = ""; ShowGsi(); overlay.Enemies(null);
        while (creepHpBusy) await Task.Delay(20);
        await StopCreepCapture();
        creepHpButton.Content = "显示小兵血量";
        creepHpStatus.Text = "仅本人 GSI 连接：已关闭本机屏幕识别，断开后才能重新开启。";
    }
    private async Task Start()
    {
        if (busy || active != null || closing || practice != null || practiceBusy) return;
        busy = true;
        CancellationTokenSource? starting = null;
        try
        {
            var pair = Pairing.Decode(pairing.Password.Trim());
            bool diagnostic = source.SelectedIndex == 1;
            bool gsiOnly = source.SelectedIndex == 2 && e2e == null;
            var chosen = gsiOnly ? videoSettings : ReadVideoSettings();
            if (gsiOnly && MessageBox.Show(this,
                "把自己的对局状态和游戏窗口尺寸传到配对的教练端？\n\n此模式不采集、编码或传送游戏画面。游戏端可独立显示教练端为本局授权的上次可见装备和眼位。开启分钟路线可能调用已配置的云模型并计费。不会控制英雄。",
                "确认仅传本人 GSI", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            if (!gsiOnly && !diagnostic && e2e == null && MessageBox.Show(this,
                "开始传送 Dota 2 游戏画面并启用自动教练？\n\n画面含主视图、小地图、HUD，也可能含游戏内聊天。\n" +
                (chosen.Capture == "window"
                    ? "窗口采集：切到其他窗口时仍持续传送 Dota 窗口自身的画面；其他应用的内容不会被采集。\n"
                    : "前台模式：只在 Dota 位于前台时采集屏幕区域，可能含系统通知。\n") +
                "教练端默认将真实画面＋自身GSI发送到指定云模型，会计费。\n不会传音频或控制英雄；可随时停止。",
                "确认游戏画面传输", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            try { if (!gsiOnly) chosen.Save(videoSettingsPath); } catch { /* Settings still apply to this run. */ }
            videoSettings = chosen;
            starting = new CancellationTokenSource(); active = starting;
            SetIdle(false);
            if (gsiOnly) { await EnterMinutePrivacy(); starting.Token.ThrowIfCancellationRequested(); }
            if (!diagnostic && e2e == null && gsi != null && !persistentGsi) {
                await gsi.DisposeAsync(); gsi = null;
            }
            if (gsi == null)
            {
                try {
                    string? identity = e2e?.ResumeGsiToken() ?? (diagnostic ? null : GsiSetup.Token(GsiState));
                    gsi = await GsiReceiver.Start(cancellation: starting.Token, resumeToken: identity,
                        dotaInstall: !diagnostic && e2e == null ? dotaInstall.Text.Trim() : null);
                    gsi.Diagnostics = d => { if (!gsiOnlyConnection) debug.Recorder?.Gsi(d); };
                    gsi.Snapshot = s => { if (!gsiOnlyConnection) debug.Recorder?.PlayerMinimap(s); };
                    persistentGsi = !diagnostic && e2e == null;
                    ShowGsi("自身GSI：本机端口 3219 已启动");
                }
                catch (OperationCanceledException) { throw; }
                catch { ShowGsi("自身GSI启动失败：检查3219端口及本地凭据存储权限。视频仍可传，但没有自身状态时不调用AI。"); }
            }
            if (e2e == null && !diagnostic && gsi != null) {
                try {
                    gsi.UseDotaInstall(dotaInstall.Text.Trim());
                    bool changed = GsiSetup.Install(dotaInstall.Text.Trim(), gsi.Config, GsiState).Changed;
                    gsiText = changed ? "自身GSI配置已写入/更新：请完全退出并重启 Dota 一次。" : "自身GSI配置已就绪，等待对局数据";
                } catch { gsiText = "自动写入GSI配置失败（目录/权限，或同名文件被改过；不会覆盖），请检查“设置”里的 Dota 目录。"; }
                ShowLaunchOptions();
            }
            if (e2e != null && !exported) {
                if (gsi == null) throw new InvalidOperationException("Local bot E2E requires GSI");
                e2e.ExportGsi(gsi.Config); exported = true;
                e2e.Trace("game", "ready", new { gsiPort = gsi.Port, localBotOnly = true, captureOverlayEvidence = e2e.CaptureOverlayEvidence });
            }
            starting.Token.ThrowIfCancellationRequested();
            var cancellation = starting.Token;
            running = gsiOnly ? RunMinuteState(pair, cancellation) : Run(pair, diagnostic, chosen, cancellation);
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
    private StackPanel BuildSettings()
    {
        var panel = new StackPanel();
        source.Items.Add("Dota 2 游戏画面"); source.Items.Add("诊断画面（不采集桌面，测试连接用）");
        source.Items.Add("仅本人 GSI（可接收装备、眼位和分钟路线；不采集画面）"); source.SelectedIndex = 0;
        panel.Children.Add(Ui.Label("画面来源")); panel.Children.Add(source);
        panel.Children.Add(BuildVideoSettings());
        if (e2e == null) {
            panel.Children.Add(Ui.Label("Dota 安装目录（连接时自动写入自身GSI配置）"));
            panel.Children.Add(dotaInstall);
            panel.Children.Add(Ui.Button("复制启动参数", (_, _) => CopyLaunchOptions()));
        }
        return panel;
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
        if (idle && gsiOnlyConnection) { gsiOnlyConnection = false; creepHpStatus.Text = "已关闭。"; }
        creepHpButton.IsEnabled = !closing && !creepHpBusy && !gsiOnlyConnection;
        foreach (var control in new Control[] { source, pairing, fpsChoice, resolutionChoice, encoderChoice, captureChoice, bitrateChoice })
            control.IsEnabled = idle;
        connect.Content = idle ? "连接" : "断开";
        practiceButton.IsEnabled = idle;
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
                var boardTask = diagnostic ? Task.CompletedTask : PollBoards(link, Viewport, streamStop.Token);
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
    /// <summary>Neither a slow board response nor a batch of missing icons may stall GSI/terminal uploads,
    /// video leases or the route poll. No diagnostic mode and no local spectator input.</summary>
    private async Task PollBoards(GameLink link, Func<(int Width, int Height)> viewport, CancellationToken cancel)
    {
        EnemyBoard? Current(EnemyBoard receipt, int w, int h) =>
            cancel.IsCancellationRequested || !transportConnected ? null : link.DisplayEnemies(receipt, gsi?.LatestMatch(), w, h);
        overlay.BoardForDisplay = Current;
        try {
            while (!cancel.IsCancellationRequested) {
                try {
                    var size = viewport();
                    var board = size.Width > 0 && (overlay.Settings.ShowEnemies || overlay.Settings.ShowWards) &&
                        BoardDelivery.Live(gsi?.LatestMatch()) ? await link.Enemies(cancel) : null;
                    size = viewport(); // Poll may have crossed a resize, pause, end, withdrawal or new session.
                    if (board != null && Current(board, size.Width, size.Height) == null) board = null;
                    overlay.Enemies(board);
                    if (board != null && overlay.Settings.ShowEnemies) {
                        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                        budget.CancelAfter(500);
                        try {
                            foreach (string key in overlay.Icons.Missing(board, Environment.TickCount64).Take(8)) {
                                if (!overlay.Settings.ShowEnemies || Current(board, size.Width, size.Height) == null) break;
                                overlay.Icons.Add(key, await link.Icon(key, budget.Token), Environment.TickCount64);
                            }
                        } catch (OperationCanceledException) when (!cancel.IsCancellationRequested) { /* Retry icons on a later poll. */ }
                    }
                } catch (Exception ex) when (ex is System.Net.Http.HttpRequestException or OperationCanceledException or System.IO.IOException or InvalidOperationException) {
                    overlay.Enemies(null); // The optional channel fails closed; don't interrupt the main transport.
                }
                await Task.Delay(750, cancel);
            }
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
        finally { overlay.BoardForDisplay = null; overlay.Enemies(null); }
    }
    private bool MinuteRouteValid(Reminder r)
    {
        if (!transportConnected || active?.IsCancellationRequested != false || shownMinuteLink == null) return false;
        var own = gsi?.Latest() ?? (null, null);
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
                    var own = diagnostic ? default : GameGsiSample.Read(gsi, participantOnly: false);
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
        status.Text = "已停止 · 不再采集或传输画面";
        metrics.Text = "";
    }
    private void ShowLaunchOptions() { launchText = Ui.LaunchOptionsText(SteamLaunch.Inspect()); ShowGsi(); }
    private void CopyLaunchOptions() { launchText = Ui.CopyLaunchOptions(SteamLaunch.Inspect()); ShowGsi(); }
}
