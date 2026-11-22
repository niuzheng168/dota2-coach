using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static partial class CopilotContractTests
{
    private static async Task CredentialsAndSettings(Files files, Action<bool, string> check)
    {
        var good = files.Settings();
        async Task NoSend(LiveCoachSettings bad, string why)
        {
            using var peer = new CopilotProxyFixture();
            // Even on a regression the injected transport cannot reach the address under test.
            var error = await Rejected(async () => {
                using var coach = Coach(bad, peer);
                await coach.ListCopilotModelsAsync(default);
            }, why);
            check(peer.Calls.Length == 0 && NoSecrets(error.ToString()), $"{why}: rejected before any authenticated request");
        }
        foreach (string model in new[] { "gpt-6-astra", "astra", "codex/" + Sol, "GPT-6.2-SOL", Sol + " ", "" })
            await NoSend(good with { CopilotModel = model }, "non-exact Copilot selection '" + model + "'");
        await NoSend(good with { ModelProvider = "unknown", FoundryEnvFile = files.Foundry }, "unknown provider, despite working Foundry config");
        foreach (var (why, url) in new[] {
            ("remote service", "https://fixture.openai.azure.com/v1"), ("LAN host", "http://192.168.1.50/v1"),
            ("lookalike localhost", "http://localhost.example.invalid/v1"), ("IPv4 shorthand", "http://127.1/v1"),
            ("numeric IP alias", "http://2130706433/v1"), ("unsupported scheme", "ftp://127.0.0.1/v1"),
            ("userinfo", $"http://user:{Decoy}@127.0.0.1/v1"), ("query", $"http://127.0.0.1/v1?key={Decoy}"),
            ("fragment", "http://127.0.0.1/v1#admin"), ("wrong path", "http://127.0.0.1/admin/v1"),
            ("normalized traversal", "http://127.0.0.1/admin/../v1"), ("encoded path", "http://127.0.0.1/%76%31"),
        }) await NoSend(good with { CopilotBaseUrl = url }, "Codey base URL with " + why);

        foreach (string local in new[] { "https://127.0.0.1:31519/v1", "http://localhost:31519/v1", "http://[::1]:31519/v1" }) {
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(good with { CopilotBaseUrl = local }, peer);
            peer.Json(Catalog(Row(Astra)));
            var ids = await coach.ListCopilotModelsAsync(default);
            var target = peer.Calls.Single().Uri;
            check(ids.SequenceEqual([Astra]) && target.IsLoopback && target.Scheme == new Uri(local).Scheme && target.Port == 31519 &&
                Trace(peer, target.GetLeftPart(UriPartial.Authority) + "/v1", FileKey, "GET"),
                $"canonical {local} is supported, including HTTPS/IPv6 without any real network call");
        }

        foreach (string path in new[] { "relative-key.env", @"\\fixture.invalid\share\codey.env" }) {
            // No IO against the remote filename, even if validation regresses.
            bool rejected = false;
            try { (good with { CopilotKeyFile = path }).Validate(); } catch (ArgumentException) { rejected = true; }
            check(rejected, "Codey settings reject a non-local or non-absolute key path without opening it");
        }
        await NoSend(good with { FoundryEnvFile = files.Codey }, "shared credential path even though it contains a valid Codey model key");
        await NoSend(good with { CopilotKeyFile = files.Missing, FoundryEnvFile = files.Foundry }, "missing explicit key file with valid ambient and Foundry keys");
        string badFile = Path.Combine(files.Root.FullName, "invalid-codey.env");
        foreach (var (why, text) in new[] {
            ("unrelated credentials", $"FOUNDRY_KEY={FoundryKey}\nGITHUB_TOKEN={Decoy}\nCODEY_ADMIN_API_KEY={Decoy}"),
            ("empty model key", "CODEY_MODEL_API_KEY=\n"),
            ("ambiguous model keys", $"CODEY_MODEL_API_KEY={FileKey}\nCODEY_MODEL_API_KEY={Decoy}"),
        }) {
            File.WriteAllText(badFile, text);
            await NoSend(good with { CopilotKeyFile = badFile, FoundryEnvFile = files.Foundry }, why + " in selected local file");
        }
        using (var locked = File.Open(files.Foundry, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var peer = new CopilotProxyFixture())
        using (var coach = Coach(good with { CopilotKeyFile = "", FoundryEnvFile = files.Foundry }, peer)) {
            peer.Json(Catalog(Row(Astra)));
            await coach.ListCopilotModelsAsync(default);
            check(Trace(peer, InMemory, EnvKey, "GET"), "omitted key file uses only the synthetic model-key env var, with Foundry file locked");
        }
        Environment.SetEnvironmentVariable("CODEY_MODEL_API_KEY", null);
        try { await NoSend(good with { CopilotKeyFile = "", FoundryEnvFile = files.Foundry }, "missing Codey environment key"); }
        finally { Environment.SetEnvironmentVariable("CODEY_MODEL_API_KEY", EnvKey); }

        string preferences = Path.Combine(files.Root.FullName, "coach.json");
        good.Save(preferences);
        var reopened = LiveCoachSettings.Load(preferences);
        check(reopened.ModelProvider == "codey-copilot" && reopened.CopilotModel == Astra && reopened.CopilotKeyFile == files.Codey &&
            reopened.Model == good.Model && NoSecrets(File.ReadAllText(preferences)),
            "saved preferences round-trip provider, model and local path, not credential contents");
    }

    private sealed class ProxyTripwire(Uri destination) : IWebProxy
    {
        private int consulted;
        internal int Consulted => Volatile.Read(ref consulted);
        public ICredentials? Credentials { get; set; }
        public Uri GetProxy(Uri _) { Interlocked.Increment(ref consulted); return destination; }
        public bool IsBypassed(Uri _) { Interlocked.Increment(ref consulted); return false; }
    }

    private static async Task PairedCalls(Files files, CloudCoach coach, CopilotProxyFixture origin, Action<bool, string> check)
    {
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        var pairing = host.Pair(IPAddress.Loopback);
        using var link = new GameLink(pairing);
        await link.Connect(default);
        var ticket = await link.GetTicket(default);
        await link.Upload(Frame.Meta with { SessionId = link.SessionId!, Ticket = ticket.Id,
            MatchPhase = new(Self.MatchId, Self.Team, Self.Hero, Self.Clock, "playing", false) { AgeMs = 10 }
        }, Frame.Jpeg, Environment.TickCount64, default);
        var frame = host.Mailbox.Latest()!;

        using var archive = new MatchArchive(Path.Combine(files.Root.FullName, "matches"));
        var state = new LiveMatchState(archive);
        state.ObserveFrame(frame);
        var slice = state.ForFarm(frame, includeTeam: false) ?? throw new Exception("Fixture did not establish an eligible own-only match");
        origin.Json(Catalog(Row(Sol))); origin.Json(Response(Sol, Choice));
        var minute = await coach.ChooseMinuteFarmWithMetrics(slice.Own, Scenario, default, slice,
            (data, ct) => state.NoteModelInput(slice, data, coach.ProviderId, coach.ModelId, ct));
        var input = JsonElement.Parse(origin.Calls[1].Body).GetProperty("input")[0].GetProperty("content")[0].GetProperty("text").GetString()!;
        var fact = archive.Read(Self.MatchId).Single(f => f.Kind == "farm-input").Data;
        check(minute.OptionId == "conditional-camp" && fact.GetProperty("providerId").GetString() == "codey-copilot" &&
            fact.GetProperty("modelId").GetString() == Sol && JsonElement.DeepEquals(JsonElement.Parse(input), fact.GetProperty("input")) &&
            NoSecrets(fact.GetRawText()) && !new[] { Lesson, "image_url", "Authorization", "CODEY_MODEL_API_KEY", Self.MatchId }.Any(fact.GetRawText().Contains),
            "paired own sample → audited minute → loopback Codey: archive has the exact tactical data, not the credential-bearing envelope");

        var imageReply = JsonNode.Parse(Response(Sol, AdviceText))!;
        imageReply["metadata"] = new JsonObject { ["gateway_diagnostic"] = Decoy };
        origin.Json(imageReply.ToJsonString());
        var result = await coach.Decide(frame, default);
        host.Mailbox.SetReminder(link.SessionId!, frame.Meta.Sequence, "visible-farming-advice", result.Advice.Message, result.Advice.Marker);
        // Inspect raw pinned-TLS response bytes, not a reserialized DTO which could hide unexpected fields.
        using var raw = new HttpClient(new HttpClientHandler { UseProxy = false, AllowAutoRedirect = false,
            ServerCertificateCustomValidationCallback = (_, cert, _, _) => cert?.GetCertHashString(HashAlgorithmName.SHA256) == pairing.Fingerprint
        }) { BaseAddress = new Uri(pairing.Address) };
        raw.DefaultRequestHeaders.Authorization = new("Bearer", pairing.Token);
        raw.DefaultRequestHeaders.Add("X-Session", link.SessionId);
        string packet = await raw.GetStringAsync("/v1/reminder");
        var advice = JsonElement.Parse(packet).GetProperty("reminder");
        check(advice.GetProperty("message").GetString() == Advice.Message && advice.GetProperty("frameSequence").GetInt64() == frame.Meta.Sequence &&
            NoSecrets(packet) && !packet.Contains("private-codey-fixture.env") && !packet.Contains("image_url") &&
            origin.Calls.All(c => new[] { link.SessionId!, ticket.Id, pairing.Token }.All(s => !c.Body.Contains(s) &&
                !c.Uri.AbsoluteUri.Contains(s) && c.Headers.Values.All(h => !h.Contains(s)))),
            "fixture image advice crosses real paired TLS as a caption; no model key to the game, no pairing bearer/session/ticket to the model");
    }

    private static async Task RealLoopback(Files files, Action<bool, string> check)
    {
        using var origin = new CopilotProxyFixture(); using var sink = new CopilotProxyFixture();
        await using var server = await origin.Listen(); await using var other = await sink.Listen();
        var settings = files.Settings(Sol, server.BaseUrl);
        // Unlike the system default, this proxy explicitly DOES NOT bypass loopback. A bug is
        // observed by a second local capture server, never by the user's proxy or an external host.
        var saved = HttpClient.DefaultProxy;
        var trap = new ProxyTripwire(new Uri(other.BaseUrl));
        HttpClient.DefaultProxy = trap;
        try {
            using (var coach = Coach(settings)) { // no injected handler: real production HttpClient
                await PairedCalls(files, coach, origin, check);
                check(Trace(origin, server.BaseUrl, FileKey, "GET", "POST", "POST") &&
                    trap.Consulted == 0 && sink.Calls.Length == 0, "real loopback discovery and Responses use Bearer, bypassing even a forced system proxy");
                origin.Redirect(new Uri(other.BaseUrl + "/responses"));
                await Rejected(() => coach.Decide(Frame, default), "redirected JPEG inference");
                check(Trace(origin, server.BaseUrl, FileKey, "GET", "POST", "POST", "POST") && sink.Calls.Length == 0,
                    "307 cannot forward the JPEG POST (even without its Bearer) to another loopback port");
            }
            using (var coach = Coach(settings)) {
                origin.Redirect(new Uri(other.BaseUrl + "/models"));
                await Rejected(() => coach.ListCopilotModelsAsync(default), "redirected model directory");
                check(Trace(origin, server.BaseUrl, FileKey, "GET", "POST", "POST", "POST", "GET") && sink.Calls.Length == 0 && trap.Consulted == 0,
                    "production catalog client also rejects redirects before a second hop or inference");
            }
        } finally { HttpClient.DefaultProxy = saved; }
    }

    private static async Task LegacyFoundry(Files files, Action<bool, string> check)
    {
        foreach (string deployment in new[] { "legacy-arbitrary-deployment", Sol }) {
            string path = Path.Combine(files.Root.FullName, "legacy.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new { foundryEnvFile = files.Foundry, model = deployment }));
            var legacy = LiveCoachSettings.Load(path);
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(legacy with { CopilotKeyFile = files.Missing }, peer);
            // Existing Foundry success envelopes were not required to echo a model.
            peer.Json(Response("legacy-server-model-not-deployment", Choice)); peer.Json(Response(null, AdviceText));
            var minute = await Minute(coach); var image = await coach.Decide(Frame, default);
            check(legacy.ModelProvider == "foundry" && legacy.Model == deployment && minute.OptionId == "conditional-camp" &&
                image.Model == deployment && image.Advice.Message == Advice.Message && peer.Calls.Length == 2 && peer.Calls.All(c =>
                    c.Method == "POST" && c.Uri.AbsoluteUri == "https://fixture.openai.azure.com/openai/v1/responses" &&
                    c.Headers.GetValueOrDefault("api-key") == FoundryKey && !c.Headers.ContainsKey("Authorization") &&
                    NoSecrets(c.Body) && JsonElement.Parse(c.Body).GetProperty("model").GetString() == deployment),
                $"legacy settings keep '{deployment}' on dedicated api-key Foundry for both modes; no Codey lookup or key needed");
            await Rejected(() => coach.ListCopilotModelsAsync(default), "catalog check on a Foundry-configured coach");
            check(peer.Calls.Length == 2, "Foundry cannot send a catalog check with its API key to the Copilot gateway");
            if (deployment == "legacy-arbitrary-deployment") {
                // The old ordinary (not minute) Foundry adapter joined output_text blocks. This is
                // ONE completed message and ONE valid JSON advice, not two answers or a refusal.
                string text = AdviceText;
                int split = text.IndexOf("\"message\":", StringComparison.Ordinal);
                var legacyReply = JsonNode.Parse(Response(null, text))!;
                legacyReply["output"]![0]!["content"] = JsonSerializer.SerializeToNode(
                    new[] { text[..split], text[split..] }.Select(s => new { type = "output_text", text = s }));
                peer.Json(legacyReply.ToJsonString());
                ModelResult multipart;
                try { multipart = await coach.Decide(Frame, default); }
                catch (InvalidOperationException e) { throw new Exception("Foundry compatibility: ordinary coach rejected one advice split across two text blocks", e); }
                check(multipart.Advice.Message == Advice.Message && peer.Calls.Length == 3,
                    "legacy Foundry ordinary coach still reads one completed JSON advice split across two output_text blocks");
            }
        }
    }
}
