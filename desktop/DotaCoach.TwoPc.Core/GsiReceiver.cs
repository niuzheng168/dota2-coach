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
    private bool lunaDrill;
    public int Port { get; private set; }
    public Action<GsiDiagnostics>? Diagnostics { get; set; }
    /// <summary>Typed minimap units of each authenticated packet, for the opt-in Debug log only.</summary>
    public Action<SpectatorSnapshot>? Snapshot { get; set; }
    public GsiDiagnostics? LastDiagnostics { get; private set; }
    private const int MaxBody = 1024 * 1024; // Own state is small; requested minimap data may not be.
    private void Describe(JsonElement root, bool accepted)
    {
        string[] keys = ["provider", "map", "player", "hero", "items", "abilities", "allplayers", "buildings", "draft", "minimap"];
        string[] present = keys.Where(k => root.TryGetProperty(k, out _)).ToArray();
        bool teams = root.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.Object &&
            (p.TryGetProperty("team2", out _) || p.TryGetProperty("team3", out _));
        bool all = root.TryGetProperty("allplayers", out _);
        int minimap = root.TryGetProperty("minimap", out var m) && m.ValueKind == JsonValueKind.Object ? m.EnumerateObject().Count() : 0;
        // Player mode does send a minimap (2026-11-17); which enemies it holds, i.e. fog filtering, is still unverified.
        SpectatorSnapshot? snapshot = null;
        try { snapshot = SpectatorGsi.Parse(root, Environment.TickCount64); } catch { /* Debug view only. */ }
        int enemies = snapshot is { OwnTeam: int own } ? snapshot.Heroes.Where(u => u.Team is 2 or 3 && u.Team != own)
            .Select(u => u.Name).Distinct().Count() : 0;
        LastDiagnostics = new(accepted ? "flat-player" : teams || all ? "observer-shaped" : "missing-own-state",
            present, teams, all, accepted, minimap, enemies);
        try { Diagnostics?.Invoke(LastDiagnostics); } catch { /* Diagnostics must never change GSI acceptance. */ }
        if (snapshot != null) try { Snapshot?.Invoke(snapshot); } catch { /* Same rule for the typed view. */ }
    }
    private GsiReceiver(int port, string? resumeToken)
    {
        if (resumeToken != null && (resumeToken.Length != 64 || !resumeToken.All(Uri.IsHexDigit)))
            throw new ArgumentException("Invalid explicit receiver credential");
        token = resumeToken ?? Wire.Secret();
        var builder = WebApplication.CreateSlimBuilder(); builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o => {
            o.Limits.MaxRequestBodySize = MaxBody;
            o.Listen(IPAddress.Loopback, port);
        });
        app = builder.Build();
        app.MapPost("/gsi", async (HttpContext c) => {
            if (c.Request.Headers.ContainsKey("Origin")) return Results.Unauthorized();
            try
            {
                using var doc = JsonDocument.Parse(await Bodies.Read(c.Request, MaxBody, c.RequestAborted),
                    new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                if (!Wire.EqualSecret(root.GetProperty("auth").GetProperty("token").GetString(), token))
                    return Results.Unauthorized();
                // An authenticated menu/observer/invalid payload invalidates old own-player state.
                OwnState own;
                try { own = GsiState.Parse(root); }
                catch { lock (gate) { latest = null; lunaDrill = false; } Describe(root, false); throw; }
                bool drill = IsLunaDrill(root, own);
                lock (gate) { latest = (own, Environment.TickCount64); lunaDrill = drill; }
                Describe(root, true);
                return Results.Ok();
            }
            catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException)
            { return Results.BadRequest(); }
        });
    }
    // A local flag only; do not extend the transmitted OwnState or accept a spectator's heroes.
    public static bool IsLunaDrill(JsonElement root, OwnState own)
    {
        var m = root.GetProperty("map"); var h = root.GetProperty("hero");
        return own is { MatchId: "0", Hero: "npc_dota_hero_luna", Alive: true, Clock: >= 0 and < 450 }
            && m.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String
            && n.GetString()!.StartsWith("hero_demo_", StringComparison.Ordinal)
            && m.TryGetProperty("game_state", out var s) && s.ValueKind == JsonValueKind.String
            && s.GetString() == "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS"
            && m.TryGetProperty("daytime", out var day) && day.ValueKind == JsonValueKind.True
            && !(m.TryGetProperty("paused", out var paused) && paused.ValueKind == JsonValueKind.True)
            && h.TryGetProperty("level", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetInt32(out int level) && level == 1
            && !new[] { "stunned", "disarmed", "hexed", "break", "has_debuff" }.Any(k => h.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.True)
            && own.Items.All(i => i.Name == "item_tpscroll");
    }
    public bool CanPractice
    {
        get { lock (gate) return lunaDrill && latest is { } s && Environment.TickCount64 - s.At < 1500; }
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
          "buffer" "0.0"
          "throttle" "0.2"
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
            "minimap" "1"
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
