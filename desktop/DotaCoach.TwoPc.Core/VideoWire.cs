using System.Buffers.Binary;
using System.Net.WebSockets;
using System.Text.Json;

namespace DotaCoach.TwoPc;

public sealed class VideoVersionException : InvalidOperationException;
public sealed record VideoHeader(FrameMeta Frame, int Width, int Height, long Time);
public sealed record VideoPacket(VideoHeader Header, byte[] H264);

/// <summary>One bounded Annex-B access unit per binary WebSocket message. No audio, JPEGs, URLs or credentials.</summary>
public static class VideoWire
{
    public const string Subprotocol = "dota-h264-v2";
    public const int MaxHeader = 16384, MaxPacket = Wire.MaxFrameBytes + MaxHeader + 4;
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
        if (h.Frame == null || h.Frame.Sequence > long.MaxValue / WindowsH264.Duration ||
            h.Time != h.Frame.Sequence * WindowsH264.Duration) throw new ArgumentException("Invalid video timestamp");
        VideoPixels.ValidateSize(h.Width, h.Height);
        CoachMailbox.ValidateMeta(h.Frame, h.Width, h.Height);
        if (data.Length is < 4 or > Wire.MaxFrameBytes || data[0] != 0 || data[1] != 0 ||
            !(data[2] == 1 || data[2] == 0 && data[3] == 1)) throw new ArgumentException("Annex-B H264 required");
    }
    public static async Task<byte[]> Receive(WebSocket socket, int limit, CancellationToken cancellation)
    {
        using var output = new MemoryStream();
        var buffer = new byte[32768];
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
        long lastSequence = 0;
        mailbox.AttachVideo(session, owner);
        try {
            while (!cancellation.IsCancellationRequested) {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
                timeout.CancelAfter(Wire.PresenceLifetimeMs);
                var packet = Unpack(await Receive(socket, MaxPacket, timeout.Token));
                var h = packet.Header;
                if (h.Frame.SessionId != session || h.Frame.Sequence <= lastSequence ||
                    first != null && (h.Width != first.Width || h.Height != first.Height ||
                        h.Frame.SourceWidth != first.Frame.SourceWidth || h.Frame.SourceHeight != first.Frame.SourceHeight ||
                        h.Frame.Source != first.Frame.Source))
                    throw new InvalidOperationException("Video format/session changed; reconnect required");
                mailbox.ValidateVideo(h.Frame, owner); // Validate session/freshness BEFORE native decoding.
                first ??= h;
                decoder ??= new(false, h.Width, h.Height);
                pending.Add(h.Time, h);
                if (pending.Count > 8) throw new InvalidOperationException("Decoder backlog");
                foreach (var decoded in decoder.Decode(packet.H264, h.Time)) {
                    if (!pending.Remove(decoded.Time, out var source)) throw new InvalidOperationException("Uncorrelated video output");
                    mailbox.AcceptVideo(source.Frame, owner, decoded.Pixels, packet.H264.Length);
                }
                lastSequence = h.Frame.Sequence;
                // Application-level ACK bounds TCP backlog. Reminder HTTP polling is independent.
                byte[] ack = new byte[8]; BinaryPrimitives.WriteInt64LittleEndian(ack, lastSequence);
                await socket.SendAsync(ack.AsMemory(), WebSocketMessageType.Binary, true, timeout.Token);
            }
        } finally {
            decoder?.Dispose(); mailbox.DetachVideo(session, owner);
        }
    }
}
