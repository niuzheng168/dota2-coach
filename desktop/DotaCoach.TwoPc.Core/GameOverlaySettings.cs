using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>How the game machine draws over Dota: reminder subtitles and the enemy panel. Persisted locally; the coach never sees it.</summary>
public sealed record GameOverlaySettings
{
    /// <summary>AI farming advice (the coach's "游戏字幕" switch still decides whether it is sent at all).</summary>
    public bool ShowAdvice { get; init; } = true;
    /// <summary>Plus spectating subtitles about enemies leaving vision (kind local-vision-observation).</summary>
    public bool ShowAlerts { get; init; } = true;
    /// <summary>The small heading line naming the source, e.g. "视觉观察 · 未经核验".</summary>
    public bool ShowTitle { get; init; } = true;
    /// <summary>0 = flush left, 50 = centred, 100 = flush right; the box never leaves the game area.</summary>
    public int X { get; init; } = 50;
    /// <summary>0 = flush top, 50 = middle, 100 = flush bottom.</summary>
    public int Y { get; init; } = 10;
    /// <summary>Font size, % of the default (28 px at 720p, scaled with the game height).</summary>
    public int Size { get; init; } = 100;
    /// <summary>Widest the box grows before the text wraps, % of the game width.</summary>
    public int Width { get; init; } = 70;
    public string TextColor { get; init; } = "#FFD700";
    public string TitleColor { get; init; } = "#F0E68C";
    public string BackgroundColor { get; init; } = "#081018";
    public int TextOpacity { get; init; } = 100;
    public int BackgroundOpacity { get; init; } = 82;
    /// <summary>The enemy panel (Plus spectating): enemy heroes' levels and items at the left edge of the game.</summary>
    public bool ShowEnemies { get; init; } = true;
    /// <summary>0 = flush top, 100 = flush bottom.</summary>
    public int EnemyY { get; init; } = 40;
    /// <summary>Icon size, % of the default (14 px rows at 720p, scaled with the game height).</summary>
    public int EnemySize { get; init; } = 100;
    public int EnemyOpacity { get; init; } = 75;
    /// <summary>Show sightings and candidate wards on the minimap (the coach-confirmed box, else this machine's setting below).</summary>
    public bool ShowWards { get; init; } = true;
    /// <summary>Short-lived lane estimates are only displayed on a coach-confirmed HUD.</summary>
    public bool ShowLaneWaves { get; init; } = true;
    /// <summary>Enemies that left the friend team's vision: last sighting, facing and the circle they are probably in.</summary>
    public bool ShowFog { get; init; } = true;
    /// <summary>Dota's minimap is on the right (the HUD option to flip it).</summary>
    public bool MinimapRight { get; init; }
    /// <summary>Minimap size, % of the default HUD's (raise it for Dota's extra-large minimap).</summary>
    public int MinimapSize { get; init; } = 100;

    public const int MinSize = 50, MaxSize = 250, MinWidth = 20, MaxWidth = 95, MinTextOpacity = 20, MinMinimap = 70, MaxMinimap = 160;
    private static readonly Regex Hex = new("^#[0-9A-Fa-f]{6}$");

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "TwoPc", "game-overlay.json");
    public void Validate()
    {
        if (X is < 0 or > 100 || Y is < 0 or > 100 || Size is < MinSize or > MaxSize || Width is < MinWidth or > MaxWidth ||
            TextOpacity is < MinTextOpacity or > 100 || BackgroundOpacity is < 0 or > 100 ||
            EnemyY is < 0 or > 100 || EnemySize is < MinSize or > MaxSize || EnemyOpacity is < MinTextOpacity or > 100 ||
            MinimapSize is < MinMinimap or > MaxMinimap ||
            new[] { TextColor, TitleColor, BackgroundColor }.Any(c => c == null || !Hex.IsMatch(c)))
            throw new ArgumentException("字幕、对手面板或小地图的位置、大小、透明度或颜色无效。");
    }
    /// <summary>The minimap as this player set it: the preview outline, and where ward marks go when the coach has no
    /// confirmed box for this resolution. Never a minute-route calibration.</summary>
    public MinimapLayout Minimap(int width, int height)
    {
        var standard = MinimapLayout.Suggest(width, height, MinimapRight);
        double size = Math.Min(standard.Size * MinimapSize / 100, height * .45);
        return new(width, height, MinimapRight ? width - size : 0, height - size, size);
    }
    /// <summary>Whether a reminder of this kind gets its subtitle drawn in game; map marks are not affected.</summary>
    public bool Shows(string kind) => kind switch {
        "local-vision-observation" => ShowAlerts,
        "experimental-bot-advice" or "visible-farming-advice" or MinuteFarmContext.Kind => ShowAdvice,
        _ => true,
    };
    /// <summary>Top-left of a caption box: the free space on each axis split by X/Y, so 0 and 100 hug the edges.</summary>
    public (double Left, double Top) Place(double areaWidth, double areaHeight, double boxWidth, double boxHeight) =>
        (Math.Max(0, areaWidth - boxWidth) * X / 100, Math.Max(0, areaHeight - boxHeight) * Y / 100);
    /// <summary>Top of the enemy panel at the left edge, placed like the subtitle's Y.</summary>
    public double EnemyTop(double areaHeight, double panelHeight) => Math.Max(0, areaHeight - panelHeight) * EnemyY / 100;
    public static (byte R, byte G, byte B) Rgb(string hex)
    {
        int value = Convert.ToInt32(hex[1..], 16);
        return ((byte)(value >> 16), (byte)(value >> 8), (byte)value);
    }
    public static GameOverlaySettings Load(string file)
    {
        try {
            if (!File.Exists(file) || new FileInfo(file).Length > 8192) return new();
            var settings = JsonSerializer.Deserialize<GameOverlaySettings>(File.ReadAllText(file), Wire.Json) ?? new();
            settings.Validate(); return settings;
        } catch { return new(); }
    }
    public void Save(string file)
    {
        Validate();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string temporary = file + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try {
            File.WriteAllText(temporary, JsonSerializer.Serialize(this, Wire.Json));
            File.Move(temporary, file, overwrite: true);
        } finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
