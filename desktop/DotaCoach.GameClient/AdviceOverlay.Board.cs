using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
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
                string.Join(";", (selected.Fog ?? []).Select(f => $"{f.Hero},{f.X:R},{f.Y:R},{f.Yaw},{(int)f.Seconds}")), Icons.Count,
                Settings.MapMarkSize, Settings.MapMarkOpacity);
            layer.SetContent(key, () => WardLayer(selected, side, side, unit, Settings, k => Icons[k]));
            layer.Place(game, map.Left, map.Top, map.Size, map.Size);
        } else if (Settings.ShowWards && !boardSession && ReferenceEquals(board, SampleBoard)) {
            // The style preview outlines where this machine's HUD setting puts the minimap, with sample marks at the
            // chosen size and see-through.
            var outline = Settings.Minimap(game.Width, game.Height);
            var layer = wardWindow ??= new("双机教练 · 目击与推测眼位");
            double side = outline.Size / layer.Scale, unit = DisplayGeometry.OverlayUnit(game.Height, layer.Scale);
            string key = string.Join("|", "outline", side, unit, Settings.MapMarkSize, Settings.MapMarkOpacity, Settings.ShowLaneWaves, Settings.ShowFog, Icons.Count);
            layer.SetContent(key, () => new Grid { Children = {
                new Rectangle { Width = side, Height = side, Stroke = Brushes.DeepSkyBlue, StrokeThickness = 1.5, StrokeDashArray = new DoubleCollection { 4, 3 } },
                WardLayer(MapParts(SampleMarks, Settings, game.Width, game.Height)!, side, side, unit, Settings, k => Icons[k]) } });
            layer.Place(game, outline.Left, outline.Top, outline.Size, outline.Size);
        } else wardWindow?.Withdraw();
    }

    // One of each kind of minimap mark for the style preview; never sent or archived.
    private static readonly EnemyBoard SampleMarks = new([],
        [new("npc_dota_hero_lion", "observer", .32, .4, 0, .6, 20, 300), new("npc_dota_hero_lion", "observer", .4, .33, 1, .25, 20, 300)],
        "0", "radiant", null, [new("sentry", "dire", 1400, -1000)]) {
        Fog = [new("npc_dota_hero_razor", -1800, 2800, 315, 4)],
        LaneMap = new(600, [new("mid", 2, "seen", -1500, -1400, 0, 600), new("mid", 3, "seen", -300, -200, 0, 600),
            new("bot", 3, "march", 5900, -4600, 0, Spawn: 600), new("top", 0, "forecast", -6100, 3200, 0, Meeting: 607)], 2000) };
    // A wave belongs to the coach's frame: a box confirmed for another size drops it. Without a box it goes where the wards go.
    private static EnemyBoard? MapParts(EnemyBoard? b, GameOverlaySettings style, int width, int height) => b == null ? null : b with {
        Wards = style.ShowWards ? b.Wards : null, SeenWards = style.ShowWards ? b.SeenWards : null,
        LaneMap = style.ShowLaneWaves && (b.Minimap == null || b.Minimap.IsValidFor(width, height)) ? b.LaneMap : null,
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

    // Estimates look like Dota's own minimap ward icon, smaller and in yellow: an eye with a pupil for an
    // observer, a hollow eye for a sentry, no pupil when the kind is unknown. The alternatives are smaller and
    // fainter than the first choice. A seen ward is a thin, empty diamond around the actual observed world point, so
    // the game's icon underneath stays visible. One category cannot turn into the other, even at an identical coordinate.
    private static readonly Brush GuessInk = (Brush)Brush("#FFD43B").GetAsFrozen(), EyeEdge = (Brush)Brush("#0A0E12", 90).GetAsFrozen();
    // The user found v0.6.5's marks far too big and too solid to read the minimap under them (2026-11-24): by default
    // shapes are 0.6 of that size and text 0.8 (so digits and the key stay legible), and the layer is see-through.
    private const double MarkScale = .6, MarkTextScale = .8;
    private static Canvas WardLayer(EnemyBoard board, double width, double height, double unit, GameOverlaySettings style,
        Func<string, ImageSource?>? icon = null)
    {
        var map = new Canvas { Width = width, Height = height, ClipToBounds = true, IsHitTestVisible = false, Opacity = style.MapMarkOpacity / 100d };
        double full = Math.Min(unit, Math.Min(width, height) / 90) * style.MapMarkSize / 100, u = full * MarkScale, t = full * MarkTextScale;
        var boxes = new List<Rect>();
        void Place(FrameworkElement element, double x, double y) {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); map.Children.Add(element);
        }
        if (board.LaneMap is { } waves) DrawWaves(map, waves, board.Team!, u, t, boxes);
        if (board.Fog is { } fog) DrawFog(map, fog, u, t, icon, boxes);
        foreach (var m in (board.Wards ?? []).Where(w => w.Left > 0).OrderByDescending(w => w.Rank)) {
            var eye = Eye(m.Kind, (m.Rank == 0 ? 10.5 : 8) * u);
            eye.Opacity = (m.Rank == 0 ? .9 : .5) * (m.Rough ? .75 : 1) * (m.Left < 60 ? .6 : 1);
            var box = new Rect(m.X * width - eye.Width / 2, m.Y * height - eye.Height / 2, eye.Width, eye.Height); boxes.Add(box);
            Place(eye, box.X, box.Y);
        }
        foreach (var w in board.SeenWards ?? []) {
            if (BoardDelivery.OnOverview(w) is not { } point) continue; // Don't clamp an off-map world reading to the HUD edge.
            double x = point.X * width, y = point.Y * height, radius = 6 * u, d = 2 * radius;
            var box = new Rect(x - radius, y - radius, d, d); boxes.Add(box);
            Place(new Polygon { Width = d, Height = d, Points = new() { new(radius, 0), new(d, radius), new(radius, d), new(0, radius) },
                Stroke = w.Team == board.Team ? Brushes.LimeGreen : Brushes.OrangeRed, StrokeThickness = u, Opacity = .85, Tag = "ward-seen" }, box.X, box.Y);
        }
        bool estimated = board.Wards?.Any(m => m.Left > 0) == true, seen = board.SeenWards?.Any(w => BoardDelivery.OnOverview(w) != null) == true;
        var legend = new TextBlock { Foreground = Brushes.White, FontSize = 8.5 * t, FontWeight = FontWeights.SemiBold,
            MaxWidth = Math.Max(1, width - 6*u), TextWrapping = TextWrapping.Wrap };
        foreach (var (text, ink) in new[] { (seen ? "◇ 目击" : null, Brushes.White), (estimated ? "◉ 推测" : null, GuessInk),
            (board.LaneMap != null ? "兵 ●见 ○雾 △波 ×汇" : null, Brushes.White),
            (board.Fog?.Any(f => BoardDelivery.OnOverview(f.X, f.Y) != null) == true ? "红圈 离开视野" : null, Brushes.OrangeRed) }.Where(e => e.Item1 != null))
            legend.Inlines.Add(new Run((legend.Inlines.Count > 0 ? "  " : "") + text) { Foreground = ink });
        var key = new Border { Background = Brush("#081018", 75), CornerRadius = new(2 * u), Padding = new Thickness(2 * u, 0, 2 * u, 0),
            Child = legend };
        key.Measure(new Size(width, height));
        double pad = u, right = width - key.DesiredSize.Width - pad, bottom = height - key.DesiredSize.Height - pad;
        var corners = new[] { new Point(pad, bottom), new Point(right, bottom), new Point(pad, pad), new Point(right, pad) };
        var corner = corners.OrderBy(p => boxes.Count(b => b.IntersectsWith(new Rect(p, key.DesiredSize)))).First();
        // Keep the key even on a crowded HUD, but never paint its backing on top of an observation.
        Panel.SetZIndex(key, -1); Place(key, corner.X, corner.Y);
        return map;
    }
    // Drawn after the client's minimap_ward_obs / minimap_ward_invis sprites: a dark-edged almond, about 3:4.
    private static Canvas Eye(string kind, double width)
    {
        double w = width, h = width * .74, edge = w * .09;
        var almond = new PathGeometry([new PathFigure(new Point(0, h / 2), [
            new QuadraticBezierSegment(new Point(w / 2, -h * .45), new Point(w, h / 2), true),
            new QuadraticBezierSegment(new Point(w / 2, h * 1.45), new Point(0, h / 2), true)], true)]);
        var eye = new Canvas { Width = w, Height = h, Tag = "ward-guess" };
        if (kind == "sentry") {
            eye.Children.Add(new Path { Data = almond, Stroke = EyeEdge, StrokeThickness = 3 * edge, StrokeLineJoin = PenLineJoin.Round });
            eye.Children.Add(new Path { Data = almond, Stroke = GuessInk, StrokeThickness = 1.6 * edge, StrokeLineJoin = PenLineJoin.Round });
            return eye;
        }
        eye.Children.Add(new Path { Data = almond, Fill = GuessInk, Stroke = EyeEdge, StrokeThickness = edge, StrokeLineJoin = PenLineJoin.Round });
        if (kind == "observer") {
            double pupil = h * .5;
            var dot = new Ellipse { Width = pupil, Height = pupil, Fill = EyeEdge };
            Canvas.SetLeft(dot, (w - pupil) / 2); Canvas.SetTop(dot, (h - pupil) / 2); eye.Children.Add(dot);
        }
        return eye;
    }

    // Under the wards: the circle an enemy that left vision is probably in (dashed red, first seconds only), a short line
    // the way it faced, and its head where it was last seen with the seconds since, fading as the sighting ages.
    private static void DrawFog(Canvas map, FogHero[] fog, double u, double t, Func<string, ImageSource?>? icon, List<Rect> boxes)
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
            var age = new TextBlock { Text = $"{(int)f.Seconds}″", FontSize = 7.5 * t, FontWeight = FontWeights.Bold, Foreground = Brushes.White,
                Background = Brush("#081018", 80), Opacity = fade };
            Canvas.SetLeft(age, Math.Clamp(box.Right, 0, Math.Max(0, map.Width - 12 * t))); Canvas.SetTop(age, Math.Clamp(box.Bottom - 4 * t, 0, map.Height - 9 * t));
            map.Children.Add(age);
        }
    }
}
