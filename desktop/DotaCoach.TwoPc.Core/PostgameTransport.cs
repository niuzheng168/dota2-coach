using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DotaCoach.TwoPc;

// The terminal lane has no GET for evidence, no image, no archive access, and no way to publish a live
// frame. It is used identically alongside pinned WSS video and GSI-only HTTPS. Only its challenge is read.
internal sealed record PostgameReceipt(string SessionId, string Ticket, string Source, long Sequence, MatchWitness End)
{
    internal static readonly JsonSerializerOptions Json = new(Wire.Json) {
        AllowDuplicateProperties = false, RespectRequiredConstructorParameters = true, PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, NumberHandling = JsonNumberHandling.Strict,
    };
}

public sealed partial class CoachMailbox
{
    private (string Id, long At)? endTicket;
    private (string Source, long Sequence, MatchWitness Witness)? endOrigin;
    private (string Source, long Sequence, MatchWitness Witness, long Bound)? admittedEnd;

    internal Ticket IssuePostgameTicket(string sessionId)
    {
        lock (gate) {
            Check(sessionId);
            endTicket = (Guid.NewGuid().ToString("N"), now());
            return new(sessionId, endTicket.Value.Id);
        }
    }

    // Under the mailbox lock. Keep only the last positively admitted participant, not an image or
    // a player inventory. Null/menu can race a receipt; a different participant or diagnostic cannot.
    private void ObservePostgameOrigin(FrameView incoming, long instant)
    {
        var m = incoming.Meta;
        if (m.Source is not ("dota2" or "dota2-gsi") || endOrigin is { } origin && (m.Source != origin.Source ||
            m.Player is { } p && (p.MatchId != origin.Witness.MatchId || p.Team != origin.Witness.Team || p.Hero != origin.Witness.Hero)))
            endOrigin = null;
        if (m.MatchPhase is not { } w || !BoardDelivery.Fresh(w, incoming.CapturedNoEarlierThan, instant)) return;
        if (w.Phase == "playing") { endOrigin = (m.Source, m.Sequence, w); return; }
        if (w.Phase == "pregame") { endOrigin = null; return; }
        // Compatibility with already shipped ordinary frames bearing a *current* terminal. Deduplicate
        // against the receipt lane and later terminal heartbeats, even after the UI consumed the first.
        if (admittedEnd?.Witness.MatchId == w.MatchId || !HasPlayingOrigin(m.Source, m.Sequence, w)) return;
        admittedEnd = (m.Source, m.Sequence, w, incoming.CapturedNoEarlierThan);
        pendingPostgame = new(m with { Ticket = "", Player = null, GsiAgeMs = null }, [],
            incoming.CapturedNoEarlierThan, incoming.ReceivedAt);
    }
    private bool HasPlayingOrigin(string source, long seq, MatchWitness end) => endOrigin is { } origin &&
        origin.Source == source && seq > origin.Sequence && PostgameOutbox.SameParticipant(origin.Witness, end) && end.Clock >= origin.Witness.Clock;

    // An already sampled/in-flight playing frame can arrive *after* its independent terminal. Keep
    // real video/geometry and capture ordering, but don't restore live GSI/boards or echo a duplicate end.
    private FrameView WithdrawEndedState(FrameView view)
    {
        var m = view.Meta;
        return (admittedEnd is { } ended && (m.MatchPhase?.MatchId ?? m.Player?.MatchId) == ended.Witness.MatchId ||
            m.MatchPhase is { Phase: "postgame" } w && !HasPlayingOrigin(m.Source, m.Sequence, w)) ?
            view with { Meta = m with { MatchPhase = null, Player = null, GsiAgeMs = null } } : view;
    }
    internal void AcceptPostgame(PostgameReceipt r)
    {
        if (r.Sequence <= 0 || r.Source is not ("dota2" or "dota2-gsi") || r.End is not { Phase: "postgame" } end || !MatchWitness.Valid(end))
            throw new ArgumentException("Invalid participant end receipt");
        lock (gate) {
            Check(r.SessionId);
            long instant = now();
            if (endTicket is not { } t || t.Id != r.Ticket || !BoardDelivery.Fresh(end, t.At, instant))
                throw new InvalidOperationException("Expired terminal evidence or challenge");
            endTicket = null;
            if (admittedEnd is { } admitted && admitted.Witness.MatchId == end.MatchId) {
                // Response lost after admission: acknowledge the *same* receipt without republishing or
                // resetting its age. Sequence alone is not authority to acknowledge another participant.
                if (r.Sequence == admitted.Sequence && r.Source == admitted.Source && BoardDelivery.Fresh(admitted.Witness, admitted.Bound, instant) &&
                    (end with { AgeMs = admitted.Witness.AgeMs }) == admitted.Witness) return;
                throw new InvalidOperationException("Match already ended");
            }
            if (!HasPlayingOrigin(r.Source, r.Sequence, end) || (r.Source == "dota2") != (videoOwner != null))
                throw new InvalidOperationException("No matching admitted playing participant");
            admittedEnd = (r.Source, r.Sequence, end, t.At);
            pendingPostgame = new(new(r.SessionId, "", r.Sequence, r.Source, 0, 0, null, null, end), [], t.At, instant);
            // Never call Store: no new current state, captured geometry, video, history sequence or frame count.
            if (frame != null) frame = WithdrawEndedState(frame);
            reminder = null; minuteWindow.Clear(); board = null; history.Clear(); historyOrder.Clear();
        }
    }
}

