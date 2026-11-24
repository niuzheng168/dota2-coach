using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace DotaCoach.TwoPc;

public sealed record DebugLimits(long MaxBytes = 2L * 1024 * 1024 * 1024, int MaxMinutes = 120,
    int MaxFrameRecords = 7200, int QueueCapacity = 64);
public sealed record DebugStatus(bool Recording, string Reason, long Bytes, long Events, long Images, long Dropped);
public sealed record DebugClientState(string State, bool Connected, bool Automatic, bool ModelReady,
    bool ModelPending, int ModelCalls, long InputTokens, long OutputTokens, long? FrameAgeMs,
    OwnState? Player, MinimapLayout? Calibration = null, int? GsiAgeMs = null,
    int? RequestLimit = null, int? IntervalSeconds = null, string? Model = null,
    bool? SubtitlesEnabled = null, bool? MapMarkersEnabled = null);
public sealed record GsiDiagnostics(string Mode, string[] Sections, bool HasTeamGroups, bool HasAllPlayers, bool OwnStateAccepted,
    int MinimapUnits = 0, int EnemyHeroes = 0);
public sealed record DebugOverlay(string ReminderId, long Sequence, int Width, int Height,
    double DpiScale, int Elements, double? MarkerPixelX, double? MarkerPixelY);
public sealed record VideoStats(string Capture, string Encoder, int SourceWidth, int SourceHeight, int Width, int Height,
    int TargetFps, double ActualFps, double Mbps, long Dropped, double CaptureMsAverage, double EncodeMsAverage,
    bool Background, string? Note = null);

