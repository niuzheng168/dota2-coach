using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed class AdviceOverlay : Window
{
    private readonly Canvas canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Reminder? reminder;
    private long until, lastFrame;
    private IntPtr handle;
    private readonly E2ERun? e2e;
    private bool localBot;
    private string? renderedId;
    private readonly Func<DebugRecorder?>? debug;
    private DebugOverlay? debugReceipt;
    public AdviceOverlay(E2ERun? run = null, Func<DebugRecorder?>? debugSink = null)
    {
        e2e = run; debug = debugSink;
        Title = "双机教练 · 实验提醒";
        WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
        Focusable = false; IsHitTestVisible = false; Content = canvas;
        SourceInitialized += (_, _) => {
            handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x20 | 0x80 | 0x08000000 | 0x80000));
            SetWindowDisplayAffinity(handle, e2e?.CaptureOverlayEvidence == true && localBot ? 0u : 0x11u);
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => {
                if (m is 0x21 or 0x84) { handled = true; return new IntPtr(m == 0x21 ? 3 : -1); }
                return IntPtr.Zero;
            });
        };
        timer.Tick += (_, _) => Draw(); timer.Start();
        Closed += (_, _) => timer.Stop();
    }
    public void FrameArrived(bool isLocalBot = false)
    {
        lastFrame = Environment.TickCount64;
        if (localBot != isLocalBot && handle != IntPtr.Zero)
            SetWindowDisplayAffinity(handle, e2e?.CaptureOverlayEvidence == true && isLocalBot ? 0u : 0x11u);
        localBot = isLocalBot;
    }
    public void Accept(Reminder? value)
    {
        if (value == null) { Clear(); return; }
        long deadline = Environment.TickCount64 + value.RemainingMs;
        until = reminder?.Id == value.Id ? Math.Min(until, deadline) : deadline;
        reminder = value; Draw();
    }
    public void Clear()
    {
        if (renderedId != null) e2e?.Trace("game", "overlay-cleared", new { id = renderedId });
        if (debugReceipt != null) debug?.Invoke()?.Overlay("overlay-cleared", debugReceipt);
        debugReceipt = null;
        renderedId = null; reminder = null; canvas.Children.Clear(); Hide();
    }
    /// <summary>Caption heading: what produced the text and how old the picture behind it is.</summary>
    public static string Label(Reminder r) =>
        r.Kind == "manual-link-test" ? "链路测试 · 非AI" :
        r.Kind is "experimental-bot-advice" or "visible-farming-advice"
            ? "AI打钱路线" + (r.SourceAgeMs is int age && age >= 1500 ? $" · 依据约{Math.Round(age / 1000.0)}秒前画面" : "") + " · 非最优保证"
            : "视觉观察 · 未经核验";
    private void Draw()
    {
        var game = GameWindow.Find(out _);
        if (reminder is not { } r || Environment.TickCount64 >= until ||
            Environment.TickCount64 - lastFrame > Wire.PresenceLifetimeMs ||
            game == null || game.Width != r.SourceWidth || game.Height != r.SourceHeight)
        { Clear(); return; }
        // Never float over other applications: hide while Dota is behind them, keep the reminder until it expires.
        if (!GameWindow.IsForeground(game)) { if (IsVisible) Hide(); return; }
        if (!IsVisible) Show();
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        double unit = DisplayGeometry.OverlayUnit(game.Height, scale);
        if (!SetWindowPos(handle, new IntPtr(-1), game.Left, game.Top, game.Width, game.Height, 0x0010))
        {
            int error = Marshal.GetLastWin32Error();
            e2e?.Trace("game", "overlay-position-failed", new { error });
            debug?.Invoke()?.Error("overlay-position", new System.ComponentModel.Win32Exception(error));
            Clear(); return;
        }
        canvas.Width = game.Width / scale; canvas.Height = game.Height / scale; canvas.Children.Clear();
        var caption = new TextBlock {
            Foreground = Brushes.Gold, Background = new SolidColorBrush(Color.FromArgb(210, 8, 16, 24)),
            FontSize = 28 * unit, Padding = new Thickness(12 * unit), TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Width = Math.Min(900 * unit, canvas.Width * .75),
        };
        caption.Inlines.Add(new Run(Label(r) + "\n") { FontSize = 20 * unit, Foreground = Brushes.Khaki });
        caption.Inlines.Add(new Run(r.Message));
        if (r.NextSteps is { Length: > 0 } steps)
            caption.Inlines.Add(new Run("\n下一步：" + string.Join(" → ", steps.Select((s, i) => $"{"①②③"[i]}{s}"))) {
                FontSize = 22 * unit, Foreground = Brushes.White });
        Canvas.SetLeft(caption, (canvas.Width - caption.Width) / 2); Canvas.SetTop(caption, canvas.Height * .1);
        if (r.ShowSubtitle) canvas.Children.Add(caption);
        if (r.Marker is { } mark)
        {
            var points = (r.Route ?? [mark]).Select(p => new Point(p.X * canvas.Width, p.Y * canvas.Height)).ToArray();
            // Route legs first so the numbered stops stay readable on top of them.
            for (int i = 1; i < points.Length; i++)
                canvas.Children.Add(new Line { X1 = points[i - 1].X, Y1 = points[i - 1].Y, X2 = points[i].X, Y2 = points[i].Y,
                    Stroke = Brushes.Gold, StrokeThickness = 2.5 * unit, StrokeDashArray = new DoubleCollection { 2, 1.5 } });
            for (int i = 0; i < points.Length; i++) {
                double x = points[i].X, y = points[i].Y, size = (i == 0 ? 26 : 20) * unit;
                var ring = new Ellipse { Width = size, Height = size, Stroke = Brushes.Gold, StrokeThickness = 3 * unit,
                    Fill = new SolidColorBrush(Color.FromArgb(150, 8, 16, 24)) };
                Canvas.SetLeft(ring, x - size / 2); Canvas.SetTop(ring, y - size / 2); canvas.Children.Add(ring);
                if (points.Length > 1) {
                    var number = new TextBlock { Text = (i + 1).ToString(), Foreground = Brushes.Gold, FontWeight = FontWeights.Bold,
                        FontSize = 14 * unit, Width = size, TextAlignment = TextAlignment.Center };
                    Canvas.SetLeft(number, x - size / 2); Canvas.SetTop(number, y - 10 * unit); canvas.Children.Add(number);
                }
            }
            var first = points[0];
            var arrow = new Polygon { Fill = Brushes.Gold, Points = new PointCollection {
                new(first.X, first.Y - 15 * unit), new(first.X - 8 * unit, first.Y - 30 * unit), new(first.X + 8 * unit, first.Y - 30 * unit) } };
            canvas.Children.Add(arrow);
        }
        if (renderedId != r.Id)
        {
            renderedId = r.Id;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                if (renderedId != r.Id || !IsVisible || reminder?.Id != r.Id) return;
                GetWindowRect(handle, out var rect);
                debugReceipt = new(r.Id, r.FrameSequence, rect.Right - rect.Left, rect.Bottom - rect.Top,
                    scale, canvas.Children.Count, r.Marker?.X * game.Width, r.Marker?.Y * game.Height);
                debug?.Invoke()?.Overlay("overlay-rendered", debugReceipt);
                e2e?.Trace("game", "overlay-rendered", new { r.Id, r.FrameSequence, r.Kind, r.Message, r.Marker,
                    actualBounds = new { rect.Left, rect.Top, rect.Right, rect.Bottom },
                    canvasWidth = canvas.ActualWidth, canvasHeight = canvas.ActualHeight,
                    dpiScale = scale, elements = canvas.Children.Count,
                    markerPixelX = r.Marker?.X * game.Width, markerPixelY = r.Marker?.Y * game.Height,
                    captureEvidence = e2e.CaptureOverlayEvidence && localBot });
            }));
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
}
