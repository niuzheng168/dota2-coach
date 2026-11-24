using System.Net;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

/// <summary>The game machine's enemy panel: the coach's last-known rows survive scanner and packet gaps, the mailbox
/// relays them per session, and the game client shows each reply until the next one.</summary>
internal static class AuthorizedBoardTests
{
    private const string Match = "9034567891", Me = "npc_dota_hero_axe", Enemy = "npc_dota_hero_lion";
    private static readonly MinimapLayout Map = new(1920, 1080, 1650, 800, 250);
    private static readonly MatchWitness Playing = new(Match, "dire", Me, 601, "playing", false);
    private static readonly OwnState Dead = new(Match, Me, "dire", 601, false, 0, 80, null, null, [], []);
    private static WardMark Estimate => new(Enemy, "ward", .3, .4, 1, .25, 20, 24, Rough: true);
    private static EnemyBoard Board() => new([new(Enemy, 11, [null, "item_blink", null, null, null, null], null, 12)],
        [Estimate], Match, "dire", Map, [new("observer", "radiant", 500, -900)]);

    public static async Task Run(Action<bool, string> check)
    {
        // The user's rule since v0.6.2: the released game client sends no pictures. Only UI tests turn this on.
        check(!Features.VideoUpload, "the game client's picture modes are off unless a developer test switches them on");
        Shape(check);
        KnownBoard(check);
        Mailbox(check);
        await RoundTrip(check);
        await PostgameDeliveryTests.Run(check);
    }

    private static void Shape(Action<bool, string> check)
    {
        var b = Board(); var guess = Estimate; var sight = b.SeenWards![0];
        check(Wire.ValidBoard(new([], [guess])) && Wire.ValidBoard(new([], null, Match, "dire", Map, [sight])) &&
            Wire.ValidBoard(b), "v0.4.8 candidates and independent sighting/equipment subsets have valid shapes");
        foreach (var bad in new[] { b with { Team = null }, b with { MatchId = "043" }, b with { MatchId = "0" },
            b with { Minimap = Map with { Top = double.NaN } }, b with { Wards = [guess with { X = double.NaN }] },
            b with { Wards = [guess with { Y = 1.1 }] }, b with { Wards = [guess with { Rank = 3 }] },
            b with { Wards = [guess with { Share = -.1 }] }, b with { Wards = [guess with { Kind = "visible" }] },
            b with { Wards = [guess with { Left = 421 }] }, b with { Wards = [guess with { Hero = Enemy + "\n" }] },
            b with { Wards = Enumerable.Repeat(guess, Wire.MaxWardMarks + 1).ToArray() },
            b with { SeenWards = [sight with { X = double.PositiveInfinity }] }, b with { SeenWards = [sight with { Team = "both" }] },
            b with { SeenWards = [sight with { Kind = "ward" }] }, b with { SeenWards = Enumerable.Repeat(sight, 65).ToArray() },
            b with { Heroes = [b.Heroes[0] with { Age = null }] }, b with { Heroes = [b.Heroes[0], b.Heroes[0]] },
            b with { Heroes = [b.Heroes[0] with { Stats = new(161, 82, 272, 24, 33, 429, null, null, null, null) }] },
            b with { Heroes = [b.Heroes[0] with { Stats = new(161, 82, 272, 24, 33, 2000, null, null, null, 1) }] },
            b with { Fog = [new("npc_dota_creep_badguys_melee", 0, 0, 0, 1)] }, b with { Fog = [new(Enemy, 0, 0, 360, 1)] },
            b with { Fog = [new(Enemy, 0, 0, 0, FogTracker.MaxSeconds + 1)] }, b with { Fog = [new(Enemy, 0, 0, 0, 1), new(Enemy, 9, 9, 0, 2)] },
            b with { MatchId = null, Team = null, Minimap = null, SeenWards = null, Fog = [new(Enemy, 0, 0, 0, 1)] } })
            check(!Wire.ValidBoard(bad), "invalid board scope, rows, candidate, sighting, stats or fog mark rejected");

        string json = JsonSerializer.Serialize(new { enemies = b with {
            SessionId = Guid.NewGuid().ToString("N"), FrameSequence = 5, RemainingMs = 2100 } }, Wire.Json);
        EnemyBoard? Read(string s) => BoardDelivery.Read(Encoding.UTF8.GetBytes(s));
        check(Read(json) is { Wards: [{ Rough: true }], SeenWards: [{ X: 500, Y: -900 }] }, "projected wire format keeps evidence types distinct");
        foreach (var bad in new[] {
            json.Replace("\"x\":0.3,", ""), json.Replace("\"y\":0.4", "\"y\":\"0.4\""),
            json.Replace("\"rank\":1", "\"rank\":0,\"rank\":1"), json.Replace("\"rough\":true", "\"rough\":true,\"track\":[]"),
            json.Replace("\"level\":11", "\"level\":11,\"wards\":[{}]"), json.Replace("\"team\":\"dire\"", "\"team\":null"),
            json.Replace("\"sessionId\":\"", "\"sessionId\":\"x") })
            check(bad != json && Read(bad) == null, "incomplete/ambiguous/unfiltered board envelope rejected at decode");
        check(Read("""{"enemies":{"heroes":[]}}""") == null && Read(json.Replace(",\"rough\":true", ""))!.Wards![0].Rough == false,
            "unbound legacy board cannot authorize display; original optional Rough is still optional");
    }

