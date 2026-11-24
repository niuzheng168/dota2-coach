using System.Text.Json;

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
/// area it was last seen in and the place it faced, at most once per <see cref="CooldownMs"/>. Brief flickers at the
/// edge of vision do not count. GSI cannot tell a death from walking into fog: an enemy that vanished as the friend
/// team's kill count rose (within <see cref="FogTracker.KillWindow"/> s) is taken as killed and not announced
/// (the user's choice, 2026-11-24; about a fifth of these subtitles in 47 replays were deaths).
/// </summary>
public sealed class VisionLossTracker
{
    /// <summary>The user's default (2026-11-24): three seconds announced too often. The coach's setting overrides it.</summary>
    public const int DefaultGoneMs = 5000, MaxGoneSeconds = 30;
    public int MinVisibleMs { get; init; } = 3000;
    public int GoneMs { get; set; } = DefaultGoneMs;
    public int CooldownMs { get; init; } = 20000;
    /// <summary>The 7.42 map for place names (camps, towers, objectives); without it the coarse <see cref="MapArea"/>.</summary>
    public CoachMap? Map { get; set; }
    private sealed class Sighting { public long RunStart, LastVisible, LastAlert = long.MinValue / 2; public double X, Y; public int? Yaw; public bool Announced; }
    private readonly Dictionary<string, Sighting> heroes = new(StringComparer.Ordinal);
    private readonly List<long> kills = []; // receipts of unclaimed rises of the friend team's kill count
    private string? match;
    private int ownTeam, lastKills = -1;
    private long? lastReceipt;

    public void Clear() { heroes.Clear(); kills.Clear(); match = null; ownTeam = 0; lastKills = -1; lastReceipt = null; }

    public List<PlusAlert> Update(SpectatorSnapshot s, HeroNames names)
    {
        var alerts = new List<PlusAlert>();
        long now = s.ReceivedAt;
        if (now < lastReceipt) return alerts;
        if (!TeamViewEvidence.IsTeamView(s)) { Clear(); return alerts; }
        // Missing packets are not evidence that a previously visible enemy walked into fog.
        if (s.MatchId != match || s.OwnTeam != ownTeam || now - lastReceipt >= TeamViewEvidence.FreshMs) {
            heroes.Clear(); kills.Clear(); lastKills = -1; // a rise during the gap names no one
        }
        match = s.MatchId; ownTeam = s.OwnTeam!.Value; lastReceipt = now;
        if (s.Kills is var (radiant, dire)) {
            int own = ownTeam == 2 ? radiant : dire;
            if (lastKills >= 0) for (int k = lastKills; k < own && kills.Count < 10; k++) kills.Add(now);
            lastKills = own;
        }
        // Illusions and clones share the hero's name; the first entry stands for it, as on the scan page.
        foreach (var unit in TeamViewEvidence.VisibleHeroes(s).Where(u => u.Team != ownTeam)) {
            if (!heroes.TryGetValue(unit.Name, out var h)) {
                if (heroes.Count >= 10) continue;
                heroes[unit.Name] = h = new() { RunStart = now };
            }
            else if (h.Announced || now - h.LastVisible >= GoneMs) { h.RunStart = now; h.Announced = false; }
            h.LastVisible = now; h.X = unit.X; h.Y = unit.Y; h.Yaw = TeamViewEvidence.Facing(unit.Yaw);
        }
        // Each rise claims the unannounced enemy that vanished nearest to it, before or after; its absence is closed silently.
        long window = (long)(FogTracker.KillWindow * 1000);
        kills.RemoveAll(k => now - k > window);
        while (kills.Count > 0 && heroes.Values.Where(h => !h.Announced && h.LastVisible < now && Math.Abs(h.LastVisible - kills[0]) <= window)
                   .OrderBy(h => Math.Abs(h.LastVisible - kills[0])).FirstOrDefault() is { } killed) {
            killed.Announced = true; kills.RemoveAt(0);
        }
        foreach (var (hero, h) in heroes) {
            if (h.Announced || h.LastVisible == now || now - h.LastVisible < GoneMs) continue;
            h.Announced = true; // one alert per absence, even when the cooldown suppresses it
            if (h.LastVisible - h.RunStart < MinVisibleMs || now - h.LastAlert < CooldownMs) continue;
            h.LastAlert = now;
            string place = Place(h.X, h.Y);
            alerts.Add(new(now, "vision-lost", hero, $"{names[hero]} 离开视野 · 最后在{place}" +
                (Ahead(h.X, h.Y, h.Yaw, place) is { } toward ? "，朝" + toward : "")));
        }
        return alerts;
    }
    private string Place(double x, double y) => Map?.Describe(x, y, ownTeam) ?? MapArea.Name(x, y, ownTeam);
    /// <summary>The first differently named place along the hero's last facing, or null. A facing is where the hero
    /// looked as it vanished, not proof of where it went.</summary>
    private string? Ahead(double x, double y, int? yaw, string place)
    {
        if (yaw is not int degrees) return null;
        double a = degrees * Math.PI / 180;
        foreach (double d in HeadingSteps) {
            double px = x + d * Math.Cos(a), py = y + d * Math.Sin(a);
            if (Math.Abs(px) > 8200 || Math.Abs(py) > 8200) return null; // off the playable map
            if (Place(px, py) is var next && next != place) return next;
        }
        return null;
    }
    private static readonly double[] HeadingSteps = [1200, 2000, 2800];
}

