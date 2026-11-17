using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DotaCoach.TwoPc;

internal sealed record CapturedVideo(FrameMeta Meta, VideoPixels Pixels, long CapturedAt, long Time);
internal sealed record VideoLease(Ticket Ticket, long ReceivedAt);

/// <summary>
/// Capture thread → newest-picture slot → encode/send worker. Only unencoded pictures may be dropped;
/// H.264 dependencies stay ordered. Capture never waits for the network or the encoder.
/// </summary>
internal sealed class VideoSender
{
    private readonly Channel<CapturedVideo> pending;
    private readonly int fps;
    private VideoLease lease;
    private CapturedVideo? latest;
    private long captures, sent, bytes, dropped, captureTicks, encodeTicks, encodes, lastPictureAt;
    private volatile string encoderName = "等待首帧", sourceName = "";
    public CapturedVideo? Latest => Volatile.Read(ref latest);
    public long Captures => Interlocked.Read(ref captures);
    public long Sent => Interlocked.Read(ref sent);
    public long Bytes => Interlocked.Read(ref bytes);
    public long Dropped => Interlocked.Read(ref dropped);
    public int Fps => fps;
    public string EncoderName => encoderName;
    public string SourceName => sourceName;
    public bool HardwareFailed { get; private set; }
    public string? EncoderNote { get; private set; }
    public double CaptureMs => Interlocked.Read(ref captures) == 0 ? 0 :
        Interlocked.Read(ref captureTicks) * 1000.0 / Stopwatch.Frequency / Interlocked.Read(ref captures);
    public double EncodeMs => Interlocked.Read(ref encodes) == 0 ? 0 :
        Interlocked.Read(ref encodeTicks) * 1000.0 / Stopwatch.Frequency / Interlocked.Read(ref encodes);
    /// <summary>Monotonic ms of the last new picture (0 = none yet).</summary>
    public long LastPictureAt => Interlocked.Read(ref lastPictureAt);
    /// <summary>True when capture stopped because the window produced no new pictures (minimised/hung).</summary>
    public bool Starved { get; private set; }

