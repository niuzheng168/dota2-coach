using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace DotaCoach.TwoPc;

internal sealed partial class AdviceOverlay
{
    // Source-pixel calibration is applied by the parent layer. World +Y is north on BOTH left/right HUDs.
    // These passive shapes never synthesize pings/inputs and do not paint over any other foreground window.
    private static void DrawWaves(Canvas map, LaneMap742 waves, string team, double u, List<Rect> boxes)
    {
        var rings = new List<(int Team, Point Center, double Radius)>();
        Point Pixel(int x, int y) {
            var p = BoardDelivery.OnOverview(x, y)!; return new(p.X * map.Width, p.Y * map.Height);
        }
        void Center(FrameworkElement e, Point p, double size, string tag) {
            e.Width = e.Height = size; e.Tag = tag; Canvas.SetLeft(e, p.X - size/2); Canvas.SetTop(e, p.Y - size/2);
            map.Children.Add(e);
        }
        // Furthest uncertainty rings first; a column of six similar fog estimates needs one band,
        // not six opaque circles. The small hollow centers/trails are still individual hypotheses.
        foreach (var m in waves.Marks.Where(m => m.Kind == "fog").OrderByDescending(m => m.Radius)) {
            var p = Pixel(m.X, m.Y); double rad = m.Radius * map.Width / 18944;
            if (rad < 3*u || rings.Any(a => a.Team == m.Team && (p-a.Center).Length < Math.Max(rad, a.Radius))) continue;
            rings.Add((m.Team, p, rad)); var ally = m.Team == (team == "radiant" ? 2 : 3);
            var color = ally ? Brushes.SpringGreen : Brushes.Coral;
            Center(new Ellipse { Stroke = color, StrokeThickness = .8*u, StrokeDashArray = new() { 2, 3 }, Opacity = .52 },
                p, 2*rad, "wave-uncertainty");
            var from = Pixel(m.LastX!.Value, m.LastY!.Value);
            if ((from-p).Length > 3*u) map.Children.Add(new Line { X1 = from.X, Y1 = from.Y, X2 = p.X, Y2 = p.Y,
                Stroke = color, StrokeThickness = .8*u, Opacity = .55, StrokeDashArray = new() { 2, 2 }, Tag = "wave-trail" });
        }
        foreach (var m in waves.Marks.OrderBy(m => m.Kind is "contested" or "last-clash" ? 0 : m.Team == 0 ? 2 : 1)) {
            var p = Pixel(m.X, m.Y); var ally = m.Team == (team == "radiant" ? 2 : 3);
            var color = ally ? Brushes.SpringGreen : Brushes.Coral;
            if (m.Kind is "contested" or "last-clash") {
                Center(new Ellipse { Stroke = m.Kind == "last-clash" ? Brushes.Gold : Brushes.Silver, StrokeDashArray = new() { 1, 3 }, StrokeThickness = u, Opacity = .65 },
                    p, 2 * m.Radius * map.Width / 18944, "wave-" + m.Kind);
            } else if (m.Kind == "march") {
                double d = 7*u;
                Center(new Polygon { Points = new() { new(d/2, 0), new(d, d), new(0, d) }, Stroke = color,
                    Fill = Brushes.Transparent, StrokeThickness = 1.2*u, Opacity = .8 }, p, d, "wave-march");
            } else if (m.Team != 0) {
                bool seen = m.Kind == "seen";
                Center(new Ellipse { Fill = seen ? color : Brushes.Transparent, Stroke = seen ? Brushes.Black : color,
                    StrokeThickness = (seen ? .65 : 1.1)*u, Opacity = seen ? 1 : .8 }, p, (seen ? 3.4 : 5)*u, "wave-" + m.Kind);
            } else {
                bool observed = m.Kind == "contact";
                var ink = observed ? Brushes.White : m.Kind == "baseline" ? Brushes.LightGray : Brushes.Gold;
                double d = 8*u;
                var cross = new Canvas();
                cross.Children.Add(new Line { X1 = 0, Y1 = 0, X2 = d, Y2 = d, Stroke = ink, StrokeThickness = 1.7*u });
                cross.Children.Add(new Line { X1 = d, Y1 = 0, X2 = 0, Y2 = d, Stroke = ink, StrokeThickness = 1.7*u });
                Center(cross, p, d, "wave-" + m.Kind);
                var box = new Rect(p.X-d/2, p.Y-d/2, d, d); boxes.Add(box);
                if (observed) continue;
                string basis = m.Kind switch { "baseline" => "理", "held" => "若守", _ => "" };
                var label = new TextBlock { Text = $"{basis}~{m.Meeting!.Value - waves.Clock}s", Foreground = ink, Background = Brush("#081018", 75),
                    FontSize = 9*u, FontWeight = FontWeights.SemiBold, Tag = "wave-eta" };
                label.Measure(new Size(map.Width, map.Height));
                double lx = Math.Clamp(box.Right+u, 0, Math.Max(0, map.Width-label.DesiredSize.Width)),
                    ly = Math.Clamp(box.Top, 0, Math.Max(0, map.Height-label.DesiredSize.Height));
                Canvas.SetLeft(label, lx); Canvas.SetTop(label, ly); map.Children.Add(label); boxes.Add(new Rect(new Point(lx, ly), label.DesiredSize));
            }
        }
    }
}
