using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static partial class CopilotContractTests
{
    // Independent wire expectations, not constants imported from the implementation under test.
    private const string Astra = "gpt-6.2-astra", Sol = "gpt-6.2-sol", InMemory = "http://127.0.0.1:39147/v1";
    private const string FileKey = "fixture-only-codey-file-bearer", EnvKey = "fixture-only-codey-env-bearer",
        FoundryKey = "fixture-only-foundry-api-key", Decoy = "fixture-unrelated-private-value";
    private const string Lesson = "ordinary-mode-only-lesson", Session = "private-fixture-session", Ticket = "private-fixture-ticket";
    private const string Choice = """{"optionId":"conditional-camp"}""", Abstain = """{"optionId":null}""";
    private static readonly OwnState Self = new("9182837465", "npc_dota_hero_luna", "radiant", 725, true, 880, 315, -500, 300,
        [new("item_blink", null, 2, "slot0")], [], new(true, true, false, Level: 10));
    private static readonly FarmMinuteScenario Scenario = new(12, 725, Self.Hero, Self.Team, [
        new("first-lane", [new("bottom", "Bottom lane", "lane", -400, 290, 3, 3)]),
        new("conditional-camp", [new("camp", "Small camp", "camp", -580, 400, 6, 25)])
    ]);
    // Real 32x18 grayscale JPEG of two synthetic rectangles. Its 241 bytes encode to 324 chars
    // (including "=="); the 23-char data URL prefix makes the gateway count floor(347 * 3/4) = 260.
    private const int CodeyImageEstimate = 260;
    private static readonly FrameView Frame = new(new(Session, Ticket, 14, "dota2", 1280, 720, Self, 10),
        Convert.FromBase64String("/9j/4AAQSkZJRgABAQAAAQABAAD//gAFUUEh/9sAQwAIBgYHBgUIBwcHCQkICgwUDQwLCwwZEhMPFB0aHx4dGhwcICQuJyAiLCMcHCg3KSwwMTQ0NB8nOT04MjwuMzQy/8AACwgAEgAgAQERAP/EABcAAQEBAQAAAAAAAAAAAAAAAAAHBQT/xAAmEAABAwMACwEAAAAAAAAAAAAAAQIDBAURBgcSFiE2VnWRlLPS/9oACAEBAAA/AKQTTXhyXR9xZ85CBA2d7tJeort7sn6Oauvt3ucDYbhda6ria7bRlRUPkajsKmcKq8cKvkzwAAf/2Q=="), 0, 0);
    private static readonly ModelAdvice Advice = new(Self.Hero, Self.Team, "可见己方英雄。", ["可见己方基地"], ["野点未知"],
        "先确认兵线。", ["观察下路"], "出现威胁就取消", null, "");
    private static string AdviceText => JsonSerializer.Serialize(Advice, Wire.Json);

    private sealed class Files : IDisposable
    {
        internal readonly DirectoryInfo Root = Directory.CreateTempSubdirectory("dotacoach-copilot-contract-");
        internal string Codey => Path.Combine(Root.FullName, "private-codey-fixture.env");
        internal string Foundry => Path.Combine(Root.FullName, "private-foundry-fixture.env");
        internal string Missing => Path.Combine(Root.FullName, "does-not-exist.env");
        internal Files()
        {
            File.WriteAllText(Codey, $"# local model gateway only\nUNRELATED={Decoy}\nCODEY_MODEL_API_KEY={FileKey}\n");
            File.WriteAllText(Foundry, $"AOAI_ENDPOINT=https://fixture.openai.azure.com\nFOUNDRY_KEY={FoundryKey}\n");
        }
        internal LiveCoachSettings Settings(string model = Astra, string url = InMemory) => new() {
            ModelProvider = "codey-copilot", CopilotModel = model, CopilotBaseUrl = url, CopilotKeyFile = Codey,
            Model = "other-foundry-deployment", FoundryEnvFile = Missing
        };
        public void Dispose() => Root.Delete(recursive: true);
    }

    private static CloudCoach Coach(LiveCoachSettings settings, HttpMessageHandler? handler = null) =>
        new(settings, Lesson, handler: handler, map: null, localBotOnly: false, layout: null, timeout: null);
    private static Task<MinuteFarmDecision> Minute(CloudCoach coach, CancellationToken token = default) =>
        coach.ChooseMinuteFarmWithMetrics(Self, Scenario, token);

    private static JsonObject Row(string id, string label = "Fixture (github-copilot)", string[]? endpoints = null,
        bool vision = true, int? imageLimit = null) => new() {
        ["id"] = id, ["object"] = "model", ["display_name"] = label,
        ["supported_endpoints"] = JsonSerializer.SerializeToNode(endpoints ?? ["/responses"]),
        ["capabilities"] = new JsonObject {
            ["supports"] = new JsonObject { ["vision"] = vision },
            ["limits"] = imageLimit is int n ? new JsonObject { ["vision"] = new JsonObject { ["max_prompt_image_size"] = n } } : new JsonObject()
        }
    };
    private static string Catalog(params JsonObject[] rows) => JsonSerializer.Serialize(new { @object = "list", data = rows });
    private static string Response(string? model, string text)
    {
        var result = JsonSerializer.SerializeToNode(new {
            id = "resp-offline-fixture", model, status = "completed", usage = new { input_tokens = 171, output_tokens = 23 },
            output = new[] { new { type = "message", role = "assistant", status = "completed",
                content = new[] { new { type = "output_text", text } } } }
        })!.AsObject();
        if (model == null) result.Remove("model");
        return result.ToJsonString();
    }

    private static bool NoSecrets(string text) => !new[] { FileKey, EnvKey, FoundryKey, Decoy }.Any(text.Contains);
    private static bool Trace(CopilotProxyFixture peer, string url, string key, params string[] methods)
    {
        var calls = peer.Calls;
        return calls.Length == methods.Length && calls.Zip(methods).All(pair => {
            var (c, method) = pair;
            return c.Method == method && c.Uri.AbsoluteUri == url + (method == "GET" ? "/models" : "/responses") &&
                c.Headers.GetValueOrDefault("Authorization") == "Bearer " + key &&
                !c.Headers.ContainsKey("api-key") && !c.Headers.ContainsKey("Proxy-Authorization") &&
                c.Headers.Where(h => !h.Key.Equals("Authorization", StringComparison.OrdinalIgnoreCase)).All(h => NoSecrets(h.Value)) &&
                NoSecrets(c.Body) && !new[] { Session, Ticket, "private-codey-fixture.env", "private-foundry-fixture.env" }.Any(c.Body.Contains) &&
                (method == "GET" ? c.Body.Length == 0 : c.Headers.GetValueOrDefault("Content-Type")?.StartsWith("application/json") == true);
        });
    }

    internal static async Task Run(Action<bool, string> check)
    {
        // This console runner is sequential and process-local. Mask rather than read/save any inherited
        // model key. The default runner invokes this suite last; nothing is changed in the user's shell.
        Environment.SetEnvironmentVariable("CODEY_MODEL_API_KEY", EnvKey);
        try {
            using var files = new Files();
            await SuccessfulCalls(files, check);
            await CatalogGates(files, check);
            await FailuresAndCancellation(files, check);
            await CredentialsAndSettings(files, check);
            await RealLoopback(files, check);
            await LegacyFoundry(files, check);
        } finally { Environment.SetEnvironmentVariable("CODEY_MODEL_API_KEY", null); }
    }

    private static async Task SuccessfulCalls(Files files, Action<bool, string> check)
    {
        foreach (string model in new[] { Astra, Sol }) {
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(files.Settings(model), peer);
            peer.Json(Catalog(Row("codex/" + Sol), Row(Sol, "github-copilot GPT-6.2 Sol", ["/responses"]),
                Row("gpt-6.2-astra-latest"), Row(Astra, "GPT-6.2 Astra (github-copilot)", imageLimit: CodeyImageEstimate),
                Row("GPT-6.2-ASTRA"), Row("extra-model")));
            if (model == Astra) {
                var ids = await coach.ListCopilotModelsAsync(default);
                check(ids.Count == 2 && ids.ToHashSet(StringComparer.Ordinal).SetEquals([Astra, Sol]) &&
                    Trace(peer, InMemory, FileKey, "GET"), "manual Codey catalog check returns only the two exact IDs; no inference or game data");
            }
            async Task Text()
            {
                peer.Json(Response(model, Choice));
                var decision = await Minute(coach);
                var body = JsonElement.Parse(peer.Calls[^1].Body);
                var content = body.GetProperty("input")[0].GetProperty("content");
                var data = JsonElement.Parse(content[0].GetProperty("text").GetString()!);
                check(decision is { OptionId: "conditional-camp", InputTokens: 171, OutputTokens: 23 } &&
                    body.GetProperty("model").GetString() == model && !body.GetProperty("store").GetBoolean() &&
                    body.GetProperty("text").GetProperty("format").GetProperty("strict").GetBoolean() &&
                    content.GetArrayLength() == 1 && content[0].GetProperty("type").GetString() == "input_text" &&
                    data.GetProperty("options").EnumerateArray().Select(o => o.GetProperty("id").GetString()).SequenceEqual(Scenario.Options.Select(o => o.Id)) &&
                    data.GetProperty("self").GetProperty("health").GetInt32() == Self.Health &&
                    !new[] { Self.MatchId, Lesson, "image_url", "data:image/" }.Any(peer.Calls[^1].Body.Contains),
                    $"{model}: native text-only minute request, second offered option and per-request usage");
            }
            async Task Image()
            {
                peer.Json(Response(model, AdviceText));
                var result = await coach.Decide(Frame, default);
                var body = JsonElement.Parse(peer.Calls[^1].Body);
                var content = body.GetProperty("input")[0].GetProperty("content").EnumerateArray().ToArray();
                var context = JsonElement.Parse(content.Single(p => p.GetProperty("type").GetString() == "input_text").GetProperty("text").GetString()!);
                var dataUrl = content.Single(p => p.GetProperty("type").GetString() == "input_image").GetProperty("image_url").GetString()!;
                check(result.Model == model && result.Advice.Message == Advice.Message && result.ResponseId == "resp-offline-fixture" &&
                    result is { InputTokens: 171, OutputTokens: 23 } && body.GetProperty("model").GetString() == model &&
                    !body.GetProperty("store").GetBoolean() && content.Length == 2 && context.GetProperty("self").GetProperty("hero").GetString() == Self.Hero &&
                    context.GetProperty("historicalLessons").GetString() == Lesson &&
                    dataUrl == "data:image/jpeg;base64," + Convert.ToBase64String(Frame.Jpeg),
                    $"{model}: ordinary live coach sends the exact JPEG and own context and returns validated advice");
                if (model == Astra)
                    check(Frame.Jpeg.Length == 241 && dataUrl.EndsWith("==", StringComparison.Ordinal) && dataUrl.Length * 3 / 4 == CodeyImageEstimate,
                        "at exactly Codey's 260-byte limit the complete original JPEG is sent, including data URL prefix and base64 padding");
            }
            if (model == Astra) { await Text(); await Image(); }
            else { await Image(); await Text(); }
            check(Trace(peer, InMemory, FileKey, "GET", "POST", "POST"),
                $"{model}: preflight precedes first inference and primes this client; only local Bearer models/responses calls");
        }
    }

    // The test only accepts operational/configuration failures, not an arbitrary NullReference, an
    // accidental user cancellation, or a successful result (including a silent null/abstention).
    private static async Task<Exception> Rejected(Func<Task> operation, string description)
    {
        try { await operation(); }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or HttpRequestException or
            InvalidDataException or IOException or JsonException) { return e; }
        throw new Exception("Core did not reject " + description);
    }
}
