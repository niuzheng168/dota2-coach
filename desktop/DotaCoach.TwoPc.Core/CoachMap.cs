using System.Text.Json;

namespace DotaCoach.TwoPc;

/// <summary>A named place of the coach map: a neutral camp, tower or objective. Team null = nobody's side.</summary>
public sealed record MapPlace(string Kind, string? Team, string Name, double X, double Y, double Radius);
/// <summary>A neutral camp: its side, size (small/medium/large/ancient) and 7.42 creep pool (radiant/dire/ancient/amphibian).</summary>
public sealed record MapCamp(string Id, string Team, string Kind, string Pool, string Name, double X, double Y,
    double MinX, double MinY, double MaxX, double MaxY)
{
    public bool Contains(double x, double y, double margin = 0) =>
        x >= MinX - margin && x <= MaxX + margin && y >= MinY - margin && y <= MaxY + margin;
}

/// <summary>
/// The installed client's map as the coach uses it (data/dota2-coach-map.json, built by scripts/client/build-coach-map.ts
/// from 7.42 build 6989): place names for alerts and ward guesses, the minimap projection and where wards cannot go.
/// Static facts only; nothing here knows whether a camp is alive or who holds an area.
/// </summary>
public sealed class CoachMap
{
    public string Patch { get; }
    public double Left { get; }
    public double Top { get; }
    public double Span { get; }
    public MapCamp[] Camps { get; }
    public MapPlace[] Places { get; }
    private readonly (double X, double Y)[][] noWards;
    private readonly (string Lane, (double X, double Y)[] Points)[] lanes;
    private readonly Dictionary<string, (double X, double Y)[]> tier3 = new();
    private readonly Dictionary<string, (double X, double Y)> fountains = new();

