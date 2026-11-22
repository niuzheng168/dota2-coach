using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace DotaCoach.TwoPc;

internal sealed partial class CoachWindow
{
    private readonly MinuteFarmDirector minuteDirector = new();
    private FrameView? minuteInFlight;
    private (string Session, int Minute)? publishedMinute;

    private void RefreshMinuteFarm(CoachHost target, FrameView? frame)
    {
        if (publishedMinute is { } sent && (frame?.Meta.SessionId != sent.Session ||
            frame.Meta.Player is not { } participant || (int)(participant.Clock / 60) != sent.Minute ||
            !target.Mailbox.Snapshot().HasReminder)) {
            publishedMinute = null;
            visionText.Text = "上一条分钟路线已撤回；当前没有在下发的标记。";
        }
        if (minuteInFlight != null && decisionStop is { IsCancellationRequested: false } &&
            (!minuteDirector.StillCurrent(minuteInFlight, frame) || frame == null || !MinuteFarmArmedFor(frame)))
            CancelDecision();
        string? waiting = !matchState.CanAudit ? "仅实时显示：本机存档不可用，不调用模型。" :
            !preferences.AutomaticAdvice ? "自动教练已暂停。" :
            !preferences.MinimapMarkers ? "请开启「小地图路线」。" :
            !modelConfigured || cloud == null ? modelSetupHint :
            frame == null ? "等待配对的游戏机和可用的 Dota 窗口。" :
            !minuteDirector.Fresh(frame) ? "等待露娜/巨魔进行中的本人 GSI（需存活、能行动、位置及时钟有效）。" :
            !MinuteFarmArmedFor(frame) ? "请在教练端确认本局标准 7.42 地图和 build。" :
            preferences.LayoutFor(frame) == null ? "请确认当前分辨率的小地图框；不会猜左右位置。" :
            modelCalls >= preferences.RequestLimit ? "已到本次启动的请求上限，不再调用模型。" : null;
        if (waiting != null) {
            // Reaching the paid-call limit does not withdraw the final still-valid result.
            if (modelCalls < preferences.RequestLimit || frame == null || !minuteDirector.Fresh(frame) ||
                !preferences.AutomaticAdvice || !preferences.MinimapMarkers || !MinuteFarmArmedFor(frame))
                target.Mailbox.ClearModelReminder();
            autoStatus.Text = "7.42 分钟路线 · " + waiting;
            return;
        }
        // A read-only catalog check blocks new decisions, not delivery of an otherwise valid route.
        if (catalogStop != null) { autoStatus.Text = "7.42 分钟路线 · " + modelSetupHint; return; }
        if (frame == null || cloud == null || preferences.LayoutFor(frame) is not { } layout) return;
        int minute = (int)(frame.Meta.Player!.Clock / 60);
        autoStatus.Text = $"7.42 分钟路线 · {ActiveModelLabel} · {minute}:00–{minute + 1}:00 · 本次启动 {modelCalls}/{preferences.RequestLimit} 次请求" +
            (deciding ? " · 分析中" : "");
        if (!deciding && minuteDirector.TryStart(frame, layout) &&
            matchState.ForFarm(frame, preferences.ModelTeamKnowledge) is { } input)
            _ = DecideMinuteFarm(frame, target, layout, cloud, input);
    }

    private async Task DecideMinuteFarm(FrameView source, CoachHost target, MinimapLayout layout, CloudCoach provider, MatchFarmSlice input)
    {
        deciding = true; minuteInFlight = source;
        long generation = modelGeneration;
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        decisionStop = deadline;
        string? selection = null;
        bool inModelRequest = false;
        try {
            bool Allowed() => matchState.CanAudit && !closing && !stop.IsCancellationRequested && generation == modelGeneration &&
                target == host && preferences.MinuteFarm742 && preferences.AutomaticAdvice &&
                preferences.MinimapMarkers && preferences.LayoutFor(source) == layout && MinuteFarmArmedFor(source);
            var result = await minuteDirector.DecideAndPublish(target.Mailbox, source, layout, preferences.Subtitles,
                Allowed, async (own, scenario, token) => {
                    if (!Allowed() || modelCalls >= preferences.RequestLimit) throw new OperationCanceledException();
                    modelCalls++; // Failures count too. No automatic retry, even after reconnect in the same minute.
                    deadline.CancelAfter(TimeSpan.FromSeconds(preferences.ModelTimeoutSeconds));
                    matchState.NoteDecision(input, "requested", offered: scenario.Options.Select(o => o.Id).ToArray());
                    inModelRequest = true;
                    var decision = await provider.ChooseMinuteFarmWithMetrics(own, scenario, token, input,
                        (data, ct) => matchState.NoteModelInput(input, data, provider.ProviderId, provider.ModelId, ct));
                    inModelRequest = false;
                    selection = decision.OptionId;
                    if (decision.InputTokens is int usedInputTokens) inputTokens += usedInputTokens;
                    if (decision.OutputTokens is int output) outputTokens += output;
                    return decision.OptionId;
                }, deadline.Token, input);
            if (result.Outcome != FarmMinuteOutcome.AlreadyRequested)
                matchState.NoteDecision(input, result.Outcome switch {
                    FarmMinuteOutcome.Published => "published", FarmMinuteOutcome.Abstained => "abstained",
                    FarmMinuteOutcome.NoOptions => "no-options", _ => "expired"
                }, result.Stops, selection);
            if (!Allowed()) return;
            string when = $"{(int)(source.Meta.Player!.Clock / 60)} 分钟";
            visionText.Text = $"{ActiveModelLabel} · {when} · " + (result.Outcome switch {
                FarmMinuteOutcome.Published => string.Join(" → ", result.Stops.Select((p, i) => $"{i + 1}. {p.Label}")) +
                    "\n已向游戏小地图下发；野怪、兵线和安全性待你现场确认，跨分钟撤回。",
                FarmMinuteOutcome.Abstained => "模型选择不建议路线；本分钟没有标记。",
                FarmMinuteOutcome.NoOptions => "剩余时间内没有可验证的行程；不标点。",
                FarmMinuteOutcome.AlreadyRequested => "本分钟已问过模型，不会重复请求。",
                _ => "状态已变或回包过晚，未回传旧路线。",
            });
            if (result.Outcome == FarmMinuteOutcome.Published) {
                publishedMinute = (source.Meta.SessionId, (int)(source.Meta.Player.Clock / 60));
                if (target.Mailbox.Poll(source.Meta.SessionId) is { } sent) debug.Recorder?.Reminder("reminder-published", sent);
            }
        }
        catch (Exception error) {
            matchState.NoteDecision(input, error is OperationCanceledException ? "expired" : "failed");
            // Do not record raw provider text, credentials, or a screenshot in the minute model path.
            debug.Recorder?.Error("minute-farm", error, source.Meta, error is OperationCanceledException ? "cancelled-or-timeout" :
                error is ModelProviderException ? "upstream-status" : "context-or-network");
            if (host == target && preferences.MinuteFarm742 && generation == modelGeneration && !closing)
                ReportModelFailure(error, vision: false, inModelRequest);
        } finally {
            if (decisionStop == deadline) decisionStop = null;
            minuteInFlight = null; deciding = false;
        }
    }
}
