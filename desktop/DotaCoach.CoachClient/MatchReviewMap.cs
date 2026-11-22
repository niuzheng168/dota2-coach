using System;
using System.Globalization;
using System.Windows;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

// A replay-only Cartesian sketch, not terrain/vision reconstruction. Raw coordinates remain in the roster.
internal sealed class MatchReviewMap : FrameworkElement
{
    private static readonly Pen GridLine = new(Brushes.DarkSlateGray, .8);
    private static readonly Typeface Font = new("Segoe UI");
    internal ReviewDot[] Dots { get; private set; } = [];
    private int focus;
    internal void Set(ReviewDot[] dots, int slot) { Dots = dots; focus = slot; InvalidateVisual(); }

    protected override void OnRender(DrawingContext g)
    {
        base.OnRender(g);
        double side = Math.Min(ActualWidth, ActualHeight), inset = 7, span = side - 2 * inset;
        if (span <= 0) return;
        g.DrawRectangle(Brushes.Black, GridLine, new Rect(0, 0, side, side));
        for (int i = 1; i < 4; i++) {
            double p = inset + span * i / 4;
            g.DrawLine(GridLine, new Point(p, inset), new Point(p, side - inset));
            g.DrawLine(GridLine, new Point(inset, p), new Point(side - inset, p));
        }
        foreach (var dot in Dots) {
            // Same symmetric overview extent as the repository's static map, +Y north. No clamping/interpolation.
            if (Math.Abs(dot.X) > 9472 || Math.Abs(dot.Y) > 9472) continue;
            var p = new Point(inset + (dot.X + 9472) / 18944 * span, inset + (9472 - dot.Y) / 18944 * span);
            if (dot.Ward) g.DrawRectangle(Brushes.Gold, null, new Rect(p.X - 5, p.Y - 5, 10, 10));
            else {
                var sideBrush = dot.Team == 2 ? Brushes.MediumSpringGreen : dot.Team == 3 ? Brushes.LightCoral : Brushes.LightGray;
                if (dot.Label == focus.ToString(CultureInfo.InvariantCulture)) g.DrawEllipse(Brushes.White, null, p, 7, 7);
                g.DrawEllipse(sideBrush, null, p, 5.5, 5.5);
            }
            var label = new FormattedText(dot.Label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, 9,
                Brushes.Black, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            g.DrawText(label, new Point(p.X - label.Width / 2, p.Y - label.Height / 2));
        }
    }
}
