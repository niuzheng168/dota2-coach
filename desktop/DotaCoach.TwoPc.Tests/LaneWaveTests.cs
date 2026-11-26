using DotaCoach.TwoPc;
using System.Text.Json;

internal static class LaneWaveTests
{
    private const string Match = "9000000100", Game = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS";
    private static MatchWitness Witness(double t, int side = 2) =>
        new(Match, side == 2 ? "radiant" : "dire", "npc_dota_hero_luna", t, "playing", false) { InstalledBuild = 6989 };
    private static SpectatorSnapshot Packet(double t, params MinimapUnit[] units) =>
        new(10_000, "team-view", t, Game, false, Match, 2, 0, units, [], []);
    private static MinimapUnit C(int team, double x, double y) => new($"npc_dota_creep_{(team == 2 ? "good" : "bad")}guys_melee", team, x, y, null, 750, null);
    private static LaneWaveReading Mid(params MinimapUnit[] units) => LaneWave742.Observe(Witness(12), Packet(12, units), 10_000)!.Lanes[1];
    public static void Run(Action<bool, string> check)
    {
        var first = LaneWave742.Baseline(0);
        check(first.Select(x => x.Lane).SequenceEqual(["top", "mid", "bot"]) && first.All(x => x.SpawnClock == 0) &&
            first[0].Meeting is { WorldX: < -4500, WorldY: > 4000 } && first[2].Meeting is { WorldX: > 4500, WorldY: < -4000 } &&
            Math.Abs(first[1].Meeting.WorldX) < 1000 && first[1].Meeting.Clock < first[0].Meeting.Clock,
            "six build-6989 directional lane paths yield distinct opening contact zones; mid arrives before the sides");
        foreach (var b in first) {
            var due = LaneWave742.Baseline(b.Meeting.Clock).Single(x => x.Lane == b.Lane);
            var next = LaneWave742.Baseline(b.Meeting.Clock + .25).Single(x => x.Lane == b.Lane);
            check(due == b && next.SpawnClock == 30 && next.Meeting == (b.Meeting with { Clock = b.Meeting.Clock + 30 }),
                $"{b.Lane}: keep the still-due wave even across :30; roll forward only after nominal contact");
        }
        var a = C(2, -363, -254); var bmid = C(3, -185, -109);
        var close = Mid(a, bmid, a, C(2, -3500, -3200));
        check(close is { Basis: "seen-close", RadiantSeen: 2, DireSeen: 1, Meeting: { Clock: 12, WorldX: -274 } },
            "current nearby opponents are measured in world XY; a reinforcement far behind is not averaged into the front");
        var near = Mid(C(2, -1920, -1650), C(3, 1620, 1220));
        var pushed = Mid(C(2, -700, -600), C(3, 1620, 1220));
        check(near is { Basis: "two-visible-fronts", Meeting.Clock: > 12 } && pushed is { Basis: "two-visible-fronts", Meeting: not null } &&
            pushed.Meeting.Clock < near.Meeting!.Clock && pushed.Meeting.WorldX > near.Meeting.WorldX + 350,
            "moving a visible front changes both time and place rather than selecting a static mid landmark");
        check(Mid(C(2, 2400, 1800), C(3, -1800, -1470)) is { Basis: "unresolved", Meeting: null } &&
            Mid(a, bmid, C(2, 2850, 2200), C(3, 2780, 2120)) is { Basis: "ambiguous", Meeting: null },
            "already crossed fronts and widely separated simultaneous contacts do not yield one fabricated meeting");
        var only = Mid(C(2, -1920, -1650));
        check(only is { Basis: "one-visible-plus-assumed-spawn", RadiantSeen: 1, DireSeen: 0, AssumedSpawnClock: 0, Meeting.Clock: > 12 } &&
            Mid(C(3, 1620, 1220)) is { Basis: "one-visible-plus-assumed-spawn", RadiantSeen: 0, DireSeen: 1 },
            "one-sided sightings may use a named hypothetical enemy spawn, never a witnessed enemy wave");
        check(Mid(C(2, 0, 0), C(3, 8500, 8500), C(2, -1920, -1650) with { Team = 3 },
            a with { Name = "npc_dota_neutral_centaur_khan" }, bmid with { Name = bmid.Name + "_diretide" }, a with { X = double.NaN },
            a with { Vision = 0 }, bmid with { Vision = null })
            is { Basis: "unseen", RadiantSeen: 0, DireSeen: 0, Meeting: null }, "reject fog placeholders, bad team/name/coords, neutrals and off-lane creeps");
        var s = Packet(12, a, bmid);
        foreach (var bad in new[] { s with { Mode = "full-spectator" }, s with { Mode = "player" }, s with { Paused = true },
            s with { OwnTeam = 3 }, s with { GroupedPlayers = 1 }, s with { MatchId = "99999999" }, s with { Clock = 12.1 },
            s with { Clock = 9 }, s with { ReceivedAt = 6999 }, s with { ReceivedAt = 10001 } })
            check(LaneWave742.Observe(Witness(12), bad, 10_000) == null, "reject wrong/stale/future wave source");
        long now = 10_000; long seq = 0;
        FrameView Frame(double t, bool pause = false, int? build = 6989) =>
            new(new("session", "ticket", ++seq, "dota2-gsi", 1280, 720, null, null,
                Witness(t) with { Paused = pause, InstalledBuild = build }), [], now, now);
        var state = new LiveMatchState(null, () => now); var frame = Frame(12); state.ObserveFrame(frame);
        state.ObserveLaneWaves(frame, s);
        var frozen = state.CurrentLaneWaves(frame)!;
        state.ObserveLaneWaves(frame, s with { Minimap = [] });
        check(state.CurrentLaneWaves(frame)?.Lanes[1] == close with { RadiantSeen = 1 } && frozen.Lanes[1].Basis == "seen-close",
            "actual local coach projection works even without archive/model; repeated clocks do not replace a sample");
        frame = Frame(13); state.ObserveFrame(frame); state.ObserveLaneWaves(frame, Packet(13, a));
        var hud = MinimapLayout.Suggest(1280,720,true);
        var disappear = state.ForGame(frame,false,false,hud,true)?.LaneMap;
        check(state.CurrentLaneWaves(frame)?.Lanes[1] is { DireSeen: 0, Basis: not "seen-close" } && frozen.Lanes[1].Basis == "seen-close" &&
            disappear != null && disappear.Marks.Any(x => x.Kind == "last-clash" && x.SeenAt == 12) &&
            !disappear.Marks.Any(x => x.Kind == "fog" && x.Team == 3),
            "a missing creep next to a visible ally isn't drawn as a new ghost; dated clash memory and original model slice remain distinct");
        now += 3000; frame = Frame(14); state.ObserveFrame(frame);
        check(state.CurrentLaneWaves(frame) == null, "fresh participant frames cannot renew an expired Plus wave lease");
        frame = Frame(15, build: 6975); state.ObserveFrame(frame); state.ObserveLaneWaves(frame, Packet(15, a) with { ReceivedAt = now }, true);
        check(state.CurrentLaneWaves(frame) == null, "explicitly mismatched build cannot be overruled by confirmation");
        frame = Frame(16); state.ObserveFrame(frame); state.ObserveLaneWaves(frame, Packet(16, a) with { ReceivedAt = now });
        state.WithdrawTeam(); check(state.CurrentLaneWaves(frame) == null, "mode/side/session discontinuity withdraws wave evidence");
        Dynamic(check);
        Console.WriteLine("Empty-lane opening (conditional): " + string.Join("; ", first.Select(b => $"{b.Lane} {b.Meeting.Clock}s ({b.Meeting.WorldX},{b.Meeting.WorldY})")));
    }

