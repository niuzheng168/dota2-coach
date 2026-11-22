namespace DotaCoach.TwoPc;

/// <summary>Allowlisted evidence from the flat participant packet. No identity, observer or minimap data.
/// InstalledBuild describes the game PC's installation, not a version reported by GSI or a server attestation.
/// Epoch changes after an interruption, even if no video frame sampled the interrupting GSI packet.</summary>
public sealed record FarmPlayerTelemetry(bool OwnParticipant, bool InProgress, bool? Paused,
    double? MoveSpeed = null, double? AttackDamage = null, double? AttackSpeed = null, int? Level = null,
    int? InstalledBuild = null, Guid Epoch = default, double? MaxHealth = null, double? MaxMana = null, int? LastHits = null,
    bool? Silenced = null, bool? Muted = null);

public static class MinuteFarmContext
{
    public const string Kind = "minute-farm-route";
    public const int MaxDisplayMs = 60_000, FreshMs = 3_000, SupportedBuild = 6989;
    private const string HeroPrefix = "npc_dota_hero_";

    // Packet freshness is enforced at ingress; captured-frame freshness must additionally be checked on
    // each machine's own monotonic clock. Missing optional combat readings are NOT evidence against ownership.
    public static bool Eligible(OwnState? own) =>
        own is { Alive: true, MatchId: { Length: >= 1 and <= 20 } match, Hero: { } hero, Team: "radiant" or "dire",
            Farm: { OwnParticipant: true, InProgress: true, Paused: false, InstalledBuild: null or SupportedBuild } f,
            X: double x, Y: double y } &&
        match[0] != '0' && match.All(char.IsAsciiDigit) &&
        ulong.TryParse(match, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out _) &&
        hero.StartsWith(HeroPrefix, StringComparison.Ordinal) && hero.Length > HeroPrefix.Length && hero.Length <= HeroPrefix.Length + 64 &&
        hero.AsSpan(HeroPrefix.Length).IndexOfAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789_") < 0 &&
        Wire.Finite(own.Clock, 0, 43_200) && own.Health > 0 && Wire.Finite(own.Health, 0, 100_000) &&
        Wire.Finite(own.Mana, 0, 100_000) && Wire.Finite(x, -12_000, 12_000) && Wire.Finite(y, -12_000, 12_000) &&
        ValidStats(f) && !(own.Health > f.MaxHealth) && !(own.Mana > f.MaxMana);

    internal static bool ValidStats(FarmPlayerTelemetry f) =>
        (f.MoveSpeed is not double m || Wire.Finite(m, 1, 2_000)) &&
        (f.AttackDamage is not double d || Wire.Finite(d, 1, 10_000)) &&
        (f.AttackSpeed is not double a || Wire.Finite(a, 1, 2_000)) && f.Level is null or (>= 1 and <= 30) &&
        f.InstalledBuild is null or (>= 1 and <= 10_000_000) && (f.MaxHealth is not double hp || Wire.Finite(hp, 1, 100_000)) &&
        (f.MaxMana is not double mp || Wire.Finite(mp, 0, 100_000)) && f.LastHits is null or (>= 0 and <= 100_000);

    internal static int Minute(OwnState own) => (int)(own.Clock / 60);
    internal static int RemainderMs(OwnState own) => (int)Math.Floor((60 - own.Clock % 60) * 1000);
    internal static bool FreshAt(long sampledAt, long now) => now >= sampledAt && now - sampledAt < FreshMs;
    internal static bool Captured(FrameMeta m) => m.Source is "dota2" or "dota2-gsi" &&
        m.GsiAgeMs is >= 0 and < FreshMs && Eligible(m.Player) &&
        m.MatchPhase?.InstalledBuild is null or SupportedBuild;
    /// <summary>The capture lease lower bound plus GSI age, not merely the last LAN arrival or video presence.</summary>
    public static bool Fresh(FrameView? frame, long now) => frame != null && Captured(frame.Meta) &&
        frame.CapturedNoEarlierThan <= now && FreshAt(frame.CapturedNoEarlierThan - frame.Meta.GsiAgeMs!.Value, now);

