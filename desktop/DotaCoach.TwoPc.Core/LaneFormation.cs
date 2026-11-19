namespace DotaCoach.TwoPc;

public enum LaneCreepType { Unknown, Melee, Ranged }

// Screen-space evidence for an ordinary opening wave. This is not a model/sprite detector.
// Only label a complete side. A smaller opposing side can anchor the axis, not supply labels.
internal static class LaneFormation
{
    internal readonly record struct Sample(CreepBar Bar, bool RecentDamage);

    internal static bool TryRoles(ReadOnlySpan<Sample> units, Span<LaneCreepType> roles)
    {
        if (units.Length is <6 or >8 || roles.Length != units.Length) return false;
        int allies = 0, rearA = -1, rearB = -1;
        bool hurtA = false, hurtB = false;
        static double Dist(CreepBar a, CreepBar b) {
            double dx = (a.X + .5*a.Width) - (b.X + .5*b.Width), dy = (a.Y - b.Y)*1.3;
            return dx*dx + dy*dy;
        }
        for (int i=0;i<units.Length;i++) {
            var a=units[i].Bar;
            if (a.Ally) allies++;
            double nearest=double.PositiveInfinity;
            for (int j=0;j<units.Length;j++)
                if (a.Ally!=units[j].Bar.Ally) nearest=Math.Min(nearest,Dist(a,units[j].Bar));
            double w2=(double)a.Width*a.Width;
            if (nearest<=1.55*1.55*w2) {
                roles[i]=LaneCreepType.Melee;
                if (units[i].RecentDamage) { if (a.Ally) hurtA=true; else hurtB=true; }
            } else if (nearest>=2.0*2.0*w2 && nearest<=5.4*5.4*w2) {
                ref int rear=ref (a.Ally ? ref rearA : ref rearB);
                if (rear>=0) return false;
                rear=i; roles[i]=LaneCreepType.Ranged;
            } else return false;
        }
        int enemies=units.Length-allies;
        if ((allies!=4 && enemies!=4) || allies is <2 or >4 || enemies is <2 or >4 ||
            rearA<0 || rearB<0 || !hurtA || !hurtB) return false;
        var left=units[rearA].Bar; var right=units[rearB].Bar;
        double x0=left.X+.5*left.Width, dx=right.X+.5*right.Width-x0, dy=(right.Y-left.Y)*1.3;
        double len=Math.Sqrt(dx*dx+dy*dy);
        if (len<3*left.Width) return false;
        for (int i=0;i<units.Length;i++) {
            if (roles[i]!=LaneCreepType.Melee) continue;
            var b=units[i].Bar;
            double x=b.X+.5*b.Width-x0, y=(b.Y-left.Y)*1.3;
            double along=(x*dx+y*dy)/len, across=Math.Abs(x*dy-y*dx)/len;
            if (along<.9*b.Width || len-along<.9*b.Width || across>1.45*b.Width) return false;
        }
        for (int i=0;i<units.Length;i++)
            if ((units[i].Bar.Ally ? allies : enemies)!=4) roles[i]=LaneCreepType.Unknown;
        return true;
    }
}
