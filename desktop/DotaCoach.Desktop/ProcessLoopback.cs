using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DotaCoach.Desktop;

/// <summary>Official Windows process-tree loopback, no injection, drivers, or recording files.</summary>
public sealed class ProcessLoopback : IDisposable
{
    private readonly CancellationTokenSource cancel = new();
    private Task? worker;
    public event Action<byte[]>? Audio;
    public event Action? Started;
    public event Action<string>? Stopped;

    public void Start(int pid, string startedAt)
    {
        if (worker != null) throw new InvalidOperationException("Capture already started.");
        worker = Task.Factory.StartNew(() => Run(pid, startedAt), CancellationToken.None,
            TaskCreationOptions.LongRunning, TaskScheduler.Default);
    }

    private void Run(int pid, string startedAt)
    {
        IAudioClient? client = null;
        IAudioCaptureClient? capture = null;
        bool com = false;
        string message = "监听已停止";
        try
        {
            Marshal.ThrowExceptionForHR(AudioInterop.CoInitializeEx(IntPtr.Zero, 0)); // MTA
            com = true;
            using var process = Process.GetProcessById(pid);
            if (process.StartTime.ToUniversalTime().Ticks.ToString() != startedAt)
                throw new InvalidOperationException("目标进程已重启，请刷新列表重新选择。");
            client = Activate(pid, cancel.Token);
            var format = new AudioInterop.WaveFormat {
                FormatTag = 1, Channels = 1, SamplesPerSecond = 24000,
                AverageBytesPerSecond = 48000, BlockAlign = 2, BitsPerSample = 16,
            };
            // LOOPBACK | EVENTCALLBACK | AUTOCONVERTPCM | SRC_DEFAULT_QUALITY.
            // Ask the Windows audio engine for the server's exact format; do not assume device mix rate.
            Marshal.ThrowExceptionForHR(client.Initialize(0, 0x00020000 | 0x00040000 | 0x80000000 | 0x08000000,
                0, 0, in format, IntPtr.Zero));
            Marshal.ThrowExceptionForHR(client.GetService(in AudioInterop.CaptureClientId, out var service));
            capture = (IAudioCaptureClient)service;
            using var samples = new EventWaitHandle(false, EventResetMode.AutoReset);
            Marshal.ThrowExceptionForHR(client.SetEventHandle(samples.SafeWaitHandle.DangerousGetHandle()));
            cancel.Token.ThrowIfCancellationRequested();
            Marshal.ThrowExceptionForHR(client.Start());
            Started?.Invoke();
            var packets = new AudioPacketizer(bytes => Audio?.Invoke(bytes));
            var lastData = Stopwatch.StartNew();
            var checkProcess = Stopwatch.StartNew();
            var waits = new[] { samples, cancel.Token.WaitHandle };
            while (!cancel.IsCancellationRequested)
            {
                if (WaitHandle.WaitAny(waits, 20) == 1) break;
                if (checkProcess.ElapsedMilliseconds >= 500)
                {
                    if (process.HasExited) { message = "目标进程已退出，请刷新列表后重新监听"; break; }
                    checkProcess.Restart();
                }
                Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out uint frames));
                while (frames > 0 && !cancel.IsCancellationRequested)
                {
                    Marshal.ThrowExceptionForHR(capture.GetBuffer(out var data, out frames, out var flags, out _, out _));
                    try
                    {
                        if (frames > 48000) throw new InvalidOperationException("音频采集积压，已停止，请重新开始。");
                        var bytes = new byte[checked((int)frames * 2)];
                        if ((flags & 2) == 0) Marshal.Copy(data, bytes, 0, bytes.Length); // AUDCLNT_BUFFERFLAGS_SILENT
                        packets.Push(bytes);
                        lastData.Restart();
                    }
                    finally { Marshal.ThrowExceptionForHR(capture.ReleaseBuffer(frames)); }
                    Marshal.ThrowExceptionForHR(capture.GetNextPacketSize(out frames));
                }
                // Some apps stop rendering altogether during silence. Preserve silence and let VAD finish a turn.
                if (lastData.ElapsedMilliseconds >= 60)
                {
                    packets.Push(new byte[AudioPacketizer.PacketBytes - packets.PendingBytes]);
                    lastData.Restart();
                }
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception error)
        {
            message = error is COMException
                ? $"Windows 进程采集失败 (0x{error.HResult:X8})。请检查系统更新和目标进程；不会回退为全系统采集。"
                : error.Message;
        }
        finally
        {
            if (client != null) { client.Stop(); }
            if (capture != null) Marshal.ReleaseComObject(capture);
            if (client != null) Marshal.ReleaseComObject(client);
            if (com) AudioInterop.CoUninitialize();
            Stopped?.Invoke(message);
        }
    }

    private static IAudioClient Activate(int pid, CancellationToken cancellation)
    {
        var completion = new ActivationCompletion();
        var parameters = new AudioInterop.ActivationParams {
            ActivationType = 1, TargetProcessId = (uint)pid, ProcessLoopbackMode = 0, // Include tree.
        };
        var memory = Marshal.AllocHGlobal(Marshal.SizeOf<AudioInterop.ActivationParams>());
        Marshal.StructureToPtr(parameters, memory, false);
        var variant = new AudioInterop.PropVariant {
            Type = 65, BlobSize = (uint)Marshal.SizeOf<AudioInterop.ActivationParams>(), BlobData = memory, // VT_BLOB
        };
        IActivateAudioInterfaceAsyncOperation? operation = null;
        try
        {
            Marshal.ThrowExceptionForHR(AudioInterop.ActivateAudioInterfaceAsync(
                @"VAD\Process_Loopback", in AudioInterop.AudioClientId, in variant, completion, out operation));
        }
        catch { Marshal.FreeHGlobal(memory); throw; }
        void ReleaseParameters()
        {
            Marshal.FreeHGlobal(memory);
            if (operation != null) Marshal.ReleaseComObject(operation);
            GC.KeepAlive(completion);
        }
        try
        {
            var client = completion.Result.Task.WaitAsync(TimeSpan.FromSeconds(12), cancellation).GetAwaiter().GetResult();
            ReleaseParameters();
            return client;
        }
        catch
        {
            // Cancellation does not cancel the OS activation. Retain its parameters/handler until it completes.
            _ = completion.Result.Task.ContinueWith(task => {
                if (task.Status == TaskStatus.RanToCompletion) Marshal.ReleaseComObject(task.Result);
                _ = task.Exception;
                ReleaseParameters();
            }, TaskScheduler.Default);
            throw;
        }
    }

    public async Task StopAsync()
    {
        cancel.Cancel();
        if (worker != null) await worker;
    }

    public void Dispose()
    {
        cancel.Cancel();
        if (worker == null || worker.IsCompleted) cancel.Dispose();
        else _ = worker.ContinueWith(_ => cancel.Dispose(), TaskScheduler.Default);
    }
}

[ComVisible(true), ClassInterface(ClassInterfaceType.None)]
public sealed class ActivationCompletion : IActivateAudioInterfaceCompletionHandler, IAgileObject
{
    internal TaskCompletionSource<IAudioClient> Result { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    public int ActivateCompleted(IActivateAudioInterfaceAsyncOperation operation)
    {
        object? audio = null;
        try
        {
            Marshal.ThrowExceptionForHR(operation.GetActivateResult(out int hr, out audio));
            Marshal.ThrowExceptionForHR(hr);
            Result.TrySetResult((IAudioClient)audio);
        }
        catch (Exception error)
        {
            if (audio != null && Marshal.IsComObject(audio)) Marshal.ReleaseComObject(audio);
            Result.TrySetException(error);
        }
        return 0;
    }
}