    internal static bool SameScope(OwnState a, OwnState b) => a.MatchId == b.MatchId && a.Team == b.Team &&
        a.Hero == b.Hero && a.Farm!.Epoch == b.Farm!.Epoch && a.Farm.InstalledBuild == b.Farm.InstalledBuild &&
        Minute(a) == Minute(b);
    // Conservative cancellation, NOT detection of an enemy or of a particular spell. Compare successive
    // observations as well as the route origin; a sudden discontinuity invalidates the entire old epoch.
    internal static bool Continues(OwnState a, OwnState b)
    {
        if (!SameScope(a, b) || b.Clock < a.Clock || b.Health < .70 * a.Health) return false;
        double dx = b.X!.Value - a.X!.Value, dy = b.Y!.Value - a.Y!.Value;
        double limit = 1600 + 1000 * (b.Clock - a.Clock);
        return dx * dx + dy * dy <= limit * limit;
    }

    internal static bool Shape(int? minute, ScreenMark? marker, ScreenMark[]? route, string[]? steps,
        MinimapLayout? map, int width, int height) =>
        minute is >= 0 and <= 720 && route != null && steps != null && route.Length == steps.Length &&
        Wire.ValidRoute(route, marker) && Wire.ValidSteps(steps) && map != null && map.IsValidFor(width, height) &&
        route.All(map.Contains);
}

/// <summary>Minute-only sequence fence shared by coach ingress and game capture. Call under the owner's lock.
/// Keeps no pictures. Losing scope revokes all earlier origins, including pending model requests.</summary>
internal sealed class MinuteFarmWindow
{
    public FrameMeta? Current { get; private set; }
    private long firstSequence, lastSequence, sampledAt, advancedAt;
    public void Clear() { Current = null; lastSequence = 0; }
    public bool Fresh(long now) => Current != null && MinuteFarmContext.FreshAt(sampledAt, now) &&
        MinuteFarmContext.FreshAt(advancedAt, now);

    public void Observe(FrameMeta m, long captureBound, long now)
    {
        // Remember runs before the game knows whether an upload succeeded. A reordered/rejected send must
        // never move its local sequence fence backwards. Only Clear (a session reset) reopens sequence space.
        if (m.Sequence <= lastSequence) { Current = null; return; }
        lastSequence = m.Sequence;
        if (!MinuteFarmContext.Captured(m) || captureBound > now ||
            !MinuteFarmContext.FreshAt(captureBound - m.GsiAgeMs!.Value, now)) { Current = null; return; }
        long stamp = captureBound - m.GsiAgeMs.Value;
        var prev = Current;
        bool continues = prev != null && prev.SessionId == m.SessionId && prev.Source == m.Source &&
            prev.SourceWidth == m.SourceWidth && prev.SourceHeight == m.SourceHeight &&
            MinuteFarmContext.Continues(prev.Player!, m.Player!);
        if (!continues || !Fresh(now)) firstSequence = m.Sequence;
        // Repeated captures/heartbeats of a frozen game clock cannot extend this clock-progress lease.
        if (!continues || prev!.Player!.Clock != m.Player!.Clock) advancedAt = stamp;
        Current = m; sampledAt = stamp;
    }

    public long Deadline(FrameMeta origin, long captureBound, int? minute, long now)
    {
        if (!Fresh(now) || !MinuteFarmContext.Captured(origin) || origin.Sequence < firstSequence ||
            origin.Sequence > Current!.Sequence || origin.SessionId != Current.SessionId || origin.Source != Current.Source ||
            origin.SourceWidth != Current.SourceWidth || origin.SourceHeight != Current.SourceHeight ||
            MinuteFarmContext.Minute(origin.Player!) != minute || !MinuteFarmContext.Continues(origin.Player!, Current.Player!))
            return now;
        // Both clocks can shorten the lease. Neither a new picture nor a new poll starts a fresh minute.
        return Math.Min(captureBound - origin.GsiAgeMs!.Value + MinuteFarmContext.RemainderMs(origin.Player!),
            sampledAt + MinuteFarmContext.RemainderMs(Current.Player!));
    }
}

