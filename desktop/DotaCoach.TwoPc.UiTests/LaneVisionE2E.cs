using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DotaCoach.Desktop;
using DotaCoach.TwoPc;

// From prepared NV12 pixels through the production detector/tracker/layout/renderer to
// actual rasterized pixels. Does not read the desktop, send keys, or start a game.
internal static class LaneVisionE2E
{
    const int W=1920,H=1080, Pitch=W+64;
    static void Require(bool ok,string msg) { if(!ok) throw new InvalidOperationException(msg); }
    static double Ms(long start)=>Stopwatch.GetElapsedTime(start).TotalMilliseconds;
    static double P95(List<double> a)=>Math.Round(a.Order().ElementAt((int)Math.Ceiling(.95*a.Count)-1),3);
    static byte[] Frame() { var b=new byte[Pitch*(H+H/2)]; Array.Fill(b,(byte)101,0,Pitch*H); Array.Fill(b,(byte)128,Pitch*H,Pitch*H/2); return b; }
    static void Bar(byte[] p,int x,int y,int fill,bool ally,int corrupt=-1,int width=80) {
        for(int j=0;j<7;j++) for(int i=0;i<width;i++) {
            bool rim=j is 0 or 6 || i==0 || i==width-1;
            p[(y+j)*Pitch+x+i]=(byte)(rim?16:j==corrupt?37:i<=fill?85:32);
            int uv=(H+(y+j)/2)*Pitch+((x+i)&~1); p[uv]=108; p[uv+1]=(byte)(ally?105:158);
        }
    }

