using DotaCoach.TwoPc;
using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;

internal static class VideoTests
{
    public static async Task Protocol(Action<bool, string> check, byte[] jpeg)
    {
        void Reject(Action action, string label) {
            bool rejected = false;
            try { action(); } catch (Exception e) when (e is ArgumentException or InvalidOperationException) { rejected = true; }
            check(rejected, label);
        }
        var raw = VideoPixels.FromBgra(640, 480, new byte[640 * 480 * 4]);
        long clock = 0; int conversions = 0;
        var box = new CoachMailbox(() => clock) { VideoJpegEncoder = _ => { conversions++; return jpeg.ToArray(); } };
        string session = box.Begin().SessionId, owner = "stream";
        box.AttachVideo(session, owner);
        var lease = box.IssueVideoLease(session);
        FrameMeta Meta(long seq) => new(session, lease.Id, seq, "diagnostic", 640, 480, null, null);
        var header = new VideoHeader(Meta(1), 640, 480, 166_666, 60);
        byte[] fakeAnnexB = [0, 0, 0, 1, 0x65, 0];
        var packet = VideoWire.Unpack(VideoWire.Pack(header, fakeAnnexB));
        check(packet.Header == header && packet.H264.SequenceEqual(fakeAnnexB), "binary video metadata/Annex-B packet roundtrip");
        Reject(() => VideoWire.Unpack([0xff, 0xff, 0xff, 0x7f, 0, 0, 0, 0, 0, 0, 0, 0]), "video length bomb rejected");
        Reject(() => VideoWire.Pack(header with { Width = 5000 }, fakeAnnexB), "oversized video rejected before decoder");
        Reject(() => VideoWire.Pack(header with { Time = -1 }, fakeAnnexB), "negative video timestamps rejected");
        Reject(() => VideoWire.Pack(header with { Fps = 45 }, fakeAnnexB), "only the offered frame rates are accepted");
        Reject(() => VideoWire.Pack(header, jpeg), "video endpoint never disguises JPEG as H264");
        var native = new FrameMeta(session, lease.Id, 1, "diagnostic", 3840, 2160, null, null);
        check(VideoWire.Unpack(VideoWire.Pack(new(native, 3840, 2160, 0, 60), fakeAnnexB)).Header.Width == 3840,
            "native 4K video is accepted for coach-side scaling");
        box.AcceptVideo(Meta(1), owner, raw, 100);
        var a = box.Latest()!; var b = box.Latest()!;
        check(conversions == 0 && a.Video != null, "ingress and preview selection never encode JPEG");
        a.Jpeg[0] = 0;
        check(conversions == 1 && b.Jpeg[0] == 0xff, "on-demand JPEG is cached per source, with isolated consumer copies");
        Reject(() => box.AcceptVideo(Meta(1), owner, raw, 100), "video sequence replay rejected");
        Reject(() => box.AcceptVideo(Meta(2), "other", raw, 100), "second video owner cannot feed a session");
        for (int i = 2; i <= 320; i++) {
            clock = i * 30;
            if (i % 20 == 0) lease = box.IssueVideoLease(session);
            box.AcceptVideo(Meta(i), owner, raw, 100);
        }
        box.SetReminder(session, 1, "manual-link-test", "original video frame", null);
        check(box.Poll(session)?.FrameSequence == 1, "30fps history retains a 9.6-second model source, not only 32 frames");
        clock = Wire.FrameLifetimeMs + 1; lease = box.IssueVideoLease(session);
        box.AcceptVideo(Meta(321), owner, raw, 100);
        Reject(() => box.SetReminder(session, 1, "manual-link-test", "expired", null), "video does not renew the original advice lifetime");
        clock += 3001;
        Reject(() => box.AcceptVideo(Meta(322), owner, raw, 100), "expired video lease rejects delayed TCP backlog");
        box.DetachVideo(session, "other");
        check(box.Snapshot().SessionId == session, "unrelated socket cleanup cannot clear the current video");
        box.DetachVideo(session, owner);
        check(box.Latest() == null && box.Snapshot().SessionId == null, "video disconnect immediately clears preview and reminders");

        await using var host = await CoachHost.Start(IPAddress.Loopback, 0);
        int extractions = 0;
        host.Mailbox.VideoJpegEncoder = _ => { Interlocked.Increment(ref extractions); return jpeg.ToArray(); };
        using var link = new GameLink(host.Pair(IPAddress.Loopback));
        await link.Connect(default); await link.OpenVideo(default);
        foreach (var failure in new[] { "token", "origin", "protocol" }) {
            var pair = host.Pair(IPAddress.Loopback);
            using var invalid = new ClientWebSocket();
            invalid.Options.Proxy = null;
            invalid.Options.RemoteCertificateValidationCallback = (_, cert, _, _) =>
                cert?.GetCertHashString(HashAlgorithmName.SHA256) == pair.Fingerprint;
            invalid.Options.SetRequestHeader("Authorization", "Bearer " + (failure == "token" ? new string('0', 64) : pair.Token));
            invalid.Options.SetRequestHeader("X-Session", link.SessionId);
            invalid.Options.AddSubProtocol(failure == "protocol" ? "wrong-protocol" : VideoWire.Subprotocol);
            if (failure == "origin") invalid.Options.SetRequestHeader("Origin", "https://untrusted.invalid");
            bool rejected = false;
            using var timeout = new CancellationTokenSource(3000);
            try { await invalid.ConnectAsync(new UriBuilder(pair.Address) { Scheme = "wss", Path = "/v2/video" }.Uri, timeout.Token); }
            catch (WebSocketException) { rejected = true; }
            check(rejected, $"video upgrade rejects invalid {failure} without disturbing active video");
        }
        var realLease = await link.GetVideoTicket(default);
        using var encoder = new WindowsH264(true, 640, 480, 60);
        long renewAt = Environment.TickCount64 + 750, sendStarted = Environment.TickCount64;
        for (int i = 1; i <= 100; i++) {
            if (Environment.TickCount64 >= renewAt) {
                realLease = await link.GetVideoTicket(default); renewAt = Environment.TickCount64 + 750;
            }
            var m = new FrameMeta(link.SessionId!, realLease.Id, i, "diagnostic", 640, 480, null, null);
            long started = Environment.TickCount64;
            foreach (var sample in encoder.Encode(raw, i * 166_666L))
                await link.SendVideo(new(m, 640, 480, sample.Time, 60), sample.Data, started, default);
        }
        long sendMs = Environment.TickCount64 - sendStarted;
        for (int wait = 0; wait < 100 && host.Mailbox.Snapshot().Frames < 100; wait++) await Task.Delay(20);
        check(host.Mailbox.Snapshot().Frames == 100 && extractions == 0,
            $"100 actual H264 frames traverse pinned WSS and native decoder without any JPEG extraction ({sendMs} ms incl. encode)");
        host.Mailbox.SetReminder(link.SessionId!, 1, "manual-link-test", "video asynchronous advice", new(.1, .8));
        check((await link.Poll(default))?.FrameSequence == 1, "independent control link accepts original source after 100 video frames");
        _ = host.Mailbox.Latest()!.Jpeg;
        check(extractions == 1, "coach explicitly extracts one requested video image");
        host.Revoke();
        bool revoked = false;
        try {
            // The ACK window lets a few sends succeed before the closed socket is noticed; keep sending until it is.
            for (int i = 101; i <= 101 + VideoWire.MaxInFlight * 4; i++) {
                var m = new FrameMeta(link.SessionId!, realLease.Id, i, "diagnostic", 640, 480, null, null);
                foreach (var sample in encoder.Encode(raw, i * 166_666L))
                    await link.SendVideo(new(m, 640, 480, sample.Time, 60), sample.Data, Environment.TickCount64, default);
                await Task.Delay(20);
            }
        } catch (Exception e) when (e is WebSocketException or InvalidOperationException or OperationCanceledException) { revoked = true; }
        check(revoked && host.Mailbox.Latest() == null, "revocation invalidates an already upgraded video socket");
        await link.End(default);
    }

