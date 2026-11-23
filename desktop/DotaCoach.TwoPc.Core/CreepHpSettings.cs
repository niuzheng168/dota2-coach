using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>
/// How the red creep HP numbers time the player's last hit. Persisted on the game machine only. With <see cref="LookAhead"/>
/// a number turns red when one attack ordered now kills, counting the hits other creeps and towers land before it (their
/// once-a-second rhythm, <see cref="CreepHitHistory"/>); the player's reaction and the ping decide how far ahead to look.
/// In the last-hit lab (scripts/lasthit) a player reacting in 0.2 s last-hit 70% of creeps this way against 31% with the
/// red of this frame alone (level-1 Luna, 208 creeps, see docs/last-hit-lab.md).
/// </summary>
public sealed record CreepHpSettings
{
    public bool LookAhead { get; init; } = true;
    /// <summary>How long before the right moment the number turns red: the player's reaction, about 50 ms shorter.</summary>
    public int ReactionMs { get; init; } = 150;
    /// <summary>Dota's ping (round trip): the order to the server and the health on screen both trail the server by about half.</summary>
    public int PingMs { get; init; } = 50;

    public const int MaxReactionMs = 400, MaxPingMs = 300;
    /// <summary>Seconds of the local path besides the ping: the server's tick wait, capture and drawing (measured near 0.03 locally).</summary>
    public const double LocalLag = .03;

    public static string DefaultPath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DotaCoach", "TwoPc", "creep-hp.json");

    /// <summary>The look-ahead timing, or null for the red of this frame alone.</summary>
    public LastHitTiming? Timing => LookAhead ? new(ReactionMs / 1000d, PingMs / 1000d + LocalLag, Patient: true) : null;

    public void Validate()
    {
        if (ReactionMs is < 0 or > MaxReactionMs || PingMs is < 0 or > MaxPingMs) throw new ArgumentException("反应提前量或网络延迟超出范围。");
    }
    public static CreepHpSettings Load(string file)
    {
        try {
            if (!File.Exists(file) || new FileInfo(file).Length > 4096) return new();
            var settings = JsonSerializer.Deserialize<CreepHpSettings>(File.ReadAllText(file), Wire.Json) ?? new();
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
