using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
if (args.Contains("--video-codec")) { VideoTests.Codec(Check); return; }
if (args is ["--creep-scan", .. var scanFiles]) { CreepHealthTests.Eval(scanFiles); return; }
if (args is ["--creep-bars", var barsOut, var barsSeconds, var barsGsi]) { CreepHealthTests.RecordBars(barsOut, double.Parse(barsSeconds), barsGsi); return; }
if (args is ["--overlay-test", var overlayDir]) { CreepHealthTests.OverlayTest(overlayDir); return; }
if (args is ["--lethal-preview", var previewFrame, var previewGsi, var previewOut]) { HeroAttackTests.Preview(previewFrame, previewGsi, previewOut); return; }
if (args.Contains("--last-hit")) { LastHitTests.Run(Check); return; }
if (args is ["--last-hit-timing"]) { LastHitTimingTests.Run(Check); return; }
if (args is ["--last-hit-eval", .. var evalArgs]) { LastHitEval.Run(evalArgs); return; }
if (args is ["--last-hit-loop", var loopTrace]) { LastHitEval.Loop(loopTrace); return; }
if (args is ["--vision-label", var visionTrace, var visionFrames]) { VisionLabel.Run(visionTrace, visionFrames); return; }
if (args is ["--lane-role-audit",var samplesFile]) { LaneRoleTests.Audit(samplesFile); return; }
if (args.Contains("--last-hit-latency")) { LastHitLatencyReplay.Run(); return; }
if (args.Contains("--last-hit-gpu-bench")) { LastHitGpuBenchmark.Run(); return; }
if (args is ["--minute-farm"]) { await MinuteSuite(); return; }
if (args is ["--lane-waves"]) { LaneWaveTests.Run(Check); Console.WriteLine($"Lane wave checks: {passed} passed."); return; }
if (args is ["--lane-wave-eval", var openings]) { LaneWaveTests.Evaluate(openings); return; }
if (args is ["--lane-wave-fog-eval", var waveData]) { LaneFogEval.Run(waveData); return; }
if (args is ["--lane-live-eval", var liveEvents, var liveOracle, .. var liveOut] && liveOut.Length <= 1) { LaneLiveEval.Run(liveEvents, liveOracle, liveOut.FirstOrDefault()); return; }
if (args is ["--copilot"]) {
    await CopilotContractTests.Run(Check); Console.WriteLine($"Offline Copilot contract checks: {passed} passed."); return;
}
if (args is ["--team-intel"]) {
    PlusAlertTests.Run(Check); FogTests.Run(Check); WardTests.Run(Check); TeamViewEvidenceTests.Run(Check); await HeroScanTests.Run(Check);
    Console.WriteLine($"Team evidence checks: {passed} passed."); return;
}
if (args is ["--ward-eval", .. var wardFiles]) { WardEval.Run(wardFiles); return; }
if (args is ["--fog-eval", .. var fogFiles]) { FogEval.Run(fogFiles); return; }
if (args is ["--ward-session", .. var sessionArgs] &&
    sessionArgs.Where(a => a is not ("--as-scanned" or "--whole-seconds")).ToArray() is [var wardSession, var wardTruth]) {
    WardSessionEval.Run(wardSession, wardTruth, sessionArgs.Contains("--as-scanned"), sessionArgs.Contains("--whole-seconds")); return;
}
if (args is ["--match-history"]) { await MatchSuite(); return; }
async Task MatchSuite()
{
    MatchArchiveTests.Run(Check);
    await LiveMatchStateTests.Run(Check);
    LiveResumeFenceTests.Run(Check);
    LiveOnlyTests.Run(Check);
    await AuthorizedBoardTests.Run(Check);
    Console.WriteLine($"Match archive and projection checks passed; cumulative checks: {passed}.");
}
async Task MinuteSuite()
{
    LaneWaveTests.Run(Check);
    MinuteFarmSimulationTests.Run(Check);
    await MinuteFarmProtocolTests.Run(Check);
    await MinuteFarmModelTests.Run(Check);
    await MinuteFarmPipelineTests.Run(Check);
    Console.WriteLine($"Minute farming checks passed; cumulative checks: {passed}.");
}
void Reject(Action action, string name)
{
    bool rejected = false; try { action(); } catch (ArgumentException) { rejected = true; } catch (InvalidOperationException) { rejected = true; }
    Check(rejected, name);
}
async Task RejectAsync(Func<Task> action, string name)
{
    bool rejected = false; try { await action(); } catch (HttpRequestException) { rejected = true; }
    catch (InvalidOperationException) { rejected = true; } catch (ArgumentException) { rejected = true; }
    Check(rejected, name);
}
// Header-only sample for mailbox/protocol tests. UI verification uses real encoded JPEGs.
byte[] jpeg = [0xff, 0xd8, 0xff, 0xc0, 0, 17, 8, 2, 208, 5, 0, 3, 1, 0x11, 0, 2, 0x11, 1, 3, 0x11, 1, 0xff, 0xd9];
if (OperatingSystem.IsWindows()) { VideoTests.Codec(Check); await VideoTests.Protocol(Check, jpeg); }
FrameMeta Meta(string session, Ticket ticket, long sequence, OwnState? player = null) =>
    new(session, ticket.Id, sequence, player == null ? "diagnostic" : "dota2", 1280, 720, player, player == null ? null : 10);
OwnState Own(string team = "radiant") => new("1234567890", "npc_dota_hero_luna", team, 700, true, 1200, 500, -2000, -3000,
    [new("item_power_treads", null, 0)], [new("luna_lucent_beam", 4, 0)]);
MatchWitness BoardWitness(OwnState p) => new(p.MatchId, p.Team, p.Hero, p.Clock, "playing", false) { AgeMs = 10 };

var valid = new Pairing(1, "https://127.0.0.1:38471/", new string('A', 64), new string('B', 64));
Check(Pairing.Decode(valid.Encode()) == valid, "pairing roundtrip");
Reject(() => Pairing.Decode((valid with { Version = 2 }).Encode()), "protocol version rejected");
foreach (var address in new[] { "http://127.0.0.1/", "https://8.8.8.8/", "https://example.com/", "https://127.0.0.1/admin",
    "https://127.0.0.1/?token=secret", "https://user@127.0.0.1/" })
    Reject(() => Pairing.Decode((valid with { Address = address }).Encode()), "non-LAN/unsafe address rejected: " + address);
Check(Wire.IsLan(IPAddress.Parse("172.31.1.5")) && !Wire.IsLan(IPAddress.Parse("172.32.1.5")), "private subnet boundaries");
Reject(() => Pairing.Decode("DCP1.not-base64"), "malformed bundle");
Check(Jpeg.Dimensions(jpeg) == (1280, 720), "JPEG bounds parsed");
Reject(() => Jpeg.Dimensions([1, 2, 3]), "non-image rejected");
var bomb = jpeg.ToArray(); bomb[7] = 0x7f;
Reject(() => Jpeg.Dimensions(bomb), "oversized decoded image rejected");

