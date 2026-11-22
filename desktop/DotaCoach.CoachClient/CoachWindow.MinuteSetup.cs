using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

internal sealed partial class CoachWindow
{
    private readonly CheckBox minuteFarm742 = new() {
        Content = "7.42 分钟打钱（按游戏时钟，每分钟最多调用一次模型）", Foreground = Brushes.White
    };
    private readonly StackPanel minuteSetup = new();
    private readonly TextBlock minuteSetupStatus = Ui.Text("", 13, Ui.Accent);
    private Button confirmMinuteGame = null!, withdrawMinuteGame = null!;

    // The checkbox is a saved preference. Neither the offered nor the accepted attestation goes to disk.
    private sealed record MinuteGame(string Session, string Match, string Hero, string Team, MinimapLayout Layout);
    private MinuteGame? offeredMinuteGame, confirmedMinuteGame;

    private void BuildMinuteSetup(StackPanel switches)
    {
        minuteFarm742.IsChecked = preferences.MinuteFarm742; // Restore the selection, never a confirmation.
        minuteFarm742.Checked += (_, _) => SaveFeatureToggles();
        minuteFarm742.Unchecked += (_, _) => SaveFeatureToggles();
        switches.Children.Add(minuteFarm742);
        switches.Children.Add(minuteSetup);
        minuteSetup.Children.Add(Ui.Text("只记住模式，不记住本局确认。换局、重连或重启后请重新确认；未确认不调用模型，取消勾选才回到普通画面模式。" +
            "GSI 不能证明补丁，请在游戏机独立核对。标记来自模拟；有兵才收、有野才打，遇险撤退。", 12, Brushes.LightGray));
        minuteSetup.Children.Add(minuteSetupStatus);
        confirmMinuteGame = Ui.Button("我确认本局为标准 dota 地图（7.42 / build 6989），小地图框已对准", (_, _) => {
            // Never accept a new match, connection or rectangle which arrived between rendering the offer and clicking it.
            var shown = offeredMinuteGame;
            if (confirmedMinuteGame == null && shown != null && shown == MinuteOffer(host?.Mailbox.Latest(), out _)) {
                CancelDecision(); host?.Mailbox.ClearReminder();
                confirmedMinuteGame = shown;
            }
            UpdateMinuteSetup();
        });
        withdrawMinuteGame = Ui.Button("撤回本局确认（保留分钟模式）", (_, _) => {
            ResetMinuteFarmConfirmation();
            UpdateMinuteSetup();
        });
        minuteSetup.Children.Add(confirmMinuteGame);
        minuteSetup.Children.Add(withdrawMinuteGame);
        UpdateMinuteSetup();
        // Only present the current offer/status. This handler neither schedules model work nor reads image pixels.
        timer.Tick += (_, _) => { if (preferences.MinuteFarm742) UpdateMinuteSetup(); };
    }

