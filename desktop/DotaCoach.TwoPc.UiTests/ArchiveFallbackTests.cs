using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static partial class Program
{
    private static async Task LockedWriterUi(string output)
    {
        string root = Directory.CreateDirectory(Path.Combine(output, "occupied-archive")).FullName;
        using var exclusive = new MatchArchive(Path.Combine(root, "match-history"));
        var coachType = Assembly.Load("DotaCoach.CoachClient").GetType("DotaCoach.TwoPc.CoachWindow")!;
        var w = (Window)Activator.CreateInstance(coachType, [null, false, Path.Combine(root, "prefs.json"), Path.Combine(root, "debug")])!;
        w.ShowInTaskbar = false; w.Show();
        try {
            await Wait(() => Field<MatchArchive?>(w, "matchArchive") == null && !Field<LiveMatchState>(w, "matchState").CanAudit &&
                Field<TextBlock>(w, "detail").Text.Contains("仅实时显示") && Field<Button>(w, "listen").IsEnabled,
                "a locked archive opens a clearly marked live-only coach rather than disabling pairing or taking over the writer");
            Click(w, "打开对局复盘窗口");
            await Wait(() => w.OwnedWindows.Count == 0 && Field<TextBlock>(w, "notice").Text.Contains("当前不会存档"),
                "a live-only second instance cannot read/recover/replace the other instance's archive");
        } finally { w.Close(); await Wait(() => !w.IsVisible, "live-only coach closes without changing the active writer"); }
    }
}
