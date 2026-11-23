using System.Text.Json;
using DotaCoach.TwoPc;

/// <summary>
/// Offline evaluation of the red "one hit kills" numbers on engine truth from the last-hit lab (scripts/lasthit/lab.ts):
/// how long the lab hero's orders took to land against <see cref="HeroAttack.DelayAt"/>, how regular creep and tower hits
/// are, and per creep what a player reacting to its first red frame would get: the kill, a hit landing too early (the creep
/// survives it), too late (someone else killed it first), or never a red number. The screen is simulated from the engine's
/// health at a display rate and resolution: the bar's fill pixels, read without error. The lab hero's own hits are not the
/// simulated player's: a creep's life is cut at the first of them.
///   --last-hit-eval [--synthetic N | TRACE.jsonl ...] [--hero NAME] [--level N] [--distance D] [--fps F] [--height H] [--extra S] [--latency S]
/// </summary>
internal static class LastHitEval
{
    private sealed record Unit(int Id, string Name, int Team);
    private sealed class Life(Unit unit, string kind)
    {
        public readonly Unit Unit = unit;
        /// <summary>The arena round it fought in: open, ours (under the Radiant tower), theirs; "lane" elsewhere.</summary>
        public readonly string Kind = kind;
        public readonly List<(double T, int Hp, int Max, double X, double Y)> Frames = [];
        /// <summary>Death; the first hit of the lab hero (later outcomes are unknown).</summary>
        public double End = double.PositiveInfinity, Cut = double.PositiveInfinity;
        public int HpBefore(double t)
        {
            int lo = 0, hi = Frames.Count - 1, found = -1;
            while (lo <= hi) { int mid = (lo + hi) / 2; if (Frames[mid].T < t) { found = mid; lo = mid + 1; } else hi = mid - 1; }
            return found < 0 ? Frames[0].Hp : Frames[found].Hp;
        }
    }
    private enum Outcome { Kill, Early, Late, None, Unknown }
    private sealed record Tower(int Team, double X, double Y, double Interval);

    public static void Run(string[] args)
    {
        string Option(string name, string fallback) { int i = Array.IndexOf(args, name); return i >= 0 && i + 1 < args.Length ? args[i + 1] : fallback; }
        var table = new HeroAttackTable("data/dota2-hero-attack.json");
        var lives = new List<Life>();
        var towers = new List<Tower>();
        string? labHero = null;
        var orders = new List<Order>();
        var hits = new List<(double T, int Attacker, int Victim)>();
        var kinds = new Dictionary<int, string>();
        if (Option("--synthetic", "") is { Length: > 0 } n) lives.AddRange(Synthetic(int.Parse(n), new Random(7)));
        foreach (var file in args.Where(a => a.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)))
            labHero ??= Read(file, table, lives, towers, orders, hits, kinds);
        string heroName = Option("--hero", labHero ?? "npc_dota_hero_luna");
        var attack = table.Attack(new(heroName, int.Parse(Option("--level", "1")), false, 0, new bool[8], []), out string? why) ??
            throw new ArgumentException(why ?? heroName);
        double distance = double.Parse(Option("--distance", attack.Projectile > 0 ? (attack.Range + HeroAttack.Hulls - 30).ToString() : "130"));
        double fps = double.Parse(Option("--fps", "144")), extra = double.Parse(Option("--extra", "0")), latency = double.Parse(Option("--latency", ".08"));
        var geometry = new CreepBarGeometry(int.Parse(Option("--height", "2160")));

