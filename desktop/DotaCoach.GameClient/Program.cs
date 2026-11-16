using System;
using System.IO;
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
        new Application().Run(new GameWindowUi(E2ERun.Load(args)));
    }
}

internal sealed class GameWindowUi : Window
{
    private readonly PasswordBox pairing = new() { Padding = new Thickness(8), Margin = new Thickness(0, 0, 0, 8), FontSize = 14 };
    private readonly ComboBox source = new() { Margin = new Thickness(0, 0, 0, 10), Padding = new Thickness(6), FontSize = 14 };
    private readonly TextBlock status = Ui.Text("未连接 · 不采集画面", 16, Ui.Accent);
    private readonly TextBlock metrics = Ui.Text("持续H.264 · 目标30fps / 6Mbps · 最大1600×900 · 保留4K原始坐标 · 无音频");
    private static readonly string GsiState = GsiSetup.StateDirectoryFor("Game");
    private readonly TextBox dotaInstall = Ui.Input(GsiSetup.FindInstall(GsiState));
    private readonly TextBlock caption = Ui.Text("回传字幕将在这里显示；真实游戏中另外显示私人悬浮层。", 18, Ui.Accent);
    private readonly TextBlock gsiStatus = Ui.Text("GSI 未启动；视频传输可以独立测试。");
    private readonly TextBlock launchStatus = Ui.Text("", 12, Brushes.LightGray);
    private readonly TextBlock minimapStatus = Ui.Text("玩家模式小地图：等待对局数据（只在本机统计，不上传）", 12, Brushes.LightGray);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas marks = new() { IsHitTestVisible = false };
    private readonly Grid preview = new() { Background = Brushes.Black, MinHeight = 240 };
    private readonly AdviceOverlay overlay;
    private readonly DebugPanel debug;
    private readonly DispatcherTimer reminderTimer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private CancellationTokenSource? active;
    private Task? running;
    private GsiReceiver? gsi;
    private bool closed, closing, busy;
    private long localSequence;
    private Reminder? shown;
    private long reminderDeadline;
    private readonly E2ERun? e2e;
    private string? lastReceivedId;
    private bool exported;
    private bool persistentGsi;
    private bool transportConnected;
    private long? lastCaptureAt;

