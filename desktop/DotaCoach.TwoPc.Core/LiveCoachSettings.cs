using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace DotaCoach.TwoPc;

/// <summary>Local preferences contain credential PATHS, never key contents or game images.</summary>
public sealed record LiveCoachSettings
{
    public bool AutomaticAdvice { get; init; } = true;
    public bool Subtitles { get; init; } = true;
    public bool MinimapMarkers { get; init; } = true;
    /// <summary>Prefer the exclusive 7.42 minute mode. This is NOT confirmation of any game's map/build.</summary>
    public bool MinuteFarm742 { get; init; } = false;
    /// <summary>The existing own-state-only consent does not authorize uploading team observations.</summary>
    public bool ModelTeamKnowledge { get; init; }
    public const string FoundryProvider = "foundry";
    public string ModelProvider { get; init; } = FoundryProvider;
    // Keep both legacy JSON names and the original deployment for profiles without a provider.
    public string FoundryEnvFile { get; init; } = DefaultCredentialPath();
    public string Model { get; init; } = "gpt-6-astra";
    public string CopilotBaseUrl { get; init; } = CopilotGateway.DefaultBaseUrl;
    public string CopilotModel { get; init; } = CopilotGateway.Astra;
    public string CopilotKeyFile { get; init; } = "";
    [JsonIgnore] public string EffectiveModel => ModelProvider switch {
        FoundryProvider => Model, CopilotGateway.ProviderId => CopilotModel, _ => ""
    };
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
    /// <summary>Display inferred ward candidates. Does not enable/disable collection or authorize model input.</summary>
    public bool EnemyWards { get; init; } = false;
    /// <summary>Local scanner workspace (.tmp/vision-assets) and vision Python. Never bundled or started automatically.</summary>
    public string ScanFolder { get; init; } = "";
    public string ScanPython { get; init; } = "";
    /// <summary>Only an explicit operator choice runs code from the scanner workspace instead of the bundled scripts.</summary>
    public bool ScanFromWorkspace { get; init; }
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
        switch (ModelProvider) {
            case FoundryProvider:
                if (FoundryEnvFile == null || !Path.IsPathFullyQualified(FoundryEnvFile) ||
                    Model == null || !Regex.IsMatch(Model, @"\A[A-Za-z0-9_.-]{1,100}\z"))
                    throw new ArgumentException("请填写 Foundry 配置文件的完整路径和有效部署名。");
                break;
            case CopilotGateway.ProviderId:
                CopilotGateway.Validate(CopilotBaseUrl, CopilotModel, CopilotKeyFile);
                if (CopilotKeyFile.Length > 0 && !string.IsNullOrEmpty(FoundryEnvFile) && Path.IsPathFullyQualified(FoundryEnvFile) &&
                    string.Equals(Path.GetFullPath(CopilotKeyFile), Path.GetFullPath(FoundryEnvFile), StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Codey 本地模型密钥文件不能使用 Foundry 配置文件。");
                break;
            default:
                throw new ArgumentException("未知模型服务；不会切换到其他服务。");
        }
        if (IntervalSeconds is < 12 or > 300 || RequestLimit is < 1 or > 1000 ||
            SubtitleSeconds is < 1 or > Wire.MaxReminderMs / 1000 || ModelTimeoutSeconds is < 8 or > 20 ||
            Calibrations == null || Calibrations.Length > 16 ||
            Calibrations.Any(c => c == null || !c.IsValidFor(c.SourceWidth, c.SourceHeight)) ||
            Calibrations.GroupBy(c => (c.SourceWidth, c.SourceHeight)).Any(g => g.Count() > 1) ||
            new[] { ScanFolder, ScanPython }.Any(p => p == null || p.Length > 0 && !Path.IsPathFullyQualified(p)))
            throw new ArgumentException("调用间隔/上限、字幕时长（1–15秒）、模型超时（8–20秒）、扫描路径或小地图校准无效。");
    }
    /// <summary>The user-confirmed calibration for this game resolution, if any.</summary>
    public MinimapLayout? LayoutFor(FrameView frame) =>
        Calibrations.SingleOrDefault(c => c.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight));
    /// <summary>Only ordinary mode may use an automatic box. Minute mode requires the confirmed source-pixel layout.</summary>
    public MinimapLayout? EffectiveLayout(FrameView frame)
    {
        if (frame.Meta.IsLegacyStatus || frame.Meta.Source is not ("dota2" or "dota2-gsi")) return null;
        if (LayoutFor(frame) is { } confirmed) return confirmed;
        if (MinuteFarm742 || !AutoMinimap || frame.Meta.Source != "dota2" || !frame.HasImage) return null;
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
