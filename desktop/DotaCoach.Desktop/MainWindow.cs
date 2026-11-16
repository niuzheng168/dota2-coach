using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using Forms = System.Windows.Forms;

namespace DotaCoach.Desktop;

/// <summary>Trusted, packaged renderer. Never navigate a privileged bridge to a remote website.</summary>
public sealed class MainWindow : Window
{
    private readonly WebView2 browser = new();
    private readonly SubtitleWindow overlay = new();
    private readonly JungleOverlayWindow jungleOverlay = new();
    private LocalHudVision? hudVision;
    private JsonElement? tacticsState;
    private readonly DesktopSession session;
    private readonly SemaphoreSlim commands = new(1, 1);
    private readonly string directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach");
    private Forms.NotifyIcon? tray;
    private JsonElement overlayOptions;
    private bool closed, localReady;
    private int rendererGeneration;
    private readonly System.Collections.Generic.List<int> hotkeys = new();
    private readonly string? initialLink;

    public MainWindow(string? address)
    {
        string? Read(string name) { try { return File.ReadAllText(Path.Combine(directory, name)); } catch { return null; } }
        var serviceAddress = DesktopPolicy.InitialServerAddress(address, Read("connected-server.txt"), Read("server.txt"));
        try { _ = DesktopPolicy.ServerUri(serviceAddress); }
        catch (ArgumentException) { serviceAddress = DesktopPolicy.DefaultServerAddress; }
        initialLink = address;
        session = new DesktopSession(serviceAddress, work => {
            if (!closed) Dispatcher.BeginInvoke(work);
        }, Post);
        try { overlayOptions = JsonSerializer.Deserialize<JsonElement>(Read("overlay.json") ?? "{}"); }
        catch { overlayOptions = JsonSerializer.SerializeToElement(new { }); }
        overlayOptions = OverlayPreferences.Normalize(overlayOptions);
        Title = $"Dota Coach · v{session.Version}";
        Width = 1440; Height = 960; MinWidth = 1000; MinHeight = 700;
        Background = new SolidColorBrush(Color.FromRgb(14, 17, 21));
        browser.DefaultBackgroundColor = System.Drawing.Color.FromArgb(14, 17, 21);
        Content = browser;
        session.Caption += turn => overlay.Caption(turn.speaker, turn.source, turn.target);
        session.ClearOverlay += () => overlay.Clear();
        session.TacticsState += value => {
            tacticsState = value.Clone(); jungleOverlay.Accept(value); hudVision?.Accept(value);
            if (!value.TryGetProperty("enabled", out var enabled) || enabled.ValueKind != JsonValueKind.True) StopVision();
        };
        session.TacticsStopped += () => { tacticsState = null; StopVision(); jungleOverlay.Clear(); };
        SourceInitialized += (_, _) => {
            var handle = new WindowInteropHelper(this).Handle;
            int dark = 1; DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int));
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
            for (int id = 1; id <= 4; id++)
                if (RegisterHotKey(handle, id, 0x0002 | 0x0004 | 0x4000, id == 1 ? 0x48u : id == 2 ? 0x4Cu : id == 3 ? 0x4Du : 0x4Au)) hotkeys.Add(id);
        };
        Loaded += async (_, _) => {
            try
            {
                Directory.CreateDirectory(directory);
                var environment = await CoreWebView2Environment.CreateAsync(null, Path.Combine(directory, "DesktopWebView"));
                await browser.EnsureCoreWebView2Async(environment);
                if (closed) return;
                var core = browser.CoreWebView2;
                core.Settings.AreDevToolsEnabled = false;
                core.Settings.IsStatusBarEnabled = false;
                core.Settings.AreDefaultContextMenusEnabled = false;
                core.Settings.AreBrowserAcceleratorKeysEnabled = false;
                core.SetVirtualHostNameToFolderMapping("dota-coach.local", Path.Combine(AppContext.BaseDirectory, "Ui"),
                    CoreWebView2HostResourceAccessKind.DenyCors);
                core.NavigationStarting += (_, e) => {
                    if (!DesktopPolicy.IsLocalDocument(e.Uri)) { e.Cancel = true; return; }
                    localReady = false; rendererGeneration++;
                    // Expected local reloads rehydrate from the native session; they do not own audio lifetime.
                };
                core.NewWindowRequested += (_, e) => e.Handled = true;
                core.PermissionRequested += (_, e) => e.State = CoreWebView2PermissionState.Deny;
                core.DownloadStarting += (_, e) => e.Cancel = true;
                core.WebMessageReceived += OnMessage;
                core.ProcessFailed += (_, _) => {
                    localReady = false; rendererGeneration++; session.RendererFailed();
                    var retry = new Button { Content = "界面发生异常，采集已暂停。点击重新加载", Padding = new Thickness(24) };
                    retry.Click += (_, _) => { Content = browser; core.Navigate(DesktopPolicy.LocalAppAddress); };
                    Content = retry;
                };
                core.Navigate(DesktopPolicy.LocalAppAddress);
                overlay.Show(); overlay.Settings(overlayOptions); ConfigureTray();
            }
            catch (Exception)
            {
                Content = new TextBlock { Text = "无法初始化桌面界面。\n请确认安装包已完整解压，并安装 Microsoft Edge WebView2 Evergreen Runtime。",
                    Foreground = Brushes.White, Margin = new Thickness(40), TextWrapping = TextWrapping.Wrap };
            }
        };
        Closed += (_, _) => {
            closed = true; localReady = false; StopVision(); session.Dispose(); overlay.Close(); jungleOverlay.Close(); tray?.Dispose();
            var hwnd = new WindowInteropHelper(this).Handle;
            foreach (int id in hotkeys) UnregisterHotKey(hwnd, id);
            browser.Dispose();
        };
    }

    private void Post(object message)
    {
        if (closed || !localReady || !DesktopPolicy.IsLocalDocument(browser.Source?.AbsoluteUri)) return;
        browser.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(message));
    }

    private async void OnMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (closed || !DesktopPolicy.IsLocalDocument(e.Source) || !DesktopPolicy.IsLocalDocument(browser.Source?.AbsoluteUri) ||
            e.WebMessageAsJson.Length > 65536) return;
        string id = "", method = "";
        int generation = rendererGeneration;
        bool held = false;
        try
        {
            using var doc = JsonDocument.Parse(e.WebMessageAsJson);
            var request = doc.RootElement;
            id = NativeVoiceChannel.Text(request, "id"); method = NativeVoiceChannel.Text(request, "method");
            if (!Guid.TryParse(id, out _)) return;
            JsonElement data = request.TryGetProperty("data", out var value) && value.ValueKind == JsonValueKind.Object
                ? value.Clone() : JsonSerializer.SerializeToElement(new { });
            if (method == "bootstrap") localReady = true;
            // Stop controls interrupt startup/network waits, rather than queueing behind them.
            if (method == "mic.stop") session.StopMic();
            else if (method == "listener.stop") session.StopListener();
            else if (method == "tactics.vision.stop") StopVision();
            else if (method == "tactics.overlay.stop") jungleOverlay.Stop();
            else { await commands.WaitAsync(); held = true; }
            if (closed || rendererGeneration != generation) return;
            object? result;
            switch (method)
            {
                case "bootstrap":
                    result = new { state = session.Snapshot(), overlay = overlayOptions,
                        monitors = Array.ConvertAll(Forms.Screen.AllScreens, s => s.DeviceName),
                        shortcuts = new { overlay = hotkeys.Contains(1), listener = hotkeys.Contains(2), microphone = hotkeys.Contains(3), jungle = hotkeys.Contains(4) },
                        initialLink };
                    break;
                case "listener.start":
                    string name = "所选程序";
                    int pid = data.GetProperty("pid").GetInt32();
                    foreach (var p in ProcessCatalog.List()) if (p.pid == pid) { name = p.name; break; }
                    if (MessageBox.Show(this, $"监听 {name} 及其子进程？\n\n声音将上传到当前服务用于英语识别和翻译；不保存录音、不广播给房间其他人。\n请确认你已告知对方。",
                        "确认外部声音采集", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
                        throw new InvalidOperationException("已取消监听");
                    if (rendererGeneration != generation || closed) return;
                    result = await session.Command(method, data); break;
                case "overlay.settings":
                    overlayOptions = OverlayPreferences.Normalize(data); overlay.Settings(overlayOptions);
                    File.WriteAllText(Path.Combine(directory, "overlay.json"), overlayOptions.GetRawText());
                    result = overlayOptions; break;
                case "overlay.preview":
                    overlay.Caption("字幕预览", "We should smoke and take Roshan.", "我们开雾去打肉山。");
                    result = null; break;
                case "tactics.overlay.configure":
                case "tactics.overlay.preview":
                    jungleOverlay.Configure(data, (int)MinimapCalibration.Number(data, "clientWidth"), (int)MinimapCalibration.Number(data, "clientHeight"), method == "tactics.overlay.preview");
                    result = null; break;
                case "tactics.overlay.stop": jungleOverlay.Stop(); result = null; break;
                case "tactics.vision.start":
                    if (tacticsState is not JsonElement ts || !ts.GetProperty("enabled").GetBoolean())
                        throw new InvalidOperationException("请先连接房间并开启刷野规划");
                    StopVision();
                    var watcher = new LocalHudVision(NativeVoiceChannel.Text(data, "model"));
                    hudVision = watcher; watcher.Accept(ts);
                    watcher.Status += text => { if (hudVision == watcher && !closed) Post(new { type = "tactics.vision.status", text }); };
                    watcher.Observation += async observation => {
                        if (hudVision != watcher || closed) return;
                        try { await session.Command("tactics.observation", observation); }
                        catch (Exception error) { if (!closed) Post(new { type = "tactics.vision.status", text = error.Message }); }
                    };
                    _ = watcher.Run();
                    result = null; break;
                case "tactics.vision.stop": StopVision(); result = null; break;
                case "window.tray": Hide(); result = null; break;
                case "room.copyInvite":
                    var invite = (string?)await session.Command("room.invite", data);
                    if (invite != null) Clipboard.SetText(invite);
                    result = null; break;
                case "gsi.download":
                    var cfg = (string?)await session.Command("gsi.config", data);
                    var save = new Microsoft.Win32.SaveFileDialog { FileName = "gamestate_integration_dota2_coach.cfg", Filter = "GSI 配置|*.cfg" };
                    if (save.ShowDialog(this) == true) await File.WriteAllTextAsync(save.FileName, cfg);
                    result = null; break;
                case "diagnostics.export":
                    var diagnostics = await session.Command("diagnostics", data);
                    var file = new Microsoft.Win32.SaveFileDialog { FileName = "dota-coach-diagnostics.json", Filter = "诊断信息|*.json" };
                    if (file.ShowDialog(this) == true) await File.WriteAllTextAsync(file.FileName, JsonSerializer.Serialize(diagnostics, new JsonSerializerOptions { WriteIndented = true }));
                    result = null; break;
                case "web.open":
                    Process.Start(new ProcessStartInfo(session.Server) { UseShellExecute = true });
                    result = null; break;
                case "server.set":
                    var uri = DesktopPolicy.ServerUri(NativeVoiceChannel.Text(data, "address"));
                    if (MessageBox.Show(this, $"将服务切换为：\n{uri.GetLeftPart(UriPartial.Authority)}\n\n只连接你信任的服务。", "切换服务",
                        MessageBoxButton.YesNo) != MessageBoxResult.Yes) throw new InvalidOperationException("已取消切换");
                    result = await session.Command(method, data);
                    File.WriteAllText(Path.Combine(directory, "connected-server.txt"), session.Server);
                    break;
                default: result = await session.Command(method, data); break;
            }
            if (rendererGeneration == generation) Post(new { type = "response", id, ok = true, result });
        }
        catch (Exception error)
        {
            if (error is ServiceException { Status: 401 }) await session.AuthenticationLost();
            string message = error is ServiceException or InvalidOperationException or ArgumentException
                ? error.Message : error is OperationCanceledException ? "操作已取消或连接超时，请重试"
                : "操作失败，请检查网络、服务状态或音频设备";
            if (rendererGeneration == generation) Post(new { type = "response", id, ok = false, error = message });
        }
        finally { if (held) commands.Release(); }
    }

    private void ConfigureTray()
    {
        var menu = new Forms.ContextMenuStrip();
        menu.Items.Add("打开 Dota Coach", null, (_, _) => RestoreWindow());
        menu.Items.Add("暂停所有采集", null, (_, _) => { session.StopMic(); session.StopListener(); StopVision(); jungleOverlay.Stop(); });
        menu.Items.Add("显示 / 隐藏字幕", null, (_, _) => ToggleOverlay());
        menu.Items.Add("退出（停止采集）", null, (_, _) => Close());
        tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Application, Text = "Dota Coach · 右键可暂停全部采集",
            ContextMenuStrip = menu, Visible = true };
        tray.DoubleClick += (_, _) => RestoreWindow();
    }
    private void RestoreWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void StopVision()
    {
        var previous = hudVision; hudVision = null; previous?.Dispose();
        if (!closed) Post(new { type = "tactics.vision.status", text = "本地视觉已停止；没有截图上传或保存" });
    }
    private void ToggleOverlay()
    {
        var options = System.Text.Json.Nodes.JsonNode.Parse(overlayOptions.GetRawText())!.AsObject();
        options["enabled"] = !(options["enabled"]?.GetValue<bool>() ?? true);
        overlayOptions = JsonSerializer.SerializeToElement(options); overlay.Settings(overlayOptions);
        try { File.WriteAllText(Path.Combine(directory, "overlay.json"), overlayOptions.GetRawText()); } catch { }
        Post(new { type = "overlay.changed", settings = overlayOptions });
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wparam, IntPtr lparam, ref bool handled)
    {
        if (message == 0x0312)
        {
            handled = true;
            switch (wparam.ToInt32()) {
                case 1: ToggleOverlay(); break;
                case 2: session.StopListener("快捷键已暂停监听"); break;
                case 3: session.StopMic("快捷键已关闭麦克风"); break;
                case 4: _ = RequestJungle(); break;
            }
        }
        return IntPtr.Zero;
    }
    private async Task RequestJungle()
    {
        try { await session.Command("tactics.request", JsonSerializer.SerializeToElement(new { })); }
        catch (Exception e) { Post(new { type = "notice", message = e.Message }); }
    }
    [DllImport("user32.dll")] private static extern bool RegisterHotKey(IntPtr hwnd, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] private static extern bool UnregisterHotKey(IntPtr hwnd, int id);
    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);
}
