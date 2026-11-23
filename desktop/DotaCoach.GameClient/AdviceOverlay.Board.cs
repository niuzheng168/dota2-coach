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
                string.Join(";", (selected.SeenWards ?? []).Select(w => $"{w.Kind},{w.Team},{w.X:R},{w.Y:R}")));
            layer.SetContent(key, () => WardLayer(selected, side, side, unit));
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

    // A wave belongs to the coach's frame: a box confirmed for another size drops it. Without a box it goes where the wards go.
    private static EnemyBoard? MapParts(EnemyBoard? b, GameOverlaySettings style, int width, int height) => b == null ? null : b with {
        Wards = style.ShowWards ? b.Wards : null, SeenWards = style.ShowWards ? b.SeenWards : null,
        LaneMap = style.ShowLaneWaves && (b.Minimap == null || b.Minimap.IsValidFor(width, height)) ? b.LaneMap : null };
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
        board.LaneMap == null && board.Wards?.Any(w => w.Left > 0) != true && board.SeenWards?.Any(w => BoardDelivery.OnOverview(w) != null) != true ? null :
        board.Minimap?.IsValidFor(width, height) == true ? board.Minimap : style.Minimap(width, height);

    // Estimates look like Dota's own minimap ward icon at about its size, in yellow: an eye with a pupil for an
    // observer, a hollow eye for a sentry, no pupil when the kind is unknown. The alternatives are smaller and
    // fainter than the first choice. A seen ward is a thin, empty diamond around the actual observed world point, so
    // the game's icon underneath stays visible. One category cannot turn into the other, even at an identical coordinate.
    private static readonly Brush GuessInk = (Brush)Brush("#FFD43B").GetAsFrozen(), EyeEdge = (Brush)Brush("#0A0E12", 90).GetAsFrozen();
    private static Canvas WardLayer(EnemyBoard board, double width, double height, double unit)
    {
        var map = new Canvas { Width = width, Height = height, ClipToBounds = true, IsHitTestVisible = false };
        double u = Math.Min(unit, Math.Min(width, height) / 90);
        var boxes = new List<Rect>();
        void Place(FrameworkElement element, double x, double y) {
            Canvas.SetLeft(element, x); Canvas.SetTop(element, y); map.Children.Add(element);
        }
        if (board.LaneMap is { } waves) DrawWaves(map, waves, board.Team!, u, boxes);
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
        var legend = new TextBlock { Foreground = Brushes.White, FontSize = 8.5 * u, FontWeight = FontWeights.SemiBold,
            MaxWidth = Math.Max(1, width - 6*u), TextWrapping = TextWrapping.Wrap };
        foreach (var (text, ink) in new[] { (seen ? "◇ 目击" : null, Brushes.White), (estimated ? "◉ 推测" : null, GuessInk),
            (board.LaneMap != null ? "兵 ●见 ○雾 △波 ×汇" : null, Brushes.White) }.Where(t => t.Item1 != null))
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
}
