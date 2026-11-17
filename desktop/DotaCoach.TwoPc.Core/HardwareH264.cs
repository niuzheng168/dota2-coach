using System.Runtime.InteropServices;
using static DotaCoach.TwoPc.NativeCom;

namespace DotaCoach.TwoPc;

/// <summary>
/// GPU H.264 encoder (NVENC / AMD VCN / Intel Quick Sync) through the vendor's asynchronous Media Foundation
/// transform. Encoding runs on the GPU's dedicated media engine instead of CPU cores. A pump thread answers
/// the transform's METransformNeedInput / METransformHaveOutput events; only unencoded pictures are ever dropped.
/// </summary>
public sealed unsafe class HardwareH264 : IVideoEncoder
{
    private nint activate, transform, events;
    private bool started;
    private readonly int width, height;
    private readonly long duration, waitMs;
    private readonly object gate = new(), mft = new();
    private readonly Queue<VideoSample> outputs = new();
    private (VideoPixels Pixels, long Time)? pending;
    private int requests;
    private volatile bool stopping;
    private Exception? failure;
    private readonly Thread? pump;
    public string Name { get; } = "GPU H.264";
    public bool Hardware => true;
    public long Dropped { get; private set; }

    private static readonly Guid Category = new("f79eac7d-e545-4387-bdee-d647d7bde42a"),
        FriendlyName = new("314ffbae-5b41-4c95-9c19-4e7d586face3"),
        Async = new("f81a699a-649a-497d-8c73-29f8fed6ad7a"), AsyncUnlock = new("e5666d6b-3422-4eb6-a421-da7db1f8e207"),
        RateControl = new("1c0608e9-370c-4710-8a58-cb6181c42423"), MeanBitrate = new("f7222374-2144-4815-b550-a37f8e12ee52"),
        GopSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");

