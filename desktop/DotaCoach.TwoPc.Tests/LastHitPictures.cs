using System.Text.Json;
using DotaCoach.TwoPc;

internal static partial class LastHitEval
{
    /// <summary>One creep's bar as the game client read it in one picture: the picture's server and capture times, its number, the reading.</summary>
    private sealed record Sighting(double T, double At, int Picture, int Fill, bool Ally, bool Covered);

    /// <summary>
    /// The red numbers on the bars the game client really read in the lab's pictures (lab.ts --frames DIR, labelled by
    /// --vision-label: each bar's unit and each picture's server time), for the creeps with a last-hit chance that were on screen
    /// in their last second. Two forecasts of the hits to come: the rhythms of <see cref="CreepHitHistory"/> (the product's), and
    /// with <paramref name="model"/> its damage in each tenth of the next second from the bar's last two seconds, red when the
    /// bar's health less that damage by the landing is within the weakest roll plus a margin. The simulated player is
    /// <see cref="Decompose"/>'s; the same creeps on the simulated screen and with better foresight for comparison.
    /// </summary>
    private static void Pictures(List<Life> chances, string directory, CreepDamageModel? model, string golden, double[] margins, HeroAttack attack,
        double distance, double latency, double fps, CreepBarGeometry geometry, List<Tower> towers, Func<Random, double> extra, double meanExtra,
        List<(double T, int Attacker, int Victim, double D)> hits, Dictionary<int, string> kinds)
    {
        const double lead = .15;
        var (sightings, height) = Sightings(directory);
        var pictured = new CreepBarGeometry(height);
        var timing = new LastHitTiming(lead, latency, Patient: true);
        double horizon = timing.Horizon(attack, distance);
        var lives = chances.Select(l => (Life: l, Seen: (sightings.GetValueOrDefault(l.Unit.Id) ?? []).Where(s => s.T >= l.Frames[0].T - .05 && s.T < l.End).ToList()))
            .Where(l => l.Seen.Any(s => s.T >= l.Life.End - 1)).ToList();
        Console.WriteLine($"pictures {directory} ({height}p): {lives.Count} of {chances.Count} chances on screen in their last second; " +
            $"horizon (lead, latency, swing and flight) {horizon:F3} s");
        if (model != null && golden.Length > 0) model.Check(golden);

        // Per creep and picture: the product's red, the model's forecast.
        var product = new List<bool[]>();
        var forecasts = new List<float[][]>();
        foreach (var (life, seen) in lives) {
            var history = new CreepHitHistory();
            double regen = CreepKinds.Regen(life.Unit.Name);
            var red = new bool[seen.Count];
            var hit = attack.Against(life.Unit.Name) ?? default;
            for (int k = 0; k < seen.Count && hit.Most > 0; k++) {
                var s = seen[k];
                var f = life.FrameAt(s.T);
                history.Add(s.At, s.Fill);
                var bar = new CreepHealthBar(0, 0, 12, s.Fill, pictured.Inner, pictured.Span, s.Ally, s.Covered);
                double? tower = towers.Where(w => w.Team != life.Unit.Team && double.Hypot(w.X - f.X, w.Y - f.Y) <= 700 + LaneView.TowerHulls)
                    .Select(w => (double?)w.Interval).FirstOrDefault();
                red[k] = CreepLabels.Kills(bar, f.Max, regen, attack, LastHitTiming.Roll(hit, timing), history, s.At, timing, distance, tower);
            }
            product.Add(red);
            if (model != null) forecasts.Add(Forecast(model, seen));
        }
        if (model != null) Accuracy(model, sightings, hits, kinds);

        var blows = hits.GroupBy(h => h.Victim).ToDictionary(g => g.Key, g => g.Select(h => (h.T, Kind: kinds.GetValueOrDefault(h.Attacker, "hero"), h.D)).OrderBy(h => h.T).ToList());
        var rows = new List<(string Name, Func<int, Random, double, double, double, Outcome> Play)> {
            ($"simulated {fps:F0} fps {geometry.Height}p / rhythms", (i, rng, reaction, spread, x) =>
                Simulate(lives[i].Life, attack, distance, timing, latency, reaction, spread, x, fps, geometry, towers, rng)),
            ("pictures / rhythms", (i, rng, reaction, spread, x) => Play(lives[i].Life, lives[i].Seen, k => product[i][k], attack, distance, latency, reaction, spread, x, rng)),
        };
        if (model != null)
            foreach (double margin in margins)
                rows.Add(($"pictures / model {margin:+0;-0;0} HP", (i, rng, reaction, spread, x) => {
                    var (life, seen) = lives[i];
                    if (attack.Against(life.Unit.Name) is not { } hit) return Outcome.Unknown;
                    double regen = CreepKinds.Regen(life.Unit.Name);
                    int max = life.Frames[0].Max;
                    return Play(life, seen, k => new CreepHealthBar(0, 0, 12, seen[k].Fill, pictured.Inner, pictured.Span, false, false).MostHp(max) + regen * horizon -
                        CreepDamageModel.Until(forecasts[i][k], horizon) <= hit.Least + margin, attack, distance, latency, reaction, spread, x, rng);
                }));
        rows.Add(("swings seen", (i, rng, reaction, spread, x) => Seen(lives[i].Life, blows.GetValueOrDefault(lives[i].Life.Unit.Id) ?? [], attack, distance,
            new LastHitTiming(lead, latency), reaction, spread, x, fps, geometry, rng)));
        rows.Add(("engine", (i, rng, reaction, spread, x) => Oracle(lives[i].Life, attack, distance, latency, reaction, spread, x, meanExtra, fps, rng)));
        foreach (var (player, reaction, spread) in new[] { ("reaction 0.2±0.04", .2, .04), ("exact 0.15", lead, 0d) }) {
            Console.WriteLine($"{"forecast / " + player,-36} {"n",5} {"kill",6} {"early",6} {"late",6} {"never",6}");
            foreach (var (name, play) in rows) {
                var rng = new Random(11);
                var tally = new Dictionary<Outcome, int>();
                for (int i = 0; i < lives.Count; i++) {
                    var outcome = play(i, rng, reaction, spread, extra(rng));
                    tally[outcome] = tally.GetValueOrDefault(outcome) + 1;
                }
                int total = tally.Where(t => t.Key != Outcome.Unknown).Sum(t => t.Value);
                string Share(Outcome o) => $"{100d * tally.GetValueOrDefault(o) / Math.Max(1, total),5:F1}%";
                Console.WriteLine($"  {name,-34} {total,5} {Share(Outcome.Kill)} {Share(Outcome.Early)} {Share(Outcome.Late)} {Share(Outcome.None)}");
            }
        }
    }

