using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

// Explicit, bounded local-bot acceptance run. Not enabled by normal application startup.
// The configuration contains paths, never credentials. Artifacts stay outside the release.
public sealed record E2ERun(string Directory, string GsiConfigPath, string FoundryEnvFile,
    string Model, string LessonsPath, int MaxModelCalls = 3, bool CaptureOverlayEvidence = false,
    string MapReferencePath = "")
{
    public static E2ERun? Load(string[] args)
    {
        if (args.Length == 0) return null;
        if (args.Length != 2 || args[0] != "--local-bot-e2e")
            throw new ArgumentException("Expected --local-bot-e2e <configuration.json>");
        var run = JsonSerializer.Deserialize<E2ERun>(File.ReadAllText(args[1]), Wire.Json)!;
        if (run == null || !Path.IsPathFullyQualified(run.Directory) ||
            !Path.IsPathFullyQualified(run.GsiConfigPath) || run.MaxModelCalls is < 1 or > 6 ||
            !Path.GetFileName(run.GsiConfigPath).StartsWith("gamestate_integration_coach_e2e_", StringComparison.Ordinal))
            throw new ArgumentException("Invalid explicit local-bot test configuration");
        System.IO.Directory.CreateDirectory(run.Directory);
        return run;
    }
    public string PairingPath => Path.Combine(Directory, "pairing.secret");
    public void Trace(string role, string stage, object details)
    {
        // Call sites construct allowlisted records. Never pass a request, config, key or pairing bundle.
        File.AppendAllText(Path.Combine(Directory, role + ".jsonl"),
            JsonSerializer.Serialize(new { at = DateTimeOffset.UtcNow, processId = Environment.ProcessId, stage, details }, Wire.Json) + "\n");
    }
    // Resume only this explicitly named acceptance run, with its unchanged generated file.
    // This lets a client rebuild keep the token already loaded by the local Dota process.
    public string? ResumeGsiToken()
    {
        if (!File.Exists(GsiConfigPath)) return null;
        string hashFile = Path.Combine(Directory, "gsi-sha256.txt");
        if (!File.Exists(hashFile) || !Wire.EqualSecret(File.ReadAllText(hashFile).Trim(),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GsiConfigPath)))))
            throw new IOException("Existing E2E GSI file has changed; refusing to reuse or overwrite it");
        var m = Regex.Match(File.ReadAllText(GsiConfigPath), "\"token\"\\s+\"([A-F0-9]{64})\"");
        return m.Success ? m.Groups[1].Value : throw new IOException("Invalid E2E GSI credential");
    }
    public void ExportGsi(string config)
    {
        if (File.Exists(GsiConfigPath)) {
            _ = ResumeGsiToken();
            if (File.ReadAllText(GsiConfigPath) != config) throw new IOException("Refusing to overwrite an existing GSI file");
            return;
        }
        File.WriteAllText(GsiConfigPath, config);
        File.WriteAllText(Path.Combine(Directory, "gsi-sha256.txt"),
            Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(GsiConfigPath))));
    }
}
