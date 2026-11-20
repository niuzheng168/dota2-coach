using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>Local preferences contain credential PATHS, never key contents or game images.</summary>
public sealed record LiveCoachSettings
{
    public bool AutomaticAdvice { get; init; } = true;
    public bool Subtitles { get; init; } = true;
    public bool MinimapMarkers { get; init; } = true;
    public string FoundryEnvFile { get; init; } = DefaultCredentialPath();
    public string Model { get; init; } = "gpt-6-astra";
    public int IntervalSeconds { get; init; } = 20;
    public int RequestLimit { get; init; } = 180;
    /// <summary>How long each AI subtitle/route stays on the game screen after it is published.</summary>
    public int SubtitleSeconds { get; init; } = 3;
    /// <summary>Model answers slower than this are discarded (observed 8.6–10.8 s; 11 s used to drop ~40 %).</summary>
    public int ModelTimeoutSeconds { get; init; } = 15;
    /// <summary>Without a confirmed calibration, use the default bottom-left box scaled from the HUD height.</summary>
    public bool AutoMinimap { get; init; } = true;
    /// <summary>Plus live spectating: enemy levels and items in a panel on the game machine (needs hero_scan.py).</summary>
    public bool EnemyPanel { get; init; } = true;
    /// <summary>Plus live spectating: subtitle on the game machine when an enemy leaves the friend team's vision.</summary>
    public bool EnemyVisionAlerts { get; init; } = true;
    public string BindAddress { get; init; } = "";
    public MinimapLayout[] Calibrations { get; init; } = [];
    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "TwoPc", "coach-settings.json");
    private static string DefaultCredentialPath() =>
        Environment.GetEnvironmentVariable("DOTA_COACH_FOUNDRY_ENV_FILE") ??
        (File.Exists(@"C:\g\foundry.use2.env") ? @"C:\g\foundry.use2.env" :
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "foundry.env"));
    public void Validate()
    {
        if (FoundryEnvFile == null || !Path.IsPathFullyQualified(FoundryEnvFile) ||
            Model == null || !Regex.IsMatch(Model, @"^[A-Za-z0-9_.-]{1,100}$") ||
            IntervalSeconds is < 12 or > 300 || RequestLimit is < 1 or > 1000 ||
            SubtitleSeconds is < 1 or > Wire.MaxReminderMs / 1000 || ModelTimeoutSeconds is < 8 or > 20 ||
            Calibrations == null || Calibrations.Length > 16 ||
            Calibrations.Any(c => c == null || !c.IsValidFor(c.SourceWidth, c.SourceHeight)) ||
            Calibrations.GroupBy(c => (c.SourceWidth, c.SourceHeight)).Any(g => g.Count() > 1))
            throw new ArgumentException("模型路径、调用间隔/上限、字幕时长（1–15秒）、模型超时（8–20秒）或小地图校准无效。");
    }
    /// <summary>The user-confirmed calibration for this game resolution, if any.</summary>
    public MinimapLayout? LayoutFor(FrameView frame) =>
        Calibrations.SingleOrDefault(c => c.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight));
    /// <summary>Confirmed calibration, else (when enabled) the default bottom-left box for this resolution.</summary>
    public MinimapLayout? EffectiveLayout(FrameView frame)
    {
        if (LayoutFor(frame) is { } confirmed) return confirmed;
        if (!AutoMinimap || frame.Meta.Source != "dota2") return null;
        var suggested = MinimapLayout.Suggest(frame.Meta.SourceWidth, frame.Meta.SourceHeight);
        return suggested.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight) ? suggested : null;
    }
    public static LiveCoachSettings Load(string file)
    {
        if (!File.Exists(file)) return new();
        if (new FileInfo(file).Length > 32768) throw new ArgumentException("Settings file too large");
        var settings = JsonSerializer.Deserialize<LiveCoachSettings>(File.ReadAllText(file), Wire.Json)
            ?? throw new ArgumentException("Invalid settings");
        settings.Validate(); return settings;
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