        // How much later than DelayAt the lab hero's hits landed; the simulated player's hits land as late, drawn from these.
        var lateness = Delays(orders, attack);
        double Extra(Random rng) => lateness.Count > 0 ? lateness[rng.Next(lateness.Count)] : extra;
        Rhythms(hits, kinds, lives);
        // Last-hit chances: creeps the lab hero never hit that died after falling below half health in the trace.
        var chances = lives.Where(l => l.Frames.Count > 0 && l.End < double.PositiveInfinity && l.Cut == double.PositiveInfinity &&
            l.Frames.Min(f => f.Hp) * 2 < l.Frames.Max(f => f.Max)).ToList();
        Console.WriteLine(JsonSerializer.Serialize(new { creeps = lives.Count, chances = chances.Count, hero = heroName, attack, distance, fps,
            height = geometry.Height, trueDelay = attack.DelayAt(distance) }));
        // Achievable: some press time lands while the creep lives, at or below the weakest roll.
        int achievable = chances.Count(life => attack.Against(life.Unit.Name) is { } hit && Enumerable.Range(0, (int)((life.End - life.Frames[0].T) * fps))
            .Select(k => life.Frames[0].T + k / fps + latency + attack.DelayAt(distance)).Any(land => land < life.End && life.HpBefore(land) <= hit.Least));
        Console.WriteLine(JsonSerializer.Serialize(new { latency, achievable = Math.Round(100d * achievable / Math.Max(1, chances.Count), 1) }));
        var rules = new List<(string Name, LastHitTiming? Timing)> { ("this frame", null) };
        foreach (double lead in new[] { .1, .15, .2 }) rules.Add(($"ahead {lead:F2}", new LastHitTiming(lead, latency)));
        rules.Add(("ahead 0.15 alive", new LastHitTiming(.15, latency, Alive: true)));
        rules.Add(("ahead 0.15 +singles", new LastHitTiming(.15, latency, true)));
        rules.Add(("ahead 0.15 patient", new LastHitTiming(.15, latency, Patient: true)));
        rules.Add(("ahead 0.15 patient alive", new LastHitTiming(.15, latency, Alive: true, Patient: true)));
        rules.Add(("ahead 0.10 patient", new LastHitTiming(.10, latency, Patient: true)));
        var kindsOfRound = chances.Select(l => l.Kind).Distinct().Order().ToArray();
        Console.WriteLine($"{"rule",-22} {"react",5} {"n",5} {"kill",6} {"early",6} {"late",6} {"never",6}  kill by round: {string.Join(" ", kindsOfRound)}");
        foreach (double reaction in new[] { .15, .2, .25 })
            foreach (var (name, timing) in rules) {
                var rng = new Random(11);
                var tally = new Dictionary<(string Kind, Outcome Outcome), int>();
                foreach (var life in chances) {
                    var outcome = Simulate(life, attack, distance, timing, latency, reaction, .04, Extra(rng), fps, geometry, towers, rng);
                    tally[(life.Kind, outcome)] = tally.GetValueOrDefault((life.Kind, outcome)) + 1;
                }
                int Count(Outcome o, string? kind = null) => tally.Where(t => t.Key.Outcome == o && (kind == null || t.Key.Kind == kind)).Sum(t => t.Value);
                int Total(string? kind = null) => tally.Where(t => t.Key.Outcome != Outcome.Unknown && (kind == null || t.Key.Kind == kind)).Sum(t => t.Value);
                string Share(Outcome o, string? kind = null) => $"{100d * Count(o, kind) / Math.Max(1, Total(kind)),5:F1}%";
                Console.WriteLine($"{name,-22} {reaction,5:F2} {Total(),5} {Share(Outcome.Kill)} {Share(Outcome.Early)} {Share(Outcome.Late)} {Share(Outcome.None)}  " +
                    string.Join(" ", kindsOfRound.Select(k => Share(Outcome.Kill, k))));
            }
    }

    /// <summary>
    /// A closed-loop run (lab.ts --loop): every attack the simulated player ordered on a red number, by rule, judged by the
    /// engine (killed it, landed on a survivor, or the creep died first), and per rule the share of the creeps dying in its
    /// rounds that the hero last-hit or denied.
    /// </summary>
    public static void Loop(string file)
    {
        int heroId = -1, round = 0;
        var roundOf = new Dictionary<int, int>();           // creep -> round it fought in
        var orders = new List<(string Rule, int Target, double T)>();
        var landed = new Dictionary<int, double?>();        // order index -> landing time
        var killedBy = new Dictionary<int, int>();          // creep -> killer
        var skipped = new Dictionary<string, int>();
        foreach (var line in File.ReadLines(file)) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (r.GetProperty("k").GetString()) {
                case "round": round = r.GetProperty("n").GetInt32(); break;
                case "unit": roundOf[r.GetProperty("id").GetInt32()] = round; break;
                case "h": if (heroId < 0) heroId = r.GetProperty("h")[0][0].GetInt32(); break;
                case "order" when r.GetProperty("kind").GetString() == "cue":
                    orders.Add((r.GetProperty("tag").GetString()!.Split('-')[0], r.GetProperty("target").GetInt32(), r.GetProperty("t").GetDouble()));
                    break;
                case "done":
                    if (orders.Count > 0 && orders[^1].Target == r.GetProperty("target").GetInt32() && !landed.ContainsKey(orders.Count - 1))
                        landed[orders.Count - 1] = r.GetProperty("landed") is { ValueKind: JsonValueKind.Number } l ? l.GetDouble() : null;
                    break;
                case "cue": {
                    string why = r.GetProperty("busy").GetBoolean() ? "busy" : "not found";
                    skipped[why] = skipped.GetValueOrDefault(why) + 1;
                    break;
                }
                case "kill": killedBy[r.GetProperty("v").GetInt32()] = r.GetProperty("a").GetInt32(); break;
            }
        }
        Console.WriteLine($"{"rule",-6} {"orders",6} {"kill",6} {"early",6} {"late",6}   {"chances",7} {"hero got",8}");
        foreach (var rule in new[] { "ahead", "frame" }) {
            var mine = orders.Select((o, i) => (o, i)).Where(x => x.o.Rule == rule).ToList();
            int kills = mine.Count(x => killedBy.GetValueOrDefault(x.o.Target) == heroId);
            int late = mine.Count(x => killedBy.GetValueOrDefault(x.o.Target, heroId) != heroId && landed.GetValueOrDefault(x.i) == null);
            int early = mine.Count - kills - late;
            int parity = rule == "ahead" ? 1 : 0;
            var chances = killedBy.Keys.Where(c => roundOf.TryGetValue(c, out int n) && n % 2 == parity).ToList();
            int got = chances.Count(c => killedBy[c] == heroId);
            string P(int k, int n) => $"{100d * k / Math.Max(1, n),5:F1}%";
            Console.WriteLine($"{rule,-6} {mine.Count,6} {P(kills, mine.Count)} {P(early, mine.Count)} {P(late, mine.Count)}   {chances.Count,7} {P(got, chances.Count),8}");
        }
        Console.WriteLine("skipped cues: " + string.Join(", ", skipped.Select(s => $"{s.Key} {s.Value}")));
        ScreenLag(file);
    }

    /// <summary>
    /// How far the screen trails the server in a closed loop: each red frame's capture time, put on the server's clock by the
    /// harness's send times against the server's receipt of the same commands, minus when the engine's health first came down
    /// to the number shown.
    /// </summary>
    private static void ScreenLag(string file)
    {
        string cuesFile = Path.ChangeExtension(file, null) + ".cues.jsonl";
        if (!File.Exists(cuesFile)) return;
        var sent = File.ReadLines(cuesFile).Select(l => JsonDocument.Parse(l).RootElement).Where(r => r.TryGetProperty("sent", out _) && r.TryGetProperty("Hp", out _))
            .Select(r => (Sent: r.GetProperty("sent").GetDouble(), At: r.GetProperty("At").GetDouble(), Hp: r.GetProperty("Hp").GetInt32())).ToList();
        var commands = new List<(double T, int Target)>();
        var health = new Dictionary<int, List<(double T, int Hp)>>();
        foreach (var line in File.ReadLines(file)) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            switch (r.GetProperty("k").GetString()) {
                case "order" when r.GetProperty("kind").GetString() == "cue": commands.Add((r.GetProperty("t").GetDouble(), r.GetProperty("target").GetInt32())); break;
                case "cue": commands.Add((r.GetProperty("t").GetDouble(), -1)); break;
                case "f":
                    foreach (var c in r.GetProperty("c").EnumerateArray()) {
                        if (!health.TryGetValue(c[0].GetInt32(), out var list)) health[c[0].GetInt32()] = list = [];
                        list.Add((r.GetProperty("t").GetDouble(), c[1].GetInt32()));
                    }
                    break;
            }
        }
        int n = Math.Min(sent.Count, commands.Count);
        if (n == 0) return;
        // The server's clock minus the harness's, per command (the console's own delay and the server's tick included).
        var offsets = Enumerable.Range(0, n).Select(i => commands[i].T - sent[i].Sent).Order().ToList();
        double offset = offsets[offsets.Count / 2];
        var lags = new List<double>();
        for (int i = 0; i < n; i++) {
            if (commands[i].Target < 0 || !health.TryGetValue(commands[i].Target, out var list)) continue;
            double shownAt = sent[i].At + offset;
            var reached = list.Where(h => h.T <= shownAt + 1 && h.Hp <= sent[i].Hp + 8).Select(h => (double?)h.T).FirstOrDefault();
            if (reached is double t) lags.Add(shownAt - t);
        }
        lags.Sort();
        if (lags.Count > 0)
            Console.WriteLine($"screen lag (red frame captured after the server reached its health), s: n {lags.Count}, p10 {lags[lags.Count / 10]:F3}, " +
                $"p50 {lags[lags.Count / 2]:F3}, p90 {lags[lags.Count * 9 / 10]:F3}; console offset spread p10-p90 {offsets[offsets.Count / 10] - offset:F3}..{offsets[offsets.Count * 9 / 10] - offset:F3}");
    }

    /// <summary>One player and one creep: the first red frame, a reaction, the order's way to the server, the swing and the flight.</summary>
    private static Outcome Simulate(Life life, HeroAttack attack, double distance, LastHitTiming? timing, double latency, double reaction, double spread,
        double extra, double fps, CreepBarGeometry geometry, List<Tower> towers, Random rng)
    {
        if (attack.Against(life.Unit.Name) is not { } hit || life.Frames.Count == 0) return Outcome.Unknown;
        var history = new CreepHitHistory();
        double regen = CreepKinds.Regen(life.Unit.Name);
        int index = 0;
        for (double t = life.Frames[0].T; t < Math.Min(life.End, life.Cut); t += 1 / fps) {
            while (index + 1 < life.Frames.Count && life.Frames[index + 1].T <= t) index++;
            var f = life.Frames[index];
            if (f.Hp <= 0) break;
            int fill = f.Hp >= f.Max ? geometry.Inner : Math.Max(1, (int)Math.Ceiling(f.Hp / (double)f.Max * geometry.Span - 1e-9));
            history.Add(t, fill);
            var bar = new CreepHealthBar(0, 0, 12, fill, geometry.Inner, geometry.Span, false, false);
            double? tower = towers.Where(w => w.Team != life.Unit.Team && double.Hypot(w.X - f.X, w.Y - f.Y) <= 700 + LaneView.TowerHulls)
                .Select(w => (double?)w.Interval).FirstOrDefault();
            if (!CreepLabels.Kills(bar, f.Max, regen, attack, LastHitTiming.Roll(hit, timing), history, t, timing, distance, tower)) continue;
            double press = Math.Max(.08, reaction + spread * Gaussian(rng));
            double land = t + press + latency + attack.DelayAt(distance) + extra;
            if (land >= life.Cut) return Outcome.Unknown;
            if (land >= life.End) return Outcome.Late;
            return life.HpBefore(land) <= hit.Least + rng.NextDouble() * (hit.Most - hit.Least) ? Outcome.Kill : Outcome.Early;
        }
        return life.Cut < life.End ? Outcome.Unknown : Outcome.None;
    }

    private static double Gaussian(Random rng) => Math.Sqrt(-2 * Math.Log(1 - rng.NextDouble())) * Math.Cos(2 * Math.PI * rng.NextDouble());

    /// <summary>One order of the lab hero: when it was given and its hit landed, the distance and the angle to turn.</summary>
    private sealed record Order(double T, double? Landed, double Distance, double Turn, double SinceLand, bool Idle);

    /// <summary>Order to hit of the lab hero against <see cref="HeroAttack.DelayAt"/> (swing plus the flight over the centre distance).</summary>
    private static List<double> Delays(List<Order> orders, HeroAttack attack)
    {
        // An order soon after the previous one can wait for the attack cooldown (it starts with the swing, landed or not):
        // only orders an interval and a delay after the previous one count.
        var ready = orders.Where((o, i) => i == 0 || o.T - orders[i - 1].T > attack.Interval + attack.Delay).ToList();
        var rows = ready.Where(o => o.Landed != null && o.SinceLand > attack.Interval && o.Idle)
            .Select(o => (Late: o.Landed!.Value - o.T - attack.DelayAt(o.Distance), o.Turn, o.Distance)).ToList();
        // Residuals in server ticks.
        Console.WriteLine("delay - model, ticks: " + string.Join(" ", rows.GroupBy(r => (int)Math.Round(r.Late * 30)).OrderBy(g => g.Key).Select(g => $"{g.Key}:{g.Count()}")));
        if (rows.Count == 0) return [];
        static object? Stats(IEnumerable<double> values)
        {
            var v = values.Order().ToList();
            return v.Count == 0 ? null : new { n = v.Count, mean = Math.Round(v.Average(), 3), p10 = Math.Round(v[v.Count / 10], 3),
                p50 = Math.Round(v[v.Count / 2], 3), p90 = Math.Round(v[v.Count * 9 / 10], 3) };
        }
        double reach = attack.Range + HeroAttack.Hulls;
        Console.WriteLine(JsonSerializer.Serialize(new { delayMinusModel = Stats(rows.Select(r => r.Late)),
            inRangeFacing = Stats(rows.Where(r => r.Distance <= reach && r.Turn < .3).Select(r => r.Late)),
            inRangeTurning = Stats(rows.Where(r => r.Distance <= reach && r.Turn >= .3).Select(r => r.Late)),
            outOfRange = Stats(rows.Where(r => r.Distance > reach).Select(r => r.Late)), orders = orders.Count,
            unlanded = orders.Count(o => o.Landed == null), cooling = orders.Count(o => o.Landed != null && o.SinceLand <= attack.Interval) }));
        return rows.Select(r => r.Late).ToList();
    }

    /// <summary>Seconds between one attacker's consecutive hits on one creep, and how often it hits again while the creep lives.</summary>
    private static void Rhythms(List<(double T, int Attacker, int Victim)> hits, Dictionary<int, string> kinds, List<Life> lives)
    {
        var ends = lives.GroupBy(l => l.Unit.Id).ToDictionary(g => g.Key, g => g.Max(l => l.End));
        foreach (var group in hits.Where(h => kinds.ContainsKey(h.Attacker)).GroupBy(h => kinds[h.Attacker])) {
            double period = group.Key switch { "tower" => .833, "siege" => 3, _ => 1 };
            var gaps = new List<double>();
            int again = 0, stopped = 0;
            foreach (var pair in group.GroupBy(h => (h.Attacker, h.Victim))) {
                var times = pair.Select(h => h.T).Order().ToList();
                for (int i = 1; i < times.Count; i++) gaps.Add(times[i] - times[i - 1]);
                again += times.Count - 1;
                if (ends.GetValueOrDefault(pair.Key.Victim, double.PositiveInfinity) > times[^1] + period + .07) stopped++;
            }
            if (gaps.Count == 0) continue;
            gaps.Sort();
            Console.WriteLine(JsonSerializer.Serialize(new { attacker = group.Key, hits = group.Count(), median = Math.Round(gaps[gaps.Count / 2], 3),
                onBeat = Math.Round(100d * gaps.Count(g => Math.Abs(g - period) <= .07) / gaps.Count, 1),
                hitsAgain = Math.Round(100d * again / Math.Max(1, again + stopped), 1) }));
        }
    }

    /// <returns>The lab hero's unit name.</returns>
    private static string? Read(string file, HeroAttackTable table, List<Life> lives, List<Tower> towers, List<Order> orders,
        List<(double T, int Attacker, int Victim)> hits, Dictionary<int, string> kinds)
    {
        var units = new Dictionary<int, Unit>();
        var alive = new Dictionary<int, Life>();
        string? hero = null, round = "lane"; int team = 0, heroId = -1;
        var pending = new Dictionary<int, JsonElement>();
        var idle = new Dictionary<double, bool>(); // order time -> the hero was not attacking then
        int heroTarget = -1;
        foreach (var line in File.ReadLines(file)) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            double t = r.TryGetProperty("t", out var tv) ? tv.GetDouble() : 0;
            switch (r.GetProperty("k").GetString()) {
                case "begin": hero = r.GetProperty("hero").GetString(); team = r.GetProperty("team").GetInt32(); break;
                case "round": round = r.GetProperty("kind").GetString()!; break;
                case "tower":
                    if (table.Tower(r.GetProperty("name").GetString()!) is { } stats)
                        towers.Add(new(r.GetProperty("team").GetInt32(), r.GetProperty("x").GetDouble(), r.GetProperty("y").GetDouble(), stats.Interval));
                    kinds[r.GetProperty("id").GetInt32()] = "tower";
                    break;
                case "unit": {
                    var u = new Unit(r.GetProperty("id").GetInt32(), r.GetProperty("name").GetString()!, r.GetProperty("team").GetInt32());
                    units[u.Id] = u;
                    kinds[u.Id] = CreepKinds.IsSiege(u.Name) ? "siege" : u.Name.Contains("_ranged") ? "ranged" : "melee";
                    break;
                }
                case "f":
                    foreach (var c in r.GetProperty("c").EnumerateArray()) {
                        int id = c[0].GetInt32();
                        if (!alive.TryGetValue(id, out var life)) {
                            if (!units.TryGetValue(id, out var u)) continue;
                            lives.Add(alive[id] = life = new(u, round));
                        }
                        life.Frames.Add((t, c[1].GetInt32(), c[2].GetInt32(), c[3].GetDouble(), c[4].GetDouble()));
                    }
                    break;
                case "h":
                    foreach (var h in r.GetProperty("h").EnumerateArray()) if (h[0].GetInt32() == heroId) heroTarget = h[6].GetInt32();
                    if (heroId < 0)
                        foreach (var h in r.GetProperty("h").EnumerateArray())
                            if (h[1].GetString() == hero && h[2].GetInt32() == team) heroId = h[0].GetInt32();
                    break;
                case "dmg": {
                    int a = r.GetProperty("a").GetInt32(), v = r.GetProperty("v").GetInt32();
                    if (r.GetProperty("inflictor").GetInt32() < 0 && units.ContainsKey(v)) hits.Add((t, a, v));
                    if (a == heroId && heroId >= 0 && alive.TryGetValue(v, out var life)) life.Cut = Math.Min(life.Cut, t);
                    break;
                }
                case "kill":
                    if (alive.Remove(r.GetProperty("v").GetInt32(), out var dead)) dead.End = t;
                    break;
                case "gone": // removed at the end of an arena round: no death, no last-hit chance
                    if (alive.Remove(r.GetProperty("id").GetInt32(), out var removed)) removed.Cut = Math.Min(removed.Cut, t);
                    break;
                case "order": pending[r.GetProperty("target").GetInt32()] = r.Clone(); idle[t] = heroTarget < 0; break;
                case "done":
                    if (pending.Remove(r.GetProperty("target").GetInt32(), out var order))
                        orders.Add(new(order.GetProperty("t").GetDouble(), r.GetProperty("landed") is { ValueKind: JsonValueKind.Number } l ? l.GetDouble() : null,
                            order.GetProperty("dist").GetDouble(), order.GetProperty("turn").GetDouble(),
                            order.TryGetProperty("sinceLand", out var since) ? since.GetDouble() : 100, idle.GetValueOrDefault(order.GetProperty("t").GetDouble())));
                    break;
            }
        }
        foreach (var life in alive.Values) life.End = life.Frames[^1].T + 1 / 30d;
        return hero;
    }

    /// <summary>
    /// Plumbing check without the game: lane creeps under one to four attackers, each hitting once a second from a random
    /// start (17-21 on melee creeps, 19-23 on ranged), now and then turning away; heroes hit at random (45-60).
    /// </summary>
    private static IEnumerable<Life> Synthetic(int count, Random rng)
    {
        for (int k = 0; k < count; k++) {
            bool ranged = rng.Next(3) == 0;
            var life = new Life(new(k, ranged ? "npc_dota_creep_badguys_ranged" : "npc_dota_creep_badguys_melee", 3), "synthetic");
            int max = ranged ? 300 : 550, hp = max;
            var next = Enumerable.Range(0, 1 + rng.Next(4)).Select(_ => rng.NextDouble() * 1.5).ToList();
            double hero = rng.NextDouble() * 8;
            for (int tick = 0; hp > 0 && tick < 30 * 60; tick++) {
                double t = tick / 30d;
                for (int a = 0; a < next.Count; a++)
                    while (next[a] <= t && hp > 0) {
                        hp -= ranged ? 19 + rng.Next(5) : 17 + rng.Next(5);
                        next[a] = rng.NextDouble() < .04 ? double.PositiveInfinity : next[a] + 1;
                    }
                if (rng.NextDouble() < .02 && !next.Any(n => n < double.PositiveInfinity)) next.Add(t + rng.NextDouble());
                if (hero <= t && hp > 0) { hp -= 45 + rng.Next(16); hero = t + 1.5 + rng.NextDouble() * 8; }
                if (hp <= 0) { life.End = t; break; }
                life.Frames.Add((t, hp, max, 0, 0));
            }
            if (life.Frames.Count > 0) yield return life;
        }
    }
}
