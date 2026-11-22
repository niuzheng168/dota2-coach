namespace DotaCoach.TwoPc;

public enum FarmMinuteOutcome { NoOptions, AlreadyRequested, Abstained, ContextEnded, Published }
public sealed record FarmMinuteResult(FarmMinuteOutcome Outcome, FarmStop[] Stops);

/// <summary>One paid decision per match/game-clock minute, not one decision per wall-clock timer tick.
/// Owns no receiver, media, key or UI. The caller binds permission to the current paired match and HUD.
/// A simulation can propose a destination; only a validated model selection can publish it.</summary>
public sealed class MinuteFarmDirector(Func<long>? clock = null)
{
    private readonly Func<long> now = clock ?? (() => Environment.TickCount64);
    private readonly object gate = new();
    private readonly HashSet<(string Match, int Minute)> requested = [];
    private (string Session, string Match, int Minute, long At)? lastProbe;
    private static int Minute(OwnState own) => (int)Math.Floor(own.Clock / 60);
    private static string Match(FrameMeta f) => f.Player!.MatchId == "0" ? "local:" + f.SessionId : f.Player.MatchId;
    private static FarmMinuteResult No(FarmMinuteOutcome outcome) => new(outcome, []);

    public bool Fresh(FrameView? frame) => MinuteFarmContext.Fresh(frame, now()) &&
        frame!.Meta.Player!.Hero is "npc_dota_hero_luna" or "npc_dota_hero_troll_warlord";

    public bool StillCurrent(FrameView source, FrameView? latest)
    {
        if (!Fresh(latest) || latest == null || !MinuteFarmContext.Eligible(source.Meta.Player)) return false;
        var a = source.Meta.Player!; var b = latest.Meta.Player!;
        return source.Meta.SessionId == latest.Meta.SessionId && source.Meta.Source == latest.Meta.Source && source.Meta.SourceWidth == latest.Meta.SourceWidth &&
            source.Meta.SourceHeight == latest.Meta.SourceHeight && latest.Meta.Sequence >= source.Meta.Sequence &&
            MinuteFarmContext.Continues(a, b) && b.Health >= a.Health * .70 &&
            now() - source.CapturedNoEarlierThan is >= 0 and < Wire.FrameLifetimeMs;
    }

    // Idle probes (no feasible itinerary, e.g. the player just left base) are local only. Don't run a
    // path search at video rate, but permit another look if the player moves before any call was made.
    public bool TryStart(FrameView source, MinimapLayout layout)
    {
        if (!Fresh(source) || !layout.IsValidFor(source.Meta.SourceWidth, source.Meta.SourceHeight)) return false;
        var p = source.Meta.Player!; var match = Match(source.Meta); int minute = Minute(p);
        if ((minute + 1) * 60 - p.Clock < 4) return false;
        lock (gate) {
            long t = now();
            if (requested.Count >= 4096 || requested.Contains((match, minute)) ||
                lastProbe is { } last && last.Session == source.Meta.SessionId && last.Match == match &&
                last.Minute == minute && t - last.At < 3000) return false;
            lastProbe = (source.Meta.SessionId, match, minute, t); return true;
        }
    }

