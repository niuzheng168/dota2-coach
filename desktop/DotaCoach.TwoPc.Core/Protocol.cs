using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotaCoach.TwoPc;

public static class Wire
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1_048_576;
    public const int MaxStatusBytes = 32_768;
    /// <summary>Video off: how often the game machine reports its own GSI; often enough to see every game-clock second.</summary>
    public const int StatusIntervalMs = 250;
    /// <summary>Oldest source frame an answer may still be published for (model timeout ≤ 20 s plus capture lease slack).</summary>
    public const int FrameLifetimeMs = 22_000;
    /// <summary>Maximum ordinary reminder display time. Minute routes have a separate game-clock deadline.</summary>
    public const int MaxReminderMs = 15_000;
    public const int DefaultReminderMs = 8_000;
    public const int TicketLifetimeMs = 3_000;
    public const int PresenceLifetimeMs = 4_000;
    // Metadata for every frame of the advice window, even at 120 fps (not images).
    public const int HistoryLimit = 8192;
    public const int MaxRouteSteps = 3, MaxStepText = 60;
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { MaxDepth = 16 };
    public static string Secret() => Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public static bool EqualSecret(string? a, string b) => a != null && a.Length == b.Length &&
        CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(a), Encoding.UTF8.GetBytes(b));
    public static bool IsLan(IPAddress ip)
    {
        if (IPAddress.IsLoopback(ip)) return true;
        if (ip.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork) return false;
        var b = ip.GetAddressBytes();
        return b[0] == 10 || b[0] == 192 && b[1] == 168 || b[0] == 172 && b[1] is >= 16 and <= 31;
    }
    public static bool Finite(double n, double min, double max) => double.IsFinite(n) && n >= min && n <= max;
    /// <summary>Ordered minimap route: 1–3 normalized points, the first one being the primary marker.</summary>
    public static bool ValidRoute(ScreenMark[]? route, ScreenMark? marker) => route == null ||
        route.Length is >= 1 and <= MaxRouteSteps && marker != null && route[0] == marker &&
        route.All(p => p != null && Finite(p.X, 0, 1) && Finite(p.Y, 0, 1));
    public static bool ValidSteps(string[]? steps) => steps == null ||
        steps.Length <= MaxRouteSteps && steps.All(s => !string.IsNullOrWhiteSpace(s) && s.Length <= MaxStepText);
    // A v0.4 status proves presence, not a sampled image, participant, or calibrated overlay.
    internal static bool StatusSubtitle(string kind, ScreenMark? marker, MinimapLayout? map, ScreenMark[]? route, string[]? steps) =>
        (kind is "manual-link-test" or "local-vision-observation") && marker == null && map == null && route == null && steps is null or { Length: 0 };
    /// <summary>An enemy panel the coach stops renewing disappears after this long.</summary>
    public const int BoardLifetimeMs = 5_000;
    public const int MaxWardMarks = WardTracker.MaxGuesses * WardTracker.MaxCandidates, MaxSeenWards = 64;
    public const int MaxIconBytes = 65_536, MaxIconSide = 256;
    private static readonly System.Text.RegularExpressions.Regex IconName =
        new(@"\A(item|npc_dota_hero)_[a-z0-9_]{1,60}\z", System.Text.RegularExpressions.RegexOptions.CultureInvariant);
    /// <summary>Icon names are Dota's own internal names: items and heroes only.</summary>
    public static bool ValidIconKey(string? key) => key != null && IconName.IsMatch(key);
    private static bool Item(string? name) => name == null || name.StartsWith("item_", StringComparison.Ordinal) && ValidIconKey(name);
    private static bool Hero(string? name) => name != null && name.StartsWith("npc_dota_hero_", StringComparison.Ordinal) && ValidIconKey(name);
    // An unbound v0.4.8 board is a valid scanner DTO, NOT authority to publish or render it on the game PC.
    public static bool ValidBoard(EnemyBoard? board) => board?.Heroes is { Length: <= 5 } heroes &&
        (board.MatchId == null && board.Team == null || MatchWitness.MatchIdOk(board.MatchId) && board.Team is "radiant" or "dire") &&
        (board.Minimap == null || board.Minimap.IsValidFor(board.Minimap.SourceWidth, board.Minimap.SourceHeight)) &&
        heroes.All(h => h != null && Hero(h.Hero) &&
            h.Level is null or (>= 1 and <= 30) && (h.Items == null || h.Items.Length == 6 && h.Items.All(Item)) && Item(h.Neutral) &&
            (h.Age is double age ? Finite(age, 0, 86_400) : h.Level == null && h.Items == null && h.Neutral == null)) &&
        heroes.Select(h => h.Hero).Distinct(StringComparer.Ordinal).Count() == heroes.Length &&
        (board.Wards == null || board.Wards.Length <= MaxWardMarks && board.Wards.All(w =>
            w is { Kind: "observer" or "sentry" or "ward", Rank: >= 0 and < WardTracker.MaxCandidates } && Hero(w.Hero) &&
            Finite(w.X, 0, 1) && Finite(w.Y, 0, 1) && Finite(w.Share, 0, 1) && Finite(w.Age, 0, 86_400) &&
            Finite(w.Left, 0, w.Kind == "observer" ? WardTracker.ObserverLifetime : WardTracker.SentryLifetime))) &&
        (board.SeenWards == null || board.SeenWards.Length <= MaxSeenWards && board.SeenWards.All(w =>
            w is { Kind: "observer" or "sentry", Team: "radiant" or "dire" } &&
            Finite(w.X, -16_000, 16_000) && Finite(w.Y, -16_000, 16_000)));
    /// <summary>A small PNG, checked from its header before any image decoder sees network data.</summary>
    public static bool IsIcon(ReadOnlySpan<byte> data)
    {
        if (data.Length is < 33 or > MaxIconBytes || !data[..8].SequenceEqual(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }) ||
            !data[12..16].SequenceEqual("IHDR"u8)) return false;
        int width = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[16..20]);
        int height = System.Buffers.Binary.BinaryPrimitives.ReadInt32BigEndian(data[20..24]);
        return width is >= 1 and <= MaxIconSide && height is >= 1 and <= MaxIconSide;
    }
}

