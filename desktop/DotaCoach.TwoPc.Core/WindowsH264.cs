using System.Runtime.InteropServices;

namespace DotaCoach.TwoPc;

public sealed record VideoSample(long Time, byte[] Data);

/// <summary>
/// Windows inbox synchronous H.264 MFT, baseline/no B frames, NV12 input/output.
/// Deliberately not advertised as NVENC/DXVA. No executable download or child process.
/// ABI slots/GUIDs follow Microsoft's mfobjects.h, mftransform.h, mfapi.h and codecapi.h.
/// </summary>
public sealed unsafe class WindowsH264 : IDisposable
{
    private nint transform;
    private bool started;
    private readonly bool encode;
    private readonly int width, height;
    private int stride, bufferHeight;
    public const int Fps = 30;
    public const int Bitrate = 6_000_000;
    public const long Duration = 10_000_000 / Fps;
    private const int NeedInput = unchecked((int)0xc00d6d72), StreamChange = unchecked((int)0xc00d6d61);
    private static readonly Guid Major = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"),
        Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        Video = new("73646976-0000-0010-8000-00aa00389b71"),
        H264 = new("34363248-0000-0010-8000-00aa00389b71"),
        Nv12 = new("3231564e-0000-0010-8000-00aa00389b71"),
        FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d"),
        FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        Aspect = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"),
        Interlace = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        LowLatency = new("9c27891a-ed7a-40e1-88e8-b22727a024ee"),
        Stride = new("644b4e48-1e02-4516-b0eb-c01ca9d49ac6");

