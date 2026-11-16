using System.Text;

namespace DotaCoach.TwoPc;

/// <param name="Options">Dota 2 launch options as Steam saved them, or null when not found.</param>
public sealed record LaunchOptionsInfo(bool Found, string? Options, bool HasGsiFlag, bool ActiveAccount);

/// <summary>
/// Reads Dota's launch options from Steam's localconfig.vdf. File reads only: never opens the
/// game process (anti-cheat) and never writes Steam files (Steam rewrites them while running).
/// </summary>
public static class SteamLaunch
{
    public const string GsiFlag = "-gamestateintegration";
    public static string? FindSteamRoot()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam");
            return key?.GetValue("SteamPath") is string path && Directory.Exists(path) ? Path.GetFullPath(path) : null;
        } catch { return null; }
    }
    private static string? ActiveAccount()
    {
        if (!OperatingSystem.IsWindows()) return null;
        try {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam\ActiveProcess");
            return key?.GetValue("ActiveUser") is int id && id > 0 ? id.ToString() : null;
        } catch { return null; }
    }
    public static LaunchOptionsInfo Inspect(string? steamRoot = null)
    {
        steamRoot ??= FindSteamRoot();
        if (steamRoot == null) return new(false, null, false, false);
        string userdata = Path.Combine(steamRoot, "userdata");
        string? active = ActiveAccount();
        string? file = active == null ? null : Path.Combine(userdata, active, "config", "localconfig.vdf");
        bool isActive = file != null && File.Exists(file);
        if (!isActive) {
            // Steam not running/logged in: fall back to the most recently used account on this PC.
            file = Directory.Exists(userdata) ? Directory.EnumerateDirectories(userdata)
                .Select(d => Path.Combine(d, "config", "localconfig.vdf")).Where(File.Exists)
                .OrderByDescending(File.GetLastWriteTimeUtc).FirstOrDefault() : null;
        }
        if (file == null || new FileInfo(file).Length > 64L * 1024 * 1024) return new(false, null, false, false);
        try {
            string? options = ReadLaunchOptions(File.ReadAllText(file));
            return new(true, options, HasFlag(options), isActive);
        } catch { return new(false, null, false, isActive); }
    }
    public static bool HasFlag(string? options) => options != null &&
        options.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Contains(GsiFlag, StringComparer.OrdinalIgnoreCase);
    /// <summary>The user's existing options with the GSI flag appended once.</summary>
    public static string WithGsiFlag(string? options) => HasFlag(options) ? options!.Trim() : $"{options?.Trim()} {GsiFlag}".Trim();

    /// <summary>Finds apps/570/LaunchOptions anywhere in a KeyValues text document.</summary>
    public static string? ReadLaunchOptions(string vdf, string appId = "570")
    {
        var tokens = Tokens(vdf).GetEnumerator();
        var path = new List<string>();
        string? pendingKey = null;
        while (tokens.MoveNext()) {
            var (text, quoted) = tokens.Current;
            if (!quoted && text == "{") { path.Add(pendingKey ?? ""); pendingKey = null; continue; }
            if (!quoted && text == "}") { if (path.Count > 0) path.RemoveAt(path.Count - 1); pendingKey = null; continue; }
            if (pendingKey == null) { pendingKey = text; continue; }
            if (path.Count >= 2 && string.Equals(path[^2], "apps", StringComparison.OrdinalIgnoreCase) &&
                path[^1] == appId && string.Equals(pendingKey, "LaunchOptions", StringComparison.OrdinalIgnoreCase))
                return text;
            pendingKey = null;
        }
        return null;
    }
    private static IEnumerable<(string Text, bool Quoted)> Tokens(string s)
    {
        int i = 0;
        while (i < s.Length) {
            char c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (c == '/' && i + 1 < s.Length && s[i + 1] == '/') { while (i < s.Length && s[i] != '\n') i++; continue; }
            if (c is '{' or '}') { yield return (c.ToString(), false); i++; continue; }
            if (c == '"') {
                var text = new StringBuilder(); i++;
                while (i < s.Length && s[i] != '"') {
                    if (s[i] == '\\' && i + 1 < s.Length) {
                        i++;
                        text.Append(s[i] switch { 'n' => '\n', 't' => '\t', _ => s[i] });
                    } else text.Append(s[i]);
                    i++;
                }
                i++; yield return (text.ToString(), true); continue;
            }
            int start = i;
            while (i < s.Length && !char.IsWhiteSpace(s[i]) && s[i] is not ('{' or '}' or '"')) i++;
            yield return (s[start..i], true);
        }
    }
}
