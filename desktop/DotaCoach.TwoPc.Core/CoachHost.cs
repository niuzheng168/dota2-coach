using System.Net;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotaCoach.TwoPc;

/// <summary>LAN ingress only. Operator controls stay in the local native UI, not HTTP admin routes.</summary>
public sealed class CoachHost : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly X509Certificate2 certificate;
    private string token = Wire.Secret();
    public CoachMailbox Mailbox { get; } = new();
    public string Fingerprint { get; }
    public int Port { get; private set; }
    public IPAddress ListeningAddress { get; private set; } = IPAddress.None;

    private CoachHost(IPAddress bind, int port)
    {
        using var rsa = RSA.Create(2048);
        var request = new CertificateRequest("CN=DotaCoach ephemeral paired LAN", rsa, HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        request.CertificateExtensions.Add(new X509BasicConstraintsExtension(false, false, 0, true));
        request.CertificateExtensions.Add(new X509KeyUsageExtension(X509KeyUsageFlags.DigitalSignature, true));
        request.CertificateExtensions.Add(new X509EnhancedKeyUsageExtension(
            new OidCollection { new("1.3.6.1.5.5.7.3.1") }, true));
        using var signed = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
        // Windows Schannel needs a user-key container. Do NOT use PersistKeySet or install/trust
        // this certificate: the import's temporary key is released when the certificate is disposed.
        certificate = X509CertificateLoader.LoadPkcs12(signed.Export(X509ContentType.Pfx), null,
            X509KeyStorageFlags.UserKeySet);
        Fingerprint = certificate.GetCertHashString(HashAlgorithmName.SHA256);
        var builder = WebApplication.CreateSlimBuilder();
        builder.Logging.ClearProviders(); // No request URLs, headers, credentials, images or GSI in logs.
        builder.WebHost.ConfigureKestrel(options => {
            options.Limits.MaxRequestBodySize = Wire.MaxFrameBytes;
            options.Limits.MaxRequestHeadersTotalSize = 32768;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(5);
            options.Listen(bind, port, listen => listen.UseHttps(certificate));
        });
        app = builder.Build();
        app.UseWebSockets(new() { KeepAliveInterval = TimeSpan.FromSeconds(10) });
        app.Use(async (context, next) => {
            context.Response.Headers.CacheControl = "no-store";
            if (context.Request.Headers.ContainsKey("Origin") ||
                !Wire.EqualSecret(context.Request.Headers.Authorization.ToString(), "Bearer " + token))
            { context.Response.StatusCode = 401; return; }
            try { await next(context); }
            catch (Exception e) when (e is ArgumentException or JsonException or KeyNotFoundException or NullReferenceException or FormatException)
            { context.Response.StatusCode = 400; }
            catch (InvalidOperationException) { context.Response.StatusCode = 409; }
        });
        app.MapPost("/v1/session", () => Results.Json(Mailbox.Begin(), Wire.Json));
        app.MapDelete("/v1/session", (HttpContext c) => { Mailbox.End(Session(c)); return Results.NoContent(); });
        app.MapGet("/v1/ticket", (HttpContext c) => Results.Json(Mailbox.Issue(Session(c)), Wire.Json));
        app.MapGet("/v1/reminder", (HttpContext c) => Results.Json(new { reminder = Mailbox.Poll(Session(c)) }, Wire.Json));
        app.MapGet("/v1/enemies", (HttpContext c) => Results.Json(new { enemies = Mailbox.Enemies(Session(c)) }, Wire.Json));
        app.MapGet("/v1/icon/{key}", (HttpContext c, string key) =>
            Mailbox.Icon(Session(c), key) is { } png ? Results.Bytes(png, "image/png") : Results.NotFound());
        app.MapGet("/v2/video-ticket", (HttpContext c) => Results.Json(Mailbox.IssueVideoLease(Session(c)), Wire.Json));
        app.Map("/v2/video", async (HttpContext c) => {
            if (!c.WebSockets.IsWebSocketRequest || !c.WebSockets.WebSocketRequestedProtocols.Contains(VideoWire.Subprotocol) ||
                Mailbox.VideoJpegEncoder == null || Mailbox.Snapshot().SessionId != Session(c))
            { c.Response.StatusCode = 409; return; }
            using var socket = await c.WebSockets.AcceptWebSocketAsync(VideoWire.Subprotocol);
            try { await VideoWire.ReceiveStream(socket, Mailbox, Session(c), c.RequestAborted); }
            catch (Exception error) {
                try { Mailbox.DebugSink?.Invoke()?.Error("video-receiver", error); } catch { }
                socket.Abort();
            }
        });
        // Video off (Features.VideoUpload): own GSI and window size, no picture.
        app.MapPost("/v1/status", async (HttpContext c) => {
            if (!c.Request.HasJsonContentType()) return Results.StatusCode(415);
            var meta = JsonSerializer.Deserialize<FrameMeta>(await Bodies.Read(c.Request, Wire.MaxStatusBytes, c.RequestAborted), Wire.Json)
                ?? throw new ArgumentException();
            if (meta.SessionId != Session(c)) return Results.BadRequest();
            Mailbox.AcceptStatus(meta);
            return Results.Json(new { sequence = meta.Sequence }, Wire.Json);
        });
        app.MapPost("/v1/frame", async (HttpContext c) => {
            if (c.Request.ContentType != "image/jpeg") return Results.StatusCode(415);
            string header = c.Request.Headers["X-Frame-Meta"].ToString();
            if (header.Length > 20000) return Results.BadRequest();
            var meta = JsonSerializer.Deserialize<FrameMeta>(Convert.FromBase64String(header), Wire.Json)
                ?? throw new ArgumentException();
            if (meta.SessionId != Session(c)) return Results.BadRequest();
            var jpeg = await Bodies.Read(c.Request, Wire.MaxFrameBytes, c.RequestAborted);
            Mailbox.Accept(meta, jpeg);
            return Results.Json(new { sequence = meta.Sequence }, Wire.Json);
        });
    }
    private static string Session(HttpContext c) => c.Request.Headers["X-Session"].ToString();
    public static async Task<CoachHost> Start(IPAddress bind, int port, CancellationToken cancellation = default)
    {
        if (port is < 0 or > 65535 || !(IPAddress.Any.Equals(bind) || Wire.IsLan(bind)))
            throw new ArgumentException("Only local/LAN listeners are supported");
        var host = new CoachHost(bind, port);
        try
        {
            await host.app.StartAsync(cancellation);
            var actual = new Uri(host.app.Urls.Single());
            host.Port = actual.Port;
            host.ListeningAddress = IPAddress.Parse(actual.Host.Trim('[', ']'));
            return host;
        }
        catch { await host.DisposeAsync(); throw; }
    }
    public Pairing Pair(IPAddress address)
    {
        if (!Wire.IsLan(address)) throw new ArgumentException("Choose a LAN address");
        return new(Wire.Version, new UriBuilder("https", address.ToString(), Port).Uri.AbsoluteUri, Fingerprint, token);
    }
    public void Revoke() { token = Wire.Secret(); Mailbox.Clear(); }
    public async ValueTask DisposeAsync()
    {
        Mailbox.Clear();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(deadline.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync(); certificate.Dispose();
    }
}

internal static class Bodies
{
    public static async Task<byte[]> Read(HttpRequest request, int limit, CancellationToken cancellation)
    {
        if (request.ContentLength > limit) throw new ArgumentException("Body too large");
        using var output = new MemoryStream();
        var buffer = new byte[8192];
        int read;
        while ((read = await request.Body.ReadAsync(buffer, cancellation)) > 0)
        {
            if (output.Length + read > limit) throw new ArgumentException("Body too large");
            await output.WriteAsync(buffer.AsMemory(0, read), cancellation);
        }
        return output.ToArray();
    }
}
