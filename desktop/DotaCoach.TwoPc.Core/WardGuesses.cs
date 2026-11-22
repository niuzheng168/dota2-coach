using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>
/// An enemy's wards as hero_scan.py read them. Unknown = grey icons whose kind the colour cannot tell. Scanners since
/// 2026-11-22 send one entry per change of the counts: <see cref="First"/> the game time of the first read with these
/// counts and <see cref="Clock"/> of the latest, both to a fraction of a second, so a ward went down between one entry's
/// Clock and the next one's First. Older scanners send the GSI's whole-second clock of single reads and no First.
/// </summary>
public sealed record WardReading(double Clock, int Observer, int Sentry, int Unknown, double? First = null)
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

/// <summary>
/// One place a guessed ward may be: its share of the guess and its name. Rough = no common spot fitted, only the hero's
/// track. Not a common spot either when the hero was in sight as the count dropped: then it is a cast range ahead of the hero.
/// </summary>
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
/// could have walked to while out of sight. In sight as the count dropped, the mark goes a cast range ahead of where the
/// hero was then. Otherwise common 7.42 spots of that team are scored on the track (<see cref="WardModel"/>); with none
/// fitting, the place it vanished is marked roughly.
/// A count can also drop when a ward is handed to an ally, dropped or sold; these look the same.
/// </summary>
public sealed class WardTracker
{
    public const double CastRange = 500, Speed = 420;
    public const int ObserverLifetime = 360, SentryLifetime = 420, MaxCandidates = 3, MaxGuesses = 12;
    /// <summary>Out of sight this long between the reads, the marks were mostly wrong: such a guess gets a subtitle, no marks.</summary>
    public const double UnsureSeconds = 15;
    /// <summary>
    /// The friend team's eyes count with this share of the vision radius its minimap reports: trees and cliffs block the
    /// rest. Offline evaluations, which know unit kinds rather than radii, pass their radius divided by it.
    /// </summary>
    public const double VisionShare = 0.75;
    private const double KeepSeconds = 600, MinSpacing = 700, PriorAll = 0.25;
    // Reads with the whole-second clock: the ward went down in [after, before + 1); sightings arrive a little late.
    private const double EarlySlack = 0.5, LateSlack = 0.3;
    // Reads timed to a fraction of a second, by the scanner's and this coach's own GSI arrival times.
    private const double ExactSlack = 0.3;
    // In sight between reads at most this far apart, the mark goes where the hero stood halfway (track points within
    // AnchorNear s of then), moved AnchorAhead along the way it walked in the second before. In 47 replays with reads
    // as the scanner takes them since 2026-11-22, that was within 600 of the real ward for 95 % of such guesses (79 %
    // for the best-fitting common spot) and within 300 for about 60 %; the ward lies within 600 of the hero placing it.
    private const double AnchorWindow = 4, AnchorNear = 0.75, AnchorAhead = 350, AnchorShare = 0.9;
    private readonly CoachMap map;
    private readonly WardSpots? spots;
    private readonly object gate = new();
    private sealed class Track { public readonly List<(double Clock, double X, double Y)> Points = new(); }
    private readonly Dictionary<string, Track> tracks = new(StringComparer.Ordinal);
    private readonly Dictionary<string, WardReading> last = new(StringComparer.Ordinal);
    private readonly List<WardGuess> guesses = new();
    // Enemy wards the friend team has seen, with the clock of the first sight. Found wards are mostly dewarded within ~10 s
    // (2026-11-17 Plus game), often before the scanner reads the enemy's lower count: the guess it then makes is answered too.
    private readonly List<(string Kind, double X, double Y, double First)> sighted = new();
    // The friend team's eyes about once a second (units with a vision radius, the radius cut to VisionShare), and its
    // own observer wards in the latest packet: an enemy out of sight was not standing in them.
    private readonly List<(double Clock, (double X, double Y, double R)[] Units)> eyes = new();
    private (double X, double Y)[] ownObservers = [];
    private string? match;
    private int ownTeam;
    private double clock = double.NaN, clockBase;
    private long clockTick;
    private int sequence;

