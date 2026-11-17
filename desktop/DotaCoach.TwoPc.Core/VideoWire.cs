using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

namespace DotaCoach.TwoPc;

public sealed class VideoVersionException : InvalidOperationException;
/// <summary>Time is the capture instant in 100 ns units since the stream started; Fps is the configured target.</summary>
public sealed record VideoHeader(FrameMeta Frame, int Width, int Height, long Time, int Fps);
public sealed record VideoPacket(VideoHeader Header, byte[] H264);

/// <summary>One bounded Annex-B access unit per binary WebSocket message. No audio, JPEGs, URLs or credentials.</summary>
public static class VideoWire
{
    public const string Subprotocol = "dota-h264-v3";
    public static readonly int[] FrameRates = [30, 60, 90, 120];
    // A 4K IDR frame at high bitrate can exceed 1 MiB; the old JPEG budget does not apply to video.
    public const int MaxAccessUnitBytes = 8 * 1024 * 1024;
    public const int MaxHeader = 16384, MaxPacket = MaxAccessUnitBytes + MaxHeader + 4;
    /// <summary>Access units the game machine may send before the coach acknowledges them (decoded).</summary>
    public const int MaxInFlight = 4;
    public const int AckTimeoutMs = 1500;
    public static byte[] Pack(VideoHeader header, byte[] h264)
    {
        Validate(header, h264);
        byte[] json = JsonSerializer.SerializeToUtf8Bytes(header, Wire.Json);
        if (json.Length > MaxHeader) throw new ArgumentException("Video header too large");
        byte[] result = new byte[4 + json.Length + h264.Length];
        BinaryPrimitives.WriteInt32LittleEndian(result, json.Length);
        json.CopyTo(result, 4); h264.CopyTo(result, 4 + json.Length); return result;
    }
    public static VideoPacket Unpack(byte[] message)
    {
        if (message.Length < 12 || message.Length > MaxPacket) throw new ArgumentException("Invalid video envelope");
        int length = BinaryPrimitives.ReadInt32LittleEndian(message);
        if (length is < 2 or > MaxHeader || 4 + length >= message.Length) throw new ArgumentException("Invalid video header");
        var header = JsonSerializer.Deserialize<VideoHeader>(message.AsSpan(4, length), Wire.Json)
            ?? throw new ArgumentException("Invalid video header");
        byte[] data = message[(4 + length)..];
        Validate(header, data); return new(header, data);
    }
    private static void Validate(VideoHeader h, byte[] data)
    {
        if (h.Frame == null || h.Time < 0 || h.Time > 10_000_000L * 86_400 * 7 || !FrameRates.Contains(h.Fps))
            throw new ArgumentException("Invalid video timestamp or frame rate");
        VideoPixels.ValidateSize(h.Width, h.Height);
        CoachMailbox.ValidateMeta(h.Frame, h.Width, h.Height);
        if (data.Length is < 4 or > MaxAccessUnitBytes || data[0] != 0 || data[1] != 0 ||
            !(data[2] == 1 || data[2] == 0 && data[3] == 1)) throw new ArgumentException("Annex-B H264 required");
    }
    public static async Task<byte[]> Receive(WebSocket socket, int limit, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[65536];
        while (true) {
            var part = await socket.ReceiveAsync(buffer.AsMemory(), cancellation);
            if (part.MessageType == WebSocketMessageType.Close) throw new InvalidOperationException("Video stream closed");
            if (part.MessageType != WebSocketMessageType.Binary || output.Length + part.Count > limit)
                throw new ArgumentException("Invalid/oversized video message");
            output.Write(buffer, 0, part.Count);
            if (part.EndOfMessage) return output.ToArray();
        }
    }
    public static async Task ReceiveStream(WebSocket socket, CoachMailbox mailbox, string session, CancellationToken cancellation)
    {
        string owner = Guid.NewGuid().ToString("N");
        WindowsH264? decoder = null;
        VideoHeader? first = null;
        var pending = new Dictionary<long, VideoHeader>();
        long lastSequence = 0, lastTime = -1;
        mailbox.AttachVideo(session, owner);
        try {
            while (!cancellation.IsCancellationRequested) {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(Wire.PresenceLifetimeMs);
                var packet = Unpack(await Receive(socket, MaxPacket, timeout.Token));
                var h = packet.Header;
                if (h.Frame.SessionId != session || h.Frame.Sequence <= lastSequence || h.Time <= lastTime ||
                    first != null && (h.Width != first.Width || h.Height != first.Height || h.Fps != first.Fps ||
                        h.Frame.SourceWidth != first.Frame.SourceWidth || h.Frame.SourceHeight != first.Frame.SourceHeight ||
                        h.Frame.Source != first.Frame.Source))
                    throw new InvalidOperationException("Video format/session changed; reconnect required");
                mailbox.ValidateVideo(h.Frame, owner); // Validate session/freshness BEFORE native decoding.
                first ??= h;
                decoder ??= new(false, h.Width, h.Height, h.Fps);
                pending.Add(h.Time, h);
                if (pending.Count > 8) throw new InvalidOperationException("Decoder backlog");
                foreach (var decoded in decoder.Decode(packet.H264, h.Time)) {
                    if (!pending.Remove(decoded.Time, out var source)) throw new InvalidOperationException("Uncorrelated video output");
                    mailbox.AcceptVideo(source.Frame, owner, decoded.Pixels, packet.H264.Length);
                }
                lastSequence = h.Frame.Sequence; lastTime = h.Time;
                // Acknowledge after decoding: the sender's in-flight window is real decode back-pressure.
                byte[] ack = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(ack, lastSequence);
                await socket.SendAsync(ack.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
        } finally {
            decoder?.Dispose(); mailbox.DetachVideo(session, owner);
        }
    }
}
