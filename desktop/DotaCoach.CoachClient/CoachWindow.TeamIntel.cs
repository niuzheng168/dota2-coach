using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DotaCoach.TwoPc;

internal sealed partial class CoachWindow
{
    private readonly CheckBox enemyWards = new() { Content = "可能的敌方眼位（推测）", Foreground = Brushes.White, Margin = new Thickness(0, 0, 16, 4) };
    private readonly WardTracker wardTracker = new(CoachMap.Bundled(), WardSpots.Bundled());
    private readonly TeamScanCursor scanCursor = new();
    private readonly object sightGate = new();
    private readonly Queue<SpectatorSnapshot> sightPackets = new();
    private (string Session, string Match, string Team, string Hero)? sightScope;
    private long receivedSightAt = -1, sightGeneration, processedGeneration;
    private SpectatorSnapshot? latestSight;
    private string? scannerRun;
    private sealed record ScanInput(ScanStamp Stamp, EnemyBoard Board, IReadOnlyDictionary<string, WardReading[]> Reads);
    private ScanInput? pendingScan;
    private double lastEstimateAt;
    private readonly HashSet<string> seenAllies = new(StringComparer.Ordinal);
    private int? scannedHeroes;
    private int wardCount;
    // What the game machine shows: last-known rows for the paired player's match, independent of the archive's
    // strict per-sample timing, so a late scan write or a Plus packet gap never blanks the panel.
    private readonly LastKnownBoard knownBoard = new();

    /// <summary>The paired player's match, team, hero and clock in its latest frame (also while paused or dead).</summary>
    private static (string Match, string Team, string Hero, double Clock, string Phase)? PanelScope(FrameView? frame)
    {
        if (frame?.Meta is not { Source: "dota2" or "dota2-gsi" } m) return null;
        if (m.MatchPhase is { } w && TeamViewEvidence.RealMatch(w.MatchId) && w.Team is "radiant" or "dire")
            return (w.MatchId, w.Team, w.Hero, w.Clock, w.Phase);
        return m.Player is { } p && TeamViewEvidence.RealMatch(p.MatchId) && p.Team is "radiant" or "dire"
            ? (p.MatchId, p.Team, p.Hero, p.Clock, "playing") : null;
    }

    /// <summary>Receiver threads: queue each authenticated, matching team packet once. A switch to
    /// full spectator (even briefly in the same match) or loss of continuity fences pending data.
    /// All MatchState writes, including vision loss, run on the UI tick after participant ingestion.</summary>
    private void ObserveTeamSight(SpectatorSnapshot s)
    {
        var frame = host?.Mailbox.Latest();
        lock (sightGate) {
            if (s.ReceivedAt < receivedSightAt) return; // HTTP callbacks can complete out of order
            receivedSightAt = s.ReceivedAt;
            long now = Environment.TickCount64;
            var own = TeamViewEvidence.Participant(frame, now);
            if (e2e != null || !TeamViewEvidence.Matches(own, s, now)) {
                if (sightScope != null) { sightPackets.Clear(); sightGeneration++; sightScope = null; }
                latestSight = null;
                return;
            }
            var scope = (frame!.Meta.SessionId, own!.MatchId, own.Team, own.Hero);
            if (sightScope != scope || latestSight == null || s.Clock < latestSight.Clock || s.Clock - latestSight.Clock > TeamViewEvidence.MaxLagSeconds ||
                s.ReceivedAt - latestSight.ReceivedAt >= TeamViewEvidence.FreshMs || sightPackets.Count >= 32) {
                sightPackets.Clear(); sightGeneration++;
            }
            sightScope = scope;
            latestSight = s;
            sightPackets.Enqueue(s);
        }
    }