    public WardTracker(CoachMap map, WardSpots? spots) { this.map = map; this.spots = spots; }
    /// <summary>Offline fitting only (WardEval --features): each new guess with the spots it weighed and their features.</summary>
    public Action<WardGuess, WardSpot[], WardModel.Part[]>? Explain { get; set; }

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
            if (eyes.Count == 0 || clock - eyes[^1].Clock >= 1 || clock < eyes[^1].Clock)
                eyes.Add((clock, s.Minimap.Where(u => u.Team == ownTeam && u.Vision > 0).Select(u => (u.X, u.Y, u.Vision!.Value * VisionShare)).ToArray()));
            while (eyes.Count > 0 && clock - eyes[0].Clock > KeepSeconds) eyes.RemoveAt(0);
            ownObservers = s.Minimap.Where(u => u.Team == ownTeam && u.Name == "npc_dota_observer_wards").Select(u => (u.X, u.Y)).ToArray();
            // An enemy ward the friend team sees is on the player's own minimap already, and usually dewarded soon after:
            // the guess it answers is done.
            foreach (var ward in s.Minimap.Where(u => u.Team is 2 or 3 && u.Team != ownTeam &&
                                                      u.Name is "npc_dota_observer_wards" or "npc_dota_sentry_wards")) {
                string kind = ward.Name == "npc_dota_observer_wards" ? "observer" : "sentry";
                guesses.RemoveAll(g => Answers(g, kind, ward.X, ward.Y));
                if (!sighted.Any(w => w.Kind == kind && double.Hypot(w.X - ward.X, w.Y - ward.Y) < 50))
                    sighted.Add((kind, ward.X, ward.Y, clock));
            }
            sighted.RemoveAll(w => clock - w.First > KeepSeconds);
        }
    }
    /// <summary>A seen enemy ward of the guess's kind (any kind for an unknown one) near one of its marks.</summary>
    private static bool Answers(WardGuess g, string kind, double x, double y) =>
        (g.Kind == kind || g.Kind == "ward") && g.Candidates.Any(k => double.Hypot(k.X - x, k.Y - y) <= 900);
    private void Reset(string? matchId, int team)
    {
        if (matchId == match && team == ownTeam) return;
        match = matchId; ownTeam = team; tracks.Clear(); last.Clear(); guesses.Clear(); sighted.Clear(); eyes.Clear();
        ownObservers = []; clock = double.NaN;
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
                    // The same counts read again since (the same entry, now with a later Clock): nothing went down.
                    if (before == null || r.Total >= before.Total || r.First != null && r.First == before.First) continue;
                    int drop = before.Total - r.Total;
                    if (drop > 4) continue; // more than a hero places between two reads: a misread, not wards
                    int observers = Math.Min(drop, Math.Max(0, before.Observer - r.Observer));
                    int sentries = Math.Min(drop - observers, Math.Max(0, before.Sentry - r.Sentry));
                    foreach (var (kind, count) in new[] { ("observer", observers), ("sentry", sentries), ("ward", drop - observers - sentries) }) {
                        if (count == 0) continue;
                        var guess = Guess(hero, kind, count, before.Clock, r.First ?? r.Clock, exact: r.First != null);
                        // Already seen since the earlier read (a ward cannot be seen before it is placed), maybe dewarded by now.
                        if (!sighted.Any(w => w.First >= guess.After - 2 && Answers(guess, w.Kind, w.X, w.Y))) {
                            found.Add(guess);
                            guesses.Add(guess); // later guesses of this read see it standing
                        }
                    }
                }
            if (guesses.Count > MaxGuesses) guesses.RemoveRange(0, guesses.Count - MaxGuesses);
        }
        return found;
    }
    /// <summary>Guesses whose ward may still stand at game clock <paramref name="now"/>.</summary>
    public WardGuess[] Active(double now)
    {
        lock (gate) { guesses.RemoveAll(g => g.Expires <= now); return guesses.ToArray(); }
    }
    /// <summary>The active guesses as minimap marks for the game machine (positions 0..1 across the minimap), except unsure ones.</summary>
    public WardMark[] Marks(double now) => Active(now).Where(g => g.Unseen < UnsureSeconds).SelectMany(g => g.Candidates.Select((k, rank) => {
        var (x, y) = map.Minimap(k.X, k.Y);
        return new WardMark(g.Hero, g.Kind, Math.Clamp(x, 0, 1), Math.Clamp(y, 0, 1), rank, Math.Round(k.Share, 2),
            Math.Max(0, Math.Round(now - g.Before, 1)), Math.Max(0, Math.Round(g.Expires - now, 1)), k.Rough);
    })).ToArray();

    private WardGuess Guess(string hero, string kind, int count, double after, double before, bool exact)
    {
        var track = tracks.TryGetValue(hero, out var t) ? t.Points : [];
        // The ward went down between the reads: up to the next whole second when they carry the GSI's clock.
        double end = exact ? before : before + 1;
        double from = after - (exact ? ExactSlack : EarlySlack), to = end + (exact ? ExactSlack : LateSlack);
        var points = track.Where(p => p.Clock >= from && p.Clock <= to).ToList();
        // The window splits into stretches in sight (samples under 1.5 s apart) and gaps out of sight between them.
        var stretches = new List<WardModel.Stretch>();
        var gaps = new List<WardModel.Gap>();
        for (int i = 0; i < points.Count; i++) {
            if (i == 0 || points[i].Clock - points[i - 1].Clock > 1.5) {
                if (i > 0) gaps.Add(new((points[i - 1].X, points[i - 1].Y), (points[i].X, points[i].Y), points[i - 1].Clock,
                    points[i].Clock - points[i - 1].Clock, points[i].Clock - points[i - 1].Clock));
                stretches.Add(new([], 0.5));
            } else stretches[^1] = stretches[^1] with { Seconds = stretches[^1].Seconds + points[i].Clock - points[i - 1].Clock };
            stretches[^1].Seen.Add((points[i].X, points[i].Y));
        }
        // Out of sight as the window opens or closes: it walked unseen from where it was last seen before (or to where it
        // was seen next). A read taken as the hero left sight (or, before 2026-11-22, while the HUD still showed it out of
        // sight) otherwise put the ward where it reappeared and called the guess "in sight".
        if (points.Count > 0 && track.FindLastIndex(p => p.Clock < from) is >= 0 and int i0 && points[0].Clock - Math.Max(track[i0].Clock, after) > 1.5) {
            var last = track[i0];
            gaps.Insert(0, new((last.X, last.Y), (points[0].X, points[0].Y), last.Clock, points[0].Clock - last.Clock,
                points[0].Clock - Math.Max(last.Clock, after)));
        }
        if (points.Count > 0 && track.FindIndex(p => p.Clock > to) is >= 0 and int i1 && end - points[^1].Clock > 1.5) {
            var next = track[i1];
            gaps.Add(new((points[^1].X, points[^1].Y), (next.X, next.Y), points[^1].Clock, next.Clock - points[^1].Clock,
                end - points[^1].Clock));
        }
        string enemy = ownTeam == 2 ? "dire" : "radiant";
        int phase = before < 600 ? 0 : before < 1500 ? 1 : 2;
        var pool = (spots?.For(enemy, kind) ?? []).Where(s => !map.NoWard(s.X, s.Y)).ToArray();
        var standing = guesses.Where(g => g.Before + (g.Kind == "observer" ? ObserverLifetime : SentryLifetime) > after &&
                                          (g.Kind == kind || "ward" == g.Kind || kind == "ward")).SelectMany(g => g.Candidates).ToArray();
        var scene = new WardModel.Scene(kind, phase, PriorAll, track, standing, ownObservers, (from, to) => Eyes(from, to));
        var parts = WardModel.Parts(pool, stretches, gaps, scene);
        var likely = WardModel.Likelihood(parts, pool.Length);
        var candidates = new List<WardCandidate>();
        double share = 1;
        // In sight all along a short window: the hero's own position says more than any common spot.
        if (gaps.Count == 0 && end - after <= AnchorWindow && Anchor(track, (after + end) / 2) is var (ax, ay)) {
            candidates.Add(new(ax, ay, AnchorShare, map.Describe(ax, ay, ownTeam)));
            share = 1 - AnchorShare;
        }
        foreach (int i in Enumerable.Range(0, pool.Length).Where(i => likely[i] > 0).OrderByDescending(i => likely[i])) {
            if (candidates.Count == MaxCandidates) break;
            if (candidates.Any(k => double.Hypot(k.X - pool[i].X, k.Y - pool[i].Y) < MinSpacing)) continue;
            candidates.Add(new(pool[i].X, pool[i].Y, share * likely[i], map.Describe(pool[i].X, pool[i].Y, ownTeam)));
        }
        if (candidates.Count == 0 && points.Count > 0) {
            // No common spot fits: where it went out of sight for longest, else where it was halfway through.
            var middle = points.MinBy(p => Math.Abs(p.Clock - (after + before) / 2));
            var at = gaps.Count > 0 ? gaps.MaxBy(g => g.Seconds)!.A : (middle.X, middle.Y);
            candidates.Add(new(at.X, at.Y, 1, map.Describe(at.X, at.Y, ownTeam), Rough: true));
        }
        int lifetime = kind == "observer" ? ObserverLifetime : SentryLifetime;
        var guess = new WardGuess($"{hero}:{before}:{kind}:{++sequence}", hero, kind, count, after, before, before + lifetime,
            candidates.ToArray(), Math.Round(gaps.Sum(g => g.Seconds), 1));
        Explain?.Invoke(guess, pool, parts);
        return guess;
    }
    /// <summary>
    /// Where the hero stood at <paramref name="mid"/> (between the track points around it, at most AnchorNear away),
    /// moved AnchorAhead along its way in the second before: a ward goes down a cast range ahead of a hero walking to it.
    /// </summary>
    private static (double X, double Y)? Anchor(List<(double Clock, double X, double Y)> track, double mid)
    {
        int b = track.FindLastIndex(p => p.Clock <= mid && p.Clock >= mid - AnchorNear);
        int a = track.FindIndex(p => p.Clock > mid && p.Clock <= mid + AnchorNear);
        if (b < 0 && a < 0) return null;
        double x, y;
        if (b >= 0 && a >= 0) {
            double u = (mid - track[b].Clock) / Math.Max(track[a].Clock - track[b].Clock, 1e-6);
            (x, y) = (track[b].X + u * (track[a].X - track[b].X), track[b].Y + u * (track[a].Y - track[b].Y));
        }
        else (x, y) = b >= 0 ? (track[b].X, track[b].Y) : (track[a].X, track[a].Y);
        if (track.FindIndex(p => p.Clock >= mid - 1 && p.Clock <= mid - 0.35) is >= 0 and int k) {
            double vx = x - track[k].X, vy = y - track[k].Y, n = double.Hypot(vx, vy);
            if (n >= 60) (x, y) = (x + vx / n * AnchorAhead, y + vy / n * AnchorAhead);
        }
        return (x, y);
    }
    /// <summary>Up to 12 of the friend team's eye snapshots between two clocks, evenly spread.</summary>
    private List<(double X, double Y, double R)[]> Eyes(double from, double to)
    {
        int i0 = eyes.FindIndex(e => e.Clock >= from), i1 = eyes.FindLastIndex(e => e.Clock <= to);
        var out_ = new List<(double X, double Y, double R)[]>();
        if (i0 < 0 || i1 < i0) return out_;
        for (int i = i0; i <= i1; i += Math.Max(1, (i1 - i0 + 1) / 12)) out_.Add(eyes[i].Units);
        return out_;
    }
}


