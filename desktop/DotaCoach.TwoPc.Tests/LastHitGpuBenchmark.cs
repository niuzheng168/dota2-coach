using System.Diagnostics;
using System.Text.Json;
using DotaCoach.TwoPc;

// Opt-in Windows benchmark, no Dota window/GSI required. Every input has a different visible
// sequence encoded as three base-64 health bars. Validate the sequence after GPU scale/readback
// and recognition: neither polling, duplicate images nor queued-but-unprocessed frames count.
internal static unsafe class LastHitGpuBenchmark
{
    public static void Run()
    {
        const int w = 3840, h = 2160, x = 512, y = 520, rw = 560, rh = 16;
        using var gpu = new GpuDevice();
        using var scaler = new GpuNv12Scaler(gpu, w, h, 1920, 1080, 240);
        using var timer = new PreciseTimer();
        var source = new Rect32 { Right = w, Bottom = h };
        var texture = gpu.Texture(w, h, 87 /* BGRA8 */, 0, 0x20, 0);
        var background = new uint[w * h]; Array.Fill(background, 0xff888888);
        var patch = new uint[rw * rh]; var bars = new List<CreepBar>(8);
        var drill = new LaneDrill(.53);
        // ID3D11DeviceContext::UpdateSubresource; box is in source-texture pixels.
        var update = (delegate* unmanaged[Stdcall]<nint,nint,uint,int*,void*,uint,uint,void>)NativeCom.Slot(gpu.Context,48);
        int* box = stackalloc int[6] { x, y, 0, x + rw, y + rh, 1 };
        try {
            fixed (uint* p = background) update(gpu.Context, texture, 0, null, p, w * 4, 0);
            int sent = 0, read = 0;
            long begin = 0, period = Stopwatch.Frequency / 240, next = Stopwatch.GetTimestamp();
            const int warmup = 240, count = 2400;
            while (read < warmup + count) {
                long now = Stopwatch.GetTimestamp();
                if (sent < warmup + count && now >= next && !scaler.CreepQueueFull) {
                    int id = sent + 1;
                    Array.Fill(patch, 0xff888888);
                    for (int j = 0; j < 3; j++) {
                        int fill = 4 + ((id >> (6 * j)) & 63);
                        for (int row = 0; row < 14; row++) {
                            var line = patch.AsSpan(row * rw + j * 200, 160);
                            line.Fill(0xff000000);
                            if (row is >= 2 and < 12) { line[2..^2].Fill(0xff181818); line.Slice(2, 2 * fill).Fill(0xffbe4925); }
                        }
                    }
                    fixed (uint* p = patch) update(gpu.Context, texture, 0, box, p, rw * 4, 0);
                    if (!scaler.TryQueueCreepTexture(texture, source, id, null)) throw new Exception("queue accounting");
                    sent++; next = Math.Max(next + period, now);
                }
                if (!scaler.TryReadCreepBars(bars, out long stamp)) { timer.Wait(5_000); continue; }
                read++;
                if (stamp != read || bars.Count != 3 || bars.Where((b, j) => b.Fill != 4 + ((read >> (6 * j)) & 63)).Any())
                    throw new Exception($"Stale or misread GPU input {read}: stamp {stamp}, fills [{string.Join(',', bars.Select(b => b.Fill))}]");
                drill.Update(bars, read / 240d);
                if (read == warmup) begin = Stopwatch.GetTimestamp();
            }
            double seconds = Stopwatch.GetElapsedTime(begin).TotalSeconds;
            Console.WriteLine(JsonSerializer.Serialize(new { source = "synthetic 4K GPU texture (excludes WGC and game)", validated = count, seconds, fps = count / seconds }));
            if (count / seconds < 200) throw new Exception("Synthetic GPU pipeline below 200 detections/s");
        } finally { NativeCom.Release(texture); }
    }
}