    private static void KnownBoard(Action<bool, string> check)
    {
        const string Sven = "npc_dota_hero_sven", Pudge = "npc_dota_hero_pudge";
        string?[] blink = ["item_blink", null, null, null, null, null], bkb = ["item_blink", "item_black_king_bar", null, null, null, null];
        long now = 1_000;
        var board = new LastKnownBoard();
        check(board.Board(now, true, true, null) == null, "nothing is published before the paired player names a match");
        board.Participant(Match, "dire", 600, now);
        board.Equipment(Match, 600.4, [new(Enemy, 11, blink, null, 2.4), new(Sven, null, null, null, null)]);
        check(board.Board(now, true, true, null) is { MatchId: Match, Team: "dire",
                Heroes: [{ Hero: Enemy, Level: 11, Age: > 2.39 and < 2.41 }, { Hero: Sven, Level: null, Age: null }] },
            "rows keep the scanner's player order and age; a never-read enemy is listed without an age");
        // A stale or missing scanner write is simply no update: the reading stays and ages on the player's clock.
        board.Participant(Match, "dire", 640, now += 10_000);
        check(board.Board(now, true, true, null) is { Heroes: [{ Level: 11, Items: ["item_blink", ..], Age: > 41.99 and < 42.01 }, _] },
            "without new scans the last reading stays on the panel and its age grows");
        // A restarted scanner lists the hero again before its next item visit: that never erases known items.
        board.Equipment(Match, 640.2, [new(Enemy, 12, null, null, 0.2), new(Sven, null, null, null, null)]);
        check(board.Board(now, true, true, null) is { Heroes: [{ Level: 11, Items: ["item_blink", ..], Age: >= 42 and < 42.5 }, { Age: null }] },
            "a level-only read after a scanner restart doesn't replace a full reading");
        board.Equipment(Match, 641, [new(Enemy, 12, bkb, null, 0.5), new(Sven, 9, blink, null, 1)]);
        check(board.Board(now, true, true, null) is { Heroes: [{ Level: 12, Items: [_, "item_black_king_bar", ..] }, { Hero: Sven, Level: 9 }] },
            "a newer full reading replaces the old one");
        board.Equipment(Match, 641.5, [new(Enemy, 10, blink, null, 30)]);
        check(board.Board(now, true, true, null) is { Heroes: [{ Level: 12, Items: [_, "item_black_king_bar", ..] }, { Hero: Sven, Level: 9 }] },
            "an older reading cannot overwrite a newer one; a hero a write leaves out keeps its row");
        var read = new EnemyStats(70, 30, 180, 12, 25, 340, 30, 40, 20, .5);
        board.Equipment(Match, 641.6, [new(Enemy, 12, bkb, null, 0.6) { Stats = read }]);
        board.Equipment(Match, 641.8, [new(Enemy, 12, null, null, 0.1)]);
        board.Equipment(Match, 641.9, [new(Enemy, 12, bkb, null, 0.3) { Stats = read with { Armor = 2, Age = 30 } }]);
        check(board.Board(now, true, true, null) is { Heroes: [{ Stats: { Armor: 12, Damage: 70, Age: > .79 and < .81 } }, { Stats: null }] },
            "stats keep their own reading: a write without them, or an older reading of them, leaves the last one in place");
        check(board.Board(now, true, true, null) is { Heroes: [{ Hero: Enemy }, { Hero: Sven }] }, "a scan of another match is ignored");
        check(board.Board(now, false, false, null) == null && board.Board(now, true, false, null) is { Wards: null, SeenWards: null },
            "the coach's toggles select equipment and wards independently");
        board.Estimates(Match, [Estimate with { Left = 5 }, Estimate with { Rank = 2, Left = 60 }], 641);
        board.Seen(Match, [new("observer", "radiant", 500, -900)], now);
        board.Participant(Match, "dire", 650, now += 1_000);
        check(board.Board(now, true, true, Map) is { Wards: [{ Rank: 2, Age: 29, Left: 51 }], SeenWards.Length: 1, Minimap: var m } && m == Map,
            "ward guesses age and expire on their own lifetime; sightings and the confirmed box travel along");
        check(board.Board(now + LastKnownBoard.SeenWardsMs, true, true, null) is { SeenWards: null },
            "a sighting is only shown while Plus keeps reporting it");
        check(board.Board(now + LastKnownBoard.ParticipantMs, true, true, null) == null,
            "without the paired player naming the match (menu, abandoned game) nothing is published");
        board.Participant(Match, "radiant", 651, now);
        check(board.Read == 0 && board.Board(now, true, true, null) == null, "another team or match starts an empty panel");
        board.Equipment(Match, 651, [new(Enemy, 11, blink, null, 1)]);
        board.Clear();
        check(board.Match == null && board.Board(now, true, true, null) == null, "the end of the match clears the panel");
    }

