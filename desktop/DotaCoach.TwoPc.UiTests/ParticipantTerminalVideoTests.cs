using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Json;
using DotaCoach.TwoPc;

internal static partial class Program
{
    private sealed class GeneratedVideo : IVideoSource
    {
        public string Name => "generated";
        public int SourceWidth => 1280;
        public int SourceHeight => 720;
        public int Width => 320;
        public int Height => 180;
        public bool Ended => false;
        private readonly VideoPixels pixels = new(320, 180, new byte[320 * 180 * 3 / 2]);
        public VideoPixels Grab() => pixels; // Generated NV12 only; no desktop/window/foreground lookup.
        public void Dispose() { }
    }

    private static async Task CheckVideoTerminal(Action<bool, string> check, bool duringGrace)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        host.Mailbox.VideoJpegEncoder = _ => throw new Exception("terminal handling must never extract video pixels");
        string secret = Wire.Secret();
        await using var receiver = await GsiReceiver.Start(0, resumeToken: secret);
        using var raw = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        await link.Connect(default); string id = link.SessionId!;
        long seq = 0;
        long Next() => Interlocked.Increment(ref seq);
        link.BindPostgame(receiver, "dota2", Next);
        await link.OpenVideo(default);
        async Task Post(string? phase) {
            var packet = new Dictionary<string, object> { ["auth"] = new { token = secret } };
            if (phase != null) {
                packet["provider"] = new { name = "Dota 2", appid = 570, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() };
                packet["map"] = new { name = "dota", matchid = BoardMatch, game_state = "DOTA_GAMERULES_STATE_" + phase,
                    paused = false, clock_time = phase == "POST_GAME" ? 502 : 501 };
                packet["player"] = new { activity = "playing", team_name = "radiant", player_slot = 0, team_slot = 0 };
                if (phase != "POST_GAME") packet["hero"] = new { name = BoardHero, alive = false, health = 0, mana = 254 };
            }
            using var response = await raw.PostAsJsonAsync("/gsi", packet);
            if (phase != null) response.EnsureSuccessStatusCode();
        }

        var dir = Directory.CreateTempSubdirectory("video-terminal-");
        try {
            using var archive = new MatchArchive(dir.FullName);
            var state = new LiveMatchState(archive);
            using var stop = new CancellationTokenSource();
            using var captureStop = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
            await Post("GAME_IN_PROGRESS");
            var sender = new VideoSender(await link.GetVideoTicket(default), 30);
            var capture = sender.StartCapture(() => new GeneratedVideo(), false, Next,
                () => GameGsiSample.ReadForUpload(receiver, participantOnly: false), captureStop.Token);
            var send = Task.Run(() => sender.SendLoop(link, false, 1_000_000, stop.Token));
            try {
                long renew = Environment.TickCount64 + 650, deadline = renew + 4500;
                while (host.Mailbox.Latest()?.Meta.MatchPhase?.Phase != "playing" && Environment.TickCount64 < deadline) {
                    if (capture.IsCompleted || send.IsCompleted) await Task.WhenAny(capture, send).Unwrap();
                    if (Environment.TickCount64 >= renew) {
                        sender.Renew(await link.GetVideoTicket(default)); await Post("GAME_IN_PROGRESS"); renew = Environment.TickCount64 + 650;
                    }
                    await Task.Delay(15);
                }
                var playing = host.Mailbox.Latest();
                check(playing is { Video: not null, Meta: { Source: "dota2", Player.Alive: false, MatchPhase.Hero: BoardHero } },
                    "real H.264 encoder, pinned WSS and native decoder admitted the original participant without a real game or capture API");
                state.ObserveFrame(playing);
                // Loss of viewport before terminal send. Stop every source of pictures and wait for the
                // already valid frames to drain. Intentionally delay the receipt worker until AFTER menu.
                captureStop.Cancel(); await Task.WhenAll(capture, send);
                await Wait(() => host.Mailbox.Snapshot().Frames == sender.Sent, "all generated pre-end video frames admitted");
                var before = host.Mailbox.Snapshot();
                Task? drain = duringGrace ? link.DrainVideoPostgame(default) : null;
                if (drain != null) {
                    check(!drain.IsCompleted, "viewport loss while still playing leaves a bounded metadata-only opportunity for the last GSI packet");
                    await Task.Delay(50);
                }
                await Post("POST_GAME");
                int originalAge = receiver.LatestMatch()!.AgeMs;
                check(GameGsiSample.ReadForUpload(receiver, false) == default && GameGsiSample.ReadForUpload(receiver, true) == default,
                    "a currently verified postgame is not replayed as a current player or future session frame on either sender");
                await Task.Delay(200); await Post(null);
                check(receiver.LatestMatch() == null && (duringGrace || host.Mailbox.TakePostgame() == null) && !archive.IsSealed(BoardMatch),
                    duringGrace ? "terminal during viewport-loss grace does not become a live menu witness" :
                    "video: terminal then menu 200 ms later before any send; viewport loss and silence did not seal");
                await (drain ?? link.DrainVideoPostgame(default)); // Drain while the original WS is still attached.
                var admitted = host.Mailbox.Snapshot();
                check(admitted.Frames == before.Frames && admitted.Bytes == before.Bytes && admitted.Sequence == before.Sequence,
                    "viewport-loss drain sends only the bounded receipt: no new live frame, video bytes or invented geometry");
                link.AbortVideo();
                await Wait(() => host.Mailbox.Snapshot().SessionId == null, "original real video session detached before the coach UI read");
                var end = host.Mailbox.TakePostgame();
                check(end is { Video: null, Jpeg.Length: 0, Meta: { Source: "dota2", SourceWidth: 0, Player: null,
                        MatchPhase: { Phase: "postgame", MatchId: BoardMatch, Hero: BoardHero } } } &&
                    end.Meta.SessionId == id && end.Meta.Sequence > before.Sequence &&
                    end.Meta.MatchPhase.AgeMs + Environment.TickCount64 - end.CapturedNoEarlierThan >= originalAge + 180,
                    "the real video pairing delivered its original, re-aged, metadata-only receipt after viewport loss");
                state.ObserveFrame(end);
                check(archive.IsSealed(BoardMatch) && archive.Read(BoardMatch).Count(f => f.Kind == "phase" &&
                    f.Data.GetProperty("phase").GetString() == "postgame") == 1 && host.Mailbox.TakePostgame() == null,
                    "coach seals the recorded participant exactly once from the admitted end after video detach");
            } finally {
                stop.Cancel(); link.AbortVideo();
                try { await Task.WhenAll(capture, send); } catch (OperationCanceledException) { }
                await link.End(default);
            }
        } finally { dir.Delete(true); }
    }
}
