using System;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed class AdviceOverlay : Window
{
    private readonly Canvas canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Reminder? reminder;
    private long until, lastFrame, previewUntil, enemiesAt;
    private EnemyBoard? enemies;
    // What the canvas shows. The window spans the whole game and is redrawn only when this changes: the enemy
    // panel stays up for the whole match, and repainting a full-screen layered window every tick costs frames.
    private string? drawn;
    /// <summary>The player's local subtitle and enemy panel style; applied from the next redraw on.</summary>
    public GameOverlaySettings Settings { get; set; } = new();
    public IconCache Icons { get; } = new(System.IO.Path.Combine(AppContext.BaseDirectory, "icons"));
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
    /// <summary>The coach's enemy panel, renewed about once a second; null hides it.</summary>
    public void Enemies(EnemyBoard? board)
    {
        enemies = board; enemiesAt = Environment.TickCount64;
    }
    /// <summary>Shows a sample subtitle and enemy panel over Dota for a while (live ones still win), to tune the style.</summary>
    public void Preview(int ms)
    {
        previewUntil = Environment.TickCount64 + ms; Draw();
    }
    /// <summary>Withdraws the reminder; the enemy panel stays. Called on every empty poll, so a no-op when there is none.</summary>
    public void Clear()
    {
        if (reminder == null && renderedId == null) return;
        DropReminder(); Draw();
    }
    private void DropReminder()
    {
        if (renderedId != null) e2e?.Trace("game", "overlay-cleared", new { id = renderedId });
        if (debugReceipt != null) debug?.Invoke()?.Overlay("overlay-cleared", debugReceipt);
        debugReceipt = null; renderedId = null; reminder = null;
    }
    private void Blank()
    {
        drawn = null; canvas.Children.Clear();
        if (IsVisible) Hide();
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
        long now = Environment.TickCount64;
        if (reminder is { } live && (now >= until || now - lastFrame > Wire.PresenceLifetimeMs ||
            game == null || game.Width != live.SourceWidth || game.Height != live.SourceHeight)) DropReminder();
        bool previewing = now < previewUntil, sample = reminder == null;
        var r = reminder ?? (previewing && game != null ? new Reminder("caption-preview", "", 0, "local-vision-observation",
            "预览：剃刀 离开视野 · 最后在中路", null, game.Width, game.Height, 0) : null);
        // A panel the coach stopped renewing (disconnected, scanner stopped) disappears within 3 s. Switched off, no
        // sample either: the switch shows its effect at once.
        var board = !Settings.ShowEnemies ? null : (now - enemiesAt <= 3000 ? enemies : null) ?? (previewing ? SampleBoard : null);
        if (game == null || r == null && board == null) { Blank(); return; }
        // Never float over other applications: hide while Dota is behind them, keep the reminder until it expires.
        // A preview also shows while this client is in front, so the style can be tuned from a second screen.
        if (!GameWindow.IsForeground(game) && !(previewing && OwnWindowInFront())) { if (IsVisible) Hide(); return; }
        if (!IsVisible) Show();
        double scale = Math.Max(96, GetDpiForWindow(handle)) / 96.0;
        double unit = DisplayGeometry.OverlayUnit(game.Height, scale);
        if (!SetWindowPos(handle, new IntPtr(-1), game.Left, game.Top, game.Width, game.Height, 0x0010))
        {
            int error = Marshal.GetLastWin32Error();
            e2e?.Trace("game", "overlay-position-failed", new { error });
            debug?.Invoke()?.Error("overlay-position", new System.ComponentModel.Win32Exception(error));
            DropReminder(); Blank(); return;
        }
        canvas.Width = game.Width / scale; canvas.Height = game.Height / scale;
        // The sample is shown whatever kinds are switched off: it previews the style, not a reminder.
        var style = sample ? Settings with { ShowAlerts = true } : Settings;
        string content = string.Join("|", r?.Id, r == null ? "" : Label(r), style, Describe(board), Icons.Count, canvas.Width, canvas.Height, unit);
        if (content != drawn) { Compose(canvas, r, style, unit, board, key => Icons[key]); drawn = content; }
        if (sample || r == null) return; // A preview leaves no Debug or E2E receipt.
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
    /// <summary>Draws a reminder and the enemy panel onto a canvas already sized to the game area, in WPF units.</summary>
    internal static void Compose(Canvas canvas, Reminder? r, GameOverlaySettings style, double unit,
        EnemyBoard? board = null, Func<string, ImageSource?>? icon = null)
    {
        canvas.Children.Clear();
        if (board != null) DrawEnemies(canvas, board, style, unit, icon ?? (_ => null));
        if (r == null) return;
        if (r.ShowSubtitle && style.Shows(r.Kind))
        {
            double u = unit * style.Size / 100;
            var text = new TextBlock {
                Foreground = Brush(style.TextColor), FontSize = 28 * u, TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center, Opacity = style.TextOpacity / 100.0,
                // Keeps the text readable when the box is made see-through.
                Effect = new DropShadowEffect { Color = Colors.Black, BlurRadius = 4 * u, ShadowDepth = 1.5 * u, Opacity = .9 },
            };
            if (style.ShowTitle) text.Inlines.Add(new Run(Label(r) + "\n") { FontSize = 20 * u, Foreground = Brush(style.TitleColor) });
            text.Inlines.Add(new Run(r.Message));
            if (r.NextSteps is { Length: > 0 } steps)
                text.Inlines.Add(new Run("\n下一步：" + string.Join(" → ", steps.Select((s, i) => $"{"①②③"[i]}{s}"))) {
                    FontSize = 22 * u, Foreground = Brushes.White });
            var box = new Border { Child = text, Padding = new Thickness(12 * u), CornerRadius = new CornerRadius(6 * u),
                Background = Brush(style.BackgroundColor, style.BackgroundOpacity), MaxWidth = canvas.Width * style.Width / 100 };
            box.Measure(new Size(box.MaxWidth, double.PositiveInfinity));
            var (left, top) = style.Place(canvas.Width, canvas.Height, box.DesiredSize.Width, box.DesiredSize.Height);
            Canvas.SetLeft(box, left); Canvas.SetTop(box, top); canvas.Children.Add(box);
        }
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
    }
    private static readonly EnemyBoard SampleBoard = new([
        new("npc_dota_hero_razor", 12, ["item_power_treads", "item_black_king_bar", "item_blink", "item_magic_wand", null, null], null, 2),
        new("npc_dota_hero_juggernaut", 14, ["item_phase_boots", "item_bfury", "item_manta", null, null, null], null, 35),
        new("npc_dota_hero_lion", 9, ["item_tranquil_boots", "item_blink", "item_ward_observer", null, null, null], null, 95),
        new("npc_dota_hero_pudge", null, null, null, null)]);
    /// <summary>
    /// The enemy panel at the game's left edge, one row per enemy: head icon, level, the six item slots and the
    /// neutral item, and how long ago they were read (shown from 10 s; rows a minute old are dimmed).
    /// </summary>
    private static void DrawEnemies(Canvas canvas, EnemyBoard board, GameOverlaySettings style, double unit, Func<string, ImageSource?> icon)
    {
        double u = unit * style.EnemySize / 100, row = 14 * u, slot = row * 88 / 64;
        var panel = new StackPanel { Opacity = style.EnemyOpacity / 100.0 };
        RenderOptions.SetBitmapScalingMode(panel, BitmapScalingMode.HighQuality);
        Border Slot(string? name, double width) => new() {
            Width = width, Height = row, Margin = new Thickness(.5 * u, 0, .5 * u, 0),
            Background = new SolidColorBrush(name == null ? Color.FromArgb(110, 20, 24, 28) : Color.FromArgb(200, 80, 80, 80)),
            Child = name != null && icon(name) is { } image ? new Image { Source = image, Stretch = Stretch.UniformToFill } : null,
        };
        TextBlock Text(string text, double size, Brush color, bool bold = false) => new() {
            Text = text, FontSize = size, Foreground = color, FontWeight = bold ? FontWeights.Bold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(2 * u, 0, 2 * u, 0),
        };
        foreach (var hero in board.Heroes)
        {
            // A dark backing of its own: on the game machine (4K) bright HUD text under a lighter one made rows hard to read.
            var line = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 2 * u),
                Background = new SolidColorBrush(Color.FromArgb(200, 0, 0, 0)) };
            line.Children.Add(Slot(hero.Hero, row));
            var level = Text(hero.Level?.ToString() ?? "?", 10 * u, Brushes.White, bold: true);
            level.MinWidth = 11 * u; level.TextAlignment = TextAlignment.Center;
            line.Children.Add(level);
            if (hero.Items is { } items)
            {
                foreach (var name in items) line.Children.Add(Slot(name, slot));
                var neutral = Slot(hero.Neutral, slot);
                neutral.Margin = new Thickness(3 * u, 0, .5 * u, 0);
                line.Children.Add(neutral);
            }
            string age = Age(hero.Age);
            if (age.Length > 0) line.Children.Add(Text(age, 9 * u, Brushes.LightGray));
            if (hero.Age >= 60) line.Opacity = .7;
            panel.Children.Add(line);
        }
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        Canvas.SetLeft(panel, 4 * unit); Canvas.SetTop(panel, style.EnemyTop(canvas.Height, panel.DesiredSize.Height));
        canvas.Children.Add(panel);
    }
    /// <summary>How old a row's reading is, in 5-second steps (so the panel repaints at most every few seconds).</summary>
    private static string Age(double? seconds) => seconds switch {
        null => "未见", < 10 => "", < 60 and var s => $"{(int)(s / 5) * 5}秒前", var s => $"{(int)(s / 60)}分前" };
    private static string Describe(EnemyBoard? board) => board == null ? "" : string.Join(";", board.Heroes.Select(h =>
        $"{h.Hero},{h.Level},{string.Join(",", h.Items ?? [])},{h.Neutral},{Age(h.Age)},{h.Age >= 60}"));
    private static SolidColorBrush Brush(string hex, int opacity = 100)
    {
        var (red, green, blue) = GameOverlaySettings.Rgb(hex);
        return new SolidColorBrush(Color.FromArgb((byte)Math.Round(opacity * 2.55), red, green, blue));
    }
    private static bool OwnWindowInFront() =>
        GetWindowThreadProcessId(GetForegroundWindow(), out uint process) != 0 && process == (uint)Environment.ProcessId;
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr h, out uint process);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr h, out RECT rect);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr h, int i);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr h, int i, IntPtr v);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(IntPtr h, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr h);
}
