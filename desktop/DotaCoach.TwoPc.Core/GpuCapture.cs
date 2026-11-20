using System.ComponentModel;
using System.Runtime.InteropServices;
using static DotaCoach.TwoPc.NativeCom;

namespace DotaCoach.TwoPc;

[StructLayout(LayoutKind.Sequential)] public struct Rect32 { public int Left, Top, Right, Bottom; }
[StructLayout(LayoutKind.Sequential)] internal struct SizeInt32 { public int Width, Height; }

/// <summary>
/// One D3D11 device for Windows.Graphics.Capture and the fixed-function video processor.
/// The immediate context is used only by the thread that owns this object.
/// </summary>
public sealed unsafe class GpuDevice : IDisposable
{
    internal nint Device, Context, VideoDevice, VideoContext, WinRtDevice;
    public GpuDevice()
    {
        nint device = 0, context = 0; int level = 0;
        // BGRA_SUPPORT for capture surfaces, VIDEO_SUPPORT for the GPU scaler/colour converter.
        Hr(D3D11CreateDevice(0, 1, 0, 0x20 | 0x800, null, 0, 7, &device, &level, &context));
        Device = device; Context = context;
        try {
            VideoDevice = Query(device, new("10ec4d5b-975a-4689-b9e4-d0aac30fe333"));
            VideoContext = Query(context, new("61f21c45-3c0e-4a74-9cea-67100d9ad5e4"));
            // The free-threaded capture pool may touch the device from its own threads.
            nint multithread = TryQuery(device, new("9b7e4e00-342c-4106-a19f-4f2704f689f0"));
            if (multithread != 0) { ((delegate* unmanaged[Stdcall]<nint, int, int>)Slot(multithread, 5))(multithread, 1); Release(multithread); }
            nint dxgi = Query(device, new("54ec77fa-1377-44e6-8c32-88fd5f44c84c")), inspectable = 0;
            try { Hr(CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable)); }
            finally { Release(dxgi); }
            try { WinRtDevice = Query(inspectable, new("a37624ab-8d5f-4650-9d3e-9eae3d9bc670")); }
            finally { Release(inspectable); }
        } catch { Dispose(); throw; }
    }
    /// <summary>
    /// Schedules this whole process's GPU work at the realtime class (granted without admin rights). Measured with
    /// Dota uncapped on an RTX 3090 Ti: at the normal, high or thread-boosted classes a 0.1 ms scan still waited
    /// ~35 ms behind the foreground game every time; at realtime it returns in ~1 ms.
    /// </summary>
    public bool RaiseGpuPriority() => D3DKMTSetProcessSchedulingPriorityClass(GetCurrentProcess(), 5 /* REALTIME */) >= 0;
    /// <summary>The adapter's LUID as the "GPU Engine" performance counters name it (luid_0x00000000_0x0000D1A3).</summary>
    public string AdapterLuid()
    {
        nint dxgi = Query(Device, new("54ec77fa-1377-44e6-8c32-88fd5f44c84c")), adapter = 0;
        try {
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(dxgi, 7))(dxgi, &adapter));
            var desc = stackalloc byte[304]; // DXGI_ADAPTER_DESC; AdapterLuid (low, high) at 296
            Hr(((delegate* unmanaged[Stdcall]<nint, byte*, int>)Slot(adapter, 8))(adapter, desc));
            return $"luid_0x{*(uint*)(desc + 300):X8}_0x{*(uint*)(desc + 296):X8}";
        } finally { Release(adapter); Release(dxgi); }
    }
    [DllImport("gdi32.dll")] private static extern int D3DKMTSetProcessSchedulingPriorityClass(nint process, int priority);
    [DllImport("kernel32.dll")] private static extern nint GetCurrentProcess();
    internal nint Texture(int width, int height, int format, int usage, uint bind, uint cpu)
    {
        var desc = new TextureDesc { Width = (uint)width, Height = (uint)height, MipLevels = 1, ArraySize = 1,
            Format = format, SampleCount = 1, Usage = usage, BindFlags = bind, CpuAccessFlags = cpu };
        nint texture = 0;
        Hr(((delegate* unmanaged[Stdcall]<nint, TextureDesc*, nint, nint*, int>)Slot(Device, 5))(Device, &desc, 0, &texture));
        return texture;
    }
    public void Dispose()
    {
        Release(WinRtDevice); Release(VideoContext); Release(VideoDevice); Release(Context); Release(Device);
        WinRtDevice = VideoContext = VideoDevice = Context = Device = 0;
    }
    [StructLayout(LayoutKind.Sequential)] private struct TextureDesc
    {
        public uint Width, Height, MipLevels, ArraySize; public int Format; public uint SampleCount, SampleQuality;
        public int Usage; public uint BindFlags, CpuAccessFlags, MiscFlags;
    }
    [DllImport("d3d11.dll")] private static extern int D3D11CreateDevice(nint adapter, int driverType, nint software, uint flags,
        int* levels, uint levelCount, uint sdkVersion, nint* device, int* level, nint* context);
    [DllImport("d3d11.dll")] private static extern int CreateDirect3D11DeviceFromDXGIDevice(nint dxgiDevice, nint* graphicsDevice);
}

