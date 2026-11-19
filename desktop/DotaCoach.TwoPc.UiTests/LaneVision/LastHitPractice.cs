using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal interface ILaneOverlay
{
    bool Visible {get;}
    void Warm();
    void Paint(GameSurface game,int w,int h,long at,LaneDrill.Reading[] values);
    void HideMarks();
    void Close();
}
internal sealed record LaneInput(Func<GameSurface?> Foreground, Func<GameSurface,bool> Focus,
    Func<GameSurface,bool> Bounds,Func<GameSurface,int,int,int,ILaneFrames> Open)
{
    public static readonly LaneInput Windows=new(GameWindow.Foreground,GameWindow.IsForeground,GameWindow.HasForegroundBounds,
        (g,w,h,fps)=>new WindowVideoSource(g,w,h,fps,limitPractice:true));
}

/// <summary>Separate from the pairing/video stream. WGC, recognition and drawing all stay on the
/// game machine. No desktop-copy fallback, game commands, coach messages, logging of frames or cloud.</summary>
internal sealed class LastHitPractice : IAsyncDisposable
{
    private sealed record Picture(GameSurface? Game, int Width, int Height, long At, LaneDrill.Reading[] Readings, string Status);
    private readonly CancellationTokenSource stop = new();
    private readonly Func<PracticeGate> eligibility;
    private bool Allowed() => eligibility().Enabled;
    private readonly Action<string> status;
    private readonly ILaneOverlay overlay;
    private readonly LaneInput input;
    private readonly DispatcherTimer timer = new() { Interval = TimeSpan.FromMilliseconds(35) };
    private readonly Task worker;
    private Picture? latest, drawn;
    private string? lastStatus;
    private readonly Dispatcher dispatcher = Dispatcher.CurrentDispatcher;
    private int uiPending, needsFreshFrame;
    private bool closed;
    internal const int EconomyFps = 60, FastFps = 150;
    private readonly int targetFps;
    private readonly bool laneVision;
    private readonly LaneHealthProfile hpProfile;
    private long suppliedFrames, detectedFrames;
    internal long SuppliedFrames => Interlocked.Read(ref suppliedFrames);
    internal long DetectedFrames => Interlocked.Read(ref detectedFrames);
    private static long Now => Stopwatch.GetElapsedTime(0).Ticks; // same 100 ns QPC clock as WGC
    internal PracticeTimings? Timings { get; }

