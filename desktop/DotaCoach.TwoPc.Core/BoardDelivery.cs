using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotaCoach.TwoPc;

/// <summary>Transport of the coach's selected subsets; never derives an item, sighting, or ward candidate.</summary>
public static class BoardDelivery
{
    private sealed record Envelope(EnemyBoard? Enemies);
    // Only the filtered downlink has a closed schema. An extra raw scanner field, repeated key, numeric
    // string, or missing required coordinate that would otherwise become zero never reaches a game overlay.
    private static readonly JsonSerializerOptions Json = new(Wire.Json) {
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, PropertyNameCaseInsensitive = false,
        NumberHandling = JsonNumberHandling.Strict, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };
    public static EnemyBoard? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 65536) return null;
        try {
            var board = JsonSerializer.Deserialize<Envelope>(bytes, Json)?.Enemies;
            return Wire.ValidBoard(board) && board is { MatchId: not null, Team: not null, FrameSequence: > 0,
                HeldMs: >= 0 and < Wire.BoardLifetimeMs, RemainingMs: > 0 and <= Wire.BoardLifetimeMs } &&
                board.HeldMs <= Wire.BoardLifetimeMs - board.RemainingMs && Guid.TryParseExact(board.SessionId, "N", out _) ? board : null;
        } catch (JsonException) { return null; }
    }
    // Standard build 6989 overview: posX=-9472, posY=9472, span=18944. No side mirroring.
    private const int WorldEdge = 9472;
    public static ScreenMark? OnOverview(ObservedWard w) =>
        Wire.Finite(w.X, -WorldEdge, WorldEdge) && Wire.Finite(w.Y, -WorldEdge, WorldEdge) ?
        new((w.X + WorldEdge) / (2 * WorldEdge), (WorldEdge - w.Y) / (2 * WorldEdge)) : null;

    public static bool SamePlayer(MatchWitness phase, OwnState own) =>
        phase.MatchId == own.MatchId && phase.Team == own.Team && phase.Hero == own.Hero && phase.Clock == own.Clock;
    public static int? InstalledBuild(FrameMeta m) => m.MatchPhase?.InstalledBuild ?? m.Player?.Farm?.InstalledBuild;
    public static bool Live(MatchWitness? w) => w is { Phase: "pregame" or "playing", Paused: false, AgeMs: < MatchWitness.MaxAgeMs } && MatchWitness.Valid(w);
    internal static void ValidatePhase(FrameMeta meta)
    {
        if (meta.MatchPhase is { } w && (meta.Source is not ("dota2" or "dota2-gsi") || !MatchWitness.Valid(w) ||
            meta.Player != null && !SamePlayer(w, meta.Player) ||
            w.InstalledBuild is int a && meta.Player?.Farm?.InstalledBuild is int b && a != b))
            throw new ArgumentException("Invalid or mismatched participant match witness");
    }
    internal static bool Fresh(MatchWitness? witness, long bound, long instant) =>
        MatchWitness.Valid(witness, instant - bound) && instant - bound < MatchWitness.MaxAgeMs - witness!.AgeMs;
    internal static long ElapsedMs(long wallMs, double fromClock, double gameClock) =>
        Math.Max(wallMs, (long)Math.Round((gameClock - fromClock) * 1000));

    // Age all last reads, even during a LAN request or between draws. Never invent an age for a never-read hero,
    // resuscitate an expired candidate, or substitute an estimate for a sighting. Cut mutable array aliases.
    internal static EnemyBoard Age(EnemyBoard board, long elapsedMs, int remainingMs)
    {
        double seconds = elapsedMs / 1000d;
        return board with {
            Heroes = board.Heroes.Select(h => h with { Items = h.Items?.ToArray(), Age = h.Age is double a ? Math.Min(86_400, a + seconds) : null }).ToArray(),
            Wards = board.Wards?.Where(w => w.Left > seconds).Select(w => w with {
                Age = Math.Min(86_400, w.Age + seconds), Left = w.Left - seconds }).ToArray(),
            SeenWards = board.SeenWards?.ToArray(), RemainingMs = remainingMs,
        };
    }
    internal static bool HasIcon(EnemyBoard? board, string key) =>
        board?.Heroes.Any(h => h.Hero == key || h.Neutral == key || h.Items?.Contains(key) == true) == true;
    internal static bool Empty(EnemyBoard board) =>
        board.Heroes.Length == 0 && board.Wards is not { Length: > 0 } && board.SeenWards is not { Length: > 0 };
}