    /// <summary>Parent calls after matchState.ObserveFrame on every tick, in every display/model mode.
    /// No delivery here. Unit positions are WORLD units; WardTracker.Marks alone returns minimap 0..1.</summary>
    private void UpdateMatchIntel(FrameView? frame)
    {
        if (e2e != null) return;
        long now = Environment.TickCount64;
        SpectatorSnapshot? plus;
        SpectatorSnapshot[] packets;
        long generation;
        string? session;
        lock (sightGate) {
            plus = latestSight; generation = sightGeneration; session = sightScope?.Session;
            packets = sightPackets.ToArray(); sightPackets.Clear();
        }
        if (processedGeneration != generation) {
            processedGeneration = generation;
            wardTracker.Clear(); visionLoss.Clear(); alerts.Clear();
            seenAllies.Clear(); scannedHeroes = null; pendingScan = null; scannerRun = null; wardCount = 0;
            lastEstimateAt = double.NegativeInfinity; lastAlert = panelText = "";
            matchState.WithdrawTeam(); // also covers a brief full view between two UI ticks
        }
        var scope = PanelScope(frame);
        if (scope is { Phase: "postgame" }) knownBoard.Clear();
        else if (scope is { } participant) knownBoard.Participant(participant.Match, participant.Team, participant.Clock, now);
        var own = TeamViewEvidence.Participant(frame, now);
        bool matched = own != null && own.MatchId == matchState.ActiveMatch && session == frame?.Meta.SessionId &&
            TeamViewEvidence.Matches(own, plus, now);
        bool poll = now >= nextScanPoll;
        long unix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        (ScanStatus Status, EnemyBoard? Board, IReadOnlyDictionary<string, WardReading[]> Wards)? scan = null;
        if (poll) {
            nextScanPoll = now + 250; UpdateScanButton();
            if (knownBoard.Match is { } panelMatch && scope is { } current) {
                var read = ScanFeed.Poll(scanFile, panelMatch, unix);
                scan = read; scanStatus = read.Status;
                // Any fresh write of this match and side updates the panel; rows it hasn't read keep their last reading.
                if (read.Board != null && read.Status.Sample is { } sample && TeamViewEvidence.Side(sample.OwnTeam) == knownBoard.Team)
                    knownBoard.Equipment(panelMatch, sample.Clock,
                        read.Board.Heroes.Where(h => h.Hero != current.Hero && !seenAllies.Contains(h.Hero)).ToArray());
            }
            panelText = (scan?.Status ?? scanStatus).Text + (knownBoard.Read > 0 ? $" · 游戏端面板 {knownBoard.Read} 名敌方英雄" : "");
        }
        if (!matched || own == null) {
            matchState.WithdrawTeam();
            pendingScan = null;
            alerts.Clear(); lastAlert = "";
            if (knownBoard.Read == 0) panelText = "等待匹配的游戏机和新鲜 Plus 队伍视角";
            ShowTeamStatus(false);
            return;
        }
        foreach (var s in packets) {
            if (!TeamViewEvidence.Matches(own, s, now)) continue;
            // A packet already in the player's next second is known by the player's current one: never a future fact.
            double at = Math.Min(s.Clock!.Value, own.Clock);
            var heroes = TeamViewEvidence.VisibleHeroes(s)
                .Select(u => new MatchSightHero(u.Name, TeamViewEvidence.Side(u.Team)!, u.X, u.Y)).ToArray();
            foreach (var h in heroes.Where(h => h.Team == own.Team)) if (seenAllies.Count < 5) seenAllies.Add(h.Hero);
            matchState.ObserveSight(own.MatchId, own.Team, at, heroes);
            var wards = TeamViewEvidence.VisibleWards(s)
                .Select(u => new ObservedWard(u.Name == "npc_dota_observer_wards" ? "observer" : "sentry",
                    TeamViewEvidence.Side(u.Team)!, u.X, u.Y)).ToArray();
            matchState.ObserveWards(own.MatchId, own.Team, at, wards);
            knownBoard.Seen(own.MatchId, wards, now);
            // Empty observed lists say only "not visible in this packet", never placed/dewarded.
            if (wardTracker.Sight(s, own.Clock))
                StoreEstimates(own, Math.Max(lastEstimateAt, wardTracker.Clock)); // never renew equipment from sight alone
            foreach (var a in visionLoss.Update(s, heroNames)) {
                var note = matchState.NoteTeam(own.MatchId, own.Team, at, "vision-alert", a.Hero, a.Text);
                if (note != null && visionAlertsOn && LegacySubtitles(frame)) alerts.Add([note], now);
            }
        }
        if (poll) {
            var scanned = knownBoard.Match == own.MatchId && scan is { } shared ? shared : ScanFeed.Poll(scanFile, own.MatchId, unix);
            scanStatus = scanned.Status;
            if (scanned.Board == null || scanned.Status.Sample is not { } stamp) {
                // The scanner stopped, lost the team view or watches another game: its archived reads end here.
                if (scannedHeroes.HasValue)
                    matchState.ObserveTeam(own.MatchId, own.Team, Math.Max(plus!.Clock!.Value, lastEstimateAt), null, authoritative: false);
                scannedHeroes = null; pendingScan = null; wardCount = 0;
            } else if (!TeamScanCursor.Fits(stamp, own, plus, now, unix, allowNextSecond: true) ||
                scanned.Board.Heroes.Any(h => h.Hero == own.Hero || seenAllies.Contains(h.Hero))) {
                // A write a second ahead of the player's GSI (or naming an ally) is skipped, not a withdrawal:
                // the next write is a quarter second away, and the archive's own hold expires a stopped source.
                pendingScan = null;
            } else {
                // The scanner dates within a second; participant GSI has whole seconds. Hold at most
                // ONE same-scope sample briefly for that clock edge. No reads/marks/notes until Fits strictly.
                if (pendingScan is { } p && (stamp.Run != p.Stamp.Run || unix - p.Stamp.WrittenMs >= TeamViewEvidence.FreshMs ||
                    p.Board.Heroes.Any(h => h.Hero == own.Hero || seenAllies.Contains(h.Hero)))) pendingScan = null;
                if (pendingScan is { } ready && TeamScanCursor.Fits(ready.Stamp, own, plus, now, unix)) {
                    ConsumeScan(own, frame!, now, ready); pendingScan = null;
                }
                var input = new ScanInput(stamp, scanned.Board, scanned.Wards);
                if (TeamScanCursor.Fits(stamp, own, plus, now, unix)) ConsumeScan(own, frame!, now, input);
                else pendingScan ??= input;
            }
            panelText = (scanned.Board == null ? scanStatus.Text : scannedHeroes == null ? "等待匹配的扫描时间和阵营" :
                $"已记录 {scannedHeroes} 名敌方英雄 · 推测眼位 {wardCount} 处") +
                (knownBoard.Read > 0 ? $" · 游戏端面板 {knownBoard.Read} 名" : "");
        }
        ShowTeamStatus(true);
    }

