namespace DotaCoach.TwoPc;

/// <summary>
/// The game machine's enemy panel as the coach last knew it. Every enemy keeps its last read level and items,
/// aged on the paired player's game clock, until another match or team starts or the match ends. A late, stale
/// or missing scanner write, a Plus packet gap or a restarted scanner never blanks a row that was already read.
/// Ward guesses stay until their own lifetime runs out; sighted wards only while Plus keeps reporting them.
/// </summary>
public sealed class LastKnownBoard
{
    /// <summary>Without a frame naming the match for this long (menu, abandoned game) nothing is published.</summary>
    public const int ParticipantMs = 15_000;
    public const int SeenWardsMs = 5_000;
    private string? match, team;
    private double clock = double.NegativeInfinity;
    private long participantAt;
    private string[] order = [];
    // ReadAt: game clock of the reading (scan clock - age); null = listed but never read.
    private readonly Dictionary<string, (EnemyHero Row, double? ReadAt)> rows = new(StringComparer.Ordinal);
    private (WardMark[] Marks, double At)? estimates;
    private (ObservedWard[] Wards, long At)? seen;

    public string? Match => match;
    public string? Team => team;
    /// <summary>Enemies with a reading (level or items) on the panel now.</summary>
    public int Read => rows.Values.Count(r => r.ReadAt != null);
    public void Clear()
    {
        match = team = null; clock = double.NegativeInfinity; order = [];
        rows.Clear(); estimates = null; seen = null;
    }
    /// <summary>The paired player's match, team and game clock from its latest frame. Another match or team starts empty.</summary>
    public void Participant(string matchId, string side, double gameClock, long now)
    {
        if (matchId != match || side != team) { Clear(); match = matchId; team = side; }
        if (double.IsFinite(gameClock) && gameClock > clock) clock = gameClock;
        participantAt = now;
    }
    /// <summary>A scanner write for this match: newer readings replace older ones, unread rows never erase a reading.</summary>
    public void Equipment(string matchId, double scanClock, IReadOnlyList<EnemyHero> heroes)
    {
        if (match == null || matchId != match || !double.IsFinite(scanClock) || heroes.Count is 0 or > 5) return;
        // A restarted scanner may list fewer enemies at first: heroes it no longer lists keep their place after it.
        order = heroes.Select(h => h.Hero).Concat(order).Distinct(StringComparer.Ordinal).Take(5).ToArray();
        foreach (var h in heroes) {
            bool read = h.Age is double age && double.IsFinite(age) && (h.Level != null || h.Items != null || h.Neutral != null);
            double? at = read ? scanClock - h.Age!.Value : null;
            var row = h with { Items = h.Items?.ToArray(), Age = null };
            if (!rows.TryGetValue(h.Hero, out var old)) { rows[h.Hero] = (row, at); continue; }
            if (at is not double readAt) continue;
            // A level-only read (the scanner restarted and has not reached the items yet) doesn't drop known items.
            int rank = Rank(row), oldRank = Rank(old.Row);
            if (old.ReadAt is not double oldAt || rank > oldRank || rank == oldRank && readAt >= oldAt - 1)
                rows[h.Hero] = (row, readAt);
        }
        if (clock < scanClock) clock = scanClock;
    }
    private static int Rank(EnemyHero row) => row.Items != null ? 2 : row.Level != null || row.Neutral != null ? 1 : 0;
    /// <summary>The ward tracker's current guesses at game clock <paramref name="knownAt"/> (an empty list means none).</summary>
    public void Estimates(string matchId, WardMark[] marks, double knownAt)
    {
        if (match == null || matchId != match || !double.IsFinite(knownAt)) return;
        estimates = (marks.ToArray(), knownAt);
    }
    /// <summary>Wards the friend team sees in the latest Plus packet.</summary>
    public void Seen(string matchId, ObservedWard[] wards, long now)
    {
        if (match == null || matchId != match) return;
        seen = (wards.ToArray(), now);
    }
    /// <summary>What the game machine should draw now, or null when nothing is known for its current match.</summary>
    public EnemyBoard? Board(long now, bool equipment, bool wards, MinimapLayout? layout)
    {
        if (match == null || now - participantAt is < 0 or >= ParticipantMs) return null;
        var heroes = !equipment ? [] : order.Where(rows.ContainsKey).Select(h => {
            var (row, readAt) = rows[h];
            return row with { Items = row.Items?.ToArray(), Age = readAt is double at ? Math.Clamp(clock - at, 0, 86_400) : null };
        }).ToArray();
        WardMark[]? marks = null;
        if (wards && estimates is { } guesses) {
            double elapsed = Math.Max(0, clock - guesses.At);
            marks = guesses.Marks.Where(w => w.Left > elapsed)
                .Select(w => w with { Age = Math.Min(86_400, w.Age + elapsed), Left = w.Left - elapsed }).ToArray();
        }
        var sighted = wards && seen is { } s && now - s.At is >= 0 and < SeenWardsMs ? s.Wards.ToArray() : null;
        if (heroes.Length == 0 && marks is not { Length: > 0 } && sighted is not { Length: > 0 }) return null;
        return new EnemyBoard(heroes, marks, match, team, layout, sighted);
    }
}
