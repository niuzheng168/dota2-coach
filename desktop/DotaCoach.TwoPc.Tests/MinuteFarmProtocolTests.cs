using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DotaCoach.TwoPc;

internal static class MinuteFarmProtocolTests
{
    private const string Kind = "minute-farm-route", Me = "76561198000000123";
    private static readonly MinimapLayout Layout = MinimapLayout.Suggest(1280, 720, right: true);
    private static readonly ScreenMark A = new(.94, .86), B = new(.96, .9);
    private static readonly byte[] Jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 2, 208, 5, 0, 3,
        1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1, 0xff, 0xd9];

    public static async Task Run(Action<bool, string> check)
    {
        Projection(check);
        Mailbox(check);
        await Ingress(check);
        await Paired(check);
    }

    private static JsonObject Packet() => new() {
        ["provider"] = new JsonObject { ["name"] = "Dota 2", ["appid"] = 570, ["version"] = 6989,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
        ["map"] = new JsonObject { ["name"] = "dota", ["matchid"] = "742000001", ["clock_time"] = 600,
            ["game_state"] = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", ["paused"] = false },
        ["player"] = new JsonObject { ["team_name"] = "radiant", ["activity"] = "playing", ["player_slot"] = 0,
            ["team_slot"] = 0, ["steamid"] = Me, ["last_hits"] = 82 },
        ["hero"] = new JsonObject { ["name"] = "npc_dota_hero_luna", ["alive"] = true, ["health"] = 900,
            ["mana"] = 400, ["xpos"] = 1100, ["ypos"] = -5400, ["level"] = 9 },
    };
    private static OwnState Parse(JsonObject p) => GsiState.Parse(JsonSerializer.SerializeToElement(p));
    private static bool Reject(Action act)
    {
        try { act(); return false; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return true; }
    }
    private static void Projection(Action<bool, string> check)
    {
        var p = Packet(); var own = Parse(p);
        check(MinuteFarmContext.Eligible(own) && own.Farm is { MoveSpeed: null, AttackDamage: null, AttackSpeed: null,
            Level: 9, InstalledBuild: null, LastHits: 82, MaxHealth: null, Silenced: null, Muted: null },
            "flat playing shape needs no invented combat stat, restriction, matching provider ID or GSI build");
        ((JsonObject)p["player"]!).Remove("activity"); p["provider"]!["steamid"] = Me;
        p["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        p["player"]!["team_name"] = "DOTA_TEAM_BADGUYS"; p["player"]!["player_slot"] = 7;
        p["hero"]!["movespeed"] = 372.5; p["hero"]!["attack_damage"] = 101; p["hero"]!["attack_speed"] = 145;
        p["hero"]!["max_health"] = 1100; p["hero"]!["max_mana"] = 525;
        p["items"] = JsonNode.Parse("""{"slot0":{"name":"item_blink"},"slot6":{"name":"item_bfury"},"stash0":{"name":"item_manta"}}""");
        p["minimap"] = JsonNode.Parse("""{"o0":{"unitname":"private-enemy"}}""");
        p["previously"] = JsonNode.Parse("""{"hero":{"xpos":9898},"items":{"slot0":{"name":"private-old-item"}}}""");
        p["auth"] = new JsonObject { ["token"] = "private-token" };
        var projected = Parse(p);
        string wire = JsonSerializer.Serialize(projected, Wire.Json);
        check(MinuteFarmContext.Eligible(projected) && projected.Team == "dire" && projected.X == 1100 &&
            projected.Farm is { MoveSpeed: 372.5, AttackDamage: 101, AttackSpeed: 145, MaxHealth: 1100, MaxMana: 525 } &&
            projected.Items.Select(i => i.Slot).SequenceEqual(["slot0", "slot6", "stash0"]) &&
            !wire.Contains(Me) && !wire.Contains("private") && !wire.Contains("9898"),
            "matching ID alternative and main/backpack/stash distinction, without projecting identity, deltas, minimap or token");
        foreach (var (id, team) in new[] { (10, "radiant"), (11, "dire"), (63, "radiant"), (128, "dire") }) {
            var body = Packet(); body["player"]!["player_slot"] = id; body["player"]!["team_slot"] = 4;
            body["player"]!["team_name"] = team;
            check(MinuteFarmContext.Eligible(Parse(body)), $"flat own {team} participant accepts player ID {id} and playable team seat");
        }
        var legacy = JsonSerializer.SerializeToNode(own, Wire.Json)!;
        legacy["farm"]!.AsObject().Remove("silenced"); legacy["farm"]!.AsObject().Remove("muted");
        var decoded = legacy.Deserialize<OwnState>(Wire.Json);
        check(MinuteFarmContext.Eligible(decoded) && decoded?.Farm is { Silenced: null, Muted: null },
            "older farm wire telemetry without active-restriction fields remains eligible but both flags are unknown");
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
            ("no positive ownership", b => ((JsonObject)b["player"]!).Remove("activity")),
            ("foreign Steam ID despite playing", b => b["provider"]!["steamid"] = "76561198000000999"),
            ("menu activity despite matching IDs", b => { b["provider"]!["steamid"] = Me; b["player"]!["activity"] = "menu"; }),
            ("player ID alone is not ownership", b => { b["player"]!["player_slot"] = 10; b["player"]!.AsObject().Remove("activity"); }),
            ("observer team slot", b => { b["player"]!["player_slot"] = 11; b["player"]!["team_slot"] = -1; }),
            ("team slot outside playable team", b => { b["player"]!["player_slot"] = 10; b["player"]!["team_slot"] = 5; }),
            ("negative player ID", b => b["player"]!["player_slot"] = -1), ("out-of-range player ID", b => b["player"]!["player_slot"] = 64),
            ("noninteger player ID", b => b["player"]!["player_slot"] = 10.5),
            ("grouped active player with ID 10 beside flat evidence", b => {
                b["player"]!["player_slot"] = 10;
                b["player"]!["team2"] = new JsonObject { ["player10"] = b["player"]!.DeepClone() };
            }),
            ("conflicting team", b => b["hero"]!["team"] = 3), ("allplayers beside flat state", b => b["allplayers"] = new JsonObject()),
            ("mixed item group", b => b["items"] = JsonNode.Parse("""{"team2":{"name":"private-foreign-item"}}""")),
            ("multiplexed delta", b => b["added"] = JsonNode.Parse("""{"abilities":{"team3":{"player9":{}}}}""")),
            ("multiplexed discarded envelope", b => b["wearables"] = JsonNode.Parse("""{"team2":{}}""")),
            ("replay", b => b["map"]!["is_replay"] = true), ("coach", b => b["player"]!["is_coach"] = true),
            ("provider app", b => b["provider"]!["appid"] = 730), ("stale source second", b => b["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 5),
            ("future source second", b => b["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 2),
            ("missing source time", b => ((JsonObject)b["provider"]!).Remove("timestamp")),
            ("unknown pause", b => ((JsonObject)b["map"]!).Remove("paused")), ("string pause", b => b["map"]!["paused"] = "false"),
            ("paused alias contradicts false", b => b["map"]!["is_paused"] = true),
            ("ended game", b => b["map"]!["game_state"] = "DOTA_GAMERULES_STATE_POST_GAME"),
            ("unknown match", b => b["map"]!["matchid"] = "0"), ("missing position", b => ((JsonObject)b["hero"]!).Remove("xpos")),
            ("zero health", b => b["hero"]!["health"] = 0), ("dead", b => b["hero"]!["alive"] = false),
            ("health above explicit max", b => b["hero"]!["max_health"] = 20),
            ("silenced is a string", b => b["hero"]!["silenced"] = "false"), ("muted is numeric", b => b["hero"]!["muted"] = 0),
            ("explicit null silence", b => b["hero"]!["silenced"] = null), ("explicit null mute", b => b["hero"]!["muted"] = null),
            ("hero demo map", b => b["map"]!["name"] = "hero_demo_main"), ("named arcade map", b => b["map"]!["customgamename"] = "arcade"),
            ("turbo", b => b["map"]!["game_mode"] = 23), ("explicit ability channel", b => b["abilities"] = JsonNode.Parse("""{"ability0":{"channeling":true}}"""))
        }) {
            var body = Packet(); mutate(body); var result = Parse(body);
            check(!MinuteFarmContext.Eligible(result) && !JsonSerializer.Serialize(result).Contains("private-"), $"minute provenance fails closed: {name}");
        }
        foreach (string flag in new[] { "stunned", "disarmed", "hexed", "rooted", "command_restricted", "channeling" }) {
            var body = Packet(); body["hero"]![flag] = true;
            check(!MinuteFarmContext.Eligible(Parse(body)), "explicit incapacity withdraws minute telemetry: " + flag);
        }
        var unknown = Packet(); ((JsonObject)unknown["map"]!).Remove("name"); ((JsonObject)unknown["hero"]!).Remove("level");
        unknown["hero"]!["move_speed"] = "380"; unknown["hero"]!["attack_damage"] = -10;
        check(Parse(unknown).Farm is { MoveSpeed: null, Level: null, AttackDamage: null } && MinuteFarmContext.Eligible(Parse(unknown)),
            "missing map name can use separate confirmation; unknown stats not parsed from strings or guesses");
        unknown["map"]!["name"] = "start";
        check(MinuteFarmContext.Eligible(Parse(unknown)), "standard start map name is not rejected as a custom mode");
        check(!MinuteFarmContext.Eligible(own with { Farm = null }) &&
            Reject(() => GsiState.Validate(own with { Farm = own.Farm! with { MoveSpeed = double.NaN } })) &&
            Reject(() => GsiState.Validate(own with { Items = [new("item_blink", null, 0, "private")] })),
            "legacy wire state is not minute proof; invalid explicit wire stats and slot strings rejected");
    }

    private static void Publish(CoachMailbox box, string id, long seq, int minute = 10, int ms = 60000) =>
        box.SetReminder(id, seq, Kind, "route", A, Layout, false, ms, [A, B], ["first", "second"], farmMinute: minute);
    private static void Mailbox(Action<bool, string> check)
    {
        long now = 50_000, seq = 0;
        var box = new CoachMailbox(() => now); var own = Parse(Packet());
        string id = box.Begin().SessionId;
        long Feed(OwnState? p, int age = 0, string source = "dota2", int leaseAge = 0)
        {
            var t = box.Issue(id); now += leaseAge;
            var m = new FrameMeta(id, t.Id, ++seq, source, 1280, 720, p, p == null ? null : age);
            if (source == "dota2-gsi") box.AcceptMinuteState(m, 100); else box.Accept(m, Jpeg);
            return seq;
        }
        long begin = Feed(own, 100);
        for (int i = 1; i <= 15; i++) { now += 1000; Feed(own with { Clock = 600 + i }, 100); }
        Publish(box, id, begin);
        check(box.Poll(id) is { FrameSequence: 1, FarmMinute: 10, RemainingMs: 44_900, ShowSubtitle: false },
            "15-second result stays bound to original sequence and only the minute remainder, including GSI age");
        check(Reject(() => Publish(box, id, begin)), "host cannot republish the same minute source with a new lifetime");
        for (int i = 16; i <= 59; i++) { now += 1000; Feed(own with { Clock = 600 + i }, 100); }
        check(box.Poll(id)?.RemainingMs == 900 && Reject(() => Publish(box, id, begin)), "display may outlive publication history, never its minute");
        now += 900;
        check(box.Poll(id) == null && !box.Snapshot().HasReminder, "source-anchored wall deadline works without another poll or new minute frame");

        void Fenced(string name, Func<OwnState, OwnState?> invalid, bool legacySurvives = false)
        {
            id = box.Begin().SessionId; seq = 0; long origin = Feed(own);
            Publish(box, id, origin); Feed(invalid(own));
            check(!box.Snapshot().HasReminder && box.Poll(id) == null, $"host immediately withdraws minute route: {name}");
            id = box.Begin().SessionId; seq = 0; origin = Feed(own); // Also test a *pending*, never-published result.
            if (legacySurvives) box.SetReminder(id, origin, "manual-link-test", "legacy", null);
            Feed(invalid(own)); long recovered = Feed(own with { Clock = 602 });
            check(Reject(() => Publish(box, id, origin)) && (!legacySurvives || box.Poll(id)?.Message == "legacy"),
                $"recovering in the same minute does not revive a pending origin: {name}");
            Publish(box, id, recovered);
            check(box.Poll(id)?.FrameSequence == recovered, "new eligible origin after " + name);
        }
        Fenced("pause", p => p with { Farm = p.Farm! with { Paused = true } }, true);
        Fenced("incapacity/unknown evidence", p => p with { Farm = null }, true);
        Fenced("different raw-GSI epoch", p => p with { Farm = p.Farm! with { Epoch = Guid.NewGuid() } }, true);
        Fenced("known other build", p => p with { Farm = p.Farm! with { InstalledBuild = 6990 } }, true);
        Fenced("death", p => p with { Alive = false }); Fenced("absent own GSI", _ => null);
        Fenced("new match", p => p with { MatchId = "9911223" }); Fenced("team", p => p with { Team = "dire" });
        Fenced("hero", p => p with { Hero = "npc_dota_hero_troll_warlord" });
        Fenced("next minute and rewind", p => p with { Clock = 660 });
        Fenced("health drop", p => p with { Health = 629 }, true);
        Fenced("position discontinuity", p => p with { X = p.X + 1601 }, true);

        id = box.Begin().SessionId; seq = 0; begin = Feed(own);
        void Shape(ScreenMark? mark, MinimapLayout? map, ScreenMark[]? points, string[]? steps, int? minute, int ms = 60000) =>
            box.SetReminder(id, begin, Kind, "x", mark, map, displayMs: ms, route: points, nextSteps: steps, farmMinute: minute);
        check(Reject(() => Shape(null, Layout, null, null, 10)) && Reject(() => Shape(A, null, [A], ["one"], 10)) &&
            Reject(() => Shape(A, Layout with { SourceWidth = 1920 }, [A], ["one"], 10)) &&
            Reject(() => Shape(A, Layout, [A, new(.2, .1)], ["one", "two"], 10)) &&
            Reject(() => Shape(A, Layout, [A, B], ["one"], 10)) && Reject(() => Shape(B, Layout, [A, B], ["one", "two"], 10)) &&
            Reject(() => Shape(A, Layout, [A], ["one"], null)) && Reject(() => Shape(A, Layout, [A], ["one"], 11)) &&
            Reject(() => Shape(A, Layout, [A], ["one"], 10, 60001)), "only calibrated, minute-bound ordered routes with equally many steps");
        check(Reject(() => box.SetReminder(id, begin, "manual-link-test", "legacy", null, displayMs: 15001)) &&
            Reject(() => box.SetReminder(id, begin, "visible-farming-advice", "legacy", null, farmMinute: 10)),
            "ordinary kinds retain 15-second max and cannot claim a farm minute");
        for (int i = 1; i <= 22; i++) { now += 1000; Feed(own with { Clock = 600 + i }); }
        check(Reject(() => Publish(box, id, begin)), "a later eligible picture never makes an old initial source publishable");
        id = box.Begin().SessionId; seq = 0; begin = Feed(own, age: 1400, leaseAge: 1100); Publish(box, id, begin);
        now += 500;
        check(box.Latest()?.Meta.Player is { Farm: null } && !box.Snapshot().HasReminder && box.Poll(id) == null,
            "3-second combined packet, capture lease and frame age expires before 4-second video presence");
        Feed(own with { Clock = 601 });
        check(Reject(() => Publish(box, id, begin)), "fresh capture after a timeout does not restore old scope");
        id = box.Begin().SessionId; seq = 0; begin = Feed(own); Publish(box, id, begin);
        for (int i = 0; i < 4; i++) { now += 1000; Feed(own); }
        check(!box.Snapshot().HasReminder && box.Latest()?.Meta.Player is { Farm: null } && Reject(() => Publish(box, id, seq)),
            "heartbeats on a frozen game clock cannot keep, offer or republish a minute route");
        id = box.Begin().SessionId; seq = 0; begin = Feed(own with { Clock = 659.75 }, 50);
        Publish(box, id, begin); check(box.Poll(id)?.RemainingMs == 200, "fractional final second is clipped, never rounded up to a one-second display");
    }

    private static async Task Ingress(Action<bool, string> check)
    {
        string dir = Directory.CreateTempSubdirectory("minute-install-").FullName;
        string game = Path.Combine(dir, "game"), dota = Directory.CreateDirectory(Path.Combine(game, "dota")).FullName;
        string path = Path.Combine(dota, "steam.inf");
        try {
            File.WriteAllText(path, "appID=570\r\nClientVersion=6989\r\nServerVersion=6989\r\n");
            await using var receiver = await GsiReceiver.Start(0, dotaInstall: dir);
            string token = Regex.Match(receiver.Config, "\"token\" \"([A-F0-9]+)\"").Groups[1].Value;
            using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{receiver.Port}") };
            async Task<HttpStatusCode> Post(JsonObject body, bool authenticated = true) {
                body["auth"] = new JsonObject { ["token"] = authenticated ? token : "wrong" };
                using var r = await http.PostAsJsonAsync("/gsi", body); return r.StatusCode;
            }
            await ActiveRestrictions(receiver, body => Post(body), check);
            await Post(Packet()); var origin = receiver.Latest().Player!;
            check(MinuteFarmContext.Eligible(origin) && origin.Farm?.InstalledBuild == 6989 && origin.Farm.Epoch != Guid.Empty,
                "real authenticated receiver adds raw-GSI epoch and independently checked installation version");
            var mixed = Packet(); mixed["allplayers"] = new JsonObject();
            await Post(mixed, false);
            check(receiver.Latest().Player?.Farm?.Epoch == origin.Farm!.Epoch, "unauthorized packets do not withdraw valid participation");
            await Post(mixed);
            check(!MinuteFarmContext.Eligible(receiver.Latest().Player), "authenticated mixed flat/observer packet immediately withdraws minute proof");
            await Task.Delay(1050); await Post(Packet()); var next = receiver.Latest().Player!;
            check(MinuteFarmContext.Eligible(next) && next.Farm?.Epoch != origin.Farm!.Epoch,
                "even a withdrawal and recovery entirely between captures crosses the raw-GSI epoch");
            var switched = Packet(); switched["player"]!["steamid"] = "76561198000000999"; await Post(switched);
            check(receiver.Latest().Player?.Farm is { } switchedFarm && switchedFarm.Epoch != next.Farm!.Epoch,
                "same hero, team and minute but a different local actor rotates the anonymized epoch");
            File.WriteAllText(path, "appID=570\nClientVersion=6990\nServerVersion=6990");
            await Post(Packet());
            check(receiver.Latest().Player?.Farm?.InstalledBuild == 6990 && !MinuteFarmContext.Eligible(receiver.Latest().Player),
                "installed build mismatch vetoes GSI claiming provider.version 6989");
            File.WriteAllText(path, "appID=570\nClientVersion=6989\nServerVersion=6990");
            await Post(Packet()); check(!MinuteFarmContext.Eligible(receiver.Latest().Player), "conflicting installation manifest is not a usable build");
            File.Delete(path); await Task.Delay(1050); await Post(Packet());
            check(!MinuteFarmContext.Eligible(receiver.Latest().Player), "losing access to a known conflicting manifest does not certify the build");
        } finally {
            File.Delete(path); Directory.Delete(dota); Directory.Delete(game); Directory.Delete(dir);
        }
    }

    private static async Task ActiveRestrictions(GsiReceiver receiver, Func<JsonObject, Task<HttpStatusCode>> post, Action<bool, string> check)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        await link.Connect(default);
        var box = host.Mailbox; string id = link.SessionId!; long seq = 0;
        var director = new MinuteFarmDirector();
        async Task<FrameView> Sample(bool silence, bool mute, Reminder? shown = null)
        {
            var body = Packet(); body["map"]!["clock_time"] = 600 + seq; body["player"]!["player_slot"] = 10;
            body["hero"]!["silenced"] = silence; body["hero"]!["muted"] = mute;
            body["items"] = JsonNode.Parse("""{"slot0":{"name":"item_mask_of_madness"}}""");
            if (await post(body) != HttpStatusCode.OK) throw new Exception("Authenticated participant fixture was not accepted");
            var (own, age) = receiver.Latest();
            if (shown != null) check(link.CanRenderMinuteRoute(shown, own, age),
                $"independent local render survives silenced={silence}, muted={mute} before next upload");
            var ticket = await link.GetTicket(default);
            await link.UploadMinuteState(new(id, ticket.Id, ++seq, "dota2-gsi", 1280, 720, own, age), Environment.TickCount64, default);
            var frame = box.Latest()!;
            check(frame.Meta.Player?.Farm is { } f && f.Silenced == silence && f.Muted == mute && f.Epoch != Guid.Empty,
                $"authenticated GSI carries silenced={silence}, muted={mute} across the paired wire");
            return frame;
        }
        var source = await Sample(false, false); var epoch = source.Meta.Player!.Farm!.Epoch;
        foreach (var (silence, mute) in new[] { (true, false), (false, false), (false, true), (true, true) }) {
            var current = await Sample(silence, mute);
            check(current.Meta.Player!.Farm!.Epoch == epoch && director.StillCurrent(source, current),
                $"in-flight origin survives silenced={silence}, muted={mute} without rotating epoch");
        }
        Publish(box, id, source.Meta.Sequence); // Finish the original request while both kinds of active are restricted.
        var shown = await link.Poll(default);
        check(shown is { Kind: Kind } && shown.FrameSequence == source.Meta.Sequence,
            "silence/mute allow publication from the original pre-Mask sequence, without a second request");
        foreach (var (silence, mute) in new[] { (false, false), (true, false), (false, true), (false, false) }) {
            var current = await Sample(silence, mute, shown!);
            var next = await link.Poll(default);
            check(current.Meta.Player!.Farm!.Epoch == epoch && next != null && next.Id == shown!.Id &&
                next.FrameSequence == source.Meta.Sequence && next.RemainingMs <= shown.RemainingMs,
                $"published route is neither withdrawn nor renewed across silenced={silence}, muted={mute}");
        }
        await link.End(default);
    }

    private static async Task Paired(Action<bool, string> check)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        long gameNow = 1_000_000, seq = 0;
        using var link = new GameLink(host.Pair(IPAddress.Loopback), () => gameNow);
        await link.Connect(default);
        string id = link.SessionId!;
        var own = Parse(Packet());
        async Task<long> Send(OwnState? p, string source = "dota2-gsi", int age = 0, bool rejectedTicket = false)
        {
            var ticket = await link.GetTicket(default);
            var meta = new FrameMeta(id, rejectedTicket ? "not-the-ticket" : ticket.Id, ++seq, source,
                1280, 720, p, p == null ? null : age);
            if (source == "dota2-gsi") await link.UploadMinuteState(meta, gameNow, default);
            else await link.Upload(meta, Jpeg, gameNow, default);
            return seq;
        }
        var box = host.Mailbox;
        foreach (string source in new[] { "dota2", "dota2-gsi" }) {
            long origin = await Send(own, source); Publish(box, id, origin);
            var r = await link.Poll(default);
            check(r is { Kind: Kind, FarmMinute: 10, RemainingMs: > 15000 and <= 60000 } &&
                link.CanRenderMinuteRoute(r, own, 30), "paired minute route with direct local GSI render check: " + source);
            check(!link.CanRenderMinuteRoute(r!, own with { Farm = own.Farm! with { Paused = true } }, 5) &&
                box.Poll(id) != null && await link.Poll(default) == null,
                "local pause before next upload/poll permanently retires displayed origin: " + source);
        }
        check(Reject(() => box.SetReminder(id, seq, "manual-link-test", "no point without pixels", new(.1, .9))) &&
            Reject(() => box.SetReminder(id, seq, "visible-farming-advice", "no image advice without pixels", null)),
            "GSI-only ingress cannot publish a map point or image-model advice");
        box.SetReminder(id, seq, "local-vision-observation", "剃刀 离开视野", null, displayMs: 3000);
        check(await link.Poll(default) is { Kind: "local-vision-observation", Marker: null, Message: "剃刀 离开视野" },
            "a markerless Plus alert crosses the GSI-only link like the v0.4.8 status link");
        long pending = await Send(own);
        try { await Send(null, rejectedTicket: true); throw new Exception("expected bad ticket"); } catch (HttpRequestException) { }
        await Send(own with { Clock = 601 });
        Publish(box, id, pending);
        check(box.Poll(id) != null && await link.Poll(default) == null,
            "game independently fences a pending origin across a local invalid frame the host never accepted");

        long start = await Send(own);
        for (int i = 1; i <= 15; i++) { gameNow += 1000; await Send(own with { Clock = 600 + i }); }
        Publish(box, id, start); var accepted = await link.Poll(default);
        check(accepted?.FrameSequence == start && accepted.SourceAgeMs == 15000 && accepted.RemainingMs <= 45000,
            "game accepts a still-fresh 15-second original capture, measures age locally");
        for (int i = 16; i <= 42; i++) { gameNow += 1000; await Send(own with { Clock = 600 + i }); }
        check(await link.Poll(default) is { RemainingMs: > 0 and <= 18000 },
            "admitted minute route survives original legacy 37-second history cap without renewing its deadline");
        check(!link.CanRenderMinuteRoute(accepted!, own with { Clock = 660 }, 0) && await link.Poll(default) == null,
            "local next-minute GSI clears while host still serves a positive wall TTL");
        start = await Send(own);
        for (int i = 1; i <= 22; i++) { gameNow += 1000; await Send(own with { Clock = 600 + i }); }
        Publish(box, id, start); // Host clock did not advance 22 s. The two machines need not agree.
        check(box.Poll(id) != null && await link.Poll(default) == null,
            "an old never-admitted local origin stays stale even if coach would publish it");
        foreach (var (reason, invalidate) in new (string, Func<OwnState, OwnState?>)[] {
            ("missing", _ => null), ("death", p => p with { Alive = false }), ("match", p => p with { MatchId = "91" }),
            ("team", p => p with { Team = "dire" }), ("hero", p => p with { Hero = "npc_dota_hero_axe" }),
            ("health loss", p => p with { Health = 629 }), ("displacement", p => p with { X = p.X + 1601 }),
            ("raw-GSI epoch", p => p with { Farm = p.Farm! with { Epoch = Guid.NewGuid() } }) }) {
            start = await Send(own); Publish(box, id, start); var live = (await link.Poll(default))!;
            check(live != null && !link.CanRenderMinuteRoute(live, invalidate(own), 0) &&
                !link.CanRenderMinuteRoute(live, own, 0), $"independent local {reason} withdrawal cannot be undone by later render");
        }
        start = await Send(own); Publish(box, id, start); var last = (await link.Poll(default))!;
        gameNow += 3000;
        check(!link.CanRenderMinuteRoute(last, own, 0) && box.Poll(id) != null,
            "local capture timeout hides even with fresh independent GSI and no network poll");
        start = await Send(own); Publish(box, id, start); last = (await link.Poll(default))!;
        check(!link.CanRenderMinuteRoute(last, own, 3000), "independent GSI timeout hides even while captures remain fresh");
        start = await Send(own); Publish(box, id, start); last = (await link.Poll(default))!;
        await link.End(default);
        check(!link.CanRenderMinuteRoute(last, own, 0), "local disconnect withdraws minute route independently of overlay deadline");
    }
}
