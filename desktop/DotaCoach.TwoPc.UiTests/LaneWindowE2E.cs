using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotaCoach.Desktop;
using DotaCoach.TwoPc;

// OS integration test. Never discover/activate/move/capture the user's Dota HWND.
// Windows stops supplying frames for wholly off-monitor windows. Place our non-activating
// test window at HWND_BOTTOM underneath the existing foreground window, never over the game.
internal static class LaneWindowE2E
{
    const int W=960,H=540;
    static long Now=>Stopwatch.GetElapsedTime(0).Ticks;
    [DllImport("user32.dll")] static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool SetWindowPos(nint h,nint after,int x,int y,int w,int ht,uint flags);
    [DllImport("user32.dll")] static extern bool ShowWindow(nint hwnd,int show);
    [DllImport("user32.dll")] static extern bool GetWindowRect(nint h,out Rect32 rect);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern nint CreateWindowExW(uint ex,string cls,string name,uint style,int x,int y,int w,int h,nint parent,nint menu,nint instance,nint arg);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] static extern nint GetDC(nint hwnd);
    [DllImport("user32.dll")] static extern int ReleaseDC(nint hwnd,nint dc);
    [DllImport("user32.dll")] static extern int FillRect(nint dc,in Rect32 rect,nint brush);
    [DllImport("gdi32.dll")] static extern nint CreateSolidBrush(int color);
    [DllImport("gdi32.dll")] static extern bool DeleteObject(nint handle);
    [DllImport("gdi32.dll")] static extern bool GdiFlush();
    sealed class Fixture(int x,int y) : IDisposable
    {
        internal readonly nint Handle=CreateWindowExW(0x08000080,"STATIC","DotaCoach generated pixel fixture",0x80000000,x,y,W,H,0,0,0,0);
        readonly nint[] brushes=[CreateSolidBrush(0x888888),CreateSolidBrush(0),CreateSolidBrush(0x181818),CreateSolidBrush(0x2549be),CreateSolidBrush(0xffffff)];
        int n;
        public void Draw(int fill) {
            nint dc=GetDC(Handle);
            try {
                void Box(int x,int y,int w,int h,int i)=>FillRect(dc,new Rect32{Left=x,Top=y,Right=x+w,Bottom=y+h},brushes[i]);
                if(n==0) Box(0,0,W,H,0);
                Box(310,180,40,5,1); Box(311,181,38,3,2); Box(311,181,fill,3,3);
                Box(8,8,7,7,(n++&1)==0?4:1); GdiFlush();
            } finally {ReleaseDC(Handle,dc);}
        }
        public void Dispose(){DestroyWindow(Handle);foreach(var b in brushes) DeleteObject(b);}
    }
    static readonly int[] fills=[37,31,25,19,16,13,10,7];
    static readonly int[] schedule=[213,1032,1857,2013,2121,2229,2332];
    sealed class Output(long[] issued) : ILaneOverlay
    {
        readonly HealthInk ink=new(); readonly HealthLayout layout=new();
        readonly LastHitPractice.Surface?[] surfaces=new LastHitPractice.Surface?[HealthLayout.Rows*HealthLayout.Columns];
        readonly Dictionary<(int,LaneForecast?,int,int),byte[]> references=new();
        public readonly Dictionary<int,double> seen=new();
        public bool Ready,Arrow;
        public bool Visible {get; private set;}
        public void Warm()=>ink.Glyph(new(default,LaneCreepType.Melee,550,0,0),1);
        public void Paint(GameSurface g,int w,int h,long at,LaneDrill.Reading[] values) {
            layout.Arrange(values,w,h);
            for(int i=0;i<layout.Tiles.Length;i++) {
                var tile=layout.Tiles[i]; if(tile.Tags.Count==0) continue;
                var b=tile.Bounds;
                var s=surfaces[i]??=new LastHitPractice.Surface(()=>true,_=>true,ink,null);
                s.Set(g,at,tile,false); s.Measure(new Size(b.Width,b.Height)); s.Arrange(new Rect(0,0,b.Width,b.Height));
                s.UpdateLayout();
                var bm=new RenderTargetBitmap(b.Width,b.Height,96,96,PixelFormats.Pbgra32);
                bm.Render(s); byte[] p=new byte[b.Width*b.Height*4]; bm.CopyPixels(p,b.Width*4,0);
                long rasterAt=Now;
                if(!Enumerable.Range(0,p.Length/4).Any(j=>p[j*4]>160 && p[j*4+3]>220)) continue; // Real OnRender suppresses stale observations.
                foreach(var tag in tile.Tags) {
                    var r=tag.Reading; int k=Array.IndexOf(fills,r.Bar.Fill);
                    if(k<0 || r.Bar.Ally) continue;
                    if(r.Value!=(fills[k]*550+19)/38 || tile.Tags.Count!=1 || HealthInk.Label(r).Contains('%') ||
                        (k<2 ? r.Type!=LaneCreepType.Unknown || r.RangedAlternative!=(fills[k]*300+19)/38 :
                        r.Type!=LaneCreepType.Melee || r.RangedAlternative.HasValue)) throw new Exception("wrong fixture HP");
                    // Matching some white ink is not enough: stale numbers can also be white.
                    var key=(r.Value,r.Next,b.Width,b.Height);
                    if(!references.TryGetValue(key,out var expected)) {
                        var v=new DrawingVisual();
                        using(var dc=v.RenderOpen()) ink.Draw(dc,[new(new(0,0,b.Width,b.Height),r)],b.Width,b.Height,new(b.Width,b.Height),1);
                        var refBitmap=new RenderTargetBitmap(b.Width,b.Height,96,96,PixelFormats.Pbgra32);
                        refBitmap.Render(v); expected=new byte[p.Length]; refBitmap.CopyPixels(expected,b.Width*4,0); references[key]=expected;
                    }
                    if(!p.AsSpan().SequenceEqual(expected)) continue;
                    if(k==0) Ready=true; else if(issued[k]!=0) seen.TryAdd(k,(rasterAt-issued[k])/(double)TimeSpan.TicksPerMillisecond);
                    Arrow|=r.Next!=null;
                }
            }
            Visible=true;
        }
        public void HideMarks()=>Visible=false;
        public void Close()=>HideMarks();
    }

    internal static void Run()
    {
        var foreground=GetForegroundWindow();
        if(foreground==0 || !GetWindowRect(foreground,out var cover) || cover.Right-cover.Left<W+64 || cover.Bottom-cover.Top<H+64)
            throw new Exception("No sufficiently large foreground window covering the synthetic test; not showing fixture");
        int x=(cover.Left+cover.Right-W)/2,y=(cover.Top+cover.Bottom-H)/2;
        using var host=new Fixture(x,y);
        if(host.Handle==0 || !SetWindowPos(host.Handle,1,x,y,W,H,0x0050) || GetForegroundWindow()!=foreground ||
            !GetWindowRect(host.Handle,out var rect)) throw new Exception("Could not keep fixture behind the original foreground");
        host.Draw(fills[0]);
        var surface=new GameSurface(host.Handle,rect.Left,rect.Top,W,H); long[] issued=new long[fills.Length];
        var sink=new Output(issued); int enabled=1;
        bool IsFixture(GameSurface s)=>s==surface && Volatile.Read(ref enabled)!=0;
        var input=new LaneInput(()=>Volatile.Read(ref enabled)!=0?surface:null,IsFixture,IsFixture,
            (s,w,h,fps)=>s==surface ? new WindowVideoSource(s,w,h,fps,limitPractice:true) : throw new Exception("not the fixture"));
        string status=""; var live=new LastHitPractice(()=>new(true,"fixture"),60,s=>status=s,true,true,input,sink);
        var frame=new DispatcherFrame(); var timer=new DispatcherTimer(DispatcherPriority.Render){Interval=TimeSpan.FromMilliseconds(9)};
        long start=Now, origin=0; int index=0; bool cleared=false; Exception? error=null;
        timer.Tick+=async (_,_)=>{
            long now=Now;
            bool covered=GetForegroundWindow()==foreground && GetWindowRect(foreground,out var bounds) &&
                rect.Left>=bounds.Left && rect.Top>=bounds.Top && rect.Right<=bounds.Right && rect.Bottom<=bounds.Bottom;
            if(!covered) { ShowWindow(host.Handle,0); Volatile.Write(ref enabled,0); }
            if(origin==0 && sink.Ready) origin=now;
            double ms=origin!=0?(now-origin)/(double)TimeSpan.TicksPerMillisecond:0;
            if(origin!=0 && index<schedule.Length && ms>=schedule[index]) issued[++index]=now;
            host.Draw(fills[index]);
            if(ms>2650) Volatile.Write(ref enabled,0);
            if(covered && ms<2890 && now-start<8*TimeSpan.TicksPerSecond) return;
            timer.Stop(); cleared=!sink.Visible && origin!=0;
            try { await live.DisposeAsync(); } catch(Exception e){error=e;} finally { frame.Continue=false; }
        };
        timer.Start(); Dispatcher.PushFrame(frame); if(error!=null) throw error;
        var delays=sink.seen.Values.Order().ToArray();
        Console.WriteLine(JsonSerializer.Serialize(new{source="covered, non-activating fixture HWND → WGC → D3D11/NV12 → worker/dispatcher → production WPF Surface → software bitmap",
            ready=sink.Ready,delivered=live.SuppliedFrames,recognized=live.DetectedFrames,expectedLosses=schedule.Length,
            rasterizedLosses=delays.Length, sawForecast=sink.Arrow,hiddenAfterFocusLoss=cleared,
            firstReadyMs=origin==0?(double?)null:Math.Round((origin-start)/(double)TimeSpan.TicksPerMillisecond,2),
            fixtureWriteToBitmapMs=delays.Select(d=>Math.Round(d,2)),over50=delays.Count(d=>d>50),status}));
        if(!sink.Ready || !sink.Arrow || !cleared || delays.Length!=schedule.Length) throw new Exception("WGC fixture incomplete");
    }
}
