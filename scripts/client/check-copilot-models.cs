#:property PublishAot=false
#:project ../../desktop/DotaCoach.TwoPc.Core/DotaCoach.TwoPc.Core.csproj
using DotaCoach.TwoPc;

// Optional developer/diagnostic CLI: only GET the catalog through the same adapter as the coach.
// Never pass a token on the command line. No GSI, archive access, inference, or configuration writes.
if (args.Length != 0) {
    Console.Error.WriteLine("No arguments. Use CODEY_MODEL_API_KEY and optionally DOTA_COACH_COPILOT_BASE_URL in the environment.");
    return 2;
}
try {
    var config = new LiveCoachSettings { ModelProvider = CopilotGateway.ProviderId,
        CopilotBaseUrl = Environment.GetEnvironmentVariable("DOTA_COACH_COPILOT_BASE_URL") ?? CopilotGateway.DefaultBaseUrl };
    using var client = new CloudCoach(config, "");
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
    var available = await client.ListCopilotModelsAsync(timeout.Token);
    foreach (var id in new[] { CopilotGateway.Astra, CopilotGateway.Sol })
        Console.WriteLine($"{id}: {(available.Contains(id) ? "listed (GitHub Copilot / Responses)" : "not listed for GitHub Copilot / Responses")}");
    return available.Count == 2 ? 0 : 1;
} catch (ModelProviderException e) {
    Console.Error.WriteLine($"Codey catalog HTTP {e.StatusCode}; no inference was sent.");
} catch (Exception e) when (e is ArgumentException or IOException or HttpRequestException or InvalidOperationException or OperationCanceledException) {
    Console.Error.WriteLine($"Codey catalog not verified ({e.GetType().Name}); check your loopback endpoint and local model key.");
}
return 1;