    public LastHitPractice(Func<PracticeGate> eligibility, int targetFps, Action<string> status, bool measure = false, bool laneVision = false,LaneHealthProfile? hpProfile=null)
        : this(eligibility,targetFps,status,measure,laneVision,LaneInput.Windows,null,hpProfile) { }
    internal LastHitPractice(Func<PracticeGate> eligibility,int targetFps,Action<string> status,bool measure,
        bool laneVision,LaneInput input,ILaneOverlay? output,LaneHealthProfile? hpProfile=null)
    {
        if (targetFps is not (EconomyFps or FastFps)) throw new ArgumentOutOfRangeException(nameof(targetFps));
        this.targetFps = targetFps; this.laneVision=laneVision; this.hpProfile=hpProfile??LaneHealthProfile.Standard;
        this.eligibility = eligibility; this.status = status; this.input=input;
        if (measure) Timings = new();
        overlay = output ?? new HealthWindows(Allowed, Timings);
        overlay.Warm(); // Opt-in only. Pay the first hidden HWND/font initialization before requesting a frame.
        worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        timer.Tick += Tick; timer.Start();
    }
    private void Run()
    {
        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal; // Dedicated worker, never the game or UI thread.
        while (!stop.IsCancellationRequested) {
            try {
                var game = Allowed() ? input.Foreground() : null;
                if (game == null) { Idle(laneVision ? "等你切回自己的 Dota 窗口。" : "等待前台露娜试玩：一级空装、白天、未暂停，前 7 分半。"); Pause(150); continue; }
                var size = VideoPixels.SizeFor(game.Width, game.Height, 1920, 1080);
                var drill = new LaneDrill(hpProfile:hpProfile);
                var bars = new List<CreepBar>(32);
                var readings = new List<LaneDrill.Reading>(32);
                using var source = input.Open(game,size.Width,size.Height,targetFps);
                using var timer = new PreciseTimer();
                long checkBounds = Now, sample = Now, received = 0, processed = 0;
                LaneDrill.Reading[] previous = [];
                int pendingPolls = 0;
                string text = "本机识别：测量新帧率…";
                while (!stop.IsCancellationRequested && Allowed() && input.Focus(game)) {
                    if (Now >= checkBounds) {
                        if (!input.Bounds(game)) break;
                        checkBounds = Now + TimeSpan.TicksPerMillisecond * 100;
                    }
                    bool read = source.TryReadBars(bars, out long raw, out int arrived, out long dequeued);
                    received += arrived; Interlocked.Add(ref suppliedFrames, arrived);
                    if (source.Ended) break;
                    if (!read) {
                        if (!source.ReadPending) pendingPolls = 0;
                        timer.Wait(source.ReadPending && pendingPolls++ < 2 ? 10_000 : targetFps == EconomyFps ? 40_000 : 15_000);
                        continue;
                    }
                    pendingPolls = 0;
                    if (!Allowed() || !input.Focus(game)) continue;
                    long readAt = Now;
                    bool fresh = PracticeTimings.Observation(raw, dequeued, readAt, targetFps, out long at);
                    Timings?.ObserveLead(raw, dequeued, readAt, fresh);
                    if (!fresh) { Timings?.SkipStale(); continue; }
                    double time=at/(double)TimeSpan.TicksPerSecond;
                    int changes=laneVision ? drill.ReadLaneVision(bars,time,readings) : drill.ReadHealth(bars,time,readings);
                    // Hand off immutable snapshots; static values/positions need no new array.
                    var snapshot=CollectionsMarshal.AsSpan(readings).SequenceEqual(previous) ? previous : readings.ToArray();
                    long now = Now;
                    if(Timings!=null) {
                        int losses=0; foreach(var r in readings) if(r.LossRevision!=0 && r.LossAt==time) losses++;
                        Timings.Readout(at, now, changes,losses);
                    }
                    processed++; Interlocked.Increment(ref detectedFrames);
                    if (!PracticeTimings.Fresh(at, now)) snapshot = [];
                    if (now - sample >= TimeSpan.TicksPerSecond) {
                        double seconds = (now - sample) / (double)TimeSpan.TicksPerSecond;
                        var kinds=drill.VisibleTypes;
                        text = $"本机识别：供帧 {received / seconds:F0} / 检测 {processed / seconds:F0} FPS · " +
                            (laneVision ? $"{readings.Count} 根可见兵条" : $"近 {kinds.Melee} / 远 {kinds.Ranged} / 未知 {kinds.Unknown}");
                        received = processed = 0; sample = now;
                    }
                    Volatile.Write(ref latest, new(game, size.Width, size.Height, at, snapshot, text));
                    bool retry = snapshot.Length>0 && Interlocked.Exchange(ref needsFreshFrame, 0)!=0;
                    if ((retry || snapshot.Length!=previous.Length || changes!=0) && Interlocked.Exchange(ref uiPending,1)==0) {
                        dispatcher.BeginInvoke(DispatcherPriority.Render, new Action(() => {
                            Interlocked.Exchange(ref uiPending, 0); if (!closed) Tick(null, EventArgs.Empty);
                        }));
                    }
                    previous = snapshot;
                }
                Idle("已暂停，等待新的试玩画面。");
            } catch (Exception ex) {
                Idle($"窗口采集不可用（{ex.GetType().Name}），请使用无边框或窗口模式。"); Pause(750);
            }
        }
    }
    private void Pause(int ms) => stop.Token.WaitHandle.WaitOne(ms);
    private void Idle(string text) => Volatile.Write(ref latest, new(null, 0, 0, 0, [], text));
    private void Tick(object? sender, EventArgs e)
    {
        var p = Volatile.Read(ref latest);
        var gate = eligibility();
        var game = p?.Game;
        bool fresh = gate.Enabled && game != null && PracticeTimings.Fresh(p!.At, Now) && input.Bounds(game);
        var interval = TimeSpan.FromMilliseconds(gate.Enabled && p?.Readings.Length>0 && (fresh || overlay.Visible) ? 24 : 200);
        if (timer.Interval != interval) timer.Interval = interval;
        string text = !gate.Enabled ? gate.Message : game != null && !fresh ? "画面已过期或已切出，标记已清除。" :
            p?.Status ?? "请切回 Dota，等待试玩画面…";
        if (text != lastStatus) { status(text); lastStatus = text; }
        if (!fresh || game == null || p!.Readings.Length==0) {
            if (gate.Enabled && p?.Readings.Length>0) Interlocked.Exchange(ref needsFreshFrame, 1);
            overlay.HideMarks(); drawn = null; return;
        }
        if (ReferenceEquals(drawn, p)) return;
        overlay.Paint(game,p.Width,p.Height,p.At,p.Readings); drawn=p;
    }
    public async ValueTask DisposeAsync()
    {
        closed = true; stop.Cancel(); timer.Stop(); timer.Tick -= Tick; overlay.Close();
        try { await worker; } finally { stop.Dispose(); }
    }

