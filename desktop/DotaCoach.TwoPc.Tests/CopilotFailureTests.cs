using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using DotaCoach.TwoPc;

internal static partial class CopilotContractTests
{
    private static async Task FailuresAndCancellation(Files files, Action<bool, string> check)
    {
        async Task BadInference(string why, bool image, string payload)
        {
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(files.Settings() with { FoundryEnvFile = files.Foundry }, peer);
            peer.Json(Catalog(Row(Sol), Row(Astra))); peer.Json(payload);
            await Rejected(() => Infer(coach, image), why);
            check(Trace(peer, InMemory, FileKey, "GET", "POST") &&
                JsonElement.Parse(peer.Calls[1].Body).GetProperty("model").GetString() == Astra,
                $"{why}: one exact-model request, no usable result, retry or fallback");
        }
        foreach (bool image in new[] { false, true }) {
            string kind = image ? "vision advice" : "minute abstention";
            foreach (var (why, echoed) in new (string, string?)[] {
                ("missing", null), ("other allowed model", Sol), ("wrong provider prefix", "codex/" + Astra),
                ("wrong case", "GPT-6.2-ASTRA"), ("padded", Astra + " ")
            }) await BadInference($"{kind} with {why} response.model", image, Response(echoed, image ? AdviceText : Abstain));
        }
        await BadInference("offered minute choice from the other allowed model", false, Response(Sol, Choice));
        string ambiguous = Response(Astra, Choice).Insert(1, $"\"model\":\"{Sol}\","); // Last field is the configured ID.
        await BadInference("two contradictory response.model fields", false, ambiguous);
        await BadInference("invented whole-option ID", false, Response(Astra, """{"optionId":"invented"}"""));
        await BadInference("wrong-team advice", true, Response(Astra, JsonSerializer.Serialize(Advice with { Team = "dire" }, Wire.Json)));
        var refusal = JsonNode.Parse(Response(Astra, AdviceText))!;
        refusal["output"]![0]!["content"]!.AsArray().Add(new JsonObject { ["type"] = "refusal", ["refusal"] = "fixture" });
        await BadInference("refusal beside otherwise valid vision advice", true, refusal.ToJsonString());
        await BadInference("malformed Responses JSON", false, "{");

        const string privateBody = "untrusted-codey-error-body";
        string debugRoot;
        await using (var debug = new DebugRecorder(files.Root.FullName, "coach", "copilot-contract")) {
            debugRoot = debug.DirectoryPath;
            foreach (var (phase, status) in new[] { ("catalog", 401), ("minute", 429), ("vision", 503) }) {
                using var peer = new CopilotProxyFixture();
                using var coach = Coach(files.Settings() with { FoundryEnvFile = files.Foundry }, peer);
                if (phase != "catalog") peer.Json(Catalog(Row(Astra), Row(Sol)));
                peer.Json(JsonSerializer.Serialize(new { error = new { message = $"{privateBody}: Bearer {FileKey} {Decoy}" } }), (HttpStatusCode)status);
                var error = await Rejected(() => Infer(coach, phase == "vision"), $"Codey {phase} HTTP {status}");
                debug.Error("codey-contract", error);
                int? code = error is ModelProviderException p ? p.StatusCode :
                    error is HttpRequestException h && h.StatusCode.HasValue ? (int)h.StatusCode : null;
                check(code == status && NoSecrets(error.ToString()) && !error.ToString().Contains(privateBody) &&
                    !error.Message.Contains("Foundry", StringComparison.OrdinalIgnoreCase) &&
                    Trace(peer, InMemory, FileKey, phase == "catalog" ? ["GET"] : ["GET", "POST"]),
                    $"Codey {phase} HTTP {status}: actionable, no raw error/credential, no retry to another provider");
            }
        }
        var lines = File.ReadAllText(Path.Combine(debugRoot, "events.jsonl"));
        check(NoSecrets(lines) && !lines.Contains(privateBody) && File.ReadLines(Path.Combine(debugRoot, "events.jsonl"))
            .Select(line => JsonElement.Parse(line).GetProperty("details").GetProperty("httpStatus").GetInt32())
            .SequenceEqual([401, 429, 503]), "local debug persists only safe provider status, not Bearer material or response bodies");

        using (var peer = new CopilotProxyFixture())
        using (var coach = Coach(files.Settings(), peer)) {
            peer.Json(Catalog(Row(Astra)));
            peer.Next(_ => Task.FromException<HttpResponseMessage>(new HttpRequestException("synthetic disconnect")));
            await Rejected(() => Minute(coach), "disconnected Responses endpoint");
            check(Trace(peer, InMemory, FileKey, "GET", "POST"), "transport failure is not an abstention and never changes the model");
        }

        static async Task Cancelled(Func<Task> call)
        {
            try { await call(); } catch (OperationCanceledException) { return; }
            throw new Exception("Expected user cancellation, not a result or abstention");
        }
        using (var peer = new CopilotProxyFixture())
        using (var coach = Coach(files.Settings(), peer)) {
            var stopped = new CancellationToken(canceled: true);
            await Cancelled(() => coach.ListCopilotModelsAsync(stopped));
            await Cancelled(() => Minute(coach, stopped));
            await Cancelled(() => coach.Decide(Frame, stopped));
            check(peer.Calls.Length == 0, "pre-cancelled check, minute and image do not even send a discovery GET");
        }
        foreach (var phase in new[] { "catalog", "minute", "vision" }) {
            using var user = new CancellationTokenSource();
            using var peer = new CopilotProxyFixture();
            using var coach = Coach(files.Settings(), peer);
            var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var aborted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            if (phase != "catalog") peer.Json(Catalog(Row(Astra)));
            async Task Stall(CancellationToken token) {
                entered.SetResult();
                try { await Task.Delay(Timeout.Infinite, token); }
                finally { if (token.IsCancellationRequested) aborted.TrySetResult(); }
            }
            if (phase == "minute") peer.Next(async token => {
                await Stall(token); throw new Exception("Unexpected completion of the stalled Codey fixture");
            });
            else peer.Next(_ => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StalledBody(Stall) }));
            // Cancel after dispatch, not via a sleep. A broken implementation times out the test,
            // rather than being mistaken for user cancellation or stalling the entire runner.
            var pending = Infer(coach, phase == "vision", user.Token);
            var watchdog = TimeSpan.FromSeconds(5);
            try {
                await entered.Task.WaitAsync(watchdog);
                user.Cancel();
                await Cancelled(() => pending).WaitAsync(watchdog);
                await aborted.Task.WaitAsync(watchdog);
            } finally { user.Cancel(); }
            check(Trace(peer, InMemory, FileKey, phase == "catalog" ? ["GET"] : ["GET", "POST"]),
                $"user cancellation during Codey {phase} aborts the transport, with no later POST, retry or replacement");
        }
    }

    // Headers have already succeeded. The cancellation token still has to reach the body reader.
    private sealed class StalledBody(Func<CancellationToken, Task> stall) : HttpContent
    {
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => throw new NotSupportedException();
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken token) => stall(token);
    }
}
