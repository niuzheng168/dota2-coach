using DotaCoach.TwoPc;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;

if (args.Length is < 1 or > 2) throw new ArgumentException("Pass the verified 9097559249 v2 JSON [original v1 JSON].");
const string id = "9097559249";
var temp = Directory.CreateTempSubdirectory("postgame-import-contract-");
try {
    using var archive = new MatchArchive(temp.FullName);
    try {
        archive.ImportReplay(id, args[0]);
        throw new Exception("Unsealed archive accepted a replay.");
    } catch (InvalidOperationException) { }
    archive.Seal(id, 1487.433); // disposable contract fixture, never the user's archive
    if (args.Length == 2) {
        if (Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(args[1]))) !=
            "AC3B302073A67EFD0ABB7E247C5C88A7F847DBC56555BB45B2F7447937F1EF14")
            throw new Exception("Compatibility check needs the unmodified original v1 bytes.");
        archive.ImportReplay(id, args[1]);
        var old = archive.Read(id, includeReplay: true);
        if (old.Count != 3599 || old.Any(x => x.Data.TryGetProperty("economy", out _)))
            throw new Exception("Importer changed the legacy economy-free timeline.");
    }
    archive.ImportReplay(id, args[0]);
    var replay = archive.Read(id, includeReplay: true);
    if (archive.Read(id, includeReplay: false).Count != 0 || replay.Count != 3599 ||
        replay[^1].Kind != "replay-end" || replay.Any(e => e.Source != "postgame-replay") ||
        !replay.Any(e => e.Kind == "replay-ward" && e.Data.GetProperty("visibleRadiant").ValueKind ==
            System.Text.Json.JsonValueKind.False && e.Data.GetProperty("visibleDire").ValueKind ==
            System.Text.Json.JsonValueKind.True))
        throw new Exception("Wrong postgame data or replay appeared on the live-only read.");
    var heroes = replay.Where(e => e.Kind == "replay-hero").ToArray();
    if (heroes.Count(e => e.Data.TryGetProperty("economy", out _)) != 3170 ||
        heroes.Any(e => e.Data.GetProperty("hero").ValueKind == JsonValueKind.Null && e.Data.TryGetProperty("economy", out _)))
        throw new Exception("Incorrect farm sample coverage.");
    // Small strict-header imports exercise rejection without storing another large fixture.
    var root = JsonNode.Parse(File.ReadAllBytes(args[0]))!.AsObject();
    var events = root["events"]!.AsArray();
    var resolved = events.First(e => e!["data"]!["economy"] is not null)!.DeepClone();
    var end = events[^1]!.DeepClone();
    string test = Path.Combine(temp.FullName, "schema-check.json");
    void Attempt(string fields, bool accept, bool deselect = false, bool wrongKind = false) {
        var h = resolved.DeepClone();
        h["data"]!["economy"] = JsonNode.Parse(fields);
        if (deselect) h["data"]!["hero"] = null;
        var last = end.DeepClone();
        if (wrongKind) last["data"]!["economy"] = JsonNode.Parse(fields);
        root["events"] = new JsonArray(h, last);
        File.WriteAllText(test, root.ToJsonString());
        try { archive.ImportReplay(id, test); }
        catch (InvalidDataException) when (!accept) { return; }
        if (!accept) throw new Exception("Importer accepted an invalid economy record: " + fields);
    }
    Attempt("""{"lastHits":0}""", true); // sparse, observed zero is valid
    foreach (var invalid in new[] { "{}", "null", """{"denies":-1}""", """{"currentXP":1.5}""",
        """{"totalEarnedGold":"100"}""", """{"netWorth":null}""", """{"creepKillGold":2147483648}""",
        """{"currentGold":100}""" }) Attempt(invalid, false);
    Attempt("""{"lastHits":0}""", false, deselect: true);
    Attempt("""{"lastHits":0}""", false, wrongKind: true);
    Console.WriteLine($"Imported {replay.Count} real events, 3170 farm rows; live-only read stayed empty. " +
        $"Legacy fixture checked: {args.Length == 2}; sparse and invalid counter cases passed.");
} finally {
    if (temp.Parent?.FullName == Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) &&
        temp.Name.StartsWith("postgame-import-contract-", StringComparison.Ordinal))
        temp.Delete(recursive: true);
}
