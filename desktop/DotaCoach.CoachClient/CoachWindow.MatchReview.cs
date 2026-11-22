using System;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("DotaCoach.MatchReview.Tests")]

namespace DotaCoach.TwoPc;

internal sealed partial class CoachWindow
{
    private MatchReviewWindow? matchReviewWindow;

    // Body for the caller's Ui.Section. Constructing or expanding it does not even list the archive.
    private FrameworkElement BuildMatchReview()
    {
        var panel = new StackPanel();
        panel.Children.Add(Ui.Text("查看本机保存的历局。按游戏时间回看当时知道了什么；已验证结束后，可另看录像全图。", 12, Brushes.LightGray));
        panel.Children.Add(Ui.Button("打开对局复盘窗口", (_, _) => {
            if (closing) return;
            try {
                if (!matchState.CanAudit || matchArchive == null) {
                    notice.Text = matchState.StorageWarning ?? "档案未打开。请修复本机存储后重启。";
                    return;
                }
                if (matchReviewWindow is { } existing) {
                    if (existing.WindowState == WindowState.Minimized) existing.WindowState = WindowState.Normal;
                    existing.Activate(); return;
                }
                var view = new MatchReviewWindow(ReviewStore.From(matchArchive, () => matchState.CanAudit),
                    () => matchState.ActiveMatch, heroNames, settingsPath) { Owner = this };
                matchReviewWindow = view;
                view.Closed += (_, _) => { if (ReferenceEquals(matchReviewWindow, view)) matchReviewWindow = null; };
                view.Show();
            } catch (Exception e) {
                matchReviewWindow = null; notice.Text = "无法打开复盘窗口：" + MatchReviewText.Clean(e.Message, 180);
            }
        }));
        return panel;
    }

    // Called by the owner BEFORE disposing matchArchive; no child process, import or background read survives it.
    private Task StopMatchReviewAsync() => matchReviewWindow?.StopAsync() ?? Task.CompletedTask;
}