/// <summary>
/// How likely each common spot is to hold a ward that went down in a guess's window, from the parts of the window: a
/// stretch in sight or a gap out of sight. Within a part a spot scores the weights times its features (below) and the
/// part's spots share the part softmax-wise; parts weigh by their seconds, gaps also by their length. The weights were
/// fitted (scripts/replays/fit-ward-model.py, on the features WardEval --features writes) on 47 7.42 replays of
/// 2026-11-19/20 (38 ranked, 9 pro; each team in turn the friend team, reads as the scanner takes them since 2026-11-22)
/// to give the spots within 400 of the real ward the most chance, with spots built without those matches. Against the
/// hand-set model before (spot prior times a Gaussian of the distance to the path in sight, an exponential of the detour
/// out of sight), with the scanner's reads before and since then, the likeliest mark came within 600 of the real ward
/// more often when the hero was out of sight: 54 → 66 % for under 5 s, 41 → 45 % for 5–15 s, 28 → 33 % for 15–60 s,
/// 16 → 21 % beyond (docs/twopc-enemy-wards.md); five-fold by match, unseen matches scored 35.7 % against 36.0 % seen.
/// </summary>
public static class WardModel
{
    public sealed record Stretch(List<(double X, double Y)> Seen, double Seconds);
    /// <param name="A">Where it vanished, at clock <paramref name="Left"/>.</param>
    /// <param name="B">Where it was seen next.</param>
    /// <param name="Reach">Seconds from A to B: how far it could walk.</param>
    /// <param name="Seconds">Of the guess window, out of sight.</param>
    public sealed record Gap((double X, double Y) A, (double X, double Y) B, double Left, double Reach, double Seconds);
    /// <param name="Standing">Marks of earlier guesses of this kind still standing: a team seldom wards a spot it holds.</param>
    /// <param name="OwnObservers">The friend team's observer wards: sentries go near them to deward.</param>
    /// <param name="Eyes">The friend team's eyes between two clocks.</param>
    public sealed record Scene(string Kind, int Phase, double PriorAll, List<(double Clock, double X, double Y)> Track,
        WardCandidate[] Standing, (double X, double Y)[] OwnObservers, Func<double, double, List<(double X, double Y, double R)[]>> Eyes);
    /// <summary>One part of a window and each spot's features, in the order of <see cref="StretchFeatures"/> or
    /// <see cref="GapFeatures"/>; null for a spot it could not have walked to.</summary>
    public sealed record Part(bool Gap, double Seconds, double Reach, double[]?[] Features);

