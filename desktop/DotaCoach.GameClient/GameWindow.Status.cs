using System;
using System.Threading;
using System.Threading.Tasks;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed partial class GameWindowUi
{
    /// <summary>
    /// Video off: no picture leaves this machine. Own GSI and the Dota window size go to the coach a few times a second,
    /// which keeps ordinary subtitles working. Not a sampled participant frame: no farm, stats, boards or end receipt.
    /// The optional GPU creep HP display stays local; unlike strict participant mode, this does not promise zero local capture.
    /// </summary>
    private async Task RunStatus(Pairing pair, bool diagnostic, CancellationToken cancellation)
    {
        using var link = new GameLink(pair);
        long statusAt = 0;
        try {
            await link.Connect(cancellation);
            transportConnected = true;
            while (!cancellation.IsCancellationRequested) {
                bool minimized = false;
                var surface = diagnostic ? null : GameWindow.Find(out minimized);
                if (!diagnostic && surface == null) {
                    // Nothing to draw on: stop reporting, so the coach sees the game machine as away and withdraws its advice.
                    ClearReminder(); overlay.Enemies(null); lastCaptureAt = null;
                    status.Text = minimized ? "已连接 · Dota 已最小化，恢复后自动继续" : "已连接 · 等待 Dota 2 窗口…";
                    await Task.Delay(250, cancellation); continue;
                }
                long now = Environment.TickCount64;
                if (now >= statusAt) {
                    statusAt = now + Wire.StatusIntervalMs;
                    var own = diagnostic ? default : GameGsiSample.ReadForUpload(gsi, participantOnly: false);
                    // SendStatus strips feature-only telemetry before serialization and remembers a text-only origin.
                    await link.SendStatus(new(link.SessionId!, "", Interlocked.Increment(ref localSequence), diagnostic ? "diagnostic" : "dota2",
                        surface?.Width ?? 1280, surface?.Height ?? 720, own.Player, own.AgeMs), cancellation);
                    lastCaptureAt = now;
                    overlay.FrameArrived(own.Player?.MatchId == "0");
                    status.Text = diagnostic ? "已连接 · 诊断模式（不需要 Dota）" :
                        GameWindow.IsForeground(surface!) ? "已连接 · 只传自身GSI，不传画面" : "已连接 · Dota 在后台 · 只传自身GSI，不传画面";
                    if (diagnostic) ShowGsi("诊断状态：未传送自身 GSI。");
                    else ShowGsi(own.Player is { } p ? $"自身GSI：{p.Team} · {p.Hero} · {p.Clock:F1}s" : "自身GSI：暂无新鲜数据");
                }
                ApplyReminder(await link.Poll(cancellation), diagnostic);
                await Task.Delay(100, cancellation);
            }
        } catch (Exception error) {
            debug.Recorder?.Error("status-transport", error);
            status.Text = LinkErrors.Message(error, cancellation.IsCancellationRequested);
        } finally {
            transportConnected = false; lastCaptureAt = null; ClearReminder(); overlay.Enemies(null);
            using var timeout = new CancellationTokenSource(1500);
            try { await link.End(timeout.Token); } catch { }
        }
    }
}
