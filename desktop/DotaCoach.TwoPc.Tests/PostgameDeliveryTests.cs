using System.Net;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class PostgameDeliveryTests
{
    private const string Match = "9765432111", Hero = "npc_dota_hero_lion";
    private static readonly MatchWitness Play = new(Match, "radiant", Hero, 144, "playing", false);
    private static readonly MatchWitness End = Play with { Phase = "postgame", Clock = 145 };

    internal static async Task Run(Action<bool, string> check)
    {
        long tick = 5000, seq = 0;
        var outbox = new PostgameOutbox("first", "dota2-gsi", () => ++seq);
        outbox.Observe(End, tick);
        check(outbox.Peek(tick) == null, "an isolated or pre-pairing end cannot seed the game outbox");
        outbox.Observe(Play, tick); outbox.Observe(End with { AgeMs = 1900 }, tick);
        outbox.Observe(End with { AgeMs = 0 }, tick + 700); outbox.Acknowledge(2);
        check(outbox.Peek(tick + 700) is { Sequence: 1, End.AgeMs: 2600 }, "a repeated postgame and an unrelated ACK do not replace or refresh the original receipt");
        check(outbox.Peek(tick + 1100) == null, "terminal TTL starts at provider evidence, not the HTTP receipt or the menu");
        outbox.Observe(End, tick + 1200);
        check(outbox.Peek(tick + 1200) == null, "a fresh-looking duplicate cannot resurrect an expired terminal");
        outbox.Reset(); outbox.Observe(Play, tick); outbox.Observe(End, tick);
        outbox.Observe(Play with { MatchId = "9765432112" }, tick + 10);
        check(outbox.Peek(tick + 10) == null, "a newly authenticated different participant withdraws an unsent end");
        outbox.Close();
        Mailbox(check);
        await DelayedAdmission(check);
        await Repaired(check, inFlight: false);
        await Repaired(check, inFlight: true);
    }

    private static void Mailbox(Action<bool, string> check)
    {
        long now = 20000;
        var box = new CoachMailbox(() => now);
        string session = box.Begin().SessionId;
        void State(long sequence, MatchWitness? w) {
            var ticket = box.Issue(session);
            box.AcceptMinuteState(new(session, ticket.Id, sequence, "dota2-gsi", 0, 0, null, null, w), 100);
        }
        PostgameReceipt Receipt(MatchWitness w, long seq = 2) => new(session, box.IssuePostgameTicket(session).Id, "dota2-gsi", seq, w);
        bool Rejected(PostgameReceipt r) {
            try { box.AcceptPostgame(r); return false; } catch (Exception e) when (e is InvalidOperationException or ArgumentException) { return true; }
        }
        check(Rejected(Receipt(End)), "metadata-only end requires a previously admitted playing participant in the same session");
        State(1, Play); State(3, null);
        foreach (var bad in new[] { End with { MatchId = "9765432112" }, End with { Team = "dire" },
            End with { Hero = "npc_dota_hero_axe" }, End with { Clock = 143 }, Play })
            check(Rejected(Receipt(bad)), "foreign participant, phase or regressing clock cannot be admitted as end");
        check(Rejected(Receipt(End, 1)), "an end cannot precede its own playing origin");
        check(Rejected(Receipt(End) with { Source = "dota2" }) && Rejected(Receipt(End) with { Source = "diagnostic" }),
            "video, metadata-only and diagnostic transports cannot substitute as a terminal origin");
        var old = Receipt(End with { AgeMs = 2750 }); now += 250;
        check(Rejected(old) && box.TakePostgame() == null, "coach includes its own transport/challenge delay at the exact expiry boundary");
        var accepted = Receipt(End with { AgeMs = 1000 }); now += 10;
        box.AcceptPostgame(accepted);
        check(Rejected(accepted), "one-use challenge is not a replay token");
        var view = box.TakePostgame();
        check(view is { Video: null, Jpeg.Length: 0, Meta: { Sequence: 2, SourceWidth: 0, Player: null, MatchPhase.Clock: 145 } } &&
            view.CapturedNoEarlierThan == now - 10 && box.Latest()?.Meta is { Sequence: 3, MatchPhase: null } &&
            box.Snapshot() is { Frames: 2, Bytes: 200 }, "an out-of-order terminal event is never a live frame, image, or new presence");
        box.AcceptPostgame(Receipt(End with { AgeMs = 1100 }));
        check(box.TakePostgame() == null && Rejected(Receipt(End with { Clock = 146 })) && Rejected(Receipt(End, 4)),
            "lost response retry acknowledges only the identical event; consumption, new challenges and heartbeat sequences cannot republish it");
        State(4, End);
        check(box.TakePostgame() == null && box.Latest()?.Meta.MatchPhase == null, "a following legacy frame cannot relatch an already admitted end");
        State(5, Play);
        check(box.Latest()?.Meta.MatchPhase == null, "older playing metadata arriving after admission cannot restore live authority");
        now += MatchWitness.MaxAgeMs;
        check(Rejected(Receipt(End with { AgeMs = 0 })), "a new challenge and apparent zero age cannot acknowledge an expired original receipt");
        var foreign = Receipt(End); box.End(session); session = box.Begin().SessionId;
        check(Rejected(foreign) && box.TakePostgame(session) == null, "new session rejects the old receipt and challenge");
    }

    private sealed class Raw(GsiReceiver receiver, string token) : IDisposable
    {
        private readonly HttpClient client = new(new HttpClientHandler { UseProxy = false }) {
            BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
        private Dictionary<string, object> Packet(string? phase, long unix)
        {
            var data = new Dictionary<string, object> { ["auth"] = new { token } };
            if (phase != null) {
                data["provider"] = new { name = "Dota 2", appid = 570, timestamp = unix };
                data["map"] = new { name = "dota", matchid = Match, game_state = "DOTA_GAMERULES_STATE_" + phase,
                    clock_time = phase == "POST_GAME" ? End.Clock : Play.Clock, paused = false };
                data["player"] = new { team_name = "radiant", activity = "playing", player_slot = 0, team_slot = 0 };
                if (phase != "POST_GAME") data["hero"] = new { name = Hero, alive = false, health = 0, mana = 80 };
            }
            return data;
        }
        public async Task Post(string? phase, long unix)
        {
            using var response = await client.PostAsJsonAsync("/gsi", Packet(phase, unix));
            if (phase != null) response.EnsureSuccessStatusCode();
        }
        public async Task RebindDuringTerminalBody(long unix, Func<Task> rebind)
        {
            // 100-continue is issued by Kestrel on its body read, AFTER BeginRequest captured the
            // recipient. No race-prone sleep or production test hook to fake a request in flight.
            using var timeout = new CancellationTokenSource(2500);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, receiver.Port, timeout.Token);
            using var socket = tcp.GetStream();
            using var head = new StreamReader(socket, Encoding.ASCII, false, 1024, leaveOpen: true);
            var body = JsonSerializer.SerializeToUtf8Bytes(Packet("POST_GAME", unix));
            await socket.WriteAsync(Encoding.ASCII.GetBytes($"POST /gsi HTTP/1.1\r\nHost: 127.0.0.1:{receiver.Port}\r\n" +
                $"Content-Type: application/json\r\nContent-Length: {body.Length}\r\nExpect: 100-continue\r\nConnection: close\r\n\r\n"), timeout.Token);
            if (await head.ReadLineAsync(timeout.Token) != "HTTP/1.1 100 Continue" || await head.ReadLineAsync(timeout.Token) != "")
                throw new Exception("GSI handler did not start the held body");
            await rebind();
            await socket.WriteAsync(body, timeout.Token);
            if (await head.ReadLineAsync(timeout.Token) != "HTTP/1.1 200 OK") throw new Exception("Authenticated terminal was not verified");
        }
        public void Dispose() => client.Dispose();
    }
    private static async Task Upload(GameLink link, long sequence, MatchWitness? phase)
    {
        var t = await link.GetTicket(default);
        await link.UploadMinuteState(new(t.SessionId, t.Id, sequence, "dota2-gsi", 0, 0, null, null, phase), Environment.TickCount64, default);
    }
    private static async Task<FrameView?> Take(CoachMailbox box, string id)
    {
        long until = Environment.TickCount64 + 1800;
        do {
            if (box.TakePostgame(id) is { } receipt) return receipt;
            await Task.Delay(10);
        } while (Environment.TickCount64 < until);
        return null;
    }
    private static async Task DelayedAdmission(Action<bool, string> check)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        string secret = Wire.Secret();
        await using var receiver = await GsiReceiver.Start(0, resumeToken: secret);
        using var raw = new Raw(receiver, secret);
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        await link.Connect(default); string id = link.SessionId!; long seq = 0;
        link.BindPostgame(receiver, "dota2-gsi", () => Interlocked.Increment(ref seq));
        long unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await raw.Post("GAME_IN_PROGRESS", unix);
        var playing = receiver.LatestMatch()!;
        long playingSeq = Interlocked.Increment(ref seq);
        await raw.Post("POST_GAME", unix);
        long observed = Environment.TickCount64; int originalAge = receiver.LatestMatch()!.AgeMs;
        await Task.Delay(200); await raw.Post(null, unix);
        check(receiver.LatestMatch() == null && host.Mailbox.Latest() == null && host.Mailbox.TakePostgame() == null,
            "verified postgame then menu 200 ms later before ANY upload: no current witness and no coach receipt yet");
        // No playing frame has yet been admitted: the first end POST cannot be acknowledged. Allow the
        // original in-flight playing frame through afterwards; the same receipt must still reach the coach.
        using var stop = new CancellationTokenSource();
        var delivery = link.DeliverPostgames(stop.Token);
        try {
            await Task.Delay(220);
            check(host.Mailbox.TakePostgame() == null, "an unadmitted POST is not a successful network acknowledgement");
            await Upload(link, playingSeq, playing with { AgeMs = playing.AgeMs + (int)(Environment.TickCount64 - observed) });
            await Upload(link, Interlocked.Increment(ref seq), null);
            var view = await Take(host.Mailbox, id);
            check(view is { Video: null, Jpeg.Length: 0, Meta: { Sequence: 2, Source: "dota2-gsi", SourceWidth: 0, Player: null,
                    MatchPhase: { MatchId: Match, Team: "radiant", Hero: Hero, Clock: 145, Phase: "postgame" } } } &&
                view.Meta.SessionId == id && view.Meta.MatchPhase.AgeMs >= originalAge + 180 && host.Mailbox.Latest()?.Meta is { Sequence: 3, MatchPhase: null },
                "pinned HTTPS admits the original game receipt after menu, charges source age, and never overwrites the new live heartbeat");
            await Task.Delay(150);
            check(host.Mailbox.TakePostgame(id) == null, "successful admission stops retries; coach receives no second end");
        } finally { stop.Cancel(); await delivery; await link.End(default); }
    }
    private static async Task Repaired(Action<bool, string> check, bool inFlight)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        string secret = Wire.Secret();
        await using var receiver = await GsiReceiver.Start(0, resumeToken: secret);
        using var raw = new Raw(receiver, secret);
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        long seq = 0, unix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        await link.Connect(default); string old = link.SessionId!;
        link.BindPostgame(receiver, "dota2-gsi", () => Interlocked.Increment(ref seq));
        await raw.Post("GAME_IN_PROGRESS", unix);
        await Upload(link, Interlocked.Increment(ref seq), receiver.LatestMatch());
        async Task Rebind() {
            await link.End(default); await link.Connect(default);
            link.BindPostgame(receiver, "dota2-gsi", () => Interlocked.Increment(ref seq));
        }
        if (inFlight) await raw.RebindDuringTerminalBody(unix, Rebind);
        else { await raw.Post("POST_GAME", unix); await Rebind(); } // Already verified when the new pairing starts.
        string next = link.SessionId!;
        // Deliberately grant EVEN a matching playing origin at the new coach: local pairing/ingress scope,
        // not just a missing coach frame, must prevent this still-fresh terminal from being rebound.
        await Upload(link, 1, Play);
        await raw.Post("POST_GAME", unix);
        using var stop = new CancellationTokenSource();
        var delivery = link.DeliverPostgames(stop.Token);
        try {
            await Task.Delay(250);
            check(next != old && receiver.LatestMatch()?.Phase == "postgame" && host.Mailbox.TakePostgame() == null &&
                host.Mailbox.Latest()?.Meta.MatchPhase?.Phase == "playing", inFlight ?
                    "re-pair during the original raw body read: neither its eventual admission nor a later duplicate binds the new pairing" :
                    "disconnect/re-pair while end is current cannot rebind a held receipt or its repeated GSI heartbeat");
        } finally { stop.Cancel(); await delivery; await link.End(default); }
    }
}