    /// <summary>The player of <see cref="Simulate"/> on the pictures: reacting to the first red one.</summary>
    private static Outcome Play(Life life, List<Sighting> seen, Func<int, bool> red, HeroAttack attack, double distance, double latency, double reaction,
        double spread, double extra, Random rng)
    {
        if (attack.Against(life.Unit.Name) is not { } hit) return Outcome.Unknown;
        for (int k = 0; k < seen.Count; k++) {
            if (!red(k)) continue;
            double land = seen[k].T + Math.Max(.08, reaction + spread * Gaussian(rng)) + latency + attack.DelayAt(distance) + extra;
            if (land >= life.End) return Outcome.Late;
            return life.HpBefore(land) <= hit.Least + rng.NextDouble() * (hit.Most - hit.Least) ? Outcome.Kill : Outcome.Early;
        }
        return Outcome.None;
    }

    /// <summary>
    /// The model's forecast at each sighting from the readings before it, the history broken as the model was trained: a missed
    /// picture, a capture gap over 0.15 s, the clock going back, a covered bar (or one just uncovered), the side changing.
    /// </summary>
    private static float[][] Forecast(CreepDamageModel model, List<Sighting> seen)
    {
        var result = new float[seen.Count][];
        var history = new List<CreepDamageModel.Reading>();
        for (int k = 0; k < seen.Count; k++) {
            var s = seen[k];
            if (k == 0 || s.Picture != seen[k - 1].Picture + 1 || s.At <= seen[k - 1].At || s.At - seen[k - 1].At > CreepDamageModel.Gap ||
                s.Covered || seen[k - 1].Covered || s.Ally != seen[k - 1].Ally)
                history.Clear();
            history.Add(new(s.At, s.Fill, s.Ally, s.Covered));
            int old = 0;
            while (s.At - history[old].At > CreepDamageModel.Lookback) old++;
            history.RemoveRange(0, old);
            result[k] = model.Predict(CreepDamageModel.Features(history));
        }
        return result;
    }

