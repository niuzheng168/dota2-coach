namespace DotaCoach.TwoPc;

/// <summary>One minimap symbol in build-6989 world XY. Team 2/3 = creep/wave; team 0 = meeting.
/// Fog coordinates and radii are hypotheses; LastX/Y refer ONLY to the last team-visible sample.</summary>
public sealed record LaneMark(string Lane, int Team, string Kind, int X, int Y, int Radius,
    int? SeenAt = null, int? LastX = null, int? LastY = null, int? Spawn = null, int? Meeting = null);

public sealed record LaneMap742(int Clock, LaneMark[] Marks, int LeaseMs)
{
    internal const int Lease = 2000, MaxMarks = 144, Memory = 12, FrontMemory = 30;
    private static bool Coord(int? v) => v is >= -9472 and <= 9472;
    internal bool Valid()
    {
        if (Clock is < 0 or > 43200 || LeaseMs is < 1 or > Lease || Marks is not { Length: > 0 and <= MaxMarks }) return false;
        int occupied = 0, zones = 0;
        foreach (var m in Marks) {
            int lane = m?.Lane switch { "top" => 1, "mid" => 2, "bot" => 4, _ => 0 };
            if (m == null || lane == 0 || !Coord(m.X) || !Coord(m.Y) || m.Radius is < 0 or > 2400 ||
                m.Spawn is int sp && (sp < 0 || sp % 30 != 0 || sp > Clock + 40) ||
                m.SeenAt is int seen && (seen < 0 || seen > Clock || Clock - seen > (m.Kind is "held" or "last-clash" ? FrontMemory : Memory))) return false;
            if (m.Team == 0) {
                if (m.Kind == "last-clash") {
                    if ((zones & lane) != 0 || m.Spawn != null || m.SeenAt == null || m.Meeting != m.SeenAt || m.SeenAt >= Clock ||
                        m.LastX != null || m.LastY != null || m.Radius == 0) return false;
                    zones |= lane; continue;
                }
                if (m.Kind == "contested") {
                    if ((zones & lane) != 0 || m.Spawn is not int origin || origin > Clock || m.Meeting is not int onset ||
                        onset < origin || onset > Clock || Clock - onset >= 30 || m.SeenAt != null || m.LastX != null || m.LastY != null || m.Radius == 0) return false;
                    zones |= lane; continue;
                }
                if ((occupied & lane) != 0 || m.LastX != null || m.LastY != null ||
                    m.Meeting is not int at || at < Clock || at > Clock + 40) return false;
                occupied |= lane;
                if (!(m.Kind switch {
                    "contact" => at == Clock && m.SeenAt == Clock && m.Spawn == null,
                    "forecast" => m.SeenAt != null && m.Radius > 0,
                    "held" => m.SeenAt != null && m.Spawn != null && m.Radius > 0,
                    "baseline" => m.SeenAt == null && m.Spawn != null && m.Radius > 0,
                    _ => false
                })) return false;
            } else if (m.Team is not (2 or 3) || m.Meeting != null || !(m.Kind switch {
                "seen" => m.SeenAt == Clock && m.LastX == null && m.LastY == null && m.Spawn == null && m.Radius == 0,
                "fog" => m.SeenAt < Clock && m.SeenAt != null && Coord(m.LastX) && Coord(m.LastY) && m.Spawn == null && m.Radius > 0,
                "march" => m.Spawn <= Clock && m.Spawn != null && m.SeenAt == null && m.LastX == null && m.LastY == null && m.Radius > 0,
                _ => false
            })) return false;
        }
        return true;
    }
    internal LaneMap742? Age(long ms) => ms < 0 || ms >= LeaseMs ? null : this with { LeaseMs = LeaseMs - (int)ms, Marks = [.. Marks] };
}