long clock = 1000;
var box = new CoachMailbox(() => clock);
var session = box.Begin().SessionId;
var ticket = box.Issue(session);
box.Accept(Meta(session, ticket, 1), jpeg);
Check(box.Snapshot().Connected && box.Latest()!.Meta.Sequence == 1, "latest frame received");
var leakedCopy = box.Latest()!; leakedCopy.Jpeg[0] = 0;
Check(box.Latest()!.Jpeg[0] == 0xff, "preview cannot mutate JPEG mailbox");
box.SetReminder(session, 1, "manual-link-test", "test", new(.12, .83));
Check(box.Poll(session)?.Marker?.X == .12, "manual reminder and normalized marker");
clock += 1000;
int remaining = box.Poll(session)!.RemainingMs;
clock += 500;
Check(box.Poll(session)!.RemainingMs == remaining - 500, "polling never renews reminder TTL");
Reject(() => box.Accept(Meta(session, ticket, 2), jpeg), "ticket cannot be replayed");
ticket = box.Issue(session);
Reject(() => box.Accept(Meta(session, ticket, 1), jpeg), "duplicate sequence rejected");
box.Accept(Meta(session, ticket, 2), jpeg);
box.SetReminder(session, 1, "manual-link-test", "older but still relevant frame", null);
Check(box.Poll(session)?.FrameSequence == 1, "bounded asynchronous response supported");
Reject(() => box.SetReminder(session, 2, "automatic-best-route", "made up", null), "unwired automatic strategy cannot publish");
Reject(() => box.SetReminder(session, 2, "local-vision-observation", "vision", new(.5, .5)), "vision cannot invent map targets");
Reject(() => box.SetReminder(session, 2, "manual-link-test", "test", new(double.NaN, .5)), "NaN coordinate rejected");
clock += 4001;
Check(box.Poll(session) == null && box.Latest() == null && !box.Snapshot().Connected, "capture loss clears reminder and preview");
ticket = box.Issue(session); clock += 3001;
Reject(() => box.Accept(Meta(session, ticket, 3), jpeg), "transport stall expires pre-capture ticket");
ticket = box.Issue(session); box.Accept(Meta(session, ticket, 3, Own()), jpeg);
box.SetReminder(session, 3, "manual-link-test", "alive", null);
ticket = box.Issue(session); box.Accept(Meta(session, ticket, 4, Own() with { Alive = false }), jpeg);
Check(box.Poll(session) == null, "death clears prior reminder");
Reject(() => box.SetReminder(session, 4, "manual-link-test", "dead", null), "no reminder for dead hero");
ticket = box.Issue(session); box.Accept(Meta(session, ticket, 5, Own()), jpeg);
box.SetReminder(session, 5, "manual-link-test", "radiant", null);
ticket = box.Issue(session); box.Accept(Meta(session, ticket, 6, Own("dire")), jpeg);
Check(box.Poll(session) == null, "Radiant/Dire context changes invalidate old advice");
Reject(() => box.SetReminder(session, 5, "manual-link-test", "old team", null), "history invalidated across teams");
ticket = box.Issue(session); box.Accept(Meta(session, ticket, 7, Own("dire") with { Clock = 100 }), jpeg);
Reject(() => box.SetReminder(session, 6, "manual-link-test", "old clock", null), "clock rewind invalidates old advice");
var oldSession = session; session = box.Begin().SessionId;
Reject(() => box.Issue(oldSession), "reconnect cannot reuse old session");
Check(box.Snapshot().Sequence == null && box.Latest() == null, "new session has no old preview");
ticket = box.Issue(session);
Reject(() => box.Accept(Meta(session, ticket, 1) with { Player = Own(), GsiAgeMs = 1 }, jpeg), "diagnostic data cannot carry real GSI");
Reject(() => box.Accept(Meta(session, ticket, 1, Own()) with { GsiAgeMs = 4000 }, jpeg), "old GSI rejected");
Reject(() => box.Accept(Meta(session, ticket, 1) with { SourceWidth = 1920 }, jpeg), "aspect distortion rejected");

// Video off: a status (own GSI and window size, no picture) keeps the game machine present for subtitles only.
var statusBox = new CoachMailbox(() => clock);
var statusSession = statusBox.Begin().SessionId;
FrameMeta Status(long sequence, OwnState? player = null) => new(statusSession, "", sequence, "dota2", 3840, 2160, player, player == null ? null : 10);
var rich = Own() with { Farm = new(true, true, false, InstalledBuild: MinuteFarmContext.SupportedBuild, Epoch: Guid.NewGuid()), Stats = new(Level: 9) };
statusBox.AcceptStatus(Status(1, rich) with { MatchPhase = BoardWitness(rich) });
Check(statusBox.Latest() is { HasImage: false, Video: null, Jpeg.Length: 0, Meta: { Player: { Farm: null, Stats: null }, MatchPhase: null } } latestStatus &&
    TeamViewEvidence.Participant(latestStatus, clock) == null && !CloudCoach.Eligible(latestStatus, localBotOnly: false) &&
    statusBox.Snapshot() is { Connected: true, Frames: 1, Bytes: 0, Player.Hero: "npc_dota_hero_luna" },
    "legacy status keeps own GSI for subtitles but cannot promote participant, archive or model input even when the payload claims a build and witness");
statusBox.SetReminder(statusSession, 1, "local-vision-observation", "剃刀 离开视野", null);
Check(statusBox.Poll(statusSession) is { FrameSequence: 1, SourceWidth: 3840, SourceHeight: 2160 }, "a subtitle may be based on a status");
Reject(() => statusBox.SetReminder(statusSession, 1, "visible-farming-advice", "model", null), "model advice cannot be based on a status");
Reject(() => statusBox.AcceptStatus(Status(1)), "a status sequence cannot repeat");
Reject(() => statusBox.AcceptStatus(Status(2, Own()) with { Source = "diagnostic" }), "a diagnostic status cannot carry GSI");
Reject(() => statusBox.AcceptStatus(Status(2) with { SessionId = session }), "a status needs the current session");
Reject(() => statusBox.AcceptStatus(Status(2) with { SourceWidth = 320 }), "a status needs a real game size");
clock += 4001;
Check(statusBox.Poll(statusSession) == null && !statusBox.Snapshot().Connected, "without statuses the game machine and its subtitle expire");

JsonElement Payload(string team = "radiant") => JsonSerializer.SerializeToElement(new {
    auth = new { token = "must-not-leave-game-machine" },
    player = new { team_name = team, steamid = "PRIVATE", name = "PRIVATE", gold = 999 },
    hero = new { name = "npc_dota_hero_luna", level = 9, alive = true, health = 900, mana = 450, xpos = -2000, ypos = -3000 },
    map = new { matchid = "1234567890", clock_time = 750 },
    items = new { slot0 = new { name = "item_power_treads", cooldown = 0 } },
    abilities = new { ability0 = new { name = "luna_lucent_beam", level = 4, cooldown = 3 } },
    allplayers = new { hiddenEnemy = new { position = "PRIVATE" } },
});
var sanitized = GsiState.Parse(Payload());
string serialized = JsonSerializer.Serialize(sanitized, Wire.Json);
Check(!serialized.Contains("PRIVATE") && !serialized.Contains("token") && !serialized.Contains("allplayers"), "GSI allowlist strips secrets and omniscient data");
Check(GsiState.Parse(Payload("dire")).Team == "dire", "Dire GSI is not mirrored to Radiant");
Reject(() => GsiState.Parse(JsonSerializer.SerializeToElement(new { player = new { team2 = new { } }, hero = new { }, map = new { } })), "observer nested GSI rejected");
Reject(() => GsiState.Validate(Own() with { Health = double.NaN }), "invalid GSI number rejected");

