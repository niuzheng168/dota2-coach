namespace DotaCoach.TwoPc;

/// <summary>The coach predates status-only links (video off): it has no /v1/status.</summary>
public sealed class CoachVersionException : InvalidOperationException;

public static class LinkErrors
{
    public static string Message(Exception error, bool userCancelled) =>
        error is VideoVersionException ? "视频协议不兼容：游戏端和教练端都需要升级到 v0.4.0（视频协议 v3），不回退JPEG抽帧。" :
        error is CoachVersionException ? "教练端版本过旧：游戏端已不传画面，请把教练端升级到与游戏端相同的版本。" :
        error is OperationCanceledException && userCancelled ? "已停止 · 不再传输画面" :
        error is OperationCanceledException ? "连接请求超时，已停止发送。请检查地址：同机测试用127.0.0.1，跨机使用真实局域网地址，不要误选WSL虚拟网卡。" :
        "连接中断或证书/会话校验失败，已停止发送；请确认教练端，再重新配对连接。";
}
