using System.ComponentModel;
using System.Runtime.InteropServices;
using static DotaCoach.TwoPc.NativeCom;

namespace DotaCoach.TwoPc;

/// <summary>A short text centred on <see cref="X"/> whose bottom edge sits at <see cref="Bottom"/> (client pixels).</summary>
public readonly record struct OverlayLabel(int X, int Bottom, string Text, uint Rgb);

/// <summary>
/// Click-through, never-activated topmost window over the game drawn by the GPU through DirectComposition:
/// a premultiplied-alpha flip swap chain on the capture device, one draw call per frame for all labels.
/// No layered-window CPU copies, so it can follow every captured frame. Create, draw and pump messages
/// on ONE thread.
/// </summary>
public sealed unsafe class GpuOverlay : IDisposable
{
    private const string Source = """
        struct Quad { float4 Rect; float4 Uv; float4 Color; };
        StructuredBuffer<Quad> Quads : register(t0);
        Texture2D<float4> Atlas : register(t1);
        SamplerState Nearest : register(s0);
        cbuffer View : register(b0) { float2 Scale; uint First; uint Unused; }; // SV_VertexID ignores the draw's start vertex
        struct V { float4 Pos : SV_Position; float2 Uv : TEXCOORD0; float4 Color : COLOR0; };
        V Vertex(uint id : SV_VertexID)
        {
            Quad q = Quads[First + id / 6];
            uint k = (0x312210u >> ((id % 6) * 4)) & 3;
            float2 t = float2(k & 1, k >> 1), p = lerp(q.Rect.xy, q.Rect.zw, t);
            V v;
            v.Pos = float4(p.x * Scale.x - 1, 1 - p.y * Scale.y, 0, 1);
            v.Uv = lerp(q.Uv.xy, q.Uv.zw, t);
            v.Color = q.Color;
            return v;
        }
        float4 Pixel(V v) : SV_Target
        {
            float4 a = Atlas.Sample(Nearest, v.Uv);
            return float4(a.rgb * v.Color.rgb, a.a) * v.Color.a;
        }
        """;
    private const int MaxQuads = 1024;
    private readonly GpuDevice gpu;
    private readonly GlyphAtlas atlas;
    private nint window, swapChain, target, dcomp, dcompTarget, visual, vertex, pixel, quads, quadsView, atlasTexture, atlasView,
        sampler, blend, replace, constants;
    private int width, height, left, top, fullClears;
    private bool visible;
    private readonly float[] quadData = new float[MaxQuads * 12];
    // Label rectangles of the last two presents: the two flip buffers still hold them, so only these are cleared.
    private List<(int X0, int Y0, int X1, int Y1)> drawn = [], drawnBefore = [];

