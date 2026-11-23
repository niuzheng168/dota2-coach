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
    /// <summary>The coach's current panel for this session. It is the coach's last-known state, not tied to one frame:
    /// the game machine shows it until the coach replaces or withdraws it.</summary>
    public static EnemyBoard? Read(ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length is 0 or > 65536) return null;
        try {
            var board = JsonSerializer.Deserialize<Envelope>(bytes, Json)?.Enemies;
            return Wire.ValidBoard(board) && board is { MatchId: not null, Team: not null } &&
                Guid.TryParseExact(board.SessionId, "N", out _) ? board : null;
        } catch (JsonException) { return null; }
    }
    // Standard build 6989 overview: posX=-9472, posY=9472, span=18944. No side mirroring.
    private const int WorldEdge = 9472;
    public static ScreenMark? OnOverview(ObservedWard w) => OnOverview(w.X, w.Y);
    public static ScreenMark? OnOverview(double x, double y) =>
        Wire.Finite(x, -WorldEdge, WorldEdge) && Wire.Finite(y, -WorldEdge, WorldEdge) ?
        new((x + WorldEdge) / (2 * WorldEdge), (WorldEdge - y) / (2 * WorldEdge)) : null;

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

    // Equipment is last-known until match end; a wave is a short-lived projection in one calibrated frame.
    internal static bool WaveWitness(EnemyBoard b, MatchWitness? w) =>
        b.LaneMap is { } map && w is { Phase: "playing" } && Live(w) && w.Team == b.Team && w.MatchId == b.MatchId &&
        w.InstalledBuild is null or MinuteFarmContext.SupportedBuild && w.Clock >= map.Clock && w.Clock - map.Clock < LaneMap742.Lease / 1000d;
    internal static bool WaveFrame(EnemyBoard b, FrameMeta m, bool origin = false) =>
        m.Source is "dota2" or "dota2-gsi" && WaveWitness(b, m.MatchPhase) &&
        InstalledBuild(m) is null or MinuteFarmContext.SupportedBuild && b.Minimap?.IsValidFor(m.SourceWidth, m.SourceHeight) == true &&
        (!origin || b.LaneMap!.Clock == Math.Floor(m.MatchPhase!.Clock));

    // Age all last reads while the panel is held. Never invent an age for a never-read hero,
    // resuscitate an expired candidate, or substitute an estimate for a sighting. Cut mutable array aliases.
    internal static EnemyBoard Age(EnemyBoard board, long elapsedMs, int remainingMs)
    {
        double seconds = Math.Max(0, elapsedMs) / 1000d;
        return board with {
            Heroes = board.Heroes.Select(h => h with { Items = h.Items?.ToArray(), Age = h.Age is double a ? Math.Min(86_400, a + seconds) : null }).ToArray(),
            Wards = board.Wards?.Where(w => w.Left > seconds).Select(w => w with {
                Age = Math.Min(86_400, w.Age + seconds), Left = w.Left - seconds }).ToArray(),
            SeenWards = board.SeenWards?.ToArray(), LaneMap = board.LaneMap?.Age(elapsedMs), RemainingMs = remainingMs,
        };
    }
    internal static bool HasIcon(EnemyBoard? board, string key) =>
        board?.Heroes.Any(h => h.Hero == key || h.Neutral == key || h.Items?.Contains(key) == true) == true;
    internal static bool Empty(EnemyBoard board) =>
        board.Heroes.Length == 0 && board.Wards is not { Length: > 0 } && board.SeenWards is not { Length: > 0 } && board.LaneMap == null;
    internal static EnemyBoard? Legacy(EnemyBoard? board) {
        if (board?.LaneMap == null) return board;
        var legacy = board with { LaneMap = null };
        return Empty(legacy) ? null : legacy;
    }
}