/// <summary>
/// Video upload is opt-in. The legacy status link carries presence and text only. Ticketed participant GSI
/// can authorize a scoped board and minute routes without uploading an image.
/// </summary>
public static class Features
{
    public static bool VideoUpload { get; set; }
}

// This bundle is a credential. Never log it, put it in a URL, or save it with diagnostics.
public sealed record Pairing(int Version, string Address, string Fingerprint, string Token)
{
    public string Encode() => "DCP1." + Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(this, Wire.Json));
    public static Pairing Decode(string text)
    {
        try
        {
            if (text.Length > 4096 || !text.StartsWith("DCP1.", StringComparison.Ordinal)) throw new FormatException();
            var p = JsonSerializer.Deserialize<Pairing>(Convert.FromBase64String(text[5..]), Wire.Json)!;
            if (p.Version != Wire.Version || p.Fingerprint.Length != 64 || p.Token.Length != 64 ||
                !p.Fingerprint.All(Uri.IsHexDigit) || !p.Token.All(Uri.IsHexDigit) ||
                !Uri.TryCreate(p.Address, UriKind.Absolute, out var uri) || uri.Scheme != "https" ||
                uri.AbsolutePath != "/" || uri.UserInfo != "" || uri.Query != "" || uri.Fragment != "" ||
                !IPAddress.TryParse(uri.Host.Trim('[', ']'), out var ip) || !Wire.IsLan(ip))
                throw new FormatException();
            return p;
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentException or NullReferenceException)
        { throw new ArgumentException("配对码无效；只接受本机或局域网 HTTPS 地址。"); }
    }
}

public sealed record StateEntry(string Name, int? Level, double? Cooldown, string? Slot = null);
public sealed record OwnState(string MatchId, string Hero, string Team, double Clock, bool Alive,
    double Health, double Mana, double? X, double? Y, StateEntry[] Items, StateEntry[] Abilities, FarmPlayerTelemetry? Farm = null,
    ParticipantStats? Stats = null);