    private abstract class PracticeWindow : Window
    {
        private IntPtr hwnd;
        private (int X, int Y, int W, int H)? lastRect;
        protected PracticeWindow()
        {
            WindowStartupLocation = WindowStartupLocation.Manual; Width = Height = 1; Left = Top = -32000;
            WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.NoResize; AllowsTransparency = true;
            Background = Brushes.Transparent; Topmost = true; ShowInTaskbar = false; ShowActivated = false;
            Focusable = false; IsHitTestVisible = false;
            SourceInitialized += (_, _) => {
                hwnd = new WindowInteropHelper(this).Handle;
                SetWindowLongPtr(hwnd, -20, GetWindowLongPtr(hwnd, -20) | 0x080000A0);
                HwndSource.FromHwnd(hwnd)?.AddHook((IntPtr h, int m, IntPtr w, IntPtr l, ref bool handled) => {
                    if (m != 0x84 && m != 0x21) return IntPtr.Zero;
                    handled = true; return m == 0x84 ? -1 : 3;
                });
            };
        }
        public void HideMarks() { if (IsVisible) Hide(); lastRect = null; }
        protected bool Place(int x, int y, int w, int h)
        {
            if (!IsVisible) Show();
            if (lastRect == (x,y,w,h)) return true;
            if (!SetWindowPos(hwnd, -1, x, y, w, h, 0x0010)) { HideMarks(); return false; }
            lastRect = (x,y,w,h); return true;
        }
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern nint GetWindowLongPtr(nint h, int index);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern nint SetWindowLongPtr(nint h, int index, nint value);
        [DllImport("user32.dll")] private static extern bool SetWindowPos(nint h, nint after, int x, int y, int w, int height, uint flags);
    }

    private sealed class HealthWindows(Func<bool> allowed, PracticeTimings? timings) : ILaneOverlay
    {
        private readonly HealthLayout layout = new();
        private readonly HealthInk ink = new();
        private readonly TileWindow?[] live = new TileWindow?[HealthLayout.Columns * HealthLayout.Rows];
        private readonly Stack<TileWindow> spare = new();
        private LaneDrill.Reading[]? arranged;
        private int frameWidth, frameHeight;
        public bool Visible { get; private set; }
        public void Warm()
        {
            var window=new TileWindow(allowed, ink, timings);
            try {
                new WindowInteropHelper(window).EnsureHandle();
                ink.Glyph(new(default,LaneCreepType.Melee,LaneHealthProfile.BaseMelee,0,0),VisualTreeHelper.GetDpi(window).PixelsPerDip);
                spare.Push(window);
            } catch { window.Close(); throw; }
        }

