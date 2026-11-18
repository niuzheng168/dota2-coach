namespace DotaCoach.TwoPc;

public readonly record struct CreepBar(int X, int Y, int Width, int Height, int Fill, bool Ally)
{
    public HitRange Health(int maximum, double pixelError = 1.5) => new(
        Math.Max(.01, (Fill - pixelError) * maximum / (Width - 2)),
        Math.Min(maximum, (Fill + pixelError) * maximum / (Width - 2)));
}

/// <summary>Recognize the thin, uninterrupted overhead bar of a normal creep, not hero HUD text.
/// Works on the local, uncompressed NV12 plane; no RGB image allocation or model call per frame.</summary>
public static class CreepBars
{
    public static List<CreepBar> Read(VideoPixels frame)
    {
        int w = frame.Width, h = frame.Height, expected = (int)Math.Round(80 * h / 1080d);
        int tolerance = Math.Max(2, expected / 25), dy = Math.Max(2, (int)Math.Round(h * 3 / 1080d));
        int barHeight = Math.Max(4, (int)Math.Round(h * 8 / 1080d));
        var result = new List<CreepBar>();
        byte[] data = frame.Nv12;
        // Exclude the scoreboard and bottom HUD. Search for a horizontal black rim, then validate
        // both sides and two interior scanlines. A red/green patch by itself is not a bar.
        for (int y = h / 12; y < h * 81 / 100 - barHeight; y++) {
            int row = y * w, x = 1;
            while (x < w - 1) {
                if (data[row + x] > 28) { x++; continue; }
                int left = x++;
                while (x < w - 1 && data[row + x] <= 28) x++;
                int width = x - left;
                if (width < expected - tolerance || width > expected + tolerance ||
                    left < 2 || x >= w - 1) continue;
                int middle = (y + dy) * w;
                if (data[middle + left] > 38 || data[middle + x - 1] > 38) continue;
                var a = Scan(y + dy, left, width);
                var b = Scan(y + dy + 1, left, width);
                if (a.Fill < 2 || b.Fill < 2 || a.Ally != b.Ally || Math.Abs(a.Fill - b.Fill) > 2) continue;
                if (result.Any(v => Math.Abs(left - v.X) <= 2 && y - v.Y < barHeight)) continue;
                result.Add(new(left, y, width, barHeight, (a.Fill + b.Fill + 1) / 2, a.Ally));
                if (result.Count >= 64) return result;
            }
        }
        return result;

        (int Fill, bool Ally) Scan(int yy, int left, int width)
        {
            int row = yy * w, uvRow = w * h + yy / 2 * w, filled = 0, colored = 0;
            bool? ally = null;
            for (int dx = 1; dx < width - 1; dx++) {
                int xx = left + dx;
                if (data[row + xx] < 48) break;
                filled++;
                int uv = uvRow + (xx & ~1), v = data[uv + 1];
                bool? side = data[uv] <= 130 ? v >= 136 ? false : v <= 120 ? true : null : null;
                if (side == null) continue;
                if (ally.HasValue && ally != side) return default;
                ally = side; colored++;
            }
            if (ally == null || colored < Math.Max(1, filled * 3 / 4)) return default;
            for (int dx = filled + 1; dx < width - 1; dx++)
                if (data[row + left + dx] > 51) return default;
            return (filled, ally.Value);
        }
    }
}