    public static void Codec(Action<bool, string> check)
    {
        check(VideoPixels.SizeFor(3840, 2160) == (1600, 900) &&
            VideoPixels.SizeFor(4096, 2160) == (1600, 842), "4K video uses bounded even dimensions");
        check(VideoPixels.SizeFor(3840, 2160, 4096, 2304) == (3840, 2160) && VideoPixels.SizeFor(7680, 4320, 9999, 9999) == (4096, 2304),
            "native video keeps 4K and bounds 8K to the H.264 level 5.2 frame size");
        foreach (var (w, h) in new[] { (640, 480), (1280, 720), (1600, 900), (1600, 842), (1920, 1080) }) {
            using var encoder = new WindowsH264(true, w, h, 60);
            using var decoder = new WindowsH264(false, w, h, 60);
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < bgra.Length; i += 4) {
                bgra[i] = 25; bgra[i + 1] = 100; bgra[i + 2] = 180; bgra[i + 3] = 255;
            }
            var image = VideoPixels.FromBgra(w, h, bgra);
            int encoded = 0, decoded = 0; long lastTime = -1;
            for (int frame = 1; frame <= 12; frame++) {
                foreach (var packet in encoder.Encode(image, frame * 166_666L)) {
                    encoded++;
                    foreach (var result in decoder.Decode(packet.Data, packet.Time)) {
                        var rgb = result.Pixels.ToBgra();
                        if (result.Time <= lastTime || Math.Abs(rgb[0] - 25) > 12 ||
                            Math.Abs(rgb[1] - 100) > 12 || Math.Abs(rgb[2] - 180) > 12 ||
                            Math.Abs(rgb[^2] - 180) > 12) throw new Exception($"H264 pixels/timestamps {w}x{h}: {rgb[0]},{rgb[1]},{rgb[2]} / last {rgb[^2]}");
                        lastTime = result.Time; decoded++;
                    }
                }
            }
            check(encoded == 12 && decoded >= 11, $"real Windows H264 encode/decode {w}x{h}, {encoded}/{decoded} timestamped frames");
        }
        // The GPU path is machine-dependent: verify it wherever Windows reports a hardware encoder.
        var gpus = HardwareH264.Available();
        if (gpus.Count == 0) { Console.WriteLine("SKIP GPU H.264: no hardware encoder registered on this machine."); return; }
        foreach (var (w, h) in new[] { (1920, 1080), (3840, 2160) }) {
            var bgra = new byte[w * h * 4];
            for (int i = 0; i < bgra.Length; i += 4) { bgra[i] = 25; bgra[i + 1] = 100; bgra[i + 2] = 180; bgra[i + 3] = 255; }
            var image = VideoPixels.FromBgra(w, h, bgra);
            using var encoder = HardwareH264.Create(w, h, 60, 20_000_000);
            using var decoder = new WindowsH264(false, w, h, 60);
            int encoded = 0, decoded = 0; long last = -1; bool colour = true, ordered = true;
            for (int frame = 1; frame <= 30; frame++)
                foreach (var packet in encoder.Encode(image, frame * 166_666L)) {
                    encoded++; ordered &= packet.Time > last; last = packet.Time;
                    foreach (var result in decoder.Decode(packet.Data, packet.Time)) {
                        var rgb = result.Pixels.ToBgra(64, 36);
                        colour &= Math.Abs(rgb[0] - 25) <= 12 && Math.Abs(rgb[1] - 100) <= 12 && Math.Abs(rgb[2] - 180) <= 12;
                        decoded++;
                    }
                }
            check(encoded >= 28 && decoded >= 27 && colour && ordered,
                $"GPU H.264 ({encoder.Name}) {w}x{h}: {encoded} ordered access units decode to the source colour");
        }
    }
}