    private bool MinuteSwitchesOn => e2e == null && preferences.MinuteFarm742 && preferences.AutomaticAdvice && preferences.MinimapMarkers;
    private static bool MinuteSource(FrameView frame) => !frame.Meta.IsLegacyStatus &&
        (frame.Meta.Source == "dota2-gsi" || frame is { HasImage: true, Meta.Source: "dota2" });
    private bool MinuteDraft(FrameView frame) => draftLayout?.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight) == true;

    private MinuteGame? MinuteOffer(FrameView? frame, out string hint)
    {
        if (!MinuteSwitchesOn) { hint = "已暂停：分钟模式需同时开启「自动教练」和「小地图路线」。"; return null; }
        if (frame == null || !MinuteSource(frame) || frame.Meta.Player is not { } p) {
            hint = "等待游戏机的新鲜本人对局 GSI 和游戏窗口尺寸；可使用仅 GSI 连接。"; return null;
        }
        if (BoardDelivery.InstalledBuild(frame.Meta) is int build && build != MinuteFarmContext.SupportedBuild) {
            hint = $"游戏机上报的安装 build 为 {build}，与 6989 不符。分钟模式已暂停，不能确认。"; return null;
        }
        if (string.IsNullOrEmpty(p.MatchId) || p.MatchId[0] == '0') {
            hint = "没有可识别本局的对局号，不能确认版本。"; return null;
        }
        if (!MinuteFarmContext.Fresh(frame, Environment.TickCount64)) {
            hint = "等待新鲜的本人 GSI：需为正在进行且未暂停的玩家对局，观战数据不可用。"; return null;
        }
        hint = $"对局 {p.MatchId} · {frame.Meta.SourceWidth}×{frame.Meta.SourceHeight}";
        if (preferences.LayoutFor(frame) is not { } layout || MinuteDraft(frame)) {
            hint += "：请在「小地图校准」核对原始像素并点「确认校准」。预设和自动左下框都不算确认。"; return null;
        }
        hint += $" · 已校准：左 {layout.Left:0.##}、上 {layout.Top:0.##}、边长 {layout.Size:0.##}";
        return new(frame.Meta.SessionId, p.MatchId, p.Hero, p.Team, layout);
    }

    private bool MinuteContextChanged(MinuteGame game, FrameView frame) =>
        !MinuteSource(frame) || frame.Meta.SessionId != game.Session || preferences.LayoutFor(frame) != game.Layout || MinuteDraft(frame) ||
        frame.Meta.Player is { } p && (p.MatchId != game.Match || p.Hero != game.Hero || p.Team != game.Team ||
                                       BoardDelivery.InstalledBuild(frame.Meta) is int build && build != MinuteFarmContext.SupportedBuild);

    // At publication the original evidence may be seconds old; check its scope AND fresh current evidence.
    // Never read WPF controls or accept an automatic layout here.
    private bool MinuteFarmArmedFor(FrameView frame) => confirmedMinuteGame is { } accepted && MinuteSwitchesOn &&
        frame.Meta.GsiAgeMs is >= 0 and < MinuteFarmContext.FreshMs && MinuteFarmContext.Eligible(frame.Meta.Player) &&
        !MinuteContextChanged(accepted, frame) && host?.Mailbox.Latest() is { } current &&
        MinuteFarmContext.Fresh(current, Environment.TickCount64) && !MinuteContextChanged(accepted, current);

    private void ResetMinuteFarmConfirmation()
    {
        offeredMinuteGame = null;
        if (confirmedMinuteGame == null) return;
        confirmedMinuteGame = null;
        CancelDecision(); host?.Mailbox.ClearReminder(); visionText.Text = "";
    }

    private void UpdateMinuteSetup()
    {
        bool selected = preferences.MinuteFarm742;
        minuteSetup.Visibility = selected ? Visibility.Visible : Visibility.Collapsed;
        autoMinimap.IsEnabled = !selected; // Preserve the ordinary mode's auto-layout preference.
        var frame = selected ? host?.Mailbox.Latest() : null;
        if (confirmedMinuteGame is { } old && (!MinuteSwitchesOn || (frame == null
            ? host?.Mailbox.Snapshot().SessionId != old.Session : MinuteContextChanged(old, frame))))
            ResetMinuteFarmConfirmation();
        if (!selected) { offeredMinuteGame = null; return; }

        // A transient gap in this same game pauses work, but is not a new map/build attestation.
        offeredMinuteGame = MinuteOffer(frame, out string hint);
        bool accepted = confirmedMinuteGame != null;
        string text = offeredMinuteGame == null ? hint : hint + (accepted ? " · 本局已人工确认。" : " · 等待本局人工确认。");
        if (minuteSetupStatus.Text != text) minuteSetupStatus.Text = text;
        confirmMinuteGame.IsEnabled = !accepted && offeredMinuteGame != null;
        confirmMinuteGame.Visibility = accepted ? Visibility.Collapsed : Visibility.Visible;
        withdrawMinuteGame.Visibility = accepted ? Visibility.Visible : Visibility.Collapsed;
    }
}