    public GpuOverlay(GpuDevice gpu, GlyphAtlas atlas, bool excludeFromCapture = true)
    {
        this.gpu = gpu; this.atlas = atlas;
        try {
            window = CreateOverlayWindow();
            if (excludeFromCapture) SetWindowDisplayAffinity(window, 0x11);
            nint device = gpu.Device;
            vertex = D3D.Shader(device, 12, GpuShaders.Compile(Source, "Vertex", "vs_5_0"));
            pixel = D3D.Shader(device, 15, GpuShaders.Compile(Source, "Pixel", "ps_5_0"));
            quads = D3D.Buffer(device, new() { ByteWidth = MaxQuads * 48, Usage = 2, BindFlags = 0x8, CpuAccessFlags = 0x10000, MiscFlags = 0x40, StructureByteStride = 48 });
            var view = new D3D.ViewDesc { Format = 0, Dimension = 1, A = 0, B = MaxQuads };
            quadsView = D3D.ShaderView(device, quads, &view);
            atlasTexture = gpu.Texture(atlas.Width, atlas.Height, 87, 0, 0x8, 0);
            fixed (byte* pixels = atlas.Bgra)
                ((delegate* unmanaged[Stdcall]<nint, nint, uint, nint, void*, uint, uint, void>)Slot(gpu.Context, 48))(
                    gpu.Context, atlasTexture, 0, 0, pixels, (uint)atlas.Width * 4, 0);
            atlasView = D3D.ShaderView(device, atlasTexture, null);
            var samplerDesc = stackalloc uint[13];
            samplerDesc[0] = 0; samplerDesc[1] = samplerDesc[2] = samplerDesc[3] = 3; samplerDesc[5] = 1; samplerDesc[6] = 1; ((float*)samplerDesc)[12] = float.MaxValue;
            nint created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Slot(device, 23))(device, samplerDesc, &created));
            sampler = created; created = 0;
            // Premultiplied "over": ONE, INV_SRC_ALPHA for colour and alpha.
            var blendDesc = stackalloc uint[66];
            blendDesc[2] = 1; blendDesc[3] = 2; blendDesc[4] = 6; blendDesc[5] = 1; blendDesc[6] = 2; blendDesc[7] = 6; blendDesc[8] = 1; blendDesc[9] = 0xF;
            Hr(((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Slot(device, 20))(device, blendDesc, &created));
            blend = created; created = 0;
            blendDesc[2] = 0; // no blending: writes transparent pixels over stale labels
            Hr(((delegate* unmanaged[Stdcall]<nint, uint*, nint*, int>)Slot(device, 20))(device, blendDesc, &created));
            replace = created;
            constants = D3D.Buffer(device, new() { ByteWidth = 16, BindFlags = 0x4 });
            CreateComposition();
        } catch { Dispose(); throw; }
    }

    private void CreateComposition()
    {
        nint dxgi = Query(gpu.Device, new("54ec77fa-1377-44e6-8c32-88fd5f44c84c")), adapter = 0, factory = 0;
        try {
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter));
            Guid factoryId = new("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
            Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(adapter, 6))(adapter, &factoryId, &factory));
            // B8G8R8A8, render target, 2 buffers, stretch, flip-sequential, premultiplied alpha.
            var desc = stackalloc uint[12] { 16, 16, 87, 0, 1, 0, 0x20, 2, 0, 3, 1, 0 };
            nint chain = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint*, nint, nint*, int>)Slot(factory, 24))(factory, gpu.Device, desc, 0, &chain));
            swapChain = chain; width = height = 16;
            Guid deviceId = new("c37ea93a-e7aa-450d-b16f-9746cb0407f3");
            nint created = 0;
            Hr(DCompositionCreateDevice(dxgi, &deviceId, &created));
            dcomp = created; created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, int, nint*, int>)Slot(dcomp, 6))(dcomp, window, 1, &created));
            dcompTarget = created; created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dcomp, 7))(dcomp, &created));
            visual = created;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(visual, 15))(visual, swapChain));
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(dcompTarget, 3))(dcompTarget, visual));
            Hr(((delegate* unmanaged[Stdcall]<nint, int>)Slot(dcomp, 3))(dcomp));
        } finally { Release(factory); Release(adapter); Release(dxgi); }
    }

    /// <summary>Covers the game client area (screen pixels); resizes the swap chain only when the size changes.</summary>
    public void Place(int x, int y, int w, int h)
    {
        bool resized = w != width || h != height;
        if (resized) {
            Release(target); target = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, uint, int, uint, int>)Slot(swapChain, 13))(swapChain, 0, (uint)w, (uint)h, 0, 0));
            width = w; height = h; fullClears = 2; drawn = []; drawnBefore = [];
        }
        if (resized || x != left || y != top || !visible) {
            if (!SetWindowPos(window, -1, x, y, w, h, 0x10 /* no activate */ | (visible ? 0u : 0x40 /* show */)))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            left = x; top = y; visible = true;
        }
    }

    /// <summary>Draws the labels (replacing the last frame) and presents; nothing is presented when nothing changes.</summary>
    public void Draw(IReadOnlyList<OverlayLabel> labels)
    {
        if (labels.Count == 0 && drawn.Count == 0 && drawnBefore.Count == 0 && fullClears == 0) return;
        // Clear quads first (blending off, transparent), then glyphs. The buffer is the full client area, but
        // per frame only a few label-sized rectangles are written and the rest of the 4K surface is untouched.
        int count = 0;
        foreach (var rect in drawn.Concat(drawnBefore)) Quad(rect.X0, rect.Y0, rect.X1, rect.Y1, default, 0, 0, 0, 0);
        int clears = count;
        var now = new List<(int, int, int, int)>(labels.Count);
        foreach (var label in labels) {
            int x = label.X - atlas.Measure(label.Text) / 2, y = label.Bottom - atlas.Height, x0 = x;
            float r = (label.Rgb >> 16 & 0xFF) / 255f, g = (label.Rgb >> 8 & 0xFF) / 255f, b = (label.Rgb & 0xFF) / 255f;
            foreach (char c in label.Text) {
                if (!atlas.Glyphs.TryGetValue(c, out var glyph)) continue;
                Quad(x, y, x + glyph.Width, y + glyph.Height, glyph, r, g, b, 1);
                x += glyph.Advance;
            }
            now.Add((x0, y, x + 2 * atlas.Pad, y + atlas.Height));
        }
        nint context = gpu.Context;
        if (target == 0) {
            nint buffer = 0, created = 0;
            Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
            Hr(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swapChain, 9))(swapChain, 0, &textureId, &buffer));
            try { Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, nint*, int>)Slot(gpu.Device, 9))(gpu.Device, buffer, 0, &created)); }
            finally { Release(buffer); }
            target = created;
        }
        if (count > 0) {
            var mapped = D3D.MapWrite(context, quads);
            fixed (float* source = quadData) Buffer.MemoryCopy(source, (void*)mapped.Data, MaxQuads * 48, count * 48);
            D3D.Unmap(context, quads);
        }
        var view = stackalloc float[4] { 2f / width, 2f / height, 0, 0 };
        nint rtv = target, srv = quadsView, tex = atlasView, smp = sampler, cb = constants;
        var viewport = stackalloc float[6] { 0, 0, width, height, 0, 1 };
        if (fullClears > 0) {
            // A new buffer's content is undefined: clear each of the two flip buffers once.
            var clear = stackalloc float[4];
            ((delegate* unmanaged[Stdcall]<nint, nint, float*, void>)Slot(context, 50))(context, rtv, clear);
            fullClears--;
        }
        if (count > 0) {
            ((delegate* unmanaged[Stdcall]<nint, nint, void>)Slot(context, 17))(context, 0);
            ((delegate* unmanaged[Stdcall]<nint, int, void>)Slot(context, 24))(context, 4);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(context, 11))(context, vertex, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 25))(context, 0, 1, &srv);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 7))(context, 0, 1, &cb);
            ((delegate* unmanaged[Stdcall]<nint, nint, nint*, uint, void>)Slot(context, 9))(context, pixel, null, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 8))(context, 1, 1, &tex);
            ((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, void>)Slot(context, 10))(context, 0, 1, &smp);
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(context, 33))(context, 1, &rtv, 0);
            ((delegate* unmanaged[Stdcall]<nint, uint, float*, void>)Slot(context, 44))(context, 1, viewport);
            if (clears > 0) {
                ((uint*)view)[2] = 0;
                D3D.Update(context, constants, view);
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Slot(context, 35))(context, replace, null, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, 13))(context, (uint)clears * 6, 0);
            }
            if (count > clears) {
                ((uint*)view)[2] = (uint)clears;
                D3D.Update(context, constants, view);
                ((delegate* unmanaged[Stdcall]<nint, nint, float*, uint, void>)Slot(context, 35))(context, blend, null, 0xFFFFFFFF);
                ((delegate* unmanaged[Stdcall]<nint, uint, uint, void>)Slot(context, 13))(context, (uint)(count - clears) * 6, 0);
            }
            rtv = 0;
            ((delegate* unmanaged[Stdcall]<nint, uint, nint*, nint, void>)Slot(context, 33))(context, 1, &rtv, 0);
        }
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, int>)Slot(swapChain, 8))(swapChain, 0, 0));
        drawnBefore = drawn; drawn = now;

        void Quad(int x0, int y0, int x1, int y1, GlyphAtlas.Glyph glyph, float r, float g, float b, float a)
        {
            if (count >= MaxQuads) return;
            int o = count++ * 12;
            quadData[o] = x0; quadData[o + 1] = y0; quadData[o + 2] = x1; quadData[o + 3] = y1;
            quadData[o + 4] = glyph.X / (float)atlas.Width; quadData[o + 5] = glyph.Y / (float)atlas.Height;
            quadData[o + 6] = (glyph.X + glyph.Width) / (float)atlas.Width; quadData[o + 7] = (glyph.Y + glyph.Height) / (float)atlas.Height;
            quadData[o + 8] = r; quadData[o + 9] = g; quadData[o + 10] = b; quadData[o + 11] = a;
        }
    }

    /// <summary>Copy of the current back buffer (tests): what the next Present would show.</summary>
    public byte[] ReadBack()
    {
        nint buffer = 0;
        Guid textureId = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, Guid*, nint*, int>)Slot(swapChain, 9))(swapChain, 0, &textureId, &buffer));
        nint staging = gpu.Texture(width, height, 87, 3, 0, 0x20000);
        try {
            D3D.Copy(gpu.Context, staging, buffer);
            using var timer = new PreciseTimer();
            var mapped = D3D.MapRead(gpu.Context, staging, timer);
            var pixels = new byte[width * height * 4];
            try {
                fixed (byte* destination = pixels)
                    for (int y = 0; y < height; y++)
                        Buffer.MemoryCopy((byte*)mapped.Data + (long)y * mapped.RowPitch, destination + y * width * 4, width * 4, width * 4);
            } finally { D3D.Unmap(gpu.Context, staging); }
            return pixels;
        } finally { Release(staging); Release(buffer); }
    }

    public void Hide()
    {
        if (!visible) return;
        ShowWindow(window, 0); visible = false;
    }

    /// <summary>Handles queued window messages; call regularly from the owning thread.</summary>
    public void Pump()
    {
        var message = stackalloc byte[64];
        while (PeekMessageW(message, 0, 0, 0, 1)) { TranslateMessage(message); DispatchMessageW(message); }
    }

    public void Dispose()
    {
        Release(target); Release(visual); Release(dcompTarget); Release(dcomp); Release(swapChain);
        Release(constants); Release(replace); Release(blend); Release(sampler); Release(atlasView); Release(atlasTexture);
        Release(quadsView); Release(quads); Release(pixel); Release(vertex);
        target = visual = dcompTarget = dcomp = swapChain = constants = replace = blend = sampler = atlasView = atlasTexture = quadsView = quads = pixel = vertex = 0;
        if (window != 0) DestroyWindow(window);
        window = 0;
    }

    private static ushort windowClass;
    private static nint CreateOverlayWindow()
    {
        nint instance = GetModuleHandleW(null);
        if (windowClass == 0) {
            fixed (char* name = "DotaCoachGpuOverlay") {
                var cls = stackalloc nint[10];
                ((uint*)cls)[0] = 80;
                cls[1] = GetProcAddress(GetModuleHandleW("user32.dll"), "DefWindowProcW");
                cls[3] = instance; cls[8] = (nint)name;
                windowClass = RegisterClassExW(cls);
                if (windowClass == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
            }
        }
        // Topmost, click-through (transparent + layered), tool window, never activated, DirectComposition only.
        nint hwnd = CreateWindowExW(0x8 | 0x20 | 0x80 | 0x80000 | 0x08000000 | 0x00200000, windowClass, "Dota Coach creep HP", 0x80000000,
            0, 0, 16, 16, 0, 0, instance, 0);
        if (hwnd == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
        SetLayeredWindowAttributes(hwnd, 0, 255, 2);
        return hwnd;
    }

    [DllImport("dcomp.dll")] private static extern int DCompositionCreateDevice(nint dxgiDevice, Guid* iid, nint* device);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern nint GetModuleHandleW(string? name);
    [DllImport("kernel32.dll", CharSet = CharSet.Ansi)] private static extern nint GetProcAddress(nint module, string name);
    [DllImport("user32.dll", SetLastError = true)] private static extern ushort RegisterClassExW(nint* cls);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)] private static extern nint CreateWindowExW(uint exStyle, nint cls, string title,
        uint style, int x, int y, int w, int h, nint parent, nint menu, nint instance, nint param);
    [DllImport("user32.dll")] private static extern bool SetLayeredWindowAttributes(nint hwnd, uint key, byte alpha, uint flags);
    [DllImport("user32.dll")] private static extern bool SetWindowDisplayAffinity(nint hwnd, uint affinity);
    [DllImport("user32.dll", SetLastError = true)] private static extern bool SetWindowPos(nint hwnd, nint after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] private static extern bool ShowWindow(nint hwnd, int command);
    [DllImport("user32.dll")] private static extern bool DestroyWindow(nint hwnd);
    [DllImport("user32.dll")] private static extern bool PeekMessageW(byte* message, nint hwnd, uint min, uint max, uint remove);
    [DllImport("user32.dll")] private static extern bool TranslateMessage(byte* message);
    [DllImport("user32.dll")] private static extern nint DispatchMessageW(byte* message);
}
