using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>
/// One observation about an enemy hero from Dota Plus live spectating (the friend team's vision), turned
/// into a subtitle on the game machine. Only team-view data qualifies: delayed/full spectating shows both
/// teams in full and never produces alerts. Enemy levels and items are not alerts: they go to the enemy
/// panel (<see cref="ScanFeed"/>).
/// </summary>
public sealed record PlusAlert(long At, string Kind, string Hero, string Text);

/// <summary>Coarse map areas for alert text, named from the player's side.</summary>
public static class MapArea
{
    /// <param name="ownTeam">2 radiant, 3 dire; the friend team being spectated.</param>
    public static string Name(double x, double y, int ownTeam)
    {
        string Side(bool radiant) => radiant == (ownTeam == 2) ? "我方" : "敌方";
        if (x < -4700 && y < -4200) return Side(true) + "高地";
        if (x > 4000 && y > 3600) return Side(false) + "高地";
        if (x < -5300 || y > 5300) return "上路";
        if (y < -5300 || x > 5300) return "下路";
        if (Math.Abs(x - y) < 1300) return "中路";
        if (Math.Abs(x + y) < 1000) return "河道";
        return Side(x + y < 0) + "野区";
    }
}

/// <summary>Chinese hero names from the bundled bilingual dictionary; unknown heroes keep their internal name.</summary>
public sealed class HeroNames
{
    private readonly Dictionary<string, string> names = new(StringComparer.Ordinal);
    public HeroNames() { }
    public HeroNames(string dictionaryFile)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(dictionaryFile));
        foreach (var hero in doc.RootElement.GetProperty("heroes").EnumerateArray())
            if (hero.TryGetProperty("key", out var key) && hero.TryGetProperty("zh", out var zh) &&
                key.GetString() is { } k && zh.GetString() is { Length: > 0 } z) names[k] = z;
    }
    public string this[string hero] => names.TryGetValue(hero, out var zh) ? zh : hero.Replace("npc_dota_hero_", "");
}

/// <summary>
/// Enemy heroes leaving the friend team's vision, from this coach machine's own Plus spectator GSI: an enemy
/// seen for at least <see cref="MinVisibleMs"/> and then missing for <see cref="GoneMs"/> is announced with the
/// area it was last seen in, at most once per <see cref="CooldownMs"/>. Brief flickers at the edge of vision do
/// not count. GSI cannot tell a death from walking into fog, so a hero killed in sight is announced too.
/// </summary>
public sealed class VisionLossTracker
{
    public int MinVisibleMs { get; init; } = 3000;
    public int GoneMs { get; init; } = 3000;
    public int CooldownMs { get; init; } = 20000;
    /// <summary>The 7.42 map for place names (camps, towers, objectives); without it the coarse <see cref="MapArea"/>.</summary>
    public CoachMap? Map { get; init; }
    private sealed class Sighting { public long RunStart, LastVisible, LastAlert = long.MinValue / 2; public double X, Y; public bool Announced; }
    private readonly Dictionary<string, Sighting> heroes = new(StringComparer.Ordinal);
    private string? match;
    private int ownTeam;

    public List<PlusAlert> Update(SpectatorSnapshot s, HeroNames names)
    {
        var alerts = new List<PlusAlert>();
        if (s.Mode != "team-view" || s.OwnTeam is not (2 or 3) || !s.Running || s.Paused) return alerts;
        if (s.MatchId != match || s.OwnTeam != ownTeam) { heroes.Clear(); match = s.MatchId; ownTeam = s.OwnTeam.Value; }
        long now = s.ReceivedAt;
        // Illusions and clones share the hero's name; the first entry stands for it, as on the scan page.
        foreach (var unit in s.Heroes.Where(u => u.Team is 2 or 3 && u.Team != ownTeam && u.Vision > 0).GroupBy(u => u.Name).Select(g => g.First())) {
            if (!heroes.TryGetValue(unit.Name, out var h)) heroes[unit.Name] = h = new() { RunStart = now };
            else if (h.Announced || now - h.LastVisible >= GoneMs) { h.RunStart = now; h.Announced = false; }
            h.LastVisible = now; h.X = unit.X; h.Y = unit.Y;
        }
        foreach (var (hero, h) in heroes) {
            if (h.Announced || h.LastVisible == now || now - h.LastVisible < GoneMs) continue;
            h.Announced = true; // one alert per absence, even when the cooldown suppresses it
            if (h.LastVisible - h.RunStart < MinVisibleMs || now - h.LastAlert < CooldownMs) continue;
            h.LastAlert = now;
            alerts.Add(new(now, "vision-lost", hero, $"{names[hero]} 离开视野 · 最后在{Map?.Describe(h.X, h.Y, ownTeam) ?? MapArea.Name(h.X, h.Y, ownTeam)}"));
        }
        return alerts;
    }
}

public sealed record ScanStatus(bool Running, string Text);

