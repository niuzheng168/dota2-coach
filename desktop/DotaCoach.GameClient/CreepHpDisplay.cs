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
/// Only creeps below half health get a number; it turns red when one attack of the player's hero surely kills
/// (last hit, or deny for allies), by the hero's attack estimated from its own GSI. Creeps a tower can shoot get
/// the tower last-hit plan with the fewest prep hits.
/// Separate from the coach link: nothing is encoded or sent. Its own thread; one frame in flight.
/// </summary>
internal sealed class CreepHpDisplay : IAsyncDisposable
{
    /// <summary>
    /// Rates over the last second: frames scanned and shown, scan round trip, bars, how many have a known unit and how many
    /// were below half health and labelled; the GPU's 3D load and whether the display is held at <see cref="GpuPacing.LimitedFps"/>
    /// because of it; and the attack the red numbers are judged by, or why there are none.
    /// </summary>
    public sealed record Stats(string Status, double Fps, double ScanMs, int Bars, int Known, int Shown, double? GpuLoad, bool Limited, string Kill);
    /// <summary>
    /// A number turning red, for the last-hit lab's closed loop (scripts/lasthit/lab.ts --loop): by which rule ("ahead" with
    /// the timing, "frame" without), the unit, where GSI has it and the health shown; <see cref="At"/> is the frame's capture time.
    /// <see cref="Drops"/>: the track's drops the prediction saw, "seconds before:pixels".
    /// </summary>
    public readonly record struct Cue(double At, string Rule, string Unit, double X, double Y, int Hp, string Drops);
    private readonly CancellationTokenSource stop = new();
    private readonly Task worker;
    private readonly Action<Stats> report;
    private readonly Func<(SpectatorSnapshot Snapshot, int AgeMs)?> minimap;
    private readonly Func<HeroLoadout?> hero;
    private readonly bool excludeFromCapture;
    private readonly Func<LastHitTiming?>? timing;
    private readonly Action<Cue>? cues;
    private readonly LabFrames? pictures;
    private readonly GpuPacing pacing = new();
    private readonly HeroAttackTable? attacks = HeroAttackTable.Bundled();
    private HeroLoadout? attackFor;
    private HeroAttack? attack;
    private SpectatorSnapshot? laneFor;
    private LaneView lane = LaneView.Empty;
    private string kill = "";
    private double? gpuLoad;

    /// <param name="minimap">The player's own GSI units; without it bars show a percentage.</param>
    /// <param name="hero">The player's own hero from GSI; without it no number turns red.</param>
    /// <param name="excludeFromCapture">Like the coach subtitles, keep the numbers out of recordings and screen captures.</param>
    /// <param name="timing">Read every frame: red numbers look ahead at the hits other units are about to land (null: only this frame's health).</param>
    /// <param name="cues">Lab only: told whenever a number turns red, by either rule.</param>
    /// <param name="frames">Lab only: saves pictures of the Dota window, also while it is behind other windows.</param>
    public CreepHpDisplay(Action<Stats> report, Func<(SpectatorSnapshot Snapshot, int AgeMs)?> minimap, Func<HeroLoadout?> hero, bool excludeFromCapture = true,
        Func<LastHitTiming?>? timing = null, Action<Cue>? cues = null, LabFrames? frames = null)
    {
        this.report = report; this.minimap = minimap; this.hero = hero; this.excludeFromCapture = excludeFromCapture; this.timing = timing; this.cues = cues;
        pictures = frames;
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    /// <summary>The own hero's attack, towers and flagbearers, worked out again only when GSI brings a new packet.</summary>
    private LaneView Lane(SpectatorSnapshot? snapshot)
    {
        var loadout = hero();
        if (ReferenceEquals(loadout, attackFor) && ReferenceEquals(snapshot, laneFor) && kill.Length > 0) return lane;
        if (!ReferenceEquals(loadout, attackFor) || kill.Length == 0) {
            attackFor = loadout; attack = null;
            if (attacks == null) kill = "缺少 data/dota2-hero-attack.json，不标红";
            else if (loadout == null) kill = "没有收到自身英雄的 GSI，不标红";
            else if ((attack = attacks.Attack(loadout, out string? why)) is not { } a) kill = $"{why ?? "算不出攻击力"}，不标红";
            else kill = Summary(a, attacks, snapshot?.Clock);
        }
        laneFor = snapshot;
        return lane = LaneView.From(snapshot, attack, attacks, loadout?.Hero);
    }

    /// <summary>The red-number thresholds, and the tower plans for full-health creeps under a tier-1 tower (flagbearer near).</summary>
    private static string Summary(HeroAttack a, HeroAttackTable table, double? clock)
    {
        static string Sure(HeroAttack a, string unit) => Math.Floor(a.Against(unit)!.Value.Least).ToString();
        string text = $"红字＝一刀必死：近战兵 ≤{Sure(a, "npc_dota_creep_goodguys_melee")} · 远程兵 ≤{Sure(a, "npc_dota_creep_goodguys_ranged")} · " +
            $"攻城车 ≤{Sure(a, "npc_dota_goodguys_siege")}（已算护甲；攻击力 {a.Min}–{a.Max}{(a.Quell > 0 ? $"，补刀斧 +{a.Quell}" : "")}，" +
            $"每 {a.Interval:F2} 秒一刀）";
        if (table.Tower("npc_dota_goodguys_tower1_mid") is not { } t) return text;
        string Plan(string unit)
        {
            int max = CreepKinds.MaxHp(unit, clock)!.Value;
            double share = HeroAttack.ArmorMultiplier(CreepKinds.Armor(unit)!.Value);
            return TowerPlans.Fewest(max, max / 77.3, CreepKinds.Regen(unit) + CreepKinds.FlagbearerRegen, a.Against(unit)!.Value, a.Interval, a.Delay,
                (t.DamageMin * share, t.DamageMax * share), t.Interval, preppable: true) is { } p ? p.Preps == 0 ? "不用垫" : $"垫 {p.Preps} 刀" : "排不出";
        }
        return text + $"\n黄字＝塔下先垫几刀，之后等塔打到变红再补。一塔下满血：远程兵 {Plan("npc_dota_creep_goodguys_ranged")} · " +
            $"近战兵 {Plan("npc_dota_creep_goodguys_melee")}（塔每 {t.Interval:F2} 秒一下）";
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
                        report(new(minimized ? "Dota 已最小化，不显示。" : "等待 Dota 窗口…", 0, 0, 0, 0, 0, gpuLoad, false, ""));
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
            report(new($"小兵血量显示已停止：{ex.GetType().Name}（{ex.Message}）", 0, 0, 0, 0, 0, gpuLoad, false, ""));
        }
    }

