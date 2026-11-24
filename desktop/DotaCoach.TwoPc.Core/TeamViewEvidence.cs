namespace DotaCoach.TwoPc;

/// <summary>Admission for live Plus evidence, not for the delay meter or offline replay evaluations.</summary>
public static class TeamViewEvidence
{
    public const int FreshMs = 3_000, MaxLagSeconds = 10, MaxLeadSeconds = 2;
    public static bool RealMatch(string? id) => MatchWitness.MatchIdOk(id);
    public static string? Side(int? n) => n switch { 2 => "radiant", 3 => "dire", _ => null };
    public static bool IsTeamView(SpectatorSnapshot? s) =>
        s is { Mode: "team-view", GroupedPlayers: 0, OwnTeam: 2 or 3, Running: true, Paused: false, Clock: double c } &&
        RealMatch(s.MatchId) && Wire.Finite(c, -3600, 43_200);
    public static MatchWitness? Participant(FrameView? frame, long now)
    {
        if (frame is not { Meta.Source: "dota2" or "dota2-gsi" } || now - frame.ReceivedAt is < 0 or >= FreshMs ||
            frame.CapturedNoEarlierThan > frame.ReceivedAt) return null;
        var m = frame.Meta;
        if (m.MatchPhase is { } w) return BoardDelivery.Live(w) && BoardDelivery.Fresh(w, frame.CapturedNoEarlierThan, now) &&
            (m.Player == null || BoardDelivery.SamePlayer(w, m.Player)) ? w : null;
        // Same narrowly proven old-format participant as LiveMatchState; never promote a bare player or match ID.
        return MinuteFarmContext.Fresh(frame, now) && m.Player is { } p
            ? new(p.MatchId, p.Team, p.Hero, p.Clock, "playing", false) { AgeMs = m.GsiAgeMs!.Value } : null;
    }
    public static bool Matches(MatchWitness? participant, SpectatorSnapshot? s, long now) =>
        participant != null && IsTeamView(s) && now - s!.ReceivedAt is >= 0 and < FreshMs &&
        participant.MatchId == s.MatchId && participant.Team == Side(s.OwnTeam) && Recent(s.Clock!.Value, participant.Clock);
    // The player's GSI reaches the coach in whole seconds a few times a second over the LAN, so this machine's own
    // Plus feed routinely shows the next second first. Treating that as another timeline reset the whole team view
    // (ward tracker, leave-vision tracking) once per game second in v0.6.1; only a larger lead is suspicious.
    internal static bool Recent(double observed, double current) =>
        Wire.Finite(current, -3600, 43_200) && Wire.Finite(observed, current - MaxLagSeconds, current + MaxLeadSeconds);
    private static bool Located(MinimapUnit u) => u.Team is 2 or 3 && (u.X != 0 || u.Y != 0) &&
        Wire.Finite(u.X, -12_000, 12_000) && Wire.Finite(u.Y, -12_000, 12_000);
    /// <summary>The minimap's yaw as whole world degrees 0–359 (0 = +x, 90 = +y; Dota reports anything from -180 to 360).</summary>
    public static int? Facing(double? yaw) => yaw is double y && double.IsFinite(y) ? ((int)Math.Round(y) % 360 + 360) % 360 : null;
    public static IEnumerable<MinimapUnit> VisibleHeroes(SpectatorSnapshot s) => !IsTeamView(s) ? [] : s.Heroes
        .Where(u => Located(u) && u.Vision > 0 && Wire.ValidIconKey(u.Name))
        .DistinctBy(u => (u.Team, u.Name)).Take(10);
    // Ward entities commonly report visionrange 0. The observation is the recognized entity on
    // this team's minimap, not its radius, its lifetime, or an inventory-based estimate.
    public static IEnumerable<MinimapUnit> VisibleWards(SpectatorSnapshot s) => !IsTeamView(s) ? [] : s.Minimap
        .Where(u => Located(u) && u.Name is "npc_dota_observer_wards" or "npc_dota_sentry_wards")
        .DistinctBy(u => (u.Name, u.Team, u.X, u.Y)).Take(Wire.MaxSeenWards);
}

/// <summary>One live file on the coach machine. A UI tick, toggling delivery, a scanner restart or
/// changing views never makes an already consumed write a new observation.</summary>
public sealed class TeamScanCursor
{
    private ScanStamp? last;
    private readonly Dictionary<string, double> chargesThrough = new(StringComparer.Ordinal);
    public static bool Fits(ScanStamp? sample, MatchWitness? own, SpectatorSnapshot? plus, long tick, long unixMs, bool allowNextSecond = false) =>
        sample != null && TeamViewEvidence.Matches(own, plus, tick) &&
        sample.MatchId == plus!.MatchId && sample.OwnTeam == plus.OwnTeam &&
        sample.FreshAt(unixMs) &&
        Wire.Finite(sample.Clock, own!.Clock - TeamViewEvidence.MaxLagSeconds, own.Clock + (allowNextSecond ? 1 : 0)) &&
        Wire.Finite(sample.WardClock, sample.Clock, own.Clock + (allowNextSecond ? 1 : 0)) &&
        Math.Abs(sample.Clock - plus.Clock!.Value) <= TeamViewEvidence.FreshMs / 1000.0;
    public bool Take(ScanStamp sample)
    {
        if (last != null && (sample.WrittenMs <= last.WrittenMs ||
                sample.MatchId == last.MatchId && sample.OwnTeam == last.OwnTeam && sample.Clock < last.Clock))
            return false;
        if (last == null || sample.MatchId != last.MatchId || sample.OwnTeam != last.OwnTeam) chargesThrough.Clear();
        last = sample;
        return true;
    }
    /// <summary>A fresh file repeats up to four past changes. After the tracker was fenced by a
    /// view change, keep at most the last already-known baseline, never replay earlier declines.
    /// This is a per-match high water, not equipment carried across an observation gap.</summary>
    public Dictionary<string, WardReading[]> UnprocessedChanges(IReadOnlyDictionary<string, WardReading[]> reads)
    {
        var result = new Dictionary<string, WardReading[]>(StringComparer.Ordinal);
        foreach (var (hero, history) in reads) {
            if (history.Length == 0 || !chargesThrough.ContainsKey(hero) && chargesThrough.Count >= 12) continue;
            double through = chargesThrough.GetValueOrDefault(hero, double.NegativeInfinity);
            var newReads = history.Where(r => r.Clock >= through).ToArray();
            if (newReads.Length > 0) { result[hero] = newReads; chargesThrough[hero] = newReads[^1].Clock; }
        }
        return result;
    }
}
