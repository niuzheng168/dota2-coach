using System.Text.Json;

namespace DotaCoach.TwoPc;

public sealed record MapLandmark(string Id, string Team, string Label, string Kind, double X, double Y);

/// <summary>Static coordinate projection, never a farming policy or a live-visibility oracle.</summary>
public sealed class CalibratedMap
{
    private readonly double left, top, span;
    public MapLandmark[] Landmarks { get; }
    public CalibratedMap(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var root = doc.RootElement;
        if (root.GetProperty("patch").GetString() != "7.41f") throw new ArgumentException("Unreviewed map patch");
        var overview = root.GetProperty("overview");
        left = overview.GetProperty("posX").GetDouble(); top = overview.GetProperty("posY").GetDouble();
        span = overview.GetProperty("coordinateSpan").GetDouble();
        if (left != -9472 || top != 9472 || span != 18944) throw new ArgumentException("Unreviewed overview calibration");
        Landmarks = root.GetProperty("camps").EnumerateArray().Select(c => new MapLandmark(
            c.GetProperty("id").GetString()!, c.GetProperty("team").GetString()!, c.GetProperty("label").GetString()!,
            c.GetProperty("kind").GetString()!, c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble()))
            .Concat(root.GetProperty("towers").EnumerateArray().Select(t => new MapLandmark(
                t.GetProperty("name").GetString()!, t.GetProperty("team").GetString()!, t.GetProperty("name").GetString()!,
                "tower-landmark", t.GetProperty("position")[0].GetDouble(), t.GetProperty("position")[1].GetDouble())))
            .ToArray();
    }
    public static bool Supports(FrameView frame) => frame.Meta.SourceWidth == 1280 && frame.Meta.SourceHeight == 720;
    public MapLandmark[] Candidates(string team) => Landmarks.Where(p => p.Team == team).ToArray();
    public ScreenMark Project(string team, string id, FrameView frame, MinimapLayout? layout = null)
    {
        if (layout == null) {
            if (!Supports(frame)) throw new ArgumentException("This minimap layout has not been calibrated");
            layout = new(1280, 720, 0, 560, 160); // Retained ONLY for the explicitly selected legacy bot E2E.
        }
        if (!layout.IsValidFor(frame.Meta.SourceWidth, frame.Meta.SourceHeight))
            throw new ArgumentException("Minimap calibration does not match the game client");
        var target = Candidates(team).SingleOrDefault(p => p.Id == id) ??
            throw new ArgumentException("Target is not an allowed own-side static landmark");
        // Default left minimap measured in the actual 1280x720 acceptance capture.
        // Own Luna (-900,-6250) projects to (72.4,692.8) pixels, checked against its visible icon.
        return new((layout.Left + (target.X - left) / span * layout.Size) / layout.SourceWidth,
            (layout.Top + (top - target.Y) / span * layout.Size) / layout.SourceHeight);
    }
}
