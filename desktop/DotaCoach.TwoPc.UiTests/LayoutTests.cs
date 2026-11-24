using System.IO;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static partial class Program
{
    /// <summary>
    /// The everyday surface the user asked for on 2026-11-22: each client's home page has its status and only the
    /// everyday switches; everything set once lives in named settings groups. Pinned here so moving settings back
    /// onto the home page, or a picture mode into the released game client, fails a test instead of a match.
    /// Also renders every page to the output folder for a look.
    /// </summary>
    private static async Task LayoutChecks(string output, Type coachType)
    {
        static void Check(bool ok, string name) { if (!ok) throw new Exception("Layout: " + name); passed++; Console.WriteLine("PASS " + name); }
        static string[] Labels<T>(FrameworkElement root) where T : ContentControl =>
            Children(root).OfType<T>().Select(c => c.Content as string ?? "").Order().ToArray();
        static bool Inputs(FrameworkElement root) => Children(root).Any(e => e is TextBox or ComboBox or Slider);
        bool developer = Features.VideoUpload;
        Features.VideoUpload = false; // the released game client
        string dir = Directory.CreateDirectory(Path.Combine(output, "layout")).FullName;
        var coach = OfflineCoach(coachType, Path.Combine(dir, "coach.json"), Path.Combine(dir, "debug"));
        var game = new GameWindowUi(null, Path.Combine(dir, "debug"), Path.Combine(dir, "video.json"), Path.Combine(dir, "overlay.json"));
        try {
            coach.Show(); game.Show();
            var coachHome = HomePage(coach);
            Check(Labels<CheckBox>(coachHome).SequenceEqual(new[] { "自动教练", "7.42 分钟打钱（按游戏时钟，每分钟最多调用一次模型）",
                    "对手等级、装备和属性（游戏左侧面板）", "对手离开视野", "可能的敌方眼位（推测）" }.Order()) &&
                Labels<Button>(coachHome).SequenceEqual(new[] { "复制配对码", "打开对局复盘窗口", "启动视觉扫描",
                    "我确认本局为标准 dota 地图（7.42 / build 6989），小地图框已对准", "撤回本局确认（保留分钟模式）" }.Order()) &&
                !Inputs(coachHome),
                "coach home: pairing code, review, scan, the AI and minute switches and the three push switches; no input fields");
            Check(SettingsGroups(coach).SequenceEqual(["连接", "Plus 观战与扫描", "AI 模型", "小地图校准", "调试"]) &&
                Children(SettingsGroup(coach, "连接")).Contains(Field<ComboBox>(coach, "address")) &&
                Children(SettingsGroup(coach, "连接")).Contains(Field<Button>(coach, "listen")) &&
                Children(SettingsGroup(coach, "Plus 观战与扫描")).Contains(Field<TextBox>(coach, "spectatorInstall")) &&
                Children(SettingsGroup(coach, "Plus 观战与扫描")).Contains(Field<TextBox>(coach, "scanFolder")) &&
                Children(SettingsGroup(coach, "Plus 观战与扫描")).Contains(Field<TextBox>(coach, "visionLossDelay")) &&
                Children(SettingsGroup(coach, "Plus 观战与扫描")).Contains(Field<CheckBox>(coach, "fogOverlay")) &&
                Children(SettingsGroup(coach, "AI 模型")).Contains(Field<ComboBox>(coach, "modelProvider")) &&
                Children(SettingsGroup(coach, "AI 模型")).Contains(Field<CheckBox>(coach, "subtitles")) &&
                Children(SettingsGroup(coach, "AI 模型")).Contains(Field<CheckBox>(coach, "mapMarkers")) &&
                Children(SettingsGroup(coach, "小地图校准")).Contains(Field<TextBox>(coach, "mapLeft")) &&
                Children(SettingsGroup(coach, "调试")).Contains(DebugSwitch(coach)),
                "coach settings: connection, Plus and scanner, AI model, minimap calibration and debug groups hold their controls");

            var gameHome = HomePage(game);
            Check(Labels<CheckBox>(gameHome).SequenceEqual(new[] { "AI 打钱建议", "对手提醒（离开视野、插眼）", "对手装备面板", "眼位（目击与推测）" }.Order()) &&
                Labels<Button>(gameHome).SequenceEqual(new[] { "连接", "显示小兵血量" }.Order()) && !Inputs(gameHome) &&
                Children(gameHome).OfType<PasswordBox>().Count() == 1,
                "game home: pairing code, connect, creep HP and the four show/hide switches; no other inputs");
            Check(SettingsGroups(game).SequenceEqual(["游戏内显示", "小兵血量", "Dota 与 GSI", "调试"]) &&
                !Children(game).Contains(Field<ComboBox>(game, "source")) && !Children(game).Contains(Field<ComboBox>(game, "fpsChoice")) &&
                Children(SettingsGroup(game, "游戏内显示")).Contains(Field<OverlayPanel>(game, "overlayPanel")) &&
                Children(SettingsGroup(game, "Dota 与 GSI")).Contains(Field<TextBox>(game, "dotaInstall")) &&
                Children(SettingsGroup(game, "调试")).Contains(DebugSwitch(game)),
                "released game client settings have no connection mode or video group anywhere");

            foreach (var (w, name) in new[] { ((Window)coach, "coach"), (game, "game") }) {
                Check(HomePage(w).Visibility == Visibility.Visible && SettingsPage(w).Visibility == Visibility.Collapsed,
                    $"{name} opens on the home page");
                Render(w, Path.Combine(dir, $"{name}-home.png"));
                foreach (string group in SettingsGroups(w)) {
                    ShowGroup(w, group);
                    Check(HomePage(w).Visibility == Visibility.Collapsed && SettingsGroups(w).All(g =>
                            SettingsGroup(w, g).Visibility == (g == group ? Visibility.Visible : Visibility.Collapsed)),
                        $"{name} settings show only the {group} group");
                    Render(w, Path.Combine(dir, $"{name}-settings-{SettingsGroups(w).ToList().IndexOf(group)}.png"));
                }
                ShowHome(w);
                Check(HomePage(w).Visibility == Visibility.Visible && SettingsPage(w).Visibility == Visibility.Collapsed &&
                    Children(w).OfType<Button>().Any(b => Equals(b.Content, "设置")), $"{name} returns to the home page");
            }
        } finally {
            coach.Close(); game.Close();
            Features.VideoUpload = developer;
        }
        await Wait(() => !coach.IsVisible && !game.IsVisible, "layout fixtures close");
    }
}