/// <summary>Opt-in local debug trace. Typed allowlists only: never pass HTTP requests, credentials or raw GSI.</summary>
public sealed class DebugRecorder : IAsyncDisposable
{
    private sealed record Work(string Kind, byte[] Json, byte[]? Image, string? ImageFile);
    private readonly Channel<Work> queue;
    private readonly Task writer;
    private readonly DebugLimits limits;
    private readonly Func<long> now;
    private readonly long started;
    private readonly object gate = new();
    private readonly string role, version;
    private bool accepting = true;
    private string reason = "recording";
    private long reserved, written, events, images, dropped, frameRecords, lastState = long.MinValue;
    private long lastSample = long.MinValue, lastSpectator = long.MinValue, lastDelay = long.MinValue, lastVideo = long.MinValue,
        lastPlayerMinimap = long.MinValue;
    private const int ReserveBytes = 32768; // Metadata and final status always have a reserved budget.
    public string DirectoryPath { get; }
    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "Debug");

    public DebugRecorder(string root, string role, string version, DebugLimits? limits = null, Func<long>? clock = null)
    {
        this.limits = limits ?? new();
        if (role is not ("game" or "coach") || this.limits.MaxBytes < 65536 ||
            this.limits.MaxBytes > 8L * 1024 * 1024 * 1024 || this.limits.MaxMinutes is < 1 or > 240 ||
            this.limits.MaxFrameRecords is < 1 or > 14400 || this.limits.QueueCapacity is < 1 or > 128)
            throw new ArgumentException("Invalid debug limits");
        this.role = role; this.version = Text(version, 100); now = clock ?? (() => Environment.TickCount64); started = now();
        DirectoryPath = Path.Combine(Path.GetFullPath(root), $"{DateTime.UtcNow:yyyyMMdd-HHmmss}-{role}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(DirectoryPath, "frames"));
        File.WriteAllText(Path.Combine(DirectoryPath, "session.json"), JsonSerializer.Serialize(new {
            schemaVersion = 1, role, clientVersion = this.version, processId = Environment.ProcessId,
            startedUtc = DateTimeOffset.UtcNow, limits = this.limits,
            privacy = "Local only. JPEGs may contain game chat/private visual content and are NOT redacted. No credentials/raw GSI/HTTP requests.",
            imageMeaning = "Coach-selected JPEG from decoded H264 (legacy diagnostic JPEG also supported), bounded to 1600x900. Not original 4K or overlay pixel proof.",
        }, Wire.Json));
        reserved = ReserveBytes;
        queue = Channel.CreateBounded<Work>(new BoundedChannelOptions(this.limits.QueueCapacity) {
            SingleReader = true, SingleWriter = false, FullMode = BoundedChannelFullMode.Wait,
        });
        writer = Task.Run(WriteLoop);
    }
    public DebugStatus Status
    {
        get {
            lock (gate) {
                if (accepting && now() - started >= limits.MaxMinutes * 60_000L) StopLocked("duration-limit");
                return new(accepting, reason, written, events, images, dropped);
            }
        }
    }
    private void StopLocked(string why)
    {
        if (!accepting) return;
        accepting = false; reason = why; queue.Writer.TryComplete();
    }
    public void Stop() { lock (gate) StopLocked("user-stopped"); }
    private static string Text(string? value, int max = 240)
    {
        string text = value ?? "";
        text = Regex.Replace(text, @"DCP1\.[A-Za-z0-9+/=]+", "[pairing-redacted]");
        text = Regex.Replace(text, @"\bsk-(?:proj-|svcacct-)?[A-Za-z0-9_-]{24,}", "[key-redacted]");
        text = Regex.Replace(text, @"(?i)Bearer\s+[A-Za-z0-9._~+/=-]+", "Bearer [redacted]");
        text = Regex.Replace(text, @"(?i)(FOUNDRY_KEY|API[_-]?KEY|PASSWORD|ACCESS_TOKEN|AUTH_TOKEN)\s*[:=]\s*[""']?[^""'\s,;}]+", "$1=[redacted]");
        return text.Length <= max ? text : text[..max];
    }
    private static object? Own(OwnState? p) => p == null ? null : new {
        matchId = Text(p.MatchId, 24), hero = Text(p.Hero, 80), team = Text(p.Team, 16), p.Clock, p.Alive,
        p.Health, p.Mana, p.X, p.Y,
        items = p.Items.Take(40).Select(i => new { name = Text(i.Name, 80), i.Level, i.Cooldown }),
        abilities = p.Abilities.Take(40).Select(a => new { name = Text(a.Name, 80), a.Level, a.Cooldown }),
    };
    private static object Source(FrameMeta m) => new {
        sessionId = Text(m.SessionId, 64), sequence = m.Sequence, source = Text(m.Source, 24),
        m.SourceWidth, m.SourceHeight, m.GsiAgeMs, player = Own(m.Player),
        informationScope = m.Player == null ? "no-accepted-player-state" : "accepted-flat-player-fields",
        spectatorIdentityAndFogVerified = false,
        // Deliberately no Ticket, pairing bundle, provider, nickname, SteamID or raw payload.
    };
    private bool Enqueue(string kind, object details, byte[]? image = null, string? imageFile = null)
    {
        try {
            if (!Status.Recording) return false;
            byte[] json = JsonSerializer.SerializeToUtf8Bytes(new {
                atUtc = DateTimeOffset.UtcNow, elapsedMs = Math.Max(0, now() - started), role,
                stage = kind, details,
            }, Wire.Json);
            if (json.Length > 131072 || image?.Length > Wire.MaxFrameBytes) return false;
            long cost = json.Length + 1L + (image?.Length ?? 0);
            lock (gate) {
                if (!accepting) return false;
                if (reserved + cost > limits.MaxBytes) { StopLocked("byte-limit"); return false; }
                if (image != null && frameRecords >= limits.MaxFrameRecords) { StopLocked("frame-limit"); return false; }
                // Non-blocking producers: slow disks never hold up capture, rendering or inference.
                if (!queue.Writer.TryWrite(new(kind, json, image?.ToArray(), imageFile))) { dropped++; return false; }
                reserved += cost; if (image != null) frameRecords++;
                return true;
            }
        } catch { lock (gate) dropped++; return false; } // Debugging must not crash the client.
    }
    public void Frame(string stage, FrameMeta meta, byte[] jpeg, long? elapsedMs = null)
    {
        if (stage is not ("frame-captured" or "frame-received" or "frame-sampled" or "model-input")) throw new ArgumentException("Unknown debug frame stage");
        if (!Status.Recording) return;
        try {
            var size = Jpeg.Dimensions(jpeg);
            string hash = Convert.ToHexString(SHA256.HashData(jpeg)).ToLowerInvariant();
            string session = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(meta.SessionId)))[..16].ToLowerInvariant();
            string file = $"frames/{session}-{meta.Sequence:D10}-{hash[..12]}.jpg";
            Enqueue(stage, new { frame = Source(meta), image = file, sha256 = hash,
                encodedWidth = size.Width, encodedHeight = size.Height, elapsedMs }, jpeg, file);
        } catch { lock (gate) dropped++; }
    }
    public void SampleVideo(FrameView frame)
    {
        if (role != "coach" || frame.Video == null || !Status.Recording) return;
        lock (gate) {
            if (lastSample != long.MinValue && now() - lastSample < 1000) return;
            lastSample = now();
        }
        Frame("frame-sampled", frame.Meta, frame.Jpeg);
    }
    public void ModelStarted(FrameView frame, string model, MinimapLayout? layout, int attempt)
    {
        Frame("model-input", frame.Meta, frame.Jpeg);
        Enqueue("model-start", new { frame = Source(frame.Meta), model = Text(model, 100), layout, attempt,
            sourceAgeMs = Math.Max(0, Environment.TickCount64 - frame.CapturedNoEarlierThan),
            receivedAgeMs = Math.Max(0, Environment.TickCount64 - frame.ReceivedAt) });
    }
    public void ModelFinished(FrameView frame, ModelResult result)
    {
        Frame("model-input", frame.Meta, frame.Jpeg);
        var a = result.Advice;
        Enqueue("model-result", new { frame = Source(frame.Meta), model = Text(result.Model, 100),
            responseId = Text(result.ResponseId, 160), result.InputTokens, result.OutputTokens, result.ElapsedMs,
            advice = new { hero = Text(a.Hero, 80), team = Text(a.Team, 16), scene = Text(a.Scene),
                message = Text(a.Message, 120), visibleFacts = a.VisibleFacts.Take(8).Select(x => Text(x, 180)),
                unknowns = a.Unknowns.Take(8).Select(x => Text(x, 180)),
                nextSteps = a.NextSteps.Take(3).Select(x => Text(x, 180)), cancelIf = Text(a.CancelIf, 200),
                a.Marker, markerBasis = Text(a.MarkerBasis),
                route = (a.Route ?? []).Take(Wire.MaxRouteSteps).Select(x => Text(x, 100)), routeMarks = a.RouteMarks } });
    }
    public void Reminder(string stage, Reminder r)
    {
        if (stage is not ("reminder-published" or "reminder-received" or "reminder-cleared"))
            throw new ArgumentException("Unknown reminder stage");
        Enqueue(stage, new { id = Text(r.Id, 64), sessionId = Text(r.SessionId, 64), r.FrameSequence,
            kind = Text(r.Kind, 32), message = Text(r.Message, 120), r.Marker, r.SourceWidth, r.SourceHeight,
            r.RemainingMs, r.Minimap, r.ShowSubtitle, route = r.Route?.Take(Wire.MaxRouteSteps),
            nextSteps = r.NextSteps?.Take(Wire.MaxRouteSteps).Select(x => Text(x, Wire.MaxStepText)), r.SourceAgeMs });
    }
    /// <summary>Game-side video pipeline summary (once per state tick): what actually ran, not what was requested.</summary>
    public void Video(VideoStats v) { if (Every(ref lastVideo, 2000)) Enqueue("video-status", v); }
    public void Transfer(FrameMeta frame, long elapsedMs) => Enqueue("frame-uploaded", new { frame = Source(frame), elapsedMs });
    public void State(DebugClientState state)
    {
        lock (gate) {
            if (lastState != long.MinValue && now() - lastState < 2000) return;
            lastState = now();
        }
        using var process = Process.GetCurrentProcess();
        Enqueue("client-state", new { state = Text(state.State, 160), state.Connected, state.Automatic,
            state.ModelReady, state.ModelPending, state.ModelCalls, state.InputTokens, state.OutputTokens,
            state.FrameAgeMs, state.GsiAgeMs, player = Own(state.Player), state.Calibration,
            state.RequestLimit, state.IntervalSeconds, model = Text(state.Model, 100),
            state.SubtitlesEnabled, state.MapMarkersEnabled,
            workingSetBytes = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            totalProcessorMs = process.TotalProcessorTime.TotalMilliseconds });
    }
    public void Gsi(GsiDiagnostics d) => Enqueue("gsi-status", new {
        mode = Text(d.Mode, 32), sections = d.Sections.Take(12).Select(s => Text(s, 24)),
        d.HasTeamGroups, d.HasAllPlayers, d.OwnStateAccepted, d.MinimapUnits, d.EnemyHeroes,
        warning = "Structure summary only. Observer fields are not admitted to live inference; fog filtering is unverified.",
    });
    private bool Every(ref long last, int intervalMs)
    {
        lock (gate) {
            if (last != long.MinValue && now() - last < intervalMs) return false;
            last = now(); return true;
        }
    }
    // [unitname, team, x, y, visionrange, icon, yaw]: no player names or IDs are parsed at all.
    // 1200 units keeps one event under the 128 KiB record cap.
    private static IEnumerable<object?[]> Units(SpectatorSnapshot s) =>
        s.Minimap.Take(1200).Select(u => new object?[] { u.Name, u.Team, Math.Round(u.X), Math.Round(u.Y), u.Vision, u.Image, TeamViewEvidence.Facing(u.Yaw) });
    /// <summary>Coach-only, once per second: typed minimap units from this machine's own spectating Dota.</summary>
    public void Spectator(SpectatorSnapshot s)
    {
        if (role != "coach" || !Status.Recording || !Every(ref lastSpectator, 1000)) return;
        Enqueue("spectator-snapshot", new { mode = Text(s.Mode, 24), s.Clock, gameState = Text(s.GameState, 64), s.Paused,
            s.OwnTeam, s.GroupedPlayers, buildingTeams = s.BuildingTeams, eventTypes = s.EventTypes.Take(20).Select(e => Text(e, 40)),
            kills = s.Kills is var (radiant, dire) ? new[] { radiant, dire } : null,
            units = Units(s),
            warning = "Dota Plus/spectator observation. Not admitted to the automatic coach; fog filtering is unverified.",
        });
    }
    /// <summary>Game-only, once per second in a match: the same typed units from this machine's own player-mode GSI.</summary>
    public void PlayerMinimap(SpectatorSnapshot s)
    {
        if (role != "game" || s.Mode == "menu" || !Status.Recording || !Every(ref lastPlayerMinimap, 1000)) return;
        Enqueue("player-minimap", new { mode = Text(s.Mode, 24), s.Clock, gameState = Text(s.GameState, 64), s.Paused,
            s.OwnTeam, buildingTeams = s.BuildingTeams, eventTypes = s.EventTypes.Take(20).Select(e => Text(e, 40)),
            units = Units(s),
            warning = "Own player-mode GSI. Not admitted to the automatic coach; fog filtering is unverified.",
        });
    }
    public void SpectatorDelay(DelayStats d)
    {
        if (role != "coach" || !Status.Recording || !Every(ref lastDelay, 10_000)) return;
        Enqueue("spectator-delay", new { d.Samples, d.MedianMs, d.P10Ms, d.P90Ms, d.ClockGapSeconds, note = Text(d.Note, 80),
            method = "coach monotonic clock; game side = frame arrival - GSI age, so it reads low by video latency" });
    }
    public void Error(string operation, Exception error, FrameMeta? frame = null, string? reason = null) => Enqueue("error", new {
        operation = Text(operation, 64), exceptionType = error.GetType().Name, error.HResult,
        reason = Text(reason, 64), httpStatus = error is ModelProviderException p ? p.StatusCode :
            error is HttpRequestException h && h.StatusCode != null ? (int?)h.StatusCode : null,
        nativeError = error is System.ComponentModel.Win32Exception w ? (int?)w.NativeErrorCode : null,
        cancelled = error is OperationCanceledException, frame = frame == null ? null : Source(frame),
        // Never serialize exception.Message/StackTrace: providers can put URLs or credentials in them.
    });
    public void Overlay(string stage, DebugOverlay data)
    {
        if (stage is not ("overlay-rendered" or "overlay-cleared")) throw new ArgumentException("Unknown overlay stage");
        Enqueue(stage, new { reminderId = Text(data.ReminderId, 64), data.Sequence, data.Width, data.Height,
            data.DpiScale, data.Elements, data.MarkerPixelX, data.MarkerPixelY,
            warning = "Render receipt, not a captured-screen proof." });
    }
    private async Task WriteLoop()
    {
        try {
            await using var log = new FileStream(Path.Combine(DirectoryPath, "events.jsonl"), FileMode.CreateNew,
                FileAccess.Write, FileShare.Read, 16384, useAsync: true);
            await foreach (var item in queue.Reader.ReadAllAsync()) {
                if (item.Image != null && item.ImageFile != null) {
                    string file = Path.Combine(DirectoryPath, item.ImageFile);
                    if (!File.Exists(file)) {
                        await File.WriteAllBytesAsync(file, item.Image);
                        lock (gate) { written += item.Image.Length; images++; }
                    }
                }
                await log.WriteAsync(item.Json); await log.WriteAsync("\n"u8.ToArray());
                await log.FlushAsync();
                lock (gate) { written += item.Json.Length + 1; events++; }
            }
        } catch {
            lock (gate) { StopLocked("disk-error"); reason = "disk-error"; }
        } finally {
            // Release queued JPEG references if the writer failed.
            while (queue.Reader.TryRead(out _)) { lock (gate) dropped++; }
            try {
                File.WriteAllText(Path.Combine(DirectoryPath, "summary.json"), JsonSerializer.Serialize(new {
                    schemaVersion = 1, role, clientVersion = version, endedUtc = DateTimeOffset.UtcNow, status = Status,
                }, Wire.Json));
            } catch { lock (gate) reason = "disk-error"; }
        }
    }
    public async ValueTask DisposeAsync()
    {
        Stop(); await writer;
    }
}
