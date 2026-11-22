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
    private readonly TextBlock scanState = Ui.Text("", 12, Brushes.LightGray);
    private Button? scanButton;
    private HeroScanProcess? scan;
    private bool scanBusy;
    private string scanNote = "";

    private void AddScanLauncher(StackPanel panel)
    {
        scanFolder.Text = preferences.ScanFolder.Length > 0 ? preferences.ScanFolder : HeroScanProcess.FindWorkspace(AppContext.BaseDirectory) ?? "";
        scanPython.Text = preferences.ScanPython.Length > 0 ? preferences.ScanPython :
            scanFolder.Text.Length > 0 ? HeroScanProcess.FindPython(scanFolder.Text) ?? "" : "";
        panel.Children.Add(Ui.Label(@"本机素材工作目录（含 .tmp\vision-assets）")); panel.Children.Add(scanFolder);
        panel.Children.Add(Ui.Label(@"装有视觉依赖的 Python（如 .tmp\vision-tools\Scripts\python.exe）")); panel.Children.Add(scanPython);
        panel.Children.Add(scanButton = Ui.Button("启动视觉扫描", async (_, _) => await ToggleScan()));
        panel.Children.Add(scanState);
        Closed += async (_, _) => await ReleaseScan();
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
            string? scripts = HeroScanProcess.FindFolder(AppContext.BaseDirectory);
            if (scripts == null || !HeroScanProcess.Supported(scripts)) { scanNote = "缺少随教练端发布的视觉扫描脚本。"; return; }
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
            preferences = preferences with { ScanFolder = workspace, ScanPython = python };
            try { preferences.Save(settingsPath); } catch (IOException) { /* session-only paths */ }
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
        scanState.Text = (running ? "视觉扫描运行中 · " + scan!.LastOutput :
            scan != null ? $"扫描已退出（{scan.ExitCode}）{scan.LastError}" : "视觉扫描未启动") +
            (scanNote.Length == 0 ? "" : "\n" + scanNote);
    }
}