/// <summary>
/// Windows.Graphics.Capture of ONE window. It keeps delivering that window's own pixels while other
/// windows are in front of it; other applications are never part of the image. Minimised windows produce no frames.
/// </summary>
public sealed unsafe class WindowCapture : IDisposable
{
    private nint item, pool, session;
    private long lastFrameTime = long.MinValue;
    /// <summary>Captured window size (DWM frame bounds) when the session started.</summary>
    public int Width { get; }
    public int Height { get; }
    public bool BorderDisabled { get; }
    /// <summary>True only if an unrestricted session successfully requested a high source cadence.</summary>
    public bool Unthrottled { get; }
    public WindowCapture(GpuDevice gpu, nint window, int maxFps = 0)
    {
        if (maxFps is < 0 or > 240) throw new ArgumentOutOfRangeException(nameof(maxFps));
        try {
            nint interop = Factory("Windows.Graphics.Capture.GraphicsCaptureItem", new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"));
            try {
                Guid iid = new("79c3f95b-31f7-4ec2-a464-632ef5d30760"); nint result = 0;
                Hr(((delegate* unmanaged[Stdcall]<nint, nint, Guid*, nint*, int>)Slot(interop, 3))(interop, window, &iid, &result));
                item = result;
            } finally { Release(interop); }
            SizeInt32 size;
            Hr(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Slot(item, 7))(item, &size));
            if (size.Width < 16 || size.Height < 16) throw new InvalidOperationException("Window has no capturable size");
            Width = size.Width; Height = size.Height;
            nint statics = Factory("Windows.Graphics.Capture.Direct3D11CaptureFramePool", new("589b103f-6bbc-5df5-a991-02e28b3b66d5"));
            try {
                nint result = 0;
                // B8G8R8A8UIntNormalized; three buffers so capture continues while one is being scaled.
                Hr(((delegate* unmanaged[Stdcall]<nint, nint, int, int, SizeInt32, nint*, int>)Slot(statics, 6))(
                    statics, gpu.WinRtDevice, 87, 3, size, &result));
                pool = result;
            } finally { Release(statics); }
            nint created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint*, int>)Slot(pool, 10))(pool, item, &created));
            session = created;
            // Best effort: no yellow capture border around the player's game (IGraphicsCaptureSession3).
            nint third = TryQuery(session, new("f2cdd966-22ae-5ea1-9596-3a289344c3be"));
            if (third != 0) {
                BorderDisabled = ((delegate* unmanaged[Stdcall]<nint, byte, int>)Slot(third, 7))(third, 0) >= 0;
                Release(third);
            }
            // Windows otherwise delivers at most one frame per 16 ms (measured: 48 fps on a 144 Hz display).
            // IGraphicsCaptureSession5.MinUpdateInterval (Windows 11 24H2+) lifts that to the display rate.
            nint fifth = TryQuery(session, new("67c0ea62-1f85-5061-925a-239be0ac09cb"));
            if (fifth != 0) {
                long interval = maxFps == 0 ? 10_000 : (TimeSpan.TicksPerSecond + maxFps - 1) / maxFps;
                Unthrottled = ((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(fifth, 7))(fifth, interval) >= 0 && maxFps == 0;
                Release(fifth);
            }
            Hr(((delegate* unmanaged[Stdcall]<nint, int>)Slot(session, 6))(session));
        } catch { Dispose(); throw; }
    }
    /// <summary>Newest frame since the last call; older queued frames are returned to the pool unused.</summary>
    public CaptureFrame? Latest() => Latest(out _);
    public CaptureFrame? Latest(out int freshFrames)
    {
        nint newest = 0; freshFrames = 0; long dequeuedAt = 0;
        try {
            for (int i = 0; i < 3; i++) { // bound draining to one pool; don't starve recognition
                nint frame = 0;
                Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(pool, 7))(pool, &frame));
                if (frame == 0) break;
                long local = System.Diagnostics.Stopwatch.GetElapsedTime(0).Ticks;
                long at;
                int hr = ((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(frame, 7))(frame, &at);
                if (hr < 0 || at <= lastFrameTime) { CaptureFrame.Close(frame); Hr(hr); continue; }
                lastFrameTime = at; dequeuedAt = local; freshFrames++;
                CaptureFrame.Close(newest); newest = frame;
            }
        } catch { CaptureFrame.Close(newest); throw; }
        return newest == 0 ? null : new CaptureFrame(newest, lastFrameTime, dequeuedAt);
    }
    public void Dispose()
    {
        CaptureFrame.Close(session); CaptureFrame.Close(pool); Release(item);
        session = pool = item = 0;
    }
}