/// <summary>A sequence fence for a continuous, authenticated participant (alive is NOT a condition).
/// Independent of the route's supported heroes, patch gate, minute, and local clock-progress lease.</summary>
internal sealed class BoardWindow(bool keepOrigins = false)
{
    private FrameMeta? current;
    public double Clock => current!.MatchPhase!.Clock; // Only read after Admits, under the owning lock.
    private long at, first, last, lastIncompatibleHud;
    private string? endedMatch, incompatibleHudMatch;
    // A few seconds even at 120 fps. Only the coach needs this ring: ordinary reminder history is
    // intentionally cleared during death, but a valid equipment publication can race another dead frame.
    private readonly (long Seq, long Stamp, double Clock)[]? origins = keepOrigins ? new (long, long, double)[1024] : null;
    public void Clear() {
        current = null; first = last = lastIncompatibleHud = 0; endedMatch = incompatibleHudMatch = null;
        if (origins != null) Array.Clear(origins);
    }
    public void Withdraw() => current = null;
    public void Observe(FrameMeta m, long bound, long instant)
    {
        if (m.Sequence <= last) { Withdraw(); return; }
        last = m.Sequence;
        var w = m.MatchPhase;
        if (w == null || m.Source is not ("dota2" or "dota2-gsi") || !BoardDelivery.Fresh(w, bound, instant) ||
            m.Player != null && !BoardDelivery.SamePlayer(w, m.Player)) { Withdraw(); return; }
        if (BoardDelivery.InstalledBuild(m) is int build) {
            incompatibleHudMatch = build != MinuteFarmContext.SupportedBuild ? w.MatchId : null;
            if (incompatibleHudMatch != null) lastIncompatibleHud = m.Sequence;
        }
        if (w.Phase == "postgame") endedMatch = w.MatchId;
        if (!BoardDelivery.Live(w) || m.SourceWidth == 0 || w.MatchId == endedMatch) { Withdraw(); return; }
        if (current == null || !BoardDelivery.Fresh(current.MatchPhase, at, instant) ||
            current.SessionId != m.SessionId || current.Source != m.Source ||
            current.SourceWidth != m.SourceWidth || current.SourceHeight != m.SourceHeight ||
            current.MatchPhase!.MatchId != w.MatchId || current.MatchPhase.Team != w.Team || current.MatchPhase.Hero != w.Hero ||
            w.Clock < current.MatchPhase.Clock || current.MatchPhase.Phase == "playing" && w.Phase == "pregame") first = m.Sequence;
        current = m; at = bound;
        if (origins != null) origins[m.Sequence % origins.Length] = (m.Sequence, bound - w.AgeMs, w.Clock);
    }
    public bool Matches(EnemyBoard b, long instant) => current?.MatchPhase is { } w && BoardDelivery.Fresh(w, at, instant) &&
        b.MatchId == w.MatchId && b.Team == w.Team && !b.Heroes.Any(h => h.Hero == w.Hero) && b.Wards?.Any(m => m.Hero == w.Hero) != true;
    public bool Admits(EnemyBoard b, long instant) => Matches(b, instant) && b.SessionId == current!.SessionId &&
        b.FrameSequence >= first && b.FrameSequence <= last;
    public bool TryOrigin(EnemyBoard b, long instant, out double clock)
    {
        clock = 0;
        if (origins == null || !Admits(b, instant)) return false;
        var origin = origins[b.FrameSequence % origins.Length];
        clock = origin.Clock;
        return origin.Seq == b.FrameSequence && instant - origin.Stamp is >= 0 and < MatchWitness.MaxAgeMs;
    }
    public bool Local(MatchWitness? w, int width, int height, long instant)
    {
        if (current?.MatchPhase is not { } c || !BoardDelivery.Fresh(c, at, instant) || !BoardDelivery.Live(w) ||
            w!.MatchId != c.MatchId || w.Team != c.Team || w.Hero != c.Hero || w.Clock < c.Clock ||
            c.Phase == "playing" && w.Phase != "playing" || width != current.SourceWidth || height != current.SourceHeight) return false;
        // Local GSI can discover an incompatible build before the next capture/poll. Keep equipment,
        // but never restore those ward coordinates on a missing build or a re-poll of the same origin.
        if (w.InstalledBuild is int build && build != MinuteFarmContext.SupportedBuild) {
            incompatibleHudMatch = w.MatchId; lastIncompatibleHud = last;
        }
        return true;
    }
    public EnemyBoard Calibrated(EnemyBoard b) =>
        current != null && b.Minimap?.IsValidFor(current.SourceWidth, current.SourceHeight) == true && incompatibleHudMatch != b.MatchId &&
        (b.FrameSequence == 0 || b.FrameSequence > lastIncompatibleHud) ? b :
        b with { Wards = null, SeenWards = null, Minimap = null }; // Equipment doesn't require HUD geometry.
}
