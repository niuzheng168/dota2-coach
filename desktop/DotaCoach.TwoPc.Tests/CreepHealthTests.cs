using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using DotaCoach.TwoPc;

internal static class CreepHealthTests
{
    public static void Run(Action<bool, string> check)
    {
        check(new CreepBarGeometry(1080).Inner == 78 && new CreepBarGeometry(1440).Inner == 104 && new CreepBarGeometry(2160).Inner == 155,
            "creep bar interior scales with the game height");
        var g4k = new CreepBarGeometry(2160);
        List<CreepBarRow> Bar(int x, int y, int rows, int fill, bool ally = true, int hidden = 0) =>
            Enumerable.Range(0, rows).Select(i => new CreepBarRow(x, y + i, fill, g4k.Inner + 1, ally, i == 0, false, i >= rows - hidden)).ToList();
        var found = CreepHealthBars.FromRows([.. Bar(100, 500, 12, 80), .. Bar(400, 500, 12, 155, ally: false)], g4k);
        check(found.Count == 2 && found[0] is { X: 100, Y: 499, Rows: 12, Fill: 80, Ally: true } && found[1].Fraction == 1,
            "scanned rows group into bars");
        check(Math.Abs(found[0].Fraction - 79.5 / 154.6) < 1e-9 && found[0].Hp(550) == 283 && found[1].Hp(935) == 935 &&
            found[0] with { Fill = 1 } is var low && low.Hp(300) == 1, "bar fill reads as health");
        check(CreepHealthBars.FromRows(Bar(100, 500, 2, 80), g4k).Count == 0 &&
              CreepHealthBars.FromRows(Bar(100, 500, 12, 80).Select(r => r with { TopRim = false }), g4k).Count == 0 &&
              CreepHealthBars.FromRows(Bar(100, 500, 12, 80, hidden: 7), g4k).Count == 0,
            "fragments, rimless strips and mostly hidden bars are not read");
        var split = Bar(100, 500, 12, 80).Select((r, i) => r with { Fill = 20 + 30 * (i % 3) });
        check(CreepHealthBars.FromRows(split, g4k).Count == 0, "rows that disagree on the fill are not read");

        check(CreepKinds.MaxHp("npc_dota_creep_goodguys_melee") == 550 && CreepKinds.MaxHp("npc_dota_creep_badguys_ranged") == 300 &&
              CreepKinds.MaxHp("npc_dota_creep_badguys_flagbearer") == 550 && CreepKinds.MaxHp("npc_dota_goodguys_siege") == 935 &&
              CreepKinds.MaxHp("npc_dota_badguys_siege_upgraded_mega") == 935 && CreepKinds.MaxHp("npc_dota_creep_goodguys_melee_upgraded_mega") == 700 &&
              CreepKinds.MaxHp("npc_dota_creep_badguys_ranged_upgraded") == 475 && CreepKinds.MaxHp("npc_dota_neutral_kobold") == null &&
              CreepKinds.MaxHp("npc_dota_creep_goodguys_melee_diretide") == null && CreepKinds.MaxHp("npc_dota_hero_luna") == null,
            "lane creep maximum health by unit name");
        check(CreepKinds.MaxHp("npc_dota_creep_goodguys_melee", 449) == 550 && CreepKinds.MaxHp("npc_dota_creep_goodguys_melee", 450) == 562 &&
              CreepKinds.MaxHp("npc_dota_creep_badguys_flagbearer", 2430) == 610 && CreepKinds.MaxHp("npc_dota_creep_badguys_ranged", 2430) == 360 &&
              CreepKinds.MaxHp("npc_dota_badguys_siege", 2430) == 935 && CreepKinds.MaxHp("npc_dota_creep_goodguys_ranged", -60) == 300,
            "lane creeps gain 12 health per 7.5 minutes of game clock");

        var camera = CameraModel.Default;
        var (sx, sy) = camera.Screen(-420, 310, 3840, 2160);
        var (gx, gy) = camera.Ground(sx, sy, 3840, 2160);
        check(Math.Abs(gx + 420) < 1e-6 && Math.Abs(gy - 310) < 1e-6, "camera projection round trip");

        // A wave seen with the camera looking at (-3000, -2500): bars where the camera puts each unit, plus a decoy unit off screen.
        var units = new List<MinimapUnit> {
            Unit("npc_dota_creep_goodguys_melee", 2, -3100, -2450), Unit("npc_dota_creep_goodguys_ranged", 2, -3180, -2560),
            Unit("npc_dota_creep_badguys_melee", 3, -2900, -2400), Unit("npc_dota_badguys_siege", 3, -2800, -2620),
            Unit("npc_dota_creep_badguys_melee", 3, -900, 1200), Unit("npc_dota_hero_luna", 2, -3050, -2500) };
        var bars = units.Take(4).Select(u => BarAt(camera.Screen(u.X + 3000, u.Y + 2500, 3840, 2160), u.Team == 2)).ToList();
        var matcher = new CreepUnitMatcher();
        var matches = matcher.Match(bars, units, 2, 3840, 2160);
        check(matches.Select(m => m?.Name).SequenceEqual(units.Take(4).Select(u => u.Name)), "bars matched to GSI units through the camera");
        var moved = units.Select(u => u with { X = u.X + 40 }).ToList(); // the camera follows: same screen, units 40 further
        check(matcher.Match(bars, moved, 2, 3840, 2160).Select(m => m?.Name).SequenceEqual(units.Take(4).Select(u => u.Name)),
            "matching follows a moving camera");
        check(matcher.Match(bars.Take(1).ToList(), [units[2]], 2, 3840, 2160)[0] == null, "no match across sides");
        var river = Unit("npc_dota_creep_badguys_ranged", 3, -2700, -2250); // in the river, 112 below the look-at point
        var riverBar = BarAt(camera.Screen(river.X + 3000, river.Y + 2500, 3840, 2160, -112), false);
        check(new CreepUnitMatcher().Match([.. bars, riverBar], [.. units, river], 2, 3840, 2160)[4]?.Name == river.Name, "a unit on lower ground is matched too");

        var tracks = new CreepTracks();
        string?[] names = [];
        for (int frame = 0; frame < 6; frame++) names = tracks.Update(bars, matches, frame * 7);
        var wrong = matches.Select((m, i) => i == 0 ? units[1] : m).ToArray();
        var shifted = bars.Select(b => b with { X = b.X + 3 }).ToList();
        check(tracks.Update(shifted, wrong, 49)[0] == "npc_dota_creep_goodguys_melee", "one stray match does not rename a tracked bar");
        check(tracks.Update(shifted, new MinimapUnit?[4], 2000).All(n => n == null), "tracks expire");

        var pacing = new GpuPacing();
        check(pacing.Limited, "pacing starts limited");
        pacing.Update(60); pacing.Update(60); pacing.Update(60);
        check(!pacing.Limited, "every frame once the GPU has room");
        pacing.Update(84); check(!pacing.Limited, "every frame below the limit");
        pacing.Update(90); check(pacing.Limited, "limited as soon as the GPU fills up");
        for (int i = 0; i < 5; i++) pacing.Update(60);
        check(pacing.Limited, "backs off after a quick fall-back");
        pacing.Update(60); check(!pacing.Limited, "free again after a longer calm");
        pacing.Update(null); check(pacing.Limited, "unknown load is treated as busy");

        if (OperatingSystem.IsWindows()) ScanSynthetic(check);

        static MinimapUnit Unit(string name, int team, double x, double y) => new(name, team, x, y, null, null, null);
        static CreepHealthBar BarAt((double X, double Y) anchor, bool ally) =>
            new((int)Math.Round(anchor.X) - 78, (int)Math.Round(anchor.Y) - 7, 12, 100, 155, 154.6, ally, false);
    }

