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
        var result = new List<CreepBar>();
        Read(frame.Nv12, frame.Width, frame.Height, frame.Width, result);
        return result;
    }

    // The WGC practice path scans a mapped, potentially padded staging texture in place.
    // No pointer/span escapes the Map/Unmap lifetime.
    internal static void Read(ReadOnlySpan<byte> data, int w, int h, int stride, List<CreepBar> result)
    {
        result.Clear();
        ReadRegion(data,w,h,stride,result,0,0,w,h);
    }

    // Bounds restrict candidate top-left points, not the pixels used to validate the rim.
    internal static void ReadRegion(ReadOnlySpan<byte> data, int w, int h, int stride, List<CreepBar> result,
        int x0, int y0, int x1, int y1)
    {
        if (result.Count >= 64) return;
        int expected = (int)Math.Round(80 * h / 1080d);
        int tolerance = Math.Max(2, expected / 25), dy = Math.Max(2, (int)Math.Round(h * 3 / 1080d));
        int barHeight = Math.Max(4, (int)Math.Round(h * 8 / 1080d));
        int step = Math.Max(1, expected - tolerance);
        // Exclude the scoreboard and bottom HUD. Search for a horizontal black rim, then validate
        // both sides and two interior scanlines. A red/green patch by itself is not a bar.
        x0=Math.Max(0,x0); x1=Math.Min(x1,w);
        int end=Math.Min(w-1,x1+expected+tolerance);
        for (int y = Math.Max(h/12,y0); y < Math.Min(h*81/100-barHeight,y1); y++) {
            int row = y * stride, probe = Math.Min(x0+step-1,w-2);
            // Every qualifying run is at least 'step' pixels, including short antialiased gaps.
            while (probe < end) {
                if (data[row + probe] > 35) { probe += step; continue; }
                int left = probe, x = probe + 1;
                if (data[row + probe] <= 28) {
                    while (left > 1 && data[row + left - 1] <= 28) left--;
                    while (x < w - 1 && data[row + x] <= 28) x++;
                    probe = x + step - (x < w - 1 && data[row + x] <= 35 ? 1 : 0);
                } else { probe += step; }
                bool softTop = x - left < expected - tolerance;
                if (softTop) {
                    // A true dark rim can be split by a few slightly brighter pixels after downsampling.
                    // Never skip the next strict probe or search an unbounded soft background.
                    int max = expected + tolerance;
                    while (left > 1 && x - left <= max && data[row + left - 1] <= 35) left--;
                    while (x < w - 1 && x - left <= max && data[row + x] <= 35) x++;
                }
                int width = x - left;
                if (left < x0 || left >= x1 || width < expected - tolerance || width > expected + tolerance ||
                    left < 2 || x >= w - 1) continue;
                int middle = (y + dy) * stride;
                if (softTop || data[middle + left] > 38 || data[middle + x - 1] > 38) {
                    // 4K -> 1080p: the top rim and antialiased side can land in adjacent
                    // columns. Only accept this fallback with an independently dark LOWER rim.
                    if (softTop) {
                        int deep = 0;
                        for (int i = left; i < x; i++) if (data[row + i] <= 28) deep++;
                        if (deep * 4 < width * 3) continue;
                    }
                    int right = x - 1;
                    if (data[middle + left] > 46 && data[middle + left - 1] <= 46) left--;
                    else if (!softTop && data[middle+left]>58 && ContrastEdge(data,middle,left-1,1) &&
                        ContrastEdge(data,middle+stride,left-1,1)) left--;
                    if (data[middle + right] > 46 && data[middle + right + 1] <= 46) right++;
                    width = right - left + 1;
                    if (width < expected - tolerance || width > expected + tolerance ||
                        (data[middle + left] > 46 && (softTop || !ContrastEdge(data,middle,left,1) || !ContrastEdge(data,middle+stride,left,1))) ||
                        data[middle + right] > 46 ||
                        !LowerRim(data, stride, y, middle, left, width, barHeight, dy)) continue;
                }
                if (!ReadFill(data,stride,h,left,y,width,barHeight,dy,out var bar)) continue;
                bool duplicate = false;
                foreach (var v in result)
                    if (Math.Abs(left - v.X) <= 2 && Math.Abs(y - v.Y) < barHeight) { duplicate = true; break; }
                if (duplicate) continue;
                result.Add(bar);
                if (result.Count >= 64) return;
            }
        }
    }

    // Fast path: validate that the entire top rim still has exactly the old boundaries, then
    // re-read the interior from this picture. A move, stale rim or dim side falls back to search.
    internal static bool ReadAt(ReadOnlySpan<byte> data,int stride,int h,CreepBar old,out CreepBar bar)
    {
        bar=default; int x=old.X,y=old.Y,w=old.Width,dy=Math.Max(2,(int)Math.Round(h*3/1080d));
        int top=y*stride+x, middle=top+dy*stride;
        if (data[top-1]<=28 || data[top+w]<=28 || data[middle]>38 || data[middle+w-1]>38 ||
            data.Slice(top,w).IndexOfAnyInRange((byte)29,byte.MaxValue)>=0) return false;
        if(ReadFill(data,stride,h,x,y,w,old.Height,dy,out bar)) return true;
        // A last sliver by itself is not a safe new target. On a previously low, stationary
        // bar require three fresh one-pixel samples with the previous team and a lower rim.
        if(old.Fill>w*.4 || !LowerRim(data,stride,y,middle-x,x,w,old.Height,dy)) return false;
        for(int j=dy-1;j<=dy+1;j++) if(Scan(data,stride,h,y+j,x,w)!=(1,old.Ally)) return false;
        bar=old with {Fill=1}; return true;
    }

    private static bool ContrastEdge(ReadOnlySpan<byte> data,int row,int x,int inward)
    {
        int edge=data[row+x];
        return edge<=58 && data[row+x+inward]-edge>=8 && data[row+x-inward]-edge>=16;
    }
    private static bool ReadFill(ReadOnlySpan<byte> data,int stride,int h,int left,int y,int width,int barHeight,int dy,out CreepBar bar)
    {
        bar=default;
        var a=Scan(data,stride,h,y+dy,left,width); var b=Scan(data,stride,h,y+dy+1,left,width);
        static bool Agree((int Fill,bool Ally) p,(int Fill,bool Ally) q) =>
            p.Fill>=2 && q.Fill>=2 && p.Ally==q.Ally && Math.Abs(p.Fill-q.Fill)<=2;
        if (!Agree(a,b)) {
            if (a.Fill>=2 && b.Fill>=2 && a.Ally!=b.Ally) return false;
            // A tiny effect may obscure one row. Require an independent third row AND the lower rim.
            var c=Scan(data,stride,h,y+dy-1,left,width);
            if (!LowerRim(data,stride,y,(y+dy)*stride,left,width,barHeight,dy)) return false;
            if (Agree(a,c)) b=c; else if (Agree(b,c)) a=c; else return false;
        }
        bar=new(left,y,width,barHeight,(a.Fill+b.Fill+1)/2,a.Ally); return true;
    }
    private static bool LowerRim(ReadOnlySpan<byte> data, int stride, int y, int middle, int x, int width, int barHeight, int dy)
    {
        int n = width - 6;
        if (n <= 0) return false;
        for (int j = Math.Max(dy + 2, barHeight - 2); j < barHeight; j++) {
            int row = (y + j) * stride, dark = 0, contrast = 0;
            for (int dx = 3; dx < width - 3; dx++) {
                int v = data[row + x + dx];
                if (v <= 42) dark++;
                if (data[middle + x + dx] - v >= 6) contrast++;
            }
            if (dark * 5 >= n * 4 && contrast * 3 >= n) return true;
        }
        return false;
    }
    private static (int Fill, bool Ally) Scan(ReadOnlySpan<byte> data, int stride, int h, int yy, int left, int width)
    {
        int row = yy * stride, uvRow = (h + yy / 2) * stride, filled = 0, colored = 0;
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