    /// <summary>
    /// In sight: spot prior (log of the phase's count + PriorAll × all + 0.5), log of all + 1, (distance to the seen path
    /// / 500)², distance beyond 550 (/500), earlier marks standing near (their shares, Gaussian 400), nearness to an own
    /// observer ward (1 − distance / 1200) for a sentry or a ward of unknown kind, and for an observer.
    /// </summary>
    public static readonly string[] StretchFeatures = ["prior", "count", "near2", "far", "standing", "ownSentry", "ownObserver"];
    /// <summary>
    /// Out of sight: spot prior, log of all, detour (/1000), detour as a share of the extra walk it had time for (at most
    /// 3), heading as it vanished (cosine, 0 within 300), nearer end of the gap (/1000), share of the gap the spot lay in
    /// the friend team's sight, earlier marks standing near, nearness to an own observer for a sentry and for an observer.
    /// </summary>
    public static readonly string[] GapFeatures = ["prior", "count", "detour", "used", "heading", "end", "sight", "standing", "ownSentry", "ownObserver"];
    private static readonly double[] StretchWeights = [1.71, -0.78, -0.90, -1.72, 0.34, 0.81, -0.01];
    private static readonly double[] GapWeights = [1.63, -0.57, -0.22, -1.63, 0.72, -0.34, -1.22, -1.06, -0.85, -0.69];
    // Weight of a gap against a stretch of equal seconds: exp(GapBase + GapLength × ln(1 + its length)).
    private const double GapBase = 0.01, GapLength = 0.29;