/// <summary>Raw packet continuity, kept on the game PC. A pause, death or observer packet between two
/// captures must still fence the old source on the coach. No Steam ID or absolute clock crosses the wire.</summary>
internal sealed class MinuteGsiStream
{
    private OwnState? previous;
    private (ulong User, int Seat, int TeamSeat) previousKey;
    private Guid epoch;
    private long sampledAt, advancedAt, newestUnix, inactiveThrough;

    public void Invalidate(long? packetUnix)
    {
        previous = null;
        previousKey = default;
        // GSI timestamps have one-second granularity. A late packet from the withdrawal second cannot
        // prove that the player has resumed; wait for the next second instead.
        inactiveThrough = Math.Max(inactiveThrough, Math.Max(newestUnix, packetUnix ?? 0));
    }

    public int? Age(long now) => previous != null && MinuteFarmContext.FreshAt(sampledAt, now) &&
        MinuteFarmContext.FreshAt(advancedAt, now) ? (int)(now - sampledAt) : null;

    public OwnState Observe(OwnState own, long? packetUnix, long utc, long now, (ulong User, int Seat, int TeamSeat) key)
    {
        if (!MinuteFarmContext.Eligible(own) || packetUnix is not long stamp || stamp <= inactiveThrough || stamp < newestUnix) {
            // Receipt time can revoke an unstamped packet; it is NEVER used as positive source-time evidence.
            Invalidate(packetUnix ?? utc / 1000 * 1000);
            return MinuteFarmContext.Eligible(own) ? own with { Farm = null } : own;
        }
        newestUnix = stamp;
        long sampled = now - Math.Max(0, utc - stamp);
        bool continues = previous != null && previousKey == key && MinuteFarmContext.Continues(previous, own);
        if (!continues || Age(now) == null) epoch = Guid.NewGuid();
        if (!continues || previous!.Clock != own.Clock) advancedAt = sampled;
        previous = own; previousKey = key; sampledAt = sampled;
        return own with { Farm = Age(now) == null ? null : own.Farm! with { Epoch = epoch } };
    }

    // Read only the small manifest from the configured Dota installation; never consult provider.version,
    // process memory, replay data or an unrelated Steam library. This is an installation witness ONLY.
    internal static (int? Build, bool Conflict) ReadInstall(string install)
    {
        try {
            if (!Path.IsPathFullyQualified(install)) return (null, false);
            string path = Path.Combine(install, "game", "dota", "steam.inf");
            using var file = File.OpenRead(path);
            if (file.Length is < 1 or > 4096) return (null, true);
            using var reader = new StreamReader(file);
            int? app = null, client = null, server = null;
            bool Set(ref int? dest, string text) {
                if (dest.HasValue || !int.TryParse(text, System.Globalization.NumberStyles.None,
                    System.Globalization.CultureInfo.InvariantCulture, out int n) || n is < 1 or > 10_000_000) return false;
                dest = n; return true;
            }
            while (reader.ReadLine() is { } line) {
                var kv = line.Split('=', 2);
                if (kv.Length != 2) continue;
                if (kv[0] == "appID" && !Set(ref app, kv[1]) ||
                    kv[0] == "ClientVersion" && !Set(ref client, kv[1]) ||
                    kv[0] == "ServerVersion" && !Set(ref server, kv[1])) return (null, true);
            }
            return app == 570 && client.HasValue && client == server ? (client, false) : (null, true);
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException) {
            return (null, false); // No installation witness: the coach's separate per-match confirmation applies.
        }
    }
}
