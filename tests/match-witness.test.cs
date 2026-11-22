#:property PublishAot=false
#:project ../desktop/DotaCoach.TwoPc.Core/DotaCoach.TwoPc.Core.csproj

// dotnet run --file tests/match-witness.test.cs
// Loopback only. Optional: append "-- --recorded-shapes <flat GSI .json files>" to replay local
// packet shapes with test identities, current timestamps, and a constructed sparse POST_GAME.
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static class MatchWitnessTest
{
    private const string A = "9130000042", B = "9130000043", Hero = "npc_dota_hero_shadow_shaman";
    private const string Playing = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", Pre = "DOTA_GAMERULES_STATE_PRE_GAME",
        Post = "DOTA_GAMERULES_STATE_POST_GAME", Disconnect = "DOTA_GAMERULES_STATE_DISCONNECT";
    private const uint Account = 42_000_123;
    private const ulong Steam = 76561197960265728UL + Account;
    private static readonly string Token = Wire.Secret();
    private static int passed;
    private static void Check(bool ok, string name)
    {
        if (!ok) throw new Exception(name);
        Interlocked.Increment(ref passed);
        Console.WriteLine("PASS " + name);
    }
    private static JsonObject Packet(string state = Playing, double clock = 615, string match = A) => new() {
        ["auth"] = new JsonObject { ["token"] = Token },
        ["provider"] = new JsonObject { ["name"] = "Dota 2", ["appid"] = 570, ["steamid"] = Steam,
            ["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
        ["map"] = new JsonObject { ["name"] = "dota", ["matchid"] = match, ["clock_time"] = clock,
            ["game_state"] = state, ["paused"] = false },
        ["player"] = new JsonObject { ["activity"] = "playing", ["team_name"] = "DOTA_TEAM_BADGUYS",
            ["steamid"] = Steam.ToString(), ["accountid"] = Account, ["player_slot"] = 10, ["team_slot"] = 2,
            ["name"] = "private-player" },
        ["hero"] = new JsonObject { ["name"] = Hero, ["id"] = 27, ["alive"] = true, ["health"] = 900, ["mana"] = 120,
            ["permanent_buffs"] = new JsonObject() },
        ["minimap"] = new JsonObject { ["o0"] = new JsonObject { ["unitname"] = "private-minimap-unit" } },
        ["previously"] = new JsonObject { ["hero"] = new JsonObject { ["name"] = "private-old-hero" } }
    };
    private static JsonObject End(string match = A)
    {
        var p = Packet(Post, 616, match); p.Remove("hero"); return p;
    }
    private static JsonObject Economy()
    {
        var p = Packet();
        var h = p["hero"]!; h["level"] = 14; h["max_health"] = 1420.5; h["max_mana"] = 670.25;
        h["alive"] = false; h["health"] = 0; h["stunned"] = true; h["silenced"] = true;
        var player = p["player"]!;
        player["gold"] = 0; player["net_worth"] = 14_682; player["last_hits"] = 143; player["denies"] = 7;
        player["kills"] = 2; player["deaths"] = 4; player["assists"] = 19; player["gpm"] = 469; player["xpm"] = 566;
        return p;
    }

    private sealed class Probe(GsiReceiver gsi) : IAsyncDisposable
    {
        public readonly GsiReceiver Gsi = gsi;
        private readonly HttpClient http = new() { BaseAddress = new($"http://127.0.0.1:{gsi.Port}") };
        public static async Task<Probe> Start(string? install = null) => new(await GsiReceiver.Start(0, resumeToken: Token, dotaInstall: install));
        public Task<HttpStatusCode> Send(JsonObject p) => Raw(p.ToJsonString());
        public async Task<HttpStatusCode> Raw(string json, bool origin = false)
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, "/gsi") { Content = new StringContent(json, Encoding.UTF8, "application/json") };
            if (origin) req.Headers.Add("Origin", "https://invalid.example");
            using var res = await http.SendAsync(req);
            return res.StatusCode;
        }
        // 100 Continue proves the server has entered the handler's asynchronous body read. No scheduling sleeps.
        public async Task HoldOlderBody(JsonObject old, Func<Task> whileReading)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            var ct = timeout.Token;
            using var tcp = new TcpClient();
            await tcp.ConnectAsync(IPAddress.Loopback, Gsi.Port, ct);
            using var stream = tcp.GetStream();
            using var reader = new StreamReader(stream, Encoding.ASCII);
            byte[] bytes = Encoding.UTF8.GetBytes(old.ToJsonString());
            string header = $"POST /gsi HTTP/1.1\r\nHost: 127.0.0.1:{Gsi.Port}\r\nContent-Type: application/json\r\n" +
                $"Content-Length: {bytes.Length}\r\nExpect: 100-continue\r\nConnection: close\r\n\r\n";
            await stream.WriteAsync(Encoding.ASCII.GetBytes(header), ct);
            if (await reader.ReadLineAsync(ct) != "HTTP/1.1 100 Continue" || await reader.ReadLineAsync(ct) != "")
                throw new Exception("Expected Kestrel to request the pending body");
            await whileReading();
            await stream.WriteAsync(bytes, ct);
            if (await reader.ReadLineAsync(ct) is not { } response || !response.StartsWith("HTTP/1.1 "))
                throw new Exception("Expected pending body to finish");
        }
        public async ValueTask DisposeAsync() { http.Dispose(); await Gsi.DisposeAsync(); }
    }

    private static void WireContract()
    {
        var w = new MatchWitness(A, "dire", Hero, 615.5, "playing", false) { AgeMs = 2500 };
        Check(MatchWitness.Valid(w, 500) && !MatchWitness.Valid(w, 501) && !MatchWitness.Valid(w, -1) &&
            !MatchWitness.Valid(w, long.MaxValue) && !MatchWitness.Valid(null), "validate the combined 3s budget without overflow");
        foreach (var invalid in new[] { w with { MatchId = "0" }, w with { MatchId = "01" }, w with { MatchId = "18446744073709551616" },
            w with { MatchId = "123\n" }, w with { Team = "spectator" }, w with { Hero = "npc_dota_hero_" }, w with { Hero = Hero + "\n" },
            w with { Clock = double.NaN }, w with { Clock = double.PositiveInfinity }, w with { Clock = 43201 },
            w with { AgeMs = -1 }, w with { AgeMs = 3001 }, w with { Phase = "ended" }, w with { Phase = "PLAYING" } })
            Check(!MatchWitness.Valid(invalid), "invalid public projection rejected");
        var obj = JsonSerializer.SerializeToNode(w, Wire.Json)!.AsObject();
        Check(obj.Select(x => x.Key).ToHashSet().SetEquals(["matchId", "team", "hero", "clock", "phase", "paused", "ageMs"]) &&
            obj.Deserialize<MatchWitness>(Wire.Json) == w, "wire allowlist and source age round-trip");
        var built = w with { InstalledBuild = 6989 };
        Check(MatchWitness.Valid(built) && MatchWitness.Valid(w with { InstalledBuild = 6990 }) &&
            !MatchWitness.Valid(w with { InstalledBuild = 0 }) && !MatchWitness.Valid(w with { InstalledBuild = -1 }) &&
            !MatchWitness.Valid(w with { InstalledBuild = 10_000_001 }), "valid build shape does not imply the layout supports that build");
        var builtJson = JsonSerializer.SerializeToNode(built, Wire.Json)!.AsObject();
        Check(builtJson["installedBuild"]!.GetValue<int>() == 6989 && builtJson.Deserialize<MatchWitness>(Wire.Json) == built &&
            obj.Deserialize<MatchWitness>(Wire.Json)!.InstalledBuild == null, "optional local build round-trips and old witnesses decode as unknown");
        builtJson["installedBuild"] = "6989";
        try { builtJson.Deserialize<MatchWitness>(Wire.Json); throw new Exception("Mistyped wire build was accepted"); }
        catch (JsonException) { Check(true, "numeric strings cannot become wire build evidence"); }
        foreach (string key in new[] { "ageMs", "clock", "paused" }) {
            var missing = obj.DeepClone().AsObject(); missing.Remove(key);
            try { missing.Deserialize<MatchWitness>(Wire.Json); throw new Exception("Missing " + key + " silently defaulted"); }
            catch (JsonException) { Check(true, "missing wire " + key + " cannot be mistaken for a zero/false reading"); }
        }
    }

    private static async Task Lifecycle()
    {
        await using var x = await Probe.Start();
        var pre = Packet(Pre, -16.5);
        pre["map"]!["name"] = "start"; pre["map"]!["matchid"] = ulong.Parse(A);
        pre["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds().ToString();
        pre["player"]!.AsObject().Remove("activity"); // Exact provider/player IDs instead.
        pre["hero"] = new JsonObject { ["name"] = Hero }; // No legacy combat state, no farm fields.
        Check(await x.Send(pre) == HttpStatusCode.OK && x.Gsi.Latest(out var first).Player == null &&
            first is { MatchId: A, Team: "dire", Hero: Hero, Clock: -16.5, Phase: "pregame", Paused: false },
            "a non-farm hero's signed-in pregame is usable without a legacy player");
        var dead = Packet();
        dead["hero"]!["alive"] = false; dead["hero"]!["health"] = 0; dead["hero"]!["silenced"] = true; dead["hero"]!["stunned"] = true;
        Check(await x.Send(dead) == HttpStatusCode.OK && x.Gsi.Latest(out var playing).Player is { Alive: false, Farm: null } &&
            playing is { Hero: Hero, Phase: "playing", Paused: false }, "death/incapacity does not end or erase the participant witness");
        var paused = Packet(); paused["map"]!.AsObject().Remove("paused"); paused["map"]!["is_paused"] = true;
        await x.Send(paused);
        Check(x.Gsi.LatestMatch() is { Phase: "playing", Paused: true, Clock: 615 }, "pause is a positive paused reading, never postgame");
        var wrong = End(); wrong["auth"]!["token"] = "not-the-token";
        Check(await x.Send(wrong) == HttpStatusCode.Unauthorized && await x.Raw(End().ToJsonString(), origin: true) == HttpStatusCode.Unauthorized &&
            x.Gsi.LatestMatch() is { Phase: "playing", Paused: true }, "unauthenticated and Origin POSTs cannot end or withdraw the match");
        var end = End();
        Check(await x.Send(end) == HttpStatusCode.OK && x.Gsi.Latest(out var verified).Player == null &&
            verified is { MatchId: A, Team: "dire", Hero: Hero, Phase: "postgame", Clock: 616 } && MatchWitness.Valid(verified),
            "explicit same-participant POST_GAME without any hero section is acknowledged, not converted to OwnState");
        foreach (var sparse in new JsonObject[] { new(), new() { ["id"] = 0 }, new() { ["name"] = "", ["id"] = 27 } }) {
            end["hero"] = sparse;
            await x.Send(end);
            Check(x.Gsi.LatestMatch() is { Phase: "postgame", Hero: Hero }, "consistent sparse terminal repeats only reaffirm the verified end");
        }
        string compact = JsonSerializer.Serialize(x.Gsi.LatestMatch(), Wire.Json);
        Check(!compact.Contains(Steam.ToString()) && !compact.Contains(Account.ToString()) &&
            !compact.Contains(Token) && !compact.Contains("private"), "no account, token, raw player, delta or minimap leaves the projection");
        await x.Send(dead);
        Check(x.Gsi.LatestMatch() == null, "duplicate former playing packet cannot reopen the verified match");
    }

    private static async Task RejectCases()
    {
        foreach (var (name, change) in new (string, Action<JsonObject>)[] {
            ("other match terminal", p => p["map"]!["matchid"] = B),
            ("other team terminal", p => p["player"]!["team_name"] = "radiant"),
            ("same hero/team but another internally consistent own account", p => {
                p["provider"]!["steamid"] = Steam + 1; p["player"]!["steamid"] = Steam + 1; p["player"]!["accountid"] = Account + 1; }),
            ("loss of previously known account", p => { p["player"]!.AsObject().Remove("steamid");
                p["player"]!.AsObject().Remove("accountid"); p["provider"]!.AsObject().Remove("steamid"); }),
            ("different player slot", p => p["player"]!["player_slot"] = 9),
            ("different team seat", p => p["player"]!["team_slot"] = 4),
            ("spectator seat", p => p["player"]!["team_slot"] = -1),
            ("mismatching explicit hero", p => p["hero"] = new JsonObject { ["name"] = "npc_dota_hero_axe" }),
            ("mismatching id-only hero", p => p["hero"] = new JsonObject { ["id"] = 2 }),
            ("mismatching sparse hero team", p => p["hero"] = new JsonObject { ["team"] = 2 }),
            ("malformed hero is not missing", p => p["hero"] = new JsonArray()),
            ("unknown nested hero group is not a sparse hero", p => p["hero"] = JsonNode.Parse("""{"group":{"name":"npc_dota_hero_axe"}}""")),
            ("malformed name is not missing", p => p["hero"] = new JsonObject { ["name"] = null }),
            ("flat Plus spectator even with an empty hero", p => {
                p["player"]!["team_name"] = "spectator"; p["hero"] = new JsonObject { ["id"] = 0 }; }),
            ("spectating activity overrides matching IDs", p => p["player"]!["activity"] = "spectating"),
            ("menu activity overrides matching IDs", p => p["player"]!["activity"] = "menu"),
            ("provider/player mismatch", p => p["provider"]!["steamid"] = Steam + 1),
            ("contradictory 32-bit accountid", p => p["player"]!["accountid"] = Account + 1),
            ("no activity, provider identity or exact prior player account", p => { p["player"]!.AsObject().Remove("activity");
                p["provider"]!.AsObject().Remove("steamid"); p["player"]!.AsObject().Remove("steamid"); p["player"]!.AsObject().Remove("accountid"); }),
            ("wrong provider app", p => p["provider"]!["appid"] = 730),
            ("missing source timestamp", p => p["provider"]!.AsObject().Remove("timestamp")),
            ("stale source", p => p["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 9),
            ("older source inside the 3s budget", p => p["provider"]!["timestamp"] = p["provider"]!["timestamp"]!.GetValue<long>() - 1),
            ("future source", p => p["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 8),
            ("unknown map", p => p["map"]!.AsObject().Remove("name")),
            ("demo map", p => p["map"]!["name"] = "hero_demo_main"),
            ("custom mode on standard map", p => p["map"]!["customgamename"] = "custom"),
            ("zero match", p => p["map"]!["matchid"] = "0"),
            ("end clock rewind", p => p["map"]!["clock_time"] = 612),
            ("terminal after impossible forward seek", p => p["map"]!["clock_time"] = 1615),
            ("malformed clock", p => p["map"]!["clock_time"] = "616"),
            ("contradictory pause", p => p["map"]!["is_paused"] = true),
            ("unknown pause", p => p["map"]!.AsObject().Remove("paused")),
            ("allplayers beside flat state", p => p["allplayers"] = new JsonObject()),
            ("malformed grouped player beside flat state", p => p["player"]!["team2"] = false),
            ("grouped delta", p => p["added"] = JsonNode.Parse("""{"items":{"team2":{"player6":{}}}}""")),
            ("observer flag in discarded data", p => p["minimap"]!["o0"]!["is_observer"] = "false"),
            ("replay", p => p["map"]!["is_replay"] = true),
            ("disconnect, even with explicit winner", p => { p["map"]!["game_state"] = Disconnect; p["map"]!["win_team"] = "dire"; }),
            ("terminal in previously only", p => { p["map"]!["game_state"] = "unknown"; p["previously"]!["map"] = new JsonObject { ["game_state"] = Post }; }),
        }) {
            await using var x = await Probe.Start();
            var active = Packet(); active["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
            await x.Send(active);
            if (x.Gsi.LatestMatch()?.Phase != "playing") throw new Exception("Missing positive control: " + name);
            var bad = End(); bad["provider"]!["timestamp"] = active["provider"]!["timestamp"]!.DeepClone(); change(bad);
            await x.Send(bad);
            Check(x.Gsi.LatestMatch() == null, "withdraw on " + name);
            await x.Send(End());
            Check(x.Gsi.LatestMatch() == null, "later clean terminal cannot bridge " + name);
        }
    }

    private static async Task RecordedBuffShape()
    {
        // Reduced from the flat player captures (not a simulated observer hero). Use a test
        // credential, account, match and current provider second; the end transition is constructed.
        static JsonObject Active()
        {
            var a = Packet();
            a["provider"]!.AsObject().Remove("steamid"); // The recorded provider had no steamid.
            a["player"]!["accountid"] = Account.ToString(); a["player"]!["team_name"] = "radiant";
            a["player"]!["player_slot"] = 0; a["player"]!["team_slot"] = 0; a["player"]!["kill_list"] = new JsonObject();
            a["hero"] = JsonNode.Parse("""
                {"facet":0,"xpos":-7104,"ypos":-6088,"id":48,"name":"npc_dota_hero_luna","level":4,"xp":1237,
                 "alive":true,"respawn_seconds":0,"health":740,"max_health":740,"mana":411,"max_mana":411,
                 "silenced":false,"stunned":false,"disarmed":false,"hexed":false,"muted":false,
                 "aghanims_scepter":false,"aghanims_shard":false,"smoked":false,"permanent_buffs":{},
                 "has_debuff":false,"talent_1":false,"talent_8":false,"attributes_level":0}
                """);
            return a;
        }

        await using var x = await Probe.Start();
        var live = Active();
        Check(await x.Send(live) == HttpStatusCode.OK && x.Gsi.Latest(out var start).Player?.Stats?.Level == 4 &&
            start is { MatchId: A, Hero: "npc_dota_hero_luna", Phase: "playing" },
            "recorded flat hero with permanent_buffs:{} establishes a participant and retains economy");
        var end = live.DeepClone().AsObject(); end["map"]!["game_state"] = Post; end["map"]!["clock_time"] = 616;
        Check(await x.Send(end) == HttpStatusCode.OK && x.Gsi.LatestMatch() is { Phase: "postgame" },
            "recorded full hero with permanent_buffs:{} can verify the explicit end");
        await using var sparseOnly = await Probe.Start();
        await sparseOnly.Send(Active()); // No intervening full terminal to supply the hero.
        end["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        end["hero"] = new JsonObject { ["id"] = 0, ["permanent_buffs"] = new JsonObject() };
        Check(await sparseOnly.Send(end) == HttpStatusCode.OK && sparseOnly.Gsi.Latest(out var sparse).Player == null &&
            sparse is { MatchId: A, Team: "radiant", Hero: "npc_dota_hero_luna", Phase: "postgame" },
            "buff container alone is not hero identity; sparse end uses only the verified participant");

        await using var counted = await Probe.Start();
        live = Active(); live["hero"]!["permanent_buffs"] = new JsonObject { ["private_buff_counter"] = 2 };
        await counted.Send(live);
        var own = counted.Gsi.Latest(out var phase).Player;
        string wire = JsonSerializer.Serialize(new { own, phase }, Wire.Json);
        Check(phase?.Phase == "playing" && own?.Stats?.MaxHealth == 740 && !wire.Contains("buff", StringComparison.OrdinalIgnoreCase),
            "bounded flat buff counters are neither projected nor used as witness inputs");

        var oversized = new JsonObject();
        for (int i = 0; i < 33; ++i) oversized["buff" + i] = i;
        foreach (var (name, buffs) in new (string, JsonNode?)[] {
            ("observer group", JsonNode.Parse("""{"team2":{"player0":{}}}""")),
            ("observer slot even if scalar", JsonNode.Parse("""{"player0":0}""")),
            ("hidden nested group", JsonNode.Parse("""{"buff0":{"nested":{"team3":{"player8":{}}}}}""")),
            ("array", new JsonArray()), ("non-counter", JsonValue.Create("playing")), ("null", null),
            ("replay flag", JsonNode.Parse("""{"is_replay":0}""")), ("negative counter", JsonNode.Parse("""{"buff0":-1}""")),
            ("out-of-range counter", JsonNode.Parse("""{"buff0":4294967296}""")),
            ("long key", new JsonObject { [new string('a', 65)] = 1 }), ("too many fields", oversized)
        }) {
            await using var bad = await Probe.Start();
            await bad.Send(Active());
            var poisoned = Active(); poisoned["map"]!["game_state"] = Post; poisoned["hero"]!["permanent_buffs"] = buffs;
            await bad.Send(poisoned);
            Check(bad.Gsi.LatestMatch() == null, $"buff {name} is not a valid terminal");
            end["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await bad.Send(end);
            Check(bad.Gsi.LatestMatch() == null, $"a clean end cannot bridge buff {name}");
        }
        await using var beside = await Probe.Start();
        var hybrid = Active(); hybrid["hero"]!["team2"] = new JsonObject();
        await beside.Send(hybrid);
        Check(beside.Gsi.LatestMatch() == null, "native empty buffs do not excuse a sibling observer hero group");
        await using var dup = await Probe.Start();
        string repeated = Active().ToJsonString().Replace("\"permanent_buffs\":{}", "\"permanent_buffs\":{\"buff0\":0,\"buff0\":1}");
        await dup.Raw(repeated);
        Check(dup.Gsi.LatestMatch() == null, "duplicate buff keys reject instead of using last value");
    }

    private static async Task Boundaries()
    {
        await using var x = await Probe.Start();
        await x.Send(Packet(Post)); // Full hero, matching IDs, actual POST_GAME, but never played here.
        Check(x.Gsi.LatestMatch() == null, "even complete terminal state cannot originate an end in a new receiver session");
        await using var y = await Probe.Start();
        await y.Send(Packet(Pre, -15)); await y.Send(End());
        Check(y.Gsi.LatestMatch() == null, "pregame is not a trusted in-progress anchor");
        await using var z = await Probe.Start();
        var a = Packet(); await z.Send(a); await z.Send(Packet(match: B));
        Check(z.Gsi.LatestMatch()?.MatchId == B, "a newly witnessed match replaces, but does not end, the old match");
        await z.Send(End(A));
        Check(z.Gsi.LatestMatch() == null, "late POST_GAME of A cannot end B or resurrect A");
        await using var dup = await Probe.Start();
        a["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        await dup.Send(a);
        string raw = a.ToJsonString().Replace("\"activity\":\"playing\"", "\"activity\":\"menu\",\"activity\":\"playing\"");
        await dup.Raw(raw);
        Check(dup.Gsi.LatestMatch() == null, "duplicate JSON fields do not resolve to the last, apparently good value");
        await dup.Send(a); await dup.Send(End());
        Check(dup.Gsi.LatestMatch() == null, "a duplicate from the withdrawal second cannot re-arm a terminal");
        await using var account = await Probe.Start();
        await account.Send(Packet());
        var unnamed = Packet(); unnamed["player"]!.AsObject().Remove("steamid"); unnamed["player"]!.AsObject().Remove("accountid");
        unnamed["provider"]!.AsObject().Remove("steamid");
        await account.Send(unnamed);
        Check(account.Gsi.LatestMatch()?.Phase == "playing", "explicit playing is still participation when IDs are omitted");
        var foreign = End(); foreign["player"]!["steamid"] = Steam + 1; foreign["player"]!["accountid"] = Account + 1;
        foreign["provider"]!["steamid"] = Steam + 1; await account.Send(foreign);
        Check(account.Gsi.LatestMatch() == null, "an ID-less heartbeat cannot erase a previously known private account binding");

        await using var exact = await Probe.Start();
        var active = Packet(); active["player"]!.AsObject().Remove("activity"); active["player"]!.AsObject().Remove("steamid");
        await exact.Send(active);
        Check(exact.Gsi.LatestMatch()?.Phase == "playing", "matching provider Steam64 and flat player account ID are positive identity");
        var sparse = End(); sparse["player"]!.AsObject().Remove("activity"); sparse["player"]!.AsObject().Remove("steamid");
        sparse["provider"]!.AsObject().Remove("steamid");
        await exact.Send(sparse);
        Check(exact.Gsi.LatestMatch() is { Phase: "postgame", Hero: Hero }, "terminal without activity can correlate the exact prior account");
        await using var alone = await Probe.Start();
        await alone.Send(sparse);
        Check(alone.Gsi.LatestMatch() == null, "an uncorrelated private account is not postgame proof");
        active["provider"]!.AsObject().Remove("steamid");
        await using var onlyId = await Probe.Start();
        await onlyId.Send(active);
        Check(onlyId.Gsi.LatestMatch() == null, "a lone player account cannot certify a live participant");

        await using var activity = await Probe.Start();
        var flat = Packet(); flat["provider"]!.AsObject().Remove("steamid");
        flat["player"]!.AsObject().Remove("steamid"); flat["player"]!.AsObject().Remove("accountid");
        await activity.Send(flat);
        flat["map"]!["game_state"] = Post; flat.Remove("hero"); await activity.Send(flat);
        Check(activity.Gsi.LatestMatch() is { MatchId: A, Phase: "postgame", Hero: Hero },
            "continuous flat playing participant can correlate when neither packet provides an account ID");

        await using var noHeroId = await Probe.Start();
        active = Packet(); active["hero"]!.AsObject().Remove("id"); await noHeroId.Send(active);
        sparse = End(); sparse["hero"] = new JsonObject { ["id"] = 2 }; await noHeroId.Send(sparse);
        Check(noHeroId.Gsi.LatestMatch() == null, "cannot disregard an explicit unbound foreign hero ID to borrow the prior name");
        await using var pause = await Probe.Start();
        var run = Packet(); await pause.Send(run);
        var frozen = run.DeepClone().AsObject(); frozen["map"]!["paused"] = true; await pause.Send(frozen); await pause.Send(run);
        Check(pause.Gsi.LatestMatch() == null, "a same-clock/same-source-second duplicate cannot attest a resume");
    }

    private static async Task AgeAndPause()
    {
        await using var x = await Probe.Start();
        var delayed = Economy(); delayed["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 1;
        await x.Send(delayed);
        var sample = x.Gsi.Latest(out var initial);
        Check(initial?.AgeMs is >= 1000 and <= 3000 && sample.AgeMs == initial.AgeMs && sample.Player?.Stats?.Gold == 0,
            "participant stats while dead share the true source age, not a zeroed legacy receipt age");
        await Task.Delay(125); await x.Send(delayed);
        var duplicate = x.Gsi.LatestMatch()!;
        Check(duplicate.AgeMs >= initial!.AgeMs + 80, "reposting identical source and polling cannot renew freshness");
        await Task.Delay(MatchWitness.MaxAgeMs - duplicate.AgeMs + 80);
        Check(x.Gsi.LatestMatch() == null && x.Gsi.Latest().Player is { Stats: null },
            "source evidence/stats expire before receipt-based legacy data; no synthetic end");
        await x.Send(End());
        Check(x.Gsi.LatestMatch() == null, "fresh terminal cannot borrow a timed-out anchor");

        await using var paused = await Probe.Start();
        for (int i = 0; i < 5; i++) {
            if (i > 0) await Task.Delay(800);
            var frozen = Packet(); frozen["map"]!["paused"] = true; await paused.Send(frozen);
        }
        Check(paused.Gsi.LatestMatch() is { Clock: 615, Phase: "playing", Paused: true },
            "fresh paused heartbeats for >3 seconds do not require advancing the game clock");
    }

    private static async Task Stats()
    {
        static OwnState Parse(JsonObject body) => GsiState.Parse(JsonSerializer.SerializeToElement(body));
        var full = Economy();
        var expected = new ParticipantStats(Level: 14, MaxHealth: 1420.5, MaxMana: 670.25, Gold: 0, NetWorth: 14_682,
            LastHits: 143, Denies: 7, Kills: 2, Deaths: 4, Assists: 19, Gpm: 469, Xpm: 566);
        var dead = Parse(full);
        Check(dead is { Alive: false, Farm: null } && dead.Stats == expected, "all twelve explicit flat readings survive death, CC and no Farm");
        full["hero"]!["alive"] = true; full["hero"]!["health"] = 750; full["map"]!["paused"] = true;
        Check(Parse(full) is { Farm: null } cc && cc.Stats == expected, "living paused/stunned hero still has the same foundational economy");

        // Rates, counters and gold are not coerced from arbitrary numeric strings, booleans, nulls or fractions.
        var invalid = Economy(); var p = invalid["player"]!; var h = invalid["hero"]!;
        h["level"] = new JsonObject { ["value"] = 14 }; h["max_health"] = "1420"; h["max_mana"] = 119; // Below current mana.
        p["net_worth"] = 10_000_001; p["last_hits"] = "143"; p["denies"] = 1.25;
        p["kills"] = true; p["deaths"] = 0; p["assists"] = -1; p["gpm"] = 10_001; p["xpm"] = null;
        Check(Parse(invalid).Stats == new ParticipantStats(Gold: 0, Deaths: 0),
            "bad optional fields become individually unknown without erasing observed zeroes");
        var noReadings = Packet(clock: 616);
        noReadings["player"]!["gold_reliable"] = 300; noReadings["player"]!["gold_unreliable"] = 45;
        noReadings["hero"]!["net_worth"] = 999; noReadings["previously"]!["player"] = new JsonObject { ["gold"] = 345 };
        Check(Parse(noReadings).Stats == null, "no stats invented from components, the wrong section, previous readings, health or mana");
        var zeroMax = Packet(); zeroMax["hero"]!["mana"] = 0; zeroMax["hero"]!["max_mana"] = 0;
        zeroMax["hero"]!["max_health"] = 1; zeroMax["hero"]!["level"] = 31;
        Check(Parse(zeroMax).Stats == new ParticipantStats(MaxMana: 0), "explicit zero maximum mana is different from unknown; contradictory HP and level are unknown");
        foreach (var (name, mutate) in new (string, Action<JsonObject>)[] {
            ("grouped spectator", b => b["items"] = new JsonObject { ["team3"] = new JsonObject() }),
            ("menu", b => b["player"]!["activity"] = "menu"), ("replay", b => b["map"]!["is_replay"] = true),
            ("wrong account", b => b["provider"]!["steamid"] = Steam + 1),
            ("stale", b => b["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 9),
            ("unsupported map", b => b["map"]!["name"] = "hero_demo_main"),
            ("uncorrelated terminal", b => b["map"]!["game_state"] = Post)
        }) {
            var b = Economy(); mutate(b);
            Check(Parse(b).Stats == null, "legacy flat projection cannot claim participant economy: " + name);
        }

        await using var x = await Probe.Start();
        await x.Send(full);
        var own = x.Gsi.Latest(out var phase);
        var frame = new FrameMeta("unit-session", "unit-ticket", 1, "dota2-gsi", 1280, 720, own.Player, own.AgeMs, phase);
        var decoded = JsonSerializer.Deserialize<FrameMeta>(JsonSerializer.Serialize(frame, Wire.Json), Wire.Json)!;
        Check(decoded.Player is { Farm: null } && decoded.Player.Stats == expected && decoded.MatchPhase is { Paused: true } &&
            decoded.GsiAgeMs == decoded.MatchPhase.AgeMs, "OwnState JSON carries optional stats with the original phase and source age");
        GsiState.Validate(decoded.Player!);
        var wire = JsonSerializer.SerializeToNode(decoded.Player, Wire.Json)!.AsObject();
        Check(wire["stats"]!.AsObject().Select(f => f.Key).ToHashSet().SetEquals(["level", "maxHealth", "maxMana", "gold",
            "netWorth", "lastHits", "denies", "kills", "deaths", "assists", "gpm", "xpm"]) && !wire.ToJsonString().Contains("private"),
            "only the twelve allowlisted stats enter OwnState JSON");
        wire["stats"]!["gold"] = "123";
        try { wire.Deserialize<OwnState>(Wire.Json); throw new Exception("Wire string was treated as a known numeric stat"); }
        catch (JsonException) { Check(true, "wire rejects mistyped explicit stats rather than creating readings"); }
        wire.Remove("stats");
        var legacy = wire.Deserialize<OwnState>(Wire.Json)!; GsiState.Validate(legacy);
        Check(legacy.Stats == null && legacy.Hero == Hero, "older OwnState JSON has null stats, not twelve zeroes");
        foreach (var bad in new[] { new ParticipantStats(Level: 0), new(Gold: -1), new(NetWorth: int.MaxValue), new(LastHits: -1),
            new(Denies: -1), new(Kills: -1), new(Deaths: -1), new(Assists: -1), new(Gpm: int.MaxValue), new(Xpm: -1),
            new(MaxHealth: double.PositiveInfinity), new(MaxMana: double.NaN), new(MaxHealth: 1), new(MaxMana: 0) }) {
            try { GsiState.Validate(legacy with { Stats = bad }); throw new Exception("Accepted invalid participant stats"); }
            catch (ArgumentException) { Check(true, "out-of-range wire participant stats rejected"); }
        }
        await x.Send(noReadings);
        Check(x.Gsi.Latest(out var live).Player is { Stats: null } && live?.Phase == "playing", "later own packet does not carry forward missing economy");
        var terminal = Economy(); terminal["map"]!["game_state"] = Post; terminal["map"]!["clock_time"] = 617;
        await x.Send(terminal);
        Check(x.Gsi.Latest(out var end).Player?.Stats == expected && end?.Phase == "postgame", "verified full terminal can carry actual final counters");
        var late = End(); late["map"]!["clock_time"] = 617; await x.Send(late);
        Check(x.Gsi.Latest(out end).Player == null && end?.Phase == "postgame", "verified sparse terminal never borrows previous stats");
        await using var cold = await Probe.Start();
        await cold.Send(terminal);
        Check(cold.Gsi.Latest(out end).Player is { Stats: null } && end == null, "uncorrelated full terminal with valid-looking totals cannot publish stats");
    }

    private static async Task Builds()
    {
        var dead = Economy(); dead["map"]!["paused"] = true;
        dead["provider"]!["version"] = 6989; dead["map"]!["installed_build"] = 6989;
        await using var plain = await Probe.Start();
        await plain.Send(dead);
        Check(plain.Gsi.LatestMatch() is { Hero: Hero, Phase: "playing", Paused: true, InstalledBuild: null },
            "provider/map version claims cannot confirm layout without a configured installation");

        string dir = Directory.CreateTempSubdirectory("match-witness-build-").FullName;
        string game = Path.Combine(dir, "game"), dota = Directory.CreateDirectory(Path.Combine(game, "dota")).FullName;
        string path = Path.Combine(dota, "steam.inf");
        void Manifest(int client, int server, int app = 570) =>
            File.WriteAllText(path, $"appID={app}\nClientVersion={client}\nServerVersion={server}\n");
        try {
            Manifest(6989, 6989);
            await using var x = await Probe.Start(dir);
            await x.Send(dead);
            Check(x.Gsi.Latest(out var phase).Player is { Alive: false, Farm: null } &&
                phase is { Hero: Hero, Paused: true, InstalledBuild: 6989 }, "dead/stunned/paused non-farmer reports the locally verified build");
            using (var locked = File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) {
                Check(x.Gsi.LatestMatch()?.InstalledBuild == 6989, "polls only return the last verified sample and do not re-read the installation");
                await x.Send(dead);
                Check(x.Gsi.LatestMatch() is { Phase: "playing", InstalledBuild: null }, "unreadable manifest on a new packet drops prior positive build");
            }
            Manifest(6990, 6990); await x.Send(dead);
            Check(x.Gsi.LatestMatch()?.InstalledBuild == 6990, "verified incompatible build wins over provider claiming the supported build");
            File.Delete(path);
            var active = Packet(clock: 616); active["hero"]!["xpos"] = 450; active["hero"]!["ypos"] = 670; active["provider"]!["version"] = 6989;
            await x.Send(active);
            Check(x.Gsi.Latest(out phase).Player is { Farm: null } && phase is { Phase: "playing", InstalledBuild: null },
                "missing file neither certifies wards nor turns a known incompatible installation into a farm fallback");
            Manifest(6989, 6990); await x.Send(active);
            Check(x.Gsi.LatestMatch() is { Phase: "playing", InstalledBuild: null }, "conflicting local client/server versions cannot confirm layout");
            Manifest(6989, 6989, 730); await x.Send(active);
            Check(x.Gsi.LatestMatch() is { Phase: "playing", InstalledBuild: null }, "another app's local manifest cannot confirm the Dota build");
            // The independent minute epoch deliberately will not resume from the withdrawal's source second.
            Manifest(6989, 6989); await Task.Delay(1050);
            active["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds(); await x.Send(active);
            Check(x.Gsi.Latest(out phase).Player?.Farm?.InstalledBuild == 6989 && phase?.InstalledBuild == 6989,
                "eligible farm and independent match phase agree on the same local manifest sample");
            var phaseOnly = Packet(clock: 617); phaseOnly["hero"] = new JsonObject { ["name"] = Hero };
            await x.Send(phaseOnly);
            Check(x.Gsi.Latest(out phase).Player == null && phase?.InstalledBuild == 6989,
                "fully witnessed non-farm packet still samples the installation without health or inventory");
            Manifest(6991, 6991);
            var end = End(); end["map"]!["clock_time"] = 618; await x.Send(end);
            Check(x.Gsi.LatestMatch() is { Phase: "postgame", InstalledBuild: 6991 }, "correlated hero-absent terminal samples its own local build");
            File.Delete(path); await x.Send(end);
            Check(x.Gsi.LatestMatch() is { Phase: "postgame", InstalledBuild: null }, "sparse terminal cannot inherit a previous build on local read failure");

            Manifest(6989, 6989);
            await using var unrelated = await Probe.Start(dir);
            var observer = Economy(); observer["allplayers"] = new JsonObject();
            await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => unrelated.Raw(observer.ToJsonString())));
            await unrelated.Send(End());
            Check(unrelated.Gsi.LatestMatch() == null, "observer traffic and uncorrelated terminal cannot publish build evidence from a good install");
            await using var switched = await Probe.Start(dir);
            active["provider"]!["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            await switched.Send(active);
            Check(switched.Gsi.LatestMatch()?.InstalledBuild == 6989, "positive build control before directory switch");
            await switched.HoldOlderBody(active, () => { switched.Gsi.UseDotaInstall(null); return Task.CompletedTask; });
            Check(switched.Gsi.LatestMatch() == null, "old install/request cannot carry its attestation through a directory reset");
        } finally {
            File.Delete(path); Directory.Delete(dota); Directory.Delete(game); Directory.Delete(dir);
        }
    }

    private static async Task Race()
    {
        await using var x = await Probe.Start();
        await x.Send(Packet());
        await x.HoldOlderBody(Packet(), async () => {
            await x.Send(Packet(match: B));
            Check(x.Gsi.LatestMatch()?.MatchId == B, "new complete participant packet wins while older request body is pending");
        });
        Check(x.Gsi.Latest(out var current).Player?.MatchId == B && current?.MatchId == B,
            "late completion cannot overwrite either half of the atomic raw sample");
        await x.HoldOlderBody(Packet(Disconnect, match: B), async () => {
            await x.Send(End(B));
            Check(x.Gsi.LatestMatch() == null, "terminal cannot correlate past an unexamined body that could withdraw ownership");
        });
        await using var reset = await Probe.Start();
        await reset.Send(Packet());
        await reset.HoldOlderBody(Packet(), () => {
            reset.Gsi.UseDotaInstall("different-install"); return Task.CompletedTask;
        });
        await reset.Send(End());
        Check(reset.Gsi.LatestMatch() == null, "an outstanding previous-install body cannot seed a postgame after reset");

        await using var minute = await Probe.Start();
        var own = Packet(); own["hero"]!["xpos"] = 50; own["hero"]!["ypos"] = 150;
        await minute.Send(own);
        Check(MinuteFarmContext.Eligible(minute.Gsi.Latest().Player), "positive independent legacy minute control before an unread raw gap");
        await minute.HoldOlderBody(Packet(Disconnect), async () => {
            await minute.Send(own);
            Check(minute.Gsi.LatestMatch() is { Phase: "playing" } && !MinuteFarmContext.Eligible(minute.Gsi.Latest().Player),
                "a full participant may start afresh, but an unread gap cannot silently retain the minute epoch");
        });
    }

    private static async Task TerminalRebind()
    {
        await using var x = await Probe.Start();
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        using var oldLink = new GameLink(host.Pair(IPAddress.Loopback));
        using var newLink = new GameLink(host.Pair(IPAddress.Loopback));
        await oldLink.Connect(default);
        int oldReceipts = 0, newReceipts = 0;
        oldLink.BindPostgame(x.Gsi, "dota2-gsi", () => Interlocked.Increment(ref oldReceipts));
        await x.Send(Packet());
        await x.HoldOlderBody(End(), async () => {
            await newLink.Connect(default);
            newLink.BindPostgame(x.Gsi, "dota2-gsi", () => Interlocked.Increment(ref newReceipts));
        });
        Check(x.Gsi.LatestMatch()?.Phase == "postgame" && oldReceipts == 0 && newReceipts == 0,
            "terminal whose body crossed a re-pair is current raw evidence but belongs to neither active outbox");
        await x.Send(End());
        Check(x.Gsi.LatestMatch()?.Phase == "postgame" && newReceipts == 0,
            "later terminal heartbeat cannot relabel the pre-pair raw transition into a new session");
        await x.Send(Packet(match: B)); await x.Send(End(B)); await x.Send(End(B));
        var menu = Packet(match: B); menu["player"]!["activity"] = "menu"; await x.Send(menu);
        Check(x.Gsi.LatestMatch() == null && oldReceipts == 0 && newReceipts == 1,
            "new pairing latches its own verified match exactly once before the menu, independently of polling");
    }

    private static async Task ReplayRecordedShapes(string[] paths)
    {
        if (paths.Length == 0) throw new ArgumentException("Expected one or more flat player .json captures");
        int pairs = 0;
        foreach (var path in paths) {
            var doc = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
            var p = (doc["payload"] ?? doc).AsObject();
            if (p["map"]?["game_state"]?.GetValue<string>() != Playing ||
                p["player"]?["activity"]?.GetValue<string>() != "playing" ||
                p["hero"]?["name"]?.GetValue<string>() is not { } hero || p["hero"]?["permanent_buffs"] is not JsonObject)
                throw new ArgumentException("Expected a recorded in-progress flat hero with permanent_buffs");
            var provider = p["provider"]!.AsObject(); provider["timestamp"] = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (provider.ContainsKey("steamid")) provider["steamid"] = Steam.ToString();
            if (provider.ContainsKey("accountid")) provider["accountid"] = Account.ToString();
            p["auth"] = new JsonObject { ["token"] = Token }; p["map"]!["matchid"] = A;
            p["player"]!["steamid"] = Steam.ToString(); p["player"]!["accountid"] = Account.ToString(); p["player"]!["name"] = "test-player";
            await using var x = await Probe.Start();
            await x.Send(p);
            Check(x.Gsi.LatestMatch() is { Phase: "playing" } live && live.Hero == hero, "captured flat playing shape");
            p["map"]!["game_state"] = Post; p.Remove("hero");
            await x.Send(p);
            Check(x.Gsi.LatestMatch() is { MatchId: A, Phase: "postgame" } end && end.Hero == hero, "correlated sparse end");
            pairs++;
        }
        Console.WriteLine($"{pairs}/{paths.Length} recorded shapes established a playing -> constructed postgame pair.");
    }

    public static async Task Main(string[] args)
    {
        if (args is ["--recorded-shapes", .. var paths]) { await ReplayRecordedShapes(paths); return; }
        WireContract();
        await Lifecycle();
        await Task.WhenAll(RecordedBuffShape(), RejectCases(), Boundaries(), AgeAndPause(), Race(), TerminalRebind(), Stats(), Builds());
        Console.WriteLine($"Match witness: {passed} checks passed.");
    }
}
