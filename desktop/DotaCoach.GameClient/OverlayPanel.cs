using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

/// <summary>
/// What the player sees over Dota: which subtitles show and their position, size, colours and see-through, and
/// the enemy panel's place, size and see-through. Every change applies at once, previews over Dota and is saved
/// on this machine only. The four show/hide switches are a separate element (<see cref="Switches"/>) for the
/// home page; the panel itself is the "游戏内显示" settings group.
/// </summary>
internal sealed class OverlayPanel : StackPanel
{
    public const int PreviewMs = 8000;
    private static readonly (string Name, string Hex)[] Palette = [
        ("金色", "#FFD700"), ("卡其", "#F0E68C"), ("白色", "#FFFFFF"), ("浅灰", "#C8C8C8"), ("浅黄", "#FFF3A0"),
        ("橙色", "#FFA500"), ("红色", "#FF5A5A"), ("粉色", "#FF8FC8"), ("青绿", "#59DDBC"), ("亮绿", "#7CFC00"),
        ("天蓝", "#87CEFA"), ("黑色", "#000000"), ("深蓝黑", "#081018"), ("深灰", "#303030"), ("深蓝", "#0A2540")];
    private readonly string path;
    private readonly AdviceOverlay overlay;
    private readonly CheckBox advice = Check("AI 打钱建议"), alerts = Check("对手提醒（离开视野、插眼）"), title = Check("标题行（来源与“未经核验”）"),
        enemies = Check("对手装备面板"), wards = Check("眼位（目击与推测）"), minimapRight = Check("小地图在右侧"),
        waves = Check("在小地图显示兵线与迷雾推演");
    private readonly Slider x = Range(0, 100), y = Range(0, 100),
        size = Range(GameOverlaySettings.MinSize, GameOverlaySettings.MaxSize), width = Range(GameOverlaySettings.MinWidth, GameOverlaySettings.MaxWidth),
        textOpacity = Range(GameOverlaySettings.MinTextOpacity, 100), backgroundOpacity = Range(0, 100),
        enemyY = Range(0, 100), enemySize = Range(GameOverlaySettings.MinSize, GameOverlaySettings.MaxSize),
        enemyOpacity = Range(GameOverlaySettings.MinTextOpacity, 100), minimapSize = Range(GameOverlaySettings.MinMinimap, GameOverlaySettings.MaxMinimap);
    private readonly ComboBox textColor = Ui.Choice(), titleColor = Ui.Choice(), backgroundColor = Ui.Choice();
    private readonly TextBlock status = Ui.Text("", 12, Ui.Accent);
    private readonly DispatcherTimer saver = new() { Interval = TimeSpan.FromMilliseconds(400) };
    private bool loading;
    public GameOverlaySettings Current { get; private set; }
    /// <summary>Show or hide AI advice, Plus alerts, the enemy panel and ward marks: the home page's everyday switches.</summary>
    public WrapPanel Switches { get; } = new();

