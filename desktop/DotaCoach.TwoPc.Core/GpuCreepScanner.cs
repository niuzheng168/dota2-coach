using static DotaCoach.TwoPc.NativeCom;

namespace DotaCoach.TwoPc;

/// <summary>
/// Finds creep health bars in a captured frame without leaving the GPU: one compute pass over the
/// full-resolution BGRA picture, then a read-back of at most <see cref="Capacity"/> 16-byte bar records
/// (a few KB) instead of the picture. Used by its owner's thread only.
/// </summary>
public sealed unsafe class GpuCreepScanner : IDisposable
{
    public const int Capacity = 4096;
    // Colour classes in 0-255 sRGB, sampled from the default HUD at 1080p/1440p/4K: ally fill ~(71,131,50),
    // empty ~(12,29,6); enemy fill ~(128,71,53), empty ~(20,7,4); rims (0,0,0).
    // One thread group per picture row: the row is classified once into group-shared memory, then every left rim
    // is measured from there. Short, parallel work: a busy game GPU loses as little time as possible to it.
    private const string Source = """
        Texture2D<float4> Frame : register(t0);
        RWByteAddressBuffer Rows : register(u0);
        cbuffer Scan : register(b0) { int2 Origin; int2 Size; int Inner; int FirstRow; int LastRow; uint Capacity; };
        static const uint Other = 0, Dark = 1, AllyFill = 2, AllyEmpty = 3, EnemyFill = 4, EnemyEmpty = 5;
        groupshared uint Kinds[2048]; // 4 classes per word: rows up to 8192 pixels
        uint3 Pixel(int x, int y) { return (uint3)(Frame.Load(int3(Origin + int2(x, y), 0)).rgb * 255 + .5); }
        uint Classify(uint3 c)
        {
            uint r = c.r, g = c.g, b = c.b;
            if (g >= 60 && g * 10 >= r * 14 && g * 10 >= b * 17) return AllyFill;
            if (r >= 60 && r * 10 >= g * 14 && g * 10 >= b * 11) return EnemyFill;
            if (g < 60 && g >= r + 6 && g >= b + 8 && g * 10 >= r * 15) return AllyEmpty;
            if (r < 60 && r >= g + 6 && r >= b + 8 && r * 10 >= g * 15) return EnemyEmpty;
            return max(r, max(g, b)) <= 20 ? Dark : Other;
        }
        uint Kind(int x) { return (Kinds[x >> 2] >> ((x & 3) * 8)) & 0xFF; }
        uint RimSamples(int x, int y, int length)
        {
            uint dark = 0;
            [unroll] for (int s = 0; s < 8; s++) {
                uint3 c = Pixel(x + 1 + (length - 1) * s / 7, y);
                dark += max(c.r, max(c.g, c.b)) <= 24 ? 1 : 0;
            }
            return dark;
        }
        [numthreads(256, 1, 1)]
        void Scan(uint3 group : SV_GroupID, uint t : SV_GroupIndex)
        {
            int y = FirstRow + (int)group.y, words = (Size.x + 3) / 4;
            for (int w = t; w < words; w += 256) {
                uint packed = 0;
                [unroll] for (int j = 0; j < 4; j++)
                    if (w * 4 + j < Size.x) packed |= Classify(Pixel(w * 4 + j, y)) << (j * 8);
                Kinds[w] = packed;
            }
            GroupMemoryBarrierWithGroupSync();
            for (int x = 1 + (int)t; x + Inner + 5 < Size.x; x += 256) {
                // Left rim, then bar interior: two pixels, as the top rim row can start with one tinted pixel.
                if (Kind(x) != Dark || Kind(x + 1) < AllyFill || Kind(x + 2) < AllyFill) continue;
                uint first = Kind(x + 1);
                bool ally = first <= AllyEmpty;
                uint fillKind = ally ? AllyFill : EnemyFill, emptyKind = fillKind + 1, k = first;
                int limit = Inner + 3, i = 1;
                while (k == fillKind && i <= limit) k = Kind(x + ++i);
                int fill = i - 1;
                if (fill > Inner) continue;            // longer than a creep bar: hero/HUD bar or terrain
                bool cut = false;
                while (i <= limit) {
                    if (k == Dark) {
                        // Another bar's left rim inside this one: that bar is drawn on top from here on.
                        if (i < Inner - 1 && Kind(x + i + 1) >= AllyFill) { cut = true; break; }
                    } else if (k != emptyKind) break;
                    k = Kind(x + ++i);
                }
                int run = i - 1;
                if (run < Inner - 1 && !(cut && run >= Inner / 4)) continue;
                int seen = min(run, Inner);
                uint top = y < 2 ? 0 : max(RimSamples(x, y - 1, seen), RimSamples(x, y - 2, seen));
                uint slot;
                Rows.InterlockedAdd(0, 1, slot);
                if (slot >= Capacity) return;
                Rows.Store4(16 + slot * 16, uint4(x | (y << 16),
                    fill | (run << 12) | (ally ? 1u << 24 : 0) | (top >= 6 ? 1u << 25 : 0) | (cut ? 1u << 26 : 0) |
                    (cut && run == fill ? 1u << 27 : 0), top, 0));
            }
            // A bar whose left end lies under another bar is read from its right rim; its left rim would be Inner + 1
            // further left. Readable when the fill ends in the visible part, or the visible part is all fill (full).
            for (int r = Inner + 3 + (int)t; r < Size.x; r += 256) {
                if (Kind(r) != Dark || Kind(r - 1) < AllyFill || Kind(r - 2) < AllyFill) continue;
                int left = r - Inner - 1;
                if (Kind(left) == Dark && Kind(left + 1) >= AllyFill && Kind(left + 2) >= AllyFill) continue; // read from the left
                bool ally = Kind(r - 1) <= AllyEmpty;
                uint fillKind = ally ? AllyFill : EnemyFill, emptyKind = fillKind + 1;
                int i = r - 1;
                while (i > left && Kind(i) == emptyKind) i--;
                int empty = r - 1 - i;
                while (i > left && Kind(i) == fillKind) i--;
                int seen = r - 1 - i;
                // Stopped at the covering bar's right rim, with some fill in view; reaching `left` means a longer (hero) bar.
                if (i <= left || Kind(i) != Dark || seen < Inner / 4 || seen == empty) continue;
                uint top = y < 2 ? 0 : max(RimSamples(i, y - 1, seen), RimSamples(i, y - 2, seen));
                uint slot;
                Rows.InterlockedAdd(0, 1, slot);
                if (slot >= Capacity) return;
                Rows.Store4(16 + slot * 16, uint4(left | (y << 16),
                    (Inner - empty) | (Inner << 12) | (ally ? 1u << 24 : 0) | (top >= 6 ? 1u << 25 : 0), top, 0));
            }
        }
        """;
    private const int Bytes = 16 + Capacity * 16;
    private readonly GpuDevice gpu;
    private nint shader, rows, rowsView, staging, constants, scratch;
    private readonly PreciseTimer timer = new();
    /// <summary>Bar rows found by the last scan, including any past <see cref="Capacity"/> that were dropped.</summary>
    public int LastRecords { get; private set; }

