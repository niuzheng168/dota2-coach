using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>Descriptive frequencies from completed replay minutes, never evidence about an unobserved camp
/// in this game. No match IDs, accounts, entity positions or future outcomes cross the model boundary.</summary>
public static class MinuteFarmReplayPrior
{
    public sealed record Band(string Hero, int FromMinute, int ToMinuteExclusive, int ReplayMatches,
        int MatchedMinutes, int LaneOnly, int CampOnly, int Both, int Neither);
    private static readonly Lazy<Band[]> known = new(Read);
    private static Band[] Read()
    {
        using var stream = typeof(MinuteFarmReplayPrior).Assembly.GetManifestResourceStream("DotaCoach.MinuteFarm.Priors742");
        if (stream == null) return [];
        using var doc = JsonDocument.Parse(stream);
        var root = doc.RootElement;
        if (root.GetProperty("patch").GetString() != "7.42") throw new InvalidDataException("Unreviewed replay band");
        var bands = root.GetProperty("bands").Deserialize<Band[]>(Wire.Json) ?? [];
        if (bands.Length > 48 || bands.Any(b => b.Hero is not ("luna" or "troll_warlord") ||
                b.FromMinute < 0 || b.ToMinuteExclusive <= b.FromMinute || b.ToMinuteExclusive > 120 ||
                b.ReplayMatches < 1 || b.MatchedMinutes < 1 || new[] { b.LaneOnly, b.CampOnly, b.Both, b.Neither }.Any(n => n < 0) ||
                (long)b.LaneOnly + b.CampOnly + b.Both + b.Neither != b.MatchedMinutes) ||
            bands.Any(b => bands.Count(o => o.Hero == b.Hero && o.FromMinute < b.ToMinuteExclusive &&
                b.FromMinute < o.ToMinuteExclusive) != 1))
            throw new InvalidDataException("Invalid replay band exposure");
        return bands;
    }
    public static Band? For(string hero, int minute)
    {
        string? key = hero switch { "npc_dota_hero_luna" => "luna", "npc_dota_hero_troll_warlord" => "troll_warlord", _ => null };
        return key == null ? null : known.Value.SingleOrDefault(b => b.Hero == key && b.FromMinute <= minute && minute < b.ToMinuteExclusive);
    }
}
