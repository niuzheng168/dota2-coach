using System.Runtime.InteropServices;

namespace DotaCoach.TwoPc;

/// <summary>
/// 3D-engine utilisation of one adapter (all processes) from the Windows "GPU Engine" performance counters.
/// Tells whether the game leaves the GPU any idle time: only then is extra GPU work free.
/// </summary>
public sealed unsafe class GpuLoad : IDisposable
{
    private nint query, counter;
    private readonly string? adapter;
    /// <param name="adapter">LUID part of the counter instance names (<see cref="GpuDevice.AdapterLuid"/>); null for all adapters.</param>
    public GpuLoad(string? adapter = null)
    {
        this.adapter = adapter;
        if (PdhOpenQueryW(null, 0, out query) != 0) { query = 0; return; }
        if (PdhAddEnglishCounterW(query, @"\GPU Engine(*engtype_3D)\Utilization Percentage", 0, out counter) != 0) { Dispose(); return; }
        PdhCollectQueryData(query);
    }
    /// <summary>Percent busy since the previous call, summed over the 3D engines of all processes; null if unavailable.</summary>
    public double? Sample()
    {
        if (query == 0 || PdhCollectQueryData(query) != 0) return null;
        uint size = 0, count = 0;
        if (PdhGetFormattedCounterArrayW(counter, 0x200 /* double */, ref size, ref count, null) != unchecked((int)0x800007D2)) return null;
        var buffer = new byte[size];
        fixed (byte* items = buffer) {
            if (PdhGetFormattedCounterArrayW(counter, 0x200, ref size, ref count, items) != 0) return null;
            double total = 0;
            // PDH_FMT_COUNTERVALUE_ITEM_W: name pointer, then status (padded to 8) and the double value.
            for (int i = 0; i < count; i++) {
                byte* item = items + i * 24;
                if (*(uint*)(item + 8) != 0) continue;
                if (adapter != null && !new string(*(char**)item).Contains(adapter, StringComparison.OrdinalIgnoreCase)) continue;
                total += *(double*)(item + 16);
            }
            return Math.Min(100, total);
        }
    }
    public void Dispose()
    {
        if (query != 0) PdhCloseQuery(query);
        query = counter = 0;
    }
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhOpenQueryW(string? source, nint user, out nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhAddEnglishCounterW(nint query, string path, nint user, out nint counter);
    [DllImport("pdh.dll")] private static extern int PdhCollectQueryData(nint query);
    [DllImport("pdh.dll", CharSet = CharSet.Unicode)] private static extern int PdhGetFormattedCounterArrayW(nint counter, uint format, ref uint size, ref uint count, byte* items);
    [DllImport("pdh.dll")] private static extern int PdhCloseQuery(nint query);
}

/// <summary>
/// Whether a GPU overlay may follow every captured frame or stays at <see cref="LimitedFps"/>, decided from the
/// adapter's 3D load once a second. Each overlay frame preempts the game on the GPU for a moment: free while the
/// game leaves the GPU idle time (fps capped), but a GPU-bound game loses frames per visit. Measured with Dota
/// uncapped at 4K/144 Hz (RTX 3090 Ti): -6.5% game fps at every frame, -2.1% at 60 Hz; capped at 182: no change.
/// The visits hide part of a saturated GPU's load (switching time counts for no process): 93% at 60 Hz and 90% at
/// every frame when Dota is GPU-bound, 69% when capped. Hence a lower threshold to leave the limit than to enter it.
/// </summary>
public sealed class GpuPacing
{
    public const int LimitedFps = 60;
    /// <summary>Load from which every-frame showing stops at once.</summary>
    public const double LimitFrom = 85;
    /// <summary>While limited, the load has to stay below this to show every frame again.</summary>
    public const double FreeBelow = 80;
    private const int CalmSeconds = 3, MaxCalmSeconds = 60;
    private int calm, required = CalmSeconds, freeFor;
    private volatile bool limited = true;
    /// <summary>Starts limited; also limited whenever the load is unknown.</summary>
    public bool Limited => limited;

    /// <param name="load">The last second's load (<see cref="GpuLoad.Sample"/>).</param>
    public void Update(double? load)
    {
        if (!limited) {
            freeFor++;
            if (load is double busy && busy < LimitFrom) {
                if (freeFor >= MaxCalmSeconds) required = CalmSeconds;
                return;
            }
            // Back off when the load rose soon after leaving the limit, so a game on the edge does not flip every few seconds.
            if (freeFor < MaxCalmSeconds) required = Math.Min(MaxCalmSeconds, required * 2);
            limited = true; calm = 0;
            return;
        }
        calm = load < FreeBelow ? calm + 1 : 0;
        if (calm >= required) { limited = false; freeFor = 0; }
    }
}