    /// <summary>One capture session of an unchanged Dota window; returns when it moves, resizes or closes.</summary>
    private void Show(GpuDevice gpu, GpuOverlay overlay, GameSurface game, (int Left, int Top) offset, PreciseTimer timer)
    {
        using var capture = new WindowCapture(gpu, game.Handle);
        using var scanner = new GpuCreepScanner(gpu);
        using var reader = pictures == null ? null : new GpuFrameReader(gpu);
        var client = new Rect32 { Left = offset.Left, Top = offset.Top, Right = offset.Left + game.Width, Bottom = offset.Top + game.Height };
        var matcher = new CreepUnitMatcher();
        var tracks = new CreepTracks();
        var labels = new List<OverlayLabel>();
        long checkAt = 0, rateAt = Environment.TickCount64, due = 0;
        int frames = 0, bars = 0, known = 0, shown = 0;
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
                report(new(status, frames / seconds, frames > 0 ? scanTotal / frames : 0, bars, known, shown, gpuLoad, pacing.Limited, kill + Ahead()));
                rateAt = now; frames = 0; scanTotal = 0;
            }
            using var frame = capture.Latest();
            bool front = GetForegroundWindow() == game.Handle;
            if (!front && cues == null && pictures == null) {
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
            // The lab reads a Dota window behind other windows too, but draws only over Dota.
            if (front) { overlay.Place(game.Left, game.Top, game.Width, game.Height); overlay.Draw(labels); }
            else overlay.Hide();
            var found = scanner.Collect();
            scanTotal += Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            var units = minimap();
            var matches = units is { } u && u.Snapshot.OwnTeam is int team
                ? matcher.Match(found, u.Snapshot.Minimap, team, game.Width, game.Height) : new MinimapUnit?[found.Count];
            double at = frame.SystemRelativeTime / 1e7;
            if (pictures != null && reader != null && pictures.Due(at)) {
                int w = client.Right - client.Left, h = client.Bottom - client.Top;
                var pixels = LabFrames.Rent(w, h);
                reader.Read(frame, client, pixels);
                pictures.Save(pixels, w, h, at, found);
            }
            var hits = new CreepHitHistory?[found.Count];
            var names = tracks.Update(found, matches, now, at, hits);
            var view = Lane(units?.Snapshot);
            var red = new bool[found.Count];
            var ahead = timing?.Invoke();
            known = CreepLabels.Build(found, names, matches, view, labels, hits, at, ahead, red);
            if (cues != null) {
                Onsets("ahead", red, redAhead);
                var plain = new bool[found.Count];
                CreepLabels.Build(found, names, matches, view, scratch, hits, at, null, plain);
                Onsets("frame", plain, redFrame);
            }
            frames++; bars = found.Count; shown = labels.Count;

            // A track's number turning red, once the bar is matched to a GSI unit (the lab's harness needs its position).
            void Onsets(string rule, bool[] flags, HashSet<CreepHitHistory> before)
            {
                var current = new HashSet<CreepHitHistory>();
                for (int i = 0; i < found.Count; i++) {
                    if (!flags[i] || hits[i] is not { } track) continue;
                    if (before.Contains(track)) { current.Add(track); continue; }
                    if (matches[i] is not { } unit || unit.Name != names[i] || CreepKinds.MaxHp(unit.Name, view.Clock) is not int max) continue;
                    current.Add(track);
                    cues?.Invoke(new(at, rule, unit.Name, unit.X, unit.Y, found[i].Hp(max),
                        string.Join(" ", track.Drops.Select(d => $"{d.At - at:F3}:{d.Pixels}"))));
                }
                before.Clear(); before.UnionWith(current);
            }
        }
    }
    private readonly HashSet<CreepHitHistory> redAhead = [], redFrame = [];
    private readonly List<OverlayLabel> scratch = [];

    /// <summary>The look-ahead in the status, at the hero's full range.</summary>
    private string Ahead() => timing?.Invoke() is { } t && attack is { } a && kill.Length > 0 && !kill.EndsWith("不标红", StringComparison.Ordinal)
        ? $"\n红字提前看：反应 {t.Lead:F2} 秒＋延迟 {t.Latency:F2} 秒＋出手到命中约 {a.DelayAt(a.Range + HeroAttack.Hulls):F2} 秒，算上这段时间里别的兵和塔按节奏要打的几下" : "";

    private void Pause(int ms) => stop.Token.WaitHandle.WaitOne(ms);

    public async ValueTask DisposeAsync()
    {
        stop.Cancel();
        try { await worker; } finally { stop.Dispose(); pictures?.Dispose(); }
    }

    [DllImport("user32.dll")] private static extern nint GetForegroundWindow();
}
