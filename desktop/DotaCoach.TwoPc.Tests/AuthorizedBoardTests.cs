using System.Net;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

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
        Shape(check);
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
            b with { Heroes = [b.Heroes[0] with { Age = null }] }, b with { Heroes = [b.Heroes[0], b.Heroes[0]] } })
            check(!Wire.ValidBoard(bad), "invalid board scope, rows, candidate or sighting rejected");

        string json = JsonSerializer.Serialize(new { enemies = b with {
            SessionId = Guid.NewGuid().ToString("N"), FrameSequence = 5, RemainingMs = 2100 } }, Wire.Json);
        EnemyBoard? Read(string s) => BoardDelivery.Read(Encoding.UTF8.GetBytes(s));
        check(Read(json) is { Wards: [{ Rough: true }], SeenWards: [{ X: 500, Y: -900 }] }, "projected wire format keeps evidence types distinct");
        foreach (var bad in new[] {
            json.Replace("\"x\":0.3,", ""), json.Replace("\"y\":0.4", "\"y\":\"0.4\""),
            json.Replace("\"rank\":1", "\"rank\":0,\"rank\":1"), json.Replace("\"rough\":true", "\"rough\":true,\"track\":[]"),
            json.Replace("\"level\":11", "\"level\":11,\"wards\":[{}]"), json.Replace("\"team\":\"dire\"", "\"team\":null"),
            json.Replace("\"remainingMs\":2100", "\"remainingMs\":0"), json.Replace("\"frameSequence\":5", "\"frameSequence\":0") })
            check(bad != json && Read(bad) == null, "incomplete/ambiguous/unfiltered board envelope rejected at decode");
        check(Read("""{"enemies":{"heroes":[]}}""") == null && Read(json.Replace(",\"rough\":true", ""))!.Wards![0].Rough == false,
            "unbound legacy board cannot authorize display; original optional Rough is still optional");
    }

    private static void Mailbox(Action<bool, string> check)
    {
        long now = 20_000, seq = 0;
        var box = new CoachMailbox(() => now); string id = box.Begin().SessionId;
        void Send(MatchWitness? w, OwnState? player = null, int width = 1920, int height = 1080, int leaseMs = 0) {
            var t = box.Issue(id); now += leaseMs;
            box.AcceptMinuteState(new(id, t.Id, ++seq, "dota2-gsi", width, height, player, player == null ? null : 0, w), 300);
        }
        var b = Board();
        Send(null, Dead); box.SetEnemies(b);
        check(box.Enemies(id) == null, "legacy own state without a fresh witness never authorizes a board");
        Send(Playing, Dead);
        long previousDead = seq; Send(Playing, Dead);
        box.SetEnemies(b with { SessionId = id, FrameSequence = previousDead });
        check(box.Enemies(id)?.FrameSequence == previousDead, "a valid participant projection can race another dead frame without depending on route history");
        box.SetEnemies(b with { SessionId = id, FrameSequence = seq, Wards = null, SeenWards = null });
        box.SetEnemies(b with { SessionId = id, FrameSequence = previousDead });
        check(box.Enemies(id) is { Wards: null, SeenWards: null } newest && newest.FrameSequence == seq,
            "an out-of-order projection cannot replace a newer equipment-only selection");
        box.SetEnemies(b with { MatchId = null, Team = null }); check(box.Enemies(id) == null, "no synthetic binding to current GSI");
        foreach (var wrong in new[] { b with { MatchId = "1234" }, b with { Team = "radiant" }, b with { Heroes = [b.Heroes[0] with { Hero = Me }] } }) {
            box.SetEnemies(wrong); check(box.Enemies(id) == null, "wrong match/team or own hero is not an enemy board");
        }
        box.SetEnemies(b with { RemainingMs = 1900, Wards = [Estimate, Estimate with { Rank = 2, Left = .2 }] });
        b.Heroes[0].Items![1] = "item_manta";
        now += 500;
        var delivered = box.Enemies(id)!;
        check(delivered is { Heroes: [{ Age: 12.5, Items: [null, "item_blink", null, null, null, null] }],
            Wards: [{ Age: 20.5, Left: 23.5, Rank: 1 }], SeenWards: [{ Team: "radiant" }], RemainingMs: 1400 } &&
            delivered.SessionId == id && delivered.FrameSequence == seq && delivered.Minimap == Map,
            "dead participant: frozen item slots, scoped source, original lease, last-read age and individual ward expiry");
        delivered.Heroes[0].Items![1] = null;
        check(box.Enemies(id)!.Heroes[0].Items![1] == "item_blink", "mutating a poll cannot mutate the publication");
        now += 1400; check(box.Enemies(id) == null, "the exact shorter source lease expires even with fresh GSI");
        foreach (var geometry in new MinimapLayout?[] { null, MinimapLayout.Suggest(1280, 720) }) {
            box.SetEnemies(b with { Minimap = geometry });
            check(box.Enemies(id) is { Heroes.Length: 1, Wards: null, SeenWards: null, Minimap: null },
                "missing/mismatched calibration strips both ward subsets, not equipment");
        }
        Send(Playing, Dead with { Farm = new(true, true, false, InstalledBuild: 6990) });
        Send(Playing, Dead); box.SetEnemies(b);
        check(box.Enemies(id) is { Heroes.Length: 1, Wards: null, SeenWards: null }, "missing telemetry cannot turn an observed incompatible HUD build into a valid one");
        Send(Playing, Dead with { Farm = new(true, true, false, InstalledBuild: 6989) });
        Send(Playing with { InstalledBuild = 6990 }, Dead); box.SetEnemies(b);
        check(box.Enemies(id) is { Heroes.Length: 1, Wards: null, SeenWards: null },
            "independent install evidence fences an unsupported map even while farming telemetry is unavailable");
        Send(Playing with { InstalledBuild = 6989 }, Dead); box.SetEnemies(b);
        check(box.Enemies(id) is { Heroes.Length: 1, Wards.Length: 1, SeenWards.Length: 1 },
            "verified standard install and HUD support the ward view without a farming-ready hero");
        box.SetEnemies(b with { Heroes = [] });
        check(box.Enemies(id) is { Heroes.Length: 0, Wards.Length: 1, SeenWards.Length: 1 }, "ward-only has no equipment rows");
        box.SetEnemies(b with { Heroes = [], Wards = [], SeenWards = [] });
        check(box.Enemies(id) == null, "an empty projection explicitly withdraws every subset");
        box.SetEnemies(b);
        Send(Playing with { Paused = true }, Dead); Send(Playing, Dead);
        check(box.Enemies(id) == null, "pause/resume does not revive a prior publication");
        box.SetEnemies(b with { SessionId = id, FrameSequence = delivered.FrameSequence });
        check(box.Enemies(id) == null, "a projection computed before the scope fence cannot be rebound");
        Send(Playing with { AgeMs = 2500 }, Dead, leaseMs: 501); box.SetEnemies(b);
        check(box.Latest()?.Meta.MatchPhase == null && box.Enemies(id) == null, "GSI age includes LAN lease, not just time since arrival");
        Send(Playing, Dead, 0, 0); box.SetEnemies(b);
        check(box.Latest()?.Meta is { SourceWidth: 0, SourceHeight: 0, Player.Alive: false } && box.Enemies(id) == null,
            "missing viewport is explicit, preserves real state and cannot authorize any overlay");
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
        async Task Send(MatchWitness w) {
            var t = await link.GetTicket(default);
            await link.UploadMinuteState(new(t.SessionId, t.Id, ++seq, "dota2-gsi", 1920, 1080, Dead with { Clock = w.Clock }, 0, w), now, default);
        }
        await Send(Playing);
        var b = Board() with { Wards = [Estimate with { Left = .4 }, Estimate with { Rank = 2 }] };
        host.Mailbox.SetEnemies(b);
        var read = (await link.Enemies(default))!;
        check(link.DisplayEnemies(read, Playing, 1920, 1080) is { Wards.Length: 2, SeenWards.Length: 1, Heroes.Length: 1 } &&
            await link.Poll(default) == null, "real pinned-TLS GSI-only board without a minute route or a living hero");
        byte[] png = [137, 80, 78, 71, 13, 10, 26, 10, 0, 0, 0, 13, 73, 72, 68, 82, 0, 0, 0, 32, 0, 0, 0, 32, .. new byte[12]];
        host.Mailbox.Icons = _ => png;
        check(await link.Icon("item_blink", default) != null && await link.Icon("item_manta", default) == null,
            "a paired client may fetch a published icon, not probe the scanner's other item files");
        now += 600;
        check(link.DisplayEnemies(read, Playing, 1920, 1080) is { Heroes: [{ Age: >= 12.6 }], Wards: [{ Rank: 2, Left: <= 23.4 }] },
            "last-read age advances and a short candidate disappears locally between polls");
        foreach (var wrong in new MatchWitness?[] { null, Playing with { MatchId = "332" }, Playing with { Team = "radiant" },
            Playing with { Hero = Enemy }, Playing with { AgeMs = 3001 }, Playing with { Phase = "postgame" }, Playing with { Paused = true } }) {
            check(link.DisplayEnemies(read, wrong, 1920, 1080) == null && link.DisplayEnemies(read, Playing, 1920, 1080) == null,
                "a local withdrawal cannot be undone by a cached render");
            await Send(Playing);
            check(await link.Enemies(default) == null, "the old source cannot reappear after a local withdrawal and resume");
            host.Mailbox.SetEnemies(b); read = (await link.Enemies(default))!;
        }
        check(link.DisplayEnemies(read, Playing, 1280, 720) == null, "viewport replacement withdraws the admitted board immediately");
        await Send(Playing);
        host.Mailbox.SetEnemies(b with { RemainingMs = 100 });
        read = (await link.Enemies(default))!;
        now += 101;
        check(link.DisplayEnemies(read, Playing, 1920, 1080) == null, "even fresh local GSI cannot extend the original publication deadline");
        host.Mailbox.SetEnemies(b);
        var ahead = Playing with { Clock = Playing.Clock + 1 }; await Send(ahead); // No advance on the fixture's wall clock.
        read = (await link.Enemies(default))!;
        check(link.DisplayEnemies(read, ahead, 1920, 1080) is { Heroes: [{ Age: >= 13 and < 13.1 }], Wards: [{ Rank: 2, Left: <= 23 and > 22.9 }] } &&
            link.DisplayEnemies(read, ahead with { Clock = ahead.Clock + 1 }, 1920, 1080) is { Heroes: [{ Age: >= 14 and < 14.1 }] },
            "source game-clock age/expiry is charged once across both PCs, and local GSI can advance it before any poll");
        host.Mailbox.SetEnemies(b with { Heroes = [], Wards = [] });
        check(await link.Enemies(default) is { Heroes: [], Wards: [], SeenWards.Length: 1 } && await link.Icon("item_blink", default) == null,
            "a sightings-only poll never carries inventory, icons, or predictions");
        await link.End(default);
        check(link.DisplayEnemies(read, Playing, 1920, 1080) == null, "disconnect invalidates the board independently of its timer");
    }
}
