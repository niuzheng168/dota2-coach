using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotaCoach.TwoPc;

/// <summary>
/// Loopback-only GSI ingress for the coach machine's own Dota (Dota Plus live spectating).
/// Raw payloads are parsed into typed snapshots and dropped; nothing is forwarded to the game machine.
/// </summary>
public sealed class SpectatorReceiver : IAsyncDisposable
{
    public const int DefaultPort = 3220;
    private const int MaxBody = 2 * 1024 * 1024; // Spectator minimap payloads are far larger than own-player state.
    private static readonly string[] Components = ["provider", "map", "player", "hero", "abilities", "items", "buildings",
        "league", "draft", "wearables", "events", "minimap", "roshan", "couriers", "neutralitems"];
    private readonly WebApplication app;
    private readonly string token;
    private readonly Func<long> now;
    private readonly object gate = new();
    private SpectatorSnapshot? latest;
    private long packets, rejected;
    public int Port { get; private set; }
    public Action<SpectatorSnapshot>? Received { get; set; }
    private SpectatorReceiver(int port, string token, Func<long>? clock)
    {
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new ArgumentException("Invalid receiver credential");
        this.token = token; now = clock ?? (() => Environment.TickCount64);
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => { o.Limits.MaxRequestBodySize = MaxBody; o.Listen(IPAddress.Loopback, port); });
        app = builder.Build();
        app.MapPost("/spectator", async (HttpContext c) => {
            if (c.Request.Headers.ContainsKey("Origin")) return Results.Unauthorized();
            long at = now();
            try {
                using var doc = JsonDocument.Parse(await Bodies.Read(c.Request, MaxBody, c.RequestAborted),
                    new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                if (!Wire.EqualSecret(root.GetProperty("auth").GetProperty("token").GetString(), token)) {
                    Interlocked.Increment(ref rejected); return Results.Unauthorized();
                }
                var snapshot = SpectatorGsi.Parse(root, at);
                lock (gate) { latest = snapshot; packets++; }
                try { Received?.Invoke(snapshot); } catch { /* Observers must never change ingestion. */ }
                return Results.Ok();
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { return Results.BadRequest(); }
        });
    }
    public (SpectatorSnapshot? Latest, long Packets, long Rejected) Status()
    {
        lock (gate) return (latest, packets, Interlocked.Read(ref rejected));
    }
    // throttle 0.1 / buffer 0: the clock_time transitions are what the delay measurement times.
    public string Config => $$"""
        "Dota Coach Plus Spectator"
        {
          "uri" "http://127.0.0.1:{{Port}}/spectator"
          "timeout" "2.0"
          "buffer" "0.0"
          "throttle" "0.1"
          "heartbeat" "1.0"
          "auth" { "token" "{{token}}" }
          "data"
          {
        {{string.Join("\n", Components.Select(c => $"    \"{c}\" \"1\""))}}
          }
        }
        """;
    public static async Task<SpectatorReceiver> Start(int port, string token, CancellationToken cancellation = default, Func<long>? clock = null)
    {
        var receiver = new SpectatorReceiver(port, token, clock);
        try {
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