    public static void Run()
    {
        var ink=new HealthInk(); var layout=new HealthLayout();
        int frames=0, rendered=0, matched=0, maxErrorPx=0, projected=0, correct=0, vetoes=0, recoveredRows=0;
        var cpu=new List<double>(); var drawEvents=new List<double>(); var errors=new List<double>(); var etaErrors=new List<double>();
        static int Fill(double hp,int maximum)=>(int)Math.Round(78*hp/maximum,MidpointRounding.AwayFromZero);
        void Paint(LaneDrill.Reading reading) {
            layout.Arrange([reading],W,H);
            var group=layout.Tiles.Single(t=>t.Tags.Count!=0); var b=group.Tags.Single().Box;
            var visual=new DrawingVisual();
            using(var dc=visual.RenderOpen()) ink.Draw(dc,[new(new(0,0,b.Width,b.Height),reading)],b.Width,b.Height,new(b.Width,b.Height),1);
            var bitmap=new RenderTargetBitmap(b.Width,b.Height,96,96,PixelFormats.Pbgra32);
            bitmap.Render(visual); var pix=new byte[b.Width*b.Height*4]; bitmap.CopyPixels(pix,b.Width*4,0);
            Require(Enumerable.Range(0,pix.Length/4).Count(i=>pix[4*i]>180 && pix[4*i+3]>220)>10,"number not rendered");
            if(reading.Next!=null) Require(HealthInk.Label(reading).Contains('→') && b.Width>120,"forecast lost in layout/ink");
            rendered++;
        }

        // Different HP pools/damage rolls and frame rates. Third blow is held out:
        // its true time/damage are not supplied to the on-screen detector.
        foreach (int hz in new[]{60,150}) foreach ((int maximum,int d1,int d2,int d3) in new[]{
            (550,78,83,79),(550,82,79,80),(300,88,89,90),(300,90,88,92),(650,87,91,89),(600,91,87,92)}) {
            bool ranged=maximum==300;
            var profile=ranged?LaneHealthProfile.Standard:new LaneHealthProfile(maximum,300);
            var img=Frame(); var scanner=new LanePixelScanner(); var drill=new LaneDrill(hpProfile:profile);
            var bars=new List<CreepBar>(); var readings=new List<LaneDrill.Reading>();
            const double a=.10,b=.92,c=1.75;
            long lastRev=0; LaneForecast? held=null; double issued=0;
            for(int i=0;i<=Math.Ceiling((c+.04)*hz);i++) {
                double t=(double)i/hz; int hp=maximum-(t>=a?d1:0)-(t>=b?d2:0)-(t>=c?d3:0);
                int fill=Fill(hp,maximum);
                Bar(img,281,351,fill,false,i%11==3?3:-1); Bar(img,710,390,59,true);
                long at=TimeSpan.TicksPerSecond+(long)Math.Round(t*TimeSpan.TicksPerSecond);
                long start=Stopwatch.GetTimestamp();
                scanner.Read(img,W,H,Pitch,at,bars); drill.ReadLaneVision(bars,at/(double)TimeSpan.TicksPerSecond,readings);
                cpu.Add(Ms(start)); frames++;
                Require(readings.Count==2,"missing/extra health bar in known image");
                var r=readings.Single(v=>!v.Bar.Ally);
                Require(!HealthInk.Label(r).Contains('%') && readings.Single(v=>v.Bar.Ally).Next==null,"wrong units or team");
                if(t<b) Require(r.Type==LaneCreepType.Unknown && r.RangedAlternative.HasValue,"classified an isolated bar too early");
                else Require(r.Type==(ranged?LaneCreepType.Ranged:LaneCreepType.Melee) && r.RangedAlternative==null,"wrong confirmed HP scale");
                int err=Math.Abs(fill-r.Bar.Fill); maxErrorPx=Math.Max(maxErrorPx,err); matched++;
                int shown=ranged?r.RangedAlternative??r.Value:r.Value;
                errors.Add(Math.Abs(shown-hp)); if(i%11==3) recoveredRows++;
                Require(err==0 && errors[^1]<=maximum/156d+.5,"wrong fill or HP");
                if(t<b) Require(r.Next==null,"forecast before second impact");
                if(r.Next.HasValue && held==null && t<c) { held=r.Next; issued=t; }
                if(r.Revision!=lastRev) {
                    Paint(r);
                    drawEvents.Add(Ms(start)); lastRev=r.Revision;
                }
            }
            Require(held!=null,"abstained on clear confirmed rhythm"); projected++;
            var n=held!.Value; int actual=maximum-d1-d2-d3;
            if(actual>=n.LowHp && actual<=n.HighHp) correct++;
            etaErrors.Add(1000*Math.Abs(issued+n.EtaMs/1000d-c));
            Require(scanner.LocalScans>0 && scanner.FullScans>0,"not exercising both scanner paths");
        }
        Require(correct==projected,"held-out strike outside the displayed interval");

        // Different negative interventions after/while gathering otherwise valid evidence.
        foreach (var scenario in new[]{"irregular","different-size","chip","new-target","conflict","pause","overdue","allied","crossing","width-change"}) {
            var scanner=new LanePixelScanner(); var drill=new LaneDrill();
            var img=Frame(); var bars=new List<CreepBar>(); var values=new List<LaneDrill.Reading>();
            int second=scenario=="irregular"?33:56;
            for(int i=0;i<=(scenario=="overdue"?112:62);i++) {
                bool after=i>=60;
                if(scenario=="pause" && i is >=55 and <60) continue;
                int x=scenario=="new-target" && after?801:301;
                if(i==60 && scenario is "new-target" or "width-change") img=Frame();
                int f=74-(i>=7?12:0)-(i>=second?(scenario=="different-size"?20:12):0)-(after && scenario=="chip"?3:0);
                Bar(img,x,360,f,scenario=="allied",width:after && scenario=="width-change"?81:80);
                if(scenario=="conflict") Bar(img,810,443,i>=55?57:69,false);
                if(scenario=="crossing" && after) Bar(img,x+7,379,45,false);
                long at=TimeSpan.TicksPerSecond+i*TimeSpan.TicksPerSecond/60;
                scanner.Read(img,W,H,Pitch,at,bars); drill.ReadLaneVision(bars,at/(double)TimeSpan.TicksPerSecond,values);
                if((after && scenario!="overdue") || (scenario=="overdue" && i>=107)) {
                    Require(values.All(v=>v.Next==null),$"{scenario}: stale/false forecast on frame {i}");
                }
            }
            vetoes++;
        }

        // Positive control for a change that appears between periodic full scans, and a camera cut.
        var discovery=new LanePixelScanner(); var scene=Frame(); var list=new List<CreepBar>();
        Bar(scene,350,380,70,false); discovery.Read(scene,W,H,Pitch,1,list);
        Bar(scene,1250,520,26,true); int discoveredAt=-1;
        for(int i=1;i<=6;i++) { discovery.Read(scene,W,H,Pitch,1+i*70000,list); if(list.Count==2 && discoveredAt<0) discoveredAt=i*7; }
        Require(discoveredAt is >0 and <=42,"new bar starvation");
        scene=Frame(); Bar(scene,1540,610,44,false);
        discovery.Read(scene,W,H,Pitch,1+49*10000,list);
        Require(list is [{X:1540,Fill:44}],"camera cut retained old bars");
        scene=Frame(); Bar(scene,327,420,9,false);
        discovery.Read(scene,W,H,Pitch,10000000,list); Bar(scene,327,420,1,false);
        for(int i=1;i<=8;i++) { discovery.Read(scene,W,H,Pitch,10000000+i*70000,list);
            Require(list is [{Fill:1,Ally:false}],"one-pixel residual lost on a fresh ROI/full scan"); }
        Bar(scene,327,420,1,true); discovery.Read(scene,W,H,Pitch,10630000,list);
        Require(list.Count==0,"a one-pixel color reversal is not the previous enemy");
        var fl= new LaneDrill.Reading(new(8,50,80,8,19,false),LaneCreepType.Melee,134,1,1){Next=new(42,65,600)};
        layout.Arrange([fl],100,100);
        Require(layout.Tiles.SelectMany(tile=>tile.Tags).Single().Reading is {Next:null,Value:134},"current HP must survive when only the longer forecast cannot fit");

        // Paired same-image CPU wall times against forced full scans. Alternate order, warm both.
        var adaptive=new LanePixelScanner(); var global=new LanePixelScanner(); var localTimes=new List<double>(); var globalTimes=new List<double>();
        var bench=Frame(); for(int j=0;j<8;j++) Bar(bench,251+j*170,321+(j%3)*60,42,j%2==0);
        for(int i=0;i<360;i++) {
            void Time(LanePixelScanner scan,bool full,List<double> times) {
                long start=Stopwatch.GetTimestamp(); scan.Read(bench,W,H,Pitch,full ? 1 : 1+i*66667L,list);
                double elapsed=Ms(start); Require(list.Count==8,"paired scan mismatch"); if(i>=30) times.Add(elapsed);
            }
            if(i%2==0){ Time(global,true,globalTimes); Time(adaptive,false,localTimes); }
            else { Time(adaptive,false,localTimes); Time(global,true,globalTimes); }
        }
        var real=ReplayCrops(ink,layout);
        var paced=Paced();
        var output=new { synthetic = new { frames, matched, maxErrorPx, maxHpError=errors.Max(), recoveredRows,
            heldOutThirdShots=projected, insidePredictedInterval=correct, maxDisplayedEtaErrorMs=Math.Round(etaErrors.Max(),1),
            negativeScenarios=vetoes, trackedSinglePixelVerified=true, newBarDiscoveredMs=discoveredAt, rasterizedChanges=rendered },
            offlineMs=new { scanAndTrackingP95=P95(cpu), frameReadyToSoftwareBitmapP95=P95(drawEvents),
                frameReadyToSoftwareBitmapMax=Math.Round(drawEvents.Max(),3), over50=drawEvents.Count(t=>t>50),
                forcedFullMean=Math.Round(globalTimes.Average(),3), adaptiveMean=Math.Round(localTimes.Average(),3) },
            realCrops=real, pacedWorkerAndDispatcher=paced, excludes="WGC, desktop compositor, game rendering, monitor and player input; only pacedWorkerAndDispatcher runs on the real-time schedule" };
        Console.WriteLine(JsonSerializer.Serialize(output,new JsonSerializerOptions{WriteIndented=true}));
    }

