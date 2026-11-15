using System;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DotaCoach.Desktop;

public static class OverlayPreferences
{
    public static JsonElement Normalize(JsonElement input)
    {
        double Number(string key, double fallback, double min, double max) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number &&
            v.TryGetDouble(out double number) && double.IsFinite(number) ? Math.Clamp(number, min, max) : fallback;
        string Color(string key, string fallback) =>
            input.ValueKind == JsonValueKind.Object && input.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String &&
            Regex.IsMatch(v.GetString() ?? "", "^#[0-9a-fA-F]{6}$") ? v.GetString()! : fallback;
        return JsonSerializer.SerializeToElement(new {
            enabled = !(input.ValueKind == JsonValueKind.Object && input.TryGetProperty("enabled", out var v) && v.ValueKind == JsonValueKind.False),
            sourceColor = Color("sourceColor", "#eef3ef"), targetColor = Color("targetColor", "#b8f3cc"),
            fontSize = Number("fontSize", 28, 16, 48), opacity = Number("opacity", .95, .15, 1),
            backgroundOpacity = Number("backgroundOpacity", .35, 0, .9),
            top = Number("top", 100, 0, 100), seconds = Number("seconds", 8, 3, 30),
            monitor = (int)Number("monitor", 0, 0, 15),
        });
    }
}
