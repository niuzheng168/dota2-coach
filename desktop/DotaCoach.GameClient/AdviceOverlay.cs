using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
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
    public AdviceOverlay(E2ERun? run = null)
    {
        e2e = run;
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
        renderedId = null; reminder = null; canvas.Children.Clear(); Hide();
    }
    private void Draw()
    {
        var game = GameWindow.Foreground();
        if (reminder is not { } r || Environment.TickCount64 >= until ||
            Environment.TickCount64 - lastFrame > Wire.PresenceLifetimeMs ||
            game == null || game.Width != r.SourceWidth || game.Height != r.SourceHeight)
        { Clear(); return; }
        if (!IsVisible) Show();
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        if (!SetWindowPos(handle, new IntPtr(-1), game.Left, game.Top, game.Width, game.Height, 0x0010))
        { e2e?.Trace("game", "overlay-position-failed", new { error = Marshal.GetLastWin32Error() }); Clear(); return; }
        canvas.Width = game.Width / scale; canvas.Height = game.Height / scale; canvas.Children.Clear();
        var caption = new TextBlock {
            Text = (r.Kind == "manual-link-test" ? "人工联调 · 非AI路线\n" :
                r.Kind == "experimental-bot-advice" ? "AI实验建议 · 非最优保证\n" : "视觉观察 · 未经核验\n") + r.Message,
            Foreground = Brushes.Gold, Background = new SolidColorBrush(Color.FromArgb(210, 8, 16, 24)),
            FontSize = 28 / scale, Padding = new Thickness(12 / scale), TextWrapping = TextWrapping.Wrap,
            TextAlignment = TextAlignment.Center, Width = Math.Min(900 / scale, canvas.Width * .75),
        };
        Canvas.SetLeft(caption, (canvas.Width - caption.Width) / 2); Canvas.SetTop(caption, canvas.Height * .1);
        canvas.Children.Add(caption);
        if (r.Marker is { } mark)
        {
            double x = mark.X * canvas.Width, y = mark.Y * canvas.Height;
            var ring = new Ellipse { Width = 26 / scale, Height = 26 / scale, Stroke = Brushes.Gold, StrokeThickness = 3 / scale };
            Canvas.SetLeft(ring, x - 13 / scale); Canvas.SetTop(ring, y - 13 / scale); canvas.Children.Add(ring);
            var arrow = new Polygon { Fill = Brushes.Gold, Points = new PointCollection {
                new(x, y - 15 / scale), new(x - 8 / scale, y - 30 / scale), new(x + 8 / scale, y - 30 / scale) } };
            canvas.Children.Add(arrow);
        }
        if (renderedId != r.Id)
        {
            renderedId = r.Id;
            _ = Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle, new Action(() => {
                if (renderedId != r.Id || !IsVisible || reminder?.Id != r.Id) return;
                GetWindowRect(handle, out var rect);
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