public sealed record FrameMeta(string SessionId, string Ticket, long Sequence, string Source,
    int SourceWidth, int SourceHeight, OwnState? Player, int? GsiAgeMs, MatchWitness? MatchPhase = null)
{
    // Set only by the actual /v1/status lane on each PC; not a wire flag. Arrival time is not a capture lease.
    internal bool IsLegacyStatus { get; private init; }
    internal FrameMeta ForLegacyStatus()
    {
        // Construct the old projection by inclusion: new fields/slot extensions never silently enter this lane.
        static StateEntry[] Flat(StateEntry[]? rows) => rows is { Length: <= 40 }
            ? Array.ConvertAll(rows, r => r == null ? throw new ArgumentException("Invalid status entry") : new StateEntry(r.Name, r.Level, r.Cooldown))
            : throw new ArgumentException("Invalid status entries");
        var own = Player is { } p ? new OwnState(p.MatchId, p.Hero, p.Team, p.Clock, p.Alive, p.Health, p.Mana,
            p.X, p.Y, Flat(p.Items), Flat(p.Abilities)) : null;
        return new(SessionId, "", Sequence, Source, SourceWidth, SourceHeight, own, own == null ? null : GsiAgeMs) { IsLegacyStatus = true };
    }

    internal bool BreaksReminderScope(FrameMeta previous) => IsLegacyStatus != previous.IsLegacyStatus ||
        Source != previous.Source || SourceWidth != previous.SourceWidth || SourceHeight != previous.SourceHeight ||
        Player?.MatchId != previous.Player?.MatchId || Player?.Hero != previous.Player?.Hero || Player?.Team != previous.Player?.Team ||
        Player != null && previous.Player != null && Player.Clock < previous.Player.Clock || Player?.Alive == false;
}
public sealed record Ticket(string SessionId, string Id);
public sealed record SessionHello(string SessionId, int Version, string? VideoProtocol = null);
public sealed record ScreenMark(double X, double Y);
public sealed record Reminder(string Id, string SessionId, long FrameSequence, string Kind, string Message,
    ScreenMark? Marker, int SourceWidth, int SourceHeight, int RemainingMs, MinimapLayout? Minimap = null,
    bool ShowSubtitle = true, ScreenMark[]? Route = null, string[]? NextSteps = null,
    // Set by the game machine from its own capture clock; ignored if a coach sends it.
    int? SourceAgeMs = null, int? FarmMinute = null);
/// <summary>
/// One enemy in the game machine's enemy panel, from Plus team view only: the level and items last read while
/// the friend team saw the hero, and how many seconds ago that was. Null = never read. Items are the six main
/// slots in order (null = empty).
/// </summary>
public sealed record EnemyHero(string Hero, int? Level, string?[]? Items, string? Neutral, double? Age);
/// <summary>A candidate, never a sighting. X/Y are fractions across the minimap from its top left, not world coordinates.</summary>
public sealed record WardMark(string Hero, string Kind, double X, double Y, int Rank, double Share, double Age, double Left, bool Rough = false);
/// <summary>Only the selected subsets. Team is the paired participant's team, not the enemy team.
/// Minimap is the confirmed original-client-pixel rectangle for this build. No tracks, snapshots or replays.</summary>
public sealed record EnemyBoard(EnemyHero[] Heroes, WardMark[]? Wards = null, string? MatchId = null, string? Team = null,
    MinimapLayout? Minimap = null, ObservedWard[]? SeenWards = null)
{
    // Filled by the mailbox, never inferred from a scanner. A poll cannot start a new publication lease.
    public string? SessionId { get; init; }
    public long FrameSequence { get; init; }
    public int RemainingMs { get; init; }
    public int HeldMs { get; init; } // Age already charged since FrameSequence; do not charge it twice on the game clock.
}
public sealed record FrameView
{
    private Lazy<byte[]> jpeg = null!;
    private bool hasJpeg;
    public FrameMeta Meta { get; init; }
    public byte[] Jpeg { get => jpeg.Value; init { jpeg = new(() => value); hasJpeg = value.Length != 0; } }
    public long CapturedNoEarlierThan { get; init; }
    public long ReceivedAt { get; init; }
    public VideoPixels? Video { get; init; }
    /// <summary>False for any metadata-only view, including status, ticketed GSI and a terminal receipt.
    /// Checking this never forces a lazy video encode.</summary>
    public bool HasImage => Video != null || hasJpeg;
    public FrameView(FrameMeta Meta, byte[] Jpeg, long CapturedNoEarlierThan, long ReceivedAt)
    { this.Meta = Meta; this.Jpeg = Jpeg; this.CapturedNoEarlierThan = CapturedNoEarlierThan; this.ReceivedAt = ReceivedAt; }
    internal FrameView(FrameMeta meta, VideoPixels pixels, Func<VideoPixels, byte[]> encode, long at, long received)
    {
        Meta = meta; Video = pixels; CapturedNoEarlierThan = at; ReceivedAt = received;
        jpeg = new(() => encode(pixels), LazyThreadSafetyMode.ExecutionAndPublication);
    }
    internal FrameView ReadCopy()
    {
        var original = this;
        return this with { jpeg = new(() => original.Jpeg.ToArray(), LazyThreadSafetyMode.ExecutionAndPublication) };
    }
}
public sealed record HostSnapshot(bool Connected, string? SessionId, long? Sequence, string? Source,
    int? SourceWidth, int? SourceHeight, long Frames, long Bytes, long? FrameAgeMs, OwnState? Player,
    string Status, bool HasReminder);

