using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static partial class CopilotContractTests
{
    private static Task Infer(CloudCoach coach, bool image, CancellationToken token = default) =>
        image ? coach.Decide(Frame, token) : Minute(coach, token);

    private static async Task CatalogGates(Files files, Action<bool, string> check)
    {
        async Task Denied(string why, string catalog, bool image = false)
        {
            using var peer = new CopilotProxyFixture();
            // A *valid* alternate provider and a usable second model must not turn a refusal into a recommendation.
            using var coach = Coach(files.Settings() with { FoundryEnvFile = files.Foundry }, peer);
            peer.Json(catalog);
            await Rejected(() => Infer(coach, image), why);
            check(Trace(peer, InMemory, FileKey, "GET"), $"{why}: catalog GET only, no probe inference, substitution or Foundry request");
        }
        var noAnnotation = Row(Astra); noAnnotation.Remove("display_name"); noAnnotation["owned_by"] = "github-copilot";
        var noEndpoints = Row(Astra); noEndpoints.Remove("supported_endpoints");
        foreach (var (why, rows) in new (string, JsonObject[])[] {
            ("selected ID absent", []),
            ("aliases, display labels and provider-prefixed IDs are not exact IDs",
                [Row("astra", Astra), Row("codex/" + Astra), Row("github-copilot/" + Astra), Row("GPT-6.2-ASTRA"), Row(Astra + " ")]),
            ("bare minimal marketplace row has no provenance", [new() { ["id"] = Astra }]),
            ("owned_by is not Codey's display_name provenance", [noAnnotation]),
            ("bare ID from a different provider", [Row(Astra, "GPT-6.2 Astra (codex)")]),
            ("lookalike source suffix", [Row(Astra, "GPT-6.2 Astra (github-copilot-extra)")]),
            ("prefix contradicted by different final provider label", [Row(Astra, "github-copilot GPT-6.2 Astra (codex)")]),
            ("missing supported_endpoints", [noEndpoints]),
            ("/v1/responses alone is a client URL, not Codey's /responses capability", [Row(Astra, endpoints: ["/v1/responses"])]),
            ("chat completions or endpoint lookalike is not Responses", [Row(Astra, endpoints: ["/chat/completions", "/other/responses"])]),
            ("duplicate bare ID, Copilot row first", [Row(Astra), Row(Astra, "GPT-6.2 Astra (codex)")]),
            ("duplicate bare ID, Copilot row last", [Row(Astra, "GPT-6.2 Astra (codex)"), Row(Astra)])
        }) await Denied(why, Catalog([.. rows, Row(Sol)]));
        await Denied("invalid model directory", """{"data":{}}""");
        var partial = JsonNode.Parse(Catalog(Row(Astra)))!; partial["has_more"] = true;
        await Denied("partial directory cannot rule out a marketplace collision", partial.ToJsonString());

        foreach (var (selected, other) in new[] { (Astra, Sol), (Sol, Astra) }) {
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(files.Settings(selected), peer);
            peer.Json(Catalog(Row(selected), Row(other)));
            var before = await coach.ListCopilotModelsAsync(default);
            peer.Json(Catalog(Row(selected, "Same bare ID (codex)"), Row("codex/" + selected), Row(other)));
            var after = await coach.ListCopilotModelsAsync(default);
            await Rejected(() => Minute(coach), "previously certified ID now from a different provider");
            check(before.Count == 2 && before.Contains(selected) && after.SequenceEqual([other]) &&
                Trace(peer, InMemory, FileKey, "GET", "GET"),
                $"refresh drops a reattributed {selected} from both the UI list and cached inference permission");
        }
        using (var peer = new CopilotProxyFixture())
        using (var coach = Coach(files.Settings(), peer)) {
            peer.Json(Catalog(Row(Astra))); await coach.ListCopilotModelsAsync(default);
            peer.Json("not-json");
            await Rejected(() => coach.ListCopilotModelsAsync(default), "failed refresh of a previously usable catalog");
            peer.Json(Catalog(Row(Sol)));
            await Rejected(() => coach.Decide(Frame, default), "stale image permission after a failed refresh");
            check(Trace(peer, InMemory, FileKey, "GET", "GET", "GET"), "failed catalog refresh cannot authorize a JPEG from the last good catalog");
        }

        var unknownVision = Row(Astra); unknownVision["capabilities"]!["supports"]!.AsObject().Remove("vision");
        await Denied("missing affirmative vision support", Catalog(unknownVision, Row(Sol)), image: true);
        await Denied("conflicting duplicate vision flags", Catalog(Row(Astra), Row(Astra, vision: false)), image: true);
        foreach (var (why, row) in new[] {
            ("vision explicitly disabled", Row(Astra, vision: false)),
            ("original JPEG one byte over the gateway's advertised limit", Row(Astra, imageLimit: Frame.Jpeg.Length - 1)),
            ("241 raw bytes fit the 259-byte cap but Codey would redact the 260-byte estimated image", Row(Astra, imageLimit: CodeyImageEstimate - 1))
        }) {
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(files.Settings(), peer);
            peer.Json(Catalog(row, Row(Sol))); peer.Json(Response(Astra, Abstain));
            var decision = await Minute(coach);
            await Rejected(() => coach.Decide(Frame, default), why);
            check(decision.OptionId == null && decision.InputTokens == 171 && Trace(peer, InMemory, FileKey, "GET", "POST") &&
                !peer.Calls[1].Body.Contains("image_url"), $"{why}: text/explicit abstention allowed, no image POST or text-only image fallback");
        }
    }
}
