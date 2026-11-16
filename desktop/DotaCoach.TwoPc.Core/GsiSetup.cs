using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

public sealed record GsiInstallResult(string Path, bool Changed);

/// <summary>Writes this client's own Dota GSI cfg; never overwrites a file the user changed.</summary>
public static class GsiSetup
{
    public const string GameConfigName = "gamestate_integration_dota2_coach_live.cfg";
    public const string SpectatorConfigName = "gamestate_integration_dota2_coach_spectator.cfg";
    public static string StateDirectoryFor(string role) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "TwoPc", role);
    public static string Token(string directory)
    {
        Directory.CreateDirectory(directory);
        string file = Path.Combine(directory, "gsi-identity.dpapi");
        if (!File.Exists(file)) {
            byte[] encrypted = Transform(Encoding.ASCII.GetBytes(Wire.Secret()), protect: true);
            using var stream = new FileStream(file, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            stream.Write(encrypted);
        }
        byte[] stored = File.ReadAllBytes(file);
        if (stored.Length > 16384) throw new IOException("Invalid GSI identity");
        string token = Encoding.ASCII.GetString(Transform(stored, protect: false));
        if (token.Length != 64 || !token.All(Uri.IsHexDigit)) throw new IOException("Invalid GSI identity");
        return token;
    }
    public static string FindInstall(string stateDirectory)
    {
        string remembered = Path.Combine(stateDirectory, "dota-install.txt");
        if (File.Exists(remembered)) return File.ReadAllText(remembered).Trim();
        var steamRoots = new List<string>();
        if (SteamLaunch.FindSteamRoot() is { } registered) steamRoots.Add(registered);
        steamRoots.AddRange([@"C:\Steam", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam")]);
        foreach (var root in steamRoots) {
            string direct = Path.Combine(root, @"steamapps\common\dota 2 beta");
            if (Directory.Exists(Path.Combine(direct, @"game\dota"))) return direct;
            string libraries = Path.Combine(root, @"steamapps\libraryfolders.vdf");
            if (!File.Exists(libraries)) continue;
            foreach (Match m in Regex.Matches(File.ReadAllText(libraries), "\"path\"\\s+\"([^\"]+)\"")) {
                string path = Path.Combine(m.Groups[1].Value.Replace(@"\\", @"\"), @"steamapps\common\dota 2 beta");
                if (Directory.Exists(Path.Combine(path, @"game\dota"))) return path;
            }
        }
        return @"C:\Steam\steamapps\common\dota 2 beta";
    }
    /// <summary>Changed=true means Dota must be restarted once to load the file.</summary>
    public static GsiInstallResult Install(string install, string config, string stateDirectory, string fileName = GameConfigName)
    {
        if (fileName is not (GameConfigName or SpectatorConfigName)) throw new ArgumentException("Unknown GSI config name");
        string dota = Path.GetFullPath(Path.Combine(install, @"game\dota"));
        if (!Directory.Exists(dota)) throw new IOException("Dota安装目录不存在");
        string target = Path.Combine(dota, @"cfg\gamestate_integration", fileName);
        string receipt = Path.Combine(stateDirectory, fileName == GameConfigName ? "installed-gsi.json" : "installed-spectator-gsi.json");
        string expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(config)));
        Directory.CreateDirectory(stateDirectory);
        if (File.Exists(target)) {
            string actual = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(target)));
            if (actual == expected) {
                File.WriteAllText(Path.Combine(stateDirectory, "dota-install.txt"), install);
                return new(target, false); // No rewrite/reload if the persisted identity is unchanged.
            }
            if (!File.Exists(receipt)) throw new IOException("已有同名GSI配置，不会覆盖");
            using var doc = JsonDocument.Parse(File.ReadAllText(receipt));
            if (!string.Equals(doc.RootElement.GetProperty("path").GetString(), target, StringComparison.OrdinalIgnoreCase) ||
                doc.RootElement.GetProperty("sha256").GetString() != actual)
                throw new IOException("GSI配置已被修改，保留原文件");
        }
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, config, new UTF8Encoding(false));
        File.WriteAllText(receipt, JsonSerializer.Serialize(new { path = target, sha256 = expected }));
        File.WriteAllText(Path.Combine(stateDirectory, "dota-install.txt"), install);
        return new(target, true);
    }
    /// <summary>True once this role has written its cfg before, so later starts can reconnect without a click.</summary>
    public static bool WasInstalled(string stateDirectory, string fileName) => File.Exists(Path.Combine(stateDirectory,
        fileName == GameConfigName ? "installed-gsi.json" : "installed-spectator-gsi.json"));
    // Current-user DPAPI; no machine-wide protection and no interactive credential prompts.
    private static byte[] Transform(byte[] bytes, bool protect)
    {
        var input = new Blob { Count = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        Blob output = default; IntPtr description = IntPtr.Zero;
        try {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            bool ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output) :
                CryptUnprotectData(ref input, out description, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new Win32Exception(Marshal.GetLastWin32Error());
            byte[] result = new byte[output.Count]; Marshal.Copy(output.Data, result, 0, result.Length); return result;
        } finally {
            Marshal.Copy(new byte[bytes.Length], 0, input.Data, bytes.Length); Marshal.FreeHGlobal(input.Data);
            if (output.Data != IntPtr.Zero) LocalFree(output.Data);
            if (description != IntPtr.Zero) LocalFree(description);
        }
    }
    [StructLayout(LayoutKind.Sequential)] private struct Blob { public int Count; public IntPtr Data; }
    [DllImport("crypt32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CryptProtectData(ref Blob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref Blob input, out IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out Blob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
}