    /// <summary>The GPU scan on a drawn 1080p picture with default-HUD colours: three bars and a hero-length bar it must skip.</summary>
    private static void ScanSynthetic(Action<bool, string> check)
    {
        const int width = 1920, height = 1080;
        var picture = new byte[width * height * 4];
        for (int i = 0; i < picture.Length; i += 4) { picture[i] = 70; picture[i + 1] = 90; picture[i + 2] = 80; picture[i + 3] = 255; }
        void Fill(int x0, int y0, int x1, int y1, (byte R, byte G, byte B) c)
        {
            for (int y = y0; y < y1; y++)
                for (int x = x0; x < x1; x++) { int o = (y * width + x) * 4; picture[o] = c.B; picture[o + 1] = c.G; picture[o + 2] = c.R; }
        }
        void Draw(int x, int y, int inner, int fill, bool ally)
        {
            Fill(x, y, x + inner + 2, y + 8, (0, 0, 0)); // rims; 6 interior rows
            Fill(x + 1, y + 1, x + 1 + fill, y + 7, ally ? ((byte)71, (byte)131, (byte)50) : ((byte)128, (byte)71, (byte)53));
            Fill(x + 1 + fill, y + 1, x + 1 + inner, y + 7, ally ? ((byte)12, (byte)29, (byte)6) : ((byte)20, (byte)7, (byte)4));
        }
        Draw(300, 400, 78, 40, true); Draw(700, 400, 78, 78, false); Draw(1100, 600, 78, 1, true);
        Draw(500, 800, 120, 100, false); // a hero's: fill longer than any creep bar
        Draw(1500, 300, 78, 60, true); Draw(1450, 300, 78, 30, false); // the second on top of the first's left part
        using var gpu = new GpuDevice();
        using var scanner = new GpuCreepScanner(gpu);
        var bars = scanner.ScanBgra(picture, width, height).OrderBy(b => b.X).ToList();
        check(bars.Select(b => (b.X, b.Y, b.Fill, b.Ally)).SequenceEqual([(300, 400, 40, true), (700, 400, 78, false), (1100, 600, 1, true),
            (1450, 300, 30, false), (1500, 300, 60, true)]), "GPU scan reads creep bars, also one whose left end is covered, and skips longer bars");
    }

