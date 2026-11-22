using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using DotaCoach.TwoPc;
using Path = System.IO.Path;

internal static partial class Program
{
    // Only the shipped compositor on a detached canvas. No Window, game lookup, input, capture, pairing or model.
    private static void MinuteRouteChecks(string output, string? archivedHud = null)
    {
        void Check(bool ok, string what) {
            if (!ok) throw new Exception("Minute route: " + what);
            passed++; Console.WriteLine("PASS " + what);
        }
        static ScreenMark Mark(MinimapLayout m, double x, double y) =>
            new((m.Left + x * m.Size) / m.SourceWidth, (m.Top + y * m.Size) / m.SourceHeight);
        static Reminder Example(MinimapLayout m, bool dire = false) {
            // Two nearby camps and an optional lane landmark. Screen marks are *already* projected, not world XY.
            var marks = dire ? new[] { Mark(m, .294, .245), Mark(m, .363, .297), Mark(m, .222, .181) } :
                [Mark(m, .710, .766), Mark(m, .746, .695), Mark(m, .758, .844)];
            string side = dire ? "上" : "下";
            return new("minute-example", "s", 7, "minute-farm-route", "空点跳过，遇险就撤。", marks[0],
                m.SourceWidth, m.SourceHeight, 8000, m, false, marks, [
                    $"约10:08 {side}侧小野 · 有野再打", $"约10:23 {side}侧大野 · 有野再打", $"约10:45 {side}路一塔 · 有线再收"
                ], FarmMinute: 10);
        }
        static Canvas Draw(Reminder r, double dpi = 1, GameOverlaySettings? style = null, EnemyBoard? board = null) {
            var c = new Canvas { Width = r.SourceWidth / dpi, Height = r.SourceHeight / dpi };
            AdviceOverlay.Compose(c, r, style ?? new(), DisplayGeometry.OverlayUnit(r.SourceHeight, dpi), board);
            c.Measure(new Size(c.Width, c.Height)); c.Arrange(new Rect(0, 0, c.Width, c.Height)); c.UpdateLayout();
            return c;
        }
        static Rect Bounds(FrameworkElement e) => new(Canvas.GetLeft(e), Canvas.GetTop(e), e.ActualWidth, e.ActualHeight);
        static bool Near(double a, double b) => Math.Abs(a - b) < .01;
        static string[] Tokens(Canvas layer) => Children(layer).OfType<WrapPanel>().Single().Children.OfType<TextBlock>().Select(t => t.Text).ToArray();
        static RenderTargetBitmap Raster(Canvas c, double dpi) {
            var bitmap = new RenderTargetBitmap((int)Math.Round(c.Width * dpi), (int)Math.Round(c.Height * dpi),
                dpi * 96, dpi * 96, PixelFormats.Pbgra32);
            bitmap.Render(c); return bitmap;
        }

        foreach (var (w, h, dpi) in new[] { (1280, 720, 1d), (3840, 2160, 1.5), (3840, 2160, 2d) })
        foreach (bool right in new[] { false, true }) {
            var m = h == 720 ? MinimapLayout.Suggest(w, h, right) : new(w, h, right ? w - 535 : 15, h - 552, 520);
            var r = Example(m, dire: dpi == 1.5);
            var c = Draw(r, dpi);
            string where = $"{h}p at {dpi:P0} / {(right ? "right" : "left")}";
            var map = c.Children.OfType<Canvas>().Single();
            var area = new Rect(0, 0, map.Width, map.Height);
            var dots = map.Children.OfType<Ellipse>().Select(Bounds).ToArray();
            var badges = map.Children.OfType<Border>().Where(b => b.Child is TextBlock).ToArray();
            var key = Bounds(map.Children.OfType<Border>().Single(b => b.Child is WrapPanel));
            Check(c.Children.Count == 1 && map.ClipToBounds && !map.IsHitTestVisible &&
                Near(Canvas.GetLeft(map) * dpi, m.Left) && Near(Canvas.GetTop(map) * dpi, m.Top) &&
                Near(map.Width * dpi, m.Size) && Near(map.Height * dpi, m.Size), $"only the calibrated, click-through map layer at {where}");
            Check(dots.Length == 3 && dots.Select((b, i) =>
                Near(m.Left + (b.X + b.Width / 2) * dpi, r.Route![i].X * w) &&
                Near(m.Top + (b.Y + b.Height / 2) * dpi, r.Route![i].Y * h)).All(x => x), $"ordered pips at source pixels, no mirroring at {where}");
            Check(badges.Select(b => ((TextBlock)b.Child).Text).SequenceEqual(["1", "2", "3"]) &&
                badges[0].Width > badges[1].Width && badges[1].CornerRadius != badges[2].CornerRadius &&
                badges[1].BorderBrush != badges[2].BorderBrush && Near(badges[0].Width * dpi / (h / 720), 17) &&
                Tokens(map).SequenceEqual(["10′", "1·2 有野再打", "3 有线再收"]), $"first stop, camp and lane are distinct without a subtitle at {where}");
            Check(area.Contains(key) && badges.Select(Bounds).All(b => area.Contains(b) && !b.IntersectsWith(key)) &&
                badges.Select((b, i) => badges.Skip(i + 1).All(other => !Bounds(b).IntersectsWith(Bounds(other)))).All(x => x) &&
                dots.All(d => !d.IntersectsWith(key)), $"nearby destinations and their key stay legible inside the map at {where}");
        }

        var left = MinimapLayout.Suggest(1280, 720);
        var example = Example(left);
        bool current = true;
        bool Guard(Reminder value) => current && ReferenceEquals(example, value);
        bool granted = AdviceOverlay.AuthorizedForDraw(example, Guard);
        current = false;
        Check(granted && !AdviceOverlay.AuthorizedForDraw(example, null) && !AdviceOverlay.AuthorizedForDraw(example, Guard) &&
            AdviceOverlay.AuthorizedForDraw(example with { Kind = "manual-link-test" }, _ => throw new InvalidOperationException()),
            "live render guard fails closed, checks the same reminder again and never gates legacy reminders");
        var onlyCamp = example with { Route = null, NextSteps = ["约10:40 线旁小野 · 有野再打"], FarmMinute = null };
        var single = Draw(onlyCamp).Children.OfType<Canvas>().Single();
        Check(Tokens(single).SequenceEqual(["1 有野再打"]) && !AdviceOverlay.Label(onlyCamp).Contains("10:00"),
            "a single marker is numbered; no inferred lane or invented minute");
        Check(Tokens(Draw(onlyCamp with { NextSteps = ["有野再打？未确认"] }).Children.OfType<Canvas>().Single()).SequenceEqual(["1 待确认"]) &&
            Tokens(Draw(example with { NextSteps = null }).Children.OfType<Canvas>().Single()).SequenceEqual(["10′", "1·2·3 待确认"]),
            "missing / unfamiliar step suffixes make no claim about the destination");

        var narrow = new MinimapLayout(1280, 720, 1190, 630, 80);
        var edge = Example(narrow);
        ScreenMark[] corners = [Mark(narrow, 0, 0), Mark(narrow, .08, .05), Mark(narrow, 1, 1)];
        var small = Draw(edge with { Marker = corners[0], Route = corners }).Children.OfType<Canvas>().Single();
        var legend = small.Children.OfType<Border>().Single(b => b.Child is WrapPanel);
        var boxes = small.Children.OfType<Border>().Select(Bounds).ToArray();
        Check(boxes.All(new Rect(0, 0, 80, 80).Contains) &&
            boxes.Select((b, i) => boxes.Skip(i + 1).All(other => !b.IntersectsWith(other))).All(x => x) &&
            Tokens(small).Contains("1·2 有野再打") && Tokens(small).Contains("3 有线再收") &&
            legend.ActualHeight > 2 * ((WrapPanel)legend.Child).Children.OfType<TextBlock>().First().FontSize,
            "clustered edge badges stay separate and whole legend groups wrap on a narrow custom HUD");

        var shown = example with { ShowSubtitle = true };
        var style = new GameOverlaySettings { X = 100, Y = 0, ShowTitle = false, TextColor = "#FF5A5A", ShowAlerts = false };
        var withCaption = Draw(shown, style: style);
        var caption = withCaption.Children.OfType<Border>().Single();
        var text = (TextBlock)caption.Child;
        string[] runs = text.Inlines.OfType<Run>().Select(run => run.Text).ToArray();
        Check(runs.Length == 2 && runs[0] == shown.Message && runs[1].Contains("③" + example.NextSteps![2]) &&
            AdviceOverlay.Label(shown).Contains("10:00"), "enabled minute subtitle shows the ordered steps and has a minute heading");
        Check(text.Foreground is SolidColorBrush { Color.R: 255, Color.G: 90 } &&
            Math.Abs(Bounds(caption).Right - 1280) < 1 && Bounds(caption).Top == 0 && withCaption.Children.OfType<Canvas>().Count() == 1,
            "minute subtitle keeps the player's existing colour and placement");
        Check(!style.Shows("local-vision-observation") && !(style with { ShowAdvice = false }).Shows(shown.Kind) &&
            Draw(shown, style: style with { ShowAdvice = false }).Children.Cast<UIElement>().Single() is Canvas &&
            Draw(example, style: style).Children.Cast<UIElement>().Single() is Canvas, "either subtitle switch leaves the minimap route intact");
        Check(new[] { example with { Minimap = null }, example with { Minimap = left with { SourceWidth = 1920 } },
                example with { Marker = null }, example with { Marker = example.Route![1] }, example with { Route = [] },
                example with { Route = [example.Marker!, new(.5, .5)] }, example with { Route = [example.Marker!, new(double.NaN, 1)] }
            }.All(r => Draw(r).Children.Count == 0), "missing/mismatched calibration, missing anchor or invalid stop fails closed as a whole");
        var legacy = Draw(example with { Kind = "visible-farming-advice" });
        Check(legacy.Children.OfType<Line>().Count() == 2 && legacy.Children.OfType<Polygon>().Count() == 1 &&
            Draw(example with { Kind = "manual-link-test", Route = null }).Children.OfType<Ellipse>().Count() == 1,
            "legacy route legs and manual link marker are unchanged");
        var board = new EnemyBoard([new("npc_dota_hero_razor", 12, null, null, 0)]);
        var mixed = Draw(example, board: board);
        Check(mixed.Children.OfType<StackPanel>().Any() && mixed.Children.OfType<Canvas>().Any(), "route coexists with the enemy panel");
        AdviceOverlay.Compose(mixed, null, style, 1, board);
        Check(mixed.Children.Cast<UIElement>().Single() is StackPanel, "withdrawing the reminder removes every route visual but not the enemy panel");

        // Pixel regression in physical 4K at 200% WPF scaling. Count all ink, including any accidentally
        // surviving caption/arrow, and check a whole band (not one pixel that could fall in a dashed leg's gap).
        var right4k = new MinimapLayout(3840, 2160, 3280, 1600, 520);
        ScreenMark[] apart = [Mark(right4k, .12, .5), Mark(right4k, .88, .5)];
        var wide = Draw(Example(right4k) with { Marker = apart[0], Route = apart, NextSteps = ["有野再打", "有线再收"] }, 2);
        Check(Tokens(wide.Children.OfType<Canvas>().Single()).SequenceEqual(["10′", "1 有野再打", "2 有线再收"]) &&
            !Children(wide).Any(v => v is Line or Polyline), "a two-stop route can return to lane at #2, without any route leg");
        var bmp = Raster(wide, 2);
        var pixels = new byte[bmp.PixelWidth * bmp.PixelHeight * 4];
        bmp.CopyPixels(pixels, bmp.PixelWidth * 4, 0);
        var middle = new Rect(3440, 1850, 200, 20);
        int inside = 0, outside = 0, between = 0;
        for (int y = 0, i = 3; y < bmp.PixelHeight; y++)
            for (int x = 0; x < bmp.PixelWidth; x++, i += 4) {
                if (pixels[i] == 0) continue;
                if (x >= right4k.Left && x < right4k.Left + right4k.Size && y >= right4k.Top && y < right4k.Top + right4k.Size) inside++;
                else outside++;
                if (middle.Contains(x, y)) between++;
            }
        Check(inside > 1000 && inside < right4k.Size * right4k.Size * .20 && outside == 0 && between == 0 && apart.All(p =>
            pixels[4 * ((int)(p.Y * bmp.PixelHeight) * bmp.PixelWidth + (int)(p.X * bmp.PixelWidth)) + 3] > 0),
            "4K raster: both destinations have ink; playfield and the space between stops remain transparent");

        Directory.CreateDirectory(output);
        void SaveCrop(BitmapSource image, Int32Rect crop, string name) {
            string path = Path.Combine(output, name);
            var png = new PngBitmapEncoder(); png.Frames.Add(BitmapFrame.Create(new CroppedBitmap(image, crop)));
            using var destination = File.Create(path); png.Save(destination);
            Console.WriteLine("Illustrative minute-route crop: " + Path.GetFullPath(path));
        }
        SaveCrop(Raster(Draw(Example(right4k, dire: true), 2), 2), new Int32Rect(3240, 1560, 600, 600), "minute-route-overlay-4k-right.png");
        // Optional offline QA background: a saved, default-left 1280×720 game frame, never a screen grab.
        BitmapSource picture = Raster(Draw(example), 1);
        if (archivedHud != null) {
            using var file = File.OpenRead(archivedHud);
            var bg = BitmapDecoder.Create(file, BitmapCreateOptions.None, BitmapCacheOption.OnLoad).Frames[0];
            if (bg.PixelWidth != 1280 || bg.PixelHeight != 720) throw new ArgumentException("Expected a saved 1280×720 left-HUD frame.");
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen()) { var full = new Rect(0, 0, 1280, 720); dc.DrawImage(bg, full); dc.DrawImage(picture, full); }
            var composite = new RenderTargetBitmap(1280, 720, 96, 96, PixelFormats.Pbgra32);
            composite.Render(visual); picture = composite;
        }
        SaveCrop(picture, new Int32Rect(0, 526, 224, 194), archivedHud == null ? "minute-route-overlay-720p.png" : "minute-route-hud-720p.png");
    }
}
