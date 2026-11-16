using System.Net;
using System.IO;
using System.Reflection;
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
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) => {
            try { await Run(args.FirstOrDefault() ?? ".tmp/twopc-ui"); Console.WriteLine($"Two native WPF clients: {passed} UI checks passed."); app.Shutdown(0); }
            catch (Exception e) { Console.Error.WriteLine(e); app.Shutdown(1); }
        };
        Environment.ExitCode = app.Run();
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
        var coach = (Window)Activator.CreateInstance(coachType)!;
        var game = (Window)Activator.CreateInstance(gameType)!;
        // Exercise packaged UI handlers without desktop input, clipboard, real game capture or AI.
        coach.Show(); game.Show();
        await Wait(() => Equals(Field<ComboBox>(coach, "address").SelectedItem, "127.0.0.1"),
            "same-machine default is loopback, not a virtual or LAN adapter");
        Field<TextBox>(coach, "port").Text = "38479";
        Click(coach, "启动局域网接收");
        await Wait(() => Field<CoachHost?>(coach, "host") != null, "coach starts its own TLS listener");
        await Wait(() => !Field<bool>(coach, "busy") && !Field<ComboBox>(coach, "address").IsEnabled,
            "listening address is locked until receiver stops");
        await Wait(() => System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
            .GetActiveTcpListeners().Where(e => e.Port == 38479)
            .Select(e => e.Address).SequenceEqual(new[] { IPAddress.Loopback }),
            "default listener binds loopback only");
        var host = Field<CoachHost>(coach, "host");
        Field<TextBox>(coach, "markX").Text = "invalid";
        Click(coach, "5秒后测试回传（请切回Dota）");
        await Wait(() => Field<DeferredLinkProbe?>(coach, "deferredProbe") == null &&
            Field<TextBlock>(coach, "notice").Text.Contains("坐标"),
            "invalid delayed-probe coordinates are rejected by actual UI handler");
        Field<TextBox>(coach, "markX").Text = ".065"; Field<TextBox>(coach, "markY").Text = ".86";
        Click(coach, "5秒后测试回传（请切回Dota）");
        await Wait(() => Field<DeferredLinkProbe?>(coach, "deferredProbe") != null,
            "explicit delayed-probe button schedules a single manual test");
        Click(coach, "清除回传");
        await Wait(() => Field<DeferredLinkProbe?>(coach, "deferredProbe") == null,
            "clear cancels a pending delayed probe");
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        Field<ComboBox>(game, "source").SelectedIndex = 1;
        Click(game, "连接并开始");
        await Wait(() => host.Mailbox.Snapshot().Frames >= 3, "game client emits successive real JPEG diagnostic frames");
        await Wait(() => Field<Image>(coach, "image").Source != null, "coach preview receives decoded bitmap");
        await Wait(() => Field<Image>(game, "image").Source != null, "game preview receives capture bitmap");
        Field<CheckBox>(coach, "frozen").IsChecked = true;
        Field<TextBox>(coach, "message").Text = "双机测试成功：字幕和小地图测试标记已回传。";
        Set(coach, "selected", new ScreenMark(.12, .80));
        Click(coach, "回传测试字幕 / 标记");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机测试成功"), "actual UI reminder handler returns caption");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 1, "UI adds returned normalized marker element");
        Render(coach, Path.Combine(output, "coach.png")); Render(game, Path.Combine(output, "game.png"));
        Click(coach, "清除回传");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 0 &&
            Field<TextBlock>(game, "caption").Text.Contains("无有效提醒"), "clear command clears game caption and marker");
        await Task.Delay(500);
        Click(coach, "回传测试字幕 / 标记");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("双机测试成功"), "second reminder received");
        await Wait(() => Field<TextBlock>(game, "caption").Text.Contains("无有效提醒"), "reminder expires without renewing on polls", 12000);
        Field<CheckBox>(coach, "frozen").IsChecked = true;
        await Task.Delay(12500);
        Click(coach, "回传测试字幕 / 标记");
        await Wait(() => Field<TextBlock>(coach, "notice").Text.StartsWith("未发送"), "expired frozen preview cannot publish a reminder");
        Click(coach, "撤销配对");
        await Wait(() => Field<TextBlock>(game, "status").Text.Contains("连接中断"), "revocation stops game uploader");
        await Wait(() => Field<Canvas>(game, "marks").Children.Count == 0, "revocation leaves no stale marker");
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        Click(game, "连接并开始");
        await Wait(() => host.Mailbox.Snapshot().Connected, "new pairing reconnects with a fresh session");
        Click(game, "停止 / 断开");
        await Wait(() => host.Mailbox.Snapshot().SessionId == null, "game stop ends capture session on coach");
        await Wait(() => Field<Image>(coach, "image").Source == null, "coach clears disconnected preview");
        Click(game, "连接并开始"); Click(game, "停止 / 断开");
        await Wait(() => Field<CancellationTokenSource?>(game, "active") == null, "stop interrupts game connection startup");
        Click(coach, "停止");
        await Wait(() => Field<CoachHost?>(coach, "host") == null, "coach stop closes listener");
        await Wait(() => Field<ComboBox>(coach, "address").IsEnabled,
            "stopping receiver unlocks address selection");
        Click(coach, "启动局域网接收"); Click(coach, "停止");
        await Wait(() => !Field<bool>(coach, "busy") && Field<CoachHost?>(coach, "host") == null, "stop interrupts coach listener startup");
        game.Close(); coach.Close();
        await Wait(() => !game.IsVisible && !coach.IsVisible, "both clients shut down cleanly");
        var emptyCoach = (Window)Activator.CreateInstance(coachType)!;
        var emptyGame = (Window)Activator.CreateInstance(gameType)!;
        emptyCoach.Show(); emptyGame.Show(); emptyCoach.Close(); emptyGame.Close();
        await Wait(() => !emptyGame.IsVisible && !emptyCoach.IsVisible, "unused clients close without reentrant-close errors");
    }
}