    /// <summary>Logs every ~100 ms the scanned bars of the live "Dota 2" window together with the newest GSI payload
    /// a test server keeps in <paramref name="gsiFile"/>, and the unit name and label the display would show for each bar.</summary>
    public static void RecordBars(string output, double seconds, string gsiFile)
    {
        SetProcessDpiAwarenessContext(-4); // physical pixels, as the game client manifest declares
        nint window = FindWindowW(null, "Dota 2");
        GetClientRect(window, out var rect);
        var client = new Rect32 { Right = rect.Right, Bottom = rect.Bottom };
        using var gpu = new GpuDevice();
        using var capture = new WindowCapture(gpu, window);
        using var scanner = new GpuCreepScanner(gpu);
        using var log = new StreamWriter(output) { AutoFlush = true };
        var matcher = new CreepUnitMatcher();
        var tracks = new CreepTracks();
        var stop = Stopwatch.StartNew();
        while (stop.Elapsed.TotalSeconds < seconds) {
            Thread.Sleep(100);
            using var frame = capture.Latest();
            if (frame == null) continue;
            var bars = scanner.Scan(frame, client);
            string gsi = "null";
            try {
                using var stream = new FileStream(gsiFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                gsi = new StreamReader(stream).ReadToEnd();
            } catch (IOException) { }
            var matches = new MinimapUnit?[bars.Count];
            SpectatorSnapshot? snapshot = null;
            try {
                using var document = JsonDocument.Parse(gsi);
                if (document.RootElement.ValueKind == JsonValueKind.Object) {
                    snapshot = SpectatorGsi.Parse(document.RootElement.GetProperty("payload"), Environment.TickCount64);
                    if (snapshot.OwnTeam is int team) matches = matcher.Match(bars, snapshot.Minimap, team, rect.Right, rect.Bottom);
                }
            } catch (JsonException) { }
            var names = tracks.Update(bars, matches, Environment.TickCount64);
            var shown = bars.Select((b, i) => new { b.X, b.Y, b.Rows, b.Fill, b.Inner, b.Ally, b.Covered, Name = names[i],
                Label = names[i] is { } n && CreepKinds.MaxHp(n, snapshot?.Clock) is int max ? b.Hp(max).ToString() : $"{Math.Round(b.Fraction * 100)}%" });
            log.WriteLine($"{{\"frame\":{frame.SystemRelativeTime / 1e7:F4},\"now\":{Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency:F4}," +
                $"\"bars\":{JsonSerializer.Serialize(shown)},\"gsi\":{gsi}}}");
        }
    }

    /// <summary>Draws known labels through the real overlay and writes the back buffer before/after each Present.</summary>
    public static void OverlayTest(string directory)
    {
        Directory.CreateDirectory(directory);
        using var gpu = new GpuDevice();
        using var overlay = new GpuOverlay(gpu, new GlyphAtlas(27), excludeFromCapture: false);
        overlay.Place(0, 0, 1600, 900);
        var a = new List<OverlayLabel> { new(200, 200, "347", 0xA8FF8C), new(300, 300, "365", 0xA8FF8C), new(400, 200, "550", 0xFFFFFF),
            new(500, 400, "532", 0xA8FF8C), new(700, 300, "550", 0xFFFFFF), new(900, 300, "120", 0xFFFFFF) };
        var b = a.Select(l => l with { X = l.X + 7, Bottom = l.Bottom + 3 }).ToList();
        int n = 0;
        foreach (var labels in new[] { a, b, a, b, a }) {
            overlay.Draw(labels);
            File.WriteAllBytes(Path.Combine(directory, $"after-{n++}.bgra"), overlay.ReadBack());
        }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern nint FindWindowW(string? cls, string? title);
    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(nint context);
    [DllImport("user32.dll")] private static extern bool GetClientRect(nint h, out Rect32 rect);

    /// <summary>Offline evaluation: each file is int32 width, int32 height, then BGRA pixels. Prints one JSON line per file.</summary>
    public static void Eval(IEnumerable<string> files)
    {
        using var gpu = new GpuDevice();
        using var scanner = new GpuCreepScanner(gpu);
        foreach (string file in files) {
            var bytes = File.ReadAllBytes(file);
            int width = BitConverter.ToInt32(bytes, 0), height = BitConverter.ToInt32(bytes, 4);
            var started = Stopwatch.GetTimestamp();
            var bars = scanner.ScanBgra(bytes[8..], width, height);
            double ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
            Console.WriteLine(JsonSerializer.Serialize(new { file = Path.GetFileName(file), records = scanner.LastRecords, ms,
                bars = bars.Select(b => new { b.X, b.Y, b.Rows, b.Fill, b.Inner, b.Ally, b.Covered, fraction = b.Fraction }) }));
        }
    }
}