public static class Jpeg
{
    // Inspect SOF dimensions before any image decoder sees network data.
    public static (int Width, int Height) Dimensions(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12 || data.Length > Wire.MaxFrameBytes || data[0] != 0xff || data[1] != 0xd8 ||
            data[^2] != 0xff || data[^1] != 0xd9) throw new ArgumentException("Invalid JPEG");
        int i = 2;
        while (i + 3 < data.Length)
        {
            if (data[i++] != 0xff) throw new ArgumentException("Invalid JPEG marker");
            while (i < data.Length && data[i] == 0xff) i++;
            if (i >= data.Length) break;
            int marker = data[i++];
            if (marker is 0xd9 or 0xda) break;
            if (marker is >= 0xd0 and <= 0xd7 || marker == 0x01) continue;
            if (i + 2 > data.Length) break;
            int size = data[i] * 256 + data[i + 1];
            if (size < 2 || i + size > data.Length) break;
            if (marker is 0xc0 or 0xc1 or 0xc2)
            {
                if (size < 8) break;
                int height = data[i + 3] * 256 + data[i + 4], width = data[i + 5] * 256 + data[i + 6];
                if (width is < 16 or > 1600 || height is < 16 or > 900) break;
                return (width, height);
            }
            i += size;
        }
        throw new ArgumentException("Unsupported JPEG dimensions");
    }
}

