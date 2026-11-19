using System.Text.Json;
using DotaCoach.TwoPc;

internal static class LaneRoleTests
{
    public static void Run(Action<bool,string> check)
    {
        static CreepBar B(int x,int y,bool ally)=>new(x,y,80,8,12,ally);
        CreepBar[] wave=[B(500,340,true),B(500,380,true),B(500,420,true),B(300,380,true),
            B(570,340,false),B(570,380,false),B(570,420,false),B(770,380,false)];
        var drill=new LaneDrill(.53); var marks=new List<DrillMark>();
        for(int i=0;i<20;i++) drill.Update(wave,i*.02,marks);
        check(drill.VisibleTypes==(0,0,8) && marks.Count==0,"formation while walking is not enough to classify by position");
        wave[1]=wave[1] with {Fill=10}; wave[5]=wave[5] with {Fill=10};
        for(int i=20;i<=38;i++) drill.Update(wave,i*.02,marks);
        check(drill.VisibleTypes==(6,2,0) && marks.Count==0,"sustained 4v4 engagement identifies two back lines before either is lethal");
        wave[3]=wave[3] with {Fill=10}; wave[7]=wave[7] with {Fill=10};
        drill.Update(wave,.78,marks);
        check(marks.Count==2 && marks.Any(m=>m.Bar.X==300 && m is {CreepType:LaneCreepType.Ranged,Plan.Action:HitAction.Deny}) &&
            marks.Any(m=>m.Bar.X==770 && m is {CreepType:LaneCreepType.Ranged,Plan.Action:HitAction.Finish}),
            "same 10/78 fill: ranged gets finish/deny immediately; both melee fronts stay silent");
        drill.Update([wave[3],wave[7]],.80,marks);
        check(marks.Count==2 && drill.VisibleTypes==(0,2,0),"known ranged type survives other creeps dying");
        drill.Update([],.82,marks); drill.Update([wave[7]],.84,marks); drill.Update([wave[7]],.86,marks);
        check(marks.Count==0 && drill.VisibleTypes==(0,0,1),"a new occupant at the same coordinates does not inherit ranged type");
        var ambiguous=wave.Select(b=>new LaneFormation.Sample(b,true)).ToArray(); var roles=new LaneCreepType[8];
        var partial=ambiguous.Where((_,i)=>i!=4 && i!=6).ToArray();
        check(LaneFormation.TryRoles(partial,roles.AsSpan(0,6)) && roles.Take(4).Count(x=>x==LaneCreepType.Ranged)==1 &&
            roles[4]==LaneCreepType.Unknown && roles[5]==LaneCreepType.Unknown,
            "4v2 with both back lines: classify only the intact four; the partial side remains unknown");
        ambiguous[7]=new(wave[7] with {X=600},true);
        check(!LaneFormation.TryRoles(ambiguous,roles) && !LaneFormation.TryRoles(ambiguous.AsSpan(0,5),roles.AsSpan(0,5)),
            "no opposed back lines, or too few visible creeps: abstain");
        var tower=new LaneDrill(.53); var unit=B(800,420,false);
        for(int i=0;i<140;i++) tower.Update([unit with { Fill=i<4?68:i<45?45:22 }],i*.02,marks);
        check(tower.VisibleTypes==(0,1,0),"two ranged-sized tower hits retain ranged identity after the tower stops");
        tower.Update([unit with {Fill=10}],2.80,marks);
        for(int i=141;i<175;i++) tower.Update([unit with {Fill=10}],i*.02,marks);
        check(marks is [{CreepType:LaneCreepType.Ranged, Plan.Action:HitAction.Finish}],"verified ranged type applies to later non-tower last hit");

        var learn=new LaneDrill(.53);
        var early=(CreepBar[])wave.Clone();
        early[7]=early[7] with {Fill=70}; // one already-low allied rear, one healthy opposing rear
        double? since=null;
        for(int i=0;i<32;i++) {
            if(i==4) {early[0]=early[0] with {Fill=early[0].Fill-2};early[4]=early[4] with {Fill=early[4].Fill-2};}
            learn.Update(early,i*.02,marks);
            foreach(var m in marks) if(m.Plan.Action==HitAction.Deny && m.FirstLethalPixelAt.HasValue) since=m.FirstLethalPixelAt;
        }
        check(since==0,"if identity arrives late, first lethal pixel timing includes time spent identifying the target");
        for(int i=32;i<88;i++) learn.Update([early[7] with {Fill=i<36?70:i<77?59:48}],i*.02,marks);
        check(learn.VisibleTypes==(0,0,1),"corroborated conflicting tower evidence revokes an inferred ranged identity");
    }

    // Offline spatial cross-check only. Production takes visible CreepBars, never replay unit names/positions.
    // Five-second snapshots cannot validate the sustained-exchange gate, tracking, camera or recognition.
    public static void Audit(string path)
    {
        using var doc=JsonDocument.Parse(File.ReadAllText(path));
        int frames=0;
        int[] groups=new int[2], accepted=new int[2], nearCorrect=new int[2], nearTotal=new int[2], farCorrect=new int[2], farTotal=new int[2];
        foreach(var m in doc.RootElement.GetProperty("matches").EnumerateArray())
        foreach(var f in m.GetProperty("frames").EnumerateArray()) {
            frames++;
            var u=f.GetProperty("units").EnumerateArray().Where(a=>a.GetProperty("kind").GetString()=="lane")
                .Select(a=>(X:a.GetProperty("x").GetDouble(),Y:a.GetProperty("y").GetDouble(),Team:a.GetProperty("team").GetInt32(), Name:a.GetProperty("name").GetString()!)).ToArray();
            var seen=new bool[u.Length];
            for(int i=0;i<u.Length;i++) {
                if(seen[i]) continue;
                var ids=new List<int>{i}; seen[i]=true;
                for(int n=0;n<ids.Count;n++) for(int j=0;j<u.Length;j++) {
                    if(seen[j]) continue;
                    double dx=u[j].X-u[ids[n]].X,dy=u[j].Y-u[ids[n]].Y;
                    if(dx*dx+dy*dy<=675*675) { seen[j]=true; ids.Add(j); }
                }
                int allies=ids.Count(k=>u[k].Team==2), enemies=ids.Count-allies;
                if(Math.Max(allies,enemies)!=4 || Math.Min(allies,enemies)<2) continue;
                int g=ids.Count==8?0:1; groups[g]++;
                var samples=ids.Select(k=>new LaneFormation.Sample(new((int)Math.Round(u[k].X*.66),(int)Math.Round(-u[k].Y*.66/1.3),
                    80,8,60,u[k].Team==2),true)).ToArray();
                var roles=new LaneCreepType[ids.Count]; if(!LaneFormation.TryRoles(samples,roles)) continue;
                accepted[g]++;
                for(int j=0;j<ids.Count;j++) {
                    string truth=u[ids[j]].Name;
                    if(roles[j]==LaneCreepType.Ranged) { farTotal[g]++; if(truth.EndsWith("_ranged")) farCorrect[g]++; }
                    else if(roles[j]==LaneCreepType.Melee) { nearTotal[g]++; if(truth.EndsWith("_melee") || truth.EndsWith("_flagbearer")) nearCorrect[g]++; }
                }
            }
        }
        Console.WriteLine(JsonSerializer.Serialize(new{frames,sets=Enumerable.Range(0,2).Select(g=>new{group=g==0?"4v4":"4v2/3",
            eligible=groups[g],accepted=accepted[g],meleeCorrect=nearCorrect[g],meleeLabels=nearTotal[g],rangedCorrect=farCorrect[g],rangedLabels=farTotal[g]})}));
    }
}
