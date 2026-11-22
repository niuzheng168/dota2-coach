using System.Text.Json;
using DotaCoach.TwoPc;

internal static class TeamViewEvidenceTests
{
    public static void Run(Action<bool, string> check)
    {
        const long tick = 20_000, unix = 1_800_000_000_000;
        const string match = "9000000001", lion = "npc_dota_hero_lion";
        var w = new MatchWitness(match, "radiant", "npc_dota_hero_luna", 601, "playing", false) { AgeMs = 400 };
        var f = new FrameView(new("session", "ticket", 1, "dota2-gsi", 1280, 720, null, null, w), [], tick - 100, tick);
        var s = new SpectatorSnapshot(tick, "team-view", 600, "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS", false, match, 2, 0, [], [], []);
        bool Accept(FrameView frame, SpectatorSnapshot source) =>
            TeamViewEvidence.Matches(TeamViewEvidence.Participant(frame, tick), source, tick);
        check(Accept(f, s), "a fresh phase witness identifies a participant even without alive combat telemetry");
        foreach (var bad in new[] { s with { Mode = "full-spectator" }, s with { GroupedPlayers = 1 }, s with { GameState = "menu" },
            s with { Paused = true }, s with { OwnTeam = 3 }, s with { OwnTeam = null }, s with { MatchId = null },
            s with { MatchId = "0" }, s with { MatchId = "000" }, s with { MatchId = "9000000002" },
            s with { Clock = 601.01 }, s with { Clock = 580 }, s with { ReceivedAt = tick - 3000 }, s with { ReceivedAt = tick + 1 } })
            check(!Accept(f, bad), "not live evidence: " + (bad.Mode, bad.GroupedPlayers, bad.OwnTeam, bad.MatchId, bad.Clock, bad.ReceivedAt, bad.Paused, bad.GameState));
        var bare = new OwnState(match, w.Hero, w.Team, w.Clock, true, 100, 100, 1, 1, [], []);
        foreach (var bad in new[] { f with { Meta = f.Meta with { MatchPhase = null, Player = bare, GsiAgeMs = 1 } },
            f with { Meta = f.Meta with { Source = "diagnostic" } }, f with { CapturedNoEarlierThan = tick - 2600 },
            f with { Meta = f.Meta with { MatchPhase = w with { Phase = "postgame" } } }, f with { Meta = f.Meta with { MatchPhase = w with { Paused = true } } },
            f with { Meta = f.Meta with { Player = bare with { Team = "dire" }, GsiAgeMs = 1 } } })
            check(!Accept(bad, s), "unproven/stale/terminal/conflicting participant is not a source of authority");

        MinimapUnit U(string n, int team, double x, double y, double vision = 0) => new(n, team, x, y, null, vision, null);
        var obs = U("npc_dota_observer_wards", 3, -1234, 2345);
        var sen = U("npc_dota_sentry_wards", 2, 100, 120);
        var seen = U(lion, 3, 800, -800, 1800);
        var units = s with { Minimap = [obs, obs, sen, seen, seen, U("npc_dota_hero_axe", 3, 10, 20), obs with { Team = 0 },
            obs with { X = 0, Y = 0 }, obs with { X = double.NaN }, obs with { Name = "item_ward_observer" },
            obs with { Name = "npc_dota_ward_base" }] };
        check(TeamViewEvidence.VisibleWards(units).SequenceEqual([obs, sen]) && TeamViewEvidence.VisibleHeroes(units).SequenceEqual([seen]),
            "observed wards are recognized concrete entities of either known side in WORLD coordinates, not items, dead heroes or 0,0");
        check(TeamViewEvidence.VisibleWards(s with { Minimap = Enumerable.Range(1, 100).Select(i => obs with { X = i }).ToArray() }).Count() == Wire.MaxSeenWards,
            "actual ward snapshots are bounded separately from 0..1 inferred candidates");
        var ambiguous = SpectatorGsi.Parse(JsonSerializer.SerializeToElement(new { map = new { matchid = match, clock_time = 600,
            game_state = s.GameState }, player = new { team_name = "spectator" }, buildings = new { radiant = new { }, dire = new { } },
            minimap = new { x = new { unitname = lion, xpos = 100, ypos = 100, team = 2, image = "minimap_herocircle_self" } } }), tick);
        check(ambiguous.OwnTeam == null && !Accept(f, ambiguous), "contradictory side proofs do not pick a team");
        var grouped = SpectatorGsi.Parse(JsonSerializer.SerializeToElement(new { map = new { matchid = match }, player = new { team_name = "spectator" },
            abilities = new { team2 = new { } }, buildings = new { radiant = new { } } }), tick);
        check(grouped.Mode == "full-spectator", "even a partial/empty multiplexed ability group is not team view");

        var stamp = new ScanStamp("ab", unix, match, 2, 600) { WardClock = 600.6 };
        bool Fits(ScanStamp st) => TeamScanCursor.Fits(st, w, s, tick, unix);
        check(Fits(stamp) && !Fits(stamp with { OwnTeam = 3 }) && !Fits(stamp with { MatchId = "0" }) &&
            !Fits(stamp with { WrittenMs = unix + 1 }) && !Fits(stamp with { WrittenMs = unix - 3000 }) &&
            !Fits(stamp with { WardClock = 601.5 }) && !Fits(stamp with { Clock = 500 }), "file requires its own same match/team, fresh stamp and no future read");
        var cursor = new TeamScanCursor();
        check(cursor.Take(stamp) && !cursor.Take(stamp) && !cursor.Take(stamp with { Run = "another" }) &&
            !cursor.Take(stamp with { WrittenMs = unix - 1 }) && !cursor.Take(stamp with { WrittenMs = unix + 1, Clock = 599 }) &&
            cursor.Take(stamp with { WrittenMs = unix + 500 }), "re-polling, restarting, replaying or renaming a write cannot create an observation");
        Dictionary<string, WardReading[]> repeat = new() { [lion] = [new(590, 3, 0, 0), new(594, 2, 0, 0), new(600, 1, 0, 0)] };
        check(cursor.UnprocessedChanges(repeat)[lion].Length == 3 && cursor.UnprocessedChanges(repeat)[lion] is [{ Clock: 600 }] &&
            cursor.UnprocessedChanges(new Dictionary<string, WardReading[]> { [lion] = [.. repeat[lion], new(601, 0, 0, 0)] })[lion] is [{ Clock: 600 }, { Clock: 601 }],
            "a fresh heartbeat repeating old history after a view switch supplies only a baseline, not three new declines");
        var tracker = new WardTracker(null, null);
        Dictionary<string, WardReading[]> reads = new() { [lion] = [new(590, 0, 0, 2), new(600, 0, 0, 1)] };
        check(tracker.Readings(null, 2, reads).Count == 0 && tracker.Readings("0", 2, reads).Count == 0 &&
            tracker.Readings(match, 0, reads).Count == 0 && tracker.Readings(match, 2, reads) is [{ Kind: "ward", Candidates.Length: 0 }] &&
            tracker.Marks(601).Length == 0, "unknown-kind, unlocated decline is still an inference; no invented point without a track or map");
    }
}
