using System;

namespace DotaCoach.Desktop;

/// <summary>60ms, 24kHz mono PCM16 packets. Never retain mutable WASAPI buffers.</summary>
public sealed class AudioPacketizer(Action<byte[]> emit)
{
    public const int PacketBytes = 2880;
    private byte[] packet = new byte[PacketBytes];
    private int used;
    public int PendingBytes => used;

    public void Push(ReadOnlySpan<byte> audio)
    {
        if (audio.Length % 2 != 0) throw new ArgumentException("PCM16 must contain complete samples.");
        while (!audio.IsEmpty)
        {
            int count = Math.Min(PacketBytes - used, audio.Length);
            audio[..count].CopyTo(packet.AsSpan(used));
            used += count;
            audio = audio[count..];
            if (used == PacketBytes)
            {
                var ready = packet;
                packet = new byte[PacketBytes];
                used = 0;
                emit(ready);
            }
        }
    }
}
