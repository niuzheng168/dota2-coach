using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>Client contract for the coach PC's already-running Codey model gateway, not a GitHub OAuth client.</summary>
public static class CopilotGateway
{
    public const string ProviderId = "codey-copilot", DefaultBaseUrl = "http://127.0.0.1:4141/v1",
        Astra = "gpt-6.2-astra", Sol = "gpt-6.2-sol";
    private const string KeyName = "CODEY_MODEL_API_KEY";
    // Validate the literal string before Uri normalization (no IPv4 shorthand, encoded hosts, userinfo,
    // proxy paths, dot segments, DNS names, arbitrary tunnels or admin endpoints).
    private static readonly Regex LocalV1 = new(
        @"\Ahttps?://(?:127\.0\.0\.1|localhost|\[::1\])(?::(?<port>[1-9][0-9]{0,4}))?/v1/?\z",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase | RegexOptions.NonBacktracking);
    internal static Uri BaseUri(string? address)
    {
        var m = address is { Length: > 0 and <= 128 } ? LocalV1.Match(address) : Match.Empty;
        if (!m.Success || m.Groups["port"] is { Success: true } port && (!int.TryParse(port.Value, out int n) || n > 65535) ||
            !Uri.TryCreate(address, UriKind.Absolute, out var u) || !u.IsLoopback || u.AbsolutePath.TrimEnd('/') != "/v1")
            throw new ArgumentException("Codey 地址必须是本机回环的 HTTP(S) /v1，例如 " + DefaultBaseUrl);
        // Never resolve 'localhost' through a custom hosts file/DNS before sending a bearer credential.
        return new UriBuilder(u) { Host = u.Host == "localhost" ? "127.0.0.1" : u.Host, Path = "/v1/" }.Uri;
    }

    public static void Validate(string baseUrl, string model, string keyFile)
    {
        _ = BaseUri(baseUrl);
        if (model is not (Astra or Sol)) throw new ArgumentException("请选择 gpt-6.2-astra 或 gpt-6.2-sol。");
        if (keyFile == null || keyFile.Length > 1024 || keyFile.Length != 0 && (!Path.IsPathFullyQualified(keyFile) ||
            keyFile.StartsWith(@"\\", StringComparison.Ordinal) || keyFile.StartsWith("//", StringComparison.Ordinal) ||
            keyFile.Any(char.IsControl))) throw new ArgumentException("Codey 配置文件必须是本机绝对路径；留空使用环境变量。");
    }

    internal static string ModelKey(string keyFile)
    {
        string? key = null;
        if (keyFile.Length == 0) key = Environment.GetEnvironmentVariable(KeyName);
        else {
            try {
                if (OperatingSystem.IsWindows() && new DriveInfo(Path.GetPathRoot(keyFile)!).DriveType == DriveType.Network)
                    throw new ArgumentException("请选择本机的 Codey 模型密钥文件。");
                using var input = File.OpenRead(keyFile);
                if (input.Length is <= 0 or > 16_384) throw new InvalidDataException();
                byte[] bytes = new byte[(int)input.Length];
                try {
                    input.ReadExactly(bytes);
                    if (input.ReadByte() != -1) throw new InvalidDataException();
                    foreach (var raw in new UTF8Encoding(false, true).GetString(bytes).TrimStart('\uFEFF').Split('\n')) {
                        var line = raw.Trim();
                        if (line.Length == 0 || line[0] == '#') continue;
                        int sep = line.IndexOf('=');
                        if (sep < 1 || line[..sep].Trim() != KeyName) continue;
                        if (key != null) throw new InvalidDataException(); // ambiguous, do not pick a secret
                        key = line[(sep + 1)..].Trim();
                        if (key.Length >= 2 && key[0] is '"' or '\'' && key[^1] == key[0]) key = key[1..^1];
                    }
                } finally { CryptographicOperations.ZeroMemory(bytes); }
            } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException) {
                throw new ArgumentException("无法读取 Codey 的本机模型密钥文件；未发送请求。");
            }
        }
        // Never try a Foundry key, gh token, Codey admin key or another file on missing/invalid input.
        if (key is not { Length: >= 1 and <= 4096 } || key.Any(c => c < 33 || c > 126 || c is '"' or '\'' or ','))
            throw new ArgumentException("需要 Codey 模型网关的 CODEY_MODEL_API_KEY（不是 GitHub 登录令牌）；未发送请求。");
        return key;
    }

    internal sealed record Capabilities(bool Vision, int? ImageBytes);
    internal static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false, MaxDepth = 32 };
    internal static Dictionary<string, Capabilities> ReadCatalog(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("data", out var rows) ||
            rows.ValueKind != JsonValueKind.Array || rows.GetArrayLength() > 4096 ||
            root.TryGetProperty("has_more", out var more) && more.ValueKind != JsonValueKind.False)
            throw new InvalidDataException("Codey 模型目录不完整或格式不受支持。");
        var results = new Dictionary<string, Capabilities>(StringComparer.Ordinal);
        var appeared = new HashSet<string>(StringComparer.Ordinal);
        static JsonElement Get(JsonElement parent, string key) =>
            parent.ValueKind == JsonValueKind.Object && parent.TryGetProperty(key, out var v) ? v : default;
        static string? Str(JsonElement value) => value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        foreach (var row in rows.EnumerateArray()) {
            string? id = Str(Get(row, "id"));
            if (id is not (Astra or Sol)) continue;
            if (!appeared.Add(id)) { results.Remove(id); continue; }
            // Codey can also publish *bare* IDs for non-Copilot providers. Its model-discovery adapter
            // stamps the origin into display_name; a same-named Codex/Foundry/custom row is NOT evidence.
            var label = Str(Get(row, "display_name"));
            if (label == null || !(label.EndsWith(" (github-copilot)", StringComparison.Ordinal) ||
                label.StartsWith("github-copilot ", StringComparison.Ordinal) && !label.EndsWith(')'))) continue;
            var endpoints = Get(row, "supported_endpoints");
            // These are UPSTREAM capability tokens, not the gateway's public HTTP route. Codey
            // chooses native HTTP only for "/responses"; "/v1/responses" is not equivalent here.
            if (endpoints.ValueKind != JsonValueKind.Array || !endpoints.EnumerateArray().Any(e => Str(e) == "/responses"))
                continue; // don't route to /messages, Chat Completions, a WebSocket-only or unknown backend
            var policy = Str(Get(Get(row, "policy"), "state"));
            if (policy != null && policy != "enabled") continue;
            var caps = Get(row, "capabilities"); var limits = Get(Get(Get(caps, "limits"), "vision"), "max_prompt_image_size");
            bool vision = Get(Get(caps, "supports"), "vision").ValueKind == JsonValueKind.True;
            int? limit = null;
            if (limits.ValueKind is not (JsonValueKind.Undefined or JsonValueKind.Null)) {
                if (limits.ValueKind == JsonValueKind.Number && limits.TryGetInt32(out int bytes) && bytes > 0) limit = bytes;
                else vision = false;
            }
            results.Add(id, new(vision, limit));
        }
        return results;
    }
}
