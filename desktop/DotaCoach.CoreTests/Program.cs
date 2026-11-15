using System.Runtime.InteropServices;
using DotaCoach.Desktop;

int count = 0;
void Check(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    count++;
}
foreach (string uri in new[] { "file:///C:/private", "http://example.com", "https://user:pass@example.com", "javascript:alert(1)" })
{
    bool denied = false;
    try { DesktopPolicy.ServerUri(uri); } catch (ArgumentException) { denied = true; }
    Check(denied, "Unsafe server URI accepted: " + uri);
}
var trusted = DesktopPolicy.ServerUri("https://coach.example.com:8443/");
Check(DesktopPolicy.SameOrigin(trusted, "https://coach.example.com:8443/?room=123"), "Same origin rejected");
foreach (string uri in new[] {
    "https://coach.example.com/", "https://coach.example.com.evil.test:8443/",
    "http://coach.example.com:8443/", "https://coach.example.com:8443@evil.test/",
    "https://user@coach.example.com:8443/", "null",
})
    Check(!DesktopPolicy.SameOrigin(trusted, uri), "Origin spoof accepted");
Check(DesktopPolicy.ServerUri("http://127.0.0.1:3210/").IsLoopback, "Local development rejected");

// Regression: an installed desktop helper does not run the Node server on the user's PC.
Check(DesktopPolicy.InitialServerAddress(null, null, null) == DesktopPolicy.DefaultServerAddress,
    "A first launch must open the hosted service, not localhost");
foreach (string oldDefault in new[] {
    "http://localhost:3210/", "http://127.0.0.1:3210/", "http://[::1]:3210/", "  http://LOCALHOST:3210/ \n",
})
    Check(DesktopPolicy.InitialServerAddress(null, null, oldDefault) == DesktopPolicy.DefaultServerAddress,
        "An old saved localhost default must not trap an upgraded user on a refused connection");
Check(DesktopPolicy.InitialServerAddress(null, null, "https://private.example.com/") == "https://private.example.com/",
    "A custom legacy server must be preserved");
Check(DesktopPolicy.InitialServerAddress(null, null, "http://localhost:4000/") == "http://localhost:4000/",
    "A customized legacy development port must be preserved");
Check(DesktopPolicy.InitialServerAddress(null, "http://localhost:3210/", null) == "http://localhost:3210/",
    "An explicitly confirmed local development server must be preserved");
Check(DesktopPolicy.InitialServerAddress("http://localhost:3210/", "https://private.example.com/", null) == "http://localhost:3210/",
    "An explicit local CLI URL must win");
Check(DesktopPolicy.InitialServerAddress(null, "https://new.example.com/", "https://old.example.com/") == "https://new.example.com/",
    "The successfully connected preference must win over the legacy file");
foreach (string corrupted in new[] { "", "null", "file:///C:/private", "http://example.com/", "https://user:pass@example.com/" })
    Check(DesktopPolicy.InitialServerAddress(null, corrupted, corrupted) == DesktopPolicy.DefaultServerAddress,
        "An invalid saved address must fall back safely");
Check(DesktopPolicy.InitialServerAddress("  invalid-address  ", null, null) == "invalid-address",
    "An invalid explicit address must remain visible for correction, never silently authorize another server");
string invite = "https://private.example.com/?room=test#invite=private-test-token";
Check(DesktopPolicy.InitialServerAddress(invite, null, null) == invite, "An explicit invitation must be preserved");
Check(DesktopPolicy.ServerUri(invite).GetLeftPart(UriPartial.Authority) + "/" == "https://private.example.com/",
    "Saving a successful origin must strip room/invitation secrets");
Check(DesktopPolicy.NavigationSucceeded(true, 200), "Successful navigation rejected");
foreach (var result in new[] { (false, 0), (false, 200), (true, 0), (true, 404), (true, 503) })
    Check(!DesktopPolicy.NavigationSucceeded(result.Item1, result.Item2), "Failed navigation must not save the address");
var localFailure = DesktopPolicy.ConnectionFailure(new Uri("http://localhost:3210/?secret=hidden"), "CannotConnect", 0);
Check(localFailure.Contains("连接默认服务") && localFailure.Contains("不包含同传服务端"),
    "Local failure must explain the missing backend and offer an online recovery");
