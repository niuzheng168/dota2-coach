namespace DotaCoach.TwoPc;

/// <summary>
/// Explicit, single-shot manual link test. Waits for a NEW real-game frame after
/// the operator returns to Dota. Never relaxes the foreground/privacy gate.
/// Not a farming decision, scheduled automation, or stale-reminder replay.
/// </summary>
public sealed class DeferredLinkProbe
{
    private readonly Func<long> now;
    private readonly long notBefore, expires;
    private readonly string message;
    private readonly ScreenMark? marker;
    public string Status { get; private set; } = "waiting";
    public DeferredLinkProbe(string message, ScreenMark? marker, Func<long>? clock = null)
    {
        if (string.IsNullOrWhiteSpace(message) || message.Length > 120 ||
            marker != null && (!Wire.Finite(marker.X, 0, 1) || !Wire.Finite(marker.Y, 0, 1)))
            throw new ArgumentException("测试文字或画面坐标无效。");
        now = clock ?? (() => Environment.TickCount64);
        notBefore = now() + 5000; expires = now() + 25000;
        this.message = message; this.marker = marker;
    }
    public string Tick(CoachMailbox mailbox)
    {
        if (Status != "waiting") return Status;
        if (now() >= expires) return Status = "expired";
        if (now() < notBefore) return Status;
        var frame = mailbox.Latest();
        if (frame == null || frame.Meta.Source != "dota2" || frame.Meta.Player?.Alive != true ||
            frame.CapturedNoEarlierThan < notBefore) return Status;
        try
        {
            mailbox.SetReminder(frame.Meta.SessionId, frame.Meta.Sequence, "manual-link-test", message, marker);
            return Status = "sent";
        }
        catch (InvalidOperationException) { return Status; } // A concurrent capture may have invalidated this frame.
    }
    public void Cancel() { if (Status == "waiting") Status = "cancelled"; }
}

public static class LinkErrors
{
    public static string Message(Exception error, bool userCancelled) =>
        error is VideoVersionException ? "视频协议不兼容：游戏端和教练端都需要升级到v0.3.0或更新的匹配版本，不回退JPEG抽帧。" :
        error is OperationCanceledException && userCancelled ? "已停止 · 不再传输画面" :
        error is OperationCanceledException ? "连接请求超时，采集已停止。请检查地址：同机测试用127.0.0.1，跨机使用真实局域网地址，不要误选WSL虚拟网卡。" :
        "连接中断或证书/会话校验失败。采集已停止；请确认教练端，再重新配对连接。";
}
