using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace DotaCoach.Desktop;

/// <summary>Independent of WebView/Dispatcher. Bounded native PCM -> WSS, with explicit readiness.</summary>
public sealed class NativeVoiceChannel : IDisposable
{
    private readonly ClientWebSocket socket = new();
    private readonly CancellationTokenSource stop = new();
    private readonly Channel<(byte[] Bytes, WebSocketMessageType Type)> outgoing =
        Channel.CreateBounded<(byte[], WebSocketMessageType)>(new BoundedChannelOptions(16) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly TaskCompletionSource ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly bool listener;
    private bool privacyConfirmed;
    private int ended;
    private long lastAck;
    private long firstAudio;
    private long lastAudio;
    private long lastMeter;
    public bool Ready { get; private set; }
    public long FramesSent { get; private set; }
    public event Action<JsonElement>? Message;
    public event Action<byte[]>? AudioOutput;
    public event Action<double>? Level;
    public event Action<int, string>? Ended;

    public NativeVoiceChannel(bool listener) { this.listener = listener; }

    public static bool AcceptsPrivateListener(JsonElement message) =>
        message.TryGetProperty("protocolVersion", out var version) && version.ValueKind == JsonValueKind.Number && version.GetInt32() == 1 &&
        Text(message, "privacy") == "connection" && Text(message, "output") == "text" &&
        message.TryGetProperty("sampleRate", out var rate) && rate.GetInt32() == 24000 &&
        message.TryGetProperty("channels", out var channels) && channels.GetInt32() == 1;
    public static string Text(JsonElement e, string key) =>
        e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    public async Task Connect(ServiceClient service, string roomId, string role, string label, string invite, bool selfMonitor)
    {
        socket.Options.Cookies = service.Cookies;
        socket.Options.SetRequestHeader("Origin", service.Origin.GetLeftPart(UriPartial.Authority));
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(10);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stop.Token);
        timeout.CancelAfter(TimeSpan.FromSeconds(35));
        try
        {
            await socket.ConnectAsync(service.SocketUri(roomId, role, listener, label, invite, selfMonitor), timeout.Token);
            _ = Receive();
            _ = Send();
            _ = Watchdog();
            await ready.Task.WaitAsync(timeout.Token);
        }
        catch { Dispose(); throw; }
    }

    public bool PushAudio(byte[] pcm)
    {
        if (!Ready || stop.IsCancellationRequested) return false;
        if (pcm.Length == 0 || pcm.Length % 2 != 0 || pcm.Length > 28800) { Fail(1011, "无效音频格式，已停止"); return false; }
        long now = Environment.TickCount64;
        Interlocked.CompareExchange(ref firstAudio, now, 0);
        Interlocked.Exchange(ref lastAudio, now);
        if (now - Interlocked.Read(ref lastMeter) > 120)
        {
            Interlocked.Exchange(ref lastMeter, now);
            double peak = 0;
            for (int i = 0; i < pcm.Length; i += 2) peak = Math.Max(peak, Math.Abs((int)BitConverter.ToInt16(pcm, i)) / 32768.0);
            Level?.Invoke(peak);
        }
        if (outgoing.Writer.TryWrite((pcm, WebSocketMessageType.Binary))) return true;
        Fail(1011, "音频上传积压，已暂停。请检查网络后重新开始");
        return false;
    }

    public void SendJson(object message)
    {
        if (!Ready) throw new InvalidOperationException("语音通道尚未就绪");
        if (!outgoing.Writer.TryWrite((JsonSerializer.SerializeToUtf8Bytes(message), WebSocketMessageType.Text)))
            Fail(1011, "发送队列已满，请重新连接");
    }

    private async Task Send()
    {
        try
        {
            await foreach (var packet in outgoing.Reader.ReadAllAsync(stop.Token))
            {
                await socket.SendAsync(new ArraySegment<byte>(packet.Bytes), packet.Type, true, stop.Token);
                if (packet.Type == WebSocketMessageType.Binary) FramesSent++;
            }
        }
        catch (OperationCanceledException) { }
        catch { Fail(1011, "上传连接断开，采集已暂停，请重新开始"); }
    }

