using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;
using System.Net.WebSockets;

namespace DotaCoach.TwoPc;

/// <summary>Game machine: TLS transport only. No model client, API keys, game orders or inference.</summary>
public sealed partial class GameLink : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<long> now;
    private readonly Dictionary<long, (long At, FrameMeta Meta)> captures = new();
    private readonly Queue<long> captureOrder = new();
    private readonly MinuteFarmWindow minuteWindow = new();
    private (Reminder Plan, FrameMeta Origin, long At, long Until)? activeMinute;
    private long lastMinuteAccepted;
    private FrameMeta? currentCapture;
    private readonly object gate = new();
    private readonly Pairing pairing;
    private ClientWebSocket? video;
    private string? videoProtocol;
    private SemaphoreSlim? window;
    private readonly Queue<long> unacknowledged = new();
    private Exception? ackFailure;
    // Longer history is metadata only. It must not extend ordinary reminders or the initial origin-age check.
    private const int LegacyRetentionMs = Wire.FrameLifetimeMs + Wire.MaxReminderMs;
    private const int CaptureRetentionMs = MinuteFarmContext.MaxDisplayMs;
    public string? SessionId { get; private set; }
    public GameLink(Pairing pairing, Func<long>? clock = null)
    {
        pairing = Pairing.Decode(pairing.Encode());
        this.pairing = pairing;
        now = clock ?? (() => Environment.TickCount64);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert != null && Wire.EqualSecret(cert.GetCertHashString(HashAlgorithmName.SHA256), pairing.Fingerprint.ToUpperInvariant());
        http = new(handler) { BaseAddress = new Uri(pairing.Address), Timeout = TimeSpan.FromSeconds(4), MaxResponseContentBufferSize = 65536 };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Token);
    }
    private HttpRequestMessage Request(HttpMethod method, string path)
    {
        var request = new HttpRequestMessage(method, path);
        if (SessionId != null) request.Headers.Add("X-Session", SessionId);
        return request;
    }
    private static async Task<T> Json<T>(HttpResponseMessage response, CancellationToken cancellation)
    {
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(
            $"教练连接被拒绝（{(int)response.StatusCode}）；请重新配对。", null, response.StatusCode);
        return JsonSerializer.Deserialize<T>(await Body(response, 65536, cancellation), Wire.Json) ?? throw new InvalidOperationException("Invalid response");
    }
    private static async Task<byte[]> Body(HttpResponseMessage response, int limit, CancellationToken cancellation)
    {
        if (response.Content.Headers.ContentLength > limit) throw new InvalidOperationException("Unexpected response size");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream();
        var bytes = new byte[4096]; int count;
        while ((count = await input.ReadAsync(bytes, cancellation)) > 0)
        {
            if (output.Length + count > limit) throw new InvalidOperationException("Unexpected response size");
            output.Write(bytes, 0, count);
        }
        return output.ToArray();
    }
    public async Task Connect(CancellationToken cancellation)
    {
        DropPostgame();
        using var request = Request(HttpMethod.Post, "/v1/session");
        using var response = await http.SendAsync(request, cancellation);
        var hello = await Json<SessionHello>(response, cancellation);
        if (hello.Version != Wire.Version || !Guid.TryParseExact(hello.SessionId, "N", out _))
            throw new InvalidOperationException("Protocol version mismatch");
        SessionId = hello.SessionId; videoProtocol = hello.VideoProtocol; ClearCaptures();
    }
    private void ClearCaptures() { lock (gate) { captures.Clear(); captureOrder.Clear(); currentCapture = null;
        minuteWindow.Clear(); activeMinute = null; lastMinuteAccepted = 0; } }
    // Ticket round-trip precedes capture: both sides can bound frame age without synchronised wall clocks.
    public async Task<Ticket> GetTicket(CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, "/v1/ticket");
        using var response = await http.SendAsync(request, cancellation);
        return await Json<Ticket>(response, cancellation);
    }
    public async Task<Ticket> GetVideoTicket(CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, "/v2/video-ticket");
        using var response = await http.SendAsync(request, cancellation);
        return await Json<Ticket>(response, cancellation);
    }
    public async Task OpenVideo(CancellationToken cancellation)
    {
        if (SessionId == null) throw new InvalidOperationException("Connect before video");
        if (videoProtocol != VideoWire.Subprotocol) throw new VideoVersionException();
        video?.Dispose(); video = new ClientWebSocket();
        video.Options.Proxy = null;
        video.Options.AddSubProtocol(VideoWire.Subprotocol);
        video.Options.SetRequestHeader("Authorization", "Bearer " + pairing.Token);
        video.Options.SetRequestHeader("X-Session", SessionId);
        video.Options.RemoteCertificateValidationCallback = (_, cert, _, _) => cert != null &&
            Wire.EqualSecret(cert.GetCertHashString(HashAlgorithmName.SHA256), pairing.Fingerprint.ToUpperInvariant());
        var address = new UriBuilder(pairing.Address) { Scheme = "wss", Path = "/v2/video" };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(4000);
        await video.ConnectAsync(address.Uri, timeout.Token);
        var slots = new SemaphoreSlim(VideoWire.MaxInFlight);
        lock (gate) { unacknowledged.Clear(); ackFailure = null; window = slots; }
        _ = Acknowledgements(video, slots);
    }
    /// <summary>
    /// Sends one access unit. Up to <see cref="VideoWire.MaxInFlight"/> may await the coach's decode ACK, so
    /// network round trips no longer cap the frame rate; a stalled coach still stops the stream within 1.5 s.
    /// </summary>
    public async Task SendVideo(VideoHeader header, byte[] h264, long captureStarted, CancellationToken cancellation)
    {
        var socket = video; var slots = window;
        if (socket == null || slots == null || header.Frame.SessionId != SessionId || now() - captureStarted > 1000)
            throw new InvalidOperationException("Stalled video capture");
        lock (gate) if (ackFailure != null) throw new InvalidOperationException("Video acknowledgement failed", ackFailure);
        var packet = VideoWire.Pack(header, h264);
        if (!await slots.WaitAsync(VideoWire.AckTimeoutMs, cancellation))
            throw new InvalidOperationException("Video acknowledgement timeout");
        Remember(header.Frame, captureStarted);
        lock (gate) unacknowledged.Enqueue(header.Frame.Sequence);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(VideoWire.AckTimeoutMs);
        await socket.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
    }
    private async Task Acknowledgements(ClientWebSocket socket, SemaphoreSlim slots)
    {
        try {
            while (true) {
                byte[] ack = await VideoWire.Receive(socket, 8, CancellationToken.None);
                lock (gate) {
                    if (ack.Length != 8 || !unacknowledged.TryDequeue(out long expected) ||
                        BinaryPrimitives.ReadInt64LittleEndian(ack) != expected)
                        throw new InvalidOperationException("Video acknowledgement mismatch");
                }
                slots.Release();
            }
        } catch (Exception e) {
            lock (gate) if (window == slots) { ackFailure = e; minuteWindow.Clear(); activeMinute = null; }
            socket.Abort(); // Any pending send fails promptly instead of waiting for its timeout.
        }
    }
    public void AbortVideo() { DropPostgame(); lock (gate) { minuteWindow.Clear(); activeMinute = null; } video?.Abort(); }
    private void Remember(FrameMeta meta, long at)
    {
        lock (gate) {
            // The wire names for v0.4 status and a real image overlap. Don't keep an origin across
            // that boundary, even if a stale/older coach returns it after switching back to video.
            if (currentCapture != null && (currentCapture.IsLegacyStatus || meta.IsLegacyStatus) && meta.BreaksReminderScope(currentCapture)) {
                captures.Clear(); captureOrder.Clear();
            }
            currentCapture = meta;
            minuteWindow.Observe(meta, at, now());
            PruneMinute(now());
            captures[meta.Sequence] = (at, meta); captureOrder.Enqueue(meta.Sequence);
            // Sequences only increase, so the queue front is always the oldest capture.
            while (captureOrder.TryPeek(out long oldest) && (captures.Count > Wire.HistoryLimit ||
                   !captures.TryGetValue(oldest, out var entry) || now() - entry.At > CaptureRetentionMs)) {
                captureOrder.Dequeue(); captures.Remove(oldest);
            }
        }
    }
    public async Task Upload(FrameMeta meta, byte[] jpeg, long captureStarted, CancellationToken cancellation)
    {
        if (meta.SessionId != SessionId || now() - captureStarted > Wire.TicketLifetimeMs) throw new InvalidOperationException("Old capture");
        Jpeg.Dimensions(jpeg);
        using var request = Request(HttpMethod.Post, "/v1/frame");
        request.Headers.Add("X-Frame-Meta", Convert.ToBase64String(JsonSerializer.SerializeToUtf8Bytes(meta, Wire.Json)));
        request.Content = new ByteArrayContent(jpeg); request.Content.Headers.ContentType = new("image/jpeg");
        Remember(meta, captureStarted);
        using var response = await http.SendAsync(request, cancellation);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(
            "画面被拒绝或会话已失效，请重新连接。", null, response.StatusCode);
    }
    /// <summary>v0.4 compatibility: flat own fields and window size only; no pixels, witness, farm telemetry or board authority.</summary>
    public async Task SendStatus(FrameMeta meta, CancellationToken cancellation)
    {
        if (SessionId == null || meta.SessionId != SessionId) throw new InvalidOperationException("Old session");
        meta = meta.ForLegacyStatus(); // Strip new fields BEFORE serialization as well as before remembering a local origin.
        CoachMailbox.ValidateMeta(meta, meta.SourceWidth, meta.SourceHeight);
        using var request = Request(HttpMethod.Post, "/v1/status");
        request.Content = new ByteArrayContent(JsonSerializer.SerializeToUtf8Bytes(meta, Wire.Json));
        request.Content.Headers.ContentType = new("application/json");
        Remember(meta, now());
        using var response = await http.SendAsync(request, cancellation);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) throw new CoachVersionException();
        if (!response.IsSuccessStatusCode) throw new HttpRequestException(
            "状态被拒绝或会话已失效，请重新连接。", null, response.StatusCode);
    }
    public async Task<Reminder?> Poll(CancellationToken cancellation)
    {
        long started = now();
        using var request = Request(HttpMethod.Get, "/v1/reminder");
        using var response = await http.SendAsync(request, cancellation);
        var result = await Json<ReminderEnvelope>(response, cancellation);
        var r = result.Reminder;
        lock (gate)
        {
            long instant = now(), elapsed = instant - started;
            if (r?.Kind != MinuteFarmContext.Kind) activeMinute = null;
            if (r == null) return null;
            bool minute = r.Kind == MinuteFarmContext.Kind;
            long statusUntil = long.MaxValue;
            if (currentCapture is { IsLegacyStatus: true } status) {
                if (!Wire.StatusSubtitle(r.Kind, r.Marker, r.Minimap, r.Route, r.NextSteps) || status.Player?.Alive == false ||
                    !captures.TryGetValue(status.Sequence, out var latest) || instant < latest.At ||
                    (statusUntil = latest.At + Wire.PresenceLifetimeMs) <= instant) return null;
            }
            if (r.SessionId != SessionId || !captures.TryGetValue(r.FrameSequence, out var captured) ||
                captured.Meta.SessionId != r.SessionId || instant < captured.At || elapsed < 0 ||
                instant - captured.At >= (minute ? CaptureRetentionMs : LegacyRetentionMs) || r.RemainingMs <= elapsed ||
                r.RemainingMs > (minute ? MinuteFarmContext.MaxDisplayMs : Wire.MaxReminderMs) ||
                string.IsNullOrWhiteSpace(r.Message) || r.Message.Length > 120 || minute != r.FarmMinute.HasValue ||
                !minute && (captured.Meta.Source == "dota2-gsi" || currentCapture?.Source == "dota2-gsi") &&
                    !Wire.StatusSubtitle(r.Kind, r.Marker, r.Minimap, r.Route, r.NextSteps) ||
                captured.Meta.IsLegacyStatus != (currentCapture?.IsLegacyStatus == true) ||
                !Wire.ValidRoute(r.Route, r.Marker) || !Wire.ValidSteps(r.NextSteps) ||
                (r.Route != null && !minute && r.Kind is not ("visible-farming-advice" or "experimental-bot-advice")) ||
                (r.Kind == "visible-farming-advice" && r.Route != null && (r.Minimap == null || r.Route.Any(p => !r.Minimap.Contains(p)))) ||
                r.SourceWidth != captured.Meta.SourceWidth || r.SourceHeight != captured.Meta.SourceHeight ||
                !minute && r.Kind is not ("manual-link-test" or "local-vision-observation" or "experimental-bot-advice" or "visible-farming-advice") ||
                (r.Kind == "experimental-bot-advice" && (captured.Meta.Source != "dota2" ||
                    captured.Meta.Player is not { MatchId: "0", Alive: true } ||
                    (r.Route ?? (r.Marker == null ? [] : [r.Marker])).Any(p => !Wire.Finite(p.X, 0, .20) || !Wire.Finite(p.Y, .65, 1)))) ||
                (r.Kind == "visible-farming-advice" && (captured.Meta.Source != "dota2" ||
                    captured.Meta.Player is not { Alive: true } || captured.Meta.GsiAgeMs is null or < 0 or > 3000 ||
                    r.Marker != null && (r.Minimap == null || !r.Minimap.IsValidFor(r.SourceWidth, r.SourceHeight) ||
                                        !r.Minimap.Contains(r.Marker)))) ||
                (r.Marker != null && (r.Kind == "local-vision-observation" ||
                    !Wire.Finite(r.Marker.X, 0, 1) || !Wire.Finite(r.Marker.Y, 0, 1)))) { activeMinute = null; return null; }
            long until = started + r.RemainingMs; // Charge the entire request round trip; no synchronized wall clocks.
            if (minute) {
                PruneMinute(instant);
                if (string.IsNullOrWhiteSpace(r.Id) || r.Id.Length > 64 ||
                    !MinuteFarmContext.Shape(r.FarmMinute, r.Marker, r.Route, r.NextSteps, r.Minimap, r.SourceWidth, r.SourceHeight) ||
                    ackFailure != null ||
                    (until = Math.Min(until, minuteWindow.Deadline(captured.Meta, captured.At, r.FarmMinute, instant))) <= instant) {
                    activeMinute = null; return null;
                }
                if (activeMinute is { } held && r.Id == held.Plan.Id && r.FrameSequence == held.Origin.Sequence) {
                    // An old origin may keep displaying the admitted plan, not supply a replacement under its ID.
                    if (r.Message != held.Plan.Message || r.Minimap != held.Plan.Minimap || r.ShowSubtitle != held.Plan.ShowSubtitle ||
                        !r.Route!.SequenceEqual(held.Plan.Route!) || !r.NextSteps!.SequenceEqual(held.Plan.NextSteps!)) {
                        activeMinute = null; return null;
                    }
                    until = Math.Min(until, held.Until);
                } else if (r.FrameSequence <= lastMinuteAccepted || captured.Meta.GsiAgeMs == null ||
                    instant - captured.At + captured.Meta.GsiAgeMs.Value >= Wire.FrameLifetimeMs) { activeMinute = null; return null; }
                lastMinuteAccepted = r.FrameSequence;
                activeMinute = (r with { Route = r.Route!.ToArray(), NextSteps = r.NextSteps!.ToArray() }, captured.Meta, captured.At, until);
            } else until = Math.Min(until, Math.Min(statusUntil, captured.At + LegacyRetentionMs));
            // Age measured on THIS machine's clock from the original capture; never taken from the coach.
            return r with { RemainingMs = (int)(until - instant), SourceAgeMs = (int)(instant - captured.At) };
        }
    }
    private void PruneMinute(long instant)
    {
        if (activeMinute is not { } r) return;
        long until = Math.Min(r.Until, minuteWindow.Deadline(r.Origin, r.At, r.Plan.FarmMinute, instant));
        activeMinute = until <= instant ? null : (r.Plan, r.Origin, r.At, until);
    }
    /// <summary>Recheck on every local render, not just when a network request or capture finishes.
    /// own/age must come directly from this machine's current receiver, never from a coach frame.</summary>
    public bool CanRenderMinuteRoute(Reminder r, OwnState? own, int? gsiAgeMs)
    {
        lock (gate) {
            long instant = now();
            PruneMinute(instant);
            if (activeMinute is not { } held || r.Kind != MinuteFarmContext.Kind || r.Id != held.Plan.Id ||
                r.FrameSequence != held.Origin.Sequence || r.FarmMinute != held.Plan.FarmMinute || r.SessionId != SessionId) return false;
            if (ackFailure != null || gsiAgeMs is null or < 0 or >= MinuteFarmContext.FreshMs || !MinuteFarmContext.Eligible(own) ||
                !MinuteFarmContext.Continues(held.Origin.Player!, own!) ||
                !MinuteFarmContext.Continues(minuteWindow.Current!.Player!, own!)) { activeMinute = null; return false; }
            long until = Math.Min(held.Until, instant - gsiAgeMs.Value + MinuteFarmContext.RemainderMs(own!));
            if (until <= instant) { activeMinute = null; return false; }
            activeMinute = (held.Plan, held.Origin, held.At, until);
            return true;
        }
    }
    /// <summary>The coach's current enemy panel for this session, or null when it has none (or is a coach without one).
    /// The coach keeps its last-known rows; this client shows each reply until the next one replaces it.</summary>
    public async Task<EnemyBoard?> Enemies(CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Get, "/v1/enemies");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(http.Timeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        EnemyBoard? board;
        try { board = BoardDelivery.Read(await Body(response, 65536, timeout.Token)); }
        catch (InvalidOperationException) { return null; } // Oversized: ignore this reply.
        return board != null && board.SessionId == SessionId ? board : null;
    }
    /// <summary>One icon of the enemy panel as PNG bytes, or null when the coach has none (it serves only icons on its panel).</summary>
    public async Task<byte[]?> Icon(string key, CancellationToken cancellation)
    {
        if (!Wire.ValidIconKey(key)) return null;
        using var request = Request(HttpMethod.Get, "/v1/icon/" + key);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(http.Timeout);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, timeout.Token);
        if (!response.IsSuccessStatusCode) return null;
        var data = await Body(response, Wire.MaxIconBytes, timeout.Token);
        return Wire.IsIcon(data) ? data : null;
    }
    public async Task End(CancellationToken cancellation)
    {
        CloseVideo();
        if (SessionId == null) return;
        try
        {
            using var request = Request(HttpMethod.Delete, "/v1/session");
            using var response = await http.SendAsync(request, cancellation);
        }
        finally { SessionId = null; ClearCaptures(); }
    }
    private void CloseVideo()
    {
        DropPostgame();
        lock (gate) { window = null; unacknowledged.Clear(); minuteWindow.Clear(); activeMinute = null; ackFailure = null; }
        video?.Abort(); video?.Dispose(); video = null;
    }
    public void Dispose() { CloseVideo(); http.Dispose(); ClearCaptures(); }
    private sealed record ReminderEnvelope(Reminder? Reminder);
}