public sealed unsafe class CaptureFrame : IDisposable
{
    private nint frame;
    internal nint Texture { get; private set; }
    public int ContentWidth { get; }
    public int ContentHeight { get; }
    /// <summary>WGC SystemRelativeTime (QPC, 100 ns units), not time of polling.</summary>
    public long SystemRelativeTime { get; }
    public long DequeuedAt { get; }
    internal CaptureFrame(nint frame, long at, long dequeuedAt)
    {
        this.frame = frame; SystemRelativeTime = at; DequeuedAt = dequeuedAt;
        try {
            SizeInt32 content;
            Hr(((delegate* unmanaged[Stdcall]<nint, SizeInt32*, int>)Slot(frame, 8))(frame, &content));
            ContentWidth = content.Width; ContentHeight = content.Height;
            nint surface = 0, access = 0, texture = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(frame, 6))(frame, &surface));
            try {
                access = Query(surface, new("a9b3d012-3df2-4ee3-b8d1-8695f457d3c1"));
                Guid iid = new("6f15aaf2-d208-4e89-9ab4-489535d34f9c"); // ID3D11Texture2D
                Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(access, 3))(access, &iid, &texture));
                Texture = texture;
            } finally { Release(access); Release(surface); }
        } catch { Dispose(); throw; }
    }
    /// <summary>IClosable.Close then Release: returns a frame/pool/session to Windows immediately.</summary>
    internal static void Close(nint winrt)
    {
        if (winrt == 0) return;
        nint closable = TryQuery(winrt, new("30d5a829-7fa4-4026-83bb-d75bae4ea99e"));
        if (closable != 0) { ((delegate* unmanaged[Stdcall]<nint, int>)Slot(closable, 6))(closable); Release(closable); }
        Release(winrt);
    }
    public void Dispose() { Release(Texture); Texture = 0; Close(frame); frame = 0; }
}