    public static Part[] Parts(WardSpot[] pool, List<Stretch> stretches, List<Gap> gaps, Scene scene)
    {
        int n = pool.Length;
        if (n == 0) return [];
        var prior = pool.Select(s => Math.Log(s.Phases.ElementAtOrDefault(scene.Phase) + scene.PriorAll * s.Count + 0.5)).ToArray();
        var count = pool.Select(s => Math.Log(s.Count + 1)).ToArray();
        var standing = pool.Select(s => scene.Standing.Sum(k => k.Share * Math.Exp(-(Sq(k.X - s.X) + Sq(k.Y - s.Y)) / (2 * 400 * 400)))).ToArray();
        var own = pool.Select(s => scene.OwnObservers.Select(o => Math.Max(0, 1 - double.Hypot(o.X - s.X, o.Y - s.Y) / 1200)).DefaultIfEmpty(0).Max()).ToArray();
        bool observer = scene.Kind == "observer";
        var parts = new List<Part>();
        foreach (var st in stretches) {
            var f = new double[]?[n];
            for (int i = 0; i < n; i++) {
                double d = st.Seen.Min(p => double.Hypot(p.X - pool[i].X, p.Y - pool[i].Y));
                f[i] = [prior[i], count[i], Sq(d / 500), Math.Max(0, d - 550) / 500, standing[i], observer ? 0 : own[i], observer ? own[i] : 0];
            }
            parts.Add(new(false, st.Seconds, 0, f));
        }
        foreach (var g in gaps) {
            double direct = double.Hypot(g.A.X - g.B.X, g.A.Y - g.B.Y), budget = WardTracker.Speed * g.Reach - direct;
            // Heading as it vanished: from where it was 0.2–1.6 s before.
            (double X, double Y)? heading = null;
            if (scene.Track.FindIndex(p => p.Clock >= g.Left - 1.6 && p.Clock <= g.Left - 0.2) is >= 0 and int k) {
                double vx = g.A.X - scene.Track[k].X, vy = g.A.Y - scene.Track[k].Y, len = double.Hypot(vx, vy);
                if (len >= 100) heading = (vx / len, vy / len);
            }
            var eyes = scene.Eyes(g.Left, g.Left + g.Reach);
            var f = new double[]?[n];
            for (int i = 0; i < n; i++) {
                var s = pool[i];
                double dA = double.Hypot(g.A.X - s.X, g.A.Y - s.Y), dB = double.Hypot(g.B.X - s.X, g.B.Y - s.Y), detour = dA + dB - direct;
                if (dA + dB > WardTracker.Speed * g.Reach + 2 * WardTracker.CastRange) continue;
                double cos = heading is var (hx, hy) && dA > 300 ? ((s.X - g.A.X) * hx + (s.Y - g.A.Y) * hy) / dA : 0;
                double sight = eyes.Count == 0 ? 0 : (double)eyes.Count(e => e.Any(u => double.Hypot(u.X - s.X, u.Y - s.Y) <= u.R)) / eyes.Count;
                f[i] = [prior[i], count[i], Math.Max(0, detour) / 1000, Math.Clamp(detour / Math.Max(budget, 500), 0, 3), cos,
                        Math.Min(dA, dB) / 1000, sight, standing[i], observer ? 0 : own[i], observer ? own[i] : 0];
            }
            parts.Add(new(true, g.Seconds, g.Reach, f));
        }
        return parts.ToArray();
    }