    static long Now=>Stopwatch.GetElapsedTime(0).Ticks;
    static readonly int[] dropsAtMs=[213,1032,1857,2013,2121,2229,2332,2443,2546,2659,2770,2881];
    static readonly int[] fills=[77,66,55,44,41,38,35,32,29,26,23,20,17];
    sealed class LaneTape : ILaneFrames
    {
        readonly LanePixelScanner scan=new(); readonly byte[] pixels=Frame();
        public long Origin; long lastSlot=-1;
        public bool Ended=>false;
        public bool ReadPending=>false;
        public bool TryReadBars(List<CreepBar> destination,out long stamp,out int arrived,out long dequeued) {
            long now=Now; if(Volatile.Read(ref Origin)==0) Volatile.Write(ref Origin,now);
            long slot=(now-Origin)*60/TimeSpan.TicksPerSecond;
            stamp=dequeued=0; arrived=0; if(slot==lastSlot) return false;
            lastSlot=slot; stamp=Origin+slot*TimeSpan.TicksPerSecond/60;
            int ms=(int)((stamp-Origin)/TimeSpan.TicksPerMillisecond); if(ms>3020) return false;
            int index=0; while(index<dropsAtMs.Length && dropsAtMs[index]<=ms) index++;
            Bar(pixels,313,386,fills[index],false); Bar(pixels,711,442,41,true);
            dequeued=Now; arrived=1; scan.Read(pixels,W,H,Pitch,stamp,destination); return true;
        }
        public void Dispose() {}
    }
    sealed class BitmapOverlay(LaneTape tape) : ILaneOverlay
    {
        readonly HealthInk ink=new(); readonly HealthLayout layout=new();
        public bool Visible {get; private set;}
        public readonly Dictionary<int,double> Delays=new();
        public bool SawArrow,Cleared; public double? FirstMs;
        public void Warm() => ink.Glyph(new(default,LaneCreepType.Melee,550,0,0),1);
        public void Paint(GameSurface g,int w,int h,long at,LaneDrill.Reading[] values) {
            layout.Arrange(values,w,h);
            foreach(var tile in layout.Tiles) {
                if(tile.Tags.Count==0) continue;
                var b=tile.Bounds; var v=new DrawingVisual();
                using(var dc=v.RenderOpen()){
                    dc.PushTransform(new TranslateTransform(-b.X,-b.Y));
                    ink.Draw(dc,tile.Tags,w,h,new Size(w,h),1); dc.Pop();
                }
                var bm=new RenderTargetBitmap(b.Width,b.Height,96,96,PixelFormats.Pbgra32); bm.Render(v);
                byte[] p=new byte[b.Width*b.Height*4]; bm.CopyPixels(p,b.Width*4,0);
                Require(Enumerable.Range(0,p.Length/4).Any(i=>p[i*4]>200 && p[i*4+3]>220),"paced ink missing");
                foreach(var tag in tile.Tags) {
                    if(tag.Reading.Bar.Ally) continue;
                    double ms=(Now-Volatile.Read(ref tape.Origin))/(double)TimeSpan.TicksPerMillisecond;
                    FirstMs??=ms;
                    int k=Array.IndexOf(fills,tag.Reading.Bar.Fill);
                    Require(k>=0,"unexpected health in paced tape");
                    if(k>0) Delays.TryAdd(k,ms-dropsAtMs[k-1]);
                    SawArrow|=tag.Reading.Next!=null;
                }
            }
            Visible=true;
        }
        public void HideMarks(){ if(Visible) Cleared=true; Visible=false; }
        public void Close()=>HideMarks();
    }
    static object Paced()
    {
        var tape=new LaneTape(); var sink=new BitmapOverlay(tape);
        var surface=new GameSurface(0,0,0,W,H);
        var input=new LaneInput(()=>surface,_=>true,_=>true,(_,_,_,_)=>tape);
        var frame=new DispatcherFrame(); Exception? failure=null;
        var app=new LastHitPractice(()=>new(true,"test"),60,_=>{},true,true,input,sink);
        var timer=new DispatcherTimer {Interval=TimeSpan.FromMilliseconds(20)};
        long start=Now;
        timer.Tick+=async (_,_)=>{
            if((tape.Origin==0 || Now-Volatile.Read(ref tape.Origin)<3400*TimeSpan.TicksPerMillisecond) && Now-start<6*TimeSpan.TicksPerSecond) return;
            timer.Stop();
            try { Require(sink.Cleared && !sink.Visible,"capture stall failed to clear overlay"); }
            catch(Exception e){failure=e;}
            try { await app.DisposeAsync(); } catch(Exception e){failure=e;} finally {frame.Continue=false;}
        };
        timer.Start(); Dispatcher.PushFrame(frame); if(failure!=null) throw failure;
        Require(sink.SawArrow && sink.Delays.Count==dropsAtMs.Length,"paced fixture missed an impact or did not render tower projection");
        var ds=sink.Delays.Values.ToList();
        return new {inputHz=60, expectedLosses=dropsAtMs.Length, rasterizedLosses=ds.Count,
            scheduledLossToBitmapP95Ms=P95(ds),maxMs=Math.Round(ds.Max(),3),over50=ds.Count(t=>t>50),
            firstVisibleMs=Math.Round(sink.FirstMs!.Value,3), stallCleared=sink.Cleared};
    }

