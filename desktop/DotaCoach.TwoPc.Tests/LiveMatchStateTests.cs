using System.Net;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class LiveMatchStateTests
{
    public static async Task Run(Action<bool, string> check)
    {
        await MinuteModelInputAuditTests.Run(check);
        string root = Directory.CreateTempSubdirectory("match-base-test-").FullName;
        const string match = "9112345601", other = "9112345602", luna = "npc_dota_hero_luna", enemy = "npc_dota_hero_lion";
        long ms = 1000, seq = 0;
        var uid = Guid.NewGuid();
        OwnState Player(double clock) => new(match, luna, "radiant", clock, true, 1000, 450, 3978, -5027,
            [new("item_power_treads", null, 0, "slot0")], [new("luna_moon_glaive", 3, 0, "ability0")],
            new(true, true, false, MoveSpeed: 370, AttackDamage: 110, AttackSpeed: 180, Level: 11, Epoch: uid, InstalledBuild: 6989),
            new(Gold: 1371, LastHits: 96, Level: 11));
        FrameView Frame(double clock, string phase = "playing", string session = "session-A",
            string id = match, bool hasPlayer = true, string? source = "dota2-gsi") =>
            new(new(session, "privateCaptureTicket", ++seq, source!, 1920, 1080,
                hasPlayer ? Player(clock) with { MatchId = id } : null, hasPlayer ? 0 : null,
                new(id, "radiant", luna, clock, phase, false)), [], ms, ms);
        var hud = MinimapLayout.Suggest(1920, 1080, true);
        EnemyBoard Board(string item = "item_blink") => new([new(enemy, 9, [item, null, null, null, null, null], null, 4)],
            [new(enemy, "observer", .25, .75, 0, .8, 2, 80)]);
        try {
            using (var archive = new MatchArchive(root)) {
                var state = new LiveMatchState(archive, () => ms);
                var first = Frame(600); state.ObserveFrame(first);
                state.ObserveTeam(other, "radiant", 598, Board("item_dagon"));
                state.ObserveTeam(match, "dire", 599, Board("item_dagon"));
                state.ObserveTeam(match, "radiant", 601, Board("item_dagon"));
                state.ObserveTeam(match, "radiant", 530, Board("item_dagon"));
                check(state.ForGame(first, true, true, hud) == null, "wrong match, wrong team, excessively delayed or future intel cannot enter a projection");
                var b = Board();
                state.ObserveTeam(match, "radiant", 598, b);
                state.ObserveWards(match, "radiant", 597, [new("sentry", "dire", -2100, 125)]);
                state.ObserveSight(match, "radiant", 598, [new(enemy, "dire", -1600, 192)]);
                state.ObserveWardReadings(match, "radiant", 598, enemy, [new(596.5, 1, 0, 0, 595), new(597.8, 0, 0, 0, 597.6)]);
                state.NoteTeam(match, "radiant", 598, "ward-alert", enemy, "推测放下侦查守卫，非目击");
                state.NoteTeam(match, "radiant", 598, "vision-alert", enemy, "离开上路视野");
                var frozen = state.ForFarm(first, true)!;
                var ownOnly = state.ForFarm(first, false)!;
                check(state.ForGame(first, false, false, hud) == null && archive.Read(match).Any(f => f.Kind == "equipment") &&
                    ownOnly.TeamKnowledge == null && frozen.TeamKnowledge?.LastReadEquipment.Single().Items?[0] == "item_blink",
                    "recording, game display filters, and model sharing consent are independent");
                var eq = state.ForGame(first, true, false, hud)!;
                var eyes = state.ForGame(first, false, true, hud)!;
                check(eq.MatchId == match && eq.Team == "radiant" && eq.Heroes.Single().Age == 6 && eq.Wards!.Length == 0 &&
                    eyes.Heroes.Length == 0 && eyes.Wards is [{ Age: 4, Left: 78 }] && eyes.SeenWards is [{ X: -2100, Kind: "sentry" }] &&
                    state.ForGame(first, false, true, null) == null, "equipment and calibrated ward layer are distinct match-bound projections; age includes Plus lag");
                ms += 120;
                var aging = state.ForGame(first, true, true, hud);
                check(aging?.SessionId == first.Meta.SessionId && aging.FrameSequence == first.Meta.Sequence &&
                    aging.RemainingMs == Wire.BoardLifetimeMs - 120, "repeat display of one observation preserves its origin and remaining lease");
                state.ObserveFrame(Frame(630, "postgame", "revoked-session", id: other, hasPlayer: false));
                check(state.ActiveMatch == match && state.ForGame(first, true, true, hud) != null && !archive.IsSealed(other),
                    "late end from a different match cannot even withdraw the active participant's live facts");
                b.Heroes[0].Items![0] = "item_dagon"; eq.Heroes[0].Items![0] = "item_dagon";
                check(frozen.TeamKnowledge!.LastReadEquipment[0].Items![0] == "item_blink" &&
                    frozen.TeamKnowledge.PossibleEnemyWards[0] is { WorldX: -4736, WorldY: -4736 } &&
                    frozen.TeamKnowledge.VisibleAtWardSample[0].Kind == "sentry", "frozen model slice owns its arrays and distinguishes inferred world XY from an actual sighting");
                var facts = archive.Read(match);
                check(facts.Any(x => x is { Source: "inference", Kind: "ward-estimate", Clock: 598, KnownAt: 600 }) &&
                    facts.Any(x => x is { Source: "team-vision", Kind: "ward-observed", Clock: 597, KnownAt: 600 }) &&
                    facts.Count(x => x.Kind == "ward-charges" && x.Source == "team-vision") == 2 &&
                    !facts.Any(f => f.KnownAt < 600), "observation time is not retroactive knowledge; evidence labels survive disk read");
                ms += 300; var newer = Frame(601); state.ObserveFrame(newer);
                state.ObserveTeam(match, "radiant", 601, Board("item_sheepstick"));
                check(frozen.TeamKnowledge.LastReadEquipment[0].Items![0] == "item_blink" && state.ForFarm(first, true) == null &&
                    state.ForFarm(newer, true)!.TeamKnowledge!.LastReadEquipment[0].Items![0] == "item_sheepstick",
                    "new inventory arriving while a model request is running cannot time-travel into the original snapshot");
                string env = Path.Combine(root, "fake-provider.cfg");
                File.WriteAllText(env, "AOAI_ENDPOINT=https://fixture.openai.azure.com\nFOUNDRY_KEY=not-a-key");
                var fake = new SliceModel(); using var cloud = new CloudCoach(env, "fixture", "", fake, localBotOnly: false);
                var scenario = MinuteFarmSimulation.Build(frozen.Own)!;
                await cloud.ChooseMinuteFarmWithMetrics(frozen.Own, scenario, default, frozen);
                check(fake.Body.Contains("item_blink") && fake.Body.Contains("ownEconomy") && fake.Body.Contains("1371") &&
                    fake.Body.Contains("possibleEnemyWards") && fake.Body.Contains("visibleAtWardSample") &&
                    !fake.Body.Contains("item_sheepstick") && !fake.Body.Contains(match) && !fake.Body.Contains("privateCaptureTicket") &&
                    !fake.Body.Contains(uid.ToString()), "real Foundry request contract carries only the authorized as-of match projection, not identities or later reads");
                await cloud.ChooseMinuteFarmWithMetrics(ownOnly.Own, MinuteFarmSimulation.Build(ownOnly.Own)!, default, ownOnly);
                check(!fake.Body.Contains("\"item_blink\"") && !fake.Body.Contains("npc_dota_hero_lion"), "no team inventory sent when the extra model sharing switch is off");
                state.NoteDecision(frozen, "published", scenario.Options[0].Stops, scenario.Options[0].Id);
                check(archive.Read(match).Last(f => f.Kind == "farm-decision").Data.GetProperty("inputSequence").GetInt64() == frozen.FactSequence,
                    "decision record refers to the exact pre-model information cursor");
                ms += 5001; var intermediate = Frame(605); state.ObserveFrame(intermediate);
                state.ObserveEstimates(match, "radiant", 605, Board().Wards!); state.ExpireTeamSources();
                check(state.ForGame(intermediate, true, false, hud) == null &&
                    state.ForGame(intermediate, false, true, hud)?.Wards?.Length == 1,
                    "a corroborating ward update never renews stale equipment; source leases expire independently");
                ms += 5001; var old = Frame(610); state.ObserveFrame(old); state.ExpireTeamSources();
                check(state.ForGame(old, true, true, hud) == null && archive.Read(match).Any(f => f.Kind == "ward-estimate"),
                    "stale equipment/ward sources disappear from the game without erasing the recorded history");
                state.Disconnect();
                check(!archive.IsSealed(match) && archive.Read(match).Any(f => f.Kind == "gap"), "disconnect is a recorded gap, never proof the game ended");
                state.ObserveFrame(Frame(611, "postgame", "other-session", hasPlayer: false));
                state.ObserveFrame(Frame(611, "postgame", id: other, hasPlayer: false));
                check(!archive.IsSealed(match) && !archive.IsSealed(other), "unrelated terminal witnesses cannot seal the current archive");
                state.ObserveFrame(Frame(612)); state.ObserveFrame(Frame(612, "postgame", hasPlayer: false));
                check(archive.IsSealed(match) && state.ActiveMatch == null && state.ForGame(old, true, true, hud) == null,
                    "verified same-participant same-session postgame seals and withdraws both live projections");
                check(state.StorageWarning == null && archive.Read(match).Any(v => v.Kind == "own-state" &&
                    v.Data.GetProperty("gold").GetInt32() == 1371), "independent economy fields persist even without a model; all event payloads meet the disk allowlist");
            }
            using var reopened = new MatchArchive(root);
            var stored = reopened.Read(match);
            check(reopened.List().Single(x => x.MatchId == match).Ended && stored.Count > 5 &&
                stored.Select(f => f.Sequence).Distinct().Count() == stored.Count &&
                stored.All(f => !f.Data.GetRawText().Contains("privateCaptureTicket") && !f.Data.GetRawText().Contains(uid.ToString())),
                "archive survives process restart with the sequence and source intact, but without transport credentials");
        } finally { Directory.Delete(root, recursive: true); }
    }
    private sealed class SliceModel : HttpMessageHandler
    {
        public string Body = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            Body = await request.Content!.ReadAsStringAsync(token);
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new {
                status = "completed", output = new[] { new { type = "message", role = "assistant", status = "completed",
                    content = new[] { new { type = "output_text", text = "{\"optionId\":null}" } } } }
            }), Encoding.UTF8, "application/json") };
        }
    }
}
