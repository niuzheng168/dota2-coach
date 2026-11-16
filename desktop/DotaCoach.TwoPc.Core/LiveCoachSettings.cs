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
            Calibrations == null || Calibrations.Length > 16 ||
            Calibrations.Any(c => c == null || !c.IsValidFor(c.SourceWidth, c.SourceHeight)) ||
            Calibrations.GroupBy(c => (c.SourceWidth, c.SourceHeight)).Any(g => g.Count() > 1))
            throw new ArgumentException("模型路径、调用间隔/上限或小地图校准无效。");
    }
    public MinimapLayout? LayoutFor(FrameView frame) =>
        Calibrations.SingleOrDefault(c => c.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight));
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
