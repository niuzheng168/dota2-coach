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
    private readonly TextBlock metrics = Ui.Text("1 fps · JPEG · 最长边约1280px · 无音频");
    private readonly TextBlock caption = Ui.Text("回传字幕将在这里显示；真实游戏中另外显示私人悬浮层。", 18, Ui.Accent);
    private readonly TextBlock gsiStatus = Ui.Text("GSI 未启动；视频传输可以独立测试。");
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas marks = new() { IsHitTestVisible = false };
    private readonly Grid preview = new() { Background = Brushes.Black, MinHeight = 240 };
    private readonly AdviceOverlay overlay;
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

    public GameWindowUi() : this(null) { }
    public GameWindowUi(E2ERun? run)
    {
        e2e = run; overlay = new AdviceOverlay(run);
        Title = "Dota Coach · 游戏端 v0.1.2";
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
        top.Children.Add(buttons); top.Children.Add(status); top.Children.Add(metrics); top.Children.Add(gsiStatus);
        root.Children.Add(top);
        preview.Children.Add(image); preview.Children.Add(marks); Grid.SetRow(preview, 1); root.Children.Add(preview);
        var bottom = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        bottom.Children.Add(caption);
        bottom.Children.Add(Ui.Text("隐私与性能：实际模式发送完整游戏客户区，可能包含游戏聊天或叠加通知。切出 Dota 后暂停。\n仅内存保留最新画面，不录制；此版为低帧率链路原型，不是 SRT，也不保证零性能开销。", 12, Brushes.LightGray));
        Grid.SetRow(bottom, 2); root.Children.Add(bottom); Content = root;
        preview.SizeChanged += (_, _) => DrawMark();
        reminderTimer.Tick += (_, _) => {
            if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
            if (shown != null && Environment.TickCount64 >= reminderDeadline) ClearReminder();
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
                "开始传送 Dota 2 前台客户区？\n\n画面含主视图、小地图、HUD，也可能含聊天/系统叠加通知。\n不会传音频或其他前台应用；连接使用配对证书加密。\n首版每秒约1帧，仍有采集编码开销。",
                "确认游戏画面传输", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            starting = new CancellationTokenSource(); active = starting;
            source.IsEnabled = false; pairing.IsEnabled = false;
            if (gsi == null)
            {
                try { gsi = await GsiReceiver.Start(cancellation: starting.Token, resumeToken: e2e?.ResumeGsiToken()); gsiStatus.Text = "GSI 本机端口 3219 已启动；可导出配置后放入 Dota cfg/gamestate_integration。"; }
                catch (OperationCanceledException) { throw; }
                catch { gsiStatus.Text = "GSI 端口 3219 不可用；视频仍可测试。请检查是否有另一个游戏端。"; }
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
        catch (Exception e) { status.Text = e is ArgumentException ? e.Message : "启动失败，请检查配对码。"; }
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
        using var link = new GameLink(pair);
        long nextCapture = 0;
        try
        {
            while (!cancellation.IsCancellationRequested)
            {
                var surface = diagnostic ? null : GameWindow.Foreground();
                if (!diagnostic && (surface == null || e2e != null && gsi?.Latest().Player is not { MatchId: "0" }))
                {
                    ClearReminder(); image.Source = null;
                    if (link.SessionId != null) await link.End(cancellation);
                    status.Text = e2e != null ? "本地验收：等待前台Dota及本地对局GSI（其他对局不采集）" :
                        "已暂停画面传输：请切回 Dota 2（其他应用不采集）";
                    await Task.Delay(500, cancellation); continue;
                }
                if (link.SessionId == null) await link.Connect(cancellation);
                if (Environment.TickCount64 >= nextCapture)
                {
                    var ticket = await link.GetTicket(cancellation);
                    long started = Environment.TickCount64, seq = ++localSequence;
                    var visibleReminder = overlay.IsVisible ? shown : null;
                    var capture = await Task.Run(() => diagnostic ? Capture.Diagnostic(seq) :
                        Capture.Game(surface!, e2e?.CaptureOverlayEvidence == true), cancellation);
                    if (capture == null) { ClearReminder(); await link.End(cancellation); await Task.Delay(500, cancellation); continue; }
                    var own = diagnostic ? (Player: (OwnState?)null, AgeMs: (int?)null) : gsi?.Latest() ?? (null, null);
                    var meta = new FrameMeta(link.SessionId!, ticket.Id, seq, diagnostic ? "diagnostic" : "dota2",
                        capture.Width, capture.Height, own.Player, own.AgeMs);
                    await link.Upload(meta, capture.Jpeg, started, cancellation);
                    if (!diagnostic && GameWindow.Foreground() != surface) { ClearReminder(); await link.End(cancellation); continue; }
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
                    metrics.Text = $"帧 #{seq} · {capture.Jpeg.Length / 1024.0:F0} KiB · 采集+上传 {Environment.TickCount64 - started} ms（不是完整教练延迟）";
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
                    caption.Text = (reminder.Kind == "manual-link-test" ? "人工联调（非 AI 路线）： " :
                        reminder.Kind == "experimental-bot-advice" ? "AI实验建议（非最优保证）： " : "视觉观察（未核验）： ") + reminder.Message;
                    if (lastReceivedId != reminder.Id) {
                        e2e?.Trace("game", "reminder-received", new { reminder });
                        lastReceivedId = reminder.Id;
                    }
                    if (!diagnostic) overlay.Accept(reminder);
                    DrawMark();
                }
                await Task.Delay(250, cancellation);
            }
        }
        catch (Exception error) {
            e2e?.Trace("game", "transport-ended", new { type = error.GetType().Name, cancelled = cancellation.IsCancellationRequested });
            status.Text = LinkErrors.Message(error, cancellation.IsCancellationRequested);
        }
        finally
        {
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
            gsiStatus.Text = "已导出本次游戏端的 GSI 配置。游戏端重启后需重新导出；不要与他人分享该文件。";
        }
    }
}
