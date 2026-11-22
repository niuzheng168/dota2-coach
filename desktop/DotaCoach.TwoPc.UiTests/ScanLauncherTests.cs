using System.IO;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static partial class Program
{
    // The test apphost stands in for Python so a fresh worktree exercises real process startup/EOF/restart
    // without vision dependencies, reading game assets, executing repository scripts or touching the user's cfg.
    private static void ScannerStandIn(string[] args)
    {
        if (args is not ["-I", "-B", "-X", "utf8", "-u", var script, var command, .. var rest] || script != HeroScanProcess.Script ||
            !File.Exists(".ui-scanner-fixture")) throw new ArgumentException("Unexpected scan fixture invocation");
        string root = Environment.CurrentDirectory, profile = Directory.GetParent(root)!.FullName;
        string live = Path.Combine(profile, "scan-button", "live.json"), assets = Path.Combine(root, ".tmp", "vision-assets");
        if (command == "setup" && rest.SequenceEqual(["--install", Path.Combine(profile, "fake-dota-coach"), "--secrets", HeroScanProcess.SecretsBeside(live)])) {
            Console.WriteLine("Wrote fixture cfg - restart Dota once so it is loaded."); return;
        }
        string? Value(string option) => Array.IndexOf(rest, option) is >= 0 and var i ? rest.ElementAtOrDefault(i + 1) : null;
        if (command != "scan" || !rest.Contains("--stop-with-stdin") || !rest.Contains("--team-view-only") ||
            Value("--assets") != assets || Value("--calibration") != Path.Combine(assets, "hud-1080.npz") || Value("--live") != live ||
            Value("--secrets") != HeroScanProcess.SecretsBeside(live)) throw new ArgumentException("Expected isolated, team-view-only scan arguments");
        Console.WriteLine("Waiting for the Dota window..."); Console.Out.Flush();
        Console.OpenStandardInput().CopyTo(Stream.Null);
        File.AppendAllText("stopped.txt", "stopped\n");
    }

    private static async Task<string> ScanLauncherChecks(Window w, string profile, string fakeInstall)
    {
        Set(w, "scanFile", Path.Combine(profile, "scan-button", "live.json"));
        var workspace = Field<TextBox>(w, "scanFolder"); var python = Field<TextBox>(w, "scanPython");
        var fromWorkspace = Field<CheckBox>(w, "scanFromWorkspace");
        string? discovered = HeroScanProcess.FindWorkspace(AppContext.BaseDirectory);
        await Wait(() => workspace.Text == (discovered ?? "") && python.Text == (discovered == null ? "" : HeroScanProcess.FindPython(discovered) ?? "") &&
            fromWorkspace.IsChecked == false, "scan paths discover local assets and Python without opting into executing workspace scripts");
        workspace.Text = fakeInstall; fromWorkspace.IsChecked = true;
        Click(w, "启动视觉扫描");
        await Wait(() => Field<TextBlock>(w, "scanState").Text.Contains(@"scripts\vision\hero_scan.py") && Field<HeroScanProcess?>(w, "scan") == null,
            "a trusted-workspace opt-in still refuses a missing or unsupported script");
        string root = Directory.CreateDirectory(Path.Combine(profile, "fake-scanner")).FullName;
        Directory.CreateDirectory(Path.Combine(root, "scripts", "vision"));
        File.WriteAllText(Path.Combine(root, HeroScanProcess.Script), "# fixture capabilities: --stop-with-stdin --team-view-only");
        File.WriteAllText(Path.Combine(root, ".ui-scanner-fixture"), "test-only");
        string assets = Directory.CreateDirectory(Path.Combine(root, ".tmp", "vision-assets")).FullName;
        File.WriteAllText(Path.Combine(assets, "manifest.json"), "{}"); File.WriteAllBytes(Path.Combine(assets, "hud-1080.npz"), []);
        workspace.Text = root; python.Text = Path.Combine(AppContext.BaseDirectory, "DotaCoach.TwoPc.UiTests.exe");
        if (!File.Exists(python.Text)) throw new FileNotFoundException("Expected the test apphost for the isolated scanner fixture", python.Text);
        var state = Field<TextBlock>(w, "scanState"); var button = Field<Button>(w, "scanButton");
        Click(w, "启动视觉扫描");
        await Wait(() => state.Text.Contains("等待本机 Dota 启动") && state.Text.Contains("完全退出再启动") && Equals(button.Content, "停止视觉扫描") &&
            LiveCoachSettings.Load(Path.Combine(profile, "spectator-settings.json")) is { ScanFromWorkspace: true } saved &&
            saved.ScanFolder == root && saved.ScanPython == python.Text, "scan button runs setup and team-view scan and remembers the explicitly chosen paths");
        Click(w, "停止视觉扫描");
        await Wait(() => state.Text == "视觉扫描：已停止" && File.Exists(Path.Combine(root, "stopped.txt")) && Equals(button.Content, "启动视觉扫描"),
            "the stop button closes the fixture's standard input");
        Click(w, "启动视觉扫描");
        await Wait(() => state.Text.Contains("等待本机 Dota 启动"), "the scan starts again");
        return root; // owner closes it; the caller checks the second EOF receipt
    }
}
