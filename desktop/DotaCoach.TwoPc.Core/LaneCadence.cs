namespace DotaCoach.TwoPc;

// A conditional projection of one more similar impact. No claim about who fired:
// only the observed health-bar steps are available to the player-side camera.
public readonly record struct LaneForecast(int LowHp, int HighHp, int EtaMs);

internal sealed class LaneCadence
{
    private double lastHit = double.NegativeInfinity, step, previousStep, nextStep, period, spread;
    internal bool Armed { get; private set; }
    internal bool Recent(double now) => now-lastHit <= .95;
    internal void Reset() { lastHit=double.NegativeInfinity; Armed=false; }
    internal void Observe(int drop, int width, double now)
    {
        if (drop==0) return;
        // A one-pixel increase can be regeneration/quantization. Actual losses, even small
        // chip damage, must never be subtracted as if they were part of the predicted blow.
        if (drop==-1) return;
        double d=(double)drop/width;
        if (d<.10 || d>.37) { Reset(); return; }
        double gap=now-lastHit, diff=Math.Abs(d-step);
        Armed=gap is >=.71 and <=.93 && diff<=Math.Max(2d/width,.12*d);
        if (Armed) { previousStep=step; period=gap; nextStep=(step+d)/2; spread=diff/2+2d/width; }
        step=d; lastHit=now;
    }
    internal bool FitsBoth(int maximum,HitRange expected,int width)
    {
        double error=1.6/width;
        bool Fits(double d)=>d>=expected.Min/maximum-error && d<=expected.Max/maximum+error;
        return Armed && Fits(previousStep) && Fits(step);
    }
    internal LaneForecast? Project(CreepBar b, double now, int maximum)
    {
        if (!Armed || b.Ally) return null;
        double eta=lastHit+period-now;
        if (eta<=0) { Armed=false; return null; } // No projection past the deadline; retain the last real sample for a slightly late hit.
        double after=(double)b.Fill/(b.Width-2)-nextStep;
        int lo=Math.Clamp((int)Math.Floor(maximum*(after-spread)),0,maximum);
        int hi=Math.Clamp((int)Math.Ceiling(maximum*(after+spread)),0,maximum);
        return new(lo,hi,50*Math.Max(1,(int)Math.Round(eta*20,MidpointRounding.AwayFromZero)));
    }
}
