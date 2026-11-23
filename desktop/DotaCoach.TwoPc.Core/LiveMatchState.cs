using System.Text.Json;

namespace DotaCoach.TwoPc;

// World XY (+Y north) in observations; a candidate from the ward tracker uses its separately labelled
// minimap coordinates (+Y south). A missing observation is not an empty inventory or a destroyed ward.
public sealed record ObservedWard(string Kind, string Team, double X, double Y);
public sealed record MatchSightHero(string Hero, string Team, double X, double Y);
public sealed record FarmEquipment(string Hero, int? Level, string?[]? Items, string? Neutral, double? LastSeenClock);
public sealed record FarmWardEstimate(string Hero, string Kind, int Rank, double Share, double WorldX, double WorldY,
    double EstimatedClock, double ExpiresClock, bool Rough);
public sealed record FarmTeamKnowledge(double KnowledgeClock, double? EquipmentObservedClock, FarmEquipment[] LastReadEquipment,
    double? SightObservedClock, MatchSightHero[] VisibleAtSightSample, double? WardSightObservedClock,
    ObservedWard[] VisibleAtWardSample, double? EstimateObservedClock, FarmWardEstimate[] PossibleEnemyWards)
{
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public LaneWaveSample? LaneWaves { get; init; }
}

/// <summary>Frozen at the source frame, BEFORE local simulation or the paid call. No review/archive API,
/// raw packet, account IDs, pairing secrets, image, or replay contents can be supplied in this projection.</summary>
public sealed class MatchFarmSlice
{
    internal MatchFarmSlice(OwnState own, string session, long frame, long cursor, FarmTeamKnowledge? intel)
        => (Own, Session, FrameSequence, FactSequence, TeamKnowledge) = (own, session, frame, cursor, intel);
    public OwnState Own { get; }
    internal string Session { get; }
    internal object? FarmScope { get; init; } // memory only; renewed on resume/phase/paused transitions
    public long FrameSequence { get; }
    public long FactSequence { get; }
    public FarmTeamKnowledge? TeamKnowledge { get; }
}

/// <summary>Single writer of live match facts and reducer for both live projections. The archive is a sink,
/// not a source of live observations. In particular, this type never calls Read or accesses a DEM.</summary>
public sealed class LiveMatchState(MatchArchive? archive, Func<long>? monotonic = null)
{
    private readonly Func<long> now = monotonic ?? (() => Environment.TickCount64);
    private readonly object gate = new();
    private object farmScope = new();
    private string? match, team, hero, session, phase, sealedMatch;
    private bool connected, paused;
    private double clock, highWater, lastOwnSample = double.NegativeInfinity;
    private long sequence, frameSequence, lastAt;
    private OwnState? own;
    private OwnState? lastArchivedOwn;
    private EnemyHero[]? equipmentRows;
    private WardMark[]? estimates;
    private ObservedWard[]? observedWards;
    private MatchSightHero[]? sight;
    private LaneWaveSample? waves;
    private readonly LaneWave742.Tracker waveTracks = new();
    private long wavesReceived;
    private void ClearWaves() { if (waves != null) waveTracks.Clear(); waves = null; }
    private double scanClock, estimateClock, wardClock, sightClock, lastSightSample = double.NegativeInfinity;
    private long scanAt, estimateAt, wardsAt, sightAt;
    private string? eqSignature, estimateSignature, wardsSignature;
    private (int Minute, string Status)? lastDecision;
    private readonly HashSet<(string Kind, string Hero, string Text, double Clock)> noted = [];
    private readonly Dictionary<string, WardReading> chargeHistory = new(StringComparer.Ordinal);
    private const string LiveOnly = "仅实时显示：本机对局档案不可用，当前不会存档、调用付费模型或加载录像。请关闭重复实例，检查磁盘/权限后重启。";
    public string? StorageWarning { get; private set; } = archive == null ? LiveOnly : null;
    public bool CanAudit { get { lock (gate) return archive != null && StorageWarning == null; } }
    private void UseLiveOnly(Exception? error = null) =>
        StorageWarning ??= error == null ? LiveOnly : LiveOnly + $"（{error.GetType().Name}）";
    public string? ActiveMatch { get { lock (gate) return connected ? match : null; } }
    private static bool At(double t) => Wire.Finite(t, -3600, 43_200);
    private static bool Team(string? t) => t is "radiant" or "dire";
    private static bool Hero(string? h) => h?.StartsWith("npc_dota_hero_", StringComparison.Ordinal) == true && Wire.ValidIconKey(h);
    private static bool Id(string? s) => s is { Length: > 0 and <= 20 } && s[0] != '0' && s.All(char.IsAsciiDigit) && ulong.TryParse(s, out _);
    private static bool IsWard(ObservedWard? w) => w != null && w.Kind is "observer" or "sentry" && Team(w.Team) &&
        Wire.Finite(w.X, -16000, 16000) && Wire.Finite(w.Y, -16000, 16000);
    private static OwnState Copy(OwnState p) => p with { Items = [.. p.Items], Abilities = [.. p.Abilities] };
    private static EnemyHero Copy(EnemyHero h) => h with { Items = h.Items?.ToArray() };

