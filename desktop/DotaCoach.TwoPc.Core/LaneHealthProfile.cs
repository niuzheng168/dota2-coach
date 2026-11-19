namespace DotaCoach.TwoPc;

/// <summary>Ordinary wave HP, not hero or tower damage. Defaults from build 6980,
/// scripts/npc/npc_units.txt (both teams). A different observed wave can be calibrated.</summary>
public sealed class LaneHealthProfile
{
    public const int BaseMelee=550, BaseRanged=300;
    public static readonly LaneHealthProfile Standard=new(BaseMelee,BaseRanged);
    public int Melee { get; }
    public int Ranged { get; }
    public LaneHealthProfile(int melee,int ranged)
    {
        if(melee is <1 or >9999) throw new ArgumentOutOfRangeException(nameof(melee));
        if(ranged is <1 or >9999) throw new ArgumentOutOfRangeException(nameof(ranged));
        Melee=melee; Ranged=ranged;
    }

    public static int Estimate(CreepBar b,int maximum)
    {
        int inner=b.Width-2;
        if(inner<=0) throw new ArgumentOutOfRangeException(nameof(b));
        return (Math.Clamp(b.Fill,0,inner)*maximum+inner/2)/inner;
    }
}