    private static void Mailbox(Action<bool, string> check)
    {
        long now = 20_000, seq = 0;
        var box = new CoachMailbox(() => now);
        var b = Board();
        box.SetEnemies(b);
        string id = box.Begin().SessionId;
        check(box.Enemies(id) == null, "a panel set before the game machine connected is not handed to its session");
        void Send(MatchWitness? w, OwnState? player = null, int width = 1920, int height = 1080, int leaseMs = 0) {
            var t = box.Issue(id); now += leaseMs;
            box.AcceptMinuteState(new(id, t.Id, ++seq, "dota2-gsi", width, height, player, player == null ? null : 0, w), 300);
        }
        Send(null, Dead);
        box.SetEnemies(b);
        now += 500;
        var delivered = box.Enemies(id)!;
        check(delivered is { Heroes: [{ Age: 12.5, Items: [null, "item_blink", null, null, null, null] }], Wards: [{ Age: 20.5, Left: 23.5 }],
            SeenWards: [{ Team: "radiant" }], HeldMs: 500 } && delivered.SessionId == id && delivered.Minimap == Map,
            "the coach's panel is relayed whatever the player's own GSI shows (dead, stale), aged while held");
        delivered.Heroes[0].Items![1] = null; b.Heroes[0].Items![1] = "item_manta";
        check(box.Enemies(id)!.Heroes[0].Items![1] == "item_blink", "neither the coach's nor a poll's arrays alias the publication");
        box.SetEnemies(Board()); now += Wire.BoardLifetimeMs - 1;
        check(box.Enemies(id) != null, "a panel the coach renews stays up");
        now += 1;
        check(box.Enemies(id) == null, "a panel the coach stops renewing (stalled or closed) disappears");
        box.SetEnemies(Board());
        box.SetEnemies(Board() with { Heroes = [], Wards = [], SeenWards = [] });
        check(box.Enemies(id) == null, "an empty panel withdraws it");
        box.SetEnemies(Board()); box.SetEnemies(null);
        check(box.Enemies(id) == null, "null withdraws it");
        box.SetEnemies(Board());
        Send(Playing, Dead);
        Send(Playing with { Phase = "postgame", Clock = 605 }, width: 0, height: 0); Send(null);
        var end = box.TakePostgame(id);
        check(end?.Meta.MatchPhase is { Phase: "postgame", Clock: 605 } && end.Meta.Sequence == seq - 1 &&
            end.Meta.SessionId == id && end.Jpeg.Length == 0 && box.Latest()?.Meta.MatchPhase == null && box.TakePostgame(id) == null,
            "one local terminal receipt survives the following unavailable sample without retaining pixels");
        Send(null); box.End(id);
        check(box.TakePostgame(id) == null, "neither missing GSI nor disconnect manufactures a terminal receipt");

        // A short final video frame can be followed by a menu, WebSocket detach, and even another pairing
        // before the next UI tick. Keep just the already authenticated terminal metadata on the local UI side.
        var pixels = new VideoPixels(32, 18, new byte[32 * 18 * 3 / 2]);
        box.VideoJpegEncoder = _ => throw new Exception("terminal handling must not read an image");
        void TerminalVideo() {
            id = box.Begin().SessionId; box.AttachVideo(id, "fixture");
            var t = box.IssueVideoLease(id);
            box.AcceptVideo(new(id, t.Id, 1, "dota2", 1920, 1080, Dead, 0, Playing), "fixture", pixels, 32);
            box.AcceptVideo(new(id, t.Id, 2, "dota2", 1920, 1080, null, null, Playing with { Phase = "postgame" }), "fixture", pixels, 32);
            box.AcceptVideo(new(id, t.Id, 3, "dota2", 1920, 1080, null, null), "fixture", pixels, 32);
            box.DetachVideo(id, "fixture");
        }
        TerminalVideo();
        check(box.Latest() == null && box.TakePostgame("not-the-session") == null &&
            box.TakePostgame() is { Video: null, Meta: { Sequence: 2, MatchPhase.Phase: "postgame" } } ended && ended.Meta.SessionId == id,
            "normal video-to-menu/detach keeps exactly the witnessed terminal, never a final image or a fabricated end");
        TerminalVideo(); string endedId = id; var replacement = box.Begin();
        check(box.TakePostgame(replacement.SessionId) == null && box.TakePostgame()?.Meta.SessionId == endedId,
            "a fast reconnect cannot relabel the previous session's pending terminal");
        TerminalVideo(); now += MatchWitness.MaxAgeMs;
        check(box.TakePostgame() == null, "a detached terminal cannot outlive the original GSI freshness bound");
        TerminalVideo(); box.Clear(); check(box.TakePostgame() == null, "explicit revocation erases even a fresh local terminal");
    }