    private bool Write(double at, string source, string kind, object data)
    {
        if (match == null || !CanAudit || archive == null) return false;
        try {
            archive.Append(new(sequence + 1, match, at, clock, source, kind, JsonSerializer.SerializeToElement(data, Wire.Json)));
            sequence++; return true;
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException) {
            UseLiveOnly(e);
            return false;
        }
    }
    private void DropTeam()
    {
        equipmentRows = null; estimates = null; observedWards = null; sight = null; ClearWaves();
        eqSignature = estimateSignature = wardsSignature = null;
        scanClock = estimateClock = wardClock = sightClock = lastSightSample = double.NegativeInfinity;
        noted.Clear(); chargeHistory.Clear();
    }
    private void Gap(string reason)
    {
        if (connected && phase != "postgame") { WithdrawSources(true); Write(clock, "coach", "gap", new { reason }); }
        connected = false; own = null; lastArchivedOwn = null; DropTeam();
    }
    public void Disconnect() { lock (gate) Gap("connection-lost"); }

    // The window supplies one consistent frame to ingest, send, and decide. Frame pixels are never read here.
    public void ObserveFrame(FrameView? frame)
    {
        lock (gate) {
            long t = now();
            if (frame == null || frame.Meta.Source is not ("dota2" or "dota2-gsi") ||
                t < frame.ReceivedAt || t - frame.ReceivedAt >= 3000) { Gap("own-feed-unavailable"); return; }
            var meta = frame.Meta;
            var w = meta.MatchPhase;
            if (w == null && MinuteFarmContext.Fresh(frame, t) && meta.Player is { } legacy) {
                // Compatibility with 0.5: this proves only a playing participant. It can NEVER seal a match.
                w = new(legacy.MatchId, legacy.Team, legacy.Hero, legacy.Clock, "playing", false) { AgeMs = meta.GsiAgeMs!.Value };
            }
            if (w == null || !Id(w.MatchId) || !Team(w.Team) || !Hero(w.Hero) || !At(w.Clock) ||
                !MatchWitness.Valid(w, t - frame.CapturedNoEarlierThan) ||
                meta.Player is { } different && (different.MatchId != w.MatchId || different.Hero != w.Hero || different.Team != w.Team)) {
                Gap("no-trusted-participant"); return;
            }
            bool newMatch = match != w.MatchId, resumedArchive = false;
            // A receipt can outlive the network session for one UI tick. A genuine late end is useful
            // for its own game only; do not let a foreign terminal receipt even withdraw today's sources.
            if (w.Phase == "postgame" && (newMatch || phase != "playing" || session != meta.SessionId ||
                hero != w.Hero || team != w.Team || w.Clock < highWater)) return;
            if (w.MatchId == sealedMatch) return;
            if (newMatch) {
                if (meta.Sequence <= 0) return;
                long nextSequence = 0;
                if (CanAudit && archive != null) {
                    try {
                        if (archive.IsSealed(w.MatchId)) { sealedMatch = w.MatchId; return; }
                        var (last, fence) = archive.GetLiveResume(w.MatchId);
                        // A sparse legacy log can be reviewed but cannot prove whose perspective to continue.
                        // Check before touching either match. A prior playing flag can veto, never authorize.
                        if (last > 0 && (fence is not { } f || w.Team != f.Team || w.Hero != f.Hero ||
                            w.Clock < f.MaxKnownAt || f.WasPlaying && w.Phase == "pregame")) return;
                        nextSequence = last;
                    } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidDataException) {
                        UseLiveOnly(e); // No recovered data, writes, model or seal in this degraded session.
                    }
                }
                // The first lookup can scan an entire live log or wait for fsync. It cannot renew this receipt.
                long admitted = now();
                if (admitted < t || admitted - frame.ReceivedAt >= MatchWitness.MaxAgeMs ||
                    admitted - frame.CapturedNoEarlierThan >= MatchWitness.MaxAgeMs - w.AgeMs) return;
                t = admitted;
                Gap("changed-match");
                sequence = CanAudit ? nextSequence : 0;
                resumedArchive = sequence > 0;
                match = w.MatchId; hero = w.Hero; team = w.Team; phase = null; highWater = w.Clock;
                session = null; frameSequence = 0; lastDecision = null; lastOwnSample = double.NegativeInfinity;
            }
            if (hero != w.Hero || team != w.Team || w.Clock < highWater ||
                phase == "playing" && w.Phase == "pregame") { Gap("participant-or-clock-conflict"); return; }
            if (session != meta.SessionId) {
                Gap("changed-connection"); frameSequence = 0; session = meta.SessionId;
            }
            if (meta.Sequence <= frameSequence) return;
            bool resuming = !connected;
            clock = highWater = w.Clock; connected = true; lastAt = frame.CapturedNoEarlierThan - w.AgeMs; frameSequence = meta.Sequence;
            if (phase != w.Phase || paused != w.Paused || resuming) {
                ClearWaves();
                farmScope = new();
                if (resumedArchive) Write(clock, "coach", "gap", new { reason = "resumed-after-interruption" });
                Write(clock, "own-gsi", "phase", new { phase = w.Phase, paused = w.Paused, team, hero, resumed = resuming && (!newMatch || resumedArchive) });
            }
            phase = w.Phase; paused = w.Paused;
            if (BoardDelivery.InstalledBuild(meta) is int wb && wb != MinuteFarmContext.SupportedBuild) ClearWaves();
            if (phase == "postgame") {
                if (CanAudit && archive != null) {
                    try { archive.Seal(match!, clock); }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException) {
                        UseLiveOnly(e);
                    }
                }
                // Also fence a genuine end in RAM when no writer is available. Only the on-disk seal
                // unlocks review. A recording failure must never be "repaired" with a fabricated seal.
                sealedMatch = match;
                connected = false; own = null; DropTeam(); return;
            }
            own = meta.Player != null && meta.GsiAgeMs is >= 0 and < 3000 &&
                meta.GsiAgeMs.Value + t - frame.ReceivedAt < 3000 && Math.Abs(meta.Player.Clock - w.Clock) <= 1
                ? meta.Player : null;
            if (own != null && (Math.Floor(clock) > Math.Floor(lastOwnSample) || lastArchivedOwn == null ||
                own.Alive != lastArchivedOwn.Alive || !own.Items.Select(x => (x.Slot, x.Name)).SequenceEqual(lastArchivedOwn.Items.Select(x => (x.Slot, x.Name))) ||
                (own.Stats?.Level ?? own.Farm?.Level) != (lastArchivedOwn.Stats?.Level ?? lastArchivedOwn.Farm?.Level))) {
                // GSI raw identity, pairing tickets, receiver epoch and screen/video are not archival inputs.
                var f = own.Farm; var stats = own.Stats;
                Write(clock, "own-gsi", "own-state", new { own.Hero, own.Team, own.Alive, own.X, own.Y, own.Health, own.Mana,
                    level = stats?.Level ?? f?.Level, maxHealth = stats?.MaxHealth ?? f?.MaxHealth,
                    maxMana = stats?.MaxMana ?? f?.MaxMana, lastHits = stats?.LastHits ?? f?.LastHits,
                    gold = stats?.Gold, netWorth = stats?.NetWorth, denies = stats?.Denies, kills = stats?.Kills,
                    deaths = stats?.Deaths, assists = stats?.Assists, gpm = stats?.Gpm, xpm = stats?.Xpm,
                    moveSpeed = f?.MoveSpeed, attackDamage = f?.AttackDamage, attackSpeed = f?.AttackSpeed, installedBuild = BoardDelivery.InstalledBuild(meta),
                    silenced = f?.Silenced, muted = f?.Muted,
                    items = own.Items, abilities = own.Abilities });
                lastArchivedOwn = own; lastOwnSample = clock;
            }
        }
    }

    private bool Fresh() => connected && now() - lastAt is >= 0 and < 3000 && phase is "pregame" or "playing";
    private bool SourceOK(string id, string side, double at) => Fresh() && match == id && team == side &&
        !paused && At(at) && at <= clock && clock - at <= 30;
    private bool Held(long arrival, double at) => Fresh() && !paused && now() - arrival is >= 0 and < Wire.BoardLifetimeMs &&
        at <= clock && clock - at <= 30;
    public void ObserveTeam(string matchId, string team, double observedClock, EnemyBoard? board, bool authoritative = true)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock)) return;
            if (!authoritative || board == null || !Wire.ValidBoard(board) ||
                board.MatchId != null && board.MatchId != matchId || board.Team != null && board.Team != team) {
                if (observedClock >= scanClock) ClearEquipment(observedClock);
                if (observedClock >= estimateClock) ClearEstimates(observedClock);
                return;
            }
            ObserveEquipment(matchId, team, observedClock, board.Heroes);
            ObserveEstimates(matchId, team, observedClock, board.Wards ?? []);
        }
    }
    public void ObserveEquipment(string matchId, string team, double observedClock, EnemyHero[] heroesRead)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock) || observedClock < scanClock || !Wire.ValidBoard(new(heroesRead))) return;
            var heroes = heroesRead.Select(Copy).OrderBy(h => h.Hero, StringComparer.Ordinal).ToArray();
            equipmentRows = heroes; scanClock = observedClock; scanAt = now();
            // Persist reads/changes, not an identical packet at every render. Rounded last-seen clocks preserve
            // the distinction between a new read and a scanner echo of the same aged inventory.
            var eq = JsonSerializer.Serialize(heroes.Select(h => new { h.Hero, h.Level, h.Items, h.Neutral,
                lastSeenClock = h.Age is double age ? (double?)Math.Floor(observedClock - age) : null }), Wire.Json);
            if (eq != eqSignature) {
                Write(observedClock, "team-vision", "equipment", new { available = true, heroes }); eqSignature = eq;
            }
        }
    }
    public void ObserveEstimates(string matchId, string team, double observedClock, WardMark[] marks)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock) || observedClock < estimateClock || !Wire.ValidBoard(new([], marks))) return;
            var wards = marks.ToArray();
            estimates = wards; estimateClock = observedClock; estimateAt = now();
            var signature = JsonSerializer.Serialize(wards.Select(w => new { w.Hero, w.Kind, w.X, w.Y, w.Rank, w.Share, w.Rough,
                when = Math.Floor(observedClock - w.Age) }), Wire.Json);
            if (signature != estimateSignature) {
                Write(observedClock, "inference", "ward-estimate", new { available = true, coordinates = "minimap-0..1", wards });
                estimateSignature = signature;
            }
        }
    }
    private void ClearEquipment(double at) {
        if (equipmentRows != null) Write(at, "team-vision", "equipment", new { available = false, heroes = Array.Empty<EnemyHero>() });
        equipmentRows = null; eqSignature = null;
    }
    private void ClearEstimates(double at) {
        if (estimates != null) Write(at, "inference", "ward-estimate", new { available = false, wards = Array.Empty<WardMark>() });
        estimates = null; estimateSignature = null;
    }
    private void WithdrawSources(bool all)
    {
        if (all || !FreshWaves()) ClearWaves();
        if (all || !Held(scanAt, scanClock)) ClearEquipment(clock);
        if (all || !Held(estimateAt, estimateClock)) ClearEstimates(clock);
        if (observedWards != null && (all || !Held(wardsAt, wardClock))) {
            Write(clock, "team-vision", "ward-observed", new { available = false, wards = Array.Empty<ObservedWard>() });
            observedWards = null; wardsSignature = null;
        }
        if (sight != null && (all || !Held(sightAt, sightClock))) {
            Write(clock, "team-vision", "team-sight", new { available = false, heroes = Array.Empty<MatchSightHero>() }); sight = null;
        }
    }
    public void WithdrawTeam() { lock (gate) WithdrawSources(true); }
    public void ExpireTeamSources() { lock (gate) WithdrawSources(false); }
    private bool FreshWaves() => waves != null && Fresh() && !paused && now() - wavesReceived is >= 0 and < TeamViewEvidence.FreshMs &&
        clock >= waves.ObservedClock && clock - waves.ObservedClock < LaneWave742.FreshSeconds;
    internal static bool WaveBuild(FrameMeta m, bool confirmed) => BoardDelivery.InstalledBuild(m) is int build
        ? build == MinuteFarmContext.SupportedBuild : confirmed;
    internal void ObserveLaneWaves(FrameView frame, SpectatorSnapshot s, bool confirmed = false)
    {
        lock (gate) {
            if (!Matches(frame) || !WaveBuild(frame.Meta, confirmed)) { ClearWaves(); return; }
            var witness = TeamViewEvidence.Participant(frame, now());
            if (witness?.Phase != "playing" || !TeamViewEvidence.Matches(witness, s, now())) { ClearWaves(); return; }
            // Consume the first packet of each game second, not each 100-ms UI refresh. No renewal on duplicates.
            if (waves != null && Math.Floor(s.Clock!.Value) <= Math.Floor(waves.ObservedClock)) return;
            waves = LaneWave742.Observe(witness, s, now(), waveTracks);
            if (waves == null) waveTracks.Clear();
            wavesReceived = s.ReceivedAt;
        }
    }
    internal LaneWaveSample? CurrentLaneWaves(FrameView frame)
    {
        lock (gate) return Matches(frame) && FreshWaves() ? waves! with { Lanes = [.. waves.Lanes] } : null;
    }
    private LaneMap742? GameWaves(FrameView frame, bool confirmed)
    {
        if (phase != "playing" || !WaveBuild(frame.Meta, confirmed) || frame.Meta.MatchPhase == null) return null;
        bool teamView = FreshWaves();
        long tick = now(), age = Math.Max(tick - lastAt, teamView ? tick - wavesReceived : 0);
        int lease = (int)(LaneMap742.Lease - age);
        if (lease <= 0) return null;
        var map = waveTracks.Project((int)Math.Floor(clock), teamView);
        return map with { Marks = [.. map.Marks], LeaseMs = lease };
    }
    public void ObserveWards(string matchId, string team, double observedClock, ObservedWard[] wards)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock) || observedClock < wardClock || wards == null || wards.Length > 64 || !wards.All(IsWard)) return;
            observedWards = wards.Distinct().OrderBy(w => w.Team).ThenBy(w => w.Kind).ThenBy(w => w.X).ThenBy(w => w.Y).ToArray();
            wardClock = observedClock; wardsAt = now();
            string signature = JsonSerializer.Serialize(observedWards, Wire.Json);
            if (signature != wardsSignature) {
                Write(observedClock, "team-vision", "ward-observed", new { coordinates = "world-xy", wards = observedWards });
                wardsSignature = signature;
            }
        }
    }
    public void ObserveSight(string matchId, string team, double observedClock, MatchSightHero[] heroes)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock) || observedClock < sightClock || heroes == null || heroes.Length > 10 ||
                heroes.Any(h => h == null || !Hero(h.Hero) || !Team(h.Team) || !Wire.Finite(h.X, -16000, 16000) || !Wire.Finite(h.Y, -16000, 16000))) return;
            var next = heroes.OrderBy(h => h.Hero, StringComparer.Ordinal).ToArray();
            bool changed = sight == null || !next.Select(h => (h.Hero, h.Team)).SequenceEqual(sight.Select(h => (h.Hero, h.Team)));
            sight = next; sightClock = observedClock; sightAt = now();
            if (changed || Math.Floor(observedClock) > Math.Floor(lastSightSample)) {
                Write(observedClock, "team-vision", "team-sight", new { heroes = next }); lastSightSample = observedClock;
            }
        }
    }
    public void ObserveWardReadings(string matchId, string team, double observedClock, string hero, IReadOnlyList<WardReading> readings)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock) || !Hero(hero) || hero == this.hero || readings == null ||
                readings.Count > 8 || readings.Any(r => r == null)) return;
            foreach (var r in readings.OrderBy(v => v.Clock)) {
                if (!At(r.Clock) || r.Clock > observedClock || r.First is double start && (!At(start) || start > r.Clock) ||
                    r.Observer is < 0 or > 30 || r.Sentry is < 0 or > 30 || r.Unknown is < 0 or > 30) continue;
                if (chargeHistory.TryGetValue(hero, out var prev) && (r.Clock <= prev.Clock || r.Observer == prev.Observer &&
                    r.Sentry == prev.Sentry && r.Unknown == prev.Unknown && r.First == prev.First &&
                    Math.Floor(r.Clock) == Math.Floor(prev.Clock))) continue;
                if (chargeHistory.Count >= 12 && !chargeHistory.ContainsKey(hero)) continue;
                chargeHistory[hero] = r;
                Write(r.Clock, "team-vision", "ward-charges", new { hero, observer = r.Observer, sentry = r.Sentry, unknown = r.Unknown,
                    firstReadClock = r.First, lastReadClock = r.Clock });
            }
        }
    }
    // Single admission: the same validated live fact yields an optional game subtitle. Its return
    // value is ephemeral, so toggling a display never replays the retained historical observations.
    public PlusAlert? NoteTeam(string matchId, string team, double observedClock, string kind, string hero, string text)
    {
        lock (gate) {
            if (!SourceOK(matchId, team, observedClock) || kind is not ("ward-alert" or "vision-alert") || !Hero(hero) ||
                string.IsNullOrWhiteSpace(text) || text.Length > 256 || text.Any(char.IsControl)) return null;
            if (noted.Count >= 4096) noted.RemoveWhere(v => clock - v.Clock > 30);
            if (noted.Count >= 4096 || !noted.Add((kind, hero, text, observedClock))) return null;
            Write(observedClock, kind == "ward-alert" ? "inference" : "team-vision", kind, new { hero, text });
            return new(now(), kind, hero, text);
        }
    }
    private bool Matches(FrameView? frame) => frame != null && Fresh() && frame.Meta.SessionId == session &&
        frame.Meta.Sequence == frameSequence && (frame.Meta.Player?.MatchId ?? frame.Meta.MatchPhase?.MatchId) == match;

    public EnemyBoard? ForGame(FrameView? frame, bool equipment, bool wards, MinimapLayout? calibrated, bool laneWaves = false, bool confirmedWaveMap = false)
    {
        lock (gate) {
            if (!Matches(frame) || paused) return null;
            bool tracked = equipmentRows != null && Held(scanAt, scanClock);
            double elapsed = clock - estimateClock;
            var eq = !equipment || !tracked ? [] : equipmentRows!.Select(h => Copy(h) with {
                Age = h.Age is double a ? Math.Min(86400, a + clock - scanClock) : null }).ToArray();
            var layout = (wards || laneWaves) && calibrated?.IsValidFor(frame!.Meta.SourceWidth, frame.Meta.SourceHeight) == true ? calibrated : null;
            var marks = !wards || layout == null || estimates == null || !Held(estimateAt, estimateClock) ? [] : estimates.Where(w => w.Left > elapsed)
                .Select(w => w with { Age = w.Age + elapsed, Left = w.Left - elapsed }).ToArray();
            var seen = wards && layout != null && observedWards != null && Held(wardsAt, wardClock) ? observedWards.ToArray() : [];
            var waveMap = laneWaves && layout != null ? GameWaves(frame!, confirmedWaveMap) : null;
            if (eq.Length + marks.Length + seen.Length == 0 && waveMap == null) return null;
            // A UI refresh is not new evidence. Pin the original participant frame and the shortest
            // remaining source lease so a final poll (or a reconnect racing publication) cannot renew it.
            long t = now();
            long Remaining(long arrival, double observed) => Math.Min(Wire.BoardLifetimeMs - (t - arrival),
                (long)Math.Ceiling((30 - (clock - observed)) * 1000));
            long left = Wire.BoardLifetimeMs;
            if (eq.Length > 0) left = Math.Min(left, Remaining(scanAt, scanClock));
            if (marks.Length > 0) left = Math.Min(left, Remaining(estimateAt, estimateClock));
            if (seen.Length > 0) left = Math.Min(left, Remaining(wardsAt, wardClock));
            return left <= 0 ? null : new EnemyBoard(eq, marks, match, team, layout, seen) {
                SessionId = session, FrameSequence = frameSequence, RemainingMs = (int)left, LaneMap = waveMap
            };
        }
    }
    public MatchFarmSlice? ForFarm(FrameView frame, bool includeTeam)
    {
        lock (gate) {
            if (!CanAudit || !Matches(frame) || !MinuteFarmContext.Fresh(frame, now()) || own == null || paused || phase != "playing") return null;
            bool sc = equipmentRows != null && Held(scanAt, scanClock), est = estimates != null && Held(estimateAt, estimateClock);
            bool ws = observedWards != null && Held(wardsAt, wardClock), sh = sight != null && Held(sightAt, sightClock);
            var intel = !includeTeam ? null : new FarmTeamKnowledge(clock, sc ? scanClock : null,
                sc ? equipmentRows!.Select(h => new FarmEquipment(h.Hero, h.Level, h.Items?.ToArray(), h.Neutral, h.Age is double a ? scanClock - a : null)).ToArray() : [],
                sh ? sightClock : null, sh ? sight!.ToArray() : [], ws ? wardClock : null, ws ? observedWards!.ToArray() : [],
                est ? estimateClock : null, est ? estimates!.Where(w => w.Left > clock - estimateClock)
                    .OrderBy(w => w.Rank).ThenByDescending(w => w.Share).Take(12).Select(w => new FarmWardEstimate(w.Hero,
                    w.Kind, w.Rank, w.Share, -9472 + 18944 * w.X, 9472 - 18944 * w.Y, estimateClock - w.Age, estimateClock + w.Left, w.Rough)).ToArray() : [])
                { LaneWaves = CurrentLaneWaves(frame) };
            return new(Copy(own), session!, frameSequence, sequence, intel) { FarmScope = farmScope };
        }
    }
    // The separate, explicitly image-enabled coach still obtains its participant and evidence cursor from
    // this reducer. Only the original player's frame may supply pixels. Never attach team/replay data here.
    public MatchFarmSlice? ForVision(FrameView frame)
    {
        lock (gate) return CanAudit && Matches(frame) && !paused && phase == "playing" && own is { Alive: true } &&
            frame.Meta.Source == "dota2" ? new(Copy(own), session!, frameSequence, sequence, null) : null;
    }
    // Record the adapter's exact frozen input, not the most recent hero sample or a recomputed scenario.
    // A pre-send attempt can still be cancelled or fail in transport; it is not evidence of provider receipt.
    internal async Task NoteModelInput(MatchFarmSlice input, JsonElement data, string? providerId, string? modelId, CancellationToken cancellation)
    {
        MatchArchive RequireOrigin() // under gate; a newer continuous frame is fine, a gap or a new minute is not
        {
            cancellation.ThrowIfCancellationRequested();
            if (!CanAudit || archive == null) throw new IOException("No writable minute audit; request not sent.");
            if (!Fresh() || paused || phase != "playing" || input.FarmScope != farmScope || input.Session != session ||
                input.Own.MatchId != match || input.Own.Team != team || input.Own.Hero != hero ||
                input.FrameSequence <= 0 || input.FrameSequence > frameSequence || input.FactSequence < 0 || input.FactSequence > sequence ||
                input.Own.Clock > clock || !MinuteFarmContext.Eligible(own) || !MinuteFarmContext.Continues(input.Own, own!))
                throw new OperationCanceledException("Minute input no longer belongs to the active planning sample.", cancellation);
            return archive;
        }
        long accepted;
        MatchArchive sink;
        lock (gate) {
            sink = RequireOrigin();
            bool shared = input.TeamKnowledge != null;
            if (data.ValueKind != JsonValueKind.Object || data.TryGetProperty("teamKnowledge", out _) != shared)
                throw new ArgumentException("Minute input and frozen sharing selection disagree.");
            var audit = new Dictionary<string, object> { ["inputSequence"] = input.FactSequence, ["inputClock"] = input.Own.Clock,
                ["includesTeamKnowledge"] = shared, ["input"] = data };
            var (provider, model) = MatchArchive.MinuteTarget(providerId, modelId);
            if (provider != null) audit.Add("providerId", provider);
            if (model != null) audit.Add("modelId", model);
            if (!Write(clock, "coach", "farm-input", audit))
                throw new IOException("Minute input was not accepted; request not sent.");
            accepted = sequence;
        }
        // This once-per-paid-attempt durability barrier does not block the dispatcher or the live reducer.
        try {
            if (await Task.Run(() => sink.LastSequence(input.Own.MatchId), cancellation).ConfigureAwait(false) < accepted)
                throw new IOException("Minute input not durable.");
        } catch (Exception e) when (e is IOException or UnauthorizedAccessException or ObjectDisposedException) {
            lock (gate) UseLiveOnly(e);
            throw;
        }
        lock (gate) RequireOrigin();
    }
    public void NoteDecision(MatchFarmSlice input, string status, FarmStop[]? stops = null, string? chosen = null, string[]? offered = null)
    {
        lock (gate) {
            if (!Fresh() || input.Session != session || input.Own.MatchId != match || input.Own.Team != team ||
                input.Own.Clock > clock || status is not ("requested" or "published" or "abstained" or "expired" or "failed" or "no-options" or
                    "image-requested" or "image-published" or "image-failed")) return;
            int minute = (int)(input.Own.Clock / 60);
            if (lastDecision == (minute, status) && status == "no-options") return;
            lastDecision = (minute, status);
            Write(clock, "coach", "farm-decision", new { status, minute, inputClock = input.Own.Clock, inputSequence = input.FactSequence,
                includesTeamKnowledge = input.TeamKnowledge != null, chosen, offered = offered ?? [], stops = stops ?? [] });
        }
    }
}