Check(LinkErrors.Message(new TaskCanceledException(), false).Contains("超时"), "network timeout is not labelled user stop");
Check(LinkErrors.Message(new TaskCanceledException(), true).StartsWith("已停止"), "explicit user cancellation remains distinct");
Check(LinkErrors.Message(new CoachVersionException(), false).Contains("教练端版本过旧"), "a coach without status links is named as too old");

await using (var host = await CoachHost.Start(IPAddress.Loopback, 0))
{
    var pairing = host.Pair(IPAddress.Loopback);
    using var link = new GameLink(pairing);
    await link.Connect(default);
    var t = await link.GetTicket(default);
    await link.Upload(Meta(link.SessionId!, t, 1), jpeg, Environment.TickCount64, default);
    Check(host.Mailbox.Snapshot().Frames == 1, "real pinned-TLS frame upload");
    host.Mailbox.SetReminder(link.SessionId!, 1, "manual-link-test", "回传字幕", new(.2, .7));
    Check((await link.Poll(default))?.Message == "回传字幕", "real encrypted caption/marker roundtrip");
    // Plus alerts ride the subtitle-only kind that game clients since v0.2 accept.
    host.Mailbox.SetReminder(link.SessionId!, 1, "local-vision-observation", "剃刀 离开视野 · 最后在中路", null, displayMs: 3000);
    Check((await link.Poll(default)) is { Kind: "local-vision-observation", Marker: null, ShowSubtitle: true, Message: "剃刀 离开视野 · 最后在中路" },
        "a Plus alert reaches the game client as a subtitle without a map mark");
    // The enemy panel and its icons have their own routes, polled about once a second.
    Check(await link.Enemies(default) == null, "no enemy panel until the coach publishes one");
    var participant = Own();
    var panel = new EnemyBoard([new("npc_dota_hero_razor", 12, ["item_blink", null, null, null, null, null], "item_pogo_stick", 3)],
        MatchId: participant.MatchId, Team: participant.Team);
    Reject(() => host.Mailbox.SetEnemies(panel with { MatchId = "0" }), "a panel for a bot/unknown match is not publishable");
    Reject(() => host.Mailbox.SetEnemies(panel with { Team = null }), "a panel without the player's team is not publishable");
    host.Mailbox.SetEnemies(panel with { MatchId = null, Team = null });
    Check(await link.Enemies(default) == null, "an unscoped (v0.4.8 scanner-shaped) panel never reaches the game overlay");
    t = await link.GetTicket(default);
    await link.Upload(Meta(link.SessionId!, t, 2, participant) with { MatchPhase = BoardWitness(participant) },
        jpeg, Environment.TickCount64, default);
    host.Mailbox.SetEnemies(panel);
    Check(await link.Enemies(default) is { Heroes: [{ Hero: "npc_dota_hero_razor", Level: 12, Items: ["item_blink", null, null, null, null, null],
        Neutral: "item_pogo_stick" }] } shown && shown.SessionId == link.SessionId, "the enemy panel reaches the game client over pinned TLS");
    byte[] Png(int width, int height) {
        var png = new byte[64];
        new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 13 }.CopyTo(png, 0);
        "IHDR"u8.CopyTo(png.AsSpan(12));
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(16), width);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(png.AsSpan(20), height);
        return png;
    }
    host.Mailbox.Icons = key => key switch { "item_blink" => Png(88, 64), "item_huge" => Png(4096, 4096), "item_text" => "not an image"u8.ToArray(), _ => null };
    host.Mailbox.SetEnemies(panel with { Heroes = [panel.Heroes[0] with { Items = ["item_blink", "item_missing", "item_huge", "item_text", null, null] }] });
    Check(await link.Icon("item_blink", default) is { Length: 64 } && await link.Icon("item_missing", default) == null &&
          await link.Icon("item_huge", default) == null && await link.Icon("item_text", default) == null && await link.Icon("../secrets", default) == null,
        "icons are small PNGs by Dota name only; missing, oversized and non-PNG ones are not served");
    await link.SendStatus(new(link.SessionId!, "", 3, "diagnostic", 1280, 720, null, null), default);
    host.Mailbox.SetReminder(link.SessionId!, 3, "manual-link-test", "只传状态", null);
    Check(host.Mailbox.Latest() is { HasImage: false, Meta.Sequence: 3 } && (await link.Poll(default)) is { Message: "只传状态", FrameSequence: 3 },
        "video off: a status over pinned TLS carries a reminder back to the game client");
    Check(await link.Enemies(default) is { Heroes.Length: 1 }, "a status between participant samples does not blank the coach's last-known panel");
    host.Mailbox.SetEnemies(null);
    Check(await link.Enemies(default) == null && await link.Icon("item_blink", default) == null,
        "the coach withdrawing its panel also ends icon access");
    bool videoRefused = false;
    try { await link.OpenVideo(default); } catch (System.Net.WebSockets.WebSocketException) { videoRefused = true; }
    Check(videoRefused, "a coach without a video decoder (video off) refuses video streams");
    using var badPin = new GameLink(pairing with { Fingerprint = new string('0', 64) });
    await RejectAsync(() => badPin.Connect(default), "TLS certificate mismatch rejected");
    using var badToken = new GameLink(pairing with { Token = new string('0', 64) });
    await RejectAsync(() => badToken.Connect(default), "wrong pairing token rejected");
    using var handler = new HttpClientHandler { ServerCertificateCustomValidationCallback = (_, c, _, _) =>
        c != null && c.GetCertHashString(System.Security.Cryptography.HashAlgorithmName.SHA256) == pairing.Fingerprint };
    using var client = new HttpClient(handler) { BaseAddress = new(pairing.Address) };
    client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", pairing.Token);
    client.DefaultRequestHeaders.Add("Origin", "https://evil.invalid");
    Check((await client.PostAsync("/v1/session", new StringContent(""))).StatusCode == HttpStatusCode.Unauthorized, "browser Origin cannot control LAN ingress");
    client.DefaultRequestHeaders.Remove("Origin");
    client.DefaultRequestHeaders.Add("X-Session", link.SessionId);
    using var badFrame = new HttpRequestMessage(HttpMethod.Post, "/v1/frame") { Content = new ByteArrayContent(jpeg) };
    badFrame.Content.Headers.ContentType = new("image/jpeg"); badFrame.Headers.Add("X-Frame-Meta", "bad-base64");
    Check((await client.SendAsync(badFrame)).StatusCode == HttpStatusCode.BadRequest, "malformed frame header handled without crashing host");
    Check(host.Mailbox.Snapshot() is { Frames: 3, Sequence: 3 } && host.Mailbox.Latest() is { HasImage: false },
        "malformed payload cannot overwrite the latest accepted status");
    host.Revoke();
    await RejectAsync(() => link.Poll(default), "revoked pairing stops existing client");
    Check(host.Mailbox.Latest() == null, "revocation clears all capture state");
}