    public OverlayPanel(string file, AdviceOverlay target)
    {
        path = file; overlay = target;
        Current = GameOverlaySettings.Load(path);
        foreach (var combo in new[] { textColor, titleColor, backgroundColor }) {
            foreach (var (name, hex) in Palette) combo.Items.Add(Swatch(name, hex));
            combo.SelectionChanged += (_, _) => Changed();
        }
        foreach (var box in new[] { advice, alerts, title, enemies, wards, minimapRight, waves }) { box.Checked += (_, _) => Changed(); box.Unchecked += (_, _) => Changed(); }
        foreach (var box in new[] { advice, alerts, enemies, wards }) Switches.Children.Add(box);
        Children.Add(Ui.Label("字幕（显示哪些字幕在主页勾选；只管这台机器怎么显示，推不推送、显示几秒在教练端设置）"));
        Children.Add(title);
        Children.Add(Ui.Columns(
            Cell("左右位置", x, v => v switch { 0 => "最左", 50 => "居中", 100 => "最右", _ => v + "%" }),
            Cell("上下位置", y, v => v switch { 0 => "最上", 50 => "正中", 100 => "最下", _ => v + "%" }),
            Cell("字号", size, v => v + "%"), Cell("最大宽度", width, v => v + "% 画面宽")));
        Children.Add(Ui.Columns(Labeled("文字颜色", textColor), Labeled("标题颜色", titleColor), Labeled("背景颜色", backgroundColor),
            Cell("文字不透明度", textOpacity, v => v + "%"), Cell("背景不透明度", backgroundOpacity, v => v == 0 ? "全透明" : v + "%")));
        Children.Add(Ui.Label("对手面板：教练端推送的上次读到的等级和装备，显示在游戏左侧，到本局结束。未见不补猜。"));
        Children.Add(Ui.Columns(
            Cell("上下位置", enemyY, v => v switch { 0 => "最上", 50 => "正中", 100 => "最下", _ => v + "%" }),
            Cell("大小", enemySize, v => v + "%"), Cell("不透明度", enemyOpacity, v => v + "%")));
        Children.Add(Ui.Label("眼位：◆ 表示队伍视角实际目击（绿边己方、红边敌方）；虚线圈和 ? 只表示推测区域。金色侦察、蓝色岗哨，和装备、分钟路线分别显示。"));
        var mapChecks = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        mapChecks.Children.Add(minimapRight);
        Children.Add(Ui.Columns(mapChecks, Cell("小地图大小", minimapSize, v => v == 100 ? "默认" : v + "%")));
        Children.Add(waves);
        Children.Add(Ui.Text("兵线需教练端确认小地图框。● 是实见的小兵，○ 是进雾后的短时估计，△ 是理论增援，× 是交汇。", 11, Brushes.LightGray));
        Children.Add(Ui.Row(status,
            Ui.Button("预览 8 秒", (_, _) => { overlay.Preview(PreviewMs); status.Text = PreviewNote(); }),
            Ui.Button("恢复默认", (_, _) => { Fill(new()); Changed(); })));
        Children.Add(Ui.Text("改动立即生效并保存在本机。调整时示例字幕和对手面板会在 Dota 画面上显示 8 秒：Dota 用无边框窗口，" +
            "放在另一块屏幕上调最方便（本窗口在前台时也会显示预览）。只隐藏字幕，地图路线圈照常显示。" +
            "装备缺图时显示上次读到的名字与栏位。教练端没有确认小地图框时，眼位按「小地图在右侧」和「小地图大小」画，" +
            "请用预览的蓝框对准游戏里的小地图；分钟路线仍须在教练端校准。", 11, Brushes.LightGray));
        Fill(Current);
        overlay.Settings = Current;
        saver.Tick += (_, _) => Flush();
    }
    /// <summary>Writes a pending change now (the panel saves 0.4 s after the last change).</summary>
    public void Flush()
    {
        if (!saver.IsEnabled) return;
        saver.Stop();
        try { Current.Save(path); }
        catch (Exception e) { status.Text = $"保存失败（{e.GetType().Name}），本次运行仍按新设置显示。"; }
    }
    private void Changed()
    {
        if (loading) return;
        Current = Read(); overlay.Settings = Current; overlay.Preview(PreviewMs);
        status.Text = PreviewNote();
        saver.Stop(); saver.Start();
    }
    private static string PreviewNote() => GameWindow.Find(out bool minimized) != null ? $"已应用，Dota 画面上预览 {PreviewMs / 1000} 秒。" :
        minimized ? "已应用。Dota 最小化了，看不到预览：请用无边框窗口。" : "已应用。没找到 Dota 窗口，开着 Dota 调整可以直接看到效果。";
    private GameOverlaySettings Read() => new() {
        ShowAdvice = advice.IsChecked == true, ShowAlerts = alerts.IsChecked == true, ShowTitle = title.IsChecked == true,
        X = Whole(x), Y = Whole(y), Size = Whole(size), Width = Whole(width),
        TextColor = Hex(textColor), TitleColor = Hex(titleColor), BackgroundColor = Hex(backgroundColor),
        TextOpacity = Whole(textOpacity), BackgroundOpacity = Whole(backgroundOpacity),
        ShowEnemies = enemies.IsChecked == true, EnemyY = Whole(enemyY), EnemySize = Whole(enemySize), EnemyOpacity = Whole(enemyOpacity),
        ShowWards = wards.IsChecked == true, MinimapRight = minimapRight.IsChecked == true, MinimapSize = Whole(minimapSize),
        ShowLaneWaves = waves.IsChecked == true,
    };
    private void Fill(GameOverlaySettings s)
    {
        loading = true;
        advice.IsChecked = s.ShowAdvice; alerts.IsChecked = s.ShowAlerts; title.IsChecked = s.ShowTitle;
        x.Value = s.X; y.Value = s.Y; size.Value = s.Size; width.Value = s.Width;
        textOpacity.Value = s.TextOpacity; backgroundOpacity.Value = s.BackgroundOpacity;
        enemies.IsChecked = s.ShowEnemies; enemyY.Value = s.EnemyY; enemySize.Value = s.EnemySize; enemyOpacity.Value = s.EnemyOpacity;
        wards.IsChecked = s.ShowWards; minimapRight.IsChecked = s.MinimapRight; minimapSize.Value = s.MinimapSize;
        waves.IsChecked = s.ShowLaneWaves;
        Select(textColor, s.TextColor); Select(titleColor, s.TitleColor); Select(backgroundColor, s.BackgroundColor);
        loading = false;
    }
    /// <summary>Picks the palette entry; a colour typed into the settings file by hand is kept as its own entry.</summary>
    private static void Select(ComboBox combo, string hex)
    {
        var item = combo.Items.OfType<ComboBoxItem>().FirstOrDefault(i => string.Equals((string)i.Tag, hex, StringComparison.OrdinalIgnoreCase));
        if (item == null) combo.Items.Add(item = Swatch("自定义 " + hex.ToUpperInvariant(), hex));
        combo.SelectedItem = item;
    }
    private static string Hex(ComboBox combo) => (string)((ComboBoxItem)combo.SelectedItem).Tag;
    private static int Whole(Slider slider) => (int)Math.Round(slider.Value);
    private static ComboBoxItem Swatch(string name, string hex)
    {
        var (red, green, blue) = GameOverlaySettings.Rgb(hex);
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(new Rectangle { Width = 14, Height = 14, Margin = new Thickness(0, 0, 6, 0), Stroke = Brushes.Gray,
            Fill = new SolidColorBrush(Color.FromRgb(red, green, blue)) });
        row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
        return new ComboBoxItem { Content = row, Tag = hex };
    }
    private static CheckBox Check(string text) =>
        new() { Content = text, Foreground = Brushes.White, Margin = new Thickness(0, 2, 16, 6) };
    private static Slider Range(int min, int max) => new() {
        Minimum = min, Maximum = max, IsSnapToTickEnabled = true, TickFrequency = 1, SmallChange = 1, LargeChange = 5,
        IsMoveToPointEnabled = true, Margin = new Thickness(0, 0, 0, 8),
    };
    private StackPanel Cell(string name, Slider slider, Func<int, string> describe)
    {
        var label = Ui.Text("", 12, Brushes.LightGray);
        label.Margin = new Thickness(0, 4, 0, 2);
        void Show() => label.Text = name + "：" + describe(Whole(slider));
        slider.ValueChanged += (_, _) => { Show(); Changed(); };
        Show();
        return Labeled(label, slider);
    }
    private static StackPanel Labeled(string name, Control control)
    {
        var label = Ui.Text(name, 12, Brushes.LightGray);
        label.Margin = new Thickness(0, 4, 0, 2);
        return Labeled(label, control);
    }
    private static StackPanel Labeled(TextBlock label, Control control)
    {
        var cell = new StackPanel(); cell.Children.Add(label); cell.Children.Add(control); return cell;
    }
}
