using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>Game-machine video choices. Persisted locally; contains no pairing secret or images.</summary>
public sealed record GameVideoSettings
{
    public int Fps { get; init; } = 60;
    /// <summary>"native" (no scaling on the game machine; the coach scales) or a maximum height: 1440/1080/900/720.</summary>
    public string Resolution { get; init; } = "1080";
    /// <summary>0 = automatic from resolution × frame rate.</summary>
    public int BitrateMbps { get; init; }
    /// <summary>"auto" = GPU encoder when Windows has one, else CPU; "software" = always CPU.</summary>
    public string Encoder { get; init; } = "auto";
    /// <summary>"window" = Windows.Graphics.Capture of the Dota window (keeps sending behind other windows); "foreground" = legacy screen copy.</summary>
    public string Capture { get; init; } = "window";

    public static readonly string[] Resolutions = ["native", "1440", "1080", "900", "720"];
    // H.264 level 5.2: 2,073,600 macroblocks per second.
    private const long MaxMacroblocksPerSecond = 2_073_600;
    // The CPU encoder competes with the game for cores: keep it at the old bounded size.
    public const int SoftwareMaxWidth = 1920, SoftwareMaxHeight = 1080;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "TwoPc", "game-video.json");
    public void Validate()
    {
        if (!VideoWire.FrameRates.Contains(Fps) || !Resolutions.Contains(Resolution) || BitrateMbps is < 0 or > 100 ||
            Encoder is not ("auto" or "software") || Capture is not ("window" or "foreground"))
            throw new ArgumentException("帧率、分辨率、码率（0–100 Mbps）、编码器或采集方式无效。");
    }
    /// <summary>Encoded size for a game client area: native or the chosen height, never upscaled, even dimensions.</summary>
    public (int Width, int Height) EncodedSize(int sourceWidth, int sourceHeight, bool hardware)
    {
        int maxHeight = Resolution == "native" ? VideoPixels.MaxHeight : int.Parse(Resolution);
        int maxWidth = Resolution == "native" ? VideoPixels.MaxWidth : (int)Math.Ceiling(maxHeight * 16 / 9.0) & ~1;
        if (Resolution != "native") maxWidth = Math.Max(maxWidth, (int)((long)sourceWidth * maxHeight / sourceHeight) & ~1);
        if (!hardware) { maxWidth = Math.Min(maxWidth, SoftwareMaxWidth); maxHeight = Math.Min(maxHeight, SoftwareMaxHeight); }
        return VideoPixels.SizeFor(sourceWidth, sourceHeight, maxWidth, maxHeight);
    }
    /// <summary>Requested fps, lowered to the highest allowed rate that fits H.264 level 5.2 at this size.</summary>
    public static int EffectiveFps(int width, int height, int fps)
    {
        long macroblocks = (long)((width + 15) / 16) * ((height + 15) / 16);
        return VideoWire.FrameRates.Where(r => r <= fps && macroblocks * r <= MaxMacroblocksPerSecond)
            .DefaultIfEmpty(VideoWire.FrameRates[0]).Max();
    }
    /// <summary>About 0.07 bit per pixel per frame (HUD text stays readable), 4–80 Mbps.</summary>
    public int Bitrate(int width, int height, int fps) => BitrateMbps > 0 ? BitrateMbps * 1_000_000 :
        (int)Math.Clamp((long)width * height * fps * 7 / 100, 4_000_000, 80_000_000);
    public static GameVideoSettings Load(string file)
    {
        try {
            if (!File.Exists(file) || new FileInfo(file).Length > 8192) return new();
            var settings = JsonSerializer.Deserialize<GameVideoSettings>(File.ReadAllText(file), Wire.Json) ?? new();
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