await using (var gsi = await GsiReceiver.Start(0))
{
    using var client = new HttpClient { BaseAddress = new($"http://127.0.0.1:{gsi.Port}") };
    var wrong = await client.PostAsync("/gsi", new StringContent(Payload().GetRawText()));
    Check(wrong.StatusCode == HttpStatusCode.Unauthorized, "GSI token required");
    string tokenValue = System.Text.RegularExpressions.Regex.Match(gsi.Config, "\"token\" \"([A-F0-9]+)\"").Groups[1].Value;
    string raw = Payload().GetRawText().Replace("must-not-leave-game-machine", tokenValue);
    Check((await client.PostAsync("/gsi", new StringContent(raw))).IsSuccessStatusCode, "official GSI receiver ingests authenticated own state");
    Check(gsi.Latest().Player?.Hero == "npc_dota_hero_luna", "GSI own state available to capture");
    Check(gsi.LatestLoadout() is { Hero: "npc_dota_hero_luna", Level: 9, Items: [{ Name: "item_power_treads" }] }, "own hero loadout kept for the creep HP display");
    string menu = JsonSerializer.Serialize(new { auth = new { token = tokenValue }, player = new { }, hero = new { }, map = new { } });
    await client.PostAsync("/gsi", new StringContent(menu));
    Check(gsi.Latest().Player == null && gsi.LatestLoadout() == null, "authenticated menu state invalidates prior GSI");
}

using (var fake = new FakeLocalVision())
using (var vision = new LocalVision(fake))
{
    var realFrame = new FrameView(Meta("session", new("session", "ticket"), 1, Own()), jpeg, 0, 0);
    var result = await vision.Observe("test-vl:4b", realFrame, default);
    Check(result.Team == "radiant" && fake.Calls == 2, "local vision checks installed model then observes");
    Check(fake.Requests.All(s => !s.Contains("cloud-key")), "local vision has no cloud credentials");
}
using (var fake = new FakeLocalVision(remote: true))
using (var vision = new LocalVision(fake))
{
    var realFrame = new FrameView(Meta("session", new("session", "ticket"), 1, Own()), jpeg, 0, 0);
    await RejectAsync(() => vision.Observe("test-vl:4b", realFrame, default), "remote/cloud model metadata rejected");
    Check(fake.Calls == 1, "remote model never receives game image");
}
var botFrame = new FrameView(Meta("s", new("s", "t"), 1, Own() with { MatchId = "0" }), jpeg, 0, 0);
var botAdvice = new ModelAdvice(botFrame.Meta.Player!.Hero, botFrame.Meta.Player.Team, "可见己方基地",
    ["小地图左下可见己方建筑"], ["野点是否有怪未知"], "先确认兵线和安全通路，不盲刷野。",
    ["观察可回收兵线，再比较附近已确认野怪"], "出现可见敌方威胁则取消", new(.05, .9), "左下基地附近");
Check(CloudCoach.Eligible(botFrame), "model accepts real-source local-bot own state");
Check(!CloudCoach.Eligible(botFrame with { Meta = botFrame.Meta with { Player = Own() with { MatchId = "123" } } }),
    "legacy bot-only entry remains restricted independently of normal live mode");
Check(!CloudCoach.Eligible(botFrame with { Meta = botFrame.Meta with { Source = "diagnostic" } }),
    "model never treats diagnostic input as a real game");
CloudCoach.Validate(botAdvice, botFrame);
Check(true, "model advice requires own identity and visual grounding");
Reject(() => CloudCoach.Validate(botAdvice with { Team = "unknown" }, botFrame), "mismatched model team rejected");
Reject(() => CloudCoach.Validate(botAdvice with { Marker = new(.7, .3) }, botFrame), "model cannot place minimap marker on main view");
Reject(() => CloudCoach.Validate(botAdvice with { MarkerBasis = "" }, botFrame), "ungrounded model marker rejected");
long botTime = 0;
var botBox = new CoachMailbox(() => botTime); var botSession = botBox.Begin().SessionId;
var botTicket = botBox.Issue(botSession);
botBox.Accept(Meta(botSession, botTicket, 1, Own() with { MatchId = "0" }), jpeg);
botBox.SetReminder(botSession, 1, "experimental-bot-advice", botAdvice.Message, botAdvice.Marker);
Check(botBox.Poll(botSession)?.Kind == "experimental-bot-advice", "valid bot model advice may be published");
botTime = Wire.FrameLifetimeMs + 1000;
Reject(() => botBox.SetReminder(botSession, 1, "experimental-bot-advice", botAdvice.Message, botAdvice.Marker),
    "late model result cannot be stamped onto a new frame");
botTicket = botBox.Issue(botSession);
botBox.Accept(Meta(botSession, botTicket, 2, Own() with { MatchId = "123" }), jpeg);
Reject(() => botBox.SetReminder(botSession, 2, "experimental-bot-advice", botAdvice.Message, botAdvice.Marker),
    "server keeps legacy experimental-bot-advice separate from live advice");
string fakeEnv = Path.GetTempFileName();
try {
    File.WriteAllText(fakeEnv, "AOAI_ENDPOINT=https://unit-test.openai.azure.com/\nFOUNDRY_KEY=unit-test-only\n");
    using var handler = new FakeCloudCoach(botAdvice);
    using var cloud = new CloudCoach(fakeEnv, "gpt-6-astra", "historical test lesson", handler);
    var result = await cloud.Decide(botFrame, default);
    Check(result.Advice.Message == botAdvice.Message && result.InputTokens == 100, "structured Responses vision result and usage parsed");
    Check(handler.Body.Contains("input_image") && handler.Body.Contains("json_schema") &&
        handler.Body.Contains("\"store\":false") && !handler.Body.Contains("unit-test-only"),
        "actual request includes image/schema and excludes the credential from model context");
} finally { File.Delete(fakeEnv); }
var calibrated = new CalibratedMap("data/dota2-map-reference.json");
var ownTower = calibrated.Project("radiant", "npc_dota_goodguys_tower2_bot", botFrame);
Check(Math.Abs(ownTower.X * 1280 - 76.96) < 1 && Math.Abs(ownTower.Y * 720 - 692.84) < 1,
    "Valve overview projects the measured bottom-left minimap landmark");
Reject(() => calibrated.Project("radiant", "npc_dota_badguys_tower1_mid", botFrame), "opponent landmark cannot be selected as an own-side target");
Reject(() => calibrated.Project("radiant", "invented_camp", botFrame), "invented map target rejected");
Reject(() => calibrated.Project("radiant", "npc_dota_goodguys_tower2_bot",
    botFrame with { Meta = botFrame.Meta with { SourceWidth = 1920 } }), "uncalibrated HUD size cannot reuse minimap projection");
Check(calibrated.Candidates("dire").All(x => x.Team == "dire") &&
    calibrated.Candidates("radiant").All(x => x.Team == "radiant"), "map candidates keep Radiant and Dire separate");
string mapEnv = Path.GetTempFileName();
try {
    File.WriteAllText(mapEnv, "AOAI_ENDPOINT=https://unit-test.openai.azure.com/\nFOUNDRY_KEY=unit-test-only\n");
    using var handler = new FakeCloudCoach(botAdvice with { Marker = null, Route = ["npc_dota_goodguys_tower2_bot"] });
    using var cloud = new CloudCoach(mapEnv, "gpt-6-astra", "historical test lesson", handler, calibrated);
    var result = await cloud.Decide(botFrame, default);
    Check(result.Advice.Marker == ownTower, "model chooses a landmark ID; server projects it rather than inventing screen coordinates");
    using var requestBody = JsonDocument.Parse(handler.Body);
    var props = requestBody.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties");
    Check(props.TryGetProperty("route", out var routeSchema) && !props.TryGetProperty("marker", out _) &&
        routeSchema.GetProperty("type").GetString() == "array",
        "calibrated model schema requests an ordered landmark route, not arbitrary coordinates");
    using var badHandler = new FakeCloudCoach(botAdvice with { Route = ["npc_dota_badguys_tower1_mid"] });
    using var badCloud = new CloudCoach(mapEnv, "gpt-6-astra", "historical test lesson", badHandler, calibrated);
    await RejectAsync(() => badCloud.Decide(botFrame, default), "model response selecting opponent landmark is rejected before publication");
} finally { File.Delete(mapEnv); }
var defaults = new LiveCoachSettings();
Check(defaults.AutomaticAdvice && defaults.Subtitles && defaults.MinimapMarkers, "normal coach features default on");
var liveFrame = botFrame with { Meta = botFrame.Meta with {
    SourceWidth = 3840, SourceHeight = 2160, Player = Own() } };
