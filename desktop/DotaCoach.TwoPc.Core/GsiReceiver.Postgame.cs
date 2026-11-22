namespace DotaCoach.TwoPc;

public sealed partial class GsiReceiver
{
    // One paired participant transport; never the practice/diagnostic/Plus receiver. A request that
    // began under the previous pairing cannot commit into the replacement, even after slow body I/O.
    private PostgameOutbox? postgameRecipient;
    internal PostgameOutbox BindPostgame(string session, string source, Func<long> nextSequence)
    {
        lock (gate) {
            postgameRecipient?.Close();
            var box = postgameRecipient = new(session, source, nextSequence);
            long tick = Environment.TickCount64;
            // A fresh playing participant can establish scope. Never adopt a terminal from before pairing.
            if (matches.Latest(tick) is { Phase: "playing" } playing) box.Observe(playing, tick);
            return box;
        }
    }
}

/// <summary>Game-side, single-slot, metadata-only receipt. LatestMatch remains the actual current
/// packet (null after menu). A menu doesn't erase the first end, nor can a heartbeat renew it.</summary>
internal sealed class PostgameOutbox(string session, string source, Func<long> nextSequence)
{
    public string Session { get; } = session;
    public string Source { get; } = source;
    public readonly SemaphoreSlim Sending = new(1, 1);
    private readonly object gate = new();
    private Func<long>? sequence = nextSequence;
    private MatchWitness? playing;
    private long playingAt;
    private string? ended;
    private (long Sequence, MatchWitness End, long SourceAt)? pending;
    private TaskCompletionSource arrived = NewSignal();
    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal static bool SameParticipant(MatchWitness a, MatchWitness b) =>
        a.MatchId == b.MatchId && a.Team == b.Team && a.Hero == b.Hero;

    internal void Observe(MatchWitness? w, long tick)
    {
        lock (gate) {
            if (sequence == null || w == null || !MatchWitness.Valid(w) || w.AgeMs >= MatchWitness.MaxAgeMs) return;
            if (w.Phase != "postgame") {
                if (pending is { } p && !SameParticipant(w, p.End)) pending = null;
                playing = w.Phase == "playing" ? w : null; playingAt = tick;
                return;
            }
            // MatchWitnessStream already correlated this very packet with the raw flat participant.
            // In addition, require our particular pairing to have seen that participant before the end.
            if (ended == w.MatchId || playing == null || !SameParticipant(playing, w) || w.Clock < playing.Clock) return;
            ended = w.MatchId; playing = null;
            pending = (sequence(), w, tick - w.AgeMs);
            arrived.TrySetResult(); arrived = NewSignal();
        }
    }
    internal (long Sequence, MatchWitness End)? Peek(long tick)
    {
        lock (gate) {
            if (pending is not { } p) return null;
            long age = tick - p.SourceAt;
            if (age is < 0 or >= MatchWitness.MaxAgeMs) { pending = null; return null; }
            return (p.Sequence, p.End with { AgeMs = (int)age });
        }
    }
    internal Task? WaitForEnd(bool viewportLost = false)
    {
        lock (gate) {
            if (sequence == null) return null;
            if (pending != null) return Task.CompletedTask;
            if (viewportLost && (playing == null || !BoardDelivery.Fresh(playing, playingAt, Environment.TickCount64))) return null;
            return arrived.Task;
        }
    }
    internal void Acknowledge(long accepted)
    {
        lock (gate) if (pending?.Sequence == accepted) pending = null;
    }
    internal void Reset()
    {
        lock (gate) { pending = null; playing = null; ended = null; arrived.TrySetResult(); arrived = NewSignal(); }
    }
    internal void Close()
    {
        lock (gate) { pending = null; playing = null; ended = null; sequence = null; arrived.TrySetResult(); }
    }
}