/// <summary>
/// GPU crop + scale + BGRA→NV12 (BT.601, 16-235, matching <see cref="VideoPixels"/>) with the D3D11
/// fixed-function video processor, then a small NV12 read-back for the encoder. No per-pixel CPU work.
/// </summary>
public sealed unsafe class GpuNv12Scaler : IDisposable
{
    private readonly GpuDevice gpu;
    private nint enumerator, processor, target, output, staging;
    // Practice only: at most two in-flight transfers. Delay releasing each WGC frame
    // until its GPU copy has completed. The streaming/encoding path stays synchronous.
    private nint secondStaging;
    private readonly CaptureFrame?[] owners = new CaptureFrame?[2];
    private readonly long[] stamps = new long[2], submittedAt = new long[2];
    private int head, pending;
    private readonly PreciseTimer timer = new();
    public int Width { get; }
    public int Height { get; }
    public GpuNv12Scaler(GpuDevice gpu, int inputWidth, int inputHeight, int width, int height, int fps)
    {
        VideoPixels.ValidateSize(width, height);
        this.gpu = gpu; Width = width; Height = height;
        try {
            var content = new ContentDesc { InputRate = new((uint)fps, 1), InputWidth = (uint)inputWidth, InputHeight = (uint)inputHeight,
                OutputRate = new((uint)fps, 1), OutputWidth = (uint)width, OutputHeight = (uint)height, Usage = 1 /* optimal speed */ };
            nint created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, ContentDesc*, nint*, int>)Slot(gpu.VideoDevice, 10))(gpu.VideoDevice, &content, &created));
            enumerator = created; created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, uint, nint*, int>)Slot(gpu.VideoDevice, 4))(gpu.VideoDevice, enumerator, 0, &created));
            processor = created; created = 0;
            target = gpu.Texture(width, height, 103 /* NV12 */, 0 /* default */, 0x20 /* render target */, 0);
            staging = gpu.Texture(width, height, 103, 3 /* staging */, 0, 0x20000 /* CPU read */);
            var view = new OutputViewDesc { ViewDimension = 1 };
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, OutputViewDesc*, nint*, int>)Slot(gpu.VideoDevice, 9))(
                gpu.VideoDevice, target, enumerator, &view, &created));
            output = created;
            nint vc = gpu.VideoContext;
            var whole = new Rect32 { Right = width, Bottom = height };
            ((delegate* unmanaged[Stdcall]<nint, nint, int, Rect32*, void>)Slot(vc, 13))(vc, processor, 1, &whole);
            uint outputSpace = 0x10; // YCbCr, BT.601 matrix, nominal 16-235
            ((delegate* unmanaged[Stdcall]<nint, nint, uint*, void>)Slot(vc, 15))(vc, processor, &outputSpace);
            uint inputSpace = 0; // RGB 0-255
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, uint*, void>)Slot(vc, 28))(vc, processor, 0, &inputSpace);
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, void>)Slot(vc, 27))(vc, processor, 0, 0); // progressive
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, void>)Slot(vc, 37))(vc, processor, 0, 0); // no driver "enhancement"
            ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, Rect32*, void>)Slot(vc, 31))(vc, processor, 0, 1, &whole);
        } catch { Dispose(); throw; }
    }
    private void Submit(nint texture, Rect32 source, nint destination)
    {
        nint vc = gpu.VideoContext, context = gpu.Context, input = 0;
        ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, Rect32*, void>)Slot(vc, 30))(vc, processor, 0, 1, &source);
        var inputDesc = new InputViewDesc { ViewDimension = 1 };
        Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, InputViewDesc*, nint*, int>)Slot(gpu.VideoDevice, 8))(
            gpu.VideoDevice, texture, enumerator, &inputDesc, &input));
        try {
            var stream = new StreamDesc { Enable = 1, InputSurface = input };
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, nint, uint, uint, StreamDesc*, int>)Slot(vc, 53))(vc, processor, output, 0, 1, &stream));
        } finally { Release(input); }
        ((delegate* unmanaged[Stdcall]<nint, nint, nint, void>)Slot(context, 47))(context, destination, target);
        ((delegate* unmanaged[Stdcall]<nint, void>)Slot(context, 111))(context);
    }

    private bool TryMap(nint buffer, out Mapped mapped)
    {
        Mapped m = default;
        int hr = ((delegate* unmanaged[Stdcall]<nint, nint, uint, int, uint, Mapped*, int>)Slot(gpu.Context, 14))(
            gpu.Context, buffer, 0, 1, 0x100000 /* DO_NOT_WAIT */, &m);
        mapped = m;
        if (hr == unchecked((int)0x887A000A)) return false;
        Hr(hr); return true;
    }

    private void Unmap(nint buffer) => ((delegate* unmanaged[Stdcall]<nint, nint, uint, void>)Slot(gpu.Context, 15))(gpu.Context, buffer, 0);

    public bool CreepQueueFull => pending == 2;
    private readonly LanePixelScanner laneScanner = new();
    public bool CreepReadPending => pending != 0;
    /// <summary>On success, transfers ownership of frame until the corresponding readback is retired.</summary>
    public bool TryQueueCreepFrame(CaptureFrame frame, Rect32 source) =>
        TryQueueCreepTexture(frame.Texture, source, frame.SystemRelativeTime, frame);

    internal bool TryQueueCreepTexture(nint texture, Rect32 source, long at, CaptureFrame? owner)
    {
        if (pending == 2) return false;
        int index = (head + pending) % 2;
        if (index == 1 && secondStaging == 0) secondStaging = gpu.Texture(Width, Height, 103, 3, 0, 0x20000);
        Submit(texture, source, index == 0 ? staging : secondStaging);
        stamps[index] = at; submittedAt[index] = Environment.TickCount64; owners[index] = owner; pending++;
        return true;
    }

    public bool TryReadCreepBars(List<CreepBar> destination, out long frameTime) => TryReadCreepBars(destination, out frameTime, out _);
    public bool TryReadCreepBars(List<CreepBar> destination, out long frameTime, out long dequeuedAt)
    {
        frameTime = dequeuedAt = 0;
        if (pending == 0) return false;
        nint buffer = head == 0 ? staging : secondStaging;
        if (!TryMap(buffer, out var m)) {
            if (Environment.TickCount64 - submittedAt[head] > 500) throw new TimeoutException("GPU scaler did not finish");
            return false;
        }
        try {
            int pitch = checked((int)m.RowPitch);
            laneScanner.Read(new ReadOnlySpan<byte>((void*)m.Data, checked(pitch * (Height + Height / 2))),
                Width, Height, pitch, stamps[head], destination);
            frameTime = stamps[head]; dequeuedAt = owners[head]?.DequeuedAt ?? 0; return true;
        } finally {
            Unmap(buffer); owners[head]?.Dispose(); owners[head] = null; head = 1 - head; pending--;
        }
    }

    /// <summary>Scales <paramref name="source"/> of a BGRA texture into a new packed NV12 picture.</summary>
    public VideoPixels Convert(CaptureFrame frame, Rect32 source)
    {
        Submit(frame.Texture, source, staging);
        Mapped mapped;
        long start = Environment.TickCount64;
        while (!TryMap(staging, out mapped)) {
            if (Environment.TickCount64 - start > 500) throw new TimeoutException("GPU scaler did not finish");
            timer.Wait(5_000);
        }
        try {
            var nv12 = GC.AllocateUninitializedArray<byte>(Width * Height * 3 / 2);
            byte* bytes = (byte*)mapped.Data;
            fixed (byte* destination = nv12) {
                for (int y = 0; y < Height; y++)
                    Buffer.MemoryCopy(bytes + (long)y * mapped.RowPitch, destination + (long)y * Width, Width, Width);
                byte* chroma = bytes + (long)mapped.RowPitch * Height;
                byte* chromaOut = destination + (long)Width * Height;
                for (int y = 0; y < Height / 2; y++)
                    Buffer.MemoryCopy(chroma + (long)y * mapped.RowPitch, chromaOut + (long)y * Width, Width, Width);
            }
            return VideoPixels.Wrap(Width, Height, nv12);
        } finally { Unmap(staging); }
    }
    public void Dispose()
    {
        for (int i = 0; i < owners.Length; i++) { owners[i]?.Dispose(); owners[i] = null; }
        Release(output); Release(secondStaging); Release(staging); Release(target); Release(processor); Release(enumerator);
        output = secondStaging = staging = target = processor = enumerator = 0;
        timer.Dispose();
    }
    [StructLayout(LayoutKind.Sequential)] private record struct Rational(uint Numerator, uint Denominator);
    [StructLayout(LayoutKind.Sequential)] private struct ContentDesc
    {
        public int InputFrameFormat; public Rational InputRate; public uint InputWidth, InputHeight;
        public Rational OutputRate; public uint OutputWidth, OutputHeight; public int Usage;
    }
    [StructLayout(LayoutKind.Sequential)] private struct InputViewDesc { public uint FourCC; public int ViewDimension; public uint MipSlice, ArraySlice; }
    [StructLayout(LayoutKind.Sequential)] private struct OutputViewDesc { public int ViewDimension; public uint MipSlice, FirstArraySlice, ArraySize; }
    [StructLayout(LayoutKind.Sequential)] private struct StreamDesc
    {
        public int Enable; public uint OutputIndex, InputFrameOrField, PastFrames, FutureFrames;
        public nint PastSurfaces, InputSurface, FutureSurfaces, PastSurfacesRight, InputSurfaceRight, FutureSurfacesRight;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Mapped { public nint Data; public uint RowPitch, DepthPitch; }
}

