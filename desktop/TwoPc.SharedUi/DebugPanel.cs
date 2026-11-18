using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace DotaCoach.TwoPc;

internal sealed class DebugPanel : IAsyncDisposable
{
    private readonly string role, root;
    private readonly CheckBox enabled = new() {
        Content = "开启 Debug 记录", Foreground = Brushes.White, Margin = new Thickness(0, 6, 0, 4),
    };
    private readonly TextBlock status = Ui.Text("未开启，不保存任何记录。", 12, Brushes.LightGray);
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool changing;
    private DebugRecorder? current;
    private string? lastDirectory;
    public DebugRecorder? Recorder => current;
    public Task FlushTask { get; private set; } = Task.CompletedTask;
    public StackPanel View { get; } = new();
    public DebugPanel(string role, string? outputRoot = null)
    {
        this.role = role; root = outputRoot ?? DebugRecorder.DefaultRoot;
        View.Children.Add(enabled);
        View.Children.Add(Ui.Text(role == "coach" ?
            "每秒存一帧画面和每次模型输入（可能含游戏聊天），只存本机，最多 2 GiB / 2 小时。" :
            "记录运行状态、提醒回执和每秒小地图明细，不存图片，只存本机。", 11, Brushes.LightGray));
        View.Children.Add(status);
        View.Children.Add(Ui.Button("打开 Debug 目录", (_, _) => {
            if (lastDirectory == null) { status.Text = "先开启一次Debug；不会创建空的录制。"; return; }
            try { Process.Start(new ProcessStartInfo { FileName = lastDirectory, UseShellExecute = true }); }
            catch { status.Text = "无法打开目录：" + lastDirectory; }
        }));
        enabled.Checked += (_, _) => {
            if (changing) return;
            try {
                current = new DebugRecorder(root, role,
                    Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "unknown");
                lastDirectory = current.DirectoryPath;
                status.Text = "正在记录：" + lastDirectory;
            } catch {
                changing = true; enabled.IsChecked = false; changing = false;
                status.Text = "Debug启动失败：检查磁盘和目录权限。客户端继续工作。";
            }
        };
        enabled.Unchecked += (_, _) => { if (!changing) FlushTask = StopAsync(); };
        timer.Tick += (_, _) => {
            if (current is not { } recorder) return;
            var s = recorder.Status;
            status.Text = $"Debug {s.Bytes / 1048576.0:F1} MiB · {s.Images}图 · 丢弃{s.Dropped} · {s.Reason}\n{lastDirectory}";
            if (!s.Recording) {
                changing = true; enabled.IsChecked = false; changing = false;
                FlushTask = StopAsync();
            }
        };
        timer.Start();
    }
    private async Task StopAsync()
    {
        var recorder = current; current = null;
        if (recorder == null) return;
        enabled.IsEnabled = false;
        try {
            await recorder.DisposeAsync();
            var s = recorder.Status;
            status.Text = $"Debug已停止 · {s.Images}图 / {s.Events}事件 · 丢弃{s.Dropped} · {s.Reason}\n{lastDirectory}";
        } catch { status.Text = "Debug写入失败，客户端继续工作；已保存部分内容请检查。"; }
        finally { enabled.IsEnabled = true; }
    }
    public async ValueTask DisposeAsync()
    {
        timer.Stop();
        changing = true; enabled.IsChecked = false; changing = false;
        await StopAsync(); await FlushTask;
    }
}
