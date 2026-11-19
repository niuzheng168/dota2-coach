using DotaCoach.TwoPc;

internal static class HealthReadoutTests
{
    internal static void Run(Action<bool,string> check)
    {
        static CreepBar B(int x, int y, bool ally) => new(x,y,80,8,39,ally);
        CreepBar[] wave = [B(500,340,true), B(500,380,true), B(500,420,true), B(300,380,true),
            B(570,340,false), B(570,380,false), B(570,420,false), B(770,380,false)];
        var drill = new LaneDrill();
        var values = new List<LaneDrill.Reading>();
        check(drill.ReadHealth(wave, 0, values) == 8 && values.All(r=>r is {Value:275,RangedAlternative:150}) &&
            values.Count(r=>r.Bar.Ally)==4, "first frame: unclassified bars get two explicitly conditional HP readings");
        long first = values[7].Revision;
        for (int i=1; i<=22; i++) {
            if (i==5) { wave[1]=wave[1] with {Fill=37}; wave[5]=wave[5] with {Fill=37}; }
            drill.ReadHealth(wave, i*.02, values);
        }
        check(drill.VisibleTypes==(6,2,0) && values[0] is {Type:LaneCreepType.Melee, Value:275} &&
            values[4].Value==275 && values[3] is {Type:LaneCreepType.Ranged, Value:150} && values[7].Value==150 &&
            values[7].Revision!=first, "same half bar: inferred allied/enemy melee and ranged switch to their own HP scale");
        var before = values[7];
        wave[7]=wave[7] with {X=wave[7].X+2};
        check(drill.ReadHealth(wave,.46,values)==0 && values[7].Revision==before.Revision &&
            values[7].ChangedAt==before.ChangedAt && values[7].Bar.X==wave[7].X, "movement updates position without restarting numeric latency");
        wave[7]=wave[7] with {Fill=4};
        check(drill.ReadHealth(wave,.48,values)==1 && values[7] is {Value:15, RangedAlternative:null, ChangedAt:.48} &&
            drill.DirectCandidates==0, "a large health loss updates immediately without running a lethal threshold or waiting for a tower");
        drill.ReadHealth([wave[7]],.50,values);
        check(values.Count==1 && values[0].Revision!=before.Revision && values[0].Value==15, "missing creeps disappear; the surviving label keeps its new value");
        drill.ReadHealth([],.52,values); check(values.Count==0, "an empty frame clears all health readings");
        drill.ReadHealth([wave[7]],.54,values);
        check(values is [{Type:LaneCreepType.Unknown,Value:28,RangedAlternative:15}] && values[0].ChangedAt==.54, "a replacement at the same spot cannot inherit the last occupant's type");
        long replacement=values[0].Revision;
        drill.ReadHealth([wave[7]],.65,values);
        check(values[0].Revision!=replacement && values[0].ChangedAt==.65, "a capture gap starts a fresh numeric observation");
        check(LaneHealthProfile.Estimate(wave[0] with {Fill=78},LaneHealthProfile.BaseMelee)==550 &&
            LaneHealthProfile.Estimate(wave[0] with {Fill=78},LaneHealthProfile.BaseRanged)==300 &&
            LaneHealthProfile.Estimate(wave[0] with {Width=42,Fill=20},475)==238,
            "estimates use the interior of the detected bar at its actual scale");
        var lane=new LaneDrill(hpProfile:new(562,312));
        for(int i=0;i<30;i++) {
            if(i==4) {wave[0]=wave[0] with {Fill=37};wave[4]=wave[4] with {Fill=37};}
            lane.ReadLaneVision(wave,i*.02,values);
        }
        check(lane.VisibleTypes==(6,2,0) && values[2] is {Value:281,RangedAlternative:null} &&
            values[3] is {Value:156,Type:LaneCreepType.Ranged,RangedAlternative:null} && values[3].LossRevision==0,
            "pure vision uses calibrated HP for both teams and formation recognition is not logged as damage");
        var ambiguous=new LaneDrill(hpProfile:new(550,550));
        for(int i=0;i<100;i++)
            ambiguous.ReadLaneVision([B(350,400,false) with {Fill=i<5?70:i<46?58:46}],i*.02,values);
        check(values is [{Type:LaneCreepType.Unknown,Next:null}],"a rhythm that fits both types cannot produce a single HP forecast");
    }
}
