using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace DotaCoach.TwoPc;

public static class Wire
{
    public const int Version = 1;
    public const int MaxFrameBytes = 1_048_576;
    public const int FrameLifetimeMs = 12_000;
    public const int TicketLifetimeMs = 3_000;
    public const int PresenceLifetimeMs = 4_000;
    // Video runs at 30fps. Retain the full advice lifetime, not the old 32-image (~1s) cap.
    public const int HistoryLimit = 512;
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

public sealed record StateEntry(string Name, int? Level, double? Cooldown);
public sealed record OwnState(string MatchId, string Hero, string Team, double Clock, bool Alive,
    double Health, double Mana, double? X, double? Y, StateEntry[] Items, StateEntry[] Abilities);
public sealed record FrameMeta(string SessionId, string Ticket, long Sequence, string Source,
    int SourceWidth, int SourceHeight, OwnState? Player, int? GsiAgeMs);
public sealed record Ticket(string SessionId, string Id);
public sealed record SessionHello(string SessionId, int Version, string? VideoProtocol = null);
public sealed record ScreenMark(double X, double Y);
public sealed record Reminder(string Id, string SessionId, long FrameSequence, string Kind, string Message,
    ScreenMark? Marker, int SourceWidth, int SourceHeight, int RemainingMs, MinimapLayout? Minimap = null,
    bool ShowSubtitle = true);
public sealed record FrameView
{
    private Lazy<byte[]> jpeg = null!;
    public FrameMeta Meta { get; init; }
    public byte[] Jpeg { get => jpeg.Value; init => jpeg = new(() => value); }
    public long CapturedNoEarlierThan { get; init; }
    public long ReceivedAt { get; init; }
    public VideoPixels? Video { get; init; }
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
public sealed class CoachMailbox
{
    private readonly object gate = new();
    private readonly Func<long> now;
    private string? session;
    private (string Id, long At)? ticket;
    private FrameView? frame;
    private (Reminder Value, long Until)? reminder;
    private readonly Dictionary<long, (FrameMeta Meta, long At)> history = new();
    private long sequence, frames, bytes;
    private readonly Dictionary<string, long> videoLeases = new();
    private string? videoOwner;
    public Func<VideoPixels, byte[]>? VideoJpegEncoder { get; set; }
    public Func<DebugRecorder?>? DebugSink { get; set; }
    public CoachMailbox(Func<long>? clock = null) { now = clock ?? (() => Environment.TickCount64); }
    public SessionHello Begin()
    {
        lock (gate)
        {
            Clear();
            session = Guid.NewGuid().ToString("N");
            return new(session, Wire.Version, VideoWire.Subprotocol);
        }
    }
    private void Check(string id)
    {
        if (session == null || id != session) throw new InvalidOperationException("Session expired");
    }
    public void End(string id) { lock (gate) { Check(id); Clear(); } }
    public void Clear() { lock (gate) { session = null; frame = null; reminder = null; ticket = null; sequence = 0; history.Clear(); videoLeases.Clear(); videoOwner = null; } }
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
    internal static void ValidateMeta(FrameMeta meta, int width, int height)
    {
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
        lock (gate) { if (session == id && videoOwner == owner) Clear(); }
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
        var meta = incoming.Meta;
        var previous = frame?.Meta;
        if (previous != null && (previous.Source != meta.Source || previous.SourceWidth != meta.SourceWidth ||
            previous.SourceHeight != meta.SourceHeight || previous.Player?.MatchId != meta.Player?.MatchId ||
            previous.Player?.Hero != meta.Player?.Hero || previous.Player?.Team != meta.Player?.Team ||
            (previous.Player != null && meta.Player != null && meta.Player.Clock < previous.Player.Clock) ||
            meta.Player?.Alive == false)) { reminder = null; history.Clear(); }
        frame = incoming;
        history[meta.Sequence] = (meta, incoming.CapturedNoEarlierThan);
        foreach (long key in history.Where(h => now() - h.Value.At >= Wire.FrameLifetimeMs).Select(h => h.Key).ToArray()) history.Remove(key);
        while (history.Count > Wire.HistoryLimit) history.Remove(history.Keys.Min());
        sequence = meta.Sequence; frames++; bytes += length;
    }
    public FrameView? Latest()
    {
        lock (gate)
        {
            if (frame == null || now() - frame.ReceivedAt > Wire.PresenceLifetimeMs) return null;
            // Consumers must never mutate the mailbox-owned JPEG.
            return frame.ReadCopy();
        }
    }
    public void SetReminder(string id, long basedOn, string kind, string text, ScreenMark? mark, MinimapLayout? minimap = null,
        bool showSubtitle = true)
    {
        if (kind is not ("manual-link-test" or "local-vision-observation" or "experimental-bot-advice" or "visible-farming-advice") || string.IsNullOrWhiteSpace(text) ||
            text.Length > 120 || (mark != null && (!Wire.Finite(mark.X, 0, 1) || !Wire.Finite(mark.Y, 0, 1))) ||
            (kind == "local-vision-observation" && mark != null)) throw new ArgumentException("Invalid reminder");
        lock (gate)
        {
            Check(id);
            if (frame == null || !history.TryGetValue(basedOn, out var source) || now() - frame.ReceivedAt > Wire.PresenceLifetimeMs ||
                now() - source.At >= Wire.FrameLifetimeMs || frame.Meta.Player?.Alive == false)
                throw new InvalidOperationException("Source frame expired; refresh the preview");
            if (kind == "experimental-bot-advice" &&
                (!CloudCoach.Eligible(frame) || source.Meta.Player is not { MatchId: "0", Alive: true } ||
                 source.Meta.Source != "dota2" || mark is { } p && (!Wire.Finite(p.X, 0, .20) || !Wire.Finite(p.Y, .65, 1))))
                throw new InvalidOperationException("Experimental advice is restricted to local bot matches and the supported minimap");
            if (kind == "visible-farming-advice" &&
                (!CloudCoach.Eligible(frame, localBotOnly: false) || source.Meta.Source != "dota2" ||
                 source.Meta.Player is not { Alive: true } || source.Meta.GsiAgeMs is null or < 0 or > 3000 ||
                 mark != null && (minimap == null || !minimap.IsValidFor(source.Meta.SourceWidth, source.Meta.SourceHeight) ||
                                  !minimap.Contains(mark))))
                throw new InvalidOperationException("Live advice requires fresh own-player state and calibrated map markers");
            long until = Math.Min(source.At + Wire.FrameLifetimeMs, now() + 8000);
            reminder = (new(Guid.NewGuid().ToString("N"), id, basedOn, kind, text.Trim(), mark,
                source.Meta.SourceWidth, source.Meta.SourceHeight, (int)(until - now()), minimap, showSubtitle), until);
        }
    }
    public void ClearReminder() { lock (gate) reminder = null; }
    public Reminder? Poll(string id)
    {
        lock (gate)
        {
            Check(id);
            if (frame == null || now() - frame.ReceivedAt > Wire.PresenceLifetimeMs ||
                reminder is not { } r || now() >= r.Until) { reminder = null; return null; }
            return r.Value with { RemainingMs = (int)(r.Until - now()) };
        }
    }
    public HostSnapshot Snapshot()
    {
        lock (gate)
        {
            bool fresh = frame != null && now() - frame.ReceivedAt <= Wire.PresenceLifetimeMs;
            return new(fresh, session, frame?.Meta.Sequence, frame?.Meta.Source, frame?.Meta.SourceWidth,
                frame?.Meta.SourceHeight, frames, bytes, frame == null ? null : now() - frame.ReceivedAt,
                fresh ? frame?.Meta.Player : null, fresh ? "正在接收；自动教练状态见模型面板" : "等待新画面；提醒已失效",
                fresh && reminder != null && now() < reminder.Value.Until);
        }
    }
}
