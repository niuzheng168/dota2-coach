using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

public sealed partial class CloudCoach
{
    private sealed record Provider(Uri Endpoint, string Model, string Key, bool Codey);
    private readonly bool codey;
    private readonly SemaphoreSlim catalogLock = new(1, 1);
    private Dictionary<string, CopilotGateway.Capabilities>? catalog;
    private long catalogAt;
    public string ProviderId => codey ? CopilotGateway.ProviderId : LiveCoachSettings.FoundryProvider;
    public string ModelId => model;
    public CloudCoach(LiveCoachSettings settings, string lessons, HttpMessageHandler? handler = null,
        CalibratedMap? map = null, bool localBotOnly = true, MinimapLayout? layout = null, TimeSpan? timeout = null)
        : this(Resolve(settings), lessons, handler, map, localBotOnly, layout, timeout) { }

    private static Provider Resolve(LiveCoachSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        settings.Validate();
        if (settings.ModelProvider == "foundry") return Foundry(settings.FoundryEnvFile, settings.Model);
        if (settings.ModelProvider != CopilotGateway.ProviderId) throw new ArgumentException("未知的模型服务；未发送请求。");
        CopilotGateway.Validate(settings.CopilotBaseUrl, settings.CopilotModel, settings.CopilotKeyFile);
        return new(new(CopilotGateway.BaseUri(settings.CopilotBaseUrl), "responses"), settings.CopilotModel,
            CopilotGateway.ModelKey(settings.CopilotKeyFile), true);
    }
    private static Provider Foundry(string envFile, string model)
    {
        var env = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (string line in File.ReadLines(envFile)) {
            var m = Regex.Match(line, @"^\s*([A-Z_0-9]+)\s*=\s*(.*?)\s*$");
            if (m.Success) env[m.Groups[1].Value] = m.Groups[2].Value.Trim('"', '\'');
        }
        if (!env.TryGetValue("AOAI_ENDPOINT", out var url) || !env.TryGetValue("FOUNDRY_KEY", out var key) ||
            string.IsNullOrWhiteSpace(key) || !Uri.TryCreate(url, UriKind.Absolute, out var host) ||
            host.Scheme != "https" || host.UserInfo != "" || host.Query != "" || host.Fragment != "" ||
            !(host.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase) ||
              host.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase)) ||
            !Regex.IsMatch(model, @"^[A-Za-z0-9_.-]{1,100}$"))
            throw new ArgumentException("Invalid dedicated Foundry configuration; no request sent");
        return new(new Uri(host.GetLeftPart(UriPartial.Authority) + "/openai/v1/responses"), model, key, false);
    }
    private CloudCoach(Provider provider, string lessons, HttpMessageHandler? handler, CalibratedMap? map,
        bool localBotOnly, MinimapLayout? layout, TimeSpan? timeout)
    {
        endpoint = provider.Endpoint; model = provider.Model; codey = provider.Codey;
        this.lessons = lessons.Length <= 12000 ? lessons : throw new ArgumentException("Lessons too large");
        this.map = map; this.localBotOnly = localBotOnly; this.layout = layout;
        var limit = timeout ?? DefaultTimeout;
        if (limit < TimeSpan.FromSeconds(5) || limit > TimeSpan.FromSeconds(20)) throw new ArgumentException("Model timeout must be 5–20 s");
        http = new HttpClient(handler ?? new HttpClientHandler { AllowAutoRedirect = false, UseProxy = false, UseCookies = false })
            { Timeout = limit };
        if (codey) {
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", provider.Key);
            http.DefaultRequestHeaders.UserAgent.ParseAdd("DotaCoach/0.6.4"); // Codey gives Codex/Claude clients a different catalog.
            http.DefaultRequestHeaders.Accept.Add(new("application/json"));
        } else http.DefaultRequestHeaders.Add("api-key", provider.Key);
    }
    public async Task<IReadOnlyList<string>> ListCopilotModelsAsync(CancellationToken cancellation)
    {
        if (!codey) throw new InvalidOperationException("只有 Codey 模式使用本地模型目录。");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        deadline.CancelAfter(http.Timeout);
        var items = await GetCatalog(force: true, deadline.Token).ConfigureAwait(false);
        return new[] { CopilotGateway.Astra, CopilotGateway.Sol }.Where(items.ContainsKey).ToArray();
    }
    // Each adapter owns its catalog/credentials. Manual refresh invalidates the previous result even
    // on failure. No ticket, match ID, picture, or private credentials from other providers enter this GET.
    private async Task<Dictionary<string, CopilotGateway.Capabilities>> GetCatalog(bool force, CancellationToken token)
    {
        await catalogLock.WaitAsync(token).ConfigureAwait(false);
        try {
            if (!force && catalog != null && Environment.TickCount64 - catalogAt is >= 0 and < 30_000) return catalog;
            catalog = null;
            using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(endpoint, "models"));
            using var res = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, token).ConfigureAwait(false);
            CheckStatus(res.StatusCode);
            try {
                var bytes = await ResponseBytes(res, 4 * 1024 * 1024, token).ConfigureAwait(false);
                using var doc = JsonDocument.Parse(bytes, CopilotGateway.StrictJson);
                var parsed = CopilotGateway.ReadCatalog(doc.RootElement);
                token.ThrowIfCancellationRequested();
                catalogAt = Environment.TickCount64; return catalog = parsed;
            } catch (JsonException) { throw new InvalidDataException("Codey 返回了无效的模型目录。"); }
        } finally { catalogLock.Release(); }
    }
    private async Task CheckTarget(int? imageBytes, CancellationToken token)
    {
        if (!codey) return;
        var items = await GetCatalog(false, token).ConfigureAwait(false);
        if (!items.TryGetValue(model, out var info)) throw new InvalidOperationException(
            $"Codey 没有公布可用的 GitHub Copilot {model}（Responses）；未改用其他模型。");
        // Codey replaces oversized images with a placeholder upstream. Do not let the coach reason on that as if it saw the game.
        if (imageBytes is int n) {
            // Codey 0.3.43 estimates size as floor(dataUrl.length * 3 / 4), including the media
            // prefix and base64 padding. Comparing ONLY raw JPEG bytes can silently yield a blank PNG.
            long dataUrlChars = JpegDataPrefix.Length + 4 * ((n + 2L) / 3);
            if (n <= 0 || !info.Vision || info.ImageBytes is int max && dataUrlChars * 3 / 4 > max)
                throw new InvalidOperationException("该 Copilot 模型未声明视觉支持，或图像超过其限制；未发送画面。");
        }
    }
    private void CheckStatus(HttpStatusCode status)
    {
        if ((int)status is < 200 or > 299) throw new ModelProviderException((int)status, codey);
    }
    private void CheckReturnedModel(JsonElement root)
    {
        if (codey && (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("model", out var actual) ||
            actual.ValueKind != JsonValueKind.String || actual.GetString() != model))
            throw new InvalidOperationException("Codey 的响应没有确认所选模型；已丢弃，不替换模型。");
    }
    private static async Task<byte[]> ResponseBytes(HttpResponseMessage response, int max, CancellationToken token)
    {
        if (response.Content.Headers.ContentLength > max) throw new InvalidOperationException("Model response too large");
        await response.Content.LoadIntoBufferAsync(max, token).ConfigureAwait(false);
        return await response.Content.ReadAsByteArrayAsync(token).ConfigureAwait(false);
    }
}