    public GpuCreepScanner(GpuDevice gpu)
    {
        this.gpu = gpu;
        try {
            shader = D3D.Shader(gpu.Device, 18, GpuShaders.Compile(Source, "Scan", "cs_5_0"));
            rows = D3D.Buffer(gpu.Device, new() { ByteWidth = Bytes, BindFlags = 0x80 /* UAV */, MiscFlags = 0x20 /* raw views */ });
            rowsView = D3D.AccessView(gpu.Device, rows, new() { Format = 39 /* R32_TYPELESS */, Dimension = 1, A = 0, B = Bytes / 4, C = 1 /* raw */ });
            staging = D3D.Buffer(gpu.Device, new() { ByteWidth = Bytes, Usage = 3, CpuAccessFlags = 0x20000 });
            constants = D3D.Buffer(gpu.Device, new() { ByteWidth = 32, BindFlags = 0x4 });
        } catch { Dispose(); throw; }
    }

    /// <summary>Health bars in the client rectangle of a captured window frame (client coordinates).</summary>
    public List<CreepHealthBar> Scan(CaptureFrame frame, Rect32 client) => Scan(frame.Texture, client);

    /// <summary>Scans a whole BGRA picture from memory (offline evaluation and tests).</summary>
    public List<CreepHealthBar> ScanBgra(byte[] bgra, int width, int height)
    {
        if (bgra.Length != width * height * 4) throw new ArgumentException("Invalid BGRA size");
        nint texture = gpu.Texture(width, height, 87, 0, 0x8, 0);
        try {
            fixed (byte* pixels = bgra)
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(gpu.Context, 48))(
                    gpu.Context, texture, 0, 0, pixels, (uint)width * 4, 0);
            return Scan(texture, new Rect32 { Right = width, Bottom = height });
        } finally { Release(texture); }
    }

    internal List<CreepHealthBar> Scan(nint texture, Rect32 client)
    {
        Submit(texture, client);
        D3D.Flush(gpu.Context);
        return Collect();
    }

    /// <summary>Queues the scan of a captured frame without submitting it: the caller's next Present or Flush
    /// carries it, so overlay drawing and scanning reach a busy GPU as one piece of work. Then <see cref="Collect"/>.</summary>
    public void Submit(CaptureFrame frame, Rect32 client) => Submit(frame.Texture, client);

    private CreepBarGeometry pending;
    private void Submit(nint texture, Rect32 client)
    {
        int width = client.Right - client.Left, height = client.Bottom - client.Top;
        if (width > 8192) throw new ArgumentException("Picture too wide");
        pending = new CreepBarGeometry(height);
        nint context = gpu.Context, view = View(texture);
        // Rows from below the top bar to above the bottom of the screen; the HUD is rejected by shape.
        int firstRow = height * 3 / 100, lastRow = height * 98 / 100;
        try {
            var scan = stackalloc int[8] { client.Left, client.Top, width, height, pending.Inner, firstRow, lastRow, Capacity };
            D3D.Update(context, constants, scan);
            var zero = stackalloc uint[4];
            ((delegate* unmanaged[Stdcall]<nint, nint, uint*, void>)Slot(context, 51))(context, rowsView, zero);
            nint cs = shader, srv = view, uav = rowsView, cb = constants;
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(context, 69))(context, cs, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 67))(context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(context, 68))(context, 0, 1, &uav, null);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 71))(context, 0, 1, &cb);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, void>)Slot(context, 41))(context, 1, (uint)(lastRow - firstRow), 1);
            srv = 0; uav = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 67))(context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, uint*, void>)Slot(context, 68))(context, 0, 1, &uav, null);
            D3D.Copy(context, staging, rows);
        } finally { Release(view); }
    }

    /// <summary>Waits for the submitted scan and returns its bars (client coordinates).</summary>
    public List<CreepHealthBar> Collect()
    {
        D3D.Flush(gpu.Context); // no-op when a Present already submitted the scan
        var mapped = D3D.MapRead(gpu.Context, staging, timer);
        var found = new List<CreepBarRow>();
        try {
            uint* data = (uint*)mapped.Data;
            LastRecords = (int)data[0];
            int count = Math.Min(LastRecords, Capacity);
            for (int i = 0; i < count; i++) {
                uint* r = data + 4 + i * 4;
                uint bits = r[1];
                found.Add(new((int)(r[0] & 0xFFFF), (int)(r[0] >> 16), (int)(bits & 0xFFF), (int)((bits >> 12) & 0xFFF),
                    (bits & 1u << 24) != 0, (bits & 1u << 25) != 0, (bits & 1u << 26) != 0, (bits & 1u << 27) != 0));
            }
        } finally { D3D.Unmap(gpu.Context, staging); }
        return CreepHealthBars.FromRows(found, pending);
    }

    /// <summary>A view of the capture texture itself, or of a GPU copy when the capture pool's textures are not shader-readable.</summary>
    private nint View(nint texture)
    {
        if (scratch == 0) {
            try { return D3D.ShaderView(gpu.Device, texture, null); } catch (ArgumentException) { }
            var (width, height) = D3D.Size(texture);
            scratch = gpu.Texture(width, height, 87, 0, 0x8 /* shader resource */, 0);
        }
        D3D.Copy(gpu.Context, scratch, texture);
        return D3D.ShaderView(gpu.Device, scratch, null);
    }

    public void Dispose()
    {
        Release(scratch); scratch = 0;
        Release(constants); Release(staging); Release(rowsView); Release(rows); Release(shader);
        constants = staging = rowsView = rows = shader = 0;
        timer.Dispose();
    }
}