/// <summary>Source time, never the time at which the coach happened to poll the file.</summary>
public sealed record ScanStamp(string Run, long WrittenMs, string MatchId, int OwnTeam, double Clock)
{
    // Some v0.4.8 files use a whole second for equipment and a later fraction of the same second for counts.
    public double WardClock { get; init; } = Clock;
    // Age of the scanner's GSI at this write. Keep it separate from the write identity: multiple
    // sub-second HUD reads can legitimately follow the same GSI packet. Absent on legacy v2 files.
    public double? GsiAgeMs { get; init; }
    public bool FreshAt(long unixMs)
    {
        long held = unixMs - WrittenMs;
        return unixMs >= WrittenMs && held is >= 0 and < TeamViewEvidence.FreshMs &&
            (GsiAgeMs is not double age || age >= 0 && held + age < TeamViewEvidence.FreshMs);
    }
}
public sealed record ScanStatus(bool Running, string Text, ScanStamp? Sample = null);

/// <summary>
/// The enemy panel found by scripts/vision/hero_scan.py on this machine, read from its live file: enemy levels,
/// items and HUD stats from Plus team view, for the match being spectated. The panel's icons sit in icons/ beside the file.
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
            using var doc = JsonDocument.Parse(stream, new JsonDocumentOptions { MaxDepth = 8, AllowDuplicateProperties = false });
            var root = doc.RootElement;
            if (root.GetProperty("version").GetInt32() != 2) return (new(false, "视觉扫描版本不符（请更新 hero_scan.py），已忽略"), null, none);
            double wall = Math.Round(root.GetProperty("written").GetDouble() * 1000);
            if (root.GetProperty("stopped").GetBoolean())
                return (new(false, "视觉扫描已停止"), null, none);
            // The writer is local. A timestamp from the future is not a fresh file either.
            if (!Wire.Finite(wall, nowUnixMs - StaleMs, nowUnixMs)) return (new(false, "视觉扫描未运行（文件时间无效或已过期）"), null, none);
            long written = (long)wall;
            string? scanMatch = root.TryGetProperty("matchId", out var m) && m.ValueKind == JsonValueKind.String ? m.GetString() : null;
            if (root.GetProperty("mode").GetString() != "team-view")
                return (new(true, "视觉扫描运行中，但不是 Plus 队伍视角：不显示对手面板"), null, none);
            if (!TeamViewEvidence.RealMatch(scanMatch) || scanMatch != matchId)
                return (new(true, "视觉扫描运行中，但对局与观战 GSI 不一致：不显示对手面板"), null, none);
            bool hasGsiAge = root.TryGetProperty("gsiAge", out var age); // absent on the v0.4.8 whole-second envelope
            if (!root.TryGetProperty("ownTeam", out var side) || side.GetInt32() is not (2 or 3) ||
                !root.TryGetProperty("clock", out var time) || !Wire.Finite(time.GetDouble(), -300, 43_200) ||
                root.GetProperty("run").GetString() is not { Length: > 0 and <= 64 } run ||
                string.IsNullOrWhiteSpace(run) || hasGsiAge && age.ValueKind != JsonValueKind.Number)
                return (new(true, "视觉扫描缺少新鲜的队伍视角或游戏时间"), null, none);
            double clock = time.GetDouble();
            var stamp = new ScanStamp(run, written, scanMatch!, side.GetInt32(), clock) { GsiAgeMs = hasGsiAge ? age.GetDouble() * 1000 : null };
            if (!stamp.FreshAt(nowUnixMs)) return (new(true, "视觉扫描运行中，但源数据已过期或时间无效"), null, none);
            var enemies = root.GetProperty("enemies");
            if (enemies.ValueKind != JsonValueKind.Array || enemies.GetArrayLength() > 5)
                return (new(true, "视觉扫描的对手数据无效，已忽略"), null, none);
            var board = new EnemyBoard(enemies.Deserialize<EnemyHero[]>(Wire.Json)!);
            if (!Wire.ValidBoard(board)) return (new(true, "视觉扫描的对手数据无效，已忽略"), null, none);
            // v0.4.8 stamps the envelope in whole GSI seconds, but individual reads within that second are precise.
            var wards = Wards(enemies, !hasGsiAge && clock == Math.Floor(clock) ? Math.Min(43_200, clock + .99) : clock);
            // A roster without a single HUD read is a running scanner, not an available empty inventory.
            if (!board.Heroes.Any(h => h.Level != null || h.Items != null || h.Neutral != null || h.Stats != null) && !wards.Values.Any(r => r.Length != 0))
                return (new(true, "视觉扫描运行中，等待读取可见敌方英雄"), null, none);
            double observed = wards.Values.SelectMany(rs => rs).Select(r => r.Clock).Append(clock).Max();
            return (new(true, "视觉扫描运行中", stamp with { WardClock = observed }), board, wards);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or KeyNotFoundException or
                                   InvalidOperationException or FormatException)
        { return (new(false, "视觉扫描文件暂不可读"), null, none); }
    }
    /// <summary>The "wards" reads of each enemy row; a malformed list is left out rather than trusted.</summary>
    private static Dictionary<string, WardReading[]> Wards(JsonElement enemies, double latest)
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
                var list = reads.EnumerateArray().Select(r =>
                    r.GetProperty("clock").GetDouble() is var c && Wire.Finite(c, -300, latest)
                        ? new WardReading(c, Count(r, "observer"), Count(r, "sentry"), Count(r, "unknown"), First(r, c)) : throw new FormatException()).ToArray();
                // A change can't start before the preceding counts were last read.
                for (int i = 1; i < list.Length; i++)
                    if ((list[i].First ?? list[i].Clock) <= list[i - 1].Clock) throw new FormatException();
                result[row.GetProperty("hero").GetString()!] = list;
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
