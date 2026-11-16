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
    private readonly TextBlock visionText = Ui.Text("视觉未调用。需要教练机本地已安装的 Ollama 视觉模型；不自动下载，不回退云端。");
    private readonly TextBlock previewTitle = Ui.Text("LIVE VIEW  /  等待游戏端", 22, Ui.Accent);
    private readonly Image image = new() { Stretch = Stretch.Uniform };
    private readonly Canvas marks = new() { IsHitTestVisible = false };
    private readonly Grid preview = new() { Background = Brushes.Black };
    private readonly CheckBox frozen = new() { Content = "冻结预览（不停止接收）", Foreground = Brushes.White, Margin = new Thickness(0, 8, 0, 8) };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(350) };
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

    public CoachWindow() : this(null) { }
    public CoachWindow(E2ERun? run)
    {
        e2e = run;
        Title = "Dota Coach · 教练端（3090 Ti）v0.1.2";
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
        address.SelectedIndex = 0; left.Children.Add(address);
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
        preview.Children.Add(image); preview.Children.Add(marks);
        Grid.SetRow(preview, 1); right.Children.Add(preview);
        var vision = new StackPanel { Margin = new Thickness(0, 14, 0, 0) };
        vision.Children.Add(Ui.Text("本地视觉识别 · 可选实验", 20, Ui.Accent));
        vision.Children.Add(Ui.Text("Ollama 已安装的视觉模型名称（仅 http://127.0.0.1:11434）", 12, Brushes.LightGray));
        vision.Children.Add(model);
        var actions = new WrapPanel();
        actions.Children.Add(Ui.Button("识别最新游戏画面一次", async (_, _) => await Observe()));
        actions.Children.Add(Ui.Button("回传本次观察字幕", (_, _) => SendObservation()));
        vision.Children.Add(actions); vision.Children.Add(visionText);
        vision.Children.Add(Ui.Text(e2e == null ?
            "普通模式：双机传输＋可选本地观察。自动打钱策略未启用，不读取云端API密钥。" :
            "LOCAL BOT E2E：已显式配置Foundry云模型；真实图像和自身GSI会送往该服务并计费。仅本地局，非最优路线保证。", 12, Brushes.LightGray));
        Grid.SetRow(vision, 2); right.Children.Add(vision); Grid.SetColumn(right, 2); root.Children.Add(right);
        Content = root;
        preview.MouseLeftButtonDown += SelectMark; preview.SizeChanged += (_, _) => DrawMark();
        timer.Tick += (_, _) => Refresh(); timer.Start();
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
            e.Cancel = true; closing = true; stop.Cancel(); timer.Stop();
            await Stop();
            cloud?.Dispose();
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
            port.IsEnabled = false; address.IsEnabled = false;
            status.Text = $"HTTPS 接收已开启 · 端口 {host.Port}";
            notice.Text = "复制配对码到游戏端。若跨机无法连接，请仅为此程序/端口允许 Windows 专用网络入站。";
        }
        catch (Exception e) { notice.Text = e is ArgumentException ? e.Message : "无法启动接收：检查端口是否被占用。未修改防火墙。"; }
        finally { starting?.Dispose(); starting = null; busy = false; }
    }
    private async Task Stop()
    {
        CancelProbe();
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
    private void Refresh()
    {
        if (e2e != null && !closing && File.Exists(System.IO.Path.Combine(e2e.Directory, "stop-requested"))) { Close(); return; }
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
        status.Text = snapshot.Connected ? "正在接收 · 加密配对连接" : snapshot.Status;
        stats.Text = $"累计 {snapshot.Frames} 帧 / {snapshot.Bytes / 1048576.0:F2} MiB\n最新画面年龄 {snapshot.FrameAgeMs?.ToString() ?? "—"} ms（教练机接收后）";
        player.Text = snapshot.Player is { } p ? $"自身GSI：{(p.Team == "radiant" ? "天辉" : "夜魇")} · 对局 {p.MatchId}\n{p.Hero}\n游戏 {p.Clock:F1}s · 血 {p.Health:F0} · 蓝 {p.Mana:F0}" : "自身 GSI：未知 / 已过期（不从全图补全）";
        if (!snapshot.Connected)
        {
            ClearView();
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
            image.Source = Ui.Image(latest.Jpeg); displayed = latest; selected = null; marks.Children.Clear();
            previewTitle.Text = $"{(latest.Meta.Source == "diagnostic" ? "诊断画面 · 不是游戏" : "DOTA 2 · 实时抽帧")}  /  #{latest.Meta.Sequence}";
        }
        catch { notice.Text = "图像解码失败；该帧未显示。"; }
    }
    private async Task Decide(FrameView frame, CoachHost target)
    {
        deciding = true; modelCalls++; nextDecision = Environment.TickCount64 + 20000;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        deadline.CancelAfter(11000);
        File.WriteAllBytes(System.IO.Path.Combine(e2e!.Directory, $"model-frame-{frame.Meta.SessionId}-{frame.Meta.Sequence}.jpg"), frame.Jpeg);
        e2e!.Trace("coach", "model-start", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, player = frame.Meta.Player, attempt = modelCalls });
        visionText.Text = $"真实模型推理中 #{modelCalls} · 来源帧 {frame.Meta.Sequence}";
        try {
            var result = await cloud!.Decide(frame, deadline.Token);
            e2e.Trace("coach", "model-result", new { frame.Meta.SessionId, frame = frame.Meta.Sequence, result });
            // Never stamp an old model result with the latest frame; mailbox checks the original capture.
            if (host != target || stop.IsCancellationRequested) throw new InvalidOperationException("Context ended");
            target.Mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence, "experimental-bot-advice",
                result.Advice.Message, result.Advice.Marker);
            var sent = target.Mailbox.Poll(frame.Meta.SessionId);
            e2e.Trace("coach", "model-published", new { frame = frame.Meta.Sequence, reminder = sent });
            visionText.Text = $"实验建议（非最优保证）：{result.Advice.Message}\n{result.ElapsedMs}ms · tokens {result.InputTokens}+{result.OutputTokens}";
        }
        catch (Exception error) {
            // No provider body or request/credential in diagnostics.
            e2e.Trace("coach", "model-rejected", new { frame = frame.Meta.Sequence, type = error.GetType().Name,
                reason = error is OperationCanceledException ? "timeout/cancelled" :
                    error is InvalidOperationException or ArgumentException ? error.Message : "request failed" });
            visionText.Text = "模型失败/过期，未回传；没有规则回退。";
        }
        finally { deciding = false; }
    }
    private void ClearView()
    {
        image.Source = null; displayed = null; selected = null; marks.Children.Clear(); frozen.IsChecked = false;
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
            notice.Text = "已回传带“视觉观察”标识的实验字幕。";
        }
        catch { notice.Text = "本次观察已过期/换局，或尚无结果；没有回传。"; }
    }
}