/// <summary>
/// The enemy panel found by scripts/vision/hero_scan.py on this machine, read from its live file: enemy levels
/// and items from Plus team view, for the match being spectated. The panel's icons sit in icons/ beside the file.
/// </summary>
public static class ScanFeed
{
    public const int StaleMs = 10000, MaxFileBytes = 262144;
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "hero-scan", "live.json");

    /// <param name="matchId">The spectated match (Plus GSI); a scan of another match is ignored.</param>
    /// <param name="nowUnixMs">Wall clock, which the scanner also stamps its file with.</param>
    /// <returns>Also each enemy's recent ward reads (oldest first; empty from scanners before ward counts).</returns>
    public static (ScanStatus Status, EnemyBoard? Board, IReadOnlyDictionary<string, WardReading[]> Wards) Poll(string path, string? matchId, long nowUnixMs)
    {
        IReadOnlyDictionary<string, WardReading[]> none = new Dictionary<string, WardReading[]>();
        try {
            if (!File.Exists(path)) return (new(false, "视觉扫描未运行"), null, none);
            // Shared for delete: the scanner replaces the file every half second and must not find it locked.
            using var stream = Shared(path);
            if (stream.Length > MaxFileBytes) return (new(false, "视觉扫描文件异常（过大），已忽略"), null, none);
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8 });
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 2) return (new(false, "视觉扫描版本不符（请更新 hero_scan.py），已忽略"), null, none);
            long written = (long)(root.GetProperty("written").GetDouble() * 1000);
            if (root.TryGetProperty("stopped", out var stopped) && stopped.ValueKind == JsonValueKind.True)
                return (new(false, "视觉扫描已停止"), null, none);
            if (nowUnixMs - written > StaleMs) return (new(false, "视觉扫描未运行（文件已过期）"), null, none);
            string? scanMatch = root.TryGetProperty("matchId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (root.GetProperty("mode").GetString() != "team-view")
                return (new(true, "视觉扫描运行中，但不是 Plus 队伍视角：不显示对手面板"), null, none);
            if (scanMatch == null || scanMatch != matchId)
                return (new(true, "视觉扫描运行中，但对局与观战 GSI 不一致：不显示对手面板"), null, none);
            var enemies = root.GetProperty("enemies");
            var board = new EnemyBoard(enemies.Deserialize<EnemyHero[]>(Wire.Json) ?? []);
            if (!Wire.ValidBoard(board)) return (new(true, "视觉扫描的对手数据无效，已忽略"), null, none);
            return (new(true, "视觉扫描运行中"), board, Wards(enemies));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                                   InvalidOperationException or FormatException)
        { return (new(false, "视觉扫描文件暂不可读"), null, none); }
    }
    /// <summary>The "wards" reads of each enemy row; a malformed list is left out rather than trusted.</summary>
    private static Dictionary<string, WardReading[]> Wards(JsonElement enemies)
    {
        var result = new Dictionary<string, WardReading[]>(StringComparer.Ordinal);
        foreach (var row in enemies.EnumerateArray()) {
            if (!row.TryGetProperty("wards", out var reads) || reads.ValueKind != JsonValueKind.Array || reads.GetArrayLength() > 8) continue;
            static int Count(JsonElement r, string key) => r.TryGetProperty(key, out var v) && v.TryGetInt32(out int n) && n is >= 0 and <= 30
                ? n : throw new FormatException();
            // Since 2026-11-22 an entry per change of the counts: its first and latest read ("first" ≤ "clock").
            static double? First(JsonElement r, double clock) => !r.TryGetProperty("first", out var f) ? null
                : f.GetDouble() is var v && Wire.Finite(v, -300, clock) ? v : throw new FormatException();
            try {
                result[row.GetProperty("hero").GetString()!] = reads.EnumerateArray().Select(r =>
                    r.GetProperty("clock").GetDouble() is var c && Wire.Finite(c, -300, 43_200)
                        ? new WardReading(c, Count(r, "observer"), Count(r, "sentry"), Count(r, "unknown"), First(r, c)) : throw new FormatException()).ToArray();
            } catch (Exception e) when (e is FormatException or InvalidOperationException or KeyNotFoundException) { }
        }
        return result;
    }
    /// <summary>A panel icon the scanner copied beside its live file, or null.</summary>
    public static byte[]? Icon(string liveFile, string key)
    {
        if (!Wire.ValidIconKey(key)) return null;
        try {
            string file = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(liveFile))!, "icons", key + ".png");
            if (!File.Exists(file)) return null;
            using var stream = Shared(file);
            if (stream.Length > Wire.MaxIconBytes) return null;
            var data = new byte[stream.Length];
            stream.ReadExactly(data);
            return Wire.IsIcon(data) ? data : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    /// <summary>Read without locking out the scanner, which writes and replaces these files while the coach runs.</summary>
    private static FileStream Shared(string path) => new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
}

/// <summary>
/// Turns alerts into game subtitles: alerts arriving close together share one subtitle, and an alert still on
/// screen is repeated in front of the new one (oldest dropped first) so a new alert does not erase it unread.
/// </summary>
public sealed class AlertComposer
{
    public const int GatherMs = 700, MaxText = 120;
    private readonly object gate = new();
    private readonly List<PlusAlert> pending = new();
    private readonly List<(string Text, long Until)> shown = new();
    private long firstPending;

    public void Add(IEnumerable<PlusAlert> alerts, long now)
    {
        lock (gate) foreach (var a in alerts) { if (pending.Count == 0) firstPending = now; pending.Add(a); }
    }
    public void Clear() { lock (gate) { pending.Clear(); shown.Clear(); } }
    /// <summary>The subtitle to publish now, or null while alerts are still gathering or none are waiting.</summary>
    public string? Next(long now, int displayMs)
    {
        lock (gate) {
            if (pending.Count == 0 || now - firstPending < GatherMs) return null;
            shown.RemoveAll(s => s.Until <= now);
            shown.AddRange(pending.Select(a => (a.Text, now + displayMs)));
            pending.Clear();
            while (shown.Count > 1 && string.Join("；", shown.Select(s => s.Text)).Length > MaxText) shown.RemoveAt(0);
            string text = string.Join("；", shown.Select(s => s.Text));
            return text.Length <= MaxText ? text : text[..(MaxText - 1)] + "…";
        }
    }
}
