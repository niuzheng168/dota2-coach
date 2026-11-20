using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using DotaCoach.TwoPc;

internal static partial class Program
{
    /// <summary>
    /// Opt-in (--panel-trial icons-dir out-dir): the enemy panel on the game machine's RUNNING Dota window. A loopback
    /// coach publishes a sample panel with the given icons (files named &lt;dota name&gt;.png, as the coach machine's scanner
    /// writes them); the real game client window connects in its diagnostic mode (Dota's own picture is never sent)
    /// and draws the panel over Dota. For this check only, the overlay is made capturable so that screenshots
    /// show it. Then: settings changes, the panel switched off and back, CPU while it stands still, disconnection.
    /// While it runs, Dota must stay in the foreground; the timeline lets FPS be compared with the panel on and off.
    /// </summary>
    private static async Task PanelTrial(string icons, string output)
    {
        Directory.CreateDirectory(output);
        var surface = DotaCoach.Desktop.GameWindow.Find(out bool minimized) ??
            throw new Exception(minimized ? "Dota is minimised" : "Dota window not found");
        var timeline = new List<object>();
        long started = Environment.TickCount64;
        void Mark(string what, object? details = null)
        {
            timeline.Add(new { at = DateTimeOffset.Now.ToString("HH:mm:ss.fff"), seconds = (Environment.TickCount64 - started) / 1000.0, what, details });
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {what} {(details == null ? "" : JsonSerializer.Serialize(details))}");
        }
        Mark("dota", new { surface.Left, surface.Top, surface.Width, surface.Height });

        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        host.Mailbox.VideoJpegEncoder = Ui.ExtractJpeg;
        host.Mailbox.Icons = key => File.Exists(Path.Combine(icons, key + ".png")) ? File.ReadAllBytes(Path.Combine(icons, key + ".png")) : null;
        // Ages grow like a real panel's: two rows recently read, two getting old, one enemy never seen.
        EnemyBoard Board(double t) => new([
            new("npc_dota_hero_razor", 12, ["item_power_treads", "item_black_king_bar", "item_blink", "item_magic_wand", null, null], "item_pogo_stick", 2),
            new("npc_dota_hero_juggernaut", 14, ["item_phase_boots", "item_bfury", "item_manta", "item_ward_observer", null, null], null, 25 + t),
            new("npc_dota_hero_lion", 9, ["item_tranquil_boots", "item_blink", null, null, null, null], null, 95 + t),
            new("npc_dota_hero_shadow_shaman", 10, ["item_arcane_boots", "item_aether_lens", "item_ward_sentry", null, null, null], null, 4),
            new("npc_dota_hero_pudge", null, null, null, null)]);
        int iconCount = new IconCache().Missing(Board(0), 0).Length;
        using var publish = new CancellationTokenSource();
        var publisher = Task.Run(async () => {
            while (!publish.IsCancellationRequested) {
                host.Mailbox.SetEnemies(Board((Environment.TickCount64 - started) / 1000.0));
                try { await Task.Delay(1000, publish.Token); } catch (OperationCanceledException) { }
            }
        });

        var game = new GameWindowUi(null, Path.Combine(output, "debug"), Path.Combine(output, "game-video.json"), Path.Combine(output, "game-overlay.json"));
        game.Show(); game.WindowState = WindowState.Minimized;
        DebugSwitch(game).IsChecked = true; // overlay positioning errors and reminders land in out-dir/debug
        Field<PasswordBox>(game, "pairing").Password = host.Pair(IPAddress.Loopback).Encode();
        Field<ComboBox>(game, "source").SelectedIndex = 1;
        Click(game, "连接");
        var overlay = Field<AdviceOverlay>(game, "overlay");
        var panel = Field<OverlayPanel>(game, "overlayPanel");
        await Wait(() => Member<EnemyBoard?>(overlay, "enemies") is { Heroes.Length: 5 } && overlay.Icons.Count == iconCount,
            "the game client receives the panel and its icons over the paired link", 15000);
        Mark("icons", new { overlay.Icons.Count });
        // The panel has a window of its own, sized to the panel.
        nint handle = overlay.Panel.Handle;
        SetWindowDisplayAffinity(handle, 0); // trial only: screenshots may show the panel

        Process self = Process.GetCurrentProcess();
        async Task<string> Shot(string name)
        {
            Front(surface.Handle);
            await Task.Delay(1500);
            string file = Path.Combine(output, name + ".png");
            Capture(surface, file);
            GetWindowRect(handle, out var rect);
            var view = overlay.Panel.View.Children.OfType<StackPanel>().FirstOrDefault();
            Mark("screenshot", new { file = Path.GetFileName(file), foreground = DotaCoach.Desktop.GameWindow.IsForeground(surface),
                panelVisible = overlay.Panel.IsVisible, panelWindow = new { rect.Left, rect.Top, Width = rect.Right - rect.Left, Height = rect.Bottom - rect.Top },
                panel = view == null ? null : new { view.ActualWidth, view.ActualHeight, rows = view.Children.Count } });
            return file;
        }
        async Task Hold(string what, int seconds)
        {
            Front(surface.Handle);
            var cpu = self.TotalProcessorTime; long at = Environment.TickCount64;
            Mark(what + "-start");
            int shown = 0, samples = 0; // 10 checks a second: a panel that blinks would show up here
            for (; Environment.TickCount64 - at < seconds * 1000; samples++) { shown += overlay.Panel.IsVisible ? 1 : 0; await Task.Delay(100); }
            self.Refresh();
            double percent = (self.TotalProcessorTime - cpu).TotalMilliseconds / (Environment.TickCount64 - at) / Environment.ProcessorCount * 100;
            Mark(what + "-end", new { panelVisibleSamples = $"{shown}/{samples}", dotaForeground = DotaCoach.Desktop.GameWindow.IsForeground(surface),
                cpuPercentOfMachine = Math.Round(percent, 2), workingSetMiB = self.WorkingSet64 >> 20 });
        }

        await Shot("1-default");
        Member<Slider>(panel, "enemySize").Value = 150; Member<Slider>(panel, "enemyOpacity").Value = 100; Member<Slider>(panel, "enemyY").Value = 0;
        await Shot("2-size150-opaque-top");
        Click(game, "恢复默认");
        await Hold("panel-on", 20);
        Member<CheckBox>(panel, "enemies").IsChecked = false;
        await Shot("3-panel-off");
        await Hold("panel-off", 20);
        Member<CheckBox>(panel, "enemies").IsChecked = true;
        await Hold("panel-on-again", 20);
        Click(game, "断开");
        long stopped = Environment.TickCount64;
        await Wait(() => !overlay.Panel.IsVisible, "after disconnecting the panel disappears", 8000);
        Mark("disconnected", new { hiddenAfterMs = Environment.TickCount64 - stopped });
        publish.Cancel(); await publisher;
        game.Close();
        await Wait(() => !game.IsVisible, "game client closes", 10000);
        File.WriteAllText(Path.Combine(output, "timeline.json"), JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void Front(nint window)
    {
        uint thread = GetWindowThreadProcessId(GetForegroundWindow(), out _), own = GetCurrentThreadId();
        AttachThreadInput(own, thread, true);
        try { BringWindowToTop(window); SetForegroundWindow(window); }
        finally { AttachThreadInput(own, thread, false); }
    }
    /// <summary>The screen under the Dota client area, layered windows included (CAPTUREBLT).</summary>
    private static void Capture(DotaCoach.Desktop.GameSurface game, string file)
    {
        nint screen = GetDC(0), memory = CreateCompatibleDC(screen), bitmap = CreateCompatibleBitmap(screen, game.Width, game.Height);
        try {
            nint old = SelectObject(memory, bitmap);
            BitBlt(memory, 0, 0, game.Width, game.Height, screen, game.Left, game.Top, 0x00CC0020 | 0x40000000);
            SelectObject(memory, old);
            var image = Imaging.CreateBitmapSourceFromHBitmap(bitmap, 0, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
            using var stream = File.Create(file); encoder.Save(stream);
        } finally { DeleteObject(bitmap); DeleteDC(memory); ReleaseDC(0, screen); }
    }
    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint h, uint affinity);
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(nint h, out uint process);
    [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] private static extern bool AttachThreadInput(uint attach, uint to, bool on);
    [DllImport("user32.dll")] private static extern bool BringWindowToTop(nint h);
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(nint h);
    [DllImport("user32.dll")] private static extern nint GetDC(nint h);
    [DllImport("user32.dll")] private static extern int ReleaseDC(nint h, nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleDC(nint dc);
    [DllImport("gdi32.dll")] private static extern nint CreateCompatibleBitmap(nint dc, int width, int height);
    [DllImport("gdi32.dll")] private static extern nint SelectObject(nint dc, nint item);
    [DllImport("gdi32.dll")] private static extern bool BitBlt(nint dc, int x, int y, int width, int height, nint source, int sx, int sy, uint rop);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(nint item);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(nint dc);
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
}
