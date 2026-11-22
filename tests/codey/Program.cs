using DotaCoach.TwoPc;

// Launched by the harness. No settings.Load/Save, real game inputs, injected HTTP handler or fallback.
try { await Smoke(args); return 0; }
catch (Exception e) { Console.Error.WriteLine($"FAIL CloudCoach: {e.Message}"); return 1; }

static void Check(bool ok, string detail) { if (!ok) throw new Exception(detail); }
static void CheckImage(ModelResult image, string id) => Check(image.Model == id && image.ResponseId == "resp-smoke-image" &&
    image.Advice.Message == "fixture-image-ok" && image.InputTokens == 17 && image.OutputTokens == 4, "Bad native image result");
static async Task Reject(Func<Task> call, string detail)
{
    try { await call(); }
    catch (InvalidOperationException e) when (e is not ModelProviderException) { return; }
    throw new Exception(detail);
}
static async Task Smoke(string[] args)
{
    if (args is not [var mode, var baseUrl, var keyFile, var jpegFile] ||
        !Uri.TryCreate(baseUrl, UriKind.Absolute, out var u) || u.Scheme != "http" || u.Host != "127.0.0.1" ||
        u.Port < 1 || u.AbsolutePath != "/v1") throw new ArgumentException("Expected private smoke fixture arguments");
    string[] models = ["gpt-6.2-astra", "gpt-6.2-sol"];
    var own = new OwnState("1234567", "npc_dota_hero_luna", "radiant", 725, true, 880, 200, -1300, -2400,
        [], [], new(true, true, false));
    var lane = new FarmStop("smoke-lane", "Synthetic lane", "lane", -1200, -2700, 6, 6);
    var scenario = new FarmMinuteScenario(12, 725, own.Hero, own.Team, [new(lane.Id, [lane])]);
    byte[] jpeg = File.ReadAllBytes(jpegFile);
    var (width, height) = Jpeg.Dimensions(jpeg);
    var frame = new FrameView(new("smoke", "synthetic", 1, "dota2", width, height, own, 10), jpeg, 0, 0);
    using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    var ct = deadline.Token;
    foreach (string id in mode switch { "happy" => models, "no-vision" => [models[1]], _ => new[] { models[0] } }) {
        var settings = new LiveCoachSettings { ModelProvider = "codey-copilot", CopilotBaseUrl = baseUrl,
            CopilotModel = id, CopilotKeyFile = keyFile };
        using var client = new CloudCoach(settings, "", localBotOnly: false);
        var listed = await client.ListCopilotModelsAsync(ct);
        switch (mode) {
            case "foreign-provider":
            case "non-native-endpoint":
                try { await Reject(() => client.ChooseMinuteFarm(own, scenario, ct), $"Ineligible {id} accepted"); }
                catch (ModelProviderException e) {
                    throw new Exception($"{mode}: list=[{string.Join(", ", listed)}]; client sent POST, gateway returned {e.StatusCode}");
                }
                Check(listed.SequenceEqual([models[1]]), $"{mode}: expected only Sol, got [{string.Join(", ", listed)}]");
                break;
            case "no-vision":
                Check(listed.Contains(id), "Sol should still qualify for text");
                Check(await client.ChooseMinuteFarm(own, scenario, ct) == lane.Id, "Text-only Sol failed to answer text");
                await Reject(() => client.Decide(frame, ct), "Non-vision Sol accepted an image");
                break;
            case "image-size-edge":
            case "image-cap-minus-one":
                await Reject(() => client.Decide(frame, ct), "Accepted advice after Codey replaced the original JPEG");
                break;
            case "image-cap-exact":
                CheckImage(await client.Decide(frame, ct), id);
                break;
            case "wrong-echo":
                await Reject(() => client.ChooseMinuteFarm(own, scenario, ct), "Wrong text model echo accepted");
                await Reject(() => client.Decide(frame, ct), "Wrong vision model echo accepted");
                break;
            case "happy":
                Check(listed.ToHashSet(StringComparer.Ordinal).SetEquals(models) && listed.Count == 2, "Wrong Copilot catalog");
                var text = await client.ChooseMinuteFarmWithMetrics(own, scenario, ct);
                Check(text is { OptionId: "smoke-lane", InputTokens: 17, OutputTokens: 4 }, "Bad native text result");
                CheckImage(await client.Decide(frame, ct), id);
                break;
            default: throw new ArgumentException("Unknown scenario");
        }
    }
    Console.WriteLine($"PASS CloudCoach/{mode}");
}
