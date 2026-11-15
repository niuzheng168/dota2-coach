using System;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using Screen = System.Windows.Forms.Screen;

namespace DotaCoach.Desktop;

internal sealed class SubtitleWindow : Window
{
    private readonly TextBlock source = Text();
    private readonly TextBlock target = Text();
    private readonly TextBlock speaker = Text();
    private readonly Border panel = new() { CornerRadius = new CornerRadius(12), Padding = new Thickness(18, 10, 18, 12) };
    private readonly DispatcherTimer expiry = new() { Interval = TimeSpan.FromMilliseconds(250) };
    private DateTime deadline;
    private bool enabled = true;
    private int monitor;
    private double top = 5, seconds = 8;
    private IntPtr handle;
    private bool positionQueued;

    public SubtitleWindow()
    {
        Title = "Dota Coach 双语字幕";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        Focusable = false;
        IsHitTestVisible = false;
        var stack = new StackPanel();
        speaker.FontSize = 12;
        speaker.Foreground = Brushes.LightGray;
        source.FontSize = 24;
        target.FontSize = 28;
        source.MaxHeight = source.FontSize * 3.4;
        target.MaxHeight = target.FontSize * 3.4;
        target.FontWeight = FontWeights.SemiBold;
        source.Foreground = Brushes.White;
        target.Foreground = new SolidColorBrush(Color.FromRgb(255, 224, 130));
        stack.Children.Add(speaker); stack.Children.Add(source); stack.Children.Add(target);
        panel.Child = stack;
        panel.VerticalAlignment = VerticalAlignment.Top;
        panel.Visibility = Visibility.Collapsed;
        panel.Background = new SolidColorBrush(Color.FromArgb(90, 0, 0, 0));
        Content = panel;
        panel.SizeChanged += (_, _) => QueuePosition();
        SourceInitialized += (_, _) => {
            handle = new WindowInteropHelper(this).Handle;
            long style = GetWindowLongPtr(handle, -20).ToInt64();
            // WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_LAYERED.
            SetWindowLongPtr(handle, -20, new IntPtr(style | 0x20 | 0x80 | 0x08000000 | 0x80000));
            HwndSource.FromHwnd(handle)?.AddHook(Hook);
            Position();
        };
        expiry.Tick += (_, _) => {
            if (DateTime.UtcNow > deadline) panel.Visibility = Visibility.Collapsed;
        };
        expiry.Start();
        Closed += (_, _) => { expiry.Stop(); handle = IntPtr.Zero; };
    }

    private static TextBlock Text() => new() {
        TextAlignment = TextAlignment.Center, TextWrapping = TextWrapping.Wrap,
        FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI"), Margin = new Thickness(0, 2, 0, 2),
        Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4, ShadowDepth = 1, Opacity = 0.9 },
    };

    private IntPtr Hook(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x21) { handled = true; return new IntPtr(3); } // WM_MOUSEACTIVATE / MA_NOACTIVATE
        if (message == 0x84) { handled = true; return new IntPtr(-1); } // WM_NCHITTEST / HTTRANSPARENT
        if (message is 0x7e or 0x2e0) QueuePosition(); // Display/DPI changed: remeasure on destination monitor.
        return IntPtr.Zero;
    }

    private static double Number(JsonElement message, string key, double fallback, double min, double max) =>
        message.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number &&
            value.TryGetDouble(out var number) && double.IsFinite(number)
            ? Math.Clamp(number, min, max) : fallback;
    private static Brush ColorValue(JsonElement message, string key, Brush fallback)
    {
        if (!message.TryGetProperty(key, out var value) || value.ValueKind != JsonValueKind.String) return fallback;
        var text = value.GetString() ?? "";
        if (text.Length != 7 || text[0] != '#' || !int.TryParse(text.AsSpan(1), System.Globalization.NumberStyles.HexNumber,
                null, out int rgb)) return fallback;
        return new SolidColorBrush(Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb));
    }

    public void Settings(JsonElement message)
    {
        enabled = !message.TryGetProperty("enabled", out var value) || value.ValueKind != JsonValueKind.False;
        source.Foreground = ColorValue(message, "sourceColor", source.Foreground);
        target.Foreground = ColorValue(message, "targetColor", target.Foreground);
        source.Opacity = target.Opacity = speaker.Opacity = Number(message, "opacity", 0.95, 0.15, 1);
        panel.Background = new SolidColorBrush(Color.FromArgb(
            (byte)(255 * Number(message, "backgroundOpacity", 0.35, 0, 0.9)), 0, 0, 0));
        target.FontSize = Number(message, "fontSize", 28, 16, 48);
        source.FontSize = target.FontSize * 0.85;
        source.MaxHeight = source.FontSize * 3.4;
        target.MaxHeight = target.FontSize * 3.4;
        top = Number(message, "top", 5, 0, 100);
        seconds = Number(message, "seconds", 8, 3, 30);
        monitor = (int)Number(message, "monitor", 0, 0, 15);
        if (!enabled) Clear();
        Position();
    }

    public void Caption(string name, string original, string translation)
    {
        if (!enabled) return;
        speaker.Text = name.Length > 80 ? name[..80] : name;
        source.Text = Tail(original);
        target.Text = Tail(translation);
        speaker.Visibility = string.IsNullOrEmpty(speaker.Text) ? Visibility.Collapsed : Visibility.Visible;
        source.Visibility = string.IsNullOrEmpty(source.Text) ? Visibility.Collapsed : Visibility.Visible;
        target.Visibility = string.IsNullOrEmpty(target.Text) ? Visibility.Collapsed : Visibility.Visible;
        deadline = DateTime.UtcNow.AddSeconds(seconds);
        panel.Visibility = Visibility.Visible;
        if (!IsVisible) Show();
        Position(); // TOPMOST + NOACTIVATE, never Activate() or Focus().
    }

    private static string Tail(string text) => text.Length > 220 ? "…" + text[^220..] : text;
    public void Clear()
    {
        source.Text = target.Text = speaker.Text = "";
        panel.Visibility = Visibility.Collapsed;
    }
    private void QueuePosition()
    {
        if (positionQueued || Dispatcher.HasShutdownStarted) return;
        positionQueued = true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(() => {
            positionQueued = false;
            Position();
        }));
    }

    private void Position()
    {
        if (handle == IntPtr.Zero || panel.Visibility != Visibility.Visible) return;
        var screens = Screen.AllScreens;
        if (screens.Length == 0) return;
        var bounds = screens[Math.Min(monitor, screens.Length - 1)].Bounds;
        double dpiScale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        var geometry = OverlayPlacement.Calculate(bounds.X, bounds.Y, bounds.Width, bounds.Height, 1, dpiScale, top);
        // WPF measures in DIPs, while the native placement below is in physical pixels.
        // Remeasure when text wraps, the font changes, or WM_DPICHANGED moves us to another monitor.
        panel.Measure(new Size(geometry.Width / dpiScale, double.PositiveInfinity));
        geometry = OverlayPlacement.Calculate(bounds.X, bounds.Y, bounds.Width, bounds.Height,
            panel.DesiredSize.Height, dpiScale, top);
        // SetWindowPos uses physical pixels under PerMonitorV2, including negative monitor coordinates.
        SetWindowPos(handle, new IntPtr(-1), geometry.Left, geometry.Top, geometry.Width, geometry.Height,
            0x0010); // SWP_NOACTIVATE: never focus or activate the overlay.
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr hwnd);
}