    public VideoSender(Ticket ticket, int fps)
    {
        this.fps = fps;
        lease = new(ticket, Environment.TickCount64);
        pending = Channel.CreateBounded<CapturedVideo>(new BoundedChannelOptions(1) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        }, _ => Interlocked.Increment(ref dropped));
    }
    public void Renew(Ticket ticket) => Volatile.Write(ref lease, new(ticket, Environment.TickCount64));

    /// <summary>Runs the capture loop on its own MTA thread (D3D/WGC objects live and die there).</summary>
    public Task StartCapture(Func<IVideoSource> open, bool diagnostic, Func<long> nextSequence,
        Func<(OwnState? Player, int? AgeMs)> ownState, CancellationToken cancellation)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => {
            try { CaptureLoop(open, diagnostic, nextSequence, ownState, cancellation); done.TrySetResult(); }
            catch (Exception e) { done.TrySetException(e); }
            finally { pending.Writer.TryComplete(); }
        }) { IsBackground = true, Name = "Dota video capture", Priority = ThreadPriority.AboveNormal };
        thread.SetApartmentState(ApartmentState.MTA);
        thread.Start();
        return done.Task;
    }
    private void CaptureLoop(Func<IVideoSource> open, bool diagnostic, Func<long> nextSequence,
        Func<(OwnState? Player, int? AgeMs)> ownState, CancellationToken cancellation)
    {
        using var source = open();
        sourceName = source.Name;
        using var timer = new PreciseTimer();
        long period = Stopwatch.Frequency / fps, start = Stopwatch.GetTimestamp(), next = start, lastTime = -1;
        Interlocked.Exchange(ref lastPictureAt, Environment.TickCount64);
        while (!cancellation.IsCancellationRequested) {
            long started = Stopwatch.GetTimestamp();
            var currentLease = Volatile.Read(ref lease);
            if (Environment.TickCount64 - currentLease.ReceivedAt > 1500) throw new InvalidOperationException("Video lease renewal stalled");
            // GSI snapshot and the monotonic capture instant precede the pixels they describe.
            var own = diagnostic ? (Player: (OwnState?)null, AgeMs: (int?)null) : ownState();
            long capturedAt = Environment.TickCount64;
            var pixels = source.Grab();
            if (source.Ended) return;
            if (pixels != null) {
                long time = Math.Max(lastTime + 1, (started - start) * 10_000_000 / Stopwatch.Frequency);
                lastTime = time;
                var meta = new FrameMeta(currentLease.Ticket.SessionId, currentLease.Ticket.Id, nextSequence(),
                    diagnostic ? "diagnostic" : "dota2", source.SourceWidth, source.SourceHeight, own.Player, own.AgeMs);
                var frame = new CapturedVideo(meta, pixels, capturedAt, time);
                Volatile.Write(ref latest, frame);
                Interlocked.Add(ref captureTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref captures);
                Interlocked.Exchange(ref lastPictureAt, Environment.TickCount64);
                pending.Writer.TryWrite(frame);
            } else if (Environment.TickCount64 - Interlocked.Read(ref lastPictureAt) > 2500) {
                // Minimised (or hung) windows deliver nothing: end cleanly instead of letting the coach time out.
                Starved = true; return;
            }
            // Fixed cadence without catch-up bursts: overload lowers the actual fps instead of queueing old frames.
            next += period;
            long now = Stopwatch.GetTimestamp();
            if (next < now) next = now;
            else timer.Wait((next - now) * 10_000_000 / Stopwatch.Frequency);
        }
    }

    /// <summary>
    /// Encodes with the GPU when allowed and available, falling back to the Windows software encoder.
    /// Sends with up to <see cref="VideoWire.MaxInFlight"/> unacknowledged access units.
    /// </summary>
    public async Task SendLoop(GameLink link, bool allowHardware, int bitrate, CancellationToken cancellation)
    {
        IVideoEncoder? encoder = null;
        var originals = new SortedDictionary<long, CapturedVideo>();
        try {
            await foreach (var frame in pending.Reader.ReadAllAsync(cancellation).ConfigureAwait(false)) {
                if (Environment.TickCount64 - frame.CapturedAt > 500) { Interlocked.Increment(ref dropped); continue; }
                encoder ??= CreateEncoder(frame.Pixels.Width, frame.Pixels.Height, allowHardware, bitrate);
                originals.Add(frame.Time, frame);
                if (originals.Count > 16) throw new InvalidOperationException("Encoder backlog");
                long started = Stopwatch.GetTimestamp();
                IReadOnlyList<VideoSample> outputs;
                try { outputs = encoder.Encode(frame.Pixels, frame.Time); }
                catch when (encoder.Hardware) { HardwareFailed = true; throw; }
                Interlocked.Add(ref encodeTicks, Stopwatch.GetTimestamp() - started);
                Interlocked.Increment(ref encodes);
                foreach (var output in outputs) {
                    // Output order equals input order (no B-frames); earlier entries were pictures the encoder skipped.
                    foreach (long skipped in originals.Keys.TakeWhile(t => t < output.Time).ToArray()) {
                        originals.Remove(skipped); Interlocked.Increment(ref dropped);
                    }
                    if (!originals.Remove(output.Time, out var source)) throw new InvalidOperationException("Uncorrelated encoder output");
                    await link.SendVideo(new(source.Meta, source.Pixels.Width, source.Pixels.Height, output.Time, fps),
                        output.Data, source.CapturedAt, cancellation).ConfigureAwait(false);
                    Interlocked.Increment(ref sent); Interlocked.Add(ref bytes, output.Data.Length);
                }
            }
        } finally { encoder?.Dispose(); }
    }
    private IVideoEncoder CreateEncoder(int width, int height, bool allowHardware, int bitrate)
    {
        if (allowHardware) {
            try {
                var gpu = HardwareH264.Create(width, height, fps, bitrate);
                encoderName = gpu.Name; return gpu;
            } catch (Exception e) {
                HardwareFailed = true;
                EncoderNote = "显卡编码器不可用，已改用CPU软件编码（" + e.GetType().Name + "）";
            }
        }
        var software = new WindowsH264(true, width, height, fps, bitrate);
        encoderName = software.Name; return software;
    }
}
