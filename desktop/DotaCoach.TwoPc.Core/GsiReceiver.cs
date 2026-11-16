using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotaCoach.TwoPc;

/// <summary>Loopback-only official GSI ingress on the game machine. Raw payloads never leave it.</summary>
public sealed class GsiReceiver : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string token;
    private readonly object gate = new();
    private (OwnState Value, long At)? latest;
    public int Port { get; private set; }
    private GsiReceiver(int port, string? resumeToken)
    {
        if (resumeToken != null && (resumeToken.Length != 64 || !resumeToken.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid explicit receiver credential");
        token = resumeToken ?? Wire.Secret();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => {
            o.Limits.MaxRequestBodySize = 65536;
            o.Listen(IPAddress.Loopback, port);
        });
        app = builder.Build();
        app.MapPost("/gsi", async (HttpContext c) => {
            if (c.Request.Headers.ContainsKey("Origin")) return Results.Unauthorized();
            try
            {
                using var doc = JsonDocument.Parse(await Bodies.Read(c.Request, 65536, c.RequestAborted),
                    new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                if (!Wire.EqualSecret(root.GetProperty("auth").GetProperty("token").GetString(), token))
                    return Results.Unauthorized();
                // An authenticated menu/observer/invalid payload invalidates old own-player state.
                lock (gate) latest = null;
                var own = GsiState.Parse(root);
                lock (gate) latest = (own, Environment.TickCount64);
                return Results.Ok();
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { return Results.BadRequest(); }
        });
    }
    public (OwnState? Player, int? AgeMs) Latest()
    {
        lock (gate)
        {
            if (latest is not { } s || Environment.TickCount64 - s.At > 3000) return (null, null);
            return (s.Value, (int)(Environment.TickCount64 - s.At));
        }
    }
    public string Config => $$"""
        "Dota Coach Two PC"
        {
          "uri" "http://127.0.0.1:{{Port}}/gsi"
          "timeout" "2.0"
          "buffer" "0.1"
          "throttle" "0.5"
          "heartbeat" "1.0"
          "auth" { "token" "{{token}}" }
          "data"
          {
            "provider" "1"
            "map" "1"
            "player" "1"
            "hero" "1"
            "abilities" "1"
            "items" "1"
          }
        }
        """;
    public static async Task<GsiReceiver> Start(int port = 3219, CancellationToken cancellation = default, string? resumeToken = null)
    {
        var receiver = new GsiReceiver(port, resumeToken);
        try
        {
            await receiver.app.StartAsync(cancellation); receiver.Port = new Uri(receiver.app.Urls.Single()).Port;
            return receiver;
        }
        catch { await receiver.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) latest = null;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(deadline.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }
}
