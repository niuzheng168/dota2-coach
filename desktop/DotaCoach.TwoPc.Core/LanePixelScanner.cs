namespace DotaCoach.TwoPc;

/// <summary>Between discovery frames, validate fresh pixels near each visible bar.
/// Never publish last frame's bar as a substitute for missing pixels.</summary>
public sealed class LanePixelScanner
{
    private readonly List<CreepBar> previous = new(32);
    private int width, height;
    private long fullAt, last;
    public int FullScans { get; private set; }
    public int LocalScans { get; private set; }

    public void Read(ReadOnlySpan<byte> nv12, int w, int h, int pitch, long ticks, List<CreepBar> bars)
    {
        bool continuous=w==width && h==height && ticks>last && ticks-last<=50*TimeSpan.TicksPerMillisecond;
        if(!continuous) previous.Clear();
        bool full = !continuous || ticks-fullAt>=30*TimeSpan.TicksPerMillisecond ||
            previous.Count is 0 or >24;
        width=w; height=h; last=ticks;
        if (!full) {
            bars.Clear();
            foreach (var b in previous) {
                if(CreepBars.ReadAt(nv12,pitch,h,b,out var exact)) {
                    if(!bars.Contains(exact)) bars.Add(exact);
                    continue;
                }
                int dx=b.Width/3, dy=b.Width/4;
                CreepBars.ReadRegion(nv12,w,h,pitch,bars,b.X-dx,b.Y-dy,b.X+dx+1,b.Y+dy+1);
            }
            LocalScans++;
            full = bars.Count<previous.Count; // Fast motion, occlusion or camera cut: reacquire now.
        }
        if (full) {
            CreepBars.Read(nv12,w,h,pitch,bars); fullAt=ticks; FullScans++;
            foreach(var old in previous) {
                if(old.Fill>old.Width*.4 || bars.Count>=64) continue;
                bool occupied=false;
                foreach(var b in bars) if(Math.Abs(b.X-old.X)<=2 && Math.Abs(b.Y-old.Y)<old.Height) {occupied=true; break;}
                if(!occupied && CreepBars.ReadAt(nv12,pitch,h,old,out var sliver) && sliver.Fill==1) bars.Add(sliver);
            }
        }
        previous.Clear(); previous.AddRange(bars);
    }
}
