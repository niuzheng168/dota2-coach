using System;
using System.Collections.Generic;
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

internal sealed partial class AdviceOverlay : Window
{
    private readonly Canvas canvas = new() { IsHitTestVisible = false };
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(150) };
    private Reminder? reminder;
    private long until, lastFrame, previewUntil;
    private EnemyBoard? enemies;
    internal Func<EnemyBoard, int, int, EnemyBoard?>? BoardForDisplay { get; set; }
    private bool boardSession;
    // Only a reminder needs the full game area. The small equipment/ward HWNDs have independent lifetimes.
    private string? drawn;
    /// <summary>The player's local subtitle and enemy panel style; applied from the next redraw on.</summary>
    public GameOverlaySettings Settings { get; set; } = new();
    /// <summary>Game-side GSI/session guard, rechecked even when the visual is cached. No guard, no live minute route.</summary>
    public Func<Reminder, bool>? MinuteRouteValid { get; set; }
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
            Passive(handle, e2e?.CaptureOverlayEvidence == true && localBot ? 0u : 0x11u);
        };
        timer.Tick += (_, _) => Draw(); timer.Start();
        Closed += (_, _) => { timer.Stop(); equipmentWindow?.Close(); wardWindow?.Close(); };
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
    /// <summary>The coach's last-known enemy panel, renewed every poll and shown until the next one; null hides it.</summary>
    public void Enemies(EnemyBoard? board)
    {
        enemies = board;
        if (board == null) HideBoards();
    }
    internal void SetBoardSession(bool active)
    {
        boardSession = active; previewUntil = 0; Enemies(null);
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
        if (drawn != null) { drawn = null; canvas.Children.Clear(); }
        if (IsVisible) Hide();
    }
    /// <summary>Caption heading: what produced the text and how old the picture behind it is.</summary>
    public static string Label(Reminder r) =>
        r.Kind == "manual-link-test" ? "链路测试 · 非AI" :
        r.Kind == MinuteFarmContext.Kind ? (r.FarmMinute is int m ? $"{m}:00" : "本分钟") + " · 打钱候选点" :
        r.Kind is "experimental-bot-advice" or "visible-farming-advice"
            ? "AI打钱路线" + (r.SourceAgeMs is int age && age >= 1500 ? $" · 依据约{Math.Round(age / 1000.0)}秒前画面" : "") + " · 非最优保证"
            : "视觉观察 · 未经核验";
    private void Draw()
    {
        var game = GameWindow.Find(out _);
        long now = Environment.TickCount64;
        if (reminder is { } live && (now >= until || now - lastFrame > Wire.PresenceLifetimeMs ||
            game == null || game.Width != live.SourceWidth || game.Height != live.SourceHeight ||
            !AuthorizedForDraw(live, MinuteRouteValid))) DropReminder();
        bool previewing = now < previewUntil, sample = reminder == null;
        var r = reminder ?? (previewing && game != null ? new Reminder("caption-preview", "", 0, "local-vision-observation",
            "预览：剃刀 离开视野 · 最后在中路", null, game.Width, game.Height, 0) : null);
        var liveBoard = enemies;
        bool foreground = game != null && GameWindow.IsForeground(game);
        if (game == null || !foreground && !(previewing && OwnWindowInFront())) { HideBoards(); Blank(); return; }
        // The manual, offline STYLE preview never supplies a ward. No synthetic rows during a real session;
        // no live board or route over a different foreground app, even while the settings preview is open.
        var board = foreground ? liveBoard : null;
        if (board?.LaneMap != null)
            board = BoardForDisplay != null ? BoardForDisplay(board, game.Width, game.Height) : board with { LaneMap = null };
        if (!boardSession && previewing && board == null) board = SampleBoard;
        DrawBoards(game, board);
        if (!foreground && !sample) r = null; // Only the labelled style sample may appear with the settings window in front.
        if (r == null) { Blank(); return; }
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
        string content = string.Join("|", r.Id, Label(r), style, canvas.Width, canvas.Height, unit);
        if (content != drawn) { Compose(canvas, r, style, unit); drawn = content; }
        if (sample) return; // A preview leaves no Debug or E2E receipt.
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
    /// <summary>Compose the same layers off screen. Source sizes are physical client pixels, canvas sizes are DIPs.</summary>
    internal static void Compose(Canvas canvas, Reminder? r, GameOverlaySettings style, double unit,
        EnemyBoard? board = null, Func<string, ImageSource?>? icon = null, (int Width, int Height)? viewport = null)
    {
        canvas.Children.Clear();
        if (board != null && Wire.ValidBoard(board)) {
            if (style.ShowEnemies && board.Heroes.Length > 0) {
                var panel = EnemyPanel(board, style, unit, icon ?? (_ => null));
                Canvas.SetLeft(panel, 4 * unit); Canvas.SetTop(panel, style.EnemyTop(canvas.Height, panel.DesiredSize.Height));
                canvas.Children.Add(panel);
            }
            var (w, h) = viewport ?? (r?.SourceWidth ?? 0, r?.SourceHeight ?? 0);
            if (MapParts(board, style, w, h) is { } selected && WardLayout(selected, w, h, style) is { } layout) {
                var layer = WardLayer(selected, layout.Size * canvas.Width / w, layout.Size * canvas.Height / h, unit, icon);
                Canvas.SetLeft(layer, layout.Left * canvas.Width / w); Canvas.SetTop(layer, layout.Top * canvas.Height / h);
                canvas.Children.Add(layer);
            }
        }
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
        if (r.Kind == MinuteFarmContext.Kind) { DrawMinuteRoute(canvas, r, unit); return; }
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

    internal static bool AuthorizedForDraw(Reminder r, Func<Reminder, bool>? minuteValid) =>
        r.Kind != MinuteFarmContext.Kind || minuteValid?.Invoke(r) == true;
    // These are the explicit condition suffixes in NextSteps, not observations of creeps. A missing or
    // unfamiliar step gets a neutral pin; in particular the last stop isn't necessarily a lane return.
    private const string Camp = "有野再打", Lane = "有线再收", UnknownStop = "待确认";
    private static string Condition(string? step) => step?.EndsWith(Camp, StringComparison.Ordinal) == true ? Camp :
        step?.EndsWith(Lane, StringComparison.Ordinal) == true ? Lane : UnknownStop;
    private static Brush PinColor(string condition) => condition switch {
        Camp => Brushes.Gold, Lane => Brushes.PaleTurquoise, _ => Brushes.Gainsboro };

    private static void DrawMinuteRoute(Canvas canvas, Reminder r, double unit)
    {
        if (r.Minimap is not { } layout || !layout.IsValidFor(r.SourceWidth, r.SourceHeight) ||
            r.Marker is not { } first || !Wire.ValidRoute(r.Route, first)) return;
        var route = r.Route ?? [first];
        if (!route.All(layout.Contains)) return; // Never guess a HUD corner or draw a partial, renumbered route.

        double sx = canvas.Width / r.SourceWidth, sy = canvas.Height / r.SourceHeight;
        double left = layout.Left * sx, top = layout.Top * sy;
        var map = new Canvas { Width = layout.Size * sx, Height = layout.Size * sy, ClipToBounds = true, IsHitTestVisible = false };
        Canvas.SetLeft(map, left); Canvas.SetTop(map, top); canvas.Children.Add(map);
        var points = route.Select(p => new Point(p.X * canvas.Width - left, p.Y * canvas.Height - top)).ToArray();
        var conditions = points.Select((_, i) => Condition(r.NextSteps is { } steps && steps.Length == points.Length ? steps[i] : null)).ToArray();
        double u = Math.Min(unit, Math.Min(map.Width, map.Height) / 88);
        var dark = Brush("#081018", 94);
        double radius = 2.5 * u;
        var anchors = points.Select(p => new Rect(p.X - radius, p.Y - radius, 2 * radius, 2 * radius)).ToArray();
        var badges = new List<Rect>();
        void Place(FrameworkElement element, Rect box) {
            Canvas.SetLeft(element, box.X); Canvas.SetTop(element, box.Y); map.Children.Add(element);
        }
        static double Overlap(Rect a, Rect b) { var hit = Rect.Intersect(a, b); return hit.IsEmpty ? 0 : hit.Width * hit.Height; }

        for (int i = 0; i < points.Length; i++) {
            var accent = PinColor(conditions[i]);
            var p = points[i];
            // Only the number can move: its pip stays at the exact calibrated destination. Choose a
            // neighbouring badge position away from the other numbers and pips, including at HUD edges.
            double size = (i == 0 ? 17 : 15) * u;
            var locations = from extra in new[] { 0d, size / 2, size } let offset = size / 2 + radius + extra
                from dx in new[] { 0, 1, -1 } from dy in new[] { -1, 0, 1 } where dx != 0 || dy != 0
                select new Rect(Math.Clamp(p.X + dx * offset - size / 2, 0, map.Width - size),
                    Math.Clamp(p.Y + dy * offset - size / 2, 0, map.Height - size), size, size);
            var at = locations.OrderBy(b => badges.Sum(other => Overlap(Rect.Inflate(other, u, u), b)))
                .ThenBy(b => anchors.Sum(dot => Overlap(dot, b)))
                .ThenBy(b => (new Point(b.X + size / 2, b.Y + size / 2) - p).LengthSquared).First();
            badges.Add(at);
            // In a crowd the badge may not touch its pip. Give just that badge a short tapered tip to its
            // own coordinate. No geometry is ever constructed from one destination to the next.
            var edge = new Point(Math.Clamp(p.X, at.Left, at.Right), Math.Clamp(p.Y, at.Top, at.Bottom));
            if ((edge - p).Length > radius * 1.5) {
                var center = new Point(at.X + size / 2, at.Y + size / 2);
                var v = center - p; v.Normalize();
                var normal = new Vector(-v.Y, v.X) * (size / 3);
                var tip = new Polygon { Fill = accent, Points = new() { p, center + normal, center - normal } };
                Panel.SetZIndex(tip, -1); map.Children.Add(tip);
            }
            Place(new Ellipse { Width = radius * 2, Height = radius * 2, Fill = accent, Stroke = dark, StrokeThickness = u }, anchors[i]);
            Place(new Border {
                Width = size, Height = size, CornerRadius = new(conditions[i] == Lane ? 2 * u : size / 2),
                BorderBrush = accent, BorderThickness = new(1.3 * u), Background = i == 0 ? accent : dark,
                Child = new TextBlock { Text = (i + 1).ToString(), FontSize = (i == 0 ? 12 : 11) * u, FontWeight = FontWeights.Bold,
                    Foreground = i == 0 ? dark : accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            }, at);
        }

        // One key, not a caption (or three landmark names) across the playfield. Keep whole groups together
        // on narrow custom maps. Match numbers as well as colours, so reading the key doesn't rely on colour.
        var groups = new WrapPanel();
        void Token(string text, Brush ink) => groups.Children.Add(new TextBlock { Text = text, Foreground = ink,
            FontSize = 11 * u, FontWeight = FontWeights.SemiBold, Margin = new Thickness(2 * u, 0, 2 * u, 0) });
        if (r.FarmMinute is int minute) Token($"{minute}′", Brushes.White);
        foreach (var group in Enumerable.Range(0, points.Length).GroupBy(i => conditions[i]))
            Token(string.Join("·", group.Select(i => i + 1)) + " " + group.Key, PinColor(group.Key));
        double pad = 2 * u;
        var key = new Border { Child = groups, Background = dark, CornerRadius = new(3 * u), Padding = new Thickness(u),
            MaxWidth = map.Width - 2 * pad };
        var ink = badges.Concat(anchors).ToArray();
        bool PlaceKey() {
            key.Measure(new Size(key.MaxWidth, double.PositiveInfinity));
            double farX = map.Width - key.DesiredSize.Width - pad, bottom = map.Height - key.DesiredSize.Height - pad;
            var edges = layout.Left < r.SourceWidth / 2.0 ? new[] { pad, farX } : [farX, pad];
            foreach (double y in new[] { pad, bottom }.Concat(ink.Select(b => b.Bottom + pad))) {
                if (y < pad || y > bottom) continue;
                foreach (double x in edges) {
                    var box = new Rect(new Point(x, y), key.DesiredSize);
                    if (ink.Any(b => Rect.Inflate(b, u, u).IntersectsWith(box))) continue;
                    Place(key, box); return true;
                }
            }
            return false;
        }
        if (!PlaceKey() && r.FarmMinute.HasValue) {
            // On very small custom HUDs, give the condition words the space before sacrificing any destination.
            groups.Children.RemoveAt(0);
            key.InvalidateMeasure();
            PlaceKey();
        }
    }

    private static readonly EnemyBoard SampleBoard = new([
        new("npc_dota_hero_razor", 12, ["item_power_treads", "item_black_king_bar", "item_blink", "item_magic_wand", null, null], null, 2) {
            Stats = new(68, 24, 160, 9, 25, 345, 40, 38, 27, 2) },
        new("npc_dota_hero_juggernaut", 14, ["item_phase_boots", "item_bfury", "item_manta", null, null, null], null, 35) {
            Stats = new(70, 81, 230, 14, 25, 360, 42, 61, 30, 35) },
        new("npc_dota_hero_lion", 9, ["item_tranquil_boots", "item_blink", "item_ward_observer", null, null, null], null, 95) {
            Stats = new(58, 0, 110, 4, 25, 380, 31, 22, 36, 95) },
        new("npc_dota_hero_pudge", null, null, null, null)]);
    /// <summary>
    /// The enemy panel at the game's left edge, one row per enemy: head icon, level, the six item slots and the
    /// neutral item, attack damage, armor and move speed, and how long ago they were read (shown from 30 s; rows a
    /// minute old are dimmed).
    /// </summary>
    private static StackPanel EnemyPanel(EnemyBoard board, GameOverlaySettings style, double unit, Func<string, ImageSource?> icon)
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
                foreach (var name in items) line.Children.Add(Slot(name, slot));
            if (hero.Items != null || hero.Neutral != null) {
                var neutral = Slot(hero.Neutral, slot);
                neutral.Margin = new Thickness(3 * u, 0, .5 * u, 0);
                line.Children.Add(neutral);
            }
            if (hero.Stats is { } stats && StatsText(stats) is { Length: > 0 } values)
                line.Children.Add(Text(values, 9.5 * u, Brushes.White));
            string age = Age(hero.Age);
            if (age.Length == 0 && board.MatchId != null) age = "刚读"; // A last read, never an assertion of current inventory.
            if (age.Length > 0) line.Children.Add(Text(age, 9 * u, Brushes.LightGray));
            if (hero.Age >= 60) line.Opacity = .7;
            panel.Children.Add(line);
            // A minimal installation may not have all the PNGs. Don't turn a known item or hero into a
            // meaningless grey rectangle while waiting for art. Keep the source slot, never reorder equipment.
            if (board.MatchId != null) {
                var missing = new List<string>();
                if (icon(hero.Hero) == null) missing.Add(hero.Hero["npc_dota_hero_".Length..].Replace('_', ' '));
                void Missing(string slotName, string? name) {
                    if (name != null && icon(name) == null) missing.Add($"{slotName} {name["item_".Length..].Replace('_', ' ')}");
                }
                if (hero.Items is { } slots) for (int i = 0; i < slots.Length; i++) Missing((i + 1).ToString(), slots[i]);
                Missing("中立", hero.Neutral);
                if (missing.Count > 0) {
                    var fallback = Text(string.Join(" · ", missing), 10 * u, Brushes.White);
                    fallback.MaxWidth = 195 * u; fallback.TextWrapping = TextWrapping.Wrap;
                    fallback.Background = Brush("#081018", 90); panel.Children.Add(fallback);
                }
            }
        }
        panel.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        return panel;
    }
    /// <summary>Bucket last-read ages at 30 s and then by minute; each GSI tick need not repaint the equipment HWND.</summary>
    private static string Age(double? seconds) => seconds switch {
        null => "未见", < 30 => "", < 60 => "30秒前", var s => $"{(int)(s / 60)}分前" };
    /// <summary>Attack damage with its green bonus, armor and move speed; the other stats stay on the coach.</summary>
    private static string StatsText(EnemyStats s) => string.Join("  ", new[] {
        s.Damage is int damage ? $"攻{damage}" + (s.BonusDamage is int bonus and not 0 ? bonus.ToString("+0;-0") : "") : null,
        s.Armor is int armor ? $"甲{armor}" : null, s.MoveSpeed is int speed ? $"速{speed}" : null }.Where(t => t != null));
    private static string Describe(EnemyBoard? board) => board == null ? "" : string.Join(";", board.Heroes.Select(h =>
        $"{h.Hero},{h.Level},{string.Join(",", h.Items ?? [])},{h.Neutral},{(h.Stats is { } s ? StatsText(s) : "")},{Age(h.Age)},{h.Age >= 60}"));
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
