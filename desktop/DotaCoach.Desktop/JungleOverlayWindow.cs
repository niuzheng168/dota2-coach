using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;

namespace DotaCoach.Desktop;

internal sealed class JungleOverlayWindow : Window
{
    private readonly Canvas canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private JsonElement? state;
    private long receivedAt;
    private IntPtr handle;
    public MinimapCalibration? Calibration { get; private set; }
    public bool Enabled { get; private set; }
    private bool preview;

    public JungleOverlayWindow()
    {
        Title = "Dota Coach 刷野路线（实验）";
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true; Background = Brushes.Transparent; ShowInTaskbar = false;
        ShowActivated = false; Topmost = true; Focusable = false; IsHitTestVisible = false;
        Content = canvas;
        SourceInitialized += (_, _) => {
            handle = new WindowInteropHelper(this).Handle;
            SetWindowLongPtr(handle, -20, new IntPtr(GetWindowLongPtr(handle, -20).ToInt64() | 0x20 | 0x80 | 0x08000000 | 0x80000));
            SetWindowDisplayAffinity(handle, 0x11); // WDA_EXCLUDEFROMCAPTURE, best effort.
            HwndSource.FromHwnd(handle)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => {
                if (m is 0x21 or 0x84) { handled = true; return new IntPtr(m == 0x21 ? 3 : -1); }
                return IntPtr.Zero;
            });
        };
        timer.Tick += (_, _) => Draw(); timer.Start();
        Closed += (_, _) => timer.Stop();
    }
    public void Configure(JsonElement data, int width, int height, bool previewOnly = false)
    {
        Calibration = MinimapCalibration.Parse(data, width, height, !previewOnly); preview = previewOnly; Enabled = true; Draw();
    }
    public void Stop() { Enabled = false; canvas.Children.Clear(); Hide(); }
    public void Clear() { state = null; canvas.Children.Clear(); Hide(); }
    public void Accept(JsonElement value)
    {
        state = value.Clone(); receivedAt = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(); Draw();
    }
    private void Draw()
    {
        canvas.Children.Clear();
        var game = GameWindow.Foreground(); var c = Calibration;
        if (!Enabled || c == null || game == null || state is not JsonElement s ||
            !MinimapCalibration.Fresh(s, receivedAt, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()) ||
            c.ClientWidth != game.Width || c.ClientHeight != game.Height) { Hide(); return; }
        if (!IsVisible) Show();
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        SetWindowPos(handle, new IntPtr(-1), game.Left, game.Top, game.Width, game.Height, 0x0010);
        canvas.Width = game.Width / scale; canvas.Height = game.Height / scale;
        canvas.Clip = new RectangleGeometry(new Rect(c.Left / scale, Math.Max(0, c.Top - 48) / scale, c.Width / scale, (c.Height + 48) / scale));
        try
        {
            var p = s.GetProperty("player"); double clock = MinimapCalibration.Number(p, "clock");
            var steps = s.GetProperty("route").GetProperty("steps").EnumerateArray()
                .Where(step => MinimapCalibration.Number(step, "finish") > clock).Take(3).ToArray();
            int number = 0;
            foreach (var step in steps)
            {
                if (preview) break;
                var line = new Polyline { Stroke = number == 0 ? Brushes.Gold : Brushes.PaleGoldenrod, StrokeThickness = number == 0 ? 3 : 1.5, Opacity = .9 };
                foreach (var point in step.GetProperty("path").EnumerateArray().Take(4096))
                {
                    var xy = c.Project(MinimapCalibration.Number(point, "x"), MinimapCalibration.Number(point, "y"));
                    if (xy is null) { line.Points.Clear(); break; }
                    line.Points.Add(new Point(xy.Value.X / scale, xy.Value.Y / scale));
                }
                canvas.Children.Add(line);
                var position = step.GetProperty("position");
                var mark = c.Project(MinimapCalibration.Number(position, "x"), MinimapCalibration.Number(position, "y"));
                number++;
                if (mark is not null)
                {
                    var label = new TextBlock { Text = number.ToString(), FontSize = 17, FontWeight = FontWeights.Bold, Foreground = Brushes.Gold, Background = Brushes.Black };
                    Canvas.SetLeft(label, mark.Value.X / scale); Canvas.SetTop(label, mark.Value.Y / scale - 18); canvas.Children.Add(label);
                }
            }
            var self = p.GetProperty("position");
            var dot = c.Project(MinimapCalibration.Number(self, "x"), MinimapCalibration.Number(self, "y"));
            if (dot is not null)
            {
                var marker = new Ellipse { Width = 9, Height = 9, Stroke = Brushes.Black, Fill = Brushes.White };
                Canvas.SetLeft(marker, dot.Value.X / scale - 4.5); Canvas.SetTop(marker, dot.Value.Y / scale - 4.5); canvas.Children.Add(marker);
            }
            var text = new TextBlock { Text = preview ? "标定预览 · 仅白点\n需在多个位置核对" : "实验路线 · 非最优保证\n下一步按数字执行", Foreground = Brushes.Gold, Background = Brushes.Black, FontSize = 12 };
            Canvas.SetLeft(text, c.Left / scale); Canvas.SetTop(text, Math.Max(0, c.Top - 42) / scale); canvas.Children.Add(text);
        }
        catch { canvas.Children.Clear(); Hide(); }
    }
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int w, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
}