    public CoachMap(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
        var root = doc.RootElement;
        Patch = root.GetProperty("patch").GetString() ?? throw new ArgumentException("No patch");
        if (Patch != "7.42") throw new ArgumentException("Unreviewed map patch");
        var overview = root.GetProperty("overview");
        Left = overview.GetProperty("posX").GetDouble(); Top = overview.GetProperty("posY").GetDouble();
        Span = overview.GetProperty("coordinateSpan").GetDouble();
        if (!Wire.Finite(Span, 10000, 30000)) throw new ArgumentException("Unreadable overview");
        static (double, double) Point(JsonElement p) => (p[0].GetDouble(), p[1].GetDouble());
        Camps = root.GetProperty("camps").EnumerateArray().Select(c => {
            var box = c.GetProperty("box");
            return new MapCamp(c.GetProperty("id").GetString()!, c.GetProperty("team").GetString()!, c.GetProperty("kind").GetString()!,
                c.GetProperty("pool").GetString()!, c.GetProperty("name").GetString()!, c.GetProperty("x").GetDouble(), c.GetProperty("y").GetDouble(),
                box.GetProperty("min")[0].GetDouble(), box.GetProperty("min")[1].GetDouble(),
                box.GetProperty("max")[0].GetDouble(), box.GetProperty("max")[1].GetDouble());
        }).ToArray();
        var places = new List<MapPlace>();
        // Camps by their spawn box (checked first), then towers and objectives within these distances.
        foreach (var t in root.GetProperty("towers").EnumerateArray()) {
            var (x, y) = Point(t.GetProperty("position"));
            places.Add(new("tower", t.GetProperty("team").GetString(), t.GetProperty("short").GetString()!, x, y, 650));
            if (t.GetProperty("tier").GetInt32() == 3)
                tier3[t.GetProperty("team").GetString()!] = [.. tier3.GetValueOrDefault(t.GetProperty("team").GetString()!) ?? [], (x, y)];
        }
        foreach (var o in root.GetProperty("objectives").EnumerateArray()) {
            string kind = o.GetProperty("kind").GetString()!;
            if (kind == "watcher") continue; // ten alike lanterns name nothing
            var (x, y) = Point(o.GetProperty("position"));
            places.Add(new(kind, o.GetProperty("team").ValueKind == JsonValueKind.String ? o.GetProperty("team").GetString() : null,
                o.GetProperty("short").GetString()!, x, y, kind is "roshan" or "tormentor" ? 750 : 600));
        }
        Places = places.ToArray();
        foreach (var b in root.GetProperty("buildings").EnumerateArray())
            if (b.GetProperty("kind").GetString() == "fountain") fountains[b.GetProperty("team").GetString()!] = Point(b.GetProperty("position"));
        noWards = root.GetProperty("noWards").EnumerateArray().Select(p => p.EnumerateArray().Select(Point).ToArray()).ToArray();
        lanes = root.GetProperty("lanes").EnumerateArray()
            .Select(l => (l.GetProperty("lane").GetString()!, l.GetProperty("points").EnumerateArray().Select(Point).ToArray())).ToArray();
        if (Camps.Length != 28 || tier3.Values.Any(t => t.Length != 3) || fountains.Count != 2 || lanes.Length != 6)
            throw new ArgumentException("Incomplete coach map");
    }
    /// <summary>The bundled map next to the program, or null when it is missing or unreadable.</summary>
    public static CoachMap? Bundled()
    {
        try { return new CoachMap(Path.Combine(AppContext.BaseDirectory, "data", "dota2-coach-map.json")); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or ArgumentException or
                                   KeyNotFoundException or InvalidOperationException or IndexOutOfRangeException) { return null; }
    }
    /// <summary>World position to the minimap square: 0..1 from its left and top edge.</summary>
    public (double X, double Y) Minimap(double x, double y) => ((x - Left) / Span, (Top - y) / Span);
    public bool NoWard(double x, double y) => noWards.Any(polygon => Inside(polygon, x, y));
    public MapCamp? CampAt(double x, double y, double margin = 0) =>
        Camps.Where(c => c.Contains(x, y, margin)).OrderBy(c => double.Hypot(c.X - x, c.Y - y)).FirstOrDefault();
    /// <summary>"radiant"/"dire" when inside that base: on the fountain's side of the line through its three tier-3 towers (plus 400).</summary>
    public string? BaseOf(double x, double y)
    {
        foreach (var (team, towers) in tier3) {
            var t = towers.OrderBy(p => Math.Atan2(p.Y - fountains[team].Y, p.X - fountains[team].X)).ToArray();
            bool inside = true;
            for (int i = 1; i < t.Length && inside; i++) inside = Side(t[i - 1], t[i], fountains[team], x, y) > -400;
            if (inside) return team;
        }
        return null;
    }
    /// <summary>
    /// Where (x, y) is, named from <paramref name="ownTeam"/>'s side (2 radiant, 3 dire): a base, a camp's spawn box,
    /// a tower or objective nearby, a camp's surroundings, else lane, river or one side's jungle.
    /// </summary>
    public string Describe(double x, double y, int ownTeam)
    {
        string own = ownTeam == 2 ? "radiant" : "dire";
        string Side(string? team) => team == null ? "" : team == own ? "我方" : "敌方";
        if (BaseOf(x, y) is { } baseTeam) return Side(baseTeam) + "高地";
        if (CampAt(x, y) is { } inside) return Side(inside.Team) + inside.Name;
        var place = Places.Where(p => double.Hypot(p.X - x, p.Y - y) <= p.Radius).OrderBy(p => double.Hypot(p.X - x, p.Y - y)).FirstOrDefault();
        if (place != null) return Side(place.Team) + place.Name + (place.Kind == "tower" ? "附近" : "");
        if (CampAt(x, y, 350) is { } camp) return Side(camp.Team) + camp.Name;
        var lane = lanes.Select(l => (l.Lane, Distance: Distance(l.Points, x, y))).MinBy(l => l.Distance);
        if (lane.Distance <= 900) return lane.Lane switch { "top" => "上路", "mid" => "中路", _ => "下路" };
        if (Math.Abs(x + y) < 1100) return "河道";
        return Side(x + y < 0 ? "radiant" : "dire") + "野区";
    }
    private static double Side((double X, double Y) a, (double X, double Y) b, (double X, double Y) toward, double x, double y)
    {
        double dx = b.X - a.X, dy = b.Y - a.Y, length = double.Hypot(dx, dy);
        double s = (dx * (y - a.Y) - dy * (x - a.X)) / length;
        return dx * (toward.Y - a.Y) - dy * (toward.X - a.X) >= 0 ? s : -s;
    }
    private static double Distance((double X, double Y)[] points, double x, double y)
    {
        double best = double.MaxValue;
        for (int i = 1; i < points.Length; i++) {
            var (a, b) = (points[i - 1], points[i]);
            double dx = b.X - a.X, dy = b.Y - a.Y, t = Math.Clamp(((x - a.X) * dx + (y - a.Y) * dy) / Math.Max(1e-9, dx * dx + dy * dy), 0, 1);
            best = Math.Min(best, double.Hypot(x - a.X - t * dx, y - a.Y - t * dy));
        }
        return best;
    }
    private static bool Inside((double X, double Y)[] polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Length - 1; i < polygon.Length; j = i++)
            if ((polygon[i].Y > y) != (polygon[j].Y > y) &&
                x < (polygon[j].X - polygon[i].X) * (y - polygon[i].Y) / (polygon[j].Y - polygon[i].Y) + polygon[i].X) inside = !inside;
        return inside;
    }
}
