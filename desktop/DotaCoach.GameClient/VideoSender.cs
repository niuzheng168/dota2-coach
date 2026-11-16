using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using DotaCoach.Desktop;

namespace DotaCoach.TwoPc;

internal sealed record CapturedVideo(FrameMeta Meta, VideoPixels Pixels, long CapturedAt);
internal sealed record VideoLease(Ticket Ticket, long ReceivedAt);

/// <summary>Capture and encode/send workers. Only unencoded pictures may be dropped; H264 dependencies stay ordered.</summary>
internal sealed class VideoSender
{
    private readonly Channel<CapturedVideo> pending;
    private VideoLease lease;
    private CapturedVideo? latest;
    private long captures, sent, bytes, dropped;
    public CapturedVideo? Latest => Volatile.Read(ref latest);
    public long Captures => Interlocked.Read(ref captures);
    public long Sent => Interlocked.Read(ref sent);
    public long Bytes => Interlocked.Read(ref bytes);
    public long Dropped => Interlocked.Read(ref dropped);
    public VideoSender(Ticket ticket)
    {
        lease = new(ticket, Environment.TickCount64);
        pending = Channel.CreateBounded<CapturedVideo>(new BoundedChannelOptions(1) {
            SingleReader = true, SingleWriter = true, FullMode = BoundedChannelFullMode.DropOldest,
        }, _ => Interlocked.Increment(ref dropped));
    }
    public void Renew(Ticket ticket) => Volatile.Write(ref lease, new(ticket, Environment.TickCount64));
    public async Task CaptureLoop(bool diagnostic, GameSurface? surface, Func<long> nextSequence,
        Func<(OwnState? Player, int? AgeMs)> ownState, CancellationToken cancellation)
    {
        try {
            while (!cancellation.IsCancellationRequested) {
                long started = Environment.TickCount64;
                var currentLease = Volatile.Read(ref lease);
                if (started - currentLease.ReceivedAt > 1500) throw new InvalidOperationException("Video lease renewal stalled");
                long sequence = nextSequence();
                var own = diagnostic ? (Player: (OwnState?)null, AgeMs: (int?)null) : ownState();
                // GSI snapshot and the original monotonic capture instant precede encoding.
                var pixels = diagnostic ? Capture.DiagnosticVideo(sequence) : Capture.GameVideo(surface!);
                if (pixels == null) return; // foreground/window bounds changed; caller ends this session.
                var meta = new FrameMeta(currentLease.Ticket.SessionId, currentLease.Ticket.Id, sequence,
                    diagnostic ? "diagnostic" : "dota2", diagnostic ? 1280 : surface!.Width,
                    diagnostic ? 720 : surface!.Height, own.Player, own.AgeMs);
                var frame = new CapturedVideo(meta, pixels, started);
                Volatile.Write(ref latest, frame); Interlocked.Increment(ref captures);
                pending.Writer.TryWrite(frame);
                // No catch-up burst: processing pressure reduces actual fps rather than creating old-frame queues.
                int delay = Math.Max(1, (int)(1000.0 / WindowsH264.Fps - (Environment.TickCount64 - started)));
                await Task.Delay(delay, cancellation).ConfigureAwait(false);
            }
        } finally { pending.Writer.TryComplete(); }
    }
    public async Task SendLoop(GameLink link, CancellationToken cancellation)
    {
        WindowsH264? encoder = null;
        var originals = new Dictionary<long, CapturedVideo>();
        try {
            await foreach (var frame in pending.Reader.ReadAllAsync(cancellation).ConfigureAwait(false)) {
                if (Environment.TickCount64 - frame.CapturedAt > 500) { Interlocked.Increment(ref dropped); continue; }
                encoder ??= new(true, frame.Pixels.Width, frame.Pixels.Height);
                long time = frame.Meta.Sequence * WindowsH264.Duration;
                originals.Add(time, frame);
                if (originals.Count > 8) throw new InvalidOperationException("Encoder backlog");
                foreach (var output in encoder.Encode(frame.Pixels, time)) {
                    if (!originals.Remove(output.Time, out var source)) throw new InvalidOperationException("Uncorrelated encoder output");
                    await link.SendVideo(new(source.Meta, source.Pixels.Width, source.Pixels.Height, output.Time),
                        output.Data, source.CapturedAt, cancellation).ConfigureAwait(false);
                    Interlocked.Increment(ref sent); Interlocked.Add(ref bytes, output.Data.Length);
                }
            }
        } finally { encoder?.Dispose(); }
    }
}
