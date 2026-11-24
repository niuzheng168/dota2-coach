namespace DotaCoach.TwoPc;

/// <summary>
/// An enemy hero out of the friend team's sight: where it was last seen (world XY), which way it faced then (whole world
/// degrees, 0 = +x, 90 = +y) and how many game seconds ago. An estimate, never a sighting; the circle is <see cref="FogTracker.Circle"/>.
/// </summary>
public sealed record FogHero(string Hero, double X, double Y, int? Yaw, double Seconds);

/// <summary>
/// Enemy heroes that left the friend team's vision in Plus team view, for the game machine's minimap. Each keeps its last
/// position and facing; for the first <see cref="CircleSeconds"/> a circle shows where it probably is now, then only the
/// last-seen mark, until <see cref="MaxSeconds"/>. A hero seen again, or one that vanished as the friend team's kill count
/// rose (died rather than walked off), is dropped. A gap in the Plus feed or another match forgets everything.
/// </summary>
public sealed class FogTracker
{
    /// <summary>Gone this briefly is a flicker at the edge of vision, not an absence.</summary>
    public const double FlickerSeconds = 1, CircleSeconds = 10, MaxSeconds = 20;
    /// <summary>A vanish this close (seconds) to a rise of the friend team's kill count is taken as that kill.</summary>
    public const double KillWindow = 1.5;
    private sealed class Seen { public double Clock, X, Y; public int? Yaw; public bool Visible; }
    private readonly Dictionary<string, Seen> heroes = new(StringComparer.Ordinal);
    private readonly List<double> kills = []; // clocks of unclaimed kill-count rises
    private string? match;
    private int ownTeam, lastKills = -1;
    private long? lastReceipt;
    private double clock = double.NaN, clockBase = double.NegativeInfinity;
    private long clockTick;

    public void Clear()
    {
        heroes.Clear(); kills.Clear(); match = null; ownTeam = 0; lastKills = -1; lastReceipt = null;
        clock = double.NaN; clockBase = double.NegativeInfinity;
    }

    public void Sight(SpectatorSnapshot s)
    {
        if (lastReceipt is long previous && s.ReceivedAt < previous) return;
        if (!TeamViewEvidence.IsTeamView(s)) { Clear(); return; }
        if (s.MatchId != match || s.OwnTeam != ownTeam || s.ReceivedAt - lastReceipt >= TeamViewEvidence.FreshMs ||
            s.Clock < clockBase) Clear();
        match = s.MatchId; ownTeam = s.OwnTeam!.Value; lastReceipt = s.ReceivedAt;
        // GSI clocks are whole seconds: the arrival time places a packet within its second (as WardTracker does).
        double c = s.Clock!.Value;
        if (c != clockBase || double.IsNaN(clock)) { clockBase = c; clockTick = s.ReceivedAt; }
        clock = clockBase + Math.Clamp((s.ReceivedAt - clockTick) / 1000.0, 0, 0.99);

        if (s.Kills is var (radiant, dire)) {
            int own = ownTeam == 2 ? radiant : dire;
            if (lastKills >= 0) for (int k = lastKills; k < own && kills.Count < 10; k++) kills.Add(clock);
            lastKills = own;
        }
        var visible = TeamViewEvidence.VisibleHeroes(s).Where(u => u.Team != ownTeam).ToDictionary(u => u.Name, StringComparer.Ordinal);
        foreach (var (name, unit) in visible) {
            if (!heroes.TryGetValue(name, out var h)) {
                if (heroes.Count >= 10) continue;
                heroes[name] = h = new();
            }
            h.Clock = clock; h.X = unit.X; h.Y = unit.Y; h.Yaw = TeamViewEvidence.Facing(unit.Yaw); h.Visible = true;
        }
        foreach (var h in heroes.Values.Where(h => h.Visible && h.Clock < clock)) h.Visible = false;
        // Each rise of the kill count claims the hero that vanished nearest to it, before or after.
        kills.RemoveAll(k => clock - k > KillWindow);
        while (kills.Count > 0 && heroes.Where(p => !p.Value.Visible && Math.Abs(p.Value.Clock - kills[0]) <= KillWindow)
                   .OrderBy(p => Math.Abs(p.Value.Clock - kills[0])).Select(p => p.Key).FirstOrDefault() is { } dead) {
            heroes.Remove(dead); kills.RemoveAt(0);
        }
        foreach (var gone in heroes.Where(p => clock - p.Value.Clock > MaxSeconds).Select(p => p.Key).ToArray()) heroes.Remove(gone);
    }

    /// <summary>Enemies out of sight for longer than a flicker, as of the latest packet.</summary>
    public FogHero[] Heroes() => double.IsNaN(clock) ? [] : heroes
        .Where(p => !p.Value.Visible && clock - p.Value.Clock >= FlickerSeconds && clock - p.Value.Clock <= MaxSeconds)
        .OrderBy(p => p.Key, StringComparer.Ordinal)
        .Select(p => new FogHero(p.Key, p.Value.X, p.Value.Y, p.Value.Yaw, Math.Round(clock - p.Value.Clock, 1))).ToArray();

    // Fitted on 47 replays (13,900 enemies leaving a team's vision for 5 s or more; vision as WardEval models it): per
    // second gone, how far along the last facing to put the centre and the radius holding the hero 70 % of the time
    // (68 % / 72 % on the two halves of the corpus). Without a facing: centred on the last sighting, a larger radius.
    // The facing beat the last second's walk: within 45° of where the hero went at 5 s for 55 % of them vs 46 %.
    private static readonly double[] Ahead = [0, 250, 400, 500, 600, 700, 800, 900, 1000, 1050, 1150];
    private static readonly double[] Radius = [100, 270, 570, 850, 1050, 1310, 1560, 1800, 1980, 2180, 2370];
    private static readonly double[] Undirected = [100, 380, 730, 1060, 1280, 1560, 1810, 2050, 2210, 2400, 2570];

    /// <summary>Where the hero probably is (world XY and radius) after <see cref="FogHero.Seconds"/>, or null after
    /// <see cref="CircleSeconds"/>, when the circle would cover too much of the map to help.</summary>
    public static (double X, double Y, double Radius)? Circle(FogHero h)
    {
        if (!Wire.Finite(h.Seconds, 0, CircleSeconds)) return null;
        int i = Math.Min((int)h.Seconds, Ahead.Length - 2);
        double f = h.Seconds - i;
        static double At(double[] table, int i, double f) => table[i] + (table[i + 1] - table[i]) * f;
        if (h.Yaw is not int yaw) return (h.X, h.Y, At(Undirected, i, f));
        double a = yaw * Math.PI / 180, ahead = At(Ahead, i, f);
        return (h.X + ahead * Math.Cos(a), h.Y + ahead * Math.Sin(a), At(Radius, i, f));
    }
}