    /// <summary>
    /// The model's error on every matched bar of the recording (the training machine's measure): the creep and tower damage
    /// that came in the next 0.3 and 1 s against the forecast, and against forecasting none.
    /// </summary>
    private static void Accuracy(CreepDamageModel model, Dictionary<int, List<Sighting>> sightings, List<(double T, int Attacker, int Victim, double D)> hits,
        Dictionary<int, string> kinds)
    {
        var taken = hits.Where(h => kinds.ContainsKey(h.Attacker)).GroupBy(h => h.Victim).ToDictionary(g => g.Key, g => g.OrderBy(h => h.T).ToList());
        double[] windows = [.3, 1], error = new double[2], none = new double[2];
        int n = 0;
        foreach (var (unit, seen) in sightings) {
            var forecast = Forecast(model, seen);
            var mine = taken.GetValueOrDefault(unit) ?? [];
            for (int k = 0; k < seen.Count; k++) {
                n++;
                for (int w = 0; w < 2; w++) {
                    double truth = mine.Where(h => h.T > seen[k].T && h.T <= seen[k].T + windows[w]).Sum(h => h.D);
                    error[w] += Math.Abs(CreepDamageModel.Until(forecast[k], windows[w]) - truth);
                    none[w] += truth;
                }
            }
        }
        Console.WriteLine($"model on {n} matched bars, mean absolute error of the damage to come: 0.3 s {error[0] / n:F2} HP (none {none[0] / n:F2}), " +
            $"1 s {error[1] / n:F2} HP (none {none[1] / n:F2})");
    }

    /// <summary>Per unit, its bar in every picture where the labels matched one to it, oldest first; and the pictures' height.</summary>
    private static (Dictionary<int, List<Sighting>> Sightings, int Height) Sightings(string directory)
    {
        var frames = new Dictionary<int, (double At, int[][] Bars)>();
        int height = 1080;
        foreach (var line in File.ReadLines(Path.Combine(directory, "frames.jsonl"))) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            if (!r.TryGetProperty("file", out _)) continue;
            height = r.GetProperty("height").GetInt32();
            frames[r.GetProperty("i").GetInt32()] = (r.GetProperty("at").GetDouble(),
                r.GetProperty("bars").EnumerateArray().Select(b => b.EnumerateArray().Select(v => v.GetInt32()).ToArray()).ToArray());
        }
        var result = new Dictionary<int, List<Sighting>>();
        foreach (var line in File.ReadLines(Path.Combine(directory, "labels.jsonl"))) {
            using var doc = JsonDocument.Parse(line);
            var r = doc.RootElement;
            int i = r.GetProperty("i").GetInt32();
            if (!frames.TryGetValue(i, out var f)) continue;
            double t = r.GetProperty("t").GetDouble();
            int j = 0;
            foreach (var id in r.GetProperty("bars").EnumerateArray()) {
                var b = f.Bars[j++];
                int unit = id.GetInt32();
                if (unit < 0) continue;
                if (!result.TryGetValue(unit, out var list)) result[unit] = list = [];
                list.Add(new(t, f.At, i, b[3], b[4] == 1, b[5] == 1));
            }
        }
        foreach (var list in result.Values) list.Sort((a, b) => a.Picture.CompareTo(b.Picture));
        return (result, height);
    }
}

/// <summary>
/// A candidate forecast of the creep and tower damage a creep takes in each tenth of the next second from its bar's readings
/// over the last two seconds: twenty features (as the training machine's features_reference.py computes them) into a small
/// network (weights exported from its ONNX file: dense layers with ReLU between, then softplus(x - shift) * scale).
/// </summary>
internal sealed class CreepDamageModel
{
    /// <summary>One reading of the bar: capture time (s), fill pixels, an ally's bar, covered.</summary>
    public readonly record struct Reading(double At, int Fill, bool Ally, bool Covered);
    /// <summary>How far back the readings go, and the longest capture gap one history may span.</summary>
    public const double Lookback = 2, Gap = .15;
    private static readonly double[] Windows = [.3, .8, 2];
    private readonly (double[,] Weight, double[] Bias)[] layers;
    private readonly double shift, scale;
    private readonly int inputs;

    public CreepDamageModel(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        var r = doc.RootElement;
        layers = r.GetProperty("layers").EnumerateArray().Select(l => {
            var rows = l.GetProperty("weight").EnumerateArray().Select(w => w.EnumerateArray().Select(v => v.GetDouble()).ToArray()).ToArray();
            var weight = new double[rows.Length, rows[0].Length];
            for (int o = 0; o < rows.Length; o++) for (int i = 0; i < rows[0].Length; i++) weight[o, i] = rows[o][i];
            return (weight, l.GetProperty("bias").EnumerateArray().Select(v => v.GetDouble()).ToArray());
        }).ToArray();
        shift = r.GetProperty("shift").GetDouble();
        scale = r.GetProperty("scale").GetDouble();
        inputs = r.GetProperty("inputs").GetInt32();
    }