    public GameWindowUi() : this(null) { }
    public GameWindowUi(E2ERun? run) : this(run, null) { }
    public GameWindowUi(E2ERun? run, string? debugRoot)
    {
        e2e = run; debug = new DebugPanel("game", debugRoot); overlay = new AdviceOverlay(run, () => debug.Recorder);
        Title = "Dota Coach · 游戏端 v0.3.1";
        Width = Math.Min(1120, SystemParameters.WorkArea.Width - 40);
        Height = Math.Min(900, SystemParameters.WorkArea.Height - 40);
        MinWidth = 860; MinHeight = 680; WindowStartupLocation = WindowStartupLocation.CenterScreen;
        preview.MinHeight = 140;
        Background = Ui.Background; FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
        var root = new Grid { Margin = new Thickness(24) };
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        root.RowDefinitions.Add(new() { Height = new GridLength(1, GridUnitType.Star) });
        root.RowDefinitions.Add(new() { Height = GridLength.Auto });
        var top = new StackPanel();
        top.Children.Add(Ui.Text("GAME CLIENT  /  游戏机", 26, Ui.Accent));
        top.Children.Add(Ui.Text("只采集、传输和显示 · 不运行模型 · 不读取游戏内存 · 不发送键鼠指令"));
        top.Children.Add(Ui.Text("1  粘贴教练端复制的配对码（含连接密钥，请勿分享截图或日志）"));
        top.Children.Add(pairing);
        source.Items.Add("Dota 2 前台画面"); source.Items.Add("诊断画面（不采集桌面）"); source.SelectedIndex = 0;
        top.Children.Add(source);
        var buttons = new WrapPanel();
        buttons.Children.Add(Ui.Button("连接并开始", async (_, _) => await Start()));
        buttons.Children.Add(Ui.Button("停止 / 断开", async (_, _) => await Stop()));
        buttons.Children.Add(Ui.Button("导出本次 GSI 配置", (_, _) => ExportGsi()));
        buttons.Children.Add(Ui.Button("复制启动参数", (_, _) => CopyLaunchOptions()));
        top.Children.Add(buttons); top.Children.Add(status); top.Children.Add(metrics); top.Children.Add(gsiStatus);
        if (e2e == null) {
            top.Children.Add(launchStatus); top.Children.Add(minimapStatus);
            top.Children.Add(Ui.Text("Dota安装目录（连接后自动配置自身GSI；首次需重启Dota加载）", 12));
            top.Children.Add(dotaInstall);
            ShowLaunchOptions();
        }
        root.Children.Add(top);
        preview.Children.Add(image); preview.Children.Add(marks); Grid.SetRow(preview, 1); root.Children.Add(preview);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(caption);
        bottom.Children.Add(Ui.Text("隐私与性能：持续发送前台游戏视频，可能含聊天/系统通知；切出Dota暂停。教练端按需抽帧。软件H.264有CPU开销；游戏端Debug只记运行状态，不重复存图。", 12, Brushes.LightGray));
        bottom.Children.Add(new Expander { Header = "Debug / 抽帧与客户端状态", Content = debug.View, Foreground = Brushes.White });
        Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
        preview.SizeChanged += (_, _) => DrawMark();
        reminderTimer.Tick += (_, _) => {
            if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
            if (shown != null && Environment.TickCount64 >= reminderDeadline) ClearReminder();
            try {
                var own = gsi?.Latest();
                debug.Recorder?.State(new(status.Text, transportConnected, false, false, false, 0, 0, 0,
                    lastCaptureAt is long at ? Environment.TickCount64 - at : null, own?.Player, GsiAgeMs: own?.AgeMs));
            } catch { /* Debug must not affect the UI timer. */ }
            if (gsi?.LastDiagnostics is { OwnStateAccepted: true } d)
                minimapStatus.Text = d.MinimapUnits > 0
                    ? $"玩家模式小地图：有，{d.MinimapUnits} 个单位（只在本机统计，不上传）"
                    : "玩家模式小地图：对局中没有收到";
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
            await Stop();
            if (gsi != null) { await gsi.DisposeAsync(); gsi = null; }
            closed = true; reminderTimer.Stop(); overlay.Close();
            await debug.DisposeAsync();
            _ = Dispatcher.BeginInvoke(new Action(Close));
        };
    }
    private async Task Start()
    {
        if (busy || active != null || closing) return;
        busy = true;
        CancellationTokenSource? starting = null;
        try
        {
            var pair = Pairing.Decode(pairing.Password.Trim());
            bool diagnostic = source.SelectedIndex == 1;
            if (!diagnostic && e2e == null && MessageBox.Show(this,
                "开始传送 Dota 2 前台客户区并启用自动教练？\n\n画面含主视图、小地图、HUD，也可能含聊天/系统通知。\n教练端默认将真实画面＋自身GSI发送到指定云模型，会计费。\n不会传音频、控制英雄或截取其他前台应用；可随时停止。\n约1fps，支持4K原始像素投影，仍有采集编码开销。",
                "确认游戏画面传输", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            starting = new CancellationTokenSource(); active = starting;
            source.IsEnabled = false; pairing.IsEnabled = false;
            if (!diagnostic && e2e == null && gsi != null && !persistentGsi) {
                await gsi.DisposeAsync(); gsi = null;
            }
            if (gsi == null)
            {
                try {
                    string? identity = e2e?.ResumeGsiToken() ?? (diagnostic ? null : GsiSetup.Token(GsiState));
                    gsi = await GsiReceiver.Start(cancellation: starting.Token, resumeToken: identity);
                    gsi.Diagnostics = d => debug.Recorder?.Gsi(d);
                    persistentGsi = !diagnostic && e2e == null;
                    gsiStatus.Text = "GSI 本机端口 3219 已启动。";
                }
                catch (OperationCanceledException) { throw; }
                catch { gsiStatus.Text = "GSI初始化失败；检查3219端口及本地凭据存储权限。视频仍可测试，但无自身状态不调用AI。"; }
            }
            if (e2e == null && !diagnostic && gsi != null) {
                try {
                    bool changed = GsiSetup.Install(dotaInstall.Text.Trim(), gsi.Config, GsiState).Changed;
                    gsiStatus.Text = changed ? "自身GSI配置已写入/更新：请完全退出并重启Dota一次（需带-gamestateintegration）。" :
                        "自身GSI配置已就绪；正常重开客户端无需再导出。";
                } catch { gsiStatus.Text = "自动配置GSI失败（目录/权限/同名文件已修改）；不会覆盖用户配置，可手动导出。"; }
                ShowLaunchOptions();
            }
            if (e2e != null && !exported) {
                if (gsi == null) throw new InvalidOperationException("Local bot E2E requires GSI");
                e2e.ExportGsi(gsi.Config); exported = true;
                e2e.Trace("game", "ready", new { gsiPort = gsi.Port, localBotOnly = true, captureOverlayEvidence = e2e.CaptureOverlayEvidence });
            }
            starting.Token.ThrowIfCancellationRequested();
            var cancellation = starting.Token;
            running = Run(pair, diagnostic, cancellation);
            _ = FinishRun(running);
        }
        catch (OperationCanceledException) { status.Text = "启动已取消 · 不采集画面"; }
        catch (Exception e) { debug.Recorder?.Error("game-start", e); status.Text = e is ArgumentException ? e.Message : "启动失败，请检查配对码。"; }
        finally
        {
            busy = false;
            if (running == null && starting != null && active == starting) {
                active.Dispose(); active = null; source.IsEnabled = true; pairing.IsEnabled = true;
            }
        }
    }
    private async Task FinishRun(Task task)
    {
        try { await task; } catch { }
        if (running != task) return;
        active?.Dispose(); active = null; running = null;
        source.IsEnabled = true; pairing.IsEnabled = true; ClearReminder();
    }
    private async Task Run(Pairing pair, bool diagnostic, CancellationToken cancellation)
    {
        // Preserve the explicitly opted-in historical bot/pixel-evidence harness, not as a live fallback.
        if (e2e != null) { await RunLegacyEvidence(pair, diagnostic, cancellation); return; }
        using var link = new GameLink(pair);
        try {
            while (!cancellation.IsCancellationRequested) {
                var surface = diagnostic ? null : GameWindow.Foreground();
                if (!diagnostic && surface == null) {
                    status.Text = "已暂停视频：请切回 Dota 2（其他应用不采集）";
                    await Task.Delay(250, cancellation); continue;
                }
                await link.Connect(cancellation);
                await link.OpenVideo(cancellation);
                var lease = await link.GetVideoTicket(cancellation);
                transportConnected = true;
                var stream = new VideoSender(lease);
                using var streamStop = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                var captureTask = Task.Run(() => stream.CaptureLoop(diagnostic, surface,
                    () => Interlocked.Increment(ref localSequence), () => gsi?.Latest() ?? (null, null), streamStop.Token));
                var sendTask = Task.Run(() => stream.SendLoop(link, streamStop.Token));
                long renewAt = Environment.TickCount64 + 750, previewAt = 0, started = Environment.TickCount64;
                try {
                    while (!cancellation.IsCancellationRequested) {
                        if (captureTask.IsCompleted) { await captureTask; break; }
                        if (sendTask.IsCompleted) { await sendTask; break; }
                        if (!diagnostic && GameWindow.Foreground() != surface) break;
                        if (Environment.TickCount64 >= renewAt) {
                            stream.Renew(await link.GetVideoTicket(cancellation));
                            renewAt = Environment.TickCount64 + 750;
                        }
                        if (stream.Latest is { } frame) {
                            lastCaptureAt = frame.CapturedAt;
                            overlay.FrameArrived(frame.Meta.Player?.MatchId == "0");
                            if (Environment.TickCount64 >= previewAt) {
                                image.Source = Ui.VideoImage(frame.Pixels); DrawMark();
                                double seconds = Math.Max(.001, (Environment.TickCount64 - started) / 1000.0);
                                metrics.Text = $"H.264 目标30fps / 6Mbps · 原始 {frame.Meta.SourceWidth}×{frame.Meta.SourceHeight} → {frame.Pixels.Width}×{frame.Pixels.Height}\n" +
                                    $"实际平均发送 {stream.Sent / seconds:F1}fps / {stream.Bytes * 8 / seconds / 1_000_000:F2}Mbps · 未编码丢帧 {stream.Dropped}";
                                status.Text = diagnostic ? "已连接 · 持续发送诊断视频（非游戏）" : "已连接 · 持续发送 Dota 2 前台视频";
                                gsiStatus.Text = frame.Meta.Player is { } p ? $"自身GSI：{p.Team} · {p.Hero} · {p.Clock:F1}s" : "无新鲜自身GSI；视频继续，AI不推测装备/阵营。";
                                previewAt = Environment.TickCount64 + 250;
                            }
                        }
                        ApplyReminder(await link.Poll(cancellation), diagnostic);
                        await Task.Delay(100, cancellation);
                    }
                } finally {
                    streamStop.Cancel(); link.AbortVideo();
                    try { await Task.WhenAll(captureTask, sendTask); } catch when (streamStop.IsCancellationRequested) { }
                    ClearReminder(); image.Source = null; transportConnected = false; lastCaptureAt = null;
                    using var timeout = new CancellationTokenSource(1500);
                    try { await link.End(timeout.Token); } catch { }
                }
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
    private void ApplyReminder(Reminder? reminder, bool diagnostic)
    {
        if (reminder == null) { ClearReminder(); return; }
        long deadline = Environment.TickCount64 + reminder.RemainingMs;
        reminderDeadline = shown?.Id == reminder.Id ? Math.Min(reminderDeadline, deadline) : deadline;
        shown = reminder;
        caption.Text = !reminder.ShowSubtitle ? "地图目标提示（字幕已关闭）" :
            (reminder.Kind == "manual-link-test" ? "人工联调（非 AI 路线）： " :
            reminder.Kind is "experimental-bot-advice" or "visible-farming-advice" ? "AI打钱建议（非最优保证）： " : "视觉观察（未核验）： ") + reminder.Message;
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
                    var own = diagnostic ? (Player: (OwnState?)null, AgeMs: (int?)null) : gsi?.Latest() ?? (null, null);
                    var meta = new FrameMeta(link.SessionId!, ticket.Id, seq, diagnostic ? "diagnostic" : "dota2",
                        capture.Width, capture.Height, own.Player, own.AgeMs);
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
                    if (own.Player is { } p) gsiStatus.Text = $"自身 GSI：{p.Team} · {p.Hero} · 游戏 {p.Clock:F1}s · 观测年龄 {own.AgeMs}ms";
                    else if (!diagnostic) gsiStatus.Text = "当前无新鲜自身 GSI；不推测装备/阵营，不使用旁观者全图数据。";
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
                        (reminder.Kind == "manual-link-test" ? "人工联调（非 AI 路线）： " :
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
        if (shown?.Marker is not { } point || image.Source == null) return;
        var rect = Ui.ImageRect(preview.ActualWidth, preview.ActualHeight, shown.SourceWidth, shown.SourceHeight);
        var ring = new Ellipse { Width = 24, Height = 24, Stroke = Brushes.Gold, StrokeThickness = 3 };
        Canvas.SetLeft(ring, rect.Left + point.X * rect.Width - 12); Canvas.SetTop(ring, rect.Top + point.Y * rect.Height - 12);
        marks.Children.Add(ring);
    }
    private void ClearReminder()
    {
        if (shown != null) debug.Recorder?.Reminder("reminder-cleared", shown);
        shown = null; overlay.Clear(); marks.Children.Clear();
        caption.Text = "无有效提醒 · 断线、过期、切出游戏时自动清除";
    }
    private async Task Stop()
    {
        active?.Cancel(); ClearReminder();
        while (busy) await Task.Delay(25);
        var task = running; if (task != null) try { await task; } catch { }
        status.Text = "已停止 · 不再采集或传输画面";
    }
    private void ExportGsi()
    {
        if (gsi == null) { status.Text = "先连接一次以启动本机 GSI 接收。"; return; }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = "gamestate_integration_dota2_coach_twopc.cfg", Filter = "Dota GSI 配置|*.cfg" };
        if (dialog.ShowDialog(this) == true) {
            File.WriteAllText(dialog.FileName, gsi.Config);
            gsiStatus.Text = "已导出GSI配置。正常游戏身份已持久化；诊断/验收身份可能仅本次有效。不要分享该文件。";
        }
    }
    private void ShowLaunchOptions() => launchStatus.Text = Ui.LaunchOptionsText(SteamLaunch.Inspect());
    private void CopyLaunchOptions() => launchStatus.Text = Ui.CopyLaunchOptions(SteamLaunch.Inspect());
}