Check(CloudCoach.Eligible(liveFrame, localBotOnly: false), "normal live path accepts real match IDs and 4K own-state frames");
Check(!CloudCoach.Eligible(liveFrame with { Meta = liveFrame.Meta with { GsiAgeMs = 3001 } }, false),
    "normal live mode still rejects stale GSI");
Check(!CloudCoach.Eligible(liveFrame with { Meta = liveFrame.Meta with { Source = "diagnostic" } }, false),
    "enabling live mode never enables inference on diagnostic frames");
var left4k = new MinimapLayout(3840, 2160, 0, 1680, 480);
var right4k = new MinimapLayout(3840, 2160, 3360, 1680, 480);
var leftTarget = calibrated.Project("radiant", "npc_dota_goodguys_tower2_bot", liveFrame, left4k);
Check(Math.Abs(leftTarget.X - ownTower.X) < .000001 && Math.Abs(leftTarget.Y - ownTower.Y) < .000001,
    "confirmed 4K default-layout projection scales original physical pixels, not JPEG pixels");
var rightTarget = calibrated.Project("radiant", "npc_dota_goodguys_tower2_bot", liveFrame, right4k);
Check(right4k.Contains(rightTarget) && Math.Abs((rightTarget.X - leftTarget.X) * 3840 - 3360) < .000001,
    "confirmed right-side 4K minimap is supported without mirroring world coordinates");
var customized = new MinimapLayout(3840, 2160, 40, 1600, 520);
Check(customized.Contains(calibrated.Project("dire", "neutralcamp_evil_11", liveFrame, customized)),
    "custom 4K HUD uses its explicit pixel rectangle for Dire landmarks");
Check(!left4k.IsValidFor(1920, 1080) && !new MinimapLayout(3840, 2160, double.NaN, 1680, 480).IsValidFor(3840, 2160) &&
    !new MinimapLayout(3840, 2160, 0, 2000, 480).IsValidFor(3840, 2160),
    "stale resolution, non-finite, and out-of-client calibrations are rejected");
Reject(() => calibrated.Project("radiant", "npc_dota_goodguys_tower2_bot", liveFrame),
    "4K projection never silently reuses the measured 720p layout");
Check(DisplayGeometry.EncodedSize(3840, 2160) == (1600, 900) &&
    DisplayGeometry.EncodedSize(4096, 2160) == (1600, 843), "4K transport is bounded while preserving source aspect");
foreach (double dpi in new[] { 1.0, 1.5, 2.0, 2.5, 3.0 })
    Check(Math.Abs(DisplayGeometry.OverlayUnit(2160, dpi) * dpi * 28 - 84) < .000001,
        $"4K caption physical size remains readable at DPI scale {dpi}");
var normalAdvice = botAdvice with { Marker = leftTarget, Route = ["npc_dota_goodguys_tower2_bot"], RouteMarks = [leftTarget] };
CloudCoach.Validate(normalAdvice, liveFrame, false, left4k);
Reject(() => CloudCoach.Validate(normalAdvice, liveFrame, false), "uncalibrated normal live mode cannot return a map point");
Reject(() => CloudCoach.Validate(normalAdvice with { Marker = new(.8, .2) }, liveFrame, false, left4k),
    "live marker outside confirmed minimap rejected");
var preferencesFile = Path.Combine(Path.GetTempPath(), "coach-prefs-" + Guid.NewGuid().ToString("N") + ".json");
try {
    var pref = defaults with { Calibrations = [left4k] };
    pref.Save(preferencesFile);
    Check(LiveCoachSettings.Load(preferencesFile).LayoutFor(liveFrame) == left4k,
        "confirmed layout persists by actual game resolution");
    Reject(() => (pref with { RequestLimit = 0 }).Save(preferencesFile), "unbounded/invalid request limits rejected");
    Reject(() => (pref with { IntervalSeconds = 1 }).Save(preferencesFile), "accidental per-frame paid requests rejected");
    Check(!File.ReadAllText(preferencesFile).Contains("FOUNDRY_KEY"), "preferences do not serialize model credentials");
} finally { File.Delete(preferencesFile); }
string liveEnv = Path.GetTempFileName();
try {
    File.WriteAllText(liveEnv, "AOAI_ENDPOINT=https://unit-test.openai.azure.com/\nFOUNDRY_KEY=unit-test-only\n");
    using var handler = new FakeCloudCoach(normalAdvice);
    using var cloud = new CloudCoach(liveEnv, "gpt-6-astra", "historical test lesson", handler, calibrated, false, left4k);
    var result = await cloud.Decide(liveFrame, default);
    Check(result.Advice.Marker == leftTarget && handler.Body.Contains("player-visible-live-next-step"),
        "normal live 4K inference traverses the actual Responses request/parse/projection path");
    using var textHandler = new FakeCloudCoach(botAdvice with { Marker = null, Route = null });
    using var textCloud = new CloudCoach(liveEnv, "gpt-6-astra", "", textHandler, calibrated, false);
    Check((await textCloud.Decide(liveFrame, default)).Advice.Marker == null,
        "uncalibrated live 4K still receives model subtitles without guessed markers");
    using var badHandler = new FakeCloudCoach(normalAdvice);
    using var badCloud = new CloudCoach(liveEnv, "gpt-6-astra", "", badHandler, calibrated, false);
    await RejectAsync(() => badCloud.Decide(liveFrame, default), "model cannot invent a target when the map has no calibration");
} finally { File.Delete(liveEnv); }
await using (var liveHost = await CoachHost.Start(IPAddress.Loopback, 0)) {
    using var link = new GameLink(liveHost.Pair(IPAddress.Loopback));
    await link.Connect(default);
    var liveTicket = await link.GetTicket(default);
    var meta = liveFrame.Meta with { SessionId = link.SessionId!, Ticket = liveTicket.Id };
    await link.Upload(meta, jpeg, Environment.TickCount64, default);
    liveHost.Mailbox.SetReminder(link.SessionId!, 1, "visible-farming-advice", normalAdvice.Message, leftTarget, left4k);
    Check((await link.Poll(default)) is { Kind: "visible-farming-advice", SourceWidth: 3840, SourceHeight: 2160 } delivered &&
        delivered.Minimap == left4k && delivered.Marker == leftTarget,
        "normal-match 4K advice crosses actual pinned TLS and game-side geometry validation");
    Reject(() => liveHost.Mailbox.SetReminder(link.SessionId!, 1, "visible-farming-advice", "bad", leftTarget),
        "server rejects normal map marks without source-size calibration");
    liveHost.Mailbox.SetReminder(link.SessionId!, 1, "visible-farming-advice", "text only", null);
    Check((await link.Poll(default))?.Marker == null, "normal uncalibrated text advice crosses TLS");
    liveHost.Mailbox.SetReminder(link.SessionId!, 1, "visible-farming-advice", "map only", leftTarget, left4k, false);
    Check((await link.Poll(default))?.ShowSubtitle == false, "subtitle off is preserved independently of map targets");
    var secondStop = calibrated.Project("radiant", "neutralcamp_good_11", liveFrame, left4k);
    liveHost.Mailbox.SetReminder(link.SessionId!, 1, "visible-farming-advice", "route", leftTarget, left4k, true, 3000,
        [leftTarget, secondStop], ["清理二塔附近野", "转去11号营地"]);
    Check((await link.Poll(default)) is { Route.Length: 2, NextSteps.Length: 2, SourceAgeMs: >= 0, RemainingMs: > 2000 and <= 3000 } routed &&
        routed.Route[1] == secondStop, "ordered route, next steps and game-clock source age cross pinned TLS");
}

