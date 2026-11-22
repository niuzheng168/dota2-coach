using System;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

internal sealed partial class CoachWindow
{
    private readonly TextBox scanFolder = Ui.Input(), scanPython = Ui.Input();
    private readonly TextBlock scanState = Ui.Text("视觉扫描：未启动", 12, Brushes.LightGray);
    private readonly CheckBox scanFromWorkspace = new() { Content = "使用这个仓库的扫描脚本（开发用，仅限可信仓库）", Foreground = Brushes.White };
    private Button? scanButton;
    private HeroScanProcess? scan;
    private bool scanBusy;
    private string scanNote = "";

    private void AddScanLauncher(StackPanel panel)
    {
        scanFolder.Text = preferences.ScanFolder.Length > 0 ? preferences.ScanFolder : HeroScanProcess.FindWorkspace(AppContext.BaseDirectory) ?? "";
        scanPython.Text = preferences.ScanPython.Length > 0 ? preferences.ScanPython :
            scanFolder.Text.Length > 0 ? HeroScanProcess.FindPython(scanFolder.Text) ?? "" : "";
        scanFromWorkspace.IsChecked = preferences.ScanFromWorkspace;
        panel.Children.Add(Ui.Label(@"视觉扫描（窗口上方启动）：本机素材工作目录，含 .tmp\vision-assets")); panel.Children.Add(scanFolder);
        panel.Children.Add(Ui.Label(@"装有视觉依赖的 Python（如 .tmp\vision-tools\Scripts\python.exe）")); panel.Children.Add(scanPython);
        panel.Children.Add(scanFromWorkspace);
        panel.Children.Add(Ui.Text("默认运行随教练端提供的脚本，不从素材目录执行代码。两种启动方式都限本队视角；不会自动启动扫描。", 12, Brushes.LightGray));
    }

    private async Task ReleaseScan()
    {
        var owned = scan; scan = null;
        if (owned != null) await owned.DisposeAsync();
    }

    private async Task ToggleScan()
    {
        if (scanBusy || closing) return;
        scanBusy = true; scanNote = ""; UpdateScanButton();
        try {
            if (scan != null) {
                bool wasRunning = scan.ExitCode == null;
                await ReleaseScan();
                if (wasRunning) { scanNote = "已停止"; return; }
            }
            string workspace = scanFolder.Text.Trim(), python = scanPython.Text.Trim(), install = spectatorInstall.Text.Trim();
            bool localScripts = scanFromWorkspace.IsChecked == true;
            string? scripts = localScripts ? workspace : HeroScanProcess.FindFolder(AppContext.BaseDirectory);
            if (scripts == null || !Path.IsPathFullyQualified(scripts) || !HeroScanProcess.Supported(scripts)) {
                scanNote = localScripts ? @"未找到支持仅队伍视角和安全停止的 scripts\vision\hero_scan.py：请更新所选仓库或改用随包脚本。" :
                    "缺少支持仅队伍视角和安全停止的随包扫描脚本。"; return;
            }
            if (!Path.IsPathFullyQualified(workspace) || !File.Exists(Path.Combine(workspace, ".tmp", "vision-assets", "manifest.json")) ||
                !File.Exists(Path.Combine(workspace, ".tmp", "vision-assets", "hud-1080.npz"))) {
                scanNote = "请选择含本机视觉素材和 HUD 标定的工作目录；见 ENEMY-WARDS.md。"; return;
            }
            if (python.Length == 0) python = HeroScanProcess.FindPython(workspace) ?? "";
            if (!Path.IsPathFullyQualified(python) || !File.Exists(python) ||
                !Path.IsPathFullyQualified(install) || !Directory.Exists(Path.Combine(install, "game", "dota"))) {
                scanNote = "请核对本机 Dota 和视觉 Python 路径。"; return;
            }
            if (ScanFeed.Poll(scanFile, null, DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()).Status.Running) {
                scanNote = "已有外部视觉扫描；不会重复启动。"; return;
            }
            scanPython.Text = python;
            preferences = preferences with { ScanFolder = workspace, ScanPython = python, ScanFromWorkspace = localScripts };
            try { preferences.Save(settingsPath); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException) {
                // A rejected model profile or unwritable settings file must not enable a fallback provider or block a local scan.
            }
            if (!HeroScanProcess.Configured(install, scanFile)) {
                var (code, output) = await HeroScanProcess.Setup(python, scripts, install, scanFile, stop.Token);
                if (code != 0) { scanNote = "未能写入扫描 GSI 配置：" + output.Trim().Split('\n').LastOrDefault(); return; }
                scanNote = "首次配置完成，请完全退出再启动本机 Dota。";
            }
            if (closing || stop.IsCancellationRequested) return;
            if (!File.Exists(Path.Combine(workspace, ".tmp", "vision-assets", "hud-digits-1080.npz")))
                scanNote += " 缺少守卫数字模板：可读装备，不推测插眼。";
            scan = HeroScanProcess.Start(python, scripts, scanFile, Path.Combine(Path.GetDirectoryName(scanFile)!, "coach-scan.log"), workspace);
        } catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or Win32Exception or ArgumentException) {
            scanNote = "视觉扫描无法启动：" + e.Message;
        } finally { scanBusy = false; UpdateScanButton(); }
    }

    private void UpdateScanButton()
    {
        if (scanButton == null) return;
        bool running = scan is { ExitCode: null };
        scanButton.Content = running ? "停止视觉扫描" : "启动视觉扫描";
        scanButton.IsEnabled = !scanBusy;
        string state = running ? scan!.LastOutput switch {
            "" => "启动中（正在加载识别模型）",
            var line when line.StartsWith("Waiting for the Dota window", StringComparison.Ordinal) => "等待本机 Dota 启动",
            var line when line.StartsWith("Dota window changed", StringComparison.Ordinal) => "Dota 窗口已变化，等待窗口恢复",
            _ => "运行中 · 前台队伍视角下读取对手等级和装备",
        } : scan != null ? $"已退出（{scan.ExitCode}）{scan.LastError}" : scanNote == "已停止" ? "已停止" : scanBusy ? "正在启动或停止…" : "未启动";
        scanState.Text = "视觉扫描：" + state + (scanNote.Length == 0 || scanNote == state ? "" : "\n" + scanNote);
    }
}
