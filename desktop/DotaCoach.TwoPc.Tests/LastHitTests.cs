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
            if(i==25) check(marks.Single().Plan is { Action: HitAction.Prepare, PrepHits: 1, TowerHits: 2 },
                "tracked melee creep: after two matching tower hits there is time for one prep before two more shots");
            if(i==40) check(marks.Single().Plan is { Action: HitAction.Wait, PrepHits: 0 },
                "after the actual setup hit, wait instead of asking for a second attack");
            if(i==58) check(marks.Single().Plan is { Action: HitAction.Finish, PrepHits: 0, TowerHits: 1 },
                "release Luna's final projectile before the final tower shot lands");
        }
        check(drill.Update([b with {Fill=15},b with {X=b.X+5,Fill=14}],4.36).Count==0,
            "overlapping targets do not inherit tower evidence");
        check(Track(5,3).Count==0,"stale image cannot retain a plan");

        JsonElement Payload(string map="hero_demo_main",string match="0",bool paused=false,int level=1,bool daytime=true) =>
            JsonSerializer.SerializeToElement(new { map=new { name=map,matchid=match,clock_time=5,game_state="DOTA_GAMERULES_STATE_GAME_IN_PROGRESS",paused,daytime },
                player=new {team_name="radiant"},hero=new {name="npc_dota_hero_luna",alive=true,health=600,mana=300,level} });
        bool Allows(JsonElement p) => GsiReceiver.IsLunaDrill(p,GsiState.Parse(p));
        check(Allows(Payload()) && !Allows(Payload("dota")) && !Allows(Payload(match:"123")) &&
            !Allows(Payload(paused:true)) && !Allows(Payload(level:2)) && !Allows(Payload(daytime:false)),
            "standalone practice only accepts the actual local level-one daytime hero demo");
    }
}