// Subtitle display time is separate from source freshness: a slow model answer still gets the full display time.
long lifeClock = 0;
var lifeBox = new CoachMailbox(() => lifeClock);
string lifeSession = lifeBox.Begin().SessionId;
long lifeSequence = 0;
void Feed() {
    var t = lifeBox.Issue(lifeSession); var p = Own();
    lifeBox.Accept(Meta(lifeSession, t, ++lifeSequence, p) with { MatchPhase = BoardWitness(p) }, jpeg);
}
Feed();
for (int i = 0; i < 5; i++) { lifeClock += 3000; Feed(); } // a 15 s model call while video keeps arriving
lifeBox.SetReminder(lifeSession, 1, "visible-farming-advice", "slow but valid", null, null, true, 3000);
Check(lifeBox.Poll(lifeSession)?.RemainingMs == 3000, "a 15-second model answer is shown for the full configured 3 seconds");
lifeClock += 2999; Feed();
Check(lifeBox.Poll(lifeSession) != null, "subtitle stays for its display time");
lifeClock += 2;
Check(lifeBox.Poll(lifeSession) == null, "subtitle disappears when its display time ends");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "too short", null, null, true, 500), "display time below 1 s rejected");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "too long", null, null, true, Wire.MaxReminderMs + 1), "display time above 15 s rejected");
for (int i = 0; i < 8; i++) { lifeClock += 3000; Feed(); }
Reject(() => lifeBox.SetReminder(lifeSession, 1, "visible-farming-advice", "stale", null, null, true, 3000),
    "a source frame older than the advice lifetime can never be published");
var layout720 = new MinimapLayout(1280, 720, 0, 560, 160);
ScreenMark stop1 = new(.05, .9), stop2 = new(.1, .85), stop3 = new(.11, .95);
lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "route", stop1, layout720, true, 3000, [stop1, stop2, stop3], ["清野", "接线", "回野"]);
Check(lifeBox.Poll(lifeSession) is { Route.Length: 3, NextSteps.Length: 3 }, "three-stop route with matching next steps is published");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "route", stop2, layout720, true, 3000, [stop1, stop2]),
    "route must start at the primary marker");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "route", stop1, layout720, true, 3000, [stop1, new(.8, .2)]),
    "every route stop must lie inside the minimap");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "route", stop1, layout720, true, 3000, [stop1, stop2, stop3, stop2]),
    "routes are limited to three stops");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "visible-farming-advice", "route", stop1, layout720, true, 3000, null, [new string('长', 61)]),
    "next-step text is bounded");
Reject(() => lifeBox.SetReminder(lifeSession, lifeSequence, "manual-link-test", "route", stop1, null, true, 3000, [stop1]),
    "manual tests cannot impersonate an AI route");

// Enemy panel: validated, aged while it waits, gone unless renewed, and only for a live session.
EnemyHero Enemy(string hero = "npc_dota_hero_razor", int? level = 12, string?[]? items = null, double? age = 3) =>
    new(hero, level, items ?? ["item_blink", null, null, null, null, null], null, age);
Feed();
lifeBox.SetEnemies(new([Enemy(), new EnemyHero("npc_dota_hero_pudge", null, null, null, null)], MatchId: Own().MatchId, Team: Own().Team));
lifeClock += 2000; Feed();
Check(lifeBox.Enemies(lifeSession) is { Heroes: [{ Age: 5 }, { Level: null, Items: null, Age: null }] },
    "the enemy panel reaches the session with its ages grown by the time it waited");
lifeClock += Wire.BoardLifetimeMs - 1999; Feed();
Check(lifeBox.Enemies(lifeSession) == null, "an enemy panel the coach stops renewing disappears");
lifeBox.SetEnemies(new([Enemy()], MatchId: Own().MatchId, Team: Own().Team));
Reject(() => lifeBox.Enemies("not-the-session"), "another session cannot read the enemy panel");
foreach (var (bad, why) in new (EnemyBoard, string)[] {
    (new(Enumerable.Range(0, 6).Select(i => Enemy("npc_dota_hero_h" + i)).ToArray()), "more than five enemies"),
    (new([Enemy(), Enemy()]), "the same hero twice"), (new([Enemy(level: 31)]), "a level above 30"),
    (new([Enemy(items: ["item_blink"])]), "fewer than six item slots"), (new([Enemy(items: ["../x", null, null, null, null, null])]), "a path as an item"),
    (new([Enemy("item_blink")]), "an item as a hero"), (new([Enemy(age: double.NaN)]), "a non-finite age") })
    Reject(() => lifeBox.SetEnemies(bad), "enemy panel with " + why + " rejected");
lifeBox.SetEnemies(null);
Check(lifeBox.Enemies(lifeSession) == null, "null hides the enemy panel");

// Model route: up to three distinct own-side landmarks, projected by the program.
string routeEnv = Path.GetTempFileName();
try {
    File.WriteAllText(routeEnv, "AOAI_ENDPOINT=https://unit-test.openai.azure.com/\nFOUNDRY_KEY=unit-test-only\n");
    using var handler = new FakeCloudCoach(botAdvice with { Marker = new(.5, .5), Route = ["npc_dota_goodguys_tower2_bot",
        "neutralcamp_good_11", "npc_dota_goodguys_tower2_bot", "neutralcamp_good_11", "npc_dota_goodguys_tower1_bot"] });
    using var cloud = new CloudCoach(routeEnv, "gpt-6-astra", "", handler, calibrated, false, left4k);
    var routed = (await cloud.Decide(liveFrame, default)).Advice;
    Check(routed.RouteMarks is { Length: 3 } marks && marks[0] == routed.Marker && marks[0] == leftTarget &&
        marks.All(left4k.Contains) && routed.Route!.Distinct().Count() == 3,
        "model route keeps three distinct stops and ignores a model-supplied marker coordinate");
    using (var sentRequest = JsonDocument.Parse(handler.Body))
        Check(sentRequest.RootElement.GetProperty("instructions").GetString()!.Contains("短期刷钱路线"), "request asks for an ordered farming route");
    using var enemyHandler = new FakeCloudCoach(botAdvice with { Route = ["neutralcamp_good_11", "npc_dota_badguys_tower1_mid"] });
    using var enemyCloud = new CloudCoach(routeEnv, "gpt-6-astra", "", enemyHandler, calibrated, false, left4k);
    await RejectAsync(() => enemyCloud.Decide(liveFrame, default), "a route through an opponent landmark is rejected");
    Reject(() => new CloudCoach(routeEnv, "gpt-6-astra", "", null, calibrated, false, left4k, TimeSpan.FromSeconds(30)),
        "model timeout cannot exceed the advice lifetime budget");
} finally { File.Delete(routeEnv); }

