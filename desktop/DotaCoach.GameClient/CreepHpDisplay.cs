using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

/// <summary>
/// Creep HP numbers over Dota, entirely on the game machine: every captured frame of the Dota window is
/// scanned on the GPU (no picture read-back) and the numbers are drawn by a GPU overlay on the same device.
/// Which unit owns a bar (melee 550, ranged 300, siege 935 ...) comes from the player's own GSI minimap.
/// Separate from the coach link: nothing is encoded or sent. Its own thread; one frame in flight.
/// </summary>
internal sealed class CreepHpDisplay : IAsyncDisposable
{
    /// <summary>
    /// Rates over the last second: frames scanned and shown, scan round trip, bars and how many have a known unit;
    /// the GPU's 3D load and whether the display is held at <see cref="GpuPacing.LimitedFps"/> because of it.
    /// </summary>
    public sealed record Stats(string Status, double Fps, double ScanMs, int Bars, int Known, double? GpuLoad, bool Limited);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Action<Stats> report;
    private readonly Func<(SpectatorSnapshot Snapshot, int AgeMs)?> minimap;
    private readonly bool excludeFromCapture;
    private readonly GpuPacing pacing = new();
    private double? gpuLoad;

    /// <param name="minimap">The player's own GSI units; without it bars show a percentage.</param>
    /// <param name="excludeFromCapture">Like the coach subtitles, keep the numbers out of recordings and screen captures.</param>
    public CreepHpDisplay(Action<Stats> report, Func<(SpectatorSnapshot Snapshot, int AgeMs)?> minimap, bool excludeFromCapture = true)
    {
        this.report = report; this.minimap = minimap; this.excludeFromCapture = excludeFromCapture;
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run()
    {
        try {
            using var gpu = new GpuDevice();
            // Realtime GPU scheduling: a game that keeps the GPU busy would otherwise hold this
            // 0.1 ms scan back by ~35 ms (Windows favours the foreground process). No admin needed.
            gpu.RaiseGpuPriority();
            using var load = new GpuLoad(gpu.AdapterLuid());
            using var sampler = new Timer(_ => {
                gpuLoad = load.Sample();
                pacing.Update(gpuLoad);
            }, null, 1000, 1000);
            using var timer = new PreciseTimer();
            GpuOverlay? overlay = null;
            int overlayHeight = 0;
            try {
                while (!stop.IsCancellationRequested) {
                    overlay?.Pump();
                    var game = GameWindow.Find(out bool minimized);
                    var offset = game == null ? null : GameWindow.ClientOffset(game);
                    if (game == null || offset == null) {
                        overlay?.Hide();
                        report(new(minimized ? "Dota 已最小化，不显示。" : "等待 Dota 窗口…", 0, 0, 0, 0, gpuLoad, false));
                        Pause(250); continue;
                    }
                    if (overlay == null || overlayHeight != game.Height) {
                        overlay?.Dispose(); overlay = null;
                        overlay = new GpuOverlay(gpu, new GlyphAtlas(Math.Max(12, (int)Math.Round(game.Height * .0125))), excludeFromCapture);
                        overlayHeight = game.Height;
                    }
                    Show(gpu, overlay, game, offset.Value, timer);
                }
            } finally { overlay?.Dispose(); }
        } catch (Exception ex) {
            report(new($"小兵血量显示已停止：{ex.GetType().Name}（{ex.Message}）", 0, 0, 0, 0, gpuLoad, false));
        }
    }

    /// <summary>One capture session of an unchanged Dota window; returns when it moves, resizes or closes.</summary>
    private void Show(GpuDevice gpu, GpuOverlay overlay, GameSurface game, (int Left, int Top) offset, PreciseTimer timer)
    {
        using var capture = new WindowCapture(gpu, game.Handle);
        using var scanner = new GpuCreepScanner(gpu);
        var client = new Rect32 { Left = offset.Left, Top = offset.Top, Right = offset.Left + game.Width, Bottom = offset.Top + game.Height };
        var matcher = new CreepUnitMatcher();
        var tracks = new CreepTracks();
        var labels = new List<OverlayLabel>();
        long checkAt = 0, rateAt = Environment.TickCount64, due = 0;
        int frames = 0, bars = 0, known = 0;
        double scanTotal = 0;
        while (!stop.IsCancellationRequested) {
            overlay.Pump();
            long now = Environment.TickCount64;
            if (now >= checkAt) {
                if (GameWindow.Find(out _) != game) return;
                checkAt = now + 250;
            }
            if (now - rateAt >= 1000) {
                double seconds = (now - rateAt) / 1000d;
                string status = !capture.Unthrottled ? "正在显示；此 Windows 版本采集上限约 60 fps" :
                    minimap() == null ? "正在显示百分比：没有收到自身 GSI，无法区分近战/远程/攻城兵" : "正在显示（本机 GPU 识别与绘制，不传输画面）";
                report(new(status, frames / seconds, frames > 0 ? scanTotal / frames : 0, bars, known, gpuLoad, pacing.Limited));
                rateAt = now; frames = 0; scanTotal = 0;
            }
            using var frame = capture.Latest();
            if (GetForegroundWindow() != game.Handle) {
                // Alt+Tab: nothing over other applications; the capture keeps running so coming back is instant.
                overlay.Hide(); labels.Clear(); Pause(20); continue;
            }
            bool limited = pacing.Limited;
            long started = Stopwatch.GetTimestamp();
            if (frame == null || limited && started < due) { timer.Wait(5_000); continue; } // 0.5 ms
            if (frame.ContentWidth < client.Right || frame.ContentHeight < client.Bottom) return;
            // Limited: the first frame after each due time, which keeps the average at the limited rate.
            due = limited ? Math.Max(due + Stopwatch.Frequency / GpuPacing.LimitedFps, started) : started;
            // One GPU submission per frame: this frame's scan goes out with the Present of the labels found in the
            // previous one, so numbers appear a frame later. Uncapped at 60 Hz a separate Present cost the game 5.5% fps, this 2.1%.
            scanner.Submit(frame, client);
            overlay.Place(game.Left, game.Top, game.Width, game.Height);
            overlay.Draw(labels);
            var found = scanner.Collect();
            scanTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var units = minimap();
            var matches = units is { } u && u.Snapshot.OwnTeam is int team
                ? matcher.Match(found, u.Snapshot.Minimap, team, game.Width, game.Height) : new MinimapUnit?[found.Count];
            var names = tracks.Update(found, matches, now);
            labels.Clear(); known = 0;
            for (int i = 0; i < found.Count; i++) {
                var bar = found[i];
                if (bar.Covered && bar.Fill >= bar.Inner) continue;
                int? maximum = names[i] is { } name ? CreepKinds.MaxHp(name, units?.Snapshot.Clock) : null;
                if (maximum != null) known++;
                string text = maximum is int max ? bar.Hp(max).ToString() : $"{Math.Round(bar.Fraction * 100)}%";
                labels.Add(new(bar.CenterX, bar.Y - 1, text, bar.Ally ? 0xA8FF8Cu : 0xFFFFFFu));
            }
            frames++; bars = found.Count;
        }
    }

    private void Pause(int ms) => stop.Token.WaitHandle.WaitOne(ms);

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; } finally { stop.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