    private static void Dynamic(Action<bool,string> check)
    {
        var tracker = new LaneWave742.Tracker();
        foreach (int t in new[]{0,19,23,30,95,605,1320,1827,2879,4500,7199,43200}) {
            var m = tracker.Project(t,false);
            check(m.Valid() && m.Marks.Count(v => v.Kind=="baseline") == 3 &&
                m.Marks.All(v => v.SeenAt == null && (v.Kind != "march" || v.Spawn <= t && t-v.Spawn <= 90)),
                $"clock-only live join at {t}s: all three roads, no fabricated individual or future spawn");
        }
        var at23 = tracker.Project(623,false);
        check(at23.Marks.Any(m => m is { Lane:"mid", Kind:"contested", Spawn:600, Meeting:618 }) &&
            at23.Marks.Any(m => m is { Lane:"mid", Kind:"baseline", Spawn:630, Meeting:648 }),
            "at 10:23 distinguish possible surviving 10:00 fight from next same-spawn contact");
        LaneMap742 Tick(int t, params MinimapUnit[] units) {
            var pkt = Packet(t,units) with { ReceivedAt=t*1000L };
            LaneWave742.Observe(Witness(t),pkt,pkt.ReceivedAt,tracker); return tracker.Project(t,true);
        }
        Tick(1240,C(3,2648,1936)); var seen = Tick(1241,C(3,2400,1745));
        var first=Tick(1242).Marks.Single(m=>m.Kind=="fog"); var second=Tick(1243).Marks.Single(m=>m.Kind=="fog");
        check(seen.Marks.Single(m=>m.Kind=="seen") is { X:2400,Y:1745,Team:3 } &&
            first is { Lane:"mid", SeenAt:1241, LastX:2400, LastY:1745 } && first.X<2400 && second.X<first.X && second.Radius>first.Radius,
            "at 20:42 a recently visible individual marches on along its own route; uncertainty grows");
        check(!tracker.Project(1243,false).Marks.Any(m=>m.Kind=="fog" || m.SeenAt!=null),
            "clock-only projection cannot inherit team history");
        var correction=Tick(1244,C(3,second.X-130,second.Y-90));
        check(correction.Marks.Count(m=>m.Team==3 && m.Kind is "seen" or "fog")==1 &&
            correction.Marks.Any(m=>m.Kind=="seen" && m.X==second.X-130),"reappearance snaps to actual XY without a duplicate ghost");
        for(int t=1245;t<=1257;t++) Tick(t);
        check(!tracker.Project(1257,true).Marks.Any(m=>m.Kind=="fog"),"long-hidden individuals expire; scheduled waves may continue");
        Tick(2400,C(3,2700,2000)); var lost=Tick(2401);
        check(lost.Marks.Any(m=>m.Kind=="fog") && tracker.Project(2401,false).Marks.All(m=>m.SeenAt==null),
            "40-minute rejoin starts a clean tracker; there is no opening-only switch");
        var fresh=Tick(2402,C(2,2600,1960));
        check(!fresh.Marks.Any(m=>m.Kind=="fog"),"fresh allied witnesses near a missing track suppress an unconfirmed survivor");
        var src = lost.Marks.Single(m=>m.Kind=="fog");
        var good = new LaneMap742(2401,[src],1000);
        check(good.Valid() && good.Age(700)?.Age(300)==null &&
            !(good with { Marks=[src with { X=10000 }] }).Valid() && !(good with { Marks=[src with { SeenAt=2402 }] }).Valid() &&
            !(good with { Marks=[src with { Kind="seen" }] }).Valid() && !(good with { Marks=[src with { LastY=null }] }).Valid(),
            "bounded typed wire marks: no precision promotion, future evidence, missing anchor or refresh-by-poll");
        var f=Tick(2500,C(3,2400,1800),C(2,2150,1670));
        for(int i=2501;i<=2520;i++) f=Tick(i);
        check(f.Marks.Any(m=>m.Kind=="last-clash" && m.Lane=="mid" && m.SeenAt==2500 && m.X>2000) &&
            !f.Marks.Any(m=>m.Kind is "held" or "baseline" or "fog"),
            "41:40 deep observed fight outlives individual tracks only as a dated clash zone; with the team's view no " +
            "reinforcement or empty-lane guess (right 20-24 % and 3-11 % on the 2026-11-25 games)");
        for(int i=2521;i<=2531;i++) f=Tick(i);
        check(!f.Marks.Any(m=>m.Kind is "held" or "last-clash"),"a historical fight is not immortal");
        Hypotheses(check);
    }

