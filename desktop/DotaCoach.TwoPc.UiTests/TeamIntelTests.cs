using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using DotaCoach.TwoPc;

internal static partial class Program
{
    private static async Task TeamIntelWithDeliveryOff(Window w, CoachHost host, string session, string live,
        HttpClient http, string token, long sequence)
    {
        const string match = "9876543210", me = "npc_dota_hero_luna", lion = "npc_dota_hero_lion", other = "npc_dota_hero_shadow_shaman";
        var state = Field<LiveMatchState>(w, "matchState");
        var archive = Field<MatchArchive>(w, "matchArchive");
        Field<CheckBox>(w, "enemyVision").IsChecked = false;
        await Wait(() => Field<CheckBox>(w, "enemyWards").IsChecked == false, "ward delivery is opt-in");
        Set(w, "preferences", Field<LiveCoachSettings>(w, "preferences") with { MinuteFarm742 = true, AutomaticAdvice = false, ModelTeamKnowledge = false });
        // Default loss timing was exercised above. Shorten the debounce here to exercise persistence rather than wait another 7s.
        Set(w, "visionLoss", new VisionLossTracker { MinVisibleMs = 0, GoneMs = 1 });
        object Unit(string n, int team, double x, double y, int vision) => new { unitname = n, team, xpos = x, ypos = y, visionrange = vision };
        var crop = new FarmPlayerTelemetry(true, true, false, Epoch: Guid.NewGuid());
        async Task Packet(int clock, object units, string side = "dire", bool full = false)
        {
            var ticket = host.Mailbox.Issue(session);
            host.Mailbox.AcceptMinuteState(new(session, ticket.Id, sequence++, "dota2-gsi", 3840, 2160,
                new(match, me, "dire", clock, true, 1200, 500, 100, 200, [], [], crop), 10,
                new(match, "dire", me, clock, "playing", false) { AgeMs = 10 }), 500);
            var packet = new { auth = new { token }, map = new { clock_time = clock, matchid = match, game_state = "DOTA_GAMERULES_STATE_GAME_IN_PROGRESS" },
                player = new { team_name = "spectator" }, hero = full ? (object)new { team2 = new { player0 = new { name = lion } } } : new { id = 0 },
                buildings = new Dictionary<string, object> { [side] = new { } }, minimap = units };
            using var reply = await http.PostAsync("/spectator", new StringContent(JsonSerializer.Serialize(packet)));
            reply.EnsureSuccessStatusCode();
        }
        FarmTeamKnowledge? Team() => host.Mailbox.Latest() is { } f ? state.ForFarm(f, true)?.TeamKnowledge : null;
        object Row(string hero, int count) => new { hero, level = 9, age = 21.25, items = new string?[] { "item_blink", null, null, null, null, null },
            neutral = (string?)null, wards = new[] { new { first = 144.7, clock = 144.8, observer = count, sentry = 0, unknown = 1 - count },
                new { first = 145.0, clock = 145.0, observer = 0, sentry = 0, unknown = 0 } } };
        var seen = new { h = Unit(lion, 2, -2500, 1200, 1800), w = Unit("npc_dota_sentry_wards", 3, 2500, -1200, 0) };
        await Packet(145, seen);
        await Wait(() => Team()?.SightObservedClock == 145, "GSI-only mode observes team sight with delivery off");
        await Packet(146, seen);
        await Wait(() => Team()?.SightObservedClock == 146, "source advances independently of scanner writes");
        File.WriteAllText(live, JsonSerializer.Serialize(new { version = 2, run = "off", stopped = false, mode = "team-view",
            matchId = match, ownTeam = 3, clock = 145, gsiAge = 0, written = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            enemies = new[] { Row(lion, 1), Row(other, 0) } }));
        await Wait(() => Team() is { EquipmentObservedClock: 145, EstimateObservedClock: 146, LastReadEquipment.Length: 2 } t &&
            t.LastReadEquipment.All(r => r.LastSeenClock == 123.75) && t.PossibleEnemyWards.Any(m => m.Hero == lion) &&
            t.VisibleAtWardSample is [{ Team: "dire", Kind: "sentry", X: 2500, Y: -1200 }] &&
            host.Mailbox.Enemies(session) == null && state.ForFarm(host.Mailbox.Latest()!, false)?.TeamKnowledge == null,
            "read equipment keeps its own clock and ages, inferred marks and observed world wards stay separate; no display/model leak");
        var candidate = Team()!.PossibleEnemyWards.First(m => m.Hero == lion && m.Rank == 0);
        await Packet(147, new { actual = Unit("npc_dota_observer_wards", 2, candidate.WorldX, candidate.WorldY, 0) });
        await Wait(() => Team() is { EquipmentObservedClock: 145, EstimateObservedClock: 147, PossibleEnemyWards.Length: 0,
            VisibleAtWardSample: [{ Team: "radiant", Kind: "observer" }] }, "sighting answers the candidate without refreshing the equipment sample");
        var facts = archive.Read(match);
        await Wait(() => facts.Count(f => f.Source == "team-vision" && f.Kind == "ward-charges") == 4 &&
            facts.Any(f => f.Kind == "ward-charges" && f.Clock == 144.8 && f.Data.GetProperty("hero").GetString() == other &&
                f.Data.GetProperty("unknown").GetInt32() == 1) &&
            facts.Any(f => f.Kind == "ward-charges" && f.Clock == 145 && f.Data.GetProperty("hero").GetString() == lion &&
                f.Data.GetProperty("observer").GetInt32() == 0), "the two actual charge readings for each hero survive as the basis of each inference");
        await Wait(() => facts.Count(f => f.Kind == "ward-alert") == 2 && facts.Any(f => f.Source == "inference" && f.Kind == "ward-alert" &&
            f.Data.GetProperty("hero").GetString() == other && f.Data.GetProperty("text").GetString()!.Contains("位置不明")) &&
            facts.Any(f => f.Source == "team-vision" && f.Kind == "vision-alert" && f.Data.GetProperty("hero").GetString() == lion) &&
            host.Mailbox.Poll(session) == null, "all delivery off: persist located and unknown inferences and point-free vision loss, without subtitles");
        await Packet(148, seen, full: true);
        await Wait(() => Team() is { EquipmentObservedClock: null, SightObservedClock: null, EstimateObservedClock: null, WardSightObservedClock: null },
            "switching to full spectator immediately withdraws all live sources");
        await Packet(149, seen, side: "radiant");
        await Task.Delay(350);
        await Wait(() => Team()?.SightObservedClock == null && Team()?.EquipmentObservedClock == null, "opposite-side Plus cannot repopulate state");
        await Packet(150, seen);
        await Wait(() => Team()?.SightObservedClock == 150 && Team()?.EquipmentObservedClock == null &&
            archive.Read(match).Count(f => f.Kind == "ward-alert") == 2, "returning to team view does not replay the old scanner file or its notes");
        File.WriteAllText(live, JsonSerializer.Serialize(new { version = 2, run = "off", stopped = false, mode = "team-view",
            matchId = match, ownTeam = 3, clock = 150, gsiAge = 0, written = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000d,
            enemies = new[] { Row(lion, 1), Row(other, 0) } }));
        await Wait(() => Team()?.EquipmentObservedClock == 150 && archive.Read(match).Count(f => f.Kind == "ward-alert") == 2,
            "a genuinely new scan after a view switch can restore equipment, but not re-announce its four old charge entries");
    }
}
