using System.Diagnostics;
using System.IO;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DotaCoach.TwoPc;

internal static partial class Program
{
    /// <summary>Opt-in only: measure the native equipment window over an already running, foreground Dota.
    /// Test the five-row board/PNG downlink on a separate fixture participant; never rebind it to the real game.
    /// Only the client's explicitly labelled, disconnected style preview is shown over Dota. PNGs contain
    /// that fixture art only: no game/desktop pixels, capture-affinity override, login or model.</summary>
    private static async Task PanelTrial(string icons, string output)
    {
        Directory.CreateDirectory(output);
        var surface = DotaCoach.Desktop.GameWindow.Find(out bool minimized) ??
            throw new Exception(minimized ? "Dota is minimised" : "Dota window not found; this opt-in trial does not start it");
        var timeline = new List<object>();
        long started = Environment.TickCount64;
        void Mark(string what, object details)
        {
            timeline.Add(new { at = DateTimeOffset.Now.ToString("HH:mm:ss.fff"), seconds = (Environment.TickCount64 - started) / 1000.0, what, details });
            Console.WriteLine($"{DateTimeOffset.Now:HH:mm:ss} {what} {JsonSerializer.Serialize(details)}");
        }
        Mark("dota", new { surface.Left, surface.Top, surface.Width, surface.Height });
        var game = new GameWindowUi(null, Path.Combine(output, "debug"), Path.Combine(output, "game-video.json"), Path.Combine(output, "game-overlay.json"));
        var overlay = Field<AdviceOverlay>(game, "overlay");
        var panel = Field<OverlayPanel>(game, "overlayPanel");
        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        host.Mailbox.Icons = key => File.Exists(Path.Combine(icons, key + ".png")) ? File.ReadAllBytes(Path.Combine(icons, key + ".png")) : null;
        try {
            var pair = host.Pair(IPAddress.Loopback);
            using (var link = new GameLink(pair)) {
                await link.Connect(default);
                var t = await link.GetTicket(default);
                var me = new OwnState(BoardMatch, BoardHero, "radiant", 600, true, 800, 450, null, null, [], []);
                var phase = new MatchWitness(me.MatchId, me.Team, me.Hero, me.Clock, "playing", false);
                await link.UploadMinuteState(new(t.SessionId, t.Id, 1, "dota2-gsi", surface.Width, surface.Height, me, 0, phase), Environment.TickCount64, default);
                var board = new EnemyBoard([
                    new("npc_dota_hero_razor", 12, ["item_power_treads", "item_black_king_bar", "item_blink", "item_magic_wand", null, null], "item_pogo_stick", 2),
                    new("npc_dota_hero_juggernaut", 14, ["item_phase_boots", "item_bfury", "item_manta", "item_ward_observer", null, null], null, 25),
                    new("npc_dota_hero_lion", 9, ["item_tranquil_boots", "item_blink", null, null, null, null], null, 95),
                    new("npc_dota_hero_shadow_shaman", 10, ["item_arcane_boots", "item_aether_lens", "item_ward_sentry", null, null, null], null, 4),
                    new("npc_dota_hero_pudge", null, null, null, null)], MatchId: me.MatchId, Team: me.Team);
                host.Mailbox.SetEnemies(board);
                var receipt = await link.Enemies(default) ?? throw new Exception("No authorized fixture board");
                var keys = new IconCache().Missing(board, 0);
                foreach (string key in keys) overlay.Icons.Add(key, await link.Icon(key, default), Environment.TickCount64);
                await Wait(() => link.DisplayEnemies(receipt, phase, surface.Width, surface.Height) is { Heroes.Length: 5 } &&
                    keys.All(key => overlay.Icons[key] != null), "all five fixture rows and their supplied PNGs crossed the pinned participant link");
                Mark("fixture-icons", new { count = keys.Length });
                await link.End(default);
                await Wait(() => link.DisplayEnemies(receipt, phase, surface.Width, surface.Height) == null, "disconnect invalidates the fixture board");
            }
            // No application input or focus manipulation. The foreground requirement is part of the trial.
            game.ShowActivated = false; game.WindowState = WindowState.Minimized; game.Show();
            await Wait(() => DotaCoach.Desktop.GameWindow.IsForeground(surface), "keep the existing Dota window in the foreground", 15000);
            Click(game, "恢复默认");
            overlay.Preview(90_000);
            await Wait(() => Member<Window?>(overlay, "equipmentWindow")?.IsVisible == true, "the offline preview occupies the native equipment HWND");
            var layer = Member<Window>(overlay, "equipmentWindow");
            nint hwnd = new WindowInteropHelper(layer).Handle;
            using var self = Process.GetCurrentProcess();
            async Task Shot(string name, bool visible)
            {
                await Wait(() => layer.IsVisible == visible && DotaCoach.Desktop.GameWindow.IsForeground(surface), "preview visibility: " + name);
                layer.UpdateLayout();
                if (!GetWindowRect(hwnd, out var r)) throw new InvalidOperationException("Cannot measure panel HWND");
                var rect = new Rect(r.Left - surface.Left, r.Top - surface.Top, r.Right - r.Left, r.Bottom - r.Top);
                var visual = new DrawingVisual();
                using (var dc = visual.RenderOpen())
                    if (visible && layer.Content is StackPanel rows) dc.DrawRectangle(new VisualBrush(rows), null, rect);
                var bitmap = new RenderTargetBitmap(surface.Width, surface.Height, 96, 96, PixelFormats.Pbgra32); bitmap.Render(visual);
                var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(output, name + ".png")); encoder.Save(file);
                Mark("preview-png", new { name, visible, r.Left, r.Top, rect.Width, rect.Height });
            }
            async Task Hold(string what, bool visible)
            {
                overlay.Preview(25_000); // Longer than the measurement; no redrawing it from the sampling loop.
                await Wait(() => layer.IsVisible == visible, what);
                self.Refresh(); var cpu = self.TotalProcessorTime; long at = Environment.TickCount64; int shown = 0, samples = 0;
                while (Environment.TickCount64 - at < 20_000) {
                    if (!DotaCoach.Desktop.GameWindow.IsForeground(surface)) throw new Exception("Dota left the foreground during the trial");
                    if (layer.IsVisible) shown++; samples++; await Task.Delay(100);
                }
                self.Refresh();
                Mark(what, new { shown, samples, cpuPercentOfMachine = Math.Round((self.TotalProcessorTime - cpu).TotalMilliseconds * 100 /
                    ((Environment.TickCount64 - at) * Environment.ProcessorCount), 2), workingSetMiB = self.WorkingSet64 >> 20 });
                if (shown != (visible ? samples : 0)) throw new Exception("Unexpected panel visibility during " + what);
            }
            await Shot("1-default", true);
            Member<Slider>(panel, "enemySize").Value = 150; Member<Slider>(panel, "enemyOpacity").Value = 100; Member<Slider>(panel, "enemyY").Value = 0;
            await Shot("2-size150-opaque-top", true);
            Click(game, "恢复默认"); await Hold("panel-on", true);
            Member<CheckBox>(panel, "enemies").IsChecked = false; await Shot("3-panel-off", false); await Hold("panel-off", false);
            Member<CheckBox>(panel, "enemies").IsChecked = true; await Hold("panel-on-again", true);
            // The same real client must withdraw even the offline sample as soon as a diagnostic session begins.
            SelectSource(game, "诊断状态"); Field<PasswordBox>(game, "pairing").Password = pair.Encode(); Click(game, "连接");
            await Wait(() => host.Mailbox.Latest() is { HasImage: false, Meta.Source: "diagnostic" } && !layer.IsVisible &&
                Member<EnemyBoard?>(overlay, "enemies") == null, "no fixture board or preview is visible over a paired diagnostic session");
            Click(game, "断开"); long stopped = Environment.TickCount64;
            await Wait(() => host.Mailbox.Snapshot().SessionId == null && !layer.IsVisible, "after disconnect there is no stale panel");
            Mark("disconnected", new { hiddenAfterMs = Environment.TickCount64 - stopped });
        } finally { game.Close(); await Wait(() => !game.IsVisible, "game client closes"); }
        File.WriteAllText(Path.Combine(output, "timeline.json"), JsonSerializer.Serialize(timeline, new JsonSerializerOptions { WriteIndented = true }));
    }

    [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
    [DllImport("user32.dll")] private static extern bool GetWindowRect(nint h, out RECT rect);
    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern bool SetProcessDpiAwarenessContext(nint context);
}
