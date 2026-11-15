using System;

namespace DotaCoach.Desktop;

public static class DesktopPolicy
{
    public const string LocalAppAddress = "https://dota-coach.local/index.html";
    public static bool IsLocalDocument(string? value) =>
        Uri.TryCreate(value, UriKind.Absolute, out var uri) && uri.Scheme == "https" &&
        uri.IdnHost == "dota-coach.local" && uri.IsDefaultPort && uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/index.html" && uri.Query.Length == 0;
    public const string DefaultServerAddress = "https://dota2.largeniu.com/";

    public static string InitialServerAddress(string? argument, string? confirmedServer, string? legacyServer)
    {
        // An explicit developer/invitation URL always wins and remains visible if it needs correcting.
        if (!string.IsNullOrWhiteSpace(argument)) return argument.Trim();
        if (TryServer(confirmedServer, out var confirmed)) return confirmed!.AbsoluteUri;
        if (TryServer(legacyServer, out var legacy))
        {
            // v0.1 saved localhost before attempting to connect, even on an end user's first launch.
            // Do not carry that accidental default into the fixed package. Explicit or confirmed local
            // development endpoints still work, and other user-chosen servers remain unchanged.
            if (!(legacy!.Scheme == "http" && legacy.IsLoopback && legacy.Port == 3210))
                return legacy.AbsoluteUri;
        }
        return DefaultServerAddress;
    }

    private static bool TryServer(string? value, out Uri? uri)
    {
        uri = null;
        if (string.IsNullOrWhiteSpace(value)) return false;
        try { uri = ServerUri(value); return true; }
        catch (ArgumentException) { return false; }
    }

    public static bool NavigationSucceeded(bool isSuccess, int httpStatusCode) =>
        isSuccess && httpStatusCode >= 200 && httpStatusCode < 400;

    public static string ConnectionFailure(Uri? uri, string webError, int httpStatusCode)
    {
        if (uri?.IsLoopback == true)
            return $"无法连接本机服务 {uri.GetLeftPart(UriPartial.Authority)}。\n\n" +
                "localhost / 127.0.0.1 指的是这台 Windows 电脑；本地助手不包含同传服务端。\n" +
                "平常使用请点击「连接默认服务」，无需在 Windows 上启动服务器。\n" +
                "只有本地开发时才需先启动服务端，再点击重试。";
        if (httpStatusCode >= 400)
            return $"同传服务返回 HTTP {httpStatusCode}。\n\n请检查服务地址或稍后重试；已停止外部采集。";
        return $"无法打开同传服务（{webError}）。\n\n" +
            "请检查网络和服务地址后重试。若是证书错误，请联系管理员修复证书，不要绕过安全验证。";
    }

    public static Uri ServerUri(string value)
    {
        if (!Uri.TryCreate(value.Trim(), UriKind.Absolute, out var uri) ||
            uri.UserInfo.Length != 0 ||
            (uri.Scheme != "https" && !(uri.Scheme == "http" && uri.IsLoopback)))
            throw new ArgumentException("请输入可信的 HTTPS 服务地址（仅本机 localhost 可用 HTTP）。");
        return uri;
    }

    public static bool SameOrigin(Uri? trusted, string? candidate) =>
        trusted != null && Uri.TryCreate(candidate, UriKind.Absolute, out var uri) &&
        uri.Scheme == trusted.Scheme && uri.IdnHost == trusted.IdnHost && uri.Port == trusted.Port &&
        uri.UserInfo.Length == 0;
}