    public WindowsH264(bool encoder, int width, int height)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Windows Media Foundation required");
        VideoPixels.ValidateSize(width, height);
        encode = encoder; this.width = width; this.height = height; stride = width; bufferHeight = height;
        try {
            Hr(MFStartup(0x20070, 0)); started = true;
            Guid clsid = new(encoder ? "6ca50344-051a-4ded-9779-a43305165e35" : "62ce7e72-4c71-4d20-b15d-452831a87d9d");
            Guid iid = new("bf94c121-5b05-4e6f-8000-ba598961414d");
            Hr(CoCreateInstance(ref clsid, 0, 1, ref iid, out transform));
            nint attributes = 0;
            try {
                Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(transform, 8))(transform, &attributes));
                Set32(attributes, LowLatency, 1);
                if (!encoder) {
                    // Bound decoder allocations even if a paired peer sends a hostile SPS.
                    Set32(attributes, new("5ae557b8-77af-41f5-9fa6-4db2fe1d4bca"), 1600);
                    Set32(attributes, new("7262a16a-d2dc-4e75-9ba8-65c0c6d32b13"), 912);
                }
            } finally { Release(attributes); }
            nint raw = 0, compressed = 0;
            try {
                raw = MediaType(Nv12); compressed = MediaType(H264);
                if (encoder) {
                    Set32(compressed, new("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Bitrate);
                    Set32(compressed, new("ad76a80b-2d5c-4e0b-b375-64e520137036"), 66); // baseline
                    SetType(16, compressed); SetType(15, raw);
                } else {
                    SetType(15, compressed); SelectDecoderType();
                }
            } finally { Release(raw); Release(compressed); }
            Message(0x10000000); Message(0x10000003); // BEGIN_STREAMING, START_OF_STREAM
        } catch { Dispose(); throw; }
    }
    public IReadOnlyList<VideoSample> Encode(VideoPixels pixels, long time)
    {
        if (!encode || pixels.Width != width || pixels.Height != height) throw new ArgumentException("Encoder format changed");
        return Process(pixels.Nv12, time);
    }
    public IReadOnlyList<(long Time, VideoPixels Pixels)> Decode(byte[] accessUnit, long time)
    {
        if (encode || accessUnit.Length is < 4 or > Wire.MaxFrameBytes) throw new ArgumentException("Invalid video packet");
        return Process(accessUnit, time).Select(s => (s.Time, Unpack(s.Data))).ToArray();
    }
    private VideoPixels Unpack(byte[] data)
    {
        int chroma = stride * bufferHeight;
        if (data.Length < chroma + stride * (height / 2)) throw new InvalidOperationException("Truncated decoded frame");
        var packed = new byte[width * height * 3 / 2];
        for (int y = 0; y < height; y++) Array.Copy(data, y * stride, packed, y * width, width);
        for (int y = 0; y < height / 2; y++) Array.Copy(data, chroma + y * stride, packed, width * height + y * width, width);
        return new(width, height, packed);
    }
    private List<VideoSample> Process(byte[] data, long time)
    {
        ObjectDisposedException.ThrowIf(transform == 0, this);
        nint sample = Sample(data.Length, data);
        try {
            Hr(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 36))(sample, time));
            Hr(((delegate* unmanaged[Stdcall]<nint, long, int>)Slot(sample, 38))(sample, Duration));
            Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, 0, sample, 0));
        } finally { Release(sample); }
        var results = new List<VideoSample>();
        for (int attempt = 0; attempt < 8; attempt++) {
            OutputInfo info = default;
            Hr(((delegate* unmanaged[Stdcall]<nint, uint, OutputInfo*, int>)Slot(transform, 7))(transform, 0, &info));
            if (info.Size > 1600 * 912 * 4) throw new InvalidOperationException("Unbounded codec buffer");
            nint allocated = (info.Flags & 0x100) != 0 ? 0 : Sample((int)Math.Max(info.Size, 4096), null);
            Output output = new() { Sample = allocated };
            uint status = 0;
            try {
                int hr = ((delegate* unmanaged[Stdcall]<nint, uint, uint, Output*, uint*, int>)Slot(transform, 25))
                    (transform, 0, 1, &output, &status);
                if (hr == NeedInput) return results;
                if (hr == StreamChange && !encode) { SelectDecoderType(); continue; }
                Hr(hr);
                if (output.Sample == 0) throw new InvalidOperationException("Missing output sample");
                long pts = 0;
                Hr(((delegate* unmanaged[Stdcall]<nint, long*, int>)Slot(output.Sample, 35))(output.Sample, &pts));
                results.Add(new(pts, SampleBytes(output.Sample)));
            } finally {
                Release(output.Events);
                if (output.Sample != allocated) Release(output.Sample);
                Release(allocated);
            }
        }
        throw new InvalidOperationException("Codec failed to drain promptly");
    }
    private void SelectDecoderType()
    {
        for (uint i = 0; i < 16; i++) {
            nint type = 0;
            try {
                Hr(((delegate* unmanaged[Stdcall]<nint, uint, uint, nint*, int>)Slot(transform, 14))(transform, 0, i, &type));
                Guid subtype = default, key = Subtype;
                Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(type, 10))(type, &key, &subtype));
                if (subtype != Nv12) continue;
                ulong size = 0; key = FrameSize;
                Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong*, int>)Slot(type, 8))(type, &key, &size));
                int w = (int)(size >> 32), h = (int)(size & uint.MaxValue);
                // Some decoder media types use macroblock-aligned height; crop only the declared input viewport.
                if (w != width || h < height || h > (height + 15) / 16 * 16)
                    throw new InvalidOperationException("Decoder changed declared dimensions");
                uint pitch = (uint)w; key = Stride;
                _ = ((delegate* unmanaged[Stdcall]<nint, Guid*, uint*, int>)Slot(type, 7))(type, &key, &pitch);
                if (pitch < width || pitch > 16384) throw new InvalidOperationException("Invalid decoded stride");
                stride = (int)pitch; bufferHeight = h;
                SetType(16, type); return;
            } finally { Release(type); }
        }
        throw new InvalidOperationException("NV12 output unavailable");
    }
    private nint MediaType(Guid subtype)
    {
        Hr(MFCreateMediaType(out nint type));
        try {
            SetGuid(type, Major, Video); SetGuid(type, Subtype, subtype);
            Set64(type, FrameSize, ((ulong)width << 32) | (uint)height);
            Set64(type, FrameRate, ((ulong)Fps << 32) | 1);
            Set64(type, Aspect, (1UL << 32) | 1); Set32(type, Interlace, 2);
            return type;
        } catch { Release(type); throw; }
    }
    private void SetType(int slot, nint type) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, slot))(transform, 0, type, 0));
    private void Message(uint message) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, message, 0));
    private static nint Sample(int capacity, byte[]? data)
    {
        Hr(MFCreateSample(out nint sample));
        nint buffer = 0;
        try {
            Hr(MFCreateMemoryBuffer((uint)capacity, out buffer));
            if (data != null) {
                nint pointer = 0; uint max = 0, current = 0;
                Hr(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &pointer, &max, &current));
                try { Marshal.Copy(data, 0, pointer, data.Length); }
                finally { Hr(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
                Hr(((delegate* unmanaged[Stdcall]<nint, uint, int>)Slot(buffer, 6))(buffer, (uint)data.Length));
            }
            Hr(((delegate* unmanaged[Stdcall]<nint, nint, int>)Slot(sample, 42))(sample, buffer));
            return sample;
        } catch { Release(sample); throw; }
        finally { Release(buffer); }
    }
    private static byte[] SampleBytes(nint sample)
    {
        nint buffer = 0;
        try {
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(sample, 41))(sample, &buffer));
            nint pointer = 0; uint max = 0, current = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, uint*, uint*, int>)Slot(buffer, 3))(buffer, &pointer, &max, &current));
            try {
                if (current > 1600 * 912 * 4 || current > max) throw new InvalidOperationException("Codec output too large");
                var data = new byte[current]; Marshal.Copy(pointer, data, 0, (int)current); return data;
            } finally { Hr(((delegate* unmanaged[Stdcall]<nint, int>)Slot(buffer, 4))(buffer)); }
        } finally { Release(buffer); }
    }
    private static void SetGuid(nint p, Guid key, Guid value) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, Guid*, int>)Slot(p, 24))(p, &key, &value));
    private static void Set32(nint p, Guid key, uint value) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, uint, int>)Slot(p, 21))(p, &key, value));
    private static void Set64(nint p, Guid key, ulong value) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, ulong, int>)Slot(p, 22))(p, &key, value));
    private static nint Slot(nint p, int index) => ((nint*)(*(nint*)p))[index];
    private static void Release(nint p) { if (p != 0) ((delegate* unmanaged[Stdcall]<nint, uint>)Slot(p, 2))(p); }
    private static void Hr(int hr) { if (hr < 0) Marshal.ThrowExceptionForHR(hr); }
    public void Dispose()
    {
        Release(transform); transform = 0;
        if (started) { MFShutdown(); started = false; }
    }
    [StructLayout(LayoutKind.Sequential)] private struct OutputInfo { public uint Flags, Size, Alignment; }
    [StructLayout(LayoutKind.Sequential)] private struct Output { public uint Stream; public nint Sample; public uint Status; public nint Events; }
    [DllImport("mfplat.dll")] private static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll")] private static extern int MFShutdown();
    [DllImport("mfplat.dll")] private static extern int MFCreateMediaType(out nint type);
    [DllImport("mfplat.dll")] private static extern int MFCreateSample(out nint sample);
    [DllImport("mfplat.dll")] private static extern int MFCreateMemoryBuffer(uint length, out nint buffer);
    [DllImport("ole32.dll")] private static extern int CoCreateInstance(ref Guid clsid, nint outer, uint context, ref Guid iid, out nint instance);
}
