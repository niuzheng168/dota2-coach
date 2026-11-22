using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace DotaCoach.TwoPc;

/// <summary>Loopback-only official GSI ingress on the game machine. Raw payloads never leave it.</summary>
public sealed partial class GsiReceiver : IAsyncDisposable
{
    private readonly WebApplication app;
    private readonly string token;
    private readonly object gate = new();
    private (OwnState Value, long At)? latest;
    private readonly MatchWitnessStream matches = new();
    private readonly MinuteGsiStream farm = new();
    private string? farmInstall;
    private long farmDelivery, farmHandled;
    private bool incompatibleInstall;
    private (SpectatorSnapshot Value, long At)? units;
    private bool lunaDrill;
    public int Port { get; private set; }
    public Action<GsiDiagnostics>? Diagnostics { get; set; }
    /// <summary>Typed minimap units of each authenticated packet, for the opt-in Debug log only.</summary>
    public Action<SpectatorSnapshot>? Snapshot { get; set; }
    public GsiDiagnostics? LastDiagnostics { get; private set; }
    private const int MaxBody = 1024 * 1024; // Own state is small; requested minimap data may not be.
    private static bool InvalidPacket(Exception e) => e is JsonException or ArgumentException or InvalidOperationException or KeyNotFoundException;
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
        if (snapshot != null) {
            lock (gate) units = (snapshot, Environment.TickCount64);
            try { Snapshot?.Invoke(snapshot); } catch { /* Same rule for the typed view. */ }
        }
    }
    private GsiReceiver(int port, string? resumeToken, string? dotaInstall)
    {
        farmInstall = dotaInstall;
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
            long request; PostgameOutbox? recipient;
            lock (gate) { request = matches.BeginRequest(); recipient = postgameRecipient; } // Bind before the asynchronous body read.
            try
            {
                using var doc = JsonDocument.Parse(await Bodies.Read(c.Request, MaxBody, c.RequestAborted),
                    new JsonDocumentOptions { MaxDepth = 16 });
                var root = doc.RootElement;
                if (!Wire.EqualSecret(root.GetProperty("auth").GetProperty("token").GetString(), token))
                    return Results.Unauthorized();
                // An authenticated menu/observer/invalid payload invalidates old own-player state.
                OwnState? own = null;
                long order = Interlocked.Increment(ref farmDelivery);
                long utc = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), received = Environment.TickCount64;
                long? sourceTime = null;
                MatchSample? candidate = null;
                try {
                    sourceTime = GsiState.PacketUnixMs(root, utc, MatchWitness.MaxAgeMs);
                    if (sourceTime.HasValue) candidate = GsiState.ReadMatch(root);
                } catch (Exception e) when (InvalidPacket(e)) { /* Malformed authenticated evidence withdraws the witness. */ }
                long? packetTime = sourceTime is long stamp && utc - stamp < MinuteFarmContext.FreshMs ? stamp : null;
                try { own = GsiState.Parse(root, participantStats: candidate?.Hero != null); }
                catch (Exception e) when (InvalidPacket(e)) { /* Sparse verified lifecycle may not have a legacy OwnState. */ }
                string? directory, probe;
                lock (gate) {
                    directory = farmInstall;
                    probe = directory != null && matches.NeedsInstall(request, candidate, sourceTime, utc, received,
                        Environment.TickCount64, MinuteFarmContext.Eligible(own)) ? directory : null;
                }
                // At most one bounded manifest read for both projections, off the capture lock. No
                // file access on polls, untrusted views or isolated terminal packets; no cached positive.
                var manifest = probe == null ? default : MinuteGsiStream.ReadInstall(probe);
                var key = own?.Farm == null ? default : GsiState.ParticipantKey(root);
                bool drill = own != null && IsLunaDrill(root, own);
                bool? witnessed;
                lock (gate) {
                    // Commit the two projections of this delivery atomically. A directory/session reset
                    // invalidates pending requests, including an old read of a subsequently reselected directory.
                    witnessed = matches.Observe(request, candidate, sourceTime, utc, received, Environment.TickCount64,
                        directory == farmInstall && !manifest.Conflict ? manifest.Build : null, out bool gap, out bool firstPostgame);
                    if (witnessed == null) return own == null ? Results.BadRequest() : Results.Ok();
                    // Re-pairing during an old terminal body read can seed the new outbox with the
                    // last playing sample. Do not let a subsequent duplicate end relabel that old transition.
                    if (witnessed == true && recipient != null && recipient == postgameRecipient &&
                        (candidate!.Phase != "postgame" || firstPostgame)) {
                        long tick = Environment.TickCount64;
                        recipient.Observe(matches.Latest(tick), tick);
                    }
                    if (gap || order != farmHandled + 1) farm.Invalidate(null);
                    bool mismatch = manifest.Conflict || manifest.Build is int b && b != MinuteFarmContext.SupportedBuild;
                    if (witnessed == true && mismatch) incompatibleInstall = true;
                    if (own == null) {
                        farm.Invalidate(packetTime ?? utc / 1000 * 1000); latest = null;
                    } else {
                        if (witnessed == false && own.Stats != null) own = own with { Stats = null };
                        if (order <= farmHandled || directory != farmInstall) own = own with { Farm = null };
                        else if (own.Farm is { } telemetry) {
                            if (manifest.Conflict || manifest.Build.HasValue) incompatibleInstall = mismatch;
                            // A known conflicting build does not turn into a manual-confirmation fallback on an I/O error.
                            own = own with { Farm = incompatibleInstall && manifest.Build == null ? null :
                                telemetry with { InstalledBuild = manifest.Build } };
                        }
                        latest = (farm.Observe(own, packetTime, utc, received, key), received);
                    }
                    farmHandled = Math.Max(farmHandled, order); lunaDrill = drill;
                }
                Describe(root, own != null);
                return own != null || witnessed == true ? Results.Ok() : Results.BadRequest();
            }
            catch (Exception e) when (InvalidPacket(e))
            { return Results.BadRequest(); }
            finally { lock (gate) matches.FinishRequest(request); }
        });
    }
    /// <summary>GSI may outlive a pairing (e.g. the local practice display). Don't reuse another directory's build.</summary>
    public void UseDotaInstall(string? directory)
    {
        lock (gate) {
            if (string.Equals(farmInstall, directory, StringComparison.OrdinalIgnoreCase)) return;
            matches.Reset(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            postgameRecipient?.Reset();
            farmInstall = directory; incompatibleInstall = false; farm.Invalidate(DateTimeOffset.UtcNow.ToUnixTimeSeconds() * 1000);
            if (latest is { } s) latest = (s.Value with { Farm = null, Stats = null }, s.At);
        }
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
        lock (gate) return PlayerAt(Environment.TickCount64);
    }
    /// <summary>When sending both projections, sample once so a withdrawal/switch cannot pair another
    /// raw packet's player with this witness. Either projection may independently be null.</summary>
    public (OwnState? Player, int? AgeMs) Latest(out MatchWitness? match)
    {
        lock (gate) {
            long now = Environment.TickCount64;
            match = matches.Latest(now);
            return PlayerAt(now);
        }
    }
    private (OwnState? Player, int? AgeMs) PlayerAt(long now)
    {
        if (latest is not { } s || now - s.At > 3000) return (null, null);
        var own = s.Value;
        int age = (int)(now - s.At);
        if (own.Stats != null) {
            if (matches.Age(now) is int sourceAge) age = Math.Max(age, sourceAge);
            else own = own with { Stats = null }; // Expiry cannot turn fresh-by-receipt legacy data into participant evidence.
        }
        if (own.Farm is { Epoch: var epoch } && epoch != Guid.Empty) {
            if (farm.Age(now) is int farmAge) return (own, Math.Max(age, farmAge));
            // Expiring the stronger packet/clock evidence must not discard a legacy flat projection.
            own = own with { Farm = null };
        }
        return (own, age);
    }
    /// <summary>Fresh authenticated participant lifecycle, independent of Latest()/Farm, death and pause.
    /// Send AgeMs with the projection. Null (including silence/disconnect) is never a multiplayer end.</summary>
    public MatchWitness? LatestMatch()
    {
        lock (gate) return matches.Latest(Environment.TickCount64);
    }
    /// <summary>The newest packet's minimap units (what the player's team sees) and own team; stays on this machine.</summary>
    public (SpectatorSnapshot Snapshot, int AgeMs)? LatestMinimap()
    {
        lock (gate) {
            if (units is not { } m || Environment.TickCount64 - m.At > 3000) return null;
            return (m.Value, (int)(Environment.TickCount64 - m.At));
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
    public static async Task<GsiReceiver> Start(int port = 3219, CancellationToken cancellation = default, string? resumeToken = null,
        string? dotaInstall = null)
    {
        var receiver = new GsiReceiver(port, resumeToken, dotaInstall);
        try
        {
            await receiver.app.StartAsync(cancellation); receiver.Port = new Uri(receiver.app.Urls.Single()).Port;
            return receiver;
        }
        catch { await receiver.DisposeAsync(); throw; }
    }
    public async ValueTask DisposeAsync()
    {
        lock (gate) { latest = null; units = null; farm.Invalidate(null); matches.Reset(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), stop: true);
            postgameRecipient?.Close(); postgameRecipient = null; }
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        try { await app.StopAsync(deadline.Token); } catch (OperationCanceledException) { }
        await app.DisposeAsync();
    }
}