    // What the team view can trust, each measured on the 2026-11-25 games' debug against their replays (--lane-live-eval).
    private static void Hypotheses(Action<bool,string> check)
    {
        (LaneMap742 Map, LaneWaveReading Mid) Tick(LaneWave742.Tracker tracker, int t, double ahead, params MinimapUnit[] units) {
            var pkt = Packet(t,units) with { ReceivedAt=t*1000L };
            var sample = LaneWave742.Observe(Witness(t),pkt,pkt.ReceivedAt,tracker)!;
            return (tracker.Project(t,true,ahead), sample.Lanes[1]);
        }
        // Each 0:00 wave's centroid at 0:10 on the Plus clock, median of five replays (builds 6989 and 6996).
        var replays = new Dictionary<(string, int), (int X, int Y)> {
            [("top",2)] = (-6391,-1250), [("mid",2)] = (-2613,-2225), [("bot",2)] = (-152,-6438),
            [("top",3)] = (-326,5874), [("mid",3)] = (2211,1614), [("bot",3)] = (6119,790) };
        var opening = new LaneWave742.Tracker().Project(10,false).Marks.Where(m => m.Kind == "march").ToArray();
        check(opening.Length == 6 && opening.All(m => m.Spawn == 0 && double.Hypot(m.X - replays[(m.Lane,m.Team)].X, m.Y - replays[(m.Lane,m.Team)].Y) <= 400),
            "a fresh wave starts spread up its lane, the safe lanes' far ahead: each △ at 0:10 is where the replays had that wave");
        var empty = Tick(new(), 10, 0).Map;
        var dire = empty.Marks.Where(m => m.Kind == "march").ToArray();
        check(dire.Length == 3 && dire.All(m => m.Team == 3 && m.Spawn == 0) && !empty.Marks.Any(m => m.Team == 0),
            "with the team's view no △ for our own waves (we see them) and no empty-lane meeting guess (right 3-11 %)");
        var mid = dire.Single(m => m.Lane == "mid");
        var watched = Tick(new(), 10, 0, new MinimapUnit("npc_dota_hero_luna", 2, mid.X - 300, mid.Y, null, 1200, null)).Map;
        var warded = Tick(new(), 10, 0, new MinimapUnit("npc_dota_observer_wards", 2, mid.X - 1000, mid.Y, null, 1600, null)).Map;
        check(!watched.Marks.Any(m => m is { Kind:"march", Lane:"mid" }) && watched.Marks.Count(m => m.Kind == "march") == 2 &&
            warded.Marks.Count(m => m.Kind == "march") == 3,
            "a hidden wave can't stand in our sight: a hero watching that spot removes its △ (inside sight it was right 4-6 %); " +
            "a ward or hero counts only to half its radius (trees and cliffs: past that creeps were visible 23-90 %)");
        var seen = new LaneWave742.Tracker(); Tick(seen, 10, 0, C(3, mid.X, mid.Y));
        var gone = Tick(seen, 11, 0).Map; var later = gone;
        for (int t = 12; t <= 30; t++) later = Tick(seen, t, 0).Map;
        check(gone.Marks.Any(m => m is { Kind:"fog", Lane:"mid", Team:3 }) && !gone.Marks.Any(m => m is { Kind:"march", Lane:"mid" }) &&
            !later.Marks.Any(m => m is { Kind:"fog" } or { Kind:"march", Lane:"mid", Spawn:0 }) && later.Marks.Any(m => m is { Kind:"march", Lane:"mid", Spawn:30 }),
            "an enemy wave once seen is followed from its sighting, never again by schedule; the next one still is");
        var still = new LaneWave742.Tracker(); Tick(still, 3000, 0, C(3,2400,1745));
        var shown = Tick(still, 3001, 1, C(3,2400,1745)).Map; Tick(still, 3002, 0);
        var now = still.Project(3002, true).Marks.Single(m => m.Kind == "fog"); var next = still.Project(3002, true, 1).Marks.Single(m => m.Kind == "fog");
        check(shown.Marks.Single(m => m.Kind == "seen") is { X:2400, Y:1745 } && double.Hypot(now.X - 2400, now.Y - 1745) is > 250 and < 400 &&
            double.Hypot(next.X - now.X, next.Y - now.Y) is > 250 and < 400 && next.X < now.X,
            "a hidden creep marches on at full speed though it stood still when last seen; a hypothesis is placed the given " +
            "seconds ahead for the game's late display, a sighting stays where it was seen");
        var (far, farMid) = Tick(new(), 3012, 1, C(2,-1920,-1650), C(3,1620,1220));
        var (near, nearMid) = Tick(new(), 3012, 1, C(2,-700,-600), C(3,1620,1220));
        check(farMid is { Basis:"two-visible-fronts", Meeting.Clock: > 3017 } && !far.Marks.Any(m => m.Team == 0) &&
            nearMid is { Basis:"two-visible-fronts", Meeting.Clock: <= 3017 } && near.Marks.Any(m => m is { Kind:"forecast", Lane:"mid", Meeting: <= 3017 }),
            "with the team's view a × only for a meeting at most 5 s away (right 78-82 %; 6-10 s away 46-47 %)");
    }