public sealed partial class GameLink
{
    private PostgameOutbox? postgame;

    /// <summary>Bind only after pairing a participant source. Never call for diagnostic video. Sampling
    /// before the first new packet can seed a playing identity, never a previously observed terminal.</summary>
    public void BindPostgame(GsiReceiver? gsi, string source, Func<long> nextSequence)
    {
        if (source is not ("dota2" or "dota2-gsi")) throw new ArgumentException("Not a participant transport", nameof(source));
        ArgumentNullException.ThrowIfNull(nextSequence);
        lock (gate) {
            DropPostgame();
            if (gsi != null) postgame = gsi.BindPostgame(SessionId ?? throw new InvalidOperationException("Not paired"), source, nextSequence);
        }
    }
    private void DropPostgame() => Interlocked.Exchange(ref postgame, null)?.Close();

    /// <summary>Wait without polling GSI, touching pixels, or blocking capture/UI. No network traffic
    /// unless an authenticated terminal was latched during this particular participant session.</summary>
    public async Task DeliverPostgames(CancellationToken cancel)
    {
        var box = Volatile.Read(ref postgame);
        try {
            while (box?.WaitForEnd() is { } ready) {
                await ready.WaitAsync(cancel).ConfigureAwait(false);
                await FlushPostgame(box, cancel).ConfigureAwait(false);
            }
        } catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    /// <summary>Capture has stopped. Keep only the existing authenticated control connection briefly for
    /// GSI arriving just after the viewport vanished; finish any fresh admission before aborting the WS.
    /// An explicit disconnect cancels the grace and drops the outbox instead of moving it to a new link.</summary>
    public async Task DrainVideoPostgame(CancellationToken cancel)
    {
        var box = Volatile.Read(ref postgame);
        if (box?.Source != "dota2" || box.WaitForEnd(viewportLost: true) is not { } ready || cancel.IsCancellationRequested) return;
        try { await ready.WaitAsync(TimeSpan.FromMilliseconds(350), cancel).ConfigureAwait(false); }
        catch (TimeoutException) { /* Only an already pending receipt can still be sent. */ }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { return; }
        try { await FlushPostgame(box, cancel).ConfigureAwait(false); }
        catch (OperationCanceledException) when (cancel.IsCancellationRequested) { }
    }

    private async Task FlushPostgame(PostgameOutbox box, CancellationToken cancel)
    {
        // Ordinary transport and shutdown may both wake; only one challenge/POST can be outstanding.
        await box.Sending.WaitAsync(cancel).ConfigureAwait(false);
        try {
            while (ReferenceEquals(box, Volatile.Read(ref postgame)) && box.Session == SessionId &&
                box.Peek(Environment.TickCount64) is { } current) {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancel);
                budget.CancelAfter(MatchWitness.MaxAgeMs - current.End.AgeMs);
                try {
                    // Use the immutable original session in BOTH headers, not Request()'s current session.
                    using var get = new HttpRequestMessage(HttpMethod.Get, "/v1/postgame-ticket");
                    get.Headers.Add("X-Session", box.Session);
                    using var offer = await http.SendAsync(get, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                    var lease = await Json<Ticket>(offer, budget.Token).ConfigureAwait(false);
                    if (lease.SessionId != box.Session || !Guid.TryParseExact(lease.Id, "N", out _))
                        throw new InvalidOperationException("Unrelated terminal challenge");
                    if (box.Peek(Environment.TickCount64) is not { } fresh || fresh.Sequence != current.Sequence) continue;
                    using var upload = new HttpRequestMessage(HttpMethod.Post, "/v1/postgame");
                    upload.Headers.Add("X-Session", box.Session);
                    upload.Content = JsonContent.Create(new PostgameReceipt(box.Session, lease.Id, box.Source, fresh.Sequence, fresh.End),
                        options: PostgameReceipt.Json);
                    using var ack = await http.SendAsync(upload, HttpCompletionOption.ResponseHeadersRead, budget.Token).ConfigureAwait(false);
                    if (ack.StatusCode == HttpStatusCode.NoContent) { box.Acknowledge(fresh.Sequence); continue; }
                } catch (Exception e) when (!cancel.IsCancellationRequested &&
                    e is HttpRequestException or IOException or OperationCanceledException or JsonException or InvalidOperationException) {
                    // A failed write, timeout or missing ACK is not delivery. Retry the *same* receipt only
                    // inside its provider-evidence lifetime (and only into the original session).
                }
                await Task.Delay(100, cancel).ConfigureAwait(false);
            }
        } finally { box.Sending.Release(); }
    }
}
