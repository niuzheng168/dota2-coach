using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Buffers.Binary;
using System.Net.WebSockets;

namespace DotaCoach.TwoPc;

/// <summary>Game machine: TLS transport only. No model client, API keys, game orders or inference.</summary>
public sealed class GameLink : IDisposable
{
    private readonly HttpClient http;
    private readonly Func<long> now;
    private readonly Dictionary<long, (long At, FrameMeta Meta)> captures = new();
    private readonly object gate = new();
    private readonly Pairing pairing;
    private ClientWebSocket? video;
    private string? videoProtocol;
    public string? SessionId { get; private set; }
    public GameLink(Pairing pairing, Func<long>? clock = null)
    {
        pairing = Pairing.Decode(pairing.Encode());
        this.pairing = pairing;
        now = clock ?? (() => Environment.TickCount64);
        var handler = new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false };
        handler.ServerCertificateCustomValidationCallback = (_, cert, _, _) =>
            cert != null && Wire.EqualSecret(cert.GetCertHashString(HashAlgorithmName.SHA256), pairing.Fingerprint.ToUpperInvariant());
        http = new(handler) { BaseAddress = new Uri(pairing.Address), Timeout = TimeSpan.FromSeconds(4) };
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
        if (response.Content.Headers.ContentLength > 65536) throw new InvalidOperationException("Unexpected response size");
        await using var input = await response.Content.ReadAsStreamAsync(cancellation);
        using var output = new MemoryStream();
        var bytes = new byte[4096]; int count;
        while ((count = await input.ReadAsync(bytes, cancellation)) > 0)
        {
            if (output.Length + count > 65536) throw new InvalidOperationException("Unexpected response size");
            output.Write(bytes, 0, count);
        }
        return JsonSerializer.Deserialize<T>(output.ToArray(), Wire.Json) ?? throw new InvalidOperationException("Invalid response");
    }
    public async Task Connect(CancellationToken cancellation)
    {
        using var request = Request(HttpMethod.Post, "/v1/session");
        using var response = await http.SendAsync(request, cancellation);
        var hello = await Json<SessionHello>(response, cancellation);
        if (hello.Version != Wire.Version || !Guid.TryParseExact(hello.SessionId, "N", out _))
            throw new InvalidOperationException("Protocol version mismatch");
        SessionId = hello.SessionId; videoProtocol = hello.VideoProtocol; lock (gate) captures.Clear();
    }
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
    }
    public async Task SendVideo(VideoHeader header, byte[] h264, long captureStarted, CancellationToken cancellation)
    {
        if (video == null || header.Frame.SessionId != SessionId || now() - captureStarted > 1000)
            throw new InvalidOperationException("Stalled video capture");
        var packet = VideoWire.Pack(header, h264);
        Remember(header.Frame, captureStarted);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(1500);
        await video.SendAsync(packet.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
        byte[] ack = await VideoWire.Receive(video, 8, timeout.Token);
        if (ack.Length != 8 || BinaryPrimitives.ReadInt64LittleEndian(ack) != header.Frame.Sequence)
            throw new InvalidOperationException("Video acknowledgement mismatch");
    }
    public void AbortVideo() { video?.Abort(); }
    private void Remember(FrameMeta meta, long at)
    {
        lock (gate) {
            captures[meta.Sequence] = (at, meta);
            foreach (var key in captures.Where(x => now() - x.Value.At > Wire.FrameLifetimeMs).Select(x => x.Key).ToArray()) captures.Remove(key);
            while (captures.Count > Wire.HistoryLimit) captures.Remove(captures.Keys.Min());
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
    public async Task<Reminder?> Poll(CancellationToken cancellation)
    {
        long started = now();
        using var request = Request(HttpMethod.Get, "/v1/reminder");
        using var response = await http.SendAsync(request, cancellation);
        var result = await Json<ReminderEnvelope>(response, cancellation);
        var r = result.Reminder;
        if (r == null) return null;
        lock (gate)
        {
            if (r.SessionId != SessionId || !captures.TryGetValue(r.FrameSequence, out var captured) ||
                now() - captured.At >= Wire.FrameLifetimeMs || r.RemainingMs <= now() - started ||
                r.RemainingMs > Wire.FrameLifetimeMs || r.Message.Length is < 1 or > 120 ||
                r.SourceWidth != captured.Meta.SourceWidth || r.SourceHeight != captured.Meta.SourceHeight ||
                r.Kind is not ("manual-link-test" or "local-vision-observation" or "experimental-bot-advice" or "visible-farming-advice") ||
                (r.Kind == "experimental-bot-advice" && (captured.Meta.Source != "dota2" ||
                    captured.Meta.Player is not { MatchId: "0", Alive: true } ||
                    r.Marker is { } p && (!Wire.Finite(p.X, 0, .20) || !Wire.Finite(p.Y, .65, 1)))) ||
                (r.Kind == "visible-farming-advice" && (captured.Meta.Source != "dota2" ||
                    captured.Meta.Player is not { Alive: true } || captured.Meta.GsiAgeMs is null or < 0 or > 3000 ||
                    r.Marker != null && (r.Minimap == null || !r.Minimap.IsValidFor(r.SourceWidth, r.SourceHeight) ||
                                        !r.Minimap.Contains(r.Marker)))) ||
                (r.Marker != null && (r.Kind == "local-vision-observation" ||
                    !Wire.Finite(r.Marker.X, 0, 1) || !Wire.Finite(r.Marker.Y, 0, 1)))) return null;
            int remaining = (int)Math.Min(r.RemainingMs - (now() - started), Wire.FrameLifetimeMs - (now() - captured.At));
            return r with { RemainingMs = remaining };
        }
    }
    public async Task End(CancellationToken cancellation)
    {
        video?.Abort(); video?.Dispose(); video = null;
        if (SessionId == null) return;
        try
        {
            using var request = Request(HttpMethod.Delete, "/v1/session");
            using var response = await http.SendAsync(request, cancellation);
        }
        finally { SessionId = null; lock (gate) captures.Clear(); }
    }
    public void Dispose() { video?.Abort(); video?.Dispose(); http.Dispose(); lock (gate) captures.Clear(); }
    private sealed record ReminderEnvelope(Reminder? Reminder);
}
