using System.Net;
using System.Net.Http.Headers;
using System.Security.Authentication;
using System.Text;
using System.Text.Json;
using DotaCoach.TwoPc;

int passed = 0;
void Check(bool value, string name) { if (!value) throw new Exception(name); passed++; Console.WriteLine("PASS " + name); }
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
FrameMeta Meta(string session, Ticket ticket, long sequence, OwnState? player = null) =>
    new(session, ticket.Id, sequence, player == null ? "diagnostic" : "dota2", 1280, 720, player, player == null ? null : 10);
OwnState Own(string team = "radiant") => new("1234567890", "npc_dota_hero_luna", team, 700, true, 1200, 500, -2000, -3000,
    [new("item_power_treads", null, 0)], [new("luna_lucent_beam", 4, 0)]);

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

JsonElement Payload(string team = "radiant") => JsonSerializer.SerializeToElement(new {
    auth = new { token = "must-not-leave-game-machine" },
    player = new { team_name = team, steamid = "PRIVATE", name = "PRIVATE", gold = 999 },
    hero = new { name = "npc_dota_hero_luna", alive = true, health = 900, mana = 450, xpos = -2000, ypos = -3000 },
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

long probeClock = 0;
var probeBox = new CoachMailbox(() => probeClock);
string probeSession = probeBox.Begin().SessionId;
var deferred = new DeferredLinkProbe("人工回传测试", new(.065, .86), () => probeClock);
var probeTicket = probeBox.Issue(probeSession);
probeBox.Accept(Meta(probeSession, probeTicket, 1, Own()), jpeg);
Check(deferred.Tick(probeBox) == "waiting", "deferred probe never sends before five seconds");
probeClock = 5001;
Check(deferred.Tick(probeBox) == "waiting", "deferred probe cannot reuse old capture");
probeTicket = probeBox.Issue(probeSession);
probeBox.Accept(Meta(probeSession, probeTicket, 2), jpeg);
Check(deferred.Tick(probeBox) == "waiting", "diagnostic frames do not satisfy real-game probe");
probeTicket = probeBox.Issue(probeSession);
probeBox.Accept(Meta(probeSession, probeTicket, 3) with { Source = "dota2" }, jpeg);
Check(deferred.Tick(probeBox) == "waiting", "probe requires fresh own-player GSI");
probeTicket = probeBox.Issue(probeSession);
probeBox.Accept(Meta(probeSession, probeTicket, 4, Own() with { Alive = false }), jpeg);
Check(deferred.Tick(probeBox) == "waiting", "deferred probe will not annotate a dead hero");
probeTicket = probeBox.Issue(probeSession);
probeBox.Accept(Meta(probeSession, probeTicket, 5, Own()), jpeg);
Check(deferred.Tick(probeBox) == "sent" && probeBox.Poll(probeSession)?.Marker?.Y == .86, "fresh real-game frame permits one explicit manual probe");
string probeReminderId = probeBox.Poll(probeSession)!.Id;
deferred.Tick(probeBox);
Check(probeBox.Poll(probeSession)!.Id == probeReminderId, "deferred probe is single-shot");
var cancelledProbe = new DeferredLinkProbe("cancel", null, () => probeClock);
cancelledProbe.Cancel();
Check(cancelledProbe.Tick(probeBox) == "cancelled", "operator can cancel a pending probe");
var expiredProbe = new DeferredLinkProbe("expire", null, () => probeClock);
probeClock += 25000;
Check(expiredProbe.Tick(probeBox) == "expired", "missing real input expires pending probe");
Reject(() => new DeferredLinkProbe("bad", new(-.1, 2)), "invalid deferred coordinate rejected");
Check(LinkErrors.Message(new TaskCanceledException(), false).Contains("超时"), "network timeout is not labelled user stop");
Check(LinkErrors.Message(new TaskCanceledException(), true).StartsWith("已停止"), "explicit user cancellation remains distinct");

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
    Check(host.Mailbox.Snapshot().Frames == 1, "malformed payload cannot overwrite latest frame");
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
    string menu = JsonSerializer.Serialize(new { auth = new { token = tokenValue }, player = new { }, hero = new { }, map = new { } });
    await client.PostAsync("/gsi", new StringContent(menu));
    Check(gsi.Latest().Player == null, "authenticated menu state invalidates prior GSI");
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
    "model does not silently activate in human matchmaking");
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
botTime = 13000;
Reject(() => botBox.SetReminder(botSession, 1, "experimental-bot-advice", botAdvice.Message, botAdvice.Marker),
    "late model result cannot be stamped onto a new frame");
botTicket = botBox.Issue(botSession);
botBox.Accept(Meta(botSession, botTicket, 2, Own() with { MatchId = "123" }), jpeg);
Reject(() => botBox.SetReminder(botSession, 2, "experimental-bot-advice", botAdvice.Message, botAdvice.Marker),
    "server blocks experimental bot advice in real matches");
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
    using var handler = new FakeCloudCoach(botAdvice with { Marker = null, TargetId = "npc_dota_goodguys_tower2_bot" });
    using var cloud = new CloudCoach(mapEnv, "gpt-6-astra", "historical test lesson", handler, calibrated);
    var result = await cloud.Decide(botFrame, default);
    Check(result.Advice.Marker == ownTower, "model chooses a landmark ID; server projects it rather than inventing screen coordinates");
    using var requestBody = JsonDocument.Parse(handler.Body);
    var props = requestBody.RootElement.GetProperty("text").GetProperty("format").GetProperty("schema").GetProperty("properties");
    Check(props.TryGetProperty("targetId", out _) && !props.TryGetProperty("marker", out _),
        "calibrated model schema requests target choice, not arbitrary coordinates");
    using var badHandler = new FakeCloudCoach(botAdvice with { TargetId = "npc_dota_badguys_tower1_mid" });
    using var badCloud = new CloudCoach(mapEnv, "gpt-6-astra", "historical test lesson", badHandler, calibrated);
    await RejectAsync(() => badCloud.Decide(botFrame, default), "model response selecting opponent landmark is rejected before publication");
} finally { File.Delete(mapEnv); }
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