    /// <summary>The features of the last reading of <paramref name="history"/> (one bar's unbroken readings, oldest first).</summary>
    public static float[] Features(IReadOnlyList<Reading> history)
    {
        var cur = history[^1];
        var drops = new List<(double Before, double After, int Pixels)>();
        for (int k = 1; k < history.Count; k++)
            if (history[k - 1].Fill - history[k].Fill >= 2) drops.Add((history[k - 1].At, history[k].At, history[k - 1].Fill - history[k].Fill));
        var x = new List<double> { cur.Fill / 80d, cur.Ally ? 1 : 0, cur.Covered ? 1 : 0, history.Count > 1 ? (cur.At - history[^2].At) / Gap : 0,
            (cur.At - history[0].At) / Lookback, drops.Count > 0 ? (cur.At - drops[^1].After) / Lookback : -1, drops.Count > 0 ? drops[^1].Pixels / 80d : 0,
            drops.Count >= 2 ? (drops[^1].After - drops[^2].After) / Lookback : -1 };
        foreach (double window in Windows) {
            double start = cur.At - window;
            var first = history.First(v => v.At >= start);
            var losses = drops.Where(d => d.Before >= start).Select(d => d.Pixels).ToList();
            x.AddRange([(cur.At - first.At) / window, (first.Fill - cur.Fill) / 80d, losses.Count / 8d, losses.Sum() / 80d]);
        }
        return x.Select(v => (float)v).ToArray();
    }

    /// <summary>The damage (HP) in each tenth of a second ahead.</summary>
    public float[] Predict(float[] features)
    {
        var x = new double[layers[0].Weight.GetLength(1)];
        for (int i = 0; i < inputs; i++) x[i] = features[i];
        for (int l = 0; l < layers.Length; l++) {
            var (weight, bias) = layers[l];
            var y = new double[bias.Length];
            for (int o = 0; o < y.Length; o++) {
                double sum = bias[o];
                for (int i = 0; i < x.Length; i++) sum += weight[o, i] * x[i];
                y[o] = l < layers.Length - 1 ? Math.Max(0, sum) : sum;
            }
            x = y;
        }
        return x.Select(v => (float)((v - shift > 30 ? v - shift : Math.Log(1 + Math.Exp(v - shift))) * scale)).ToArray();
    }

    /// <summary>The damage foretold until <paramref name="horizon"/> seconds ahead; past the last tenth at the last half second's rate.</summary>
    public static double Until(float[] damage, double horizon)
    {
        double sum = 0;
        for (int b = 0; b < damage.Length; b++) sum += damage[b] * Math.Clamp(horizon * 10 - b, 0, 1);
        if (horizon > damage.Length / 10d) sum += (horizon - damage.Length / 10d) * 10 * damage[^5..].Average(v => (double)v);
        return sum;
    }

    /// <summary>The training machine's golden vectors: readings to features to damage, within its tolerances.</summary>
    public void Check(string file)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(file));
        double features = 0, damage = 0;
        bool close = true;
        foreach (var g in doc.RootElement.EnumerateArray()) {
            var history = g.GetProperty("observations").EnumerateArray()
                .Select(o => new Reading(o[0].GetDouble(), o[1].GetInt32(), o[2].GetInt32() == 1, o[3].GetInt32() == 1)).ToList();
            var x = Features(history);
            var want = g.GetProperty("bar").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            features = Math.Max(features, x.Select((v, i) => Math.Abs(v - want[i])).Max());
            var y = Predict(x);
            var expected = g.GetProperty("damage_hp").EnumerateArray().Select(v => v.GetDouble()).ToArray();
            for (int i = 0; i < y.Length; i++) {
                damage = Math.Max(damage, Math.Abs(y[i] - expected[i]));
                close &= Math.Abs(y[i] - expected[i]) <= 3e-4 + 2e-4 * Math.Abs(expected[i]);
            }
        }
        Console.WriteLine($"model golden vectors: features max |diff| {features:G3}, damage max |diff| {damage:G3} HP, within tolerance {close}");
    }
}