var liveDefaults = new LiveCoachSettings();
Check(liveDefaults.SubtitleSeconds == 3 && liveDefaults.ModelTimeoutSeconds == 15 && liveDefaults.AutoMinimap,
    "defaults: 3-second subtitles, 15-second model timeout, automatic minimap box");
Check(liveDefaults.EffectiveLayout(liveFrame) == left4k && (liveDefaults with { AutoMinimap = false }).EffectiveLayout(liveFrame) == null,
    "automatic 4K minimap box is the bottom-left default and can be switched off");
Check((liveDefaults with { Calibrations = [right4k] }).EffectiveLayout(liveFrame) == right4k, "a confirmed calibration overrides the automatic box");
Check(liveDefaults.EffectiveLayout(liveFrame with { Meta = liveFrame.Meta with { Source = "diagnostic" } }) == null, "no automatic map box for diagnostic video");
foreach (var bad in new[] { liveDefaults with { SubtitleSeconds = 0 }, liveDefaults with { SubtitleSeconds = 16 },
    liveDefaults with { ModelTimeoutSeconds = 7 }, liveDefaults with { ModelTimeoutSeconds = 21 } })
    Reject(bad.Validate, $"coach timing bounds enforced ({bad.SubtitleSeconds}s / {bad.ModelTimeoutSeconds}s)");
Check(liveDefaults.ScanFolder == "" && liveDefaults.ScanPython == "", "vision scan paths start unset");
Reject((liveDefaults with { ScanFolder = @"scripts\vision" }).Validate, "a relative vision scan folder is refused");
Reject((liveDefaults with { ScanPython = "python.exe" }).Validate, "a bare python name is refused: the scan runs exactly the python chosen");

var video = new GameVideoSettings();
Check(video.Fps == 60 && video.Resolution == "1080" && video.Encoder == "auto" && video.Capture == "window",
    "game video defaults: 60 fps, 1080p, GPU encoder when present, window capture");
Check(video.EncodedSize(3840, 2160, true) == (1920, 1080) && (video with { Resolution = "native" }).EncodedSize(3840, 2160, true) == (3840, 2160),
    "1080p preset scales 4K on the game GPU; native leaves scaling to the coach");
Check((video with { Resolution = "native" }).EncodedSize(3840, 2160, false) == (1920, 1080),
    "CPU encoder is capped at 1080p to protect the game's frame rate");
Check(video.EncodedSize(3440, 1440, true) == (2580, 1080) && video.EncodedSize(1280, 720, true) == (1280, 720),
    "presets keep ultrawide aspect and never upscale");
Check(GameVideoSettings.EffectiveFps(3840, 2160, 120) == 60 && GameVideoSettings.EffectiveFps(1920, 1080, 120) == 120 &&
    GameVideoSettings.EffectiveFps(2560, 1440, 120) == 120, "frame rate is lowered only when H.264 level 5.2 cannot carry it");
Check(video.Bitrate(1920, 1080, 60) is > 8_000_000 and < 9_000_000 && (video with { BitrateMbps = 25 }).Bitrate(1920, 1080, 60) == 25_000_000,
    "automatic bitrate scales with pixels × fps; manual Mbps overrides it");
foreach (var bad in new[] { video with { Fps = 45 }, video with { Resolution = "4k" }, video with { Encoder = "nvenc" }, video with { BitrateMbps = 101 } })
    Reject(bad.Validate, "invalid game video setting rejected");
string videoFile = Path.Combine(Path.GetTempPath(), "game-video-" + Guid.NewGuid().ToString("N") + ".json");
try {
    (video with { Fps = 120, Resolution = "native" }).Save(videoFile);
    Check(GameVideoSettings.Load(videoFile) is { Fps: 120, Resolution: "native" }, "game video settings persist");
    File.WriteAllText(videoFile, "{\"fps\": 7}");
    Check(GameVideoSettings.Load(videoFile) == new GameVideoSettings(), "corrupt game video settings fall back to defaults");
} finally { File.Delete(videoFile); }

var halves = new byte[64 * 32 * 4];
for (int i = 0; i < halves.Length; i += 4) {
    bool left = i / 4 % 64 < 32;
    halves[i] = (byte)(left ? 20 : 220); halves[i + 1] = 60; halves[i + 2] = (byte)(left ? 220 : 20); halves[i + 3] = 255;
}
var scaled = VideoPixels.FromBgra(64, 32, halves).ToBgra(32, 16);
Check(scaled.Length == 32 * 16 * 4 && Math.Abs(scaled[(8 * 32 + 4) * 4 + 2] - 220) < 12 && Math.Abs(scaled[(8 * 32 + 28) * 4] - 220) < 12,
    "coach-side downscale keeps colours and layout");
Reject(() => VideoPixels.FromBgra(64, 32, halves).ToBgra(128, 64), "coach never upscales video pixels");
string debugRoot = Path.Combine(Path.GetTempPath(), "dota-coach-debug-tests-" + Guid.NewGuid().ToString("N"));
var debugMeta = liveFrame.Meta with { SessionId = "../../outside", Ticket = "NEVER-SAVE-CAPTURE-TICKET" };
var debugFrame = liveFrame with { Meta = debugMeta };
var recorder = new DebugRecorder(debugRoot, "coach", "unit-test");
recorder.Frame("frame-received", debugMeta, jpeg);
recorder.ModelStarted(debugFrame, "test-model", left4k, 1);
recorder.ModelFinished(debugFrame, new(botAdvice with {
    Message = "Bearer super-private-token FOUNDRY_KEY=secret-value", Marker = null,
    Unknowns = ["DCP1.privatepairing=="], Route = null,
}, "resp-unit-test", "test-model", 11, 22, 33));
recorder.Error("test-error", new InvalidOperationException("HTTP credential=NEVER-SAVE-EXCEPTION-MESSAGE"));
recorder.Error("model-provider", new ModelProviderException(429), debugMeta, "upstream-status");
recorder.State(new("connected", true, true, true, false, 1, 11, 22, 10, Own(), left4k));
await recorder.DisposeAsync();
string logText = File.ReadAllText(Path.Combine(recorder.DirectoryPath, "events.jsonl"));
var debugRows = File.ReadAllLines(Path.Combine(recorder.DirectoryPath, "events.jsonl")).Select(x => JsonDocument.Parse(x)).ToArray();
Check(debugRows.Any(d => d.RootElement.GetProperty("stage").GetString() == "model-result") &&
    debugRows.Any(d => d.RootElement.GetProperty("stage").GetString() == "client-state"),
    "debug saves correlated model decisions and client runtime state");
Check(debugRows.Any(d => d.RootElement.GetProperty("stage").GetString() == "error" &&
    d.RootElement.GetProperty("details").GetProperty("httpStatus").ValueKind == JsonValueKind.Number &&
    d.RootElement.GetProperty("details").GetProperty("httpStatus").GetInt32() == 429),
    "debug preserves actionable upstream HTTP status without response bodies");