    /// <summary>Keep the original evidence sequence across the entire async call. Never substitute the latest
    /// frame for the model source. Reprice the selected stops from the latest position and remaining game time;
    /// losing a suffix is OK, swapping in a different plan without the model is not.</summary>
    public async Task<FarmMinuteResult> DecideAndPublish(CoachMailbox mailbox, FrameView source,
        MinimapLayout layout, bool showSubtitle, Func<bool> authorized,
        Func<OwnState, FarmMinuteScenario, CancellationToken, Task<string?>> select, CancellationToken cancel,
        MatchFarmSlice? matchInput = null)
    {
        FrameView? Current() {
            cancel.ThrowIfCancellationRequested();
            var current = mailbox.Latest();
            return authorized() && StillCurrent(source, current) ? current : null;
        }
        double ClockUpper(FrameView f) => f.Meta.Player!.Clock + (f.Meta.GsiAgeMs!.Value + now() - f.CapturedNoEarlierThan) / 1000d;
        if (!layout.IsValidFor(source.Meta.SourceWidth, source.Meta.SourceHeight) || Current() == null)
            return No(FarmMinuteOutcome.ContextEnded);
        if (matchInput != null && (matchInput.Session != source.Meta.SessionId || matchInput.FrameSequence != source.Meta.Sequence ||
            source.Meta.Player is not { } participant || !MinuteFarmContext.SameScope(participant, matchInput.Own) ||
            participant.Clock != matchInput.Own.Clock)) return No(FarmMinuteOutcome.ContextEnded);
        var own = matchInput?.Own ?? source.Meta.Player!; int minute = Minute(own), end = (minute + 1) * 60;
        var scenario = await Task.Run(() => MinuteFarmSimulation.Build(own), cancel);
        if (scenario == null || scenario.Options.Length == 0) return No(FarmMinuteOutcome.NoOptions);
        if (Current() is not { } before || end - ClockUpper(before) < 4) return No(FarmMinuteOutcome.ContextEnded);
        lock (gate) {
            if (requested.Count >= 4096 || !requested.Add((Match(source.Meta), minute))) return No(FarmMinuteOutcome.AlreadyRequested);
        }
        var id = await select(own, scenario, cancel);
        if (Current() is not { } fresh) return No(FarmMinuteOutcome.ContextEnded);
        if (id == null) return No(FarmMinuteOutcome.Abstained);
        var selected = scenario.Options.SingleOrDefault(o => o.Id == id) ?? throw new InvalidDataException("Unknown itinerary");
        double at = ClockUpper(fresh);
        if (at >= end) return No(FarmMinuteOutcome.ContextEnded);
        var repriced = await Task.Run(() => MinuteFarmSimulation.Reprice(fresh.Meta.Player! with { Clock = at }, selected), cancel);
        if (Current() is not { } final) return No(FarmMinuteOutcome.ContextEnded);
        // The reprice itself runs off the UI thread. A blink/TP during this short interval invalidates the origin.
        var from = fresh.Meta.Player!; var to = final.Meta.Player!;
        if (Math.Abs(from.X!.Value - to.X!.Value) + Math.Abs(from.Y!.Value - to.Y!.Value) > 192)
            return No(FarmMinuteOutcome.ContextEnded);
        double latestClock = Math.Max(at, ClockUpper(final));
        int displayMs = (int)Math.Floor((end - latestClock) * 1000);
        // Leave one second for quantized GSI clock and transport. All times below are relative
        // to publication, NOT to the earlier minute tick on which the model started reasoning.
        // Reprice's offsets include the fractional-start allowance and are anchored to floor(at).
        double budgetOrigin = Math.Floor(at) + (latestClock - at);
        var stops = repriced.TakeWhile(s => budgetOrigin + s.FinishSeconds + 1 <= end).ToArray();
        if (displayMs < 1000 || stops.Length == 0) return No(FarmMinuteOutcome.NoOptions);
        var marks = stops.Select(s => MinuteFarmSimulation.Project(s, layout)).ToArray();
        string Step(FarmStop stop) {
            int eta = (int)Math.Ceiling(budgetOrigin + stop.ArrivalSeconds);
            return $"约{eta / 60}:{eta % 60:00} {stop.Label} · {(stop.Kind == "lane" ? "有线再收" : "有野再打")}";
        }
        string message = $"{minute}:00–{minute + 1}:00　按编号走；空点跳过，遇险就撤。";
        mailbox.SetReminder(source.Meta.SessionId, source.Meta.Sequence, "minute-farm-route", message,
            marks[0], layout, showSubtitle, displayMs, marks, stops.Select(Step).ToArray(), farmMinute: minute);
        return new(FarmMinuteOutcome.Published, stops);
    }
}
