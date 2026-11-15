using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading.Tasks;

namespace DotaCoach.Desktop;

/// <summary>Application lifetime and all audio ownership stay here. UI route changes are not lifecycle events.</summary>
internal sealed class DesktopSession : IDisposable
{
    private ServiceClient service;
    private readonly Action<Action> dispatch;
    private readonly Action<object> emit;
    private NativePlayback playback = new();
    private NativeVoiceChannel? voice;
    private NativeVoiceChannel? listener;
    private NativeMicrophone? microphone;
    private ProcessLoopback? capture;
    private JsonElement? user, room, config, roster;
    private string invitation = "", role = "student", micStatus = "未连接房间", listenStatus = "未开始监听", sourceName = "";
    private bool selfMonitor, disposed;
    private int sourcePid;
    private readonly ConversationStore conversation = new();
    public event Action<ConversationTurn>? Caption;
    public event Action? ClearOverlay;
    public event Action<int>? AccessLost;
    public string Server => service.Origin.AbsoluteUri;
    public string? RoomId => room is JsonElement r ? Text(r, "roomId") : null;
    public bool HasSession => user != null;
    public bool IsCapturing => microphone != null || capture != null;
    public string Version => typeof(DesktopSession).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion.Split('+')[0] ?? "unknown";
    public string Summary => $"麦克风：{micStatus}；外部监听：{listenStatus}";
    private static string Text(JsonElement e, string key) => NativeVoiceChannel.Text(e, key);
    private static bool Flag(JsonElement e, string key) => e.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.True;

    public DesktopSession(string address, Action<Action> dispatch, Action<object> emit)
    { service = new ServiceClient(address); this.dispatch = dispatch; this.emit = emit; }

    public object Snapshot() => new {
        version = Version, server = Server, user, room, config, roster, role, selfMonitor,
        microphone = new { ready = voice?.Ready == true, active = microphone != null, status = micStatus, frames = voice?.FramesSent ?? 0 },
        listener = new { ready = listener?.Ready == true, connecting = listener != null && !listener.Ready,
            active = capture != null, status = listenStatus, sourceName, pid = sourcePid, frames = listener?.FramesSent ?? 0 },
        history = conversation.Snapshot(),
    };
    private void Changed() { if (!disposed) emit(new { type = "state", state = Snapshot() }); }
    private void Notify(string message) { if (!disposed) emit(new { type = "notice", message }); }

