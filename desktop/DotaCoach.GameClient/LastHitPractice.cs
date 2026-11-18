using System;
using System.Collections.Generic;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

/// <summary>Separate from the pairing/video stream. WGC, recognition and drawing all stay on the
/// game machine. No desktop-copy fallback, game commands, coach messages, logging of frames or cloud.</summary>
internal sealed class LastHitPractice : IAsyncDisposable
{
    private sealed record Picture(GameSurface? Game, int Width, int Height, long At, List<DrillMark> Marks, string Status);
    private readonly CancellationTokenSource stop = new();
    private readonly Func<bool> allowed;
    private readonly double impact;
    private readonly Action<string> status;
    private readonly PracticeOverlay overlay = new();
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly Task worker;
    private Picture? latest, drawn;
    private string? lastStatus;

    public LastHitPractice(Func<bool> allowed, double impact, Action<string> status)
    {
        this.allowed = allowed; this.impact = impact; this.status = status;
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Tick += Tick; timer.Start();
    }
    private void Run()
    {
        while (!stop.IsCancellationRequested) {
            try {
                var game = allowed() ? GameWindow.Foreground() : null;
                if (game == null) { Idle("等待前台露娜试玩：一级空装、白天、未暂停，前 7 分半。"); Pause(150); continue; }
                var size = VideoPixels.SizeFor(game.Width, game.Height, 1920, 1080);
                var drill = new LaneDrill(impact);
                using var source = new WindowVideoSource(game, size.Width, size.Height, 25);
                while (!stop.IsCancellationRequested && allowed() && GameWindow.Foreground() == game) {
                    long at = Environment.TickCount64;
                    var pixels = source.Grab();
                    if (source.Ended) break;
                    if (pixels != null) {
                        var bars = CreepBars.Read(pixels);
                        var marks = drill.Update(bars, at / 1000d);
                        Volatile.Write(ref latest, new(game, size.Width, size.Height, at, marks, $"本机识别：{bars.Count} 根兵形血条，{marks.Count} 条提示"));
                    }
                    Pause((int)Math.Clamp(40 - (Environment.TickCount64 - at), 1, 40));
                }
                Idle("已暂停，等待新的试玩画面。");
            } catch (Exception ex) {
                Idle($"窗口采集不可用（{ex.GetType().Name}），请使用无边框或窗口模式。"); Pause(750);
            }
        }
    }
    private void Pause(int ms) => stop.Token.WaitHandle.WaitOne(ms);
    private void Idle(string text) => Volatile.Write(ref latest, new(null, 0, 0, 0, [], text));
    private void Tick(object? sender, EventArgs e)
    {
        var p = Volatile.Read(ref latest);
        string text = p?.Status ?? "等待试玩画面…";
        if (p?.Game != null && Environment.TickCount64 - p.At > 180) text = "画面已过期，标记已清除。";
        if (text != lastStatus) { status(text); lastStatus = text; }
        if (p?.Game is not { } game || p.Marks.Count == 0 || !allowed() || Environment.TickCount64 - p.At > 180 ||
            GameWindow.Foreground() != game) { overlay.HideMarks(); drawn = null; return; }
        if (drawn == p) return;
        overlay.Paint(game, p.Width, p.Height, p.Marks); drawn = p;
    }
    public async ValueTask DisposeAsync()
    {
        timer.Stop(); timer.Tick -= Tick; overlay.Close(); stop.Cancel();
        try { await worker; } finally { stop.Dispose(); }
    }

    private sealed class PracticeOverlay : Window
    {
        private readonly Surface surface = new();
        private IntPtr hwnd;
        public PracticeOverlay()
        {
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            Focusable = false; IsHitTestVisible = false; Content = surface;
            SourceInitialized += (_, _) => {
                hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 0x080000A0);
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => {
                    if (m != 0x84 && m != 0x21) return IntPtr.Zero;
                    handled = true; return m == 0x84 ? -1 : 3;
                });
            };
        }
        public void HideMarks() { if (IsVisible) Hide(); }
        public void Paint(GameSurface g, int w, int h, List<DrillMark> marks)
        {
            if (!IsVisible) Show();
            if (!SetWindowPos(hwnd, -1, g.Left, g.Top, g.Width, g.Height, 0x0010)) { HideMarks(); return; }
            surface.Draw(w, h, marks);
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags);
    }

    private sealed class Surface : FrameworkElement
    {
        private List<DrillMark> marks = [];
        private int w = 1, h = 1;
        private readonly Typeface face = new("Microsoft YaHei UI");
        public void Draw(int width, int height, List<DrillMark> values)
        { w = width; h = height; marks = values; InvalidateVisual(); }
        protected override void OnRender(DrawingContext dc)
        {
            double sx = ActualWidth / w, sy = ActualHeight / h;
            if (sx <= 0 || sy <= 0) return;
            foreach (var m in marks) {
                var b = m.Bar; var p = m.Plan;
                (Brush color, string label) = p.Action switch {
                    HitAction.Deny => (Brushes.Cyan, "反补"), HitAction.Prepare => (Brushes.Orange, $"垫 {p.PrepHits} 刀 · 塔 {p.TowerHits} 下"),
                    HitAction.Wait => (Brushes.LightSkyBlue, p.PrepHits > 0 ? $"等普攻 · 垫 {p.PrepHits} 刀" : p.TowerHits > 0 ? $"等塔 {p.TowerHits} 下" : "等普攻收"),
                    _ => (Brushes.Gold, p.TowerHits > 0 ? "出手 · 塔后收" : "正补"),
                };
                var rect = new Rect((b.X - 2)*sx, (b.Y - 2)*sy, (b.Width + 4)*sx, (b.Height + 4)*sy);
                dc.DrawRectangle(null, new Pen(color, Math.Max(1, 2*sx)), rect);
                var text = new FormattedText(label, CultureInfo.CurrentUICulture, FlowDirection.LeftToRight,
                    face, Math.Max(9, b.Width*.18*sy), color, VisualTreeHelper.GetDpi(this).PixelsPerDip);
                var origin = new Point(rect.Left, rect.Top - text.Height - 1);
                dc.DrawRectangle(Brushes.Black, null, new Rect(origin, new Size(text.Width + 2, text.Height)));
                dc.DrawText(text, origin);
            }
        }
    }
}
