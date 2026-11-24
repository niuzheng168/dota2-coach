using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed partial class AdviceOverlay
{
    private HudLayer? equipmentWindow, wardWindow;
    private void HideBoards() { equipmentWindow?.Withdraw(); wardWindow?.Withdraw(); }

    private static void Passive(IntPtr hwnd, uint affinity)
    {
        SetWindowLongPtr(hwnd, -20, new IntPtr(GetWindowLongPtr(hwnd, -20).ToInt64() | 0x20 | 0x80 | 0x08000000 | 0x80000));
        SetWindowDisplayAffinity(hwnd, affinity);
        HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => {
            if (m is 0x21 or 0x84) { handled = true; return new IntPtr(m == 0x21 ? 3 : -1); }
            return IntPtr.Zero;
        });
    }

    // Like the v0.4.8 small panel: renewals/age ticks do not repaint a transparent full-screen surface.
    // These windows are always excluded from capture, including the bot-only subtitle evidence harness.
    private sealed class HudLayer : Window
    {
        private readonly IntPtr hwnd;
        private string? drawn;
        public double Scale => Math.Max(96, GetDpiForWindow(hwnd)) / 96d;
        public HudLayer(string title)
        {
            Title = title;
            WindowStyle = WindowStyle.None; AllowsTransparency = true; Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize; ShowInTaskbar = false; ShowActivated = false; Topmost = true;
            Focusable = false; IsHitTestVisible = false; WindowStartupLocation = WindowStartupLocation.Manual;
            Width = Height = 1;
            hwnd = new WindowInteropHelper(this).EnsureHandle(); Passive(hwnd, 0x11);
        }
        public Size SetContent(string key, Func<FrameworkElement> create)
        {
            if (drawn != key) {
                var view = create();
                view.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                Content = view; drawn = key;
            }
            return ((FrameworkElement)Content).DesiredSize;
        }
        public void Place(GameSurface game, double x, double y, double width, double height)
        {
            bool Position() => SetWindowPos(hwnd, new IntPtr(-1), game.Left + (int)Math.Round(x), game.Top + (int)Math.Round(y),
                (int)Math.Ceiling(width), (int)Math.Ceiling(height), 0x10);
            if (!IsVisible) { if (!Position()) { Withdraw(); return; } Show(); }
            if (!Position()) Withdraw(); // Never leave a stale window in place.
        }
        public void Withdraw()
        {
            if (IsVisible) Hide();
            if (drawn != null) { Content = null; drawn = null; }
        }
    }

    private void DrawBoards(GameSurface game, EnemyBoard? board)
    {
        if (Settings.ShowEnemies && board is { Heroes.Length: > 0 }) {
            var layer = equipmentWindow ??= new("双机教练 · 上次可见装备");
            double scale = layer.Scale, unit = DisplayGeometry.OverlayUnit(game.Height, scale);
            var key = string.Join("|", Describe(board), Settings.EnemySize, Settings.EnemyOpacity, Icons.Count, unit);
            var size = layer.SetContent(key, () => EnemyPanel(board, Settings, unit, k => Icons[k]));
            layer.Place(game, 4 * unit * scale, Settings.EnemyTop(game.Height / scale, size.Height) * scale,
                size.Width * scale, size.Height * scale);
        } else equipmentWindow?.Withdraw();

        if (MapParts(board, Settings, game.Width, game.Height) is { } selected && WardLayout(selected, game.Width, game.Height, Settings) is { } map) {
            var layer = wardWindow ??= new("双机教练 · 兵线与眼位");
            double scale = layer.Scale, side = map.Size / scale, unit = DisplayGeometry.OverlayUnit(game.Height, scale);
            string key = string.Join("|", side, unit, selected.MatchId, selected.Team, WaveKey(selected.LaneMap),
                string.Join(";", (selected.Wards ?? []).Select(m => $"{m.Hero},{m.Kind},{m.X:R},{m.Y:R},{m.Rank},{m.Rough},{m.Left < 60}")),
                string.Join(";", (selected.SeenWards ?? []).Select(w => $"{w.Kind},{w.Team},{w.X:R},{w.Y:R}")),
                string.Join(";", (selected.Fog ?? []).Select(f => $"{f.Hero},{f.X:R},{f.Y:R},{f.Yaw},{(int)f.Seconds}")), Icons.Count);
            layer.SetContent(key, () => WardLayer(selected, side, side, unit, k => Icons[k]));
            layer.Place(game, map.Left, map.Top, map.Size, map.Size);
        } else if (Settings.ShowWards && !boardSession && ReferenceEquals(board, SampleBoard)) {
            // The style preview outlines where this machine's HUD setting puts the minimap.
            var outline = Settings.Minimap(game.Width, game.Height);
            var layer = wardWindow ??= new("双机教练 · 目击与推测眼位");
            double side = outline.Size / layer.Scale;
            layer.SetContent($"outline|{side:R}", () => new Rectangle { Width = side, Height = side,
                Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } });
            layer.Place(game, outline.Left, outline.Top, outline.Size, outline.Size);
        } else wardWindow?.Withdraw();
    }

    private static EnemyBoard? MapParts(EnemyBoard? b, GameOverlaySettings style, int width, int height) => b == null ? null : b with {
        Wards = style.ShowWards ? b.Wards : null, SeenWards = style.ShowWards ? b.SeenWards : null,
        LaneMap = style.ShowLaneWaves && b.Minimap?.IsValidFor(width, height) == true ? b.LaneMap : null,
        Fog = style.ShowFog ? b.Fog : null };
    private static int WaveKey(LaneMap742? map) {
        if (map == null) return 0;
        var hash = new HashCode(); hash.Add(map.Clock);
        foreach (var mark in map.Marks) hash.Add(mark);
        return hash.ToHashCode(); // A lease countdown alone doesn't move any glyph.
    }
    // A box the coach confirmed for this resolution wins; otherwise this machine's own HUD setting (minimap side and
    // size under 游戏内显示) places the marks, as in v0.4.8. Marks are fractions of the minimap, not screen points.
    private static MinimapLayout? WardLayout(EnemyBoard board, int width, int height, GameOverlaySettings style) =>
        board.MatchId == null || board.Team == null || width <= 0 || height <= 0 ||
        board.LaneMap == null && board.Wards?.Any(w => w.Left > 0) != true && board.SeenWards?.Any(w => BoardDelivery.OnOverview(w) != null) != true &&
        board.Fog?.Any(f => BoardDelivery.OnOverview(f.X, f.Y) != null) != true ? null :
        board.Minimap?.IsValidFor(width, height) == true ? board.Minimap : style.Minimap(width, height);

    // Estimates are hollow dashed regions with '?', including rank 0. Seen wards are a solid diamond at the
    // actual observed world point. One category cannot turn into the other, even at an identical coordinate.
    private static Canvas WardLayer(EnemyBoard board, double width, double height, double unit, Func<string, ImageSource?>? icon = null)
    {
        var map = new Canvas { Width = width, Height = height, ClipToBounds = true, IsHitTestVisible = false };
        double u = Math.Min(unit, Math.Min(width, height) / 90);
        static Brush ColorOf(string kind) => kind switch { "observer" => Brushes.Gold, "sentry" => Brushes.DeepSkyBlue, _ => Brushes.White };
        var boxes = new List<Rect>();
        void Place(FrameworkElement element, double x, double y) {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); map.Children.Add(element);
        }
        if (board.LaneMap is { } waves) DrawWaves(map, waves, board.Team!, u, boxes);
        if (board.Fog is { } fog) DrawFog(map, fog, u, icon, boxes);
        foreach (var m in (board.Wards ?? []).Where(w => w.Left > 0).OrderByDescending(w => w.Rank)) {
            double x = m.X * width, y = m.Y * height, size = (m.Rough ? 25 : m.Rank == 0 ? 18 : 14) * u;
            var color = ColorOf(m.Kind);
            var box = new Rect(x - size / 2, y - size / 2, size, size); boxes.Add(box);
            double opacity = (m.Rank == 0 ? 1 : .82) * (m.Left < 60 ? .66 : 1);
            Place(new Ellipse { Width = size, Height = size, Stroke = color, StrokeThickness = 1.8 * u,
                Fill = Brushes.Transparent, StrokeDashArray = new DoubleCollection { 2, 1.6 }, Opacity = opacity }, box.X, box.Y);
            // Keep the question legible at the edge of the HUD and away from a sighting at the same point.
            double q = 10 * u;
            var question = new Border { Width = q, Height = q, Background = Brush("#081018", 92), CornerRadius = new(q / 2),
                Child = new TextBlock { Text = "?", Foreground = color, FontSize = 9 * u, FontWeight = FontWeights.Bold,
                    TextAlignment = TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center } };
            double qx = Math.Clamp(box.Right - 2 * u, 0, width - q), qy = Math.Clamp(box.Top - q / 2, 0, height - q);
            Place(question, qx, qy); boxes.Add(new Rect(qx, qy, q, q));
        }
        foreach (var w in board.SeenWards ?? []) {
            if (BoardDelivery.OnOverview(w) is not { } point) continue; // Don't clamp an off-map world reading to the HUD edge.
            double x = point.X * width, y = point.Y * height, radius = 4.3 * u, d = 2 * radius;
            var box = new Rect(x - radius, y - radius, d, d); boxes.Add(box);
            Place(new Polygon { Width = d, Height = d, Points = new() { new(radius, 0), new(d, radius), new(radius, d), new(0, radius) },
                Fill = ColorOf(w.Kind), Stroke = w.Team == board.Team ? Brushes.LimeGreen : Brushes.OrangeRed, StrokeThickness = 1.5 * u }, box.X, box.Y);
        }
        bool estimated = board.Wards?.Any(m => m.Left > 0) == true, seen = board.SeenWards?.Any(w => BoardDelivery.OnOverview(w) != null) == true;
        string legend = string.Join("  ", new[] { seen ? "◆ 目击" : null, estimated ? "? 推测" : null,
            board.LaneMap != null ? "兵 ●见 ○雾 △波 ×汇" : null,
            board.Fog?.Any(f => BoardDelivery.OnOverview(f.X, f.Y) != null) == true ? "红圈 离开视野后可能在" : null }.Where(t => t != null));
        var key = new Border { Background = Brush("#081018", 90), CornerRadius = new(2 * u), Padding = new Thickness(2 * u, 0, 2 * u, 0),
            Child = new TextBlock { Text = legend, Foreground = Brushes.White, FontSize = 10 * u, FontWeight = FontWeights.SemiBold,
                MaxWidth = Math.Max(1, width - 6*u), TextWrapping = TextWrapping.Wrap } };
        key.Measure(new Size(width, height));
        double pad = u, right = width - key.DesiredSize.Width - pad, bottom = height - key.DesiredSize.Height - pad;
        var corners = new[] { new Point(pad, bottom), new Point(right, bottom), new Point(pad, pad), new Point(right, pad) };
        var corner = corners.OrderBy(p => boxes.Count(b => b.IntersectsWith(new Rect(p, key.DesiredSize)))).First();
        // Keep the key even on a crowded HUD, but never paint its backing on top of an observation.
        Panel.SetZIndex(key, -1); Place(key, corner.X, corner.Y);
        return map;
    }

    // Under the wards: the circle an enemy that left vision is probably in (dashed red, first seconds only), a short line
    // the way it faced, and its head where it was last seen with the seconds since, fading as the sighting ages.
    private static void DrawFog(Canvas map, FogHero[] fog, double u, Func<string, ImageSource?>? icon, List<Rect> boxes)
    {
        double scale = map.Width; // the overview's span maps onto the square minimap
        foreach (var f in fog.OrderByDescending(f => f.Seconds)) {
            if (BoardDelivery.OnOverview(f.X, f.Y) is not { } at) continue;
            double x = at.X * map.Width, y = at.Y * map.Height, fade = 1 - .45 * Math.Clamp(f.Seconds / FogTracker.MaxSeconds, 0, 1);
            if (FogTracker.Circle(f) is var (cx, cy, radius) && BoardDelivery.OnOverview(cx, cy) is { } centre) {
                double r = radius / (2.0 * BoardDelivery.WorldEdge) * scale;
                var ring = new Ellipse { Width = 2 * r, Height = 2 * r, Stroke = Brushes.OrangeRed, StrokeThickness = 1.3 * u,
                    StrokeDashArray = new DoubleCollection { 3, 2 }, Fill = new SolidColorBrush(Color.FromArgb(28, 255, 69, 0)), Opacity = .85 * fade };
                Panel.SetZIndex(ring, -2);
                Canvas.SetLeft(ring, centre.X * map.Width - r); Canvas.SetTop(ring, centre.Y * map.Height - r); map.Children.Add(ring);
            }
            if (f.Yaw is int yaw) {
                double a = yaw * Math.PI / 180, length = 9 * u;
                map.Children.Add(new Line { X1 = x, Y1 = y, X2 = x + length * Math.Cos(a), Y2 = y - length * Math.Sin(a),
                    Stroke = Brushes.OrangeRed, StrokeThickness = 1.6 * u, Opacity = fade });
            }
            double side = 9 * u;
            FrameworkElement head = icon?.Invoke(f.Hero) is { } image
                ? new Border { Width = side, Height = side, BorderBrush = Brushes.OrangeRed, BorderThickness = new(u),
                    Child = new Image { Source = image, Stretch = Stretch.UniformToFill } }
                : new Ellipse { Width = side * .7, Height = side * .7, Fill = Brushes.OrangeRed, Stroke = Brushes.Black, StrokeThickness = .6 * u };
            head.Opacity = fade;
            head.Measure(new Size(side, side));
            var box = new Rect(x - head.Width / 2, y - head.Height / 2, head.Width, head.Height); boxes.Add(box);
            Canvas.SetLeft(head, box.X); Canvas.SetTop(head, box.Y); map.Children.Add(head);
            var age = new TextBlock { Text = $"{(int)f.Seconds}″", FontSize = 7.5 * u, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Background = Brush("#081018", 80), Opacity = fade };
            Canvas.SetLeft(age, Math.Clamp(box.Right, 0, Math.Max(0, map.Width - 12 * u))); Canvas.SetTop(age, Math.Clamp(box.Bottom - 4 * u, 0, map.Height - 9 * u));
            map.Children.Add(age);
        }
    }
}
