using System.Net;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class MinuteFarmModelTests
{
    private static object Message(string text) => new { type = "message", role = "assistant", status = "completed",
        content = new[] { new { type = "output_text", text } } };
    private static string Envelope(params object[] output) => JsonSerializer.Serialize(new { status = "completed", output,
        usage = new { input_tokens = 217, output_tokens = 19 } });
    private static HttpResponseMessage Reply(string body) => new(HttpStatusCode.OK) {
        Content = new StringContent(body, Encoding.UTF8, "application/json") };
    private static bool Keys(JsonElement obj, params string[] keys) =>
        obj.EnumerateObject().Select(p => p.Name).ToHashSet().SetEquals(keys);
    private static async Task<T> Throws<T>(Func<Task> call) where T : Exception
    {
        try { await call(); } catch (T error) { return error; }
        throw new Exception($"Expected minute adapter to throw {typeof(T).Name}");
    }

    public static async Task Run(Action<bool, string> check)
    {
        var own = new OwnState("9898987878787654", "npc_dota_hero_luna", "radiant", 725, true, 881, 203, -1300, -2400,
            [new("item_mask_of_madness", null, 11, "slot0"), new("item_pogo_stick", null, 2, "neutral0"),
             new("item_bfury", null, 0, "slot6"), new("item_blink", null, 0, "stash0"), new("item_manta", null, 0),
             new("item_tpscroll", null, 0, "teleport0"), new("item_pupils_gift", null, 0, "preserved_neutral0")],
            [new("luna_lucent_beam", 3, 4, "ability0")]) {
            Farm = new(true, true, false, MoveSpeed: 331, AttackDamage: 95, AttackSpeed: 162, Level: 10,
                Epoch: Guid.Parse("fd0216c7-8dc4-488e-8e31-7adb3104e789"), MaxHealth: 1300, MaxMana: 520, LastHits: 89,
                Silenced: true, Muted: false) };
        var lane = new FarmStop("lane-bot", "Bottom lane return point", "lane", -1200, -2700, 6, 6) {
            WalkUnits = 1100, CostBasis = "static-lane-arrival-no-wave-work" };
        var camp = new FarmStop("small-camp", "Small camp", "camp", -1800, -2800, 8, 33) {
            WalkUnits = 1480, CostBasis = "visit-deadline-not-a-clear", PackWindowSeconds = 43, ModeledRosters = 2, CatalogRosters = 4 };
        var back = lane with { ArrivalSeconds = 42, FinishSeconds = 42, WalkUnits = 2000 };
        var scenario = new FarmMinuteScenario(12, 725, own.Hero, own.Team, [
            new(lane.Id, [lane]), new("small-camp__lane-bot", [camp, back]) ]) {
            Timing = new(10, "own-GSI", 331, "own-GSI-assumed-held", 84, "own-GSI-scalar-minus-KV-roll-width",
                162, "own-GSI-total-IAS-assumed-held", 1.7) };
        const string choice = """{"optionId":"small-camp__lane-bot"}""", abstain = """{"optionId":null}""";
        string config = Path.GetTempFileName();
        try {
            File.WriteAllText(config, "AOAI_ENDPOINT=https://minute-test.services.ai.azure.com/\nFOUNDRY_KEY=fixture-not-a-credential\n");
            using var mock = new MockHandler();
            using var cloud = new CloudCoach(config, "test-deployment", "PRIVATE-LESSON-NOT-FOR-MINUTES", mock, timeout: TimeSpan.FromSeconds(5));
            mock.Respond = () => Reply(Envelope(new { type = "reasoning", summary = Array.Empty<object>() }, Message(choice)));
            check(await cloud.ChooseMinuteFarm(own, scenario, default) == scenario.Options[1].Id && mock.Calls == 1,
                "minute model selects the whole second option, not its first stop or a default");
            using var request = JsonDocument.Parse(mock.Body);
            var body = request.RootElement;
            var messages = body.GetProperty("input");
            var parts = messages[0].GetProperty("content");
            check(body.GetProperty("model").GetString() == "test-deployment" && !body.GetProperty("store").GetBoolean() &&
                body.GetProperty("max_output_tokens").GetInt32() is > 0 and <= 512 &&
                messages.GetArrayLength() == 1 && parts.GetArrayLength() == 1 && parts[0].GetProperty("type").GetString() == "input_text" &&
                !mock.Body.Contains(own.MatchId) && !mock.Body.Contains(own.Farm!.Epoch.ToString()) &&
                !mock.Body.Contains("PRIVATE-LESSON") && !mock.Body.Contains("fixture-not-a-credential"),
                "minute request is text only, bounded, non-stored and excludes match ID, legacy lessons and credential");
            using var input = JsonDocument.Parse(parts[0].GetProperty("text").GetString()!);
            var data = input.RootElement;
            var self = data.GetProperty("self");
            var options = data.GetProperty("options");
            var stops = options[1].GetProperty("stops");
            check(Keys(data, "minute", "clock", "horizonSeconds", "mapBasis", "conditions", "self", "timing", "options", "historicalPrior", "emptyLaneMeetings") &&
                data.GetProperty("emptyLaneMeetings").GetArrayLength() == 3 &&
                Keys(self, "hero", "team", "alive", "health", "maxHealth", "mana", "maxMana", "lastHits", "silenced", "muted", "x", "y",
                    "level", "moveSpeed", "attackDamage", "attackSpeed", "items", "abilities") &&
                self.GetProperty("health").GetInt32() == 881 && self.GetProperty("mana").GetInt32() == 203 &&
                self.GetProperty("silenced").GetBoolean() && !self.GetProperty("muted").GetBoolean() &&
                self.GetProperty("maxHealth").GetInt32() == 1300 && self.GetProperty("maxMana").GetInt32() == 520 && self.GetProperty("lastHits").GetInt32() == 89 &&
                self.GetProperty("level").GetInt32() == 10 && self.GetProperty("moveSpeed").GetInt32() == 331 &&
                self.GetProperty("attackDamage").GetInt32() == 95 && self.GetProperty("attackSpeed").GetInt32() == 162 &&
                self.GetProperty("abilities")[0].GetProperty("name").GetString() == "luna_lucent_beam" &&
                self.GetProperty("abilities")[0].GetProperty("level").GetInt32() == 3 &&
                self.GetProperty("abilities")[0].GetProperty("cooldownSeconds").GetInt32() == 4,
                "minute tactical input explicitly projects own resources, stats, inventory and ability cooldowns");
            var items = self.GetProperty("items");
            check(items.GetArrayLength() == 2 && items[0].GetProperty("slot").GetString() == "slot0" &&
                items[0].GetProperty("name").GetString() == "item_mask_of_madness" && items[0].GetProperty("cooldownSeconds").GetInt32() == 11 &&
                items[1].GetProperty("slot").GetString() == "neutral0" && items[1].GetProperty("name").GetString() == "item_pogo_stick" &&
                !new[] { "item_bfury", "item_blink", "item_manta", "item_tpscroll", "item_pupils_gift" }.Any(mock.Body.Contains),
                "backpack, stash, preserved, teleport and unknown item slots are not sent as equipped items");
            check(data.GetProperty("clock").GetInt32() == 725 && data.GetProperty("horizonSeconds").GetInt32() == 55 &&
                data.GetProperty("mapBasis").GetString() == scenario.MapBasis && data.GetProperty("conditions").GetString() == scenario.Conditions &&
                options.GetArrayLength() == 2 && options[0].GetProperty("stops")[0].GetProperty("kind").GetString() == "lane" &&
                stops.GetArrayLength() == 2 && stops[0].GetProperty("kind").GetString() == "camp" &&
                stops[1].GetProperty("arrivalSeconds").GetInt32() == 42 && stops[1].GetProperty("finishSeconds").GetInt32() == 42 &&
                stops[1].GetProperty("costBasis").GetString() == "static-lane-arrival-no-wave-work" &&
                JsonElement.DeepEquals(data.GetProperty("historicalPrior"), JsonSerializer.SerializeToElement(MinuteFarmReplayPrior.For(own.Hero, 12), Wire.Json)),
                "minute input distinguishes a static lane return from camp-then-lane, with an explicitly historical local prior");
            var budget = stops[0];
            check(budget.GetProperty("finishSeconds").GetInt32() - budget.GetProperty("arrivalSeconds").GetInt32() == 25 &&
                budget.GetProperty("costBasis").GetString() == "visit-deadline-not-a-clear" && budget.GetProperty("packWindowSeconds").GetInt32() == 43 &&
                budget.GetProperty("walkUnits").GetInt32() == 1480 && budget.GetProperty("modeledRosters").GetInt32() == 2 &&
                budget.GetProperty("catalogRosters").GetInt32() == 4 && body.GetProperty("instructions").GetString()!.Contains("LEAVE-BY"),
                "25s camp leave-by budget stays distinct from a longer reference pack window and partial roster coverage");
            var timing = data.GetProperty("timing");
            check(Keys(timing, "level", "levelBasis", "moveSpeed", "moveBasis", "damagePerHit", "damageBasis", "attackSpeed", "attackSpeedBasis", "baseAttackTime") &&
                timing.GetProperty("damagePerHit").GetInt32() == 84 && timing.GetProperty("baseAttackTime").GetDouble() == 1.7 &&
                timing.GetProperty("levelBasis").GetString() == scenario.Timing!.LevelBasis && timing.GetProperty("moveBasis").GetString() == scenario.Timing.MoveBasis &&
                timing.GetProperty("damageBasis").GetString() == scenario.Timing.DamageBasis && timing.GetProperty("attackSpeedBasis").GetString() == scenario.Timing.AttackSpeedBasis,
                "effective simulation timing, BAT and source of each assumption are separate from live hero readings");
            var format = body.GetProperty("text").GetProperty("format");
            var schema = format.GetProperty("schema");
            var properties = schema.GetProperty("properties");
            check(format.GetProperty("type").GetString() == "json_schema" && format.GetProperty("strict").GetBoolean() &&
                !schema.GetProperty("additionalProperties").GetBoolean() && Keys(properties, "optionId") &&
                schema.GetProperty("required").EnumerateArray().Select(v => v.GetString()).SequenceEqual(["optionId"]) &&
                properties.GetProperty("optionId").GetProperty("enum").EnumerateArray().Select(v => v.GetString()).ToHashSet()
                    .SetEquals(["lane-bot", "small-camp__lane-bot", null]), "minute response schema allows only supplied option IDs and explicit null");

            mock.Respond = () => Reply(Envelope(Message(abstain)));
            var measured = await cloud.ChooseMinuteFarmWithMetrics(own, scenario, default);
            check(measured is { OptionId: null, InputTokens: 217, OutputTokens: 19, ElapsedMs: >= 0 } &&
                await cloud.ChooseMinuteFarm(own, scenario, default) == null, "explicit abstention is a valid decision with per-call provider usage");
            mock.Respond = () => Reply(JsonSerializer.Serialize(new { status = "completed", output = new[] { Message("""{"optionId":"lane-bot"}""") } }));
            var later = scenario with { Minute = 40, Clock = 2405, Options = [scenario.Options[0]], Timing = new(
                1, "assumed-level-1", 325, "6989-hero-base-no-boots-or-effects", 46, "6989-min-weapon-plus-base-primary-at-level",
                119, "6989-base-IAS-plus-AGI-at-level", 1.7) };
            var unmeasured = await cloud.ChooseMinuteFarmWithMetrics(own with { Clock = later.Clock, Farm = new(true, true, false) }, later, default);
            using var sparseRequest = JsonDocument.Parse(mock.Body);
            using var sparse = JsonDocument.Parse(sparseRequest.RootElement.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
            check(unmeasured is { OptionId: "lane-bot", InputTokens: null, OutputTokens: null } &&
                new[] { "moveSpeed", "maxHealth", "maxMana", "lastHits" }.All(k => sparse.RootElement.GetProperty("self").GetProperty(k).ValueKind == JsonValueKind.Null) &&
                sparse.RootElement.GetProperty("timing").GetProperty("moveBasis").GetString() == "6989-hero-base-no-boots-or-effects" &&
                sparse.RootElement.GetProperty("historicalPrior").ValueKind == JsonValueKind.Null,
                "a supplied lane-only option is selectable; assumed speed is not presented as observed, missing usage/prior stays null");

            async Task Invalid(string raw, string name) {
                mock.Respond = () => Reply(raw);
                int before = mock.Calls;
                await Throws<InvalidOperationException>(() => cloud.ChooseMinuteFarm(own, scenario, default));
                check(mock.Calls == before + 1, "minute model rejects " + name + " without retry or fallback");
            }
            foreach (var (text, name) in new[] {
                ("""{"optionId":"invented"}""", "unsupplied ID"), ("""{"optionId":"small-camp"}""", "stop ID not offered as a whole option"),
                ("""{"optionId":"LANE-BOT"}""", "case-normalized ID"), ("""{"optionId":" lane-bot"}""", "trimmed ID"),
                ("""{"optionId":["lane-bot","small-camp__lane-bot"]}""", "multiple choices"), ("{}", "missing abstention field"),
                ("""{"optionId":null,"destination":"go mid","x":1,"y":2}""", "directions beside abstention"),
                ("""{"optionId":"lane-bot","text":"go mid"}""", "text beside a choice"),
                ("""{"optionId":null,"optionId":"lane-bot"}""", "duplicate choice keys"), ("abstain", "unstructured text") })
                await Invalid(Envelope(Message(text)), name);
            await Invalid(Envelope(Message(choice), Message(abstain)), "ambiguous messages");
            await Invalid(Envelope(new { type = "message", content = new[] {
                new { type = "output_text", text = """{"optionId":""" }, new { type = "output_text", text = "null}" } } }), "concatenated fragments");
            await Invalid(Envelope(new { type = "message", content = new[] { new { type = "refusal", refusal = "no" } } }), "provider refusal");
            await Invalid(Envelope(Message(choice), new { type = "function_call", name = "move" }), "unexpected action beside choice");
            await Invalid(Envelope(Message(abstain)).Replace("\"completed\"", "\"incomplete\""), "incomplete response");
            await Invalid("{}", "missing provider output");

            var changing = scenario with { Options = scenario.Options.ToArray() };
            mock.Respond = () => {
                changing.Options[0] = changing.Options[0] with { Id = "late-id" };
                return Reply(Envelope(Message("""{"optionId":"late-id"}""")));
            };
            await Throws<InvalidOperationException>(() => cloud.ChooseMinuteFarm(own, changing, default));
            check(changing.Options[0].Id == "late-id", "validation uses the offered ID snapshot even if the caller's option array changes in flight");

            int sent = mock.Calls;
            foreach (var bad in new[] { scenario with { Team = "dire" }, scenario with { Options = [] },
                scenario with { Options = [scenario.Options[0], scenario.Options[0]] },
                scenario with { Options = [new("empty", [])] },
                scenario with { Options = [new("overlap", [camp, back with { ArrivalSeconds = 18, FinishSeconds = 18 }])] },
                scenario with { Options = [new("fake-lane-clear", [lane with { FinishSeconds = 20 }])] },
                scenario with { Options = [new("ends-next-minute", [camp with { FinishSeconds = 56 }])] } })
                await Throws<ArgumentException>(() => cloud.ChooseMinuteFarm(own, bad, default));
            foreach (var farm in new[] { null, own.Farm with { OwnParticipant = false }, own.Farm with { InProgress = false },
                own.Farm with { Paused = null } })
                await Throws<ArgumentException>(() => cloud.ChooseMinuteFarm(own with { Farm = farm }, scenario, default));
            await Throws<OperationCanceledException>(() => cloud.ChooseMinuteFarm(own, scenario, new CancellationToken(true)));
            check(mock.Calls == sent, "invalid scenarios, unconfirmed participants and pre-cancellation spend no request");

            mock.Respond = () => new(HttpStatusCode.TooManyRequests) { Content = new StringContent("UNTRUSTED-ERROR-BODY") };
            var upstream = await Throws<ModelProviderException>(() => cloud.ChooseMinuteFarm(own, scenario, default));
            mock.Respond = () => throw new HttpRequestException("simulated transport failure");
            await Throws<HttpRequestException>(() => cloud.ChooseMinuteFarm(own, scenario, default));
            check(mock.Calls == sent + 2 && upstream.StatusCode == 429 && !upstream.Message.Contains("UNTRUSTED"),
                "minute HTTP and transport failures propagate once, without provider body or default option");
            byte[] oversized = Encoding.UTF8.GetBytes(new string(' ', 65536) + Envelope(Message(choice)));
            mock.Respond = () => new(HttpStatusCode.OK) { Content = new Chunked((s, t) => s.WriteAsync(oversized.AsMemory(), t).AsTask()) };
            await Throws<HttpRequestException>(() => cloud.ChooseMinuteFarm(own, scenario, default));
            check(mock.Calls == sent + 3, "even a syntactically valid chunked response is byte-bounded");

            mock.Respond = () => new(HttpStatusCode.OK) { Content = new Chunked((_, t) => Task.Delay(Timeout.Infinite, t)) };
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(12));
            await Throws<OperationCanceledException>(() => cloud.ChooseMinuteFarm(own, scenario, watchdog.Token).WaitAsync(watchdog.Token));
            check(!watchdog.IsCancellationRequested && mock.Calls == sent + 4, "configured minute timeout includes a stalled body after successful headers");
        }
        finally { File.Delete(config); }
    }

    private sealed class MockHandler : HttpMessageHandler
    {
        public int Calls;
        public string Body = "";
        public Func<HttpResponseMessage> Respond = () => throw new Exception("Unexpected minute request");
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Method != HttpMethod.Post || request.RequestUri?.AbsoluteUri != "https://minute-test.services.ai.azure.com/openai/v1/responses" ||
                !request.Headers.Contains("api-key")) throw new Exception("Not the configured dedicated Foundry transport");
            Calls++;
            Body = await request.Content!.ReadAsStringAsync(cancellationToken);
            return Respond();
        }
    }

    private sealed class Chunked(Func<Stream, CancellationToken, Task> write) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken) => write(stream, cancellationToken);
    }
}
