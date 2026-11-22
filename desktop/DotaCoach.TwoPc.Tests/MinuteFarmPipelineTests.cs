using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

/// <summary>Same path as the two shipping apps, but with a synthetic own-player GSI packet and an
/// in-process HTTP model double: no Dota window, pictures, provider key, or paid API calls required.</summary>
internal static class MinuteFarmPipelineTests
{
    internal static async Task Run(Action<bool, string> check)
    {
        await using var gsi = await GsiReceiver.Start(0);
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{gsi.Port}/") };
        string token = System.Text.RegularExpressions.Regex.Match(gsi.Config, "\"token\" \"([A-F0-9]+)\"").Groups[1].Value;
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        await link.Connect(default);
        long seq = 0;
        async Task<FrameView> Own(int clock, bool paused = false, bool silenced = false)
        {
            const string me = "76561198000000001";
            using var accepted = await http.PostAsJsonAsync("gsi", new {
                auth = new { token },
                provider = new { name = "Dota 2", appid = 570, version = 47, steamid = me, timestamp = DateTimeOffset.UtcNow.ToUnixTimeSeconds() },
                map = new { name = "dota", matchid = "9130000042", clock_time = clock, game_time = clock + 100,
                    game_state = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", paused, daytime = true },
                player = new { steamid = me, team_name = "radiant", activity = "playing", last_hits = 85 },
                hero = new { id = 48, name = "npc_dota_hero_luna", level = 11, alive = true, health = 1200, max_health = 1300,
                    mana = 580, max_mana = 660, xpos = 3978, ypos = -5027, stunned = false, silenced, hexed = false,
                    disarmed = false, rooted = false, muted = false, command_restricted = false, move_speed = 370,
                    attack_damage = 108, attack_speed = 175 },
                items = new { slot0 = new { name = "item_power_treads", cooldown = 0 } },
                abilities = new { ability0 = new { name = "luna_moon_glaive", level = 3, cooldown = 0, passive = true } },
            }, Wire.Json);
            accepted.EnsureSuccessStatusCode();
            var (p, age) = gsi.Latest();
            if (!MinuteFarmContext.Eligible(p)) { p = null; age = null; }
            var t = await link.GetTicket(default);
            var meta = new FrameMeta(link.SessionId!, t.Id, ++seq, "dota2-gsi", 1920, 1080, p, age);
            await link.UploadMinuteState(meta, Environment.TickCount64, default);
            return host.Mailbox.Latest()!;
        }
        var director = new MinuteFarmDirector();
        var layout = MinimapLayout.Suggest(1920, 1080, right: true);
        var source = await Own(600);
        check(source.Meta.Player is not null && source.Jpeg.Length == 0 && source.Video == null &&
            !CloudCoach.Eligible(source, localBotOnly: false), "GSI-only paired transport sends own state and zero pixels; legacy image model cannot run");
        check(director.TryStart(source, layout) && !director.TryStart(source, layout),
            "first eligible mid-match minute start is independent of launch time; GSI heartbeats coalesce");
        string file = Path.GetTempFileName();
        try {
            File.WriteAllText(file, "AOAI_ENDPOINT=https://fixture-test.openai.azure.com\nFOUNDRY_KEY=mock-not-a-key");
            var fake = new MinuteResponseFixture();
            using var provider = new CloudCoach(file, "test-deployment", "", fake, localBotOnly: false);
            FarmOption? chosen = null;
            async Task<string?> Select(OwnState p, FarmMinuteScenario s, CancellationToken ct)
            {
                // The fixture intentionally chooses a lane-return option, not the simulation's first route.
                chosen = s.Options.Where(o => o.Stops.Any(stop => stop.Kind == "lane"))
                    .OrderBy(o => o.Stops.Length).First();
                fake.Selection = chosen.Id;
                await Own(604, silenced: true); // A self-silence during thinking must still allow walking and basic attacks.
                return await provider.ChooseMinuteFarm(p, s, ct);
            }
            var result = await director.DecideAndPublish(host.Mailbox, source, layout, true, () => true, Select, default);
            var received = await link.Poll(default);
            check(result.Outcome == FarmMinuteOutcome.Published && fake.Calls == 1 && chosen != null &&
                result.Stops.Select(s => s.Id).SequenceEqual(chosen.Stops.Take(result.Stops.Length).Select(s => s.Id)) &&
                result.Stops.Any(s => s.Kind == "lane"), "authenticated player GSI → 7.42 simulation → mocked Foundry selection → selected lane itinerary only");
            check(received is { Kind: "minute-farm-route", FarmMinute: 10, RemainingMs: > 15000 and <= 56000,
                    Route: { } route, NextSteps: { } steps } &&
                received.FrameSequence == source.Meta.Sequence && route.All(layout.Contains) &&
                route.Length == steps.Length && steps.Any(s => s.Contains("有线再收")),
                "ordered conditional route reaches actual game-link protocol on mirrored HUD until minute boundary, not 3-second legacy TTL");
            var whileSilenced = await Own(610, silenced: true);
            var currentOwn = gsi.Latest();
            check(whileSilenced.Meta.Player?.Farm?.Silenced == true && received != null &&
                link.CanRenderMinuteRoute(received, currentOwn.Player, currentOwn.AgeMs) &&
                (await link.Poll(default))?.Id == received.Id,
                "Luna self-silencing while thinking and staying silenced after publication does not revoke the paid route");
            check(!director.TryStart(whileSilenced, layout), "no second paid request for another same-minute packet");
            await Own(611, paused: true);
            check(await link.Poll(default) == null, "a pause withdraws the route immediately on the GSI-only link");
            // Valve source timestamps are integer seconds. Packets from the withdrawal second
            // intentionally cannot attest a resume even after a new transport session is created.
            await Task.Delay(1050);
            await link.End(default); await link.Connect(default);
            check(!director.TryStart(await Own(620), layout), "reconnecting or rearming the same real match cannot charge again in that minute");
            var late = await Own(660);
            check(director.TryStart(late, layout), "new game minute unlocks exactly one fresh decision");
            var discarded = await director.DecideAndPublish(host.Mailbox, late, layout, true, () => true,
                async (_, s, _) => { await Own(720); return s.Options[0].Id; }, default);
            check(discarded.Outcome == FarmMinuteOutcome.ContextEnded && await link.Poll(default) == null,
                "response crossing an in-game minute is dropped, not retagged to the new clock");
            var abstainSource = await Own(721);
            var abstained = await director.DecideAndPublish(host.Mailbox, abstainSource, layout, true, () => true,
                (_, _, _) => Task.FromResult<string?>(null), default);
            check(abstained.Outcome == FarmMinuteOutcome.Abstained && await link.Poll(default) == null &&
                !director.TryStart(abstainSource, layout), "model abstention gives no fallback route and consumes that minute");
            var shortening = await Own(780);
            FarmOption? full = null;
            var shortened = await director.DecideAndPublish(host.Mailbox, shortening, layout, true, () => true,
                async (_, s, _) => {
                    full = s.Options.First(o => o.Stops.Length >= 2 &&
                        o.Stops[^1].FinishSeconds - o.Stops[0].FinishSeconds >= 8);
                    // Only enough time remains for the first stop, from the same position.
                    await Own(840 - full.Stops[0].FinishSeconds - 4);
                    return full.Id;
                }, default);
            check(shortened.Outcome == FarmMinuteOutcome.Published && shortened.Stops.Length >= 1 &&
                shortened.Stops.Length < full!.Stops.Length &&
                shortened.Stops.Select(s => s.Id).SequenceEqual(full.Stops.Take(shortened.Stops.Length).Select(s => s.Id)) &&
                (await link.Poll(default))?.FrameSequence == shortening.Meta.Sequence,
                "response delayed within the minute publishes only its feasible prefix, never a replacement option");
        } finally { File.Delete(file); }
        check(MinuteFarmReplayPrior.For("npc_dota_hero_luna", 12) is { ReplayMatches: > 0 } &&
            MinuteFarmReplayPrior.For("npc_dota_hero_troll_warlord", 12) is { MatchedMinutes: > 0 } &&
            MinuteFarmReplayPrior.For("npc_dota_hero_luna", 40) == null, "shipped anonymous replay priors match hero and observed time band only");
    }

    private sealed class MinuteResponseFixture : HttpMessageHandler
    {
        public int Calls; public string Selection = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellation)
        {
            Calls++;
            using var doc = JsonDocument.Parse(await request.Content!.ReadAsStringAsync(cancellation));
            var schema = doc.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema");
            string field = schema.GetProperty("required")[0].GetString()!;
            string text = JsonSerializer.Serialize(new Dictionary<string, string> { [field] = Selection });
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                id = "synthetic", status = "completed", model = "test-deployment", usage = new { input_tokens = 180, output_tokens = 10 },
                output = new[] { new { type = "message", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text } } } },
            }), Encoding.UTF8, "application/json") };
        }
    }
}
