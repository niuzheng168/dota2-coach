namespace DotaCoach.TwoPc;

/// <summary>
/// Dota's default game camera, fitted on build 6976 from 116 creep bars at known engine positions (4K, RMS 3 px):
/// maps a unit's ground offset from the camera's look-at point to the centre of its health bar on screen.
/// Pixel quantities are for a 2160-pixel-high picture and scale with the height; the centre is mid-width.
/// </summary>
public sealed record CameraModel(double Pitch, double Distance, double Focal, double BarHeight, double OffsetX, double OffsetY, double CenterY)
{
    public static readonly CameraModel Default = new(1.00331, 1224.99, 1783.53, 78.18, .01, 17.61, 988.69);

    /// <param name="dz">The unit's ground height above the look-at point's (river and slopes lie up to ~128 below a lane).</param>
    public (double X, double Y) Screen(double dx, double dy, int width, int height, double dz = 0)
    {
        double s = height / 2160d, rx = dx - OffsetX, ry = dy - OffsetY + Distance * Math.Cos(Pitch), rz = BarHeight + dz - Distance * Math.Sin(Pitch);
        double up = ry * Math.Sin(Pitch) + rz * Math.Cos(Pitch), depth = ry * Math.Cos(Pitch) - rz * Math.Sin(Pitch);
        return (width / 2d + Focal * s * rx / depth, CenterY * s - Focal * s * up / depth);
    }

    /// <summary>Inverse of <see cref="Screen"/> for a unit standing at the look-at point's height.</summary>
    public (double X, double Y) Ground(double x, double y, int width, int height)
    {
        double s = height / 2160d, a = (x - width / 2d) / (Focal * s), b = (CenterY * s - y) / (Focal * s);
        double rz = BarHeight - Distance * Math.Sin(Pitch);
        double ry = -rz * (Math.Cos(Pitch) + b * Math.Sin(Pitch)) / (Math.Sin(Pitch) - b * Math.Cos(Pitch));
        double depth = ry * Math.Cos(Pitch) - rz * Math.Sin(Pitch);
        return (a * depth + OffsetX, ry + OffsetY - Distance * Math.Cos(Pitch));
    }
}

/// <summary>
/// Which unit owns each health bar, from the player's own GSI minimap (unit names and ground positions of
/// everything the player's team sees). The camera's look-at point is not in GSI: it is the offset on which
/// most bars and same-side units agree (a vote over all bar/unit pairs, then nearest-unit assignment). GSI has no
/// heights either: bars left over are then paired with units left over at the terrain height that fits them best.
/// Measured on staged mixed waves at 4K: 99.0% exact unit, 0.03% wrong with GSI 0.2 s old.
/// </summary>
public sealed class CreepUnitMatcher
{
    private (double X, double Y)? lookAt;
    private readonly CameraModel camera;
    public CreepUnitMatcher(CameraModel? camera = null) => this.camera = camera ?? CameraModel.Default;

    public static bool IsCreep(string name) => name.StartsWith("npc_dota_creep_", StringComparison.Ordinal) ||
        name.StartsWith("npc_dota_neutral_", StringComparison.Ordinal) || name is "npc_dota_goodguys_siege" or "npc_dota_badguys_siege" ||
        name.StartsWith("npc_dota_goodguys_siege_", StringComparison.Ordinal) || name.StartsWith("npc_dota_badguys_siege_", StringComparison.Ordinal);

