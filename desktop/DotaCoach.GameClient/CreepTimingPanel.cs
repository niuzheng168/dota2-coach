using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

/// <summary>
/// When the red creep HP numbers come (<see cref="CreepHpSettings"/>): looking ahead at the hits other creeps and towers are
/// about to land, the player's reaction and Dota's ping. Every change applies at once and is saved on this machine only.
/// </summary>
internal sealed class CreepTimingPanel : StackPanel
{
    private readonly string path;
    private readonly CheckBox lookAhead = new() {
        Content = "红字提前看：算上别的兵和塔接下来按节奏要打的几下（推荐）", Foreground = Brushes.White, Margin = new Thickness(0, 2, 16, 6) };
    private readonly Slider reaction = Range(CreepHpSettings.MaxReactionMs, 10), ping = Range(CreepHpSettings.MaxPingMs, 5);
    private readonly TextBlock status = Ui.Text("", 12, Ui.Accent);
    private readonly DispatcherTimer saver = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool loading;
    /// <summary>Read by the creep HP display every frame.</summary>
    public CreepHpSettings Current { get; private set; }

    public CreepTimingPanel(string file)
    {
        path = file;
        Current = CreepHpSettings.Load(path);
        lookAhead.Checked += (_, _) => Changed(); lookAhead.Unchecked += (_, _) => Changed();
        Children.Add(lookAhead);
        Children.Add(Ui.Columns(
            Cell("反应提前量", reaction, v => $"{v} 毫秒（比你看到变红到按下攻击的时间短约 50 毫秒）"),
            Cell("网络延迟（游戏里的 ping）", ping, v => $"{v} 毫秒")));
        Children.Add(Ui.Row(status, Ui.Button("恢复默认时机", (_, _) => { Fill(new()); Changed(); })));
        Children.Add(Ui.Text("提前看：线上兵每 1 秒、塔每 0.83 秒打一下，按血条上看到的节奏推算你这一刀落地前它还会挨几下，" +
            "红字因此比“这一帧就够一刀”提前出现。看到变红就按攻击：数字变红的时刻已经扣掉了你的反应、网络延迟、" +
            "英雄前摇和弹道。本地补刀实验室里，反应 0.2 秒的玩家补到 70% 的兵，只看这一帧是 31%。关掉则回到只看这一帧。", 11, Brushes.LightGray));
        Fill(Current);
        saver.Tick += (_, _) => Flush();
    }
    /// <summary>Writes a pending change now (the panel saves 0.4 s after the last change).</summary>
    public void Flush()
    {
        if (!saver.IsEnabled) return;
        saver.Stop();
        try { Current.Save(path); }
        catch (Exception e) { status.Text = $"保存失败（{e.GetType().Name}），本次运行仍按新设置。"; }
    }
    private void Changed()
    {
        if (loading) return;
        Current = new() { LookAhead = lookAhead.IsChecked == true, ReactionMs = Whole(reaction), PingMs = Whole(ping) };
        reaction.IsEnabled = ping.IsEnabled = Current.LookAhead;
        status.Text = "已应用。";
        saver.Stop(); saver.Start();
    }
    private void Fill(CreepHpSettings s)
    {
        loading = true;
        lookAhead.IsChecked = s.LookAhead; reaction.Value = s.ReactionMs; ping.Value = s.PingMs;
        reaction.IsEnabled = ping.IsEnabled = s.LookAhead;
        loading = false;
    }
    private static int Whole(Slider slider) => (int)Math.Round(slider.Value);
    private static Slider Range(int max, int step) => new() {
        Minimum = 0, Maximum = max, IsSnapToTickEnabled = true, TickFrequency = step, SmallChange = step, LargeChange = step * 5,
        IsMoveToPointEnabled = true, Margin = new Thickness(0, 0, 0, 8),
    };
    private StackPanel Cell(string name, Slider slider, Func<int, string> describe)
    {
        var label = Ui.Text("", 12, Brushes.LightGray);
        label.Margin = new Thickness(0, 4, 0, 2);
        void Show() => label.Text = name + "：" + describe(Whole(slider));
        slider.ValueChanged += (_, _) => { Show(); Changed(); };
        Show();
        var cell = new StackPanel();
        cell.Children.Add(label); cell.Children.Add(slider);
        return cell;
    }
}
