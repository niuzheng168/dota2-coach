using System;
using System.Linq;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace DotaCoach.Desktop;

internal sealed class NativeMicrophone : IDisposable
{
    private readonly WasapiCapture capture;
    private readonly MMDevice device;
    public event Action<byte[]>? Audio;
    public event Action? Stopped;
    public static object[] Devices()
    {
        using var devices = new MMDeviceEnumerator();
        return devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)
            .Select(d => { using (d) return (object)new { id = d.ID, name = d.FriendlyName }; }).ToArray();
    }
    private sealed class ConvertedCapture(MMDevice device) : WasapiCapture(device, true, 60)
    {
        protected override AudioClientStreamFlags GetAudioClientStreamFlags() =>
            base.GetAudioClientStreamFlags() | AudioClientStreamFlags.AutoConvertPcm | AudioClientStreamFlags.SrcDefaultQuality;
    }
    public NativeMicrophone(string deviceId)
    {
        using var devices = new MMDeviceEnumerator();
        device = string.IsNullOrEmpty(deviceId) ? devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications) : devices.GetDevice(deviceId);
        capture = new ConvertedCapture(device) { WaveFormat = new WaveFormat(24000, 16, 1) };
        var packetizer = new AudioPacketizer(bytes => Audio?.Invoke(bytes));
        capture.DataAvailable += (_, e) => packetizer.Push(e.Buffer.AsSpan(0, e.BytesRecorded));
        capture.RecordingStopped += (_, _) => Stopped?.Invoke();
    }
    public void Start() => capture.StartRecording();
    public void Dispose() { capture.Dispose(); device.Dispose(); }
}

internal sealed class NativePlayback : IDisposable
{
    private readonly object gate = new();
    private BufferedWaveProvider? buffer;
    private WasapiOut? output;
    private bool disposed;
    public void Add(byte[] pcm)
    {
        lock (gate)
        {
            if (disposed) return; // Late packets from a closed channel must not restart playback.
            if (output == null)
            {
                buffer = new BufferedWaveProvider(new WaveFormat(24000, 16, 1)) {
                    BufferDuration = TimeSpan.FromSeconds(8), DiscardOnBufferOverflow = false,
                };
                output = new WasapiOut(AudioClientShareMode.Shared, 100);
                output.Init(buffer);
                output.Play();
            }
            if (buffer!.BufferedBytes + pcm.Length > buffer.BufferLength)
                throw new InvalidOperationException("译音播放积压，请重新连接");
            buffer.AddSamples(pcm, 0, pcm.Length);
        }
    }
    public void Dispose()
    {
        lock (gate) { disposed = true; output?.Dispose(); output = null; buffer = null; }
    }
}
