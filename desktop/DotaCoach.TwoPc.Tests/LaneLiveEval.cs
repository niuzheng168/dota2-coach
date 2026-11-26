using System.Text.Json;
using DotaCoach.TwoPc;

// Offline only: the lane tracker re-run on the Plus team view a coach debug session recorded during a real game,
// then every symbol it would have drawn scored at that second against the same match's replay. The replay is
// scoring truth, never an input. Debug samples are about one a second, so this approximates the live cadence.
internal static class LaneLiveEval
{
    private sealed record Truth(int Side, string Lane, double X, double Y, string Radiant, string Dire);
    private sealed class Score { public int Shown; public List<double> Distance = [], Time = []; public int NoContact, Near; }

    // A Plus packet's clock t lines up best with replay second t+1 (median 53 world units on the 2026-11-25 debug).
    private const int ReplayLag = 1;

    public static void Run(string events, string oracle, string? output = null)
    {
        // One session can follow several games: keep the snapshots taken while the coach's participant was this match.
        string match = Path.GetFileName(oracle).Split('.')[0];
        string? current = null, team = null, hero = null;
        var snaps = new List<SpectatorSnapshot>();
        foreach (var line in File.ReadLines(events)) {
            using var doc = JsonDocument.Parse(line); var e = doc.RootElement; var d = e.GetProperty("details");
            string stage = e.GetProperty("stage").GetString()!;
            if (stage == "client-state") {
                current = d.TryGetProperty("player", out var p) && p.ValueKind == JsonValueKind.Object ? p.GetProperty("matchId").GetString() : null;
                if (current == match) { team = p.GetProperty("team").GetString(); hero = p.GetProperty("hero").GetString(); }
            }
            if (stage != "spectator-snapshot" || current != match || d.GetProperty("mode").GetString() != "team-view" ||
                d.GetProperty("clock").ValueKind != JsonValueKind.Number) continue;
            var units = d.GetProperty("units").EnumerateArray().Select(u => new MinimapUnit(u[0].GetString()!, u[1].GetInt32(),
                u[2].GetDouble(), u[3].GetDouble(), u[6].ValueKind == JsonValueKind.Number ? u[6].GetDouble() : null,
                u[4].ValueKind == JsonValueKind.Number ? u[4].GetDouble() : null, u[5].GetString())).ToArray();
            snaps.Add(new(e.GetProperty("elapsedMs").GetInt64(), "team-view", d.GetProperty("clock").GetDouble(), d.GetProperty("gameState").GetString(),
                d.GetProperty("paused").GetBoolean(), null, d.GetProperty("ownTeam").GetInt32(), d.GetProperty("groupedPlayers").GetInt32(), units,
                d.GetProperty("buildingTeams").EnumerateArray().Select(b => b.GetString()!).ToArray(),
                d.GetProperty("eventTypes").EnumerateArray().Select(b => b.GetString()!).ToArray()));
        }
        if (team == null || hero == null) throw new InvalidDataException($"The debug session never followed match {match}");
        int ours = team == "radiant" ? 2 : 3;
        string Ours(Truth a) => ours == 2 ? a.Radiant : a.Dire;
        var truth = new Dictionary<int, Truth[]>();
        foreach (var line in File.ReadLines(oracle)) {
            using var doc = JsonDocument.Parse(line); var r = doc.RootElement;
            truth[r[0].GetInt32()] = r[1].EnumerateArray().Select(u => new Truth(u[1].GetInt32(), u[2].GetString()!, u[3].GetDouble(), u[4].GetDouble(),
                u[5].GetString()!, u[6].GetString()!)).ToArray();
        }
        // First approach of the two sides' creeps born on that lane, at or after t (within 40 s), on the Plus clock.
        (int At, double X, double Y)? Contact(string lane, int from) {
            for (int s = from + ReplayLag; s <= from + ReplayLag + 40 && truth.ContainsKey(s); s++) {
                double best = 501; (double X, double Y)? mid = null;
                var rows = truth[s].Where(a => a.Lane == lane).ToArray();
                foreach (var a in rows.Where(a => a.Side == 2)) foreach (var b in rows.Where(b => b.Side == 3)) {
                    double gap = double.Hypot(a.X - b.X, a.Y - b.Y);
                    if (gap < best) { best = gap; mid = ((a.X + b.X) / 2, (a.Y + b.Y) / 2); }
                }
                if (mid is { } m) return (s - ReplayLag, m.X, m.Y);
            }
            return null;
        }
        // As displayed: the coach places hypotheses a second ahead of its clock (LiveMatchState.GameWaves) and the game
        // client draws the map about a second later, so each symbol is scored against the replay one second on.
        // ○/△ count only next to a real enemy creep HIDDEN from us; ● against any real creep of that side; × against the
        // next real contact on its lane. v0.6.6 on the 2026-11-25 games, scored the same way: ○ 40-43 %, △ 45-51 %.
        const int Shown = 1;
        var scores = new SortedDictionary<string, Score>(StringComparer.Ordinal);
        Score Bucket(string key) { if (!scores.TryGetValue(key, out var b)) scores.Add(key, b = new()); return b; }
        var tracker = new LaneWave742.Tracker();
        using var dump = output == null ? null : new StreamWriter(output);
        int last = int.MinValue, seconds = 0, waves = 0, unmarked = 0;
        foreach (var raw in snaps) {
            var s = raw with { MatchId = match };
            int t = (int)Math.Floor(s.Clock!.Value);
            if (t == last || !truth.TryGetValue(t + Shown + ReplayLag, out var then)) continue;
            var own = new MatchWitness(match, team, hero, s.Clock.Value, "playing", false);
            if (LaneWave742.Observe(own, s, s.ReceivedAt, tracker) == null) continue;
            last = t; seconds++;
            var map = tracker.Project(t, true, ahead: Shown);
            var hidden = then.Where(a => a.Side != ours && Ours(a) == "hidden" && a.Lane != "?").ToArray();
            double Nearest(IEnumerable<Truth> rows, LaneMark m) =>
                rows.Select(a => double.Hypot(a.X - m.X, a.Y - m.Y)).DefaultIfEmpty(double.PositiveInfinity).Min();
            foreach (var m in map.Marks) {
                if (m.Team is 2 or 3) {
                    var b = Bucket($"{m.Kind} {(m.Team == ours ? "own" : "enemy")}"); b.Shown++;
                    double gap = Nearest(m.Team != ours && m.Kind != "seen" ? hidden : then.Where(a => a.Side == m.Team), m);
                    b.Distance.Add(gap); if (gap <= 600) b.Near++;
                } else if (m.Meeting is int meet && m.Kind is "forecast" or "baseline" or "held" or "contact") {
                    var b = Bucket($"× {m.Kind}"); b.Shown++;
                    if (Contact(m.Lane, t) is not { } c) { b.NoContact++; continue; }
                    double gap = double.Hypot(c.X - m.X, c.Y - m.Y);
                    b.Distance.Add(gap); b.Time.Add(Math.Abs(c.At - meet)); if (gap <= 600) b.Near++;
                }
            }
            // Coverage: hidden enemy creeps grouped into waves (900 units); a wave counts if any ○/△ is within 600.
            var symbols = map.Marks.Where(m => m.Team != 0 && m.Team != ours && m.Kind is "fog" or "march").ToArray();
            var left = hidden.ToList();
            while (left.Count > 0) {
                var head = left[0]; var wave = left.Where(a => double.Hypot(a.X - head.X, a.Y - head.Y) <= 900).ToList();
                left.RemoveAll(wave.Contains); waves++;
                if (!symbols.Any(m => wave.Any(a => double.Hypot(a.X - m.X, a.Y - m.Y) <= 600))) unmarked++;
            }
            dump?.WriteLine(JsonSerializer.Serialize(new { t, team, map, truth = then.Select(a => new[] { a.Side, Math.Round(a.X), Math.Round(a.Y), Ours(a) == "visible" ? 1 : 0 }) }, Wire.Json));
        }
        static string P(List<double> v, double q) => v.Count == 0 ? "n/a" : v.Order().ElementAt((int)Math.Round((v.Count - 1) * q)).ToString("0");
        Console.WriteLine($"{match} {team} ({hero}): {seconds} team-view seconds re-run, {snaps.Count} snapshots; scored {Shown} s after each projection");
        foreach (var (k, b) in scores)
            Console.WriteLine($"  {k,-14} {100.0 * b.Near / Math.Max(1, b.Shown),5:0.0}% of {b.Shown,5} within 600; distance median/P90 {P(b.Distance, .5)}/{P(b.Distance, .9)}" +
                (b.Time.Count > 0 ? $"; time error median/P90 {P(b.Time, .5)}/{P(b.Time, .9)} s; no contact in 40 s {b.NoContact}" : ""));
        Console.WriteLine($"  hidden enemy waves (900-unit groups) with a ○/△ within 600: {100.0 * (waves - unmarked) / Math.Max(1, waves):0.0}% of {waves}");
    }
}
