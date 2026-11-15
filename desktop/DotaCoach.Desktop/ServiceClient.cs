using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace DotaCoach.Desktop;

/// <summary>Owns service credentials. Cookies/CSRF never cross into the local web renderer.</summary>
public sealed class ServiceClient : IDisposable
{
    private readonly HttpClient http;
    private string csrf = "";
    public Uri Origin { get; }
    public CookieContainer Cookies { get; } = new();

    public ServiceClient(string address)
    {
        var uri = DesktopPolicy.ServerUri(address);
        Origin = new Uri(uri.GetLeftPart(UriPartial.Authority) + "/");
        http = new HttpClient(new HttpClientHandler { CookieContainer = Cookies, AllowAutoRedirect = false }) {
            Timeout = TimeSpan.FromSeconds(20),
        };
    }

    public async Task<JsonElement> Request(string method, string path, object? body = null, string? invite = null)
    {
        // Only the native controller chooses paths; nevertheless reject absolute URLs and traversal.
        if (!path.StartsWith("/api/", StringComparison.Ordinal) || path.Contains("..") || path.Contains('\\') || path.Contains('#'))
            throw new ArgumentException("无效服务路径");
        using var request = new HttpRequestMessage(new HttpMethod(method), new Uri(Origin, path));
        request.Headers.TryAddWithoutValidation("Origin", Origin.GetLeftPart(UriPartial.Authority));
        if (csrf.Length > 0) request.Headers.TryAddWithoutValidation("X-CSRF-Token", csrf);
        if (!string.IsNullOrEmpty(invite)) request.Headers.TryAddWithoutValidation("X-Room-Invite", invite);
        if (body != null) request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");
        using var response = await http.SendAsync(request);
        var text = await response.Content.ReadAsStringAsync();
        if ((int)response.StatusCode >= 300 && (int)response.StatusCode < 400)
            throw new ServiceException((int)response.StatusCode, "服务地址发生跳转，请在设置中填写正确的 HTTPS 地址");
        JsonElement result;
        try { result = JsonSerializer.Deserialize<JsonElement>(text); }
        catch { throw new ServiceException((int)response.StatusCode, "服务返回了非预期内容，请检查服务地址和版本"); }
        if (!response.IsSuccessStatusCode)
            throw new ServiceException((int)response.StatusCode, result.TryGetProperty("error", out var error)
                ? error.GetString() ?? "服务请求失败" : $"服务请求失败 HTTP {(int)response.StatusCode}");
        if (path == "/api/auth/login" && result.TryGetProperty("csrfToken", out var token)) csrf = token.GetString() ?? "";
        // Remove credentials from the renderer-facing response.
        if (path is "/api/auth/login" or "/api/auth/session")
            return JsonSerializer.SerializeToElement(new { user = result.GetProperty("user") });
        return result;
    }

    public async Task<string> GsiConfig(string roomId)
    {
        using var response = await http.GetAsync(new Uri(Origin, "/api/gsi-config?room=" + Uri.EscapeDataString(roomId)));
        if (!response.IsSuccessStatusCode) throw new ServiceException((int)response.StatusCode, "无法下载 GSI 配置，请确认房主权限");
        return await response.Content.ReadAsStringAsync();
    }

    public Uri SocketUri(string roomId, string role, bool listener, string sourceLabel, string invite, bool selfMonitor)
    {
        var query = $"room={Uri.EscapeDataString(roomId)}&role={Uri.EscapeDataString(role)}" +
            $"&selfMonitor={(selfMonitor ? "1" : "0")}&invite={Uri.EscapeDataString(invite)}";
        if (listener) query += "&inputKind=process&sourceLabel=" + Uri.EscapeDataString(sourceLabel);
        return new UriBuilder(Origin) { Scheme = Origin.Scheme == "https" ? "wss" : "ws", Path = "/ws", Query = query }.Uri;
    }
    public void Dispose() { http.Dispose(); }
}

public sealed class ServiceException(int status, string message) : Exception(message)
{
    public int Status { get; } = status;
}