/// <summary>Latest-frame mailbox, not a video archive or farming-policy engine.</summary>
public sealed partial class CoachMailbox
{
    private readonly object gate = new();
    private readonly Func<long> now;
    private string? session;
    private (string Id, long At)? ticket;
    private FrameView? frame;
    private (Reminder Value, long Until, FrameMeta Origin, long OriginAt)? reminder;
    private readonly MinuteFarmWindow minuteWindow = new();
    private long lastMinutePublished;
    private readonly BoardWindow boardWindow = new(keepOrigins: true);
    private (EnemyBoard Value, long At, double Clock)? board;
    private long lastBoardFrame;
    private FrameView? pendingPostgame;
    private readonly Dictionary<long, (FrameMeta Meta, long At)> history = new();
    private readonly Queue<long> historyOrder = new();
    private long sequence, frames, bytes;
    private readonly Dictionary<string, long> videoLeases = new();
    private string? videoOwner;
    public Func<VideoPixels, byte[]>? VideoJpegEncoder { get; set; }
    public Func<DebugRecorder?>? DebugSink { get; set; }
    /// <summary>Icons of the enemy panel by name (PNG), from files on this machine; null when there is none.</summary>
    public Func<string, byte[]?>? Icons { get; set; }
    public CoachMailbox(Func<long>? clock = null) { now = clock ?? (() => Environment.TickCount64); }
    public SessionHello Begin()
    {
        lock (gate)
        {
            ResetSession(keepTerminal: true);
            session = Guid.NewGuid().ToString("N");
            return new(session, Wire.Version, VideoWire.Subprotocol);
        }
    }
    private void Check(string id)
    {
        if (session == null || id != session) throw new InvalidOperationException("Session expired");
    }
    public void End(string id) { lock (gate) { Check(id); ResetSession(keepTerminal: true); } }
    public void Clear() { lock (gate) ResetSession(keepTerminal: false); } // Explicit revocation/disposal erases even the local receipt.
    private void ResetSession(bool keepTerminal)
    {
        session = null; frame = null; reminder = null; board = null; boardWindow.Clear(); lastBoardFrame = 0;
        if (!keepTerminal || pendingPostgame is { } end && !BoardDelivery.Fresh(end.Meta.MatchPhase, end.CapturedNoEarlierThan, now()))
            pendingPostgame = null;
        ticket = null; sequence = 0; history.Clear(); historyOrder.Clear(); videoLeases.Clear(); videoOwner = null;
        endTicket = null; endOrigin = null; admittedEnd = null;
        minuteWindow.Clear(); lastMinutePublished = 0;
    }
    public Ticket Issue(string id)
    {
        lock (gate) { Check(id); ticket = (Guid.NewGuid().ToString("N"), now()); return new(id, ticket.Value.Id); }
    }
    public void Accept(FrameMeta meta, byte[] jpeg)
    {
        var dimensions = Jpeg.Dimensions(jpeg);
        ValidateMeta(meta, dimensions.Width, dimensions.Height);
        lock (gate)
        {
            Check(meta.SessionId);
            if (videoOwner != null) throw new InvalidOperationException("Session uses continuous video");
            if (ticket is not { } t || meta.Ticket != t.Id || now() - t.At > Wire.TicketLifetimeMs || now() < t.At ||
                meta.Sequence <= sequence) throw new InvalidOperationException("Stale capture ticket or sequence");
            ticket = null;
            Store(new(meta, jpeg.ToArray(), t.At, now()), jpeg.Length);
        }
        try { DebugSink?.Invoke()?.Frame("frame-received", meta, jpeg); } catch { /* Legacy diagnostic ingress. */ }
    }
    /// <summary>
    /// Legacy status carries flat own fields, viewport, presence and markerless subtitles only. Neither
    /// a ticket string nor newer participant/terminal/farm fields in this old body can grant authority.
    /// </summary>
    public void AcceptStatus(FrameMeta meta)
    {
        meta = meta.ForLegacyStatus();
        ValidateMeta(meta, meta.SourceWidth, meta.SourceHeight);
        lock (gate)
        {
            Check(meta.SessionId);
            if (videoOwner != null) throw new InvalidOperationException("Session uses continuous video");
            if (meta.Sequence <= sequence) throw new InvalidOperationException("Stale status sequence");
            // A capture/terminal request started before a switch to raw status cannot restore its old lane.
            ticket = endTicket = null; videoLeases.Clear();
            long received = now();
            Store(new(meta, [], received, received), 0);
        }
    }
    internal static void ValidateMeta(FrameMeta meta, int width, int height)
    {
        BoardDelivery.ValidatePhase(meta);
        if (meta.Source is not ("dota2" or "diagnostic") || meta.SourceWidth is < 640 or > 7680 ||
            meta.SourceHeight is < 480 or > 4320 || meta.Sequence < 1 ||
            Math.Abs((double)width / height - (double)meta.SourceWidth / meta.SourceHeight) > .01)
            throw new ArgumentException("Invalid capture metadata");
        if (meta.Player != null)
        {
            GsiState.Validate(meta.Player);
            if (meta.Source != "dota2" || meta.GsiAgeMs is null or < 0 or > 3000)
                throw new ArgumentException("Stale or synthetic GSI");
        }
    }
    public void AttachVideo(string id, string owner)
    {
        lock (gate) { Check(id); if (videoOwner != null) throw new InvalidOperationException("Video already attached"); videoOwner = owner; }
    }
    public void DetachVideo(string id, string owner)
    {
        lock (gate) { if (session == id && videoOwner == owner) ResetSession(keepTerminal: true); }
    }
    public Ticket IssueVideoLease(string id)
    {
        lock (gate) {
            Check(id);
            foreach (var key in videoLeases.Where(x => now() - x.Value > Wire.TicketLifetimeMs).Select(x => x.Key).ToArray()) videoLeases.Remove(key);
            if (videoLeases.Count >= 8) throw new InvalidOperationException("Too many video leases");
            var lease = new Ticket(id, Guid.NewGuid().ToString("N")); videoLeases[lease.Id] = now(); return lease;
        }
    }
    public long ValidateVideo(FrameMeta meta, string owner)
    {
        lock (gate) {
            Check(meta.SessionId);
            if (videoOwner != owner || !videoLeases.TryGetValue(meta.Ticket, out long at) ||
                now() < at || now() - at > Wire.TicketLifetimeMs || meta.Sequence <= sequence)
                throw new InvalidOperationException("Expired/replayed video or session");
            return at;
        }
    }
    public void AcceptVideo(FrameMeta meta, string owner, VideoPixels pixels, int encodedBytes)
    {
        ValidateMeta(meta, pixels.Width, pixels.Height);
        var converter = VideoJpegEncoder ?? throw new InvalidOperationException("Coach image extractor unavailable");
        lock (gate) {
            long at = ValidateVideo(meta, owner);
            Store(new(meta, pixels, converter, at, now()), encodedBytes);
        }
        // No JPEG encoding or disk I/O here. The coach samples independently of video ingress.
    }
    private void Store(FrameView incoming, int length)
    {
        incoming = WithdrawEndedState(incoming);
        var meta = incoming.Meta;
        var previous = frame?.Meta;
        if (previous != null && meta.BreaksReminderScope(previous)) { reminder = null; history.Clear(); historyOrder.Clear(); }
        frame = incoming;
        long instant = now();
        boardWindow.Observe(meta, incoming.CapturedNoEarlierThan, instant);
        if (board is { } b && !boardWindow.Admits(b.Value, instant)) board = null;
        if (meta.IsLegacyStatus) endOrigin = null; // No terminal may borrow a playing origin across this lane.
        else ObservePostgameOrigin(incoming, instant);
        minuteWindow.Observe(meta, incoming.CapturedNoEarlierThan, instant);
        PruneMinute(instant);
        history[meta.Sequence] = (meta, incoming.CapturedNoEarlierThan); historyOrder.Enqueue(meta.Sequence);
        // Sequences only increase: prune from the front in O(1) per frame, even at 120 fps.
        while (historyOrder.TryPeek(out long oldest) && (history.Count > Wire.HistoryLimit ||
               !history.TryGetValue(oldest, out var entry) || now() - entry.At >= Wire.FrameLifetimeMs)) {
            historyOrder.Dequeue(); history.Remove(oldest);
        }
        sequence = meta.Sequence; frames++; bytes += length;
    }
    public FrameView? Latest()
    {
        lock (gate)
        {
            long instant = now();
            if (frame == null || instant - frame.ReceivedAt > Wire.PresenceLifetimeMs) return null;
            // Consumers must never mutate the mailbox-owned JPEG.
            var copy = frame.ReadCopy();
            var player = CurrentPlayer(instant);
            var phase = BoardDelivery.Fresh(copy.Meta.MatchPhase, copy.CapturedNoEarlierThan, instant) ? copy.Meta.MatchPhase : null;
            return player == frame.Meta.Player && phase == copy.Meta.MatchPhase ? copy :
                copy with { Meta = copy.Meta with { Player = player, MatchPhase = phase } };
        }
    }
    /// <summary>Local UI only, not a LAN endpoint. A short authenticated postgame packet can be followed by a menu
    /// packet/normal video detach between refresh ticks. Consume its exact original session/sequence once, still
    /// under its original freshness bound. An unscoped local read also catches a quick reconnect; never rebind it.</summary>
    public FrameView? TakePostgame(string? id = null)
    {
        lock (gate) {
            var pending = pendingPostgame;
            if (id != null && pending?.Meta.SessionId != id) return null;
            pendingPostgame = null;
            return pending != null && BoardDelivery.Fresh(pending.Meta.MatchPhase, pending.CapturedNoEarlierThan, now()) ? pending : null;
        }
    }
    // Video presence can outlive minute GSI freshness. Preserve the legacy projection, and also expose
    // a known incompatible installation for the settings UI, but don't offer stale evidence to a planner.
    private OwnState? CurrentPlayer(long instant) => frame?.Meta.Player is { } p &&
        !minuteWindow.Fresh(instant) && MinuteFarmContext.Eligible(p) ? p with { Farm = null } : frame?.Meta.Player;
    /// <summary>
    /// Publishes advice derived from frame <paramref name="basedOn"/>. The source must still be younger than
    /// <see cref="Wire.FrameLifetimeMs"/>; once published it stays visible for <paramref name="displayMs"/>
    /// (unless death, a new match/team/resolution or disconnection withdraws it first). Minute routes are
    /// additionally clipped to the original and current minute's remainder and withdrawn on loss of GSI continuity.
    /// </summary>
    public void SetReminder(string id, long basedOn, string kind, string text, ScreenMark? mark, MinimapLayout? minimap = null,
        bool showSubtitle = true, int displayMs = Wire.DefaultReminderMs, ScreenMark[]? route = null, string[]? nextSteps = null,
        int? farmMinute = null)
    {
        bool minute = kind == MinuteFarmContext.Kind;
        if (!minute && kind is not ("manual-link-test" or "local-vision-observation" or "experimental-bot-advice" or "visible-farming-advice") || string.IsNullOrWhiteSpace(text) ||
            text.Length > 120 || (mark != null && (!Wire.Finite(mark.X, 0, 1) || !Wire.Finite(mark.Y, 0, 1))) ||
            (kind == "local-vision-observation" && mark != null) || minute != farmMinute.HasValue ||
            (minute ? displayMs is < 1 or > MinuteFarmContext.MaxDisplayMs : displayMs is < 1000 or > Wire.MaxReminderMs) ||
            !Wire.ValidRoute(route, mark) || !Wire.ValidSteps(nextSteps) ||
            (route != null && !minute && kind is not ("visible-farming-advice" or "experimental-bot-advice"))) throw new ArgumentException("Invalid reminder");
        lock (gate)
        {
            Check(id);
            long instant = now();
            if (frame == null || !history.TryGetValue(basedOn, out var source) || instant - frame.ReceivedAt > Wire.PresenceLifetimeMs ||
                instant - source.At is < 0 or >= Wire.FrameLifetimeMs || frame.Meta.Player?.Alive == false ||
                !minute && (frame.Meta.Source == "dota2-gsi" || source.Meta.Source == "dota2-gsi") ||
                (frame.Meta.IsLegacyStatus || source.Meta.IsLegacyStatus) && !Wire.StatusSubtitle(kind, mark, minimap, route, nextSteps))
                throw new InvalidOperationException("Source frame expired; refresh the preview");
            ScreenMark[] points = route ?? (mark == null ? Array.Empty<ScreenMark>() : new[] { mark });
            if (kind == "experimental-bot-advice" &&
                (!CloudCoach.Eligible(frame) || source.Meta.Player is not { MatchId: "0", Alive: true } ||
                 source.Meta.Source != "dota2" || points.Any(p => !Wire.Finite(p.X, 0, .20) || !Wire.Finite(p.Y, .65, 1))))
                throw new InvalidOperationException("Experimental advice is restricted to local bot matches and the supported minimap");
            if (kind == "visible-farming-advice" &&
                (!CloudCoach.Eligible(frame, localBotOnly: false) || source.Meta.Source != "dota2" ||
                 source.Meta.Player is not { Alive: true } || source.Meta.GsiAgeMs is null or < 0 or > 3000 ||
                 points.Length > 0 && (minimap == null || !minimap.IsValidFor(source.Meta.SourceWidth, source.Meta.SourceHeight) ||
                                       points.Any(p => !minimap.Contains(p)))))
                throw new InvalidOperationException("Live advice requires fresh own-player state and calibrated map markers");
            long until = instant + displayMs;
            if (minute) {
                if (!MinuteFarmContext.Shape(farmMinute, mark, route, nextSteps, minimap, source.Meta.SourceWidth, source.Meta.SourceHeight))
                    throw new ArgumentException("Minute routes require a calibrated point for each step");
                if (basedOn <= lastMinutePublished || source.Meta.GsiAgeMs == null ||
                    instant - source.At + source.Meta.GsiAgeMs.Value >= Wire.FrameLifetimeMs ||
                    (until = Math.Min(until, minuteWindow.Deadline(source.Meta, source.At, farmMinute, instant))) <= instant)
                    throw new InvalidOperationException("Minute route origin expired or player context changed");
                lastMinutePublished = basedOn;
            }
            reminder = (new(Guid.NewGuid().ToString("N"), id, basedOn, kind, text.Trim(), mark,
                source.Meta.SourceWidth, source.Meta.SourceHeight, (int)(until - instant), minimap, showSubtitle,
                route?.ToArray(), nextSteps?.Select(s => s.Trim()).ToArray(), FarmMinute: farmMinute), until, source.Meta, source.At);
        }
    }
    private void PruneMinute(long instant)
    {
        if (reminder is not { Value.Kind: MinuteFarmContext.Kind } r) return;
        long until = Math.Min(r.Until, minuteWindow.Deadline(r.Origin, r.OriginAt, r.Value.FarmMinute, instant));
        reminder = until <= instant ? null : (r.Value, until, r.Origin, r.OriginAt);
    }
    public void ClearReminder() { lock (gate) reminder = null; }
    // Editing model credentials/selection must retract AI output, not an independent team-vision
    // subtitle that happened to be published in the meantime, or a manual pairing diagnostic.
    public void ClearModelReminder()
    {
        lock (gate)
            if (reminder is { } r && r.Value.Kind is MinuteFarmContext.Kind or "visible-farming-advice" or "experimental-bot-advice")
                reminder = null;
    }
    /// <summary>Replaces the enemy panel on the game machine; null hides it. Unless renewed it expires after <see cref="Wire.BoardLifetimeMs"/>.</summary>
    public void SetEnemies(EnemyBoard? value)
    {
        lock (gate) {
            if (value is { FrameSequence: > 0 } stamped && stamped.SessionId == session && stamped.FrameSequence < lastBoardFrame) return;
            board = null;
            if (value == null) { lastBoardFrame = frame?.Meta.Sequence ?? 0; return; }
            if (!Wire.ValidBoard(value)) throw new ArgumentException("Invalid enemy board");
            long instant = now();
            if (frame == null || !boardWindow.Matches(value, instant)) return;
            var filtered = boardWindow.Calibrated(value);
            if (BoardDelivery.Empty(filtered) || value.RemainingMs is < 0 or > Wire.BoardLifetimeMs) return;
            // If the projection supplies its source (possibly computed just before a reconnect/withdrawal),
            // verify it, never silently rebind it. Otherwise pin this synchronous publication to the current frame.
            double sourceClock = boardWindow.Clock;
            if ((value.SessionId != null || value.FrameSequence != 0) && !boardWindow.TryOrigin(value, instant, out sourceClock)) return;
            lastBoardFrame = value.FrameSequence != 0 ? value.FrameSequence : frame.Meta.Sequence;
            board = (BoardDelivery.Age(filtered, 0, value.RemainingMs > 0 ? value.RemainingMs : Wire.BoardLifetimeMs) with {
                SessionId = session, FrameSequence = lastBoardFrame, HeldMs = 0 }, instant, sourceClock);
        }
    }
    public EnemyBoard? Enemies(string id)
    {
        lock (gate)
        {
            Check(id);
            long instant = now();
            if (board is not { } b || instant < b.At || instant - b.At >= b.Value.RemainingMs ||
                !boardWindow.Admits(b.Value, instant)) { board = null; return null; }
            long held = BoardDelivery.ElapsedMs(instant - b.At, b.Clock, boardWindow.Clock);
            if (held >= b.Value.RemainingMs) { board = null; return null; }
            var result = BoardDelivery.Age(boardWindow.Calibrated(b.Value), held, b.Value.RemainingMs - (int)held) with { HeldMs = (int)held };
            return BoardDelivery.Empty(result) ? null : result;
        }
    }
    public byte[]? Icon(string id, string key)
    {
        if (!Wire.ValidIconKey(key)) throw new ArgumentException("Invalid icon name");
        bool OnBoard() => BoardDelivery.HasIcon(Enemies(id), key);
        // Existence of a file in a scanner's icon directory is not a separate query for enemy items.
        if (!OnBoard()) return null;
        return Icons?.Invoke(key) is { } data && Wire.IsIcon(data) && OnBoard() ? data : null;
    }
    public Reminder? Poll(string id)
    {
        lock (gate)
        {
            Check(id);
            long instant = now();
            PruneMinute(instant);
            if (frame == null || instant - frame.ReceivedAt > Wire.PresenceLifetimeMs ||
                reminder is not { } r || instant >= r.Until) { reminder = null; return null; }
            return r.Value with { RemainingMs = (int)(r.Until - instant), Route = r.Value.Route?.ToArray(),
                NextSteps = r.Value.NextSteps?.ToArray() };
        }
    }
    public HostSnapshot Snapshot()
    {
        lock (gate)
        {
            long instant = now();
            PruneMinute(instant);
            bool fresh = frame != null && instant - frame.ReceivedAt <= Wire.PresenceLifetimeMs;
            return new(fresh, session, frame?.Meta.Sequence, frame?.Meta.Source, frame?.Meta.SourceWidth,
                frame?.Meta.SourceHeight, frames, bytes, frame == null ? null : instant - frame.ReceivedAt,
                fresh ? CurrentPlayer(instant) : null, fresh ? "正在接收；自动教练状态见模型面板" : "等待新画面；提醒已失效",
                fresh && reminder != null && instant < reminder.Value.Until);
        }
    }
}