    /// <summary>Each of <paramref name="n"/> spots' share of the guess (summing to 1 when any part fits; all 0 when none does).</summary>
    public static double[] Likelihood(Part[] parts, int n)
    {
        var total = new double[n];
        var shares = new List<(double Logit, double[] Share)>();
        foreach (var part in parts) {
            var w = part.Gap ? GapWeights : StretchWeights;
            var score = part.Features.Select(f => f == null ? double.NegativeInfinity : f.Select((v, j) => v * w[j]).Sum()).ToArray();
            if (Softmax(score) is { } share)
                shares.Add((Math.Log(Math.Max(part.Seconds, 0.05)) + (part.Gap ? GapBase + GapLength * Math.Log(1 + part.Reach) : 0), share));
        }
        if (shares.Count == 0) return total;
        double top = shares.Max(p => p.Logit), sum = shares.Sum(p => Math.Exp(p.Logit - top));
        foreach (var (logit, share) in shares)
            for (int i = 0; i < n; i++) total[i] += Math.Exp(logit - top) / sum * share[i];
        return total;
    }
    private static double Sq(double v) => v * v;
    /// <summary>Shares of the finite scores; null when none is.</summary>
    private static double[]? Softmax(double[] score)
    {
        if (score.Length == 0) return null;
        double top = score.Max();
        if (double.IsNegativeInfinity(top)) return null;
        var e = score.Select(s => Math.Exp(s - top)).ToArray();
        double sum = e.Sum();
        return e.Select(v => v / sum).ToArray();
    }
}