    public async Task<object?> Command(string method, JsonElement data)
    {
        switch (method)
        {
            case "bootstrap": return Snapshot();
            case "login":
                await Leave();
                user = null;
                var login = await service.Request("POST", "/api/auth/login", new { username = Text(data, "username"), password = Text(data, "password") });
                user = login.GetProperty("user");
                try { config = await service.Request("GET", "/api/config"); }
                finally { Changed(); }
                return Snapshot();
            case "logout":
                await Leave();
                try { await service.Request("POST", "/api/auth/logout", new { }); }
                finally { ResetCredentials(); }
                return Snapshot();
            case "rooms.list": return await service.Request("GET", "/api/rooms");
            case "users.list": return await service.Request("GET", "/api/users");
            case "room.create": return await service.Request("POST", "/api/rooms", data);
            case "room.join":
                string id = Text(data, "roomId"), invite = Text(data, "invite");
                string link = Text(data, "link");
                string requestedRole = Text(data, "role");
                if (link.Length > 0)
                {
                    var parsed = new Uri(link);
                    if (!DesktopPolicy.SameOrigin(service.Origin, link)) throw new InvalidOperationException("邀请链接不属于当前服务");
                    var values = System.Web.HttpUtility.ParseQueryString(parsed.Query);
                    id = values["room"] ?? "";
                    invite = System.Web.HttpUtility.ParseQueryString(parsed.Fragment.TrimStart('#'))["invite"] ?? values["invite"] ?? "";
                    requestedRole = values["role"] ?? requestedRole;
                }
                if (!Guid.TryParse(id, out _)) throw new InvalidOperationException("请输入有效的房间邀请链接或房间号");
                var detail = await service.Request("GET", "/api/rooms/" + id, invite: invite);
                await Leave();
                room = detail.GetProperty("room"); invitation = invite;
                role = requestedRole == "coach" ? "coach" : "student";
                Changed();
                await ConnectVoice();
                return Snapshot();
            case "room.leave": await Leave(); return Snapshot();
            case "room.reconnect": await ConnectVoice(); return Snapshot();
            case "room.preferences":
                string nextRole = Text(data, "role") == "coach" ? "coach" : "student";
                selfMonitor = Flag(data, "selfMonitor");
                if (role != nextRole) { role = nextRole; await ConnectVoice(); }
                else voice?.SendJson(new { type = "client.set_self_monitor", selfMonitor });
                Changed(); return Snapshot();
            case "room.save":
                RequireRoom();
                var settings = await service.Request("PATCH", "/api/rooms/" + RoomId + "/settings", data);
                room = settings.GetProperty("room");
                if (Flag(settings, "reconnected")) { StopListener("房间设置已更新，请手动重新开始"); await ConnectVoice(); }
                Changed(); return Snapshot();
            case "room.access":
                RequireRoom();
                var access = await service.Request("PATCH", "/api/rooms/" + RoomId + "/access", data);
                room = access.GetProperty("room"); Changed(); return Snapshot();
            case "room.heroes":
                RequireOwner();
                var heroes = data.GetProperty("heroes");
                if (heroes.ValueKind != JsonValueKind.Array || heroes.GetArrayLength() > 10) throw new InvalidOperationException("最多选择 10 位英雄");
                voice?.SendJson(new { type = "room.set_match_heroes", matchHeroes = heroes });
                return null;
            case "room.invite":
                RequireOwner();
                return Server + "?room=" + RoomId + "&role=coach#invite=" + Uri.EscapeDataString(Text(room!.Value, "inviteToken"));
            case "gsi.config": RequireOwner(); return await service.GsiConfig(RoomId!);
            case "text.send":
                var text = Text(data, "text").Trim();
                if (text.Length is 0 or > 4000) throw new InvalidOperationException("请输入 1–4000 字");
                if (voice?.Ready != true) throw new InvalidOperationException("请先连接房间");
                voice.SendJson(new { type = "text.send", text }); return null;
            case "mic.devices": return NativeMicrophone.Devices();
            case "mic.start":
                if (voice?.Ready != true) throw new InvalidOperationException("请等待房间语音就绪");
                if (microphone != null) return Snapshot();
                var input = new NativeMicrophone(Text(data, "deviceId"));
                var channel = voice;
                microphone = input;
                input.Audio += bytes => channel.PushAudio(bytes);
                input.Stopped += () => dispatch(() => { if (microphone == input) StopMic("麦克风已停止或断开，请重新开启"); });
                try { input.Start(); micStatus = "麦克风采集中"; }
                catch { StopMic("无法打开麦克风，请检查设备和系统权限"); throw; }
                Changed(); return Snapshot();
            case "mic.stop": StopMic("麦克风已关闭"); return Snapshot();
            case "process.list": return ProcessCatalog.List();
            case "listener.start":
                RequireRoom();
                int pid = data.GetProperty("pid").GetInt32();
                var process = ProcessCatalog.List().SingleOrDefault(p => p.pid == pid && p.startedAt == Text(data, "startedAt"));
                if (process == null) throw new InvalidOperationException("目标进程已变更，请刷新并重新选择");
                StopListener("正在连接监听服务…");
                var next = new NativeVoiceChannel(true);
                listener = next; sourceName = process.name; sourcePid = pid; listenStatus = "正在连接私有监听…"; Changed();
                Bind(next, true);
                try
                {
                    await next.Connect(service, RoomId!, "coach", process.name, invitation, false);
                    if (listener != next || disposed) return Snapshot();
                    var loopback = new ProcessLoopback();
                    capture = loopback;
                    loopback.Audio += bytes => next.PushAudio(bytes);
                    loopback.Started += () => dispatch(() => { if (capture == loopback) { listenStatus = "正在监听 · 仅自己可见"; Changed(); } });
                    loopback.Stopped += reason => dispatch(() => { if (capture == loopback) StopListener(reason); });
                    loopback.Start(pid, process.startedAt);
                }
                catch { if (listener == next) StopListener("监听连接失败，未开启采音。请检查网络或服务兼容性"); throw; }
                return Snapshot();
            case "listener.stop": StopListener("监听已停止"); return Snapshot();
            case "server.set":
                if (HasSession || RoomId != null || IsCapturing) throw new InvalidOperationException("请先退出登录再切换服务");
                var replacement = new ServiceClient(Text(data, "address"));
                service.Dispose(); service = replacement; Changed(); return Snapshot();
            case "diagnostics":
                return new { version = Version, serverHost = service.Origin.Host, os = Environment.OSVersion.VersionString,
                    voiceReady = voice?.Ready == true, listenerReady = listener?.Ready == true,
                    microphoneActive = microphone != null, listenerActive = capture != null,
                    microphoneFrames = voice?.FramesSent ?? 0, listenerFrames = listener?.FramesSent ?? 0,
                    at = DateTimeOffset.UtcNow }; // Deliberately no credentials, recordings, transcripts, PIDs, or room IDs.
            default: throw new InvalidOperationException("不支持的桌面操作");
        }
    }

