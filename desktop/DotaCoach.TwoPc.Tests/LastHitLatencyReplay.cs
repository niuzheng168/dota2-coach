using System.Diagnostics;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class LastHitLatencyReplay
{
    // Real wall clock, in-memory NV12 input. No game, WGC, GPU work, network, or visible window.
    // The trigger is the first frame with the lethal fill, NOT the last confirming frame.
    private const int W = 1920, H = 1080, Trials = 24;
    private static long Now => Stopwatch.GetElapsedTime(0).Ticks;
    public static void Run(bool verify = true)
    {
        var frames = new Dictionary<int,byte[]>();
        foreach (int fill in new[] { 4,5,7,10 }) {
            var nv = new byte[W*H*3/2]; Array.Fill(nv,(byte)80,0,W*H); Array.Fill(nv,(byte)128,W*H,W*H/2);
            for (int i=0;i<9;i++) {
                int x=280+i*130, y=350+i%3*70, length=i==0 ? fill : 62;
                for(int j=0;j<7;j++) for(int k=0;k<80;k++) {
                    nv[(y+j)*W+x+k]=(byte)(j is 0 or 6 || k is 0 or 79 ? 16 : k<=length ? 85 : 32);
                    int uv=(H+(y+j)/2)*W+((x+k)&~1); nv[uv]=108; nv[uv+1]=158;
                }
            }
            frames.Add(fill,nv);
        }
        using var timer = new PreciseTimer();
        var bars=new List<CreepBar>(16); var marks=new List<DrillMark>(4);
        var cpu=new List<double>(); var results=new List<object>(); bool pass=true;
        static double P95(List<double> v) => v.Order().ElementAt((int)Math.Ceiling(v.Count*.95)-1);
        foreach (var (name, prior) in new[]{("new-target",0),("tracked-threshold",5),("isolated-small-hit",7),("ambiguous-hero-sized-hit",10)}) {
            var ms=new List<double>(); int missing=0, falseActions=0;
            for(int trial=0;trial<Trials;trial++) {
                var drill=new LaneDrill(.53);
                bool Step(int fill,long sourceAt) {
                    long start=Now;
                    CreepBars.Read(frames[fill],W,H,W,bars);
                    drill.Update(bars,sourceAt/(double)TimeSpan.TicksPerSecond,marks);
                    bool hit=marks.Count!=0 && marks[0].Plan.Action==HitAction.Finish && marks[0].Bar.X==280;
                    cpu.Add((Now-start)/(double)TimeSpan.TicksPerMillisecond); return hit;
                }
                long period=TimeSpan.TicksPerSecond/(trial%2==0?55:60);
                // Established targets have a quiet, measured history BEFORE the trigger. Priming is
                // outside reaction time, not an artificial warm-up for a newly appearing target.
                if(prior!=0) {
                    long origin=Now;
                    for(int i=10;i>0;i--) if(Step(prior,origin-i*period)) throw new Exception("Premature gold cue");
                }
                long first=Now, next=first; bool seen=false;
                for(;;) {
                    timer.Wait(next-Now); long at=Now;
                    if(Step(4,at)) {
                        if(prior==10) falseActions++; else ms.Add((Now-first)/(double)TimeSpan.TicksPerMillisecond);
                        seen=true; break;
                    }
                    // This ambiguous target disappears at ~120 ms. Withholding is expected, not a 0-ms success.
                    if(at-first >= (prior==10 ? 120 : 720)*TimeSpan.TicksPerMillisecond) break;
                    next=at+period;
                }
                if(!seen && prior!=10) missing++;
            }
            int late=ms.Count(v=>v>50);
            results.Add(new { scenario=name, trials=Trials, decisions=ms.Count, missing, falseActions, over50=late,
                p95Ms=ms.Count==0?(double?)null:Math.Round(P95(ms),3), maxMs=ms.Count==0?(double?)null:Math.Round(ms.Max(),3) });
            pass &= missing==0 && falseActions==0 && (prior==10 || P95(ms)<=50);
        }
        Console.WriteLine(JsonSerializer.Serialize(new { input="paced synthetic 1080p NV12; 55/60 Hz; no WGC or WPF", results,
            scanAndDecision=new { frames=cpu.Count, p95Ms=Math.Round(P95(cpu),3), maxMs=Math.Round(cpu.Max(),3) } }));
        if(verify && !pass) throw new Exception("Offline decision latency / correctness regression");
    }
}