Check(!logText.Contains("NEVER-SAVE-CAPTURE-TICKET") && !logText.Contains("super-private-token") &&
    !logText.Contains("secret-value") && !logText.Contains("privatepairing") &&
    !logText.Contains("NEVER-SAVE-EXCEPTION-MESSAGE"),
    "debug excludes transport tickets, pairing, credentials and raw exception bodies");
var frameRow = debugRows.First(d => d.RootElement.GetProperty("stage").GetString() == "model-input");
string relativeImage = frameRow.RootElement.GetProperty("details").GetProperty("image").GetString()!;
string savedImage = Path.GetFullPath(Path.Combine(recorder.DirectoryPath, relativeImage));
Check(savedImage.StartsWith(Path.GetFullPath(recorder.DirectoryPath) + Path.DirectorySeparatorChar) &&
    File.ReadAllBytes(savedImage).SequenceEqual(jpeg),
    "debug preserves exact model JPEG bytes and hashes untrusted session names for safe filenames");
Check(Directory.GetFiles(Path.Combine(recorder.DirectoryPath, "frames")).Length == 1 &&
    debugRows.Count(d => d.RootElement.GetProperty("stage").GetString() == "model-input") == 2,
    "repeated model-input references reuse one immutable image without losing trace events");
long stoppedSize = new FileInfo(Path.Combine(recorder.DirectoryPath, "events.jsonl")).Length;
recorder.Frame("frame-captured", debugMeta, jpeg);
Check(new FileInfo(Path.Combine(recorder.DirectoryPath, "events.jsonl")).Length == stoppedSize &&
    File.Exists(Path.Combine(recorder.DirectoryPath, "summary.json")), "stopped debug cannot keep recording and produces a summary");
foreach (var row in debugRows) row.Dispose();
var byteLimited = new DebugRecorder(debugRoot, "game", "test", new(MaxBytes: 65536));
var padded = jpeg[..^2].Concat(new byte[40000]).Concat(jpeg[^2..]).ToArray();
byteLimited.Frame("frame-captured", debugMeta, padded);
await byteLimited.DisposeAsync();
Check(byteLimited.Status.Reason == "byte-limit" && Directory.GetFiles(Path.Combine(byteLimited.DirectoryPath, "frames")).Length == 0,
    "debug disk budget includes queued images before writing");
var frameLimited = new DebugRecorder(debugRoot, "game", "test", new(MaxFrameRecords: 1));
frameLimited.Frame("frame-captured", debugMeta, jpeg); frameLimited.Frame("frame-captured", debugMeta with { Sequence = 2 }, jpeg);
await frameLimited.DisposeAsync();
Check(frameLimited.Status.Reason == "frame-limit" && frameLimited.Status.Images == 1, "debug stops at its image-record limit");
long debugClock = 0;
var timeLimited = new DebugRecorder(debugRoot, "game", "test", new(MaxMinutes: 1), () => debugClock);
debugClock = 60001; timeLimited.Frame("frame-captured", debugMeta, jpeg); await timeLimited.DisposeAsync();
Check(timeLimited.Status.Reason == "duration-limit" && timeLimited.Status.Images == 0, "debug time limit stops unattended recording");
var brokenDisk = new DebugRecorder(debugRoot, "game", "test");
Directory.Delete(Path.Combine(brokenDisk.DirectoryPath, "frames")); // Empty directory owned by this new test session only.
brokenDisk.Frame("frame-captured", debugMeta, jpeg); await brokenDisk.DisposeAsync();
Check(brokenDisk.Status.Reason == "disk-error", "debug disk failures are isolated instead of crashing capture");

await using (var gsiProbe = await GsiReceiver.Start(0)) {
    var gsiDebug = new DebugRecorder(debugRoot, "game", "test");
    gsiProbe.Diagnostics = gsiDebug.Gsi;
    using var http = new HttpClient { BaseAddress = new($"http://127.0.0.1:{gsiProbe.Port}") };
    string secret = System.Text.RegularExpressions.Regex.Match(gsiProbe.Config, "\"token\" \"([A-F0-9]+)\"").Groups[1].Value;
    string observer = JsonSerializer.Serialize(new { auth = new { token = secret },
        player = new { team2 = new { player0 = new { steamid = "PRIVATE-OBSERVER-ID", name = "PRIVATE-NAME" } } },
        hero = new { team2 = new { player0 = new { xpos = 9999 } } }, map = new { matchid = "123", clock_time = 700 },
        allplayers = new { hidden = "NEVER-SAVE-HIDDEN-VALUES" },
    });
    var response = await http.PostAsync("/gsi", new StringContent(observer));
    Check(response.StatusCode == HttpStatusCode.BadRequest && gsiProbe.Latest().Player == null &&
        gsiProbe.LastDiagnostics is { Mode: "observer-shaped", HasTeamGroups: true, OwnStateAccepted: false },
        "debug identifies nested observer GSI without routing it into own-player coaching");
    await gsiDebug.DisposeAsync();
    string diagnostics = File.ReadAllText(Path.Combine(gsiDebug.DirectoryPath, "events.jsonl"));
    Check(!diagnostics.Contains(secret) && !diagnostics.Contains("PRIVATE-") && !diagnostics.Contains("NEVER-SAVE-HIDDEN-VALUES") &&
        diagnostics.Contains("observer-shaped"), "GSI debug records structural presence, never auth, identities or hidden values");
}
await SpectatorTests.Run(Check);
LastHitTests.Run(Check);
PlusAlertTests.Run(Check); FogTests.Run(Check);
WardTests.Run(Check); TeamViewEvidenceTests.Run(Check); await HeroScanTests.Run(Check);
OverlaySettingsTests.Run(Check);
CreepHealthTests.Run(Check);
HeroAttackTests.Run(Check);
LastHitTimingTests.Run(Check);
await MinuteSuite();
await MatchSuite();
await CopilotContractTests.Run(Check);
Console.WriteLine($"Two-PC protocol + real TLS + GSI + mocked model contracts: {passed} checks passed.");

sealed class FakeCloudCoach(ModelAdvice advice) : HttpMessageHandler
{
    public string Body = "";
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        Body = await request.Content!.ReadAsStringAsync(token);
        object envelope = new { id = "test-response", status = "completed",
            usage = new { input_tokens = 100, output_tokens = 80 },
            output = new[] { new { type = "message", content = new[] { new { type = "output_text",
                text = JsonSerializer.Serialize(advice, Wire.Json) } } } } };
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(envelope)) };
    }
}

sealed class FakeLocalVision(bool remote = false) : HttpMessageHandler
{
    public int Calls;
    public List<string> Requests = [];
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        Calls++; Requests.Add(await request.Content!.ReadAsStringAsync(cancellationToken));
        if (request.RequestUri!.Host != "127.0.0.1") throw new Exception("Unexpected remote host");
        object result = request.RequestUri.AbsolutePath == "/api/show"
            ? remote ? new { remote_host = "cloud.invalid", details = new { format = "gguf" }, capabilities = new[] { "vision" } }
                : (object)new { details = new { format = "gguf" }, capabilities = new[] { "vision" } }
            : new { message = new { content = JsonSerializer.Serialize(new VisionObservation("npc_dota_hero_luna",
                "radiant", 750, "可见英雄和小地图；未确认营地状态。", ["迷雾区域"]), Wire.Json) } };
        return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(result), Encoding.UTF8, "application/json") };
    }
}