    // Offline only. The oracle determines the first observed <=500 separation by first-wave spawn IDs
    // independently of lane projection. Only the fixed earlier positions (not truth) are given to Observe.
    internal sealed record Unit(string Name, int Team, double X, double Y);
    internal sealed record Actual(string Lane, double Clock, double X, double Y);
    internal sealed record Opening(string Match, double Clock, Unit[] Units, Actual[] Truth);
    public static void Evaluate(string path) {
        var cases = JsonSerializer.Deserialize<Opening[]>(File.ReadAllText(path), Wire.Json)!;
        var errors = new Dictionary<string, List<(double Time, double Distance)>>();
        foreach (var m in cases) foreach (string mask in new[] { "empty", "both", "radiant-only", "dire-only" }) {
            var units = m.Units.Where(u => mask == "both" || (mask == "radiant-only" && u.Team == 2) || (mask == "dire-only" && u.Team == 3))
                .Select(u => C(u.Team, u.X, u.Y) with { Name = u.Name }).ToArray();
            var witness = Witness(m.Clock) with { MatchId = m.Match };
            var obs = LaneWave742.Observe(witness, Packet(m.Clock, units) with { MatchId = m.Match }, 10_000)!;
            var baseRows = LaneWave742.Baseline(m.Clock);
            foreach (var truth in m.Truth) {
                var prediction = mask == "empty" ? baseRows.Single(l => l.Lane == truth.Lane).Meeting : obs.Lanes.Single(l => l.Lane == truth.Lane).Meeting;
                if (prediction == null) continue;
                if (!errors.TryGetValue(mask, out var list)) errors.Add(mask, list = []);
                list.Add((Math.Abs(truth.Clock - prediction.Clock), double.Hypot(truth.X - prediction.WorldX, truth.Y - prediction.WorldY)));
            }
        }
        static double Median(IEnumerable<double> v) { var a = v.Order().ToArray(); return (a[(a.Length - 1) / 2] + a[a.Length / 2]) / 2; }
        foreach (var (mask, rows) in errors) Console.WriteLine($"{mask}: {rows.Count}/{cases.Sum(c => c.Truth.Length)}; " +
            $"median / max absolute error: {Median(rows.Select(r => r.Time)):0.0} / {rows.Max(r => r.Time):0.0} game seconds, " +
            $"{Median(rows.Select(r => r.Distance)):0} / {rows.Max(r => r.Distance):0} world units");
    }
}
