using System;
using System.Collections.Generic;

namespace DotaCoach.TwoPc;

// Test-only measurement. No extra capture, per-frame log, or image retention.
internal sealed class PracticeTimings
{
    internal const long Budget = LaneDrill.DecisionBudgetMs * TimeSpan.TicksPerMillisecond;
    internal static bool Fresh(long source, long now) => now >= source && now - source <= Budget;
    // Both clocks are 100-ns QPC. Trust the earlier physical observation, never re-date old pixels
    // to "now". WGC's original timestamp still controls ordering, duplicate rejection and rate limiting.
    internal static bool Observation(long source, long dequeue, long now, int fps, out long at)
    {
        at = Math.Min(source, dequeue);
        return source > 0 && dequeue > 0 && dequeue <= now &&
            source - dequeue <= Math.Min(20 * TimeSpan.TicksPerMillisecond, 2 * TimeSpan.TicksPerSecond / fps) &&
            Fresh(at, now);
    }
    internal sealed record Distribution(long Count, int? P95UpperMs, double? MaxMs, long Over50);
    internal sealed record Report(Distribution FrameToReadout, Distribution FrameToWpfDraw, Distribution ReadingChangeToFirstDraw,
        long ObservedReadingChanges, long StaleBeforeReadout, long StaleAfterReadout,
        long AdjustedToDequeue,
        long RawFutureAtRead, double MaxRawLeadMs, long ObservedHealthLosses, Distribution HealthLossToFirstWpfDraw);
    private sealed class Histogram
    {
        private readonly long[] bins = new long[1002]; // roof in ms; final bin is >1000 ms
        private long count, max, late;
        public void Add(long ticks)
        {
            long ms = (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;
            bins[Math.Clamp(ms, 0, bins.Length - 1)]++; count++; max = Math.Max(max, ticks);
            if (ticks > Budget) late++;
        }
        public Distribution Read()
        {
            long sum = 0, rank = (count * 95 + 99) / 100; int? p95 = null;
            for (int i = 0; count > 0 && i < bins.Length - 1; i++)
                if ((sum += bins[i]) >= rank) { p95 = i; break; }
            return new(count, p95, count == 0 ? null : Math.Round(max / (double)TimeSpan.TicksPerMillisecond, 2), late);
        }
    }

    private readonly object sync = new();
    private readonly Histogram frame = new(), draw = new(), firstDraw = new(), lossDraw = new();
    // Timing mode is a bounded (30 s / 5 min) run. Ordinary display never allocates this recorder.
    private readonly HashSet<long> painted = new();
    private readonly HashSet<long> paintedLoss = new();
    private long observedLoss;
    private long changes, skipped, expired;
    private long adjusted, rawFuture, maxLead;
    public void SkipStale() { lock (sync) skipped++; }
    public void ObserveLead(long source, long dequeued, long read, bool accepted)
    {
        if (source <= dequeued) return;
        lock (sync) {
            if (accepted) adjusted++;
            if (source > read) rawFuture++;
            maxLead = Math.Max(maxLead, source-dequeued);
        }
    }
    public void Readout(long at, long done, int changed, int losses=0)
    {
        lock (sync) {
            frame.Add(done - at); changes += changed; observedLoss+=losses;
            if (!Fresh(at, done)) expired++;
        }
    }
    public void Draw(long at, long done, List<HealthTag> tags)
    {
        lock (sync) {
            draw.Add(done - at);
            foreach(var tag in tags) {
                var r=tag.Reading;
                if(painted.Add(r.Revision)) firstDraw.Add(done - (long)Math.Round(r.ChangedAt * TimeSpan.TicksPerSecond));
                if(r.LossRevision!=0 && paintedLoss.Add(r.LossRevision))
                    lossDraw.Add(done-(long)Math.Round(r.LossAt*TimeSpan.TicksPerSecond));
            }
        }
    }
    public Report Read() { lock (sync) return new(frame.Read(), draw.Read(), firstDraw.Read(), changes, skipped, expired,
        adjusted, rawFuture, Math.Round(maxLead/(double)TimeSpan.TicksPerMillisecond,2),observedLoss,lossDraw.Read()); }
}