        private void Release(int i)
        {
            if (live[i] is not { } window) return;
            window.HideMarks(); spare.Push(window); live[i] = null;
        }
        public void Paint(GameSurface g, int w, int h, long at, LaneDrill.Reading[] values)
        {
            if (!ReferenceEquals(values, arranged) || w != frameWidth || h != frameHeight) {
                layout.Arrange(values, w, h);
                arranged = values; frameWidth = w; frameHeight = h;
            }
            for (int i = 0; i < live.Length; i++) if (layout.Tiles[i].Tags.Count == 0) Release(i);
            Visible = false;
            for (int i = 0; i < live.Length; i++) {
                var tile = layout.Tiles[i];
                if (tile.Tags.Count == 0) continue;
                var window = live[i] ??= spare.TryPop(out var cached) ? cached : new TileWindow(allowed, ink, timings);
                Visible |= window.Paint(g, w, h, at, tile);
            }
        }
        public void HideMarks()
        {
            for (int i = 0; i < live.Length; i++) Release(i);
            Visible = false; arranged = null;
        }
        public void Close() { HideMarks(); while (spare.TryPop(out var window)) window.Close(); }
    }

    private sealed class TileWindow : PracticeWindow
    {
        private readonly Surface surface;
        public TileWindow(Func<bool> allowed, HealthInk ink, PracticeTimings? timings)
        {
            Title = "Dota Coach · 小兵血量"; Content = surface = new(allowed, GameWindow.HasForegroundBounds, ink, timings);
        }
        public bool Paint(GameSurface g, int w, int h, long at, HealthLayout.Tile tile)
        {
            var screen = HealthLayout.Screen(tile.Bounds, w, h, g.Left, g.Top, g.Width, g.Height);
            surface.Set(g, at, tile, !IsVisible);
            return Place(screen.X, screen.Y, screen.Width, screen.Height);
        }
    }

    internal sealed class Surface(Func<bool> allowed, Func<GameSurface,bool> boundsMatch, HealthInk ink, PracticeTimings? timings) : FrameworkElement
    {
        private readonly List<HealthTag> local = new(8);
        private bool painted;
        private int width, height;
        private long at;
        private GameSurface? game;
        public void Set(GameSurface g, long stamp, HealthLayout.Tile tile, bool force)
        {
            var bounds = tile.Bounds;
            bool redraw = force || !painted || g != game || width != bounds.Width || height != bounds.Height || local.Count != tile.Tags.Count;
            for (int i = 0; i < tile.Tags.Count; i++) {
                var tag = tile.Tags[i]; var b = tag.Box;
                var next = new HealthTag(new(b.X - bounds.X, b.Y - bounds.Y, b.Width, b.Height), tag.Reading);
                if (i == local.Count) local.Add(next);
                else {
                    var old = local[i];
                    if (next.Box != old.Box || next.Reading.Value != old.Reading.Value || next.Reading.Type != old.Reading.Type ||
                        next.Reading.RangedAlternative != old.Reading.RangedAlternative ||
                        next.Reading.Next != old.Reading.Next ||
                        (timings != null && next.Reading.Revision != old.Reading.Revision)) redraw = true;
                    local[i] = next;
                }
            }
            if (local.Count > tile.Tags.Count) local.RemoveRange(tile.Tags.Count, local.Count - tile.Tags.Count);
            at = stamp; game = g; width = bounds.Width; height = bounds.Height;
            if (redraw) InvalidateVisual();
        }
        protected override void OnRender(DrawingContext dc)
        {
            if (RenderSize.Width <= 0 || RenderSize.Height <= 0 || game == null || !PracticeTimings.Fresh(at, Now) ||
                !allowed() || !boundsMatch(game)) { painted = false; return; }
            ink.Draw(dc, local, width, height, RenderSize, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            painted = true; timings?.Draw(at, Now, local);
        }
    }
}