/// <summary>High-resolution waitable timer: frame pacing above 64 fps without spinning a core.</summary>
public sealed unsafe class PreciseTimer : IDisposable
{
    private nint handle;
    public PreciseTimer()
    {
        handle = CreateWaitableTimerExW(0, 0, 2 /* high resolution */, 0x1F0003);
        if (handle == 0) handle = CreateWaitableTimerExW(0, 0, 0, 0x1F0003);
        if (handle == 0) throw new Win32Exception(Marshal.GetLastWin32Error());
    }
    /// <summary>Waits about <paramref name="ticks"/> × 100 ns.</summary>
    public void Wait(long ticks)
    {
        if (ticks <= 0) return;
        long due = -ticks;
        if (!SetWaitableTimer(handle, &due, 0, 0, 0, 0)) throw new Win32Exception(Marshal.GetLastWin32Error());
        WaitForSingleObject(handle, uint.MaxValue);
    }
    public void Dispose() { if (handle != 0) CloseHandle(handle); handle = 0; }
    [DllImport("kernel32.dll", SetLastError = true)] private static extern nint CreateWaitableTimerExW(nint attributes, nint name, uint flags, uint access);
    [DllImport("kernel32.dll", SetLastError = true)] private static extern bool SetWaitableTimer(nint timer, long* due, int period, nint routine, nint argument, int resume);
    [DllImport("kernel32.dll")] private static extern uint WaitForSingleObject(nint handle, uint milliseconds);
    [DllImport("kernel32.dll")] private static extern bool CloseHandle(nint handle);
}