    static object ReplayCrops(HealthInk ink, HealthLayout layout) {
        string path=Path.Combine(AppContext.BaseDirectory,"Fixtures","lane-rims.json.gz");
        using var file=File.OpenRead(path); using var gz=new GZipStream(file,CompressionMode.Decompress);
        using var doc=JsonDocument.Parse(gz); int frames=0,total=0,match=0,other=0,tagCount=0;
        var scan=new LanePixelScanner(); var drill=new LaneDrill(); var bars=new List<CreepBar>(); var readings=new List<LaneDrill.Reading>();
        foreach(var frame in doc.RootElement.EnumerateArray()) {
            var p=Frame(); var candidates=frame.EnumerateArray().ToArray();
            foreach(var e in candidates) {
                int x=e.GetProperty("px").GetInt32(),y=e.GetProperty("py").GetInt32();
                var data=e.GetProperty("patch").GetBytesFromBase64(); Require(data.Length==96*21,"invalid crop");
                for(int j=0;j<14;j++) Array.Copy(data,j*96,p,(y+j)*Pitch+x,96);
                for(int j=0;j<7;j++) Array.Copy(data,(14+j)*96,p,(H+y/2+j)*Pitch+x,96);
            }
            frames++; long at=frames*TimeSpan.TicksPerSecond; scan.Read(p,W,H,Pitch,at,bars);
            drill.ReadLaneVision(bars,frames,readings); layout.Arrange(readings.ToArray(),W,H);
            int found=candidates.Count(e=>bars.Any(b=>Math.Abs(b.X-e.GetProperty("l").GetInt32())<=3 && Math.Abs(b.Y-e.GetProperty("y").GetInt32())<=3));
            foreach(var t in layout.Tiles) foreach(var tag in t.Tags) { Require(ink.Glyph(tag.Reading,1).IsFrozen,"invalid real label"); tagCount++; }
            total+=candidates.Length; match+=found; other+=bars.Count-found;
        }
        Require(total==30 && match>=28 && other==0,"real candidate-rim regression");
        return new {frames, candidateRims=total,matched=match,additionalDetectionsOnBlankBackground=other,labels=tagCount,
            note="selected real 4K→1080p crops, not complete frames or absolute HP truth"};
    }
}