Check(!localFailure.Contains("hidden"), "Connection errors must not disclose URL query credentials");
Check(DesktopPolicy.ConnectionFailure(new Uri("https://private.example.com/"), "Unknown", 503).Contains("HTTP 503"),
    "Server errors must include actionable HTTP status");
Check(DesktopPolicy.ConnectionFailure(new Uri("https://private.example.com/"), "HostNameNotResolved", 0).Contains("HostNameNotResolved"),
    "Network failure must retain the diagnostic status");
Check(DesktopPolicy.ConnectionFailure(new Uri("https://private.example.com/"), "CertificateIsInvalid", 0).Contains("不要绕过"),
    "Certificate failures must not encourage bypassing security");

var packets = new List<byte[]>();
var packetizer = new AudioPacketizer(packets.Add);
var audio = Enumerable.Range(0, 11520).Select(index => (byte)(index % 251)).ToArray();
for (int offset = 0; offset < audio.Length;)
{
    int size = Math.Min(audio.Length - offset, offset % 2 == 0 ? 478 : 602);
    packetizer.Push(audio.AsSpan(offset, size));
    offset += size;
}
Check(packets.Count == 4 && packets.All(packet => packet.Length == 2880), "Incorrect PCM framing");
Check(packets.SelectMany(packet => packet).SequenceEqual(audio), "PCM corrupted across native buffer boundaries");
Check(packetizer.PendingBytes == 0, "Unexpected retained audio");
packetizer.Push(new byte[100]);
Check(packetizer.PendingBytes == 100, "Partial packet was lost");
bool oddRejected = false;
try { packetizer.Push(new byte[1]); } catch (ArgumentException) { oddRejected = true; }
Check(oddRejected, "Incomplete PCM16 sample accepted");
Check(Marshal.SizeOf<AudioInterop.WaveFormat>() == 18, "WAVEFORMATEX ABI mismatch");
Check(Marshal.SizeOf<AudioInterop.ActivationParams>() == 12, "AUDIOCLIENT_ACTIVATION_PARAMS ABI mismatch");
Check(Marshal.SizeOf<AudioInterop.PropVariant>() == 24, "x64 PROPVARIANT ABI mismatch");
Check(Marshal.OffsetOf<AudioInterop.PropVariant>("BlobData").ToInt32() == 16, "VT_BLOB pointer alignment mismatch");

// Regression: screenHeight * 60% and a fixed-height window could never put the actual subtitle at the bottom.
foreach (var screen in new[] {
    (left: 0, top: 0, width: 1366, height: 768),
    (left: 0, top: 0, width: 1920, height: 1080),
    (left: -2560, top: -240, width: 2560, height: 1440),
    (left: 1920, top: -2160, width: 3840, height: 2160),
})
foreach (double scale in new[] { 1.0, 1.25, 1.5, 2.0 })
foreach (double measuredHeight in new[] { 64.0, 112.5, 310.0 })
{
    OverlayBounds Place(double percent) => OverlayPlacement.Calculate(screen.left, screen.top,
        screen.width, screen.height, measuredHeight, scale, percent);
    var top = Place(0);
    var middle = Place(50);
    var bottom = Place(100);
    int margin = (int)Math.Ceiling(OverlayPlacement.EdgeMarginDip * scale);
    Check(top.Top == screen.top + margin, "0% must place the panel at the screen top inset");
    Check(bottom.Top + bottom.Height == screen.top + screen.height - margin,
        "100% must put the ACTUAL panel bottom at the screen bottom inset at every DPI");
    Check(Math.Abs((middle.Top + middle.Height / 2.0) - (screen.top + screen.height / 2.0)) <= 0.5,
        "50% must center the whole panel, not its top edge");
    Check(top.Height == (int)Math.Ceiling(measuredHeight * scale), "Panel height must use measured WPF DIPs");
    Check(top.Top <= middle.Top && middle.Top <= bottom.Top, "The vertical slider must move monotonically");
    Check(bottom.Left >= screen.left && bottom.Left + bottom.Width <= screen.left + screen.width &&
        bottom.Top >= screen.top && bottom.Top + bottom.Height <= screen.top + screen.height, "Subtitle bounds left the selected monitor");
    Check(Place(-20) == top && Place(180) == bottom, "Position range must clamp to 0–100%");
}
var shortPanel = OverlayPlacement.Calculate(0, 0, 1366, 768, 64, 1, 100);
var wrappedPanel = OverlayPlacement.Calculate(0, 0, 1366, 768, 192, 1, 100);
Check(shortPanel.Top + shortPanel.Height == wrappedPanel.Top + wrappedPanel.Height &&
    wrappedPanel.Top < shortPanel.Top, "Bottom anchoring must survive font changes or a longer wrapped translation");