    private static async Task RoundTrip(Action<bool, string> check)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        long now = 5_000_000, seq = 0;
        using var link = new GameLink(host.Pair(IPAddress.Loopback), () => now);
        await link.Connect(default);
        async Task Send(MatchWitness? w, OwnState? own) {
            var t = await link.GetTicket(default);
            await link.UploadMinuteState(new(t.SessionId, t.Id, ++seq, "dota2-gsi", 1920, 1080, own, own == null ? null : 0, w), now, default);
        }
        await Send(Playing, Dead);
        var b = Board() with { Wards = [Estimate with { Left = .4 }, Estimate with { Rank = 2 }] };
        var dots = new LaneMap742(601,[new("top",3,"fog",-6230,4920,350,599,-6240,5550), new("mid",0,"baseline",-179,-102,900,Spawn:600,Meeting:619)],1600);
        host.Mailbox.SetEnemies(b with { LaneMap = dots });
        var waveRead = (await link.Enemies(default))!;
        check(link.DisplayEnemies(waveRead,Playing,1920,1080)?.LaneMap?.Marks[0] is { Kind:"fog", SeenAt:599 } &&
            BoardDelivery.Age(waveRead,1600,2000) is { LaneMap:null, Heroes.Length:1 } &&
            link.DisplayEnemies(waveRead,Playing with { InstalledBuild=9999 },1920,1080) is { LaneMap:null, Heroes.Length:1, SeenWards.Length:1 },
            "real TLS wave layer is independently leased; an incompatible build strips wave estimates, not equipment or existing ward marks");
        var pairing = host.Pair(IPAddress.Loopback);
        using (var old = new HttpClient(new HttpClientHandler { UseProxy=false, ServerCertificateCustomValidationCallback=(_,cert,_,_) =>
            cert?.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256).Equals(pairing.Fingerprint,StringComparison.OrdinalIgnoreCase)==true }) {
            BaseAddress=new Uri(pairing.Address) }) {
            old.DefaultRequestHeaders.Authorization=new("Bearer",pairing.Token); old.DefaultRequestHeaders.Add("X-Session",link.SessionId);
            string legacy = await old.GetStringAsync("/v1/enemies");
            check(!legacy.Contains("laneMap") && BoardDelivery.Read(Encoding.UTF8.GetBytes(legacy)) is { Heroes.Length:1 },
                "the legacy endpoint omits even the optional key so old strict game clients still receive their board");
            // Since 2026-11-24: enemy stats and fog marks only for a game client that asks for them; v0.6.4 still gets its board.
            var stats = new EnemyStats(161, 82, 272, 24, 33, 429, 92, 132, 78, 1.5);
            host.Mailbox.SetEnemies(b with { Heroes = [b.Heroes[0] with { Stats = stats }], Fog = [new(Enemy, 1200, -300, 90, 3.2)] });
            string v064 = await old.GetStringAsync("/v1/enemies?waves=1");
            check(await link.Enemies(default) is { Heroes: [{ Stats: { Damage: 161, Armor: 24, MoveSpeed: 429, Age: >= 1.5 } }], Fog: [{ Yaw: 90, Seconds: >= 3.2 }] } &&
                  !v064.Contains("stats") && !v064.Contains("fog") && BoardDelivery.Read(Encoding.UTF8.GetBytes(v064)) is { Heroes: [{ Level: 11 }] },
                "stats and fog reach a client that asks; an older strict client gets the same board without them");
            host.Mailbox.SetEnemies(b with { Heroes = [], Wards = null, SeenWards = null, Fog = [new(Enemy, 1200, -300, null, 12)] });
            check(await link.Enemies(default) is { Fog: [{ Yaw: null }], Heroes: [] } &&
                  JsonDocument.Parse(await old.GetStringAsync("/v1/enemies?waves=1")).RootElement.GetProperty("enemies").ValueKind == JsonValueKind.Null,
                "a fog-only board is no board for an older client");
            host.Mailbox.SetEnemies(b with { Heroes=[],Wards=null,SeenWards=null,LaneMap=dots });
            var waveOnly = await link.Enemies(default);
            check(waveOnly is { LaneMap:not null, Heroes.Length:0 } &&
                JsonDocument.Parse(await old.GetStringAsync("/v1/enemies")).RootElement.GetProperty("enemies").ValueKind==JsonValueKind.Null,
                "wave-only downlink with wards/panel off; no phantom legacy board");
            now += 2000;
            check(link.DisplayEnemies(waveOnly!, Playing, 1920, 1080) == null,
                "a stalled game poll cannot keep drawing an expired wave receipt");
        }
        host.Mailbox.SetEnemies(b);
        check(await link.Enemies(default) is { Wards.Length: 2, SeenWards.Length: 1, Heroes.Length: 1 } read && read.SessionId == link.SessionId &&
            await link.Poll(default) == null, "real pinned-TLS GSI-only link receives the panel without a minute route or a living hero");
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 32, 0, 0, 0, 32, .. new byte[12]];
        host.Mailbox.Icons = _ => png;
        check(await link.Icon("item_blink", default) != null && await link.Icon("item_manta", default) == null,
            "a paired client may fetch a published icon, not probe the scanner's other item files");
        await Send(null, null); await Send(Playing with { Paused = true }, Dead);
        check(await link.Enemies(default) is { Heroes.Length: 1 }, "missing own GSI or a pause between polls doesn't blank the panel");
        // Plus alerts are markerless text and reach the GSI-only link; anything with a map point needs a picture or a minute route.
        await Send(Playing, Dead with { Alive = true, Health = 500 });
        host.Mailbox.SetReminder(link.SessionId!, seq, "local-vision-observation", "剃刀 离开视野", null, displayMs: 3000);
        check(await link.Poll(default) is { Kind: "local-vision-observation", Message: "剃刀 离开视野", Marker: null },
            "a Plus alert subtitle reaches the GSI-only game client");
        bool refused = false;
        try { host.Mailbox.SetReminder(link.SessionId!, seq, "manual-link-test", "带点", new(.1, .9)); } catch (InvalidOperationException) { refused = true; }
        check(refused, "a map point is not accepted without a picture behind it");
        host.Mailbox.SetEnemies(b with { Heroes = [], Wards = [] });
        check(await link.Enemies(default) is { Heroes: [], Wards: [], SeenWards.Length: 1 } && await link.Icon("item_blink", default) == null,
            "a sightings-only panel never carries inventory or icons");
        string first = link.SessionId!;
        await link.End(default); await link.Connect(default);
        check(link.SessionId != first && await link.Enemies(default) == null, "a new session starts without the previous session's panel");
    }
}