    /// <returns>Per bar, the unit it belongs to, or null when no unit of the bar's side projects close enough.</returns>
    public MinimapUnit?[] Match(IReadOnlyList<CreepHealthBar> bars, IReadOnlyList<MinimapUnit> units, int ownTeam, int width, int height)
    {
        var result = new MinimapUnit?[bars.Count];
        var creeps = units.Where(u => IsCreep(u.Name)).ToArray();
        if (bars.Count == 0 || creeps.Length == 0) return result;
        double tolerance = .3 * (bars[0].Inner + 2);
        var anchors = bars.Select(b => (X: (double)b.CenterX, Y: b.Y + b.Rows / 2d + 1)).ToArray();
        bool Same(int bar, MinimapUnit u) => bars[bar].Ally == (u.Team == ownTeam);
        var projected = new (double X, double Y)[creeps.Length];
        int Score((double X, double Y) at)
        {
            for (int j = 0; j < creeps.Length; j++) projected[j] = camera.Screen(creeps[j].X - at.X, creeps[j].Y - at.Y, width, height);
            int hits = 0;
            for (int i = 0; i < bars.Count; i++)
                for (int j = 0; j < creeps.Length; j++)
                    if (Same(i, creeps[j]) && Math.Abs(projected[j].X - anchors[i].X) < tolerance && Math.Abs(projected[j].Y - anchors[i].Y) < tolerance) { hits++; break; }
            return hits;
        }
        (double X, double Y)? best = null;
        int bestScore = -1;
        if (lookAt is { } previous) { best = previous; bestScore = Score(previous); }
        // The camera usually moves little between frames; search every pairing only when the old point stopped fitting.
        if (bestScore < bars.Count * 3 / 4) {
            for (int i = 0; i < bars.Count; i++) {
                var ground = camera.Ground(anchors[i].X, anchors[i].Y, width, height);
                foreach (var u in creeps) {
                    if (!Same(i, u)) continue;
                    var at = (u.X - ground.X, u.Y - ground.Y);
                    int score = Score(at);
                    if (score > bestScore) { best = at; bestScore = score; }
                }
            }
        }
        if (best is not { } look || bestScore < 1) return result;
        lookAt = look;
        for (int j = 0; j < creeps.Length; j++) projected[j] = camera.Screen(creeps[j].X - look.X, creeps[j].Y - look.Y, width, height);
        var pairs = new List<(double Distance, int Bar, int Unit)>();
        for (int i = 0; i < bars.Count; i++)
            for (int j = 0; j < creeps.Length; j++) {
                if (!Same(i, creeps[j])) continue;
                double d = Math.Sqrt(Math.Pow(projected[j].X - anchors[i].X, 2) + Math.Pow(projected[j].Y - anchors[i].Y, 2));
                if (d < tolerance) pairs.Add((d, i, j));
            }
        var usedUnits = new HashSet<int>();
        foreach (var (_, bar, unit) in pairs.OrderBy(p => p.Distance)) {
            if (result[bar] != null || !usedUnits.Add(unit)) continue;
            result[bar] = creeps[unit];
        }
        // A unit lower or higher than the look-at point (river banks, high ground) shows its bar up to ~120 px off.
        pairs.Clear();
        for (int i = 0; i < bars.Count; i++) {
            if (result[i] != null) continue;
            for (int j = 0; j < creeps.Length; j++) {
                if (usedUnits.Contains(j) || !Same(i, creeps[j])) continue;
                double nearest = double.MaxValue;
                for (int dz = -256; dz <= 256; dz += 16) {
                    var p = camera.Screen(creeps[j].X - look.X, creeps[j].Y - look.Y, width, height, dz);
                    nearest = Math.Min(nearest, Math.Sqrt(Math.Pow(p.X - anchors[i].X, 2) + Math.Pow(p.Y - anchors[i].Y, 2)));
                }
                if (nearest < tolerance) pairs.Add((nearest, i, j));
            }
        }
        foreach (var (_, bar, unit) in pairs.OrderBy(p => p.Distance)) {
            if (result[bar] != null || !usedUnits.Add(unit)) continue;
            result[bar] = creeps[unit];
        }
        return result;
    }
}

/// <summary>
/// Follows bars from frame to frame (same side, nearest within half a bar width) and keeps the unit name each
/// track was matched to, so one stale or missing GSI match does not change the number shown, and the bar's drops.
/// </summary>
public sealed class CreepTracks
{
    private sealed class Track(CreepHealthBar bar, long seen)
    {
        public CreepHealthBar Bar = bar;
        public long Seen = seen;
        public readonly Dictionary<string, int> Votes = [];
        public readonly CreepHitHistory Hits = new();
        public string? Name => Votes.Count == 0 ? null : Votes.MaxBy(v => v.Value).Key;
    }
    private List<Track> tracks = [];

    /// <param name="at">This frame's capture time in seconds, for the drop histories.</param>
    /// <param name="hits">Filled per bar with its track's drop history.</param>
    /// <returns>Per bar, the unit name its track was most often matched to (null if never matched).</returns>
    public string?[] Update(IReadOnlyList<CreepHealthBar> bars, IReadOnlyList<MinimapUnit?> matches, long now, double at = 0, CreepHitHistory?[]? hits = null)
    {
        var next = new List<Track>(bars.Count);
        var names = new string?[bars.Count];
        var free = tracks.Where(t => now - t.Seen < 400).ToList();
        for (int i = 0; i < bars.Count; i++) {
            var bar = bars[i];
            double radius = (bar.Inner + 2) / 2d;
            var track = free.Where(t => t.Bar.Ally == bar.Ally && Math.Abs(t.Bar.CenterX - bar.CenterX) < radius && Math.Abs(t.Bar.Y - bar.Y) < radius)
                .MinBy(t => Math.Abs(t.Bar.CenterX - bar.CenterX) + Math.Abs(t.Bar.Y - bar.Y));
            if (track != null) free.Remove(track); else track = new(bar, now);
            track.Bar = bar; track.Seen = now;
            if (i < matches.Count && matches[i] is { } unit) {
                track.Votes[unit.Name] = Math.Min(30, track.Votes.GetValueOrDefault(unit.Name) + 1);
                foreach (var other in track.Votes.Keys.Where(k => k != unit.Name).ToList())
                    if (--track.Votes[other] <= 0) track.Votes.Remove(other);
            }
            names[i] = track.Name;
            if (hits != null) { track.Hits.Add(at, bar.Fill); hits[i] = track.Hits; }
            next.Add(track);
        }
        // Tracks not seen this frame survive briefly (a bar hidden for a frame by another bar).
        next.AddRange(free);
        tracks = next;
        return names;
    }
}
