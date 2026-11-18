namespace DotaCoach.TwoPc;

public static class LinkErrors
{
    public static string Message(Exception error, bool userCancelled) =>
        error is VideoVersionException ? "视频协议不兼容：游戏端和教练端都需要升级到 v0.4.0（视频协议 v3），不回退JPEG抽帧。" :
        error is OperationCanceledException && userCancelled ? "已停止 · 不再传输画面" :
        error is OperationCanceledException ? "连接请求超时，采集已停止。请检查地址：同机测试用127.0.0.1，跨机使用真实局域网地址，不要误选WSL虚拟网卡。" :
        "连接中断或证书/会话校验失败。采集已停止；请确认教练端，再重新配对连接。";
}
