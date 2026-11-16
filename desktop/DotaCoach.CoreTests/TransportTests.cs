using System.Text.Json;
using DotaCoach.Desktop;

internal static class TransportTests
{
    public static async Task Run(string url)
    {
        static async Task Until(Func<bool> check)
        {
            for (int i = 0; i < 150 && !check(); i++) await Task.Delay(20);
            if (!check()) throw new Exception("Native transport condition timed out");
        }
        using var owner = new ServiceClient(url);
        using var guest = new ServiceClient(url);
        var login = await owner.Request("POST", "/api/auth/login", new { username = "native-owner", password = "native-test-only" });
        if (login.TryGetProperty("csrfToken", out _)) throw new Exception("CSRF credential leaked to renderer");
        await guest.Request("POST", "/api/auth/login", new { username = "native-guest", password = "native-test-only" });
        var config = await owner.Request("GET", "/api/config");
        var response = await owner.Request("POST", "/api/rooms", new {
            name = "Native integration", settings = config.GetProperty("defaults"), allowedUsernames = new[] { "native-guest" },
        });
        var room = response.GetProperty("room");
        string roomId = room.GetProperty("roomId").GetString()!, invite = room.GetProperty("inviteToken").GetString()!;
        using var microphone = new NativeVoiceChannel(false);
        using var listener = new NativeVoiceChannel(true);
        int publicTurns = 0, privateTurns = 0, privateAudio = 0, ended = 0;
        int tacticsEnabled = 0, treadsConfirmed = 0;
        microphone.Message += e => { if (NativeVoiceChannel.Text(e, "type").StartsWith("subtitle.")) Interlocked.Increment(ref publicTurns); };
        microphone.Message += e => {
            if (NativeVoiceChannel.Text(e, "type") != "tactics.state") return;
            var state = e.GetProperty("state");
            if (state.GetProperty("enabled").GetBoolean()) Interlocked.Exchange(ref tacticsEnabled, 1);
            if (state.GetProperty("options").GetProperty("treadsAttribute").GetString() == "agi") Interlocked.Exchange(ref treadsConfirmed, 1);
        };
        listener.Message += e => { if (NativeVoiceChannel.Text(e, "type").StartsWith("subtitle.")) Interlocked.Increment(ref privateTurns); };
        listener.AudioOutput += _ => Interlocked.Increment(ref privateAudio);
        listener.Ended += (code, _) => Interlocked.Exchange(ref ended, code);
        await microphone.Connect(owner, roomId, "student", "", "", false);
        await listener.Connect(guest, roomId, "coach", "dota2.exe", invite, false);
        if (!microphone.Ready || !listener.Ready) throw new Exception("Native channels not independently ready");
        microphone.SendJson(new { type = "room.set_tactics", enabled = true });
        await Until(() => Volatile.Read(ref tacticsEnabled) == 1);
        await Task.Delay(1100);
        microphone.SendJson(new { type = "tactics.set_options", options = new { treadsAttribute = "agi" } });
        await Until(() => Volatile.Read(ref treadsConfirmed) == 1);
        if (!microphone.Ready || !listener.Ready) throw new Exception("Tactics controls restarted native audio");
        listener.PushAudio(new byte[2880]);
        await Until(() => Volatile.Read(ref privateTurns) == 2);
        if (publicTurns != 0 || privateAudio != 0) throw new Exception("Private listener leaked to room/TTS");
        microphone.PushAudio(new byte[2880]);
        await Until(() => Volatile.Read(ref publicTurns) == 2);
        if (privateTurns != 2) throw new Exception("Listener duplicated public room subtitles");
        await owner.Request("PATCH", $"/api/rooms/{roomId}/access", new { allowedUsernames = Array.Empty<string>() });
        await Until(() => Volatile.Read(ref ended) == 4003);
        if (listener.Ready || listener.PushAudio(new byte[2880])) throw new Exception("Revoked native channel continued uploading");
        if (!microphone.Ready) throw new Exception("Revocation stopped unrelated microphone connection");
        microphone.Dispose();
        using var pending = new NativeVoiceChannel(false);
        var starting = pending.Connect(owner, roomId, "student", "", "", false);
        pending.Dispose();
        try { await starting; throw new Exception("Cancelled startup succeeded"); }
        catch (Exception e) when (e is OperationCanceledException or System.Net.WebSockets.WebSocketException or ObjectDisposedException) { }
        // A fake outdated server must never be accepted as a private listener.
        using var old = new NativeVoiceChannel(true);
        try { await old.Connect(owner, "outdated", "coach", "dota2.exe", "", false); throw new Exception("Old server accepted"); }
        catch (InvalidOperationException) { }
        catch (OperationCanceledException) { }
        Console.WriteLine("PASS: native HTTP login/CSRF, tactics/treads roundtrip, dual WSS PCM, private captions/no TTS, revoke, cancellation and old-server guard.");
    }
}
