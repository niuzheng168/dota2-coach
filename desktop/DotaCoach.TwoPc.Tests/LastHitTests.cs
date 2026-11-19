using System.Text.Json;
using DotaCoach.TwoPc;

internal static class LastHitTests
{
    public static void Run(Action<bool,string> check)
    {
        var drill = new LaneDrill(.53);
        check(drill.Rehearse(false, 150, 1.4) is { Action: HitAction.Prepare, PrepHits: 1, TowerHits: 1 },
            "150 HP melee: one setup hit then one tower shot, enough time to recover");
        check(drill.Rehearse(false, 150, .4) == null, "same HP, but tower arrives too soon for setup and recovery");
        check(drill.Rehearse(true, 118, 1) is { PrepHits: 0, TowerHits: 1, Action: HitAction.Wait },
            "ranged creep needs no setup; wait to land after the tower");
        check(drill.Rehearse(false, 30, .9) is { Action: HitAction.Finish, TowerHits: 0 }, "take the kill before the tower");
        check(drill.Rehearse(false, 30, .2) == null, "tower kills the creep before Luna's projectile");
        check(TowerLastHit.Plan(new(175,175),new(30,30),.2,.65,0,0,new(100,100),2.4,1)
            is { PrepHits: 2, TowerHits: 1, Action: HitAction.Prepare }, "can require two separate setup attacks");
        check(TowerLastHit.Plan(new(98,102),new(30,30),.2,1,0,0,new(98,102),.2,.82) == null,
            "tower damage roll straddles death: do not promise last hit");
        check(TowerLastHit.Plan(new(30,30),new(30,30),.2,1,1,0,new(98,102),.6,.82) == null,
            "already on attack cooldown: cannot take an otherwise lethal hit");
        check(drill.Rehearse(true, 301, 1) == null && drill.Rehearse(false, 120, double.NaN) == null,
            "invalid drill calculation inputs");

        const int w=1920, h=1080;
        var nv = new byte[w*h*3/2]; Array.Fill(nv, (byte)110, 0, w*h); Array.Fill(nv,(byte)128,w*h,w*h/2);
        void Bar(int x,int y,int width,int fill,bool ally) {
            for (int j=0;j<7;j++) for(int i=0;i<width;i++) {
                bool rim=j==0||j==6||i==0||i==width-1;
                nv[(y+j)*w+x+i]=(byte)(rim?16:i<=fill?85:32);
                int uv=w*h+(y+j)/2*w+((x+i)&~1); nv[uv]=108; nv[uv+1]=(byte)(ally?105:158);
            }
        }
        Bar(200,280,80,5,false); Bar(550,340,80,4,true);
        Bar(800,350,112,4,false); Bar(200,980,80,4,false);
        var bars=CreepBars.Read(new(w,h,nv));
        check(bars.Count==2 && bars[0] is { X:200, Fill:5, Ally:false } && bars[1] is { Fill:4, Ally:true },
            "enemy/ally fill is measured on local pixels; longer hero bars and bottom HUD excluded");
        var health = new LaneDrill(); var values = new List<LaneDrill.Reading>();
        health.ReadHealth(bars, 0, values);
        check(values is [{Value:35, RangedAlternative:19}, {Value:28, RangedAlternative:15}],
            "NV12 to live readout: the first picture shows both creeps, even without classified types");
        Bar(200,280,80,2,false);
        health.ReadHealth(CreepBars.Read(new(w,h,nv)), .016, values);
        check(values is [{Value:14, RangedAlternative:8, ChangedAt:.016}, {Value:28, RangedAlternative:15, ChangedAt:0}],
            "a 3-pixel loss changes the enemy number on the very next picture, without delaying the ally");
        drill.Update(bars,1);drill.Update(bars,1.04);var marks=drill.Update(bars,1.08);
        check(marks.Count==1 && marks[0].Plan.Action==HitAction.Deny, "conservative deny, no highlight for an uncertain enemy kill");
        check(drill.Update([],1.12).Count==0 && drill.Update(bars,1.16).Count==0,"no ghost marks after target disappearance");
        var b = new CreepBar(300,300,80,8,78,false);
        List<DrillMark> Track(double t,int fill) => drill.Update([b with { Fill=fill }],t);
        for(int i=0;i<=58;i++) {
            // Start with the real tower trace 388 → 310 → 231 HP. Then inject a hero impact
            // (about 49 HP) and the next tower impact; the reminder must not ask for a second prep.
            int fill=i<4?56:i<24?44:i<37?33:i<45?26:15;
            marks=Track(2+i*.04,fill);
            if(i==5) check(marks.Count==0,"single damage drop does not identify a tower");
            if(i==25) check(marks.Count==0 && drill.VisibleTypes.Melee==1,
                "two matching tower hits identify melee but a setup plan is not a visible cue");
            if(i==40) check(marks.Count==0, "a tower wait plan never becomes a visible cue");
            if(i==58) check(marks.Single().Plan is { Action: HitAction.Finish, PrepHits: 0, TowerHits: 1 },
                "release Luna's final projectile before the final tower shot lands");
        }
        check(drill.Update([b with {Fill=15},b with {X=b.X+5,Fill=14}],4.36).Count==0,
            "overlapping targets do not inherit tower evidence");
        check(Track(5,3).Count==0,"stale image cannot retain a plan");

        var fast = new LaneDrill(.53); var low = new[] { b with { Fill = 3 } };
        bool early = false;
        for (int i=0; i<3; i++) early |= fast.Update(low, i / 240d).Count != 0;
        check(!early && fast.Update(low, 3/240d) is [{ Plan.Action: HitAction.Finish, FirstLethalPixelAt:0 }],
            "two independent timestamps spanning 12 ms are required; reaction is timed from the first qualifying frame");
        check(fast.Update(low,4/240d) is [{ FirstLethalPixelAt:null, DirectEvidenceAt:0 }] && fast.DirectCandidates==1,
            "one sustained lethal window is counted once, not once per refresh");
        check(fast.Update(low,.10).Count==0 && fast.Update(low,.10).Count==0 &&
            fast.Update(low,.119) is [{Plan.Action:HitAction.Finish}],
            "no confirmation across a stale gap or by replaying the same timestamp");

        var training = new LaneDrill(.53); var reuse = new List<DrillMark>();
        var warn = new[] { b with { Fill = 9 } };
        training.Update(warn,0,reuse); training.Update(warn,.025,reuse);
        check(reuse.Count==0,"an established low-HP target alone produces no mark");
        training.Update(warn,.06,reuse); training.Update(warn,.09,reuse); training.Update(low,.12,reuse);
        check(reuse.Count==0,"mixed damage produces no mark until a kill is actionable");
        for(int i=4;i<=17;i++) training.Update(low,i*.04,reuse);
        check(reuse is [{Plan.Action:HitAction.Finish}],"confirmed kill produces gold");
        training.Update([low[0],low[0] with {X=b.X+3}],.72,reuse);
        check(reuse.Count==0,"crossing targets clear the kill");

        var chip=new LaneDrill(.53); var chipBar=b with { Fill=7 };
        for(int i=0;i<8;i++) chip.Update([chipBar],i*.018,reuse);
        chip.Update(low,.144,reuse);
        check(reuse.Count==0, "isolated small drop does not declare a kill on the impact frame");
        chip.Update(low,.162,reuse);
        check(reuse is [{ Plan.Action:HitAction.Finish, FirstLethalPixelAt:.144 }],
            "isolated sub-hero hit: reconfirm stable lethal pixels in 18 ms, not a projectile flight");
        var fight=new LaneDrill(.53);
        for(int i=0;i<8;i++) fight.Update([b with { Fill=11 }],i*.018,reuse);
        fight.Update([b with {Fill=7}],.144,reuse);
        fight.Update(low,.162,reuse); fight.Update(low,.18,reuse);
        check(reuse.Count==0, "two closely spaced small hits cannot bypass mixed-damage protection");
        var uncertain=new LaneDrill(.53);
        for(int i=0;i<8;i++) uncertain.Update([b with {Fill=10}],i*.018,reuse);
        uncertain.Update(low,.144,reuse);
        for(int i=1;i<=30;i++) uncertain.Update(low,.144+i*.018,reuse);
        check(reuse is [{ FirstLethalPixelAt:.144 }] && uncertain.DirectCandidates==1,
            "later confirmed ambiguous runs keep their original lethal timestamp; the half-second delay is not hidden");
        LaneRoleTests.Run(check);
        HealthReadoutTests.Run(check);

        // 4K -> 1080p: top begins one pixel inside the left edge; the fill and chroma must
        // still be read from the true side. Test a split rim and a near-lethal partial fill.
        void Soft(int x, int fill, bool ally, bool bottom = true) {
            Bar(x,420,80,fill,ally);
            nv[420*w+x]=65;
            nv[420*w+x+30]=32; nv[420*w+x+31]=30;
            for(int j=1;j<=5;j++) nv[(420+j)*w+x]=33;
            if (!bottom) Array.Fill(nv,(byte)80,426*w+x,80);
        }
        Soft(900,4,false); Soft(1150,38,true); Soft(1380,4,false,false);
        Soft(1550,4,true); Array.Fill(nv,(byte)33,420*w+1551,79);
        var softBars=CreepBars.Read(new(w,h,nv)).Where(v=>v.Y==420).ToArray();
        check(softBars is [{X:900,Fill:4,Width:80,Ally:false},{X:1150,Fill:38,Width:80,Ally:true}],
            "4K split rim and offset side preserve both fill lengths; no lower rim or weak-only top is rejected");

        // DXGI may align rows beyond the visible width. Vary x across all phases of the sparse
        // probe grid, including the minimum qualifying rim length and a second adjacent rim.
        const int pitch = w+128;
        var padded = new byte[pitch*h*3/2]; Array.Fill(padded, (byte)110);
        var scratch = new List<CreepBar>(8);
        bool all = true;
        for (int i=0;i<80;i++) {
            for (int j=0;j<7;j++) Array.Fill(padded,(byte)110,(280+j)*pitch,w);
            int x = 2+i, width=77+i%7;
            foreach (int left in new[]{ x, x+width+1 }) {
                for(int j=0;j<7;j++) for(int dx=0;dx<width;dx++) {
                    padded[(280+j)*pitch+left+dx]=(byte)(j is 0 or 6 || dx==0 || dx==width-1?16:dx<=4?85:32);
                    int uv=(h+(280+j)/2)*pitch+((left+dx)&~1); padded[uv]=108; padded[uv+1]=158;
                }
            }
            CreepBars.Read(padded,w,h,pitch,scratch);
            all &= scratch.Count==2 && scratch[0]==new CreepBar(x,280,width,8,4,false)
                && scratch[1]==new CreepBar(x+width+1,280,width,8,4,false);
        }
        check(all,"padded NV12: detect adjacent bars at every probe alignment and width tolerance");
        long bytes=GC.GetAllocatedBytesForCurrentThread();
        for(int i=0;i<16;i++) CreepBars.Read(padded,w,h,pitch,scratch);
        check(GC.GetAllocatedBytesForCurrentThread()-bytes<1024,"no per-pixel or full-frame allocation in practice scan");

        JsonElement Payload(string map="hero_demo_main",string match="0",bool paused=false,int level=1,bool daytime=true) =>
            JsonSerializer.SerializeToElement(new { map=new { name=map,matchid=match,clock_time=5,game_state="DOTA_GAMERULES_STATE_GAME_IN_PROGRESS",paused,daytime },
                player=new {team_name="radiant"},hero=new {name="npc_dota_hero_luna",alive=true,health=600,mana=300,level} });
        bool Allows(JsonElement p) => GsiReceiver.IsLunaDrill(p,GsiState.Parse(p));
        check(Allows(Payload()) && !Allows(Payload("dota")) && !Allows(Payload(match:"123")) &&
            !Allows(Payload(paused:true)) && !Allows(Payload(level:2)) && !Allows(Payload(daytime:false)),
            "standalone practice only accepts the actual local level-one daytime hero demo");
        var paused=Payload(paused:true); var other=Payload("dota");
        check(GsiReceiver.DescribePractice(paused,GsiState.Parse(paused)) is { Enabled:false, Message:var message } &&
            message.Contains("暂停") && !GsiReceiver.DescribePractice(other,GsiState.Parse(other)).Enabled && !PracticeGate.NoSignal.Enabled,
            "a pause, a foreign map, or no fresh player signal cannot authorize capture");
    }
}