    /// <summary>Friendly names of the GPU H.264 encoders Windows reports, best first.</summary>
    public static IReadOnlyList<string> Available()
    {
        var names = new List<string>();
        if (MediaFoundation.Startup() < 0) return names;
        try {
            foreach (nint candidate in Enumerate())
                try { names.Add(MediaFoundation.GetString(candidate, FriendlyName) ?? "GPU H.264"); }
                finally { Release(candidate); }
        } catch { /* No hardware MFT registration is a normal "software only" answer. */ }
        finally { MediaFoundation.Shutdown(); }
        return names;
    }
    /// <summary>First GPU encoder that accepts this format; throws when none does.</summary>
    public static HardwareH264 Create(int width, int height, int fps, int bitrate)
    {
        VideoPixels.ValidateSize(width, height);
        Hr(MediaFoundation.Startup());
        Exception? last = null;
        nint[] candidates;
        try { candidates = Enumerate(); } catch { MediaFoundation.Shutdown(); throw; }
        try {
            for (int i = 0; i < candidates.Length; i++) {
                try {
                    var encoder = new HardwareH264(candidates[i], width, height, fps, bitrate);
                    candidates[i] = 0; // Owned by the encoder now.
                    return encoder;
                } catch (Exception e) { last = e; }
            }
        } finally {
            foreach (nint c in candidates) Release(c);
            MediaFoundation.Shutdown();
        }
        throw new InvalidOperationException("No GPU H.264 encoder accepted this format", last);
    }
    private static nint[] Enumerate()
    {
        var input = new TypeInfo(MediaFoundation.Video, MediaFoundation.Nv12);
        var output = new TypeInfo(MediaFoundation.Video, MediaFoundation.H264);
        nint array = 0; uint count = 0;
        // MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER: merit order, the way Windows itself picks.
        Hr(MFTEnumEx(Category, 0x04 | 0x40, &input, &output, &array, &count));
        try {
            var result = new nint[count];
            for (int i = 0; i < count; i++) result[i] = ((nint*)array)[i];
            return result;
        } finally { if (array != 0) Marshal.FreeCoTaskMem(array); }
    }
    private HardwareH264(nint candidate, int width, int height, int fps, int bitrate)
    {
        this.width = width; this.height = height;
        duration = MediaFoundation.Duration(fps);
        waitMs = Math.Clamp(2000 / fps, 8, 50);
        Hr(MediaFoundation.Startup()); started = true;
        activate = candidate;
        try {
            Name = MediaFoundation.GetString(activate, FriendlyName) ?? "GPU H.264";
            Guid iid = MediaFoundation.TransformIid; nint created = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, Guid*, nint*, int>)Slot(activate, 33))(activate, &iid, &created));
            transform = created;
            nint attributes = 0;
            Hr(((delegate* unmanaged[Stdcall]<nint, nint*, int>)Slot(transform, 8))(transform, &attributes));
            try {
                if (MediaFoundation.Get32(attributes, Async, 0) == 0) throw new InvalidOperationException("Not an asynchronous hardware MFT");
                MediaFoundation.Set32(attributes, AsyncUnlock, 1);
                MediaFoundation.Set32(attributes, MediaFoundation.LowLatency, 1);
            } finally { Release(attributes); }
            Configure(fps, bitrate);
            SetTypes(fps, bitrate);
            events = Query(transform, new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d"));
            Message(0x10000000); Message(0x10000003); // BEGIN_STREAMING, START_OF_STREAM → first NeedInput
            pump = new Thread(Pump) { IsBackground = true, Name = "GPU H.264 events" };
            pump.Start();
        } catch {
            // The caller still owns (and releases) the activation object; only undo the activation here.
            nint owned = activate; activate = 0;
            Dispose();
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(owned, 34))(owned);
            throw;
        }
    }
    /// <summary>ICodecAPI: low-latency CBR and a 2-second IDR interval. Best effort; vendors differ.</summary>
    private void Configure(int fps, int bitrate)
    {
        nint codec = TryQuery(transform, new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da"));
        if (codec == 0) return;
        try {
            void Set(Guid api, ushort type, long value)
            {
                var variant = new Variant { Type = type, Value = value };
                _ = ((delegate* unmanaged[Stdcall]<nint, Guid*, Variant*, int>)Slot(codec, 9))(codec, &api, &variant);
            }
            Set(MediaFoundation.LowLatency, 11 /* VT_BOOL */, -1);
            Set(RateControl, 19 /* VT_UI4 */, 0 /* CBR */);
            Set(MeanBitrate, 19, bitrate);
            Set(GopSize, 19, fps * 2);
        } finally { Release(codec); }
    }
    private void SetTypes(int fps, int bitrate)
    {
        Exception? last = null;
        foreach (uint profile in new uint[] { 66, 77, 100 }) { // baseline, main, high
            nint raw = 0, compressed = 0;
            try {
                compressed = MediaFoundation.MediaType(MediaFoundation.H264, width, height, fps);
                MediaFoundation.Set32(compressed, MediaFoundation.AverageBitrate, (uint)bitrate);
                MediaFoundation.Set32(compressed, MediaFoundation.Profile, profile);
                Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 16))(transform, 0, compressed, 0));
                raw = MediaFoundation.MediaType(MediaFoundation.Nv12, width, height, fps);
                Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 15))(transform, 0, raw, 0));
                return;
            } catch (Exception e) { last = e; }
            finally { Release(raw); Release(compressed); }
        }
        throw new InvalidOperationException("GPU encoder rejected NV12→H.264 at this size/rate", last);
    }
    private void Message(uint message) =>
        Hr(((delegate* unmanaged[Stdcall]<nint, uint, nuint, int>)Slot(transform, 23))(transform, message, 0));
    public IReadOnlyList<VideoSample> Encode(VideoPixels pixels, long time)
    {
        if (pixels.Width != width || pixels.Height != height) throw new ArgumentException("Encoder format changed");
        bool submit = false;
        lock (gate) {
            if (failure != null) throw new InvalidOperationException("GPU encoder failed", failure);
            ObjectDisposedException.ThrowIf(stopping, this);
            if (requests > 0) { requests--; submit = true; }
            else {
                if (pending != null) Dropped++; // still unencoded: the newest picture replaces it
                pending = (pixels, time);
            }
        }
        if (submit) Submit(pixels, time);
        var ready = new List<VideoSample>();
        lock (gate) {
            // Low latency: give the media engine a moment to return THIS picture before handing back control.
            long deadline = Environment.TickCount64 + waitMs;
            while (failure == null && !outputs.Any(o => o.Time == time) && Environment.TickCount64 < deadline)
                Monitor.Wait(gate, (int)Math.Max(1, deadline - Environment.TickCount64));
            if (failure != null) throw new InvalidOperationException("GPU encoder failed", failure);
            while (outputs.Count > 0) ready.Add(outputs.Dequeue());
        }
        return ready;
    }
    private void Submit(VideoPixels pixels, long time)
    {
        nint sample = MediaFoundation.Sample(pixels.Nv12, time, duration);
        try {
            lock (mft) Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint, uint, int>)Slot(transform, 24))(transform, 0, sample, 0));
        } finally { Release(sample); }
    }
    private void Pump()
    {
        try {
            while (!stopping) {
                nint evt = 0; uint type = 0; int status = 0;
                Hr(((delegate* unmanaged[Stdcall]<nint, uint, nint*, int>)Slot(events, 3))(events, 0, &evt));
                try {
                    Hr(((delegate* unmanaged[Stdcall]<nint, uint*, int>)Slot(evt, 33))(evt, &type));
                    Hr(((delegate* unmanaged[Stdcall]<nint, int*, int>)Slot(evt, 35))(evt, &status));
                } finally { Release(evt); }
                if (stopping) break;
                Hr(status);
                if (type == 601) { // METransformNeedInput
                    (VideoPixels Pixels, long Time)? next;
                    lock (gate) {
                        next = pending; pending = null;
                        if (next == null) requests++;
                    }
                    if (next is { } work) Submit(work.Pixels, work.Time);
                } else if (type == 602) { // METransformHaveOutput
                    VideoSample? sample; int hr;
                    lock (mft) hr = MediaFoundation.ProcessOutput(transform, out sample);
                    Hr(hr);
                    lock (gate) { outputs.Enqueue(sample!); Monitor.PulseAll(gate); }
                }
            }
        } catch (Exception e) when (!stopping) {
            lock (gate) { failure = e; Monitor.PulseAll(gate); }
        } catch { /* Shutdown unblocks GetEvent with MF_E_SHUTDOWN. */ }
    }
    public void Dispose()
    {
        lock (gate) { stopping = true; Monitor.PulseAll(gate); }
        if (events != 0) {
            // Wake a blocked GetEvent, then shut the transform down (MF_E_SHUTDOWN for any further call).
            Guid none = Guid.Empty;
            _ = ((delegate* unmanaged[Stdcall]<nint, uint, Guid*, int, nint, int>)Slot(events, 6))(events, 0, &none, 0, 0);
        }
        nint shutdown = transform == 0 ? 0 : TryQuery(transform, new("97ec2ea4-0e42-4937-97ac-9d6d328824e1"));
        if (shutdown != 0) { _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(shutdown, 3))(shutdown); Release(shutdown); }
        if (pump != null && pump.IsAlive && Thread.CurrentThread != pump) pump.Join(2000);
        lock (mft) {
            Release(events); events = 0;
            Release(transform); transform = 0;
        }
        if (activate != 0) {
            _ = ((delegate* unmanaged[Stdcall]<nint, int>)Slot(activate, 34))(activate); // ShutdownObject
            Release(activate); activate = 0;
        }
        if (started) { MediaFoundation.Shutdown(); started = false; }
    }
    [StructLayout(LayoutKind.Sequential)] private record struct TypeInfo(Guid Major, Guid Subtype);
    [StructLayout(LayoutKind.Explicit, Size = 24)] private struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public long Value;
    }
    [DllImport("mfplat.dll")] private static extern int MFTEnumEx(Guid category, uint flags, TypeInfo* input, TypeInfo* output, nint* activates, uint* count);
}
