using System;
using System.Threading;
using System.Threading.Tasks;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed partial class GameWindowUi
{
    private static (int Width, int Height) Viewport() =>
        GameWindow.Find(out _) is { Width: >= 640 and <= 7680, Height: >= 480 and <= 4320 } game ? (game.Width, game.Height) : default;

    /// <summary>Only authenticated own GSI and the real client rectangle leave this machine. The paired
    /// coach's filtered display channel is independent: no pixels, video encoder, or local spectator source.</summary>
    private Task RunMinuteState(Pairing pair, CancellationToken cancel) => RunParticipantState(pair, Viewport, cancel);
    internal async Task RunParticipantState(Pairing pair, Func<(int Width, int Height)> viewport, CancellationToken cancel)
    {
        using var link = new GameLink(pair);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        Task boards = Task.CompletedTask, ends = Task.CompletedTask;
        try {
            await link.Connect(cancel);
            link.BindPostgame(gsi, "dota2-gsi", () => Interlocked.Increment(ref localSequence));
            ends = link.DeliverPostgames(stop.Token);
            transportConnected = true;
            boards = PollBoards(link, viewport, stop.Token);
            while (!cancel.IsCancellationRequested) {
                var lease = await link.GetTicket(cancel);
                long sampledAt = Environment.TickCount64;
                var (width, height) = viewport();
                long sequence = Interlocked.Increment(ref localSequence); // Reserve before sampling, also ordered against an end receipt.
                var current = GameGsiSample.Read(gsi, participantOnly: true);
                var sample = current.ForUpload();
                var player = sample.Player;
                // 0x0 is a withdrawal of geometry, not a made-up layout or a match end. Keep phase/own state
                // (including death and pause) for the coach, but never enable a planner without the viewport.
                if (width == 0 && player?.Farm != null) player = player with { Farm = null };
                var meta = new FrameMeta(link.SessionId!, lease.Id, sequence,
                    "dota2-gsi", width, height, player, sample.AgeMs, sample.Phase);
                await link.UploadMinuteState(meta, sampledAt, cancel);
                lastCaptureAt = player == null ? null : sampledAt; // Fresh metadata, never a captured picture.
                status.Text = Mode == LinkMode.ParticipantWithLocalHp ? "本人 GSI · 允许本机血量识别 · 不传送画面" :
                    "仅本人 GSI · 加密连接 · 不采集或传送画面";
                metrics.Text = width == 0 ? "暂无游戏窗口 · 不显示叠加层" : $"游戏窗口 {width}×{height} · 无视频编码";
                ShowGsi(current.Phase is { } w ? $"本人 GSI：{w.Hero} · {w.Team} · {w.Clock:F0}s" +
                    (w.Phase == "postgame" ? " · 已结束" : w.Paused ? " · 已暂停" : player?.Alive == false ? " · 阵亡" : "") :
                    "等待新鲜的本人对局 GSI；不使用观战数据。");
                if (!MinuteFarmContext.Eligible(player)) {
                    ClearReminder(); // Never clears the independent equipment/ward layers.
                } else {
                    overlay.FrameArrived();
                    var reminder = await link.Poll(cancel);
                    ApplyReminder(reminder?.Kind == MinuteFarmContext.Kind ? reminder : null, diagnostic: false, link: link);
                }
                await Task.Delay(250, cancel);
            }
        } catch (Exception e) when (e is not OutOfMemoryException) {
            status.Text = LinkErrors.Message(e, cancel.IsCancellationRequested);
            debug.Recorder?.Error("minute-gsi-transport", e);
        } finally {
            stop.Cancel(); transportConnected = false; lastCaptureAt = null; ClearReminder(); overlay.Enemies(null); image.Source = null;
            await Task.WhenAll(boards, ends);
            using var timeout = new CancellationTokenSource(1500);
            try { await link.End(timeout.Token); } catch { }
        }
    }
}