    private async Task ConnectVoice()
    {
        RequireRoom();
        StopMic("正在连接房间…");
        var previous = voice; voice = null; previous?.Dispose(); playback.Dispose();
        playback = new NativePlayback();
        var next = new NativeVoiceChannel(false); voice = next; Bind(next, false); Changed();
        try
        {
            await next.Connect(service, RoomId!, role, "", invitation, selfMonitor);
            if (voice == next) { micStatus = "语音已就绪 · 麦克风关闭"; Changed(); }
        }
        catch { if (voice == next) { voice = null; next.Dispose(); micStatus = "房间连接失败，请重连"; Changed(); } throw; }
    }
    private void Bind(NativeVoiceChannel channel, bool external)
    {
        bool Current() => !disposed && (external ? listener == channel : voice == channel);
        channel.Level += level => dispatch(() => { if (Current()) emit(new { type = "level", source = external ? "listener" : "microphone", level }); });
        var channelPlayback = playback;
        if (!external) channel.AudioOutput += pcm => { if (!disposed) channelPlayback.Add(pcm); };
        channel.Message += message => dispatch(() => {
            if (!Current()) return;
            string type = Text(message, "type");
            if (type == "room.state") { roster = message; Changed(); }
            else if (type == "room.settings.changed")
            {
                StopMic("房间设置已变更，请重新连接"); StopListener("房间设置已变更，请重新开始");
                Notify("房间设置已更新。重新连接后，需手动开启采集。");
            }
            else if (type == "audio.input.status") emit(new { type = "receipt", source = external ? "listener" : "microphone", data = message });
            else if (type == "engine.error" && (external || Text(message, "speaker") == (user is JsonElement u ? Text(u, "username") : "")))
            {
                if (external) StopListener("监听服务发生错误，已暂停");
                Notify(Text(message, "message"));
            }
            else if (type == "room.error") Notify(Text(message, "message"));
            else if (type.StartsWith("subtitle."))
            {
                var turn = conversation.Accept(message);
                if (turn != null) { emit(new { type = "conversation", turn }); Caption?.Invoke(turn); }
                else emit(new { type = "partial", source = external ? "listener" : "microphone", text = Text(message, "text") });
            }
        });
        channel.Ended += (code, reason) => dispatch(() => {
            if (!Current()) return;
            if (external) StopListener(reason);
            else { StopMic(reason); voice = null; channel.Dispose(); playback.Dispose(); }
            if (code is 4001 or 4003)
            {
                _ = Leave();
                if (code == 4001) ResetCredentials();
                AccessLost?.Invoke(code);
            }
            Notify(reason); Changed();
        });
    }
    public void StopMic(string reason = "麦克风已关闭")
    {
        var previous = microphone; microphone = null; previous?.Dispose();
        if (previous != null && voice?.Ready == true)
        {
            try { voice.SendJson(new { type = "speech.end" }); } catch (InvalidOperationException) { }
        }
        micStatus = reason; Changed();
    }
    public void StopListener(string reason = "监听已停止")
    {
        var previous = capture; capture = null; previous?.Dispose();
        var previousChannel = listener; listener = null; previousChannel?.Dispose();
        listenStatus = reason; ClearOverlay?.Invoke(); Changed();
    }
    private void ResetCredentials()
    {
        var address = Server; service.Dispose(); service = new ServiceClient(address);
        user = config = null; Changed();
    }
    public async Task AuthenticationLost()
    {
        await Leave();
        ResetCredentials();
    }
    public Task Leave()
    {
        StopMic("未连接房间"); StopListener();
        var previous = voice; voice = null; previous?.Dispose(); playback.Dispose();
        room = roster = null; invitation = ""; conversation.Clear(); ClearOverlay?.Invoke(); Changed();
        return Task.CompletedTask;
    }
    private void RequireRoom() { if (room == null || user == null) throw new InvalidOperationException("请先登录并进入房间"); }
    private void RequireOwner() { RequireRoom(); if (!Flag(room!.Value, "isOwner")) throw new InvalidOperationException("只有房主可以执行此操作"); }
    public void RendererFailed() { StopMic("界面中断，已暂停采集"); StopListener("界面中断，已暂停采集"); }
    public void Dispose() { disposed = true; _ = Leave(); service.Dispose(); }
}