var oversized = OverlayPlacement.Calculate(-1920, 0, 1920, 1080, 5000, 2, 100);
Check(oversized.Top == 16 && oversized.Height == 1048, "Oversized panels must remain within the monitor");
var tiny = OverlayPlacement.Calculate(0, 0, 1, 1, 100, 2, 100);
Check(tiny == new OverlayBounds(0, 0, 1, 1), "Tiny screens must not create invalid native window bounds");
var invalid = OverlayPlacement.Calculate(0, 0, 1920, 1080, double.NaN, double.NaN, double.NaN);
Check(invalid.Width > 0 && invalid.Height > 0 && invalid.Top >= 0, "Non-finite values must not reach Win32 geometry");
Check(DesktopPolicy.IsLocalDocument(DesktopPolicy.LocalAppAddress), "Packaged entrypoint must be trusted");
foreach (var url in new[] { "https://dota2.largeniu.com/index.html", "https://dota-coach.local.evil.test/index.html",
    "http://dota-coach.local/index.html", "https://user@dota-coach.local/index.html", "https://dota-coach.local:444/index.html",
    "https://dota-coach.local/index.html?remote=1", "https://dota-coach.local/other.html", "file:///index.html" })
    Check(!DesktopPolicy.IsLocalDocument(url), "Remote/untrusted page must never receive the native bridge");
using (var good = System.Text.Json.JsonDocument.Parse("""{"protocolVersion":1,"privacy":"connection","output":"text","sampleRate":24000,"channels":1}"""))
    Check(NativeVoiceChannel.AcceptsPrivateListener(good.RootElement), "Expected private listener handshake rejected");
using (var bad = System.Text.Json.JsonDocument.Parse("""{"protocolVersion":1,"privacy":"room","output":"audio","sampleRate":24000,"channels":1}"""))
    Check(!NativeVoiceChannel.AcceptsPrivateListener(bad.RootElement), "Public audio handshake must be rejected");
var history = new ConversationStore();
for (int i = 0; i < 310; i++)
{
    history.Accept(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "subtitle.source.final", streamId = "a",
        turnId = $"t{i}", text = "Go Roshan", speaker = "dota2.exe", sourceKind = "process", role = "coach" }));
    history.Accept(System.Text.Json.JsonSerializer.SerializeToElement(new { type = "subtitle.target.final", streamId = "a",
        turnId = $"t{i}", text = "打肉山", speaker = "dota2.exe", sourceKind = "process", role = "coach" }));
}
Check(history.Snapshot().Length == 300, "Native conversation retention must be bounded");
Check(history.Snapshot()[0].source == "Go Roshan" && history.Snapshot()[0].target == "打肉山", "Native source/translation pairing lost");
history.Clear();
Check(history.Snapshot().Length == 0, "Room leave must clear native conversation history");
var preferences = OverlayPreferences.Normalize(System.Text.Json.JsonSerializer.SerializeToElement(new {
    enabled = "corrupt", top = 300, fontSize = -20, targetColor = "<script>", monitor = 1.8, opacity = 5,
}));
Check(preferences.GetProperty("enabled").GetBoolean(), "Malformed toggle must normalize to a boolean");
Check(preferences.GetProperty("top").GetDouble() == 100 && preferences.GetProperty("fontSize").GetDouble() == 16,
    "Native subtitle preferences must clamp before reaching WPF");
Check(preferences.GetProperty("targetColor").GetString() == "#b8f3cc" &&
    preferences.GetProperty("monitor").GetInt32() == 1, "Malformed color or fractional monitor must normalize");
Console.WriteLine($"PASS: {count} core assertions (subtitle placement/DPI, startup/migration, origin security, PCM, Windows ABI).");
if (args.Length == 2 && args[0] == "--transport") await TransportTests.Run(args[1]);
