using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>An enemy's wards in one item read by hero_scan.py (game clock of the read). Unknown = grey icons whose kind the colour cannot tell.</summary>
public sealed record WardReading(double Clock, int Observer, int Sentry, int Unknown)
{
    public int Total => Observer + Sentry + Unknown;
}

/// <summary>A common ward spot of one team (data/dota2-ward-spots-742.json): placements per match and by game phase.</summary>
public sealed record WardSpot(double X, double Y, int Count, int[] Phases);

/// <summary>Where each team usually wards in 7.42, from public OpenDota parses (scripts/build-ward-spots.ts). A prior, not an observation.</summary>
public sealed class WardSpots
{
    private readonly Dictionary<string, WardSpot[]> spots = new();
    public int Matches { get; }
    public WardSpots(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = doc.RootElement;
        if (root.GetProperty("patch").GetString() != "7.42") throw new ArgumentException("Unreviewed ward spots");
        Matches = root.GetProperty("source").GetProperty("matches").GetInt32();
        foreach (var group in root.GetProperty("spots").EnumerateObject())
            spots[group.Name] = group.Value.EnumerateArray().Select(s => new WardSpot(s.GetProperty("x").GetDouble(), s.GetProperty("y").GetDouble(),
                s.GetProperty("count").GetInt32(), s.GetProperty("phases").EnumerateArray().Select(p => p.GetInt32()).ToArray())).ToArray();
    }
    public static WardSpots? Bundled()
    {
        try { return new WardSpots(Path.Combine(AppContext.BaseDirectory, "data", "dota2-ward-spots-742.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
                                   KeyNotFoundException or InvalidOperationException) { return null; }
    }
    /// <summary>Spots of <paramref name="team"/> ("radiant"/"dire") for a ward kind; "ward" (unknown kind) takes both.</summary>
    public WardSpot[] For(string team, string kind) => kind switch {
        "observer" => spots.GetValueOrDefault(team + "Observer") ?? [],
        "sentry" => spots.GetValueOrDefault(team + "Sentry") ?? [],
        _ => [.. spots.GetValueOrDefault(team + "Observer") ?? [], .. spots.GetValueOrDefault(team + "Sentry") ?? []],
    };
}

/// <summary>One place a guessed ward may be: its share of the guess and its name. Rough = no common spot fitted, only the hero's track.</summary>
public sealed record WardCandidate(double X, double Y, double Share, string Place, bool Rough = false);
/// <summary>
/// Wards an enemy used between two item reads (<see cref="After"/>, <see cref="Before"/>: game clock), and where they
/// may be. <see cref="Kind"/>: observer, sentry, or ward when the kind is unknown. Lasts until the ward would expire.
/// <see cref="Unseen"/>: seconds the hero was out of sight in between; the longer, the less the marks can be trusted.
/// </summary>
public sealed record WardGuess(string Id, string Hero, string Kind, int Count, double After, double Before, double Expires,
    WardCandidate[] Candidates, double Unseen = 0);

/// <summary>
/// Guesses where enemy wards went, from Plus team view only. hero_scan.py reads each visible enemy's ward counts with
/// its items; when a later read has fewer wards than the one before, the wards were used in between. The enemy's
/// track on this coach's own Plus GSI minimap bounds where: within cast range of where it was seen, or of any point it
/// could have walked to while out of sight. Common 7.42 spots of that team inside these bounds are the candidates,
/// weighted by how often they are warded at this point of the game; with none, the place it vanished is marked roughly.
/// A count can also drop when a ward is handed to an ally, dropped or sold; these look the same.
/// </summary>
public sealed class WardTracker
{
    public const double CastRange = 500, Speed = 420, PathSlack = 200;
    public const int ObserverLifetime = 360, SentryLifetime = 420, MaxCandidates = 3, MaxGuesses = 12;
    // Chosen on three 7.42 pro games with spots built without them (WardEval): the likeliest mark was within 600 of the
    // real ward for 42 % of the guesses (67 % when the hero stayed in sight between the reads), one of three marks for 60 %.
    private const double KeepSeconds = 600, MinSpacing = 700, DetourScale = 800, NearSigma = 250, PriorAll = 0.25;
    private readonly CoachMap map;
    private readonly WardSpots? spots;
    private readonly object gate = new();
    private sealed class Track { public readonly List<(double Clock, double X, double Y)> Points = new(); }
    private readonly Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WardReading> last = new(StringComparer.Ordinal);
    private readonly List<WardGuess> guesses = new();
    private string? match;
    private int ownTeam;
    private double clock = double.NaN, clockBase;
    private long clockTick;
    private int sequence;

    public WardTracker(CoachMap map, WardSpots? spots) { this.map = map; this.spots = spots; }

    /// <summary>Spectator threads: every Plus packet adds where visible enemies are (sub-second clock from arrival time).</summary>
    public void Sight(SpectatorSnapshot s)
    {
        if (s.Mode != "team-view" || s.OwnTeam is not (2 or 3) || !s.Running || s.Clock is not double c) return;
        lock (gate) {
            Reset(s.MatchId, s.OwnTeam.Value);
            // GSI clocks are whole seconds: the arrival time places a packet within its second.
            if (c != clockBase || double.IsNaN(clock)) { clockBase = c; clockTick = s.ReceivedAt; }
            if (s.Paused) clockTick = s.ReceivedAt;
            clock = clockBase + Math.Clamp((s.ReceivedAt - clockTick) / 1000.0, 0, 0.99);
            foreach (var unit in s.Heroes.Where(u => u.Team is 2 or 3 && u.Team != ownTeam && u.Vision > 0)) {
                if (!tracks.TryGetValue(unit.Name, out var track)) tracks[unit.Name] = track = new();
                var p = track.Points;
                if (p.Count == 0 || clock - p[^1].Clock >= 0.25 || double.Hypot(unit.X - p[^1].X, unit.Y - p[^1].Y) >= 150)
                    p.Add((clock, unit.X, unit.Y));
                while (p.Count > 0 && clock - p[0].Clock > KeepSeconds) p.RemoveAt(0);
            }
            // An enemy ward the friend team sees is on the player's own minimap already: the guess it answers is done.
            foreach (var ward in s.Minimap.Where(u => u.Team is 2 or 3 && u.Team != ownTeam &&
                                                      u.Name is "npc_dota_observer_wards" or "npc_dota_sentry_wards")) {
                string kind = ward.Name == "npc_dota_observer_wards" ? "observer" : "sentry";
                guesses.RemoveAll(g => (g.Kind == kind || g.Kind == "ward") &&
                    g.Candidates.Any(k => double.Hypot(k.X - ward.X, k.Y - ward.Y) <= 900));
            }
        }
    }
    private void Reset(string? matchId, int team)
    {
        if (matchId == match && team == ownTeam) return;
        match = matchId; ownTeam = team; tracks.Clear(); last.Clear(); guesses.Clear(); clock = double.NaN;
    }
    /// <summary>The newest game clock seen, with its fraction; NaN before the first packet.</summary>
    public double Clock { get { lock (gate) return clock; } }

    /// <summary>The scanner's latest ward reads of each enemy (oldest first); returns the guesses they add.</summary>
    public List<WardGuess> Readings(string? matchId, int ownTeamNumber, IReadOnlyDictionary<string, WardReading[]> enemies)
    {
        var found = new List<WardGuess>();
        lock (gate) {
            Reset(matchId, ownTeamNumber);
            foreach (var (hero, reads) in enemies)
                foreach (var r in reads.OrderBy(r => r.Clock)) {
                    if (last.TryGetValue(hero, out var before) && r.Clock <= before.Clock) continue;
                    last[hero] = r;
                    if (before == null || r.Total >= before.Total) continue;
                    int drop = before.Total - r.Total;
                    if (drop > 4) continue; // more than a hero places between two reads: a misread, not wards
                    int observers = Math.Min(drop, Math.Max(0, before.Observer - r.Observer));
                    int sentries = Math.Min(drop - observers, Math.Max(0, before.Sentry - r.Sentry));
                    foreach (var (kind, count) in new[] { ("observer", observers), ("sentry", sentries), ("ward", drop - observers - sentries) })
                        if (count > 0) found.Add(Guess(hero, kind, count, before.Clock, r.Clock));
                }
            guesses.AddRange(found);
            if (guesses.Count > MaxGuesses) guesses.RemoveRange(0, guesses.Count - MaxGuesses);
        }
        return found;
    }
    /// <summary>Guesses whose ward may still stand at game clock <paramref name="now"/>.</summary>
    public WardGuess[] Active(double now)
    {
        lock (gate) { guesses.RemoveAll(g => g.Expires <= now); return guesses.ToArray(); }
    }
    /// <summary>The active guesses as minimap marks for the game machine (positions 0..1 across the minimap).</summary>
    public WardMark[] Marks(double now) => Active(now).SelectMany(g => g.Candidates.Select((k, rank) => {
        var (x, y) = map.Minimap(k.X, k.Y);
        return new WardMark(g.Hero, g.Kind, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), rank, Math.Round(k.Share, 2),
            Math.Max(0, Math.Round(now - g.Before, 1)), Math.Max(0, Math.Round(g.Expires - now, 1)), k.Rough);
    })).ToArray();

    private WardGuess Guess(string hero, string kind, int count, double after, double before)
    {
        var points = tracks.TryGetValue(hero, out var t)
            ? t.Points.Where(p => p.Clock >= after - 1.5 && p.Clock <= before + 1.5).ToList() : [];
        // The window splits into stretches in sight (samples under 1.5 s apart) and gaps out of sight between them. The
        // ward went down in one of them, more likely in a longer one; within each, a spot's chance is its prior weight
        // times how well it fits (cast range of where the hero was seen, or a detour it could walk while unseen).
        var stretches = new List<(List<(double X, double Y)> Seen, double Seconds)>();
        var gaps = new List<((double X, double Y) A, (double X, double Y) B, double Seconds)>();
        for (int i = 0; i < points.Count; i++) {
            if (i == 0 || points[i].Clock - points[i - 1].Clock > 1.5) {
                if (i > 0) gaps.Add(((points[i - 1].X, points[i - 1].Y), (points[i].X, points[i].Y), points[i].Clock - points[i - 1].Clock));
                stretches.Add(([], 0.5));
            } else stretches[^1] = (stretches[^1].Seen, stretches[^1].Seconds + points[i].Clock - points[i - 1].Clock);
            stretches[^1].Seen.Add((points[i].X, points[i].Y));
        }
        string enemy = ownTeam == 2 ? "dire" : "radiant";
        int phase = before < 600 ? 0 : before < 1500 ? 1 : 2;
        var pool = (spots?.For(enemy, kind) ?? []).Where(s => !map.NoWard(s.X, s.Y))
            .Select(s => (Spot: s, Prior: s.Phases.ElementAtOrDefault(phase) + PriorAll * s.Count)).ToArray();
        var parts = stretches.Select(st => (st.Seconds, Fit: (Func<WardSpot, double>)(s => {
                double near = st.Seen.Min(p => double.Hypot(p.X - s.X, p.Y - s.Y)) - CastRange;
                return near <= 0 ? 1 : Math.Exp(-near * near / (2 * NearSigma * NearSigma));
            })))
            .Concat(gaps.Select(g => (g.Seconds, Fit: (Func<WardSpot, double>)(s => {
                double via = double.Hypot(g.A.X - s.X, g.A.Y - s.Y) + double.Hypot(s.X - g.B.X, s.Y - g.B.Y);
                if (via > Speed * g.Seconds + 2 * CastRange) return 0;
                return Math.Exp(-Math.Max(0, via - double.Hypot(g.A.X - g.B.X, g.A.Y - g.B.Y) - 2 * CastRange) / DetourScale);
            })))).ToArray();
        var scores = new double[pool.Length];
        double seconds = parts.Sum(p => p.Seconds);
        foreach (var (length, fit) in parts) {
            var likely = pool.Select(p => p.Prior * fit(p.Spot)).ToArray();
            double sum = likely.Sum();
            if (sum > 0) for (int i = 0; i < pool.Length; i++) scores[i] += length / seconds * likely[i] / sum;
        }
        var scored = pool.Select((p, i) => (p.Spot, Score: scores[i])).Where(s => s.Score > 0).ToList();
        double total = scored.Sum(s => s.Score);
        var candidates = new List<WardCandidate>();
        foreach (var (spot, score) in scored.OrderByDescending(s => s.Score)) {
            if (candidates.Count == MaxCandidates) break;
            if (candidates.Any(k => double.Hypot(k.X - spot.X, k.Y - spot.Y) < MinSpacing)) continue;
            candidates.Add(new(spot.X, spot.Y, total > 0 ? score / total : 0, map.Describe(spot.X, spot.Y, ownTeam)));
        }
        if (candidates.Count == 0 && points.Count > 0) {
            // No common spot fits: where it went out of sight for longest, else where it was halfway through.
            var middle = points.MinBy(p => Math.Abs(p.Clock - (after + before) / 2));
            var at = gaps.Count > 0 ? gaps.MaxBy(g => g.Seconds).A : (middle.X, middle.Y);
            candidates.Add(new(at.X, at.Y, 1, map.Describe(at.X, at.Y, ownTeam), Rough: true));
        }
        int lifetime = kind == "observer" ? ObserverLifetime : SentryLifetime;
        return new($"{hero}:{before}:{kind}:{++sequence}", hero, kind, count, after, before, before + lifetime, candidates.ToArray(),
            Math.Round(gaps.Sum(g => g.Seconds), 1));
    }
}
