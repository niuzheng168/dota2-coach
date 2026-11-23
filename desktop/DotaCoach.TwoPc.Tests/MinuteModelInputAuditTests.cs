using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static class MinuteModelInputAuditTests
{
    private sealed class Transport(Action<JsonElement> onSend) : HttpMessageHandler
    {
        public int Calls;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            if (request.Method == HttpMethod.Get) return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                data = new[] { CopilotGateway.Astra, CopilotGateway.Sol }.Select(id => new {
                    id, display_name = $"{id} (github-copilot)", supported_endpoints = new[] { "/responses" } })
            }) };
            Calls++;
            using var body = JsonDocument.Parse(await request.Content!.ReadAsByteArrayAsync(ct));
            onSend(body.RootElement);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(new {
                status = "completed", model = body.RootElement.GetProperty("model").GetString(),
                output = new[] { new { type = "message", content = new[] { new { type = "output_text", text = """{"optionId":null}""" } } } }
            }) };
        }
    }

    public static async Task Run(Action<bool, string> check)
    {
        var temp = Directory.CreateTempSubdirectory("minute-input-audit-");
        string root = Path.Combine(temp.FullName, "matches"), config = Path.Combine(temp.FullName, "fixture.env");
        const string id = "9112345655", session = "private-session", hero = "npc_dota_hero_luna", enemy = "npc_dota_hero_lion";
        const string deployment = "Farm.Blue_v2-7.42", sensitiveAlias = "gho_OnlyASyntheticValue";
        var epoch = Guid.NewGuid();
        long t = 10_000, frame = 0;
        FrameView Sample(double clock, int hp, int? gold)
        {
            var own = new OwnState(id, hero, "radiant", clock, true, hp, 409, hp + .25, -409.75,
                [new("item_blink", null, 1.25, "slot0"), new("item_rapier", null, 0, "stash0")],
                [new("luna_lucent_beam", 4, null, "ability0")], new(true, true, false, Level: 12, Epoch: epoch),
                gold.HasValue ? new(Gold: gold, Denies: 0) : null);
            return new(new(session, "private-ticket", ++frame, "dota2-gsi", 1920, 1080, own, 0,
                new(id, "radiant", hero, clock, "playing", false)), [], ++t, t);
        }
        static FarmMinuteScenario Scenario(MatchFarmSlice slice)
        {
            int at = (int)slice.Own.Clock;
            return new(at / 60, at, slice.Own.Hero, slice.Own.Team, [
                new("lane", [new("lane-bottom", "Bottom lane", "lane", 100, 54, 3, 3)]),
                new("camp", [new("camp-s", "Small camp", "camp", 120, -65, 8, 27) { WalkUnits = 876, CostBasis = "visit-budget",
                    PackWindowSeconds = 33, ModeledRosters = 2, CatalogRosters = 3 }])
            ]) { Timing = at < 1000 ? new(12, "sampled", 330, "reference", 82, "sampled", 150, "reference", 1.7) : null };
        }
        var sent = new List<JsonElement>();
        try {
            File.WriteAllText(config, "AOAI_ENDPOINT=https://fixture.openai.azure.com\nFOUNDRY_KEY=private-test-key");
            using (var archive = new MatchArchive(root)) {
                var state = new LiveMatchState(archive, () => t);
                state.ObserveFrame(Sample(600.1, 900, 15));
                var origin = Sample(600.625, 813, 27); state.ObserveFrame(origin);
                state.ObserveTeam(id, "radiant", 600.25, new([new(enemy, 8, ["item_sheepstick", null, null, null, null, null], null, 1.5)],
                    [new(enemy, "observer", .3, .4, 0, .6, 4, 50)]));
                state.ObserveWards(id, "radiant", 600.25, [new("sentry", "dire", 77, 80)]);
                state.ObserveSight(id, "radiant", 600.25, [new(enemy, "dire", -501, 220)]);
                state.ObserveLaneWaves(origin, new(t, "team-view", 600, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, id, 2, 0,
                    [new("npc_dota_creep_goodguys_melee", 2, -363, -254, null, 750, null),
                     new("npc_dota_creep_badguys_ranged", 3, -185, -109, null, 750, null)], [], []), confirmed: true);
                var shared = state.ForFarm(origin, true)!; var ownOnly = state.ForFarm(origin, false)!;
                state.ObserveFrame(Sample(601.25, 810, 4999));
                state.ObserveEquipment(id, "radiant", 601.25, [new(enemy, 9, ["item_dagon", null, null, null, null, null], null, 0)]);
                var before = archive.Read(id);
                check(!before.Any(f => f.Kind == "own-state" && f.Clock == 600.625), "subsecond model sample is not the periodic own-state fact");
                Transport Capture(string service, string requestModel, bool omitName = false) => new(body => {
                    var data = JsonElement.Parse(body.GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!);
                    // No archive Read/barrier here: this physical line must already exist at entry to HTTP.
                    var row = JsonElement.Parse(File.ReadLines(Path.Combine(root, id, "live.jsonl")).Last());
                    var audit = row.GetProperty("data");
                    check(row.GetProperty("kind").GetString() == "farm-input" && row.GetProperty("source").GetString() == "coach" &&
                        JsonElement.DeepEquals(data, audit.GetProperty("input")),
                        "exact structured HTTP input is durable before transport; request envelope is not the audit");
                    string prompt = body.GetProperty("instructions").GetString() + "\n" + data.GetRawText();
                    check(body.GetProperty("model").GetString() == requestModel && audit.GetProperty("providerId").GetString() == service &&
                        (omitName ? !audit.TryGetProperty("modelId", out _) : audit.GetProperty("modelId").GetString() == requestModel) &&
                        !new[] { "providerId", "modelId", service, requestModel }.Any(s => prompt.Contains(s, StringComparison.OrdinalIgnoreCase)),
                        "this request's safe target is durable before POST, never part of DATA or instructions");
                    sent.Add(data);
                });
                using var transport = Capture("foundry", deployment);
                using var cloud = new CloudCoach(config, deployment, "private-lessons", transport, localBotOnly: false);
                Task<MinuteFarmDecision> Send(MatchFarmSlice slice, CloudCoach? adapter = null) {
                    var provider = adapter ?? cloud;
                    return provider.ChooseMinuteFarmWithMetrics(slice.Own, Scenario(slice), default, slice,
                        (data, ct) => state.NoteModelInput(slice, data, provider.ProviderId, provider.ModelId, ct));
                }
                await Send(shared); await Send(ownOnly);
                var fact = archive.Read(id).First(f => f.Kind == "farm-input");
                var self = sent[0].GetProperty("self"); var options = sent[0].GetProperty("options");
                check(fact.Clock == 601.25 && fact.KnownAt == 601.25 && fact.Data.GetProperty("inputClock").GetDouble() == 600.625 &&
                    fact.Data.GetProperty("inputSequence").GetInt64() == shared.FactSequence && self.GetProperty("health").GetInt32() == 813 &&
                    self.GetProperty("x").GetDouble() == 813.25 && sent[0].GetProperty("ownEconomy").GetProperty("gold").GetInt32() == 27 &&
                    sent[0].GetRawText().Contains("item_sheepstick") && !sent[0].GetRawText().Contains("item_dagon"),
                    "input uses the frozen subsecond self, original causal cursor and authorized team reads, never the later frame");
                check(sent[0].GetProperty("teamKnowledge").GetProperty("laneWaves").GetProperty("lanes")[1].GetProperty("basis").GetString() == "seen-close" &&
                    !sent[1].GetRawText().Contains("seen-close") && JsonElement.DeepEquals(sent[0].GetProperty("emptyLaneMeetings"), sent[1].GetProperty("emptyLaneMeetings")),
                    "sharing off still gets identical clock-only baseline, no team sightings; opt-in sends and archives frozen wave evidence");
                check(options.GetArrayLength() == 2 && options[0].GetProperty("stops")[0].GetProperty("costBasis").ValueKind == JsonValueKind.Null &&
                    options[1].GetProperty("stops")[0].GetProperty("packWindowSeconds").GetInt32() == 33 &&
                    sent[0].GetProperty("historicalPrior").ValueKind == JsonValueKind.Object && !sent[1].TryGetProperty("teamKnowledge", out _) &&
                    !sent[1].GetRawText().Contains(enemy) && before.Select(f => f.Data.GetRawText()).SequenceEqual(archive.Read(id).Take(before.Count).Select(f => f.Data.GetRawText())),
                    "all offered stop budgets and aggregate prior are preserved; own-only does not inherit team data or rewrite old facts");
                foreach (var change in new Action<JsonNode>[] {
                    d => d["input"]!["self"]!["items"]![0]!["apiKey"] = "bad", d => d["input"]!["ownEconomy"]!["gold"] = "27",
                    d => d["input"]!["timing"]!["auth"] = true, d => d["input"]!["historicalPrior"]!["matchId"] = id,
                    d => d["input"]!["options"]![1]!["stops"]![0]!["pixels"] = true,
                    d => d["input"]!["teamKnowledge"]!["lastReadEquipment"]![0]!["steamId"] = "private",
                    d => d["input"]!["teamKnowledge"]!["laneWaves"]!["lanes"]![1]!["minimap"] = "raw",
                    d => d["input"]!["emptyLaneMeetings"]![0]!["meeting"]!["steamId"] = "private",
                    d => d["input"]!["options"] = new JsonArray(new JsonNode?[33]), d => d["includesTeamKnowledge"] = false,
                    d => d["input"] = "arbitrary blob", d => d["inputSequence"] = long.MaxValue,
                    d => d["input"]!["providerId"] = "foundry", d => d["input"]!["modelId"] = deployment,
                    d => d["providerId"] = "private-unknown", d => d.AsObject().Remove("providerId"),
                    d => d["providerId"] = CopilotGateway.ProviderId, // cannot pair a custom Foundry alias with this provider
                    d => d["modelId"] = sensitiveAlias, d => d["modelId"] = new string('f', 32), d => d["modelId"] = "sk-fixture",
                    d => d["modelId"] = config, d => d["modelId"] = "fixture.env.local", d => d["modelId"] = "https://fixture.openai.azure.com",
                    d => d["modelId"] = new string('-', 101), d => d["modelId"] = true,
                    d => d["headers"] = new JsonObject(), d => d["oauth"] = "private-value"
                }) {
                    var bad = JsonNode.Parse(fact.Data.GetRawText())!; change(bad);
                    bool rejected = false;
                    try { archive.Append(fact with { Sequence = archive.LastSequence(id) + 1, Data = JsonSerializer.SerializeToElement(bad) }); }
                    catch (ArgumentException) { rejected = true; }
                    check(rejected, "malformed minute DATA, causal metadata or unsafe request target rejected");
                }
                state.Disconnect(); state.ObserveFrame(Sample(602, 810, 28)); // even same textual session and minute
                bool cancelled = false;
                try { await Send(shared); } catch (OperationCanceledException) { cancelled = true; }
                check(cancelled && transport.Calls == 2 && archive.Read(id).Count(f => f.Kind == "farm-input") == 2,
                    "a disconnected slice cannot record an input or reach HTTP after resume");
                var sparseFrame = Sample(2401.5, 810, null); state.ObserveFrame(sparseFrame);
                var sparse = state.ForFarm(sparseFrame, false)!; await Send(sparse);
                check(!sent[2].TryGetProperty("ownEconomy", out _) && sent[2].GetProperty("timing").ValueKind == JsonValueKind.Null &&
                    sent[2].GetProperty("historicalPrior").ValueKind == JsonValueKind.Null, "missing economy, timing and historical prior remain missing/null");
                string proxyFile = Path.Combine(temp.FullName, "private-proxy.env");
                File.WriteAllText(proxyFile, "CODEY_MODEL_API_KEY=private-fixture-key");
                foreach (string model in new[] { CopilotGateway.Astra, CopilotGateway.Sol }) {
                    using var proxy = new CloudCoach(new LiveCoachSettings { ModelProvider = CopilotGateway.ProviderId,
                        CopilotModel = model, CopilotKeyFile = proxyFile, FoundryEnvFile = config }, "", Capture("codey-copilot", model));
                    await Send(sparse, proxy);
                }
                using var sameId = new CloudCoach(config, CopilotGateway.Astra, "", Capture("foundry", CopilotGateway.Astra));
                await Send(sparse, sameId); // a deployment with the same spelling is not evidence of Copilot routing
                using var ambiguous = new CloudCoach(config, sensitiveAlias, "", Capture("foundry", sensitiveAlias, omitName: true));
                await Send(sparse, ambiguous); // valid deployment for routing, not safe to persist
                // An unrecognized adapter reduces to the old four-field wrapper; it cannot inject either ID.
                await state.NoteModelInput(sparse, sent[^1], "private-unknown", deployment, default);
                var overlayOnly = new LiveMatchState(null, () => t);
                bool missing = false, lost = false;
                try { await cloud.ChooseMinuteFarmWithMetrics(sparse.Own, Scenario(sparse), default, sparse,
                    (data, ct) => overlayOnly.NoteModelInput(sparse, data, cloud.ProviderId, cloud.ModelId, ct)); } catch (IOException) { missing = true; }
                archive.Dispose(); // writable at freeze, unavailable at pre-send
                try { await Send(sparse); } catch (IOException) { lost = true; }
                check(missing && lost && !overlayOnly.CanAudit && !state.CanAudit && transport.Calls == 3,
                    "missing or lost archive rejects pre-send audit and never calls HTTP, including an already frozen slice");
            }
            using var reopened = new MatchArchive(root);
            var attempts = reopened.Read(id).Where(f => f.Kind == "farm-input").ToArray();
            var legacy = attempts[^1].Data;
            check(legacy.GetPropertyCount() == 4 && !legacy.TryGetProperty("providerId", out _) && !legacy.TryGetProperty("modelId", out _) &&
                JsonElement.DeepEquals(legacy.GetProperty("input"), sent[^1]), "old wrapper without either ID remains readable on restart");
            var models = attempts[..^1];
            check(models.Length == sent.Count && models.Zip(sent).All(p => JsonElement.DeepEquals(p.Second, p.First.Data.GetProperty("input"))) &&
                models.All(f => new[] { id, session, "private-", sensitiveAlias, "fixture.openai.azure.com", "fixture.env", "127.0.0.1",
                    epoch.ToString(), "item_rapier", "instructions", "max_output_tokens", "headers", "image_url", "output_text" }.All(s => !f.Data.GetRawText().Contains(s))),
                "restart retains exact DATA and safe request IDs, never participant identities, credentials, prompt, stash, image or provider output");
        } finally { temp.Delete(recursive: true); }
    }
}