    private void ConsumeScan(MatchWitness own, FrameView frame, long now, ScanInput scan)
    {
        var stamp = scan.Stamp;
        // Disk/queue time is part of freshness. A receiver callback can also invalidate this view during Poll.
        long consumeAt = Environment.TickCount64;
        lock (sightGate)
            if (processedGeneration != sightGeneration || sightScope?.Session != frame.Meta.SessionId ||
                !TeamScanCursor.Fits(stamp, TeamViewEvidence.Participant(frame, consumeAt), latestSight, consumeAt,
                    DateTimeOffset.UtcNow.ToUnixTimeMilliseconds())) return;
        if (!scanCursor.Take(stamp)) return;
        if (scannerRun != stamp.Run) { wardTracker.ForgetReadings(); scannerRun = stamp.Run; }
        // A later corroborating track point can date the inference, not the inventory.
        matchState.ObserveEquipment(own.MatchId, own.Team, stamp.Clock, scan.Board.Heroes);
        scannedHeroes = scan.Board.Heroes.Length;
        if (!scan.Reads.Values.Any(r => r.Length > 0)) return; // uncalibrated/didn't read counts, not "no wards"
        foreach (var (hero, reads) in scan.Reads)
            if (reads.Length > 0) matchState.ObserveWardReadings(own.MatchId, own.Team, stamp.WardClock, hero, reads);
        double knownAt = double.IsFinite(wardTracker.Clock) ? Math.Max(stamp.WardClock, wardTracker.Clock) : stamp.WardClock;
        var found = wardTracker.Readings(own.MatchId, stamp.OwnTeam, scanCursor.UnprocessedChanges(scan.Reads));
        StoreEstimates(own, knownAt);
        foreach (var g in found) {
            string text = WardText(g);
            var note = matchState.NoteTeam(own.MatchId, own.Team, knownAt, "ward-alert", g.Hero, text);
            if (note != null && preferences.EnemyWards && LegacySubtitles(frame) && knownAt - g.Before <= TeamViewEvidence.MaxLagSeconds)
                alerts.Add([note], now);
        }
    }
    private void StoreEstimates(MatchWitness own, double knownAt)
    {
        var marks = wardTracker.Marks(knownAt);
        matchState.ObserveEstimates(own.MatchId, own.Team, knownAt, marks);
        knownBoard.Estimates(own.MatchId, marks, knownAt);
        lastEstimateAt = knownAt;
        wardCount = marks.Count(m => m.Rank == 0);
    }

    private string WardText(WardGuess g) =>
        $"{heroNames[g.Hero]} 少了{g.Count}个{g.Kind switch { "observer" => "假眼", "sentry" => "真眼", _ => "守卫" }}，推测可能插眼" +
        (g.Unseen >= WardTracker.UnsureSeconds || g.Candidates.Length == 0 ? " · 位置不明" :
            $" · 可能在{g.Candidates[0].Place}{(g.Candidates[0].Rough ? "一带" : "")}") +
        $"（读数 {g.After:0.##}–{g.Before:0.##} 秒）";

    // Plus alerts are markerless text: they reach the GSI-only game client too. A live minute route keeps its slot (UpdateAlerts).
    private bool LegacySubtitles(FrameView? frame) => e2e == null && frame?.Meta.Source is "dota2" or "dota2-gsi";

    private void ShowTeamStatus(bool matched, string delivered = "")
    {
        alertStatus.Text = (matched ? "Plus 队伍视角" : "等待 Plus 队伍视角（完整观战不推送）") +
            "\n" + (!panelOn ? "对手面板已关 · " : "") + panelText + delivered +
            (lastAlert.Length > 0 ? "\n" + lastAlert : "");
    }
}