    private async Task Receive()
    {
        var buffer = new byte[16384];
        try
        {
            while (!stop.IsCancellationRequested)
            {
                using var bytes = new MemoryStream();
                WebSocketReceiveResult result;
                do
                {
                    result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), stop.Token);
                    if (result.MessageType == WebSocketMessageType.Close)
                    {
                        int code = (int?)result.CloseStatus ?? 1000;
                        Fail(code, code == 4000 ? "房间设置已更新，请重新连接；采集不会自动开启"
                            : code == 4001 ? "登录已失效，请重新登录"
                            : code == 4003 ? "房间授权已失效，采集已停止" : "连接已断开，请重新连接");
                        return;
                    }
                    bytes.Write(buffer, 0, result.Count);
                    if (bytes.Length > 256 * 1024) throw new InvalidDataException();
                } while (!result.EndOfMessage);
                var data = bytes.ToArray();
                if (result.MessageType == WebSocketMessageType.Binary)
                {
                    if (!listener && data.Length > 2 && (data[0] == 1 || data[0] == 2) && data.Length % 2 == 0)
                        AudioOutput?.Invoke(data[2..]);
                    continue;
                }
                var message = JsonSerializer.Deserialize<JsonElement>(data);
                if (message.ValueKind != JsonValueKind.Object) continue;
                var type = Text(message, "type");
                if (type == "listener.config" && listener)
                {
                    privacyConfirmed = AcceptsPrivateListener(message);
                    if (!privacyConfirmed) { Fail(1008, "服务端监听协议不兼容，已阻止采集"); return; }
                }
                if (type == "engine.state")
                {
                    if (Text(message, "state") == "ready")
                    {
                        if (listener && !privacyConfirmed) { Fail(1008, "服务端未确认私有监听，已阻止采集"); return; }
                        Ready = true;
                        ready.TrySetResult();
                    }
                    else if (Ready || Text(message, "state") == "failed")
                    {
                        Fail(1011, "语音引擎暂不可用，采集已暂停，请重新连接"); return;
                    }
                }
                if (type == "audio.input.status" && message.TryGetProperty("engineReady", out var upstream) && upstream.ValueKind == JsonValueKind.True)
                    Interlocked.Exchange(ref lastAck, Environment.TickCount64);
                // Listener socket may also receive ordinary room broadcasts. Do not duplicate them.
                if (listener && type.StartsWith("subtitle.") &&
                    (Text(message, "sourceKind") != "process" || !message.TryGetProperty("private", out var priv) || priv.ValueKind != JsonValueKind.True)) continue;
                if (listener && type is "room.state" or "turn.metrics") continue;
                if (listener && type == "engine.error" && Text(message, "sourceKind") == "microphone") continue;
                Message?.Invoke(message);
            }
        }
        catch (OperationCanceledException) { }
        catch { Fail(1011, "接收连接中断，采集已暂停，请重新连接"); }
    }

    private async Task Watchdog()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
            while (await timer.WaitForNextTickAsync(stop.Token))
            {
                long now = Environment.TickCount64;
                if (firstAudio != 0 && now - lastAudio < 5000 && now - Math.Max(lastAck, firstAudio) > 12000)
                { Fail(1011, "服务器未确认收到声音，已暂停采集"); return; }
            }
        }
        catch (OperationCanceledException) { }
    }
    private void Fail(int code, string message)
    {
        if (Interlocked.Exchange(ref ended, 1) != 0) return;
        Ready = false; stop.Cancel(); outgoing.Writer.TryComplete(); socket.Abort();
        ready.TrySetException(new InvalidOperationException(message));
        Ended?.Invoke(code, message);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref ended, 1) == 0)
        {
            Ready = false; stop.Cancel(); outgoing.Writer.TryComplete(); socket.Abort();
            ready.TrySetCanceled();
        }
        socket.Dispose();
    }
}
