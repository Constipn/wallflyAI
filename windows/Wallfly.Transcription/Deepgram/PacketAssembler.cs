namespace Wallfly.Transcription.Deepgram;

/// <summary>One unit of linear16 audio for the socket; <see cref="Offset"/> is the session offset of its first sample.</summary>
public readonly record struct DeepgramPacket(double Offset, byte[] Data)
{
    public const int BytesPerSecond = 32_000;
    public double Duration => Data.Length / (double)BytesPerSecond;
}

/// <summary>
/// Cuts the PCM stream into 3 200-byte (100 ms) packets. A started packet keeps the offset of its first
/// byte; every full packet advances the offset by 0.1 s. <see cref="Flush"/> emits the short remainder
/// (after 100 ms without audio, at a capture gap and at stop).
/// </summary>
public sealed class PacketAssembler
{
    public const int PacketBytes = 3200;
    public const double PacketSeconds = PacketBytes / (double)DeepgramPacket.BytesPerSecond;

    private byte[] partial = new byte[PacketBytes * 2];
    private int count;
    private double partialOffset;

    public bool HasPartial => count > 0;
    public int PartialBytes => count;

    /// <summary>Appends <paramref name="pcm"/> that starts at <paramref name="offset"/> and returns the completed packets.</summary>
    public List<DeepgramPacket> Append(ReadOnlySpan<byte> pcm, double offset)
    {
        if (count == 0) partialOffset = offset;
        if (count + pcm.Length > partial.Length) Array.Resize(ref partial, Math.Max(partial.Length * 2, count + pcm.Length));
        pcm.CopyTo(partial.AsSpan(count));
        count += pcm.Length;
        var packets = new List<DeepgramPacket>();
        var consumed = 0;
        while (count - consumed >= PacketBytes)
        {
            packets.Add(new(partialOffset, partial.AsSpan(consumed, PacketBytes).ToArray()));
            consumed += PacketBytes;
            partialOffset += PacketSeconds;
        }
        if (consumed > 0)
        {
            partial.AsSpan(consumed, count - consumed).CopyTo(partial);
            count -= consumed;
        }
        return packets;
    }

    /// <summary>The short remainder as its own packet, or null when nothing is buffered.</summary>
    public DeepgramPacket? Flush()
    {
        if (count == 0) return null;
        var packet = new DeepgramPacket(partialOffset, partial.AsSpan(0, count).ToArray());
        count = 0;
        return packet;
    }

    /// <summary>
    /// Signed 16-bit little-endian PCM. Values are clamped to [-1, 1], scaled by 32 768 and rounded half
    /// away from zero, then clamped to the Int16 range; NaN and infinities become silence.
    /// </summary>
    public static byte[] EncodePcm(ReadOnlySpan<float> samples)
    {
        var result = new byte[samples.Length * 2];
        for (var index = 0; index < samples.Length; index++)
        {
            var sample = samples[index];
            var value = float.IsFinite(sample) ? Math.Clamp((double)sample, -1, 1) : 0;
            var integer = (short)Math.Clamp(Math.Round(value * 32768, MidpointRounding.AwayFromZero), short.MinValue, short.MaxValue);
            var bits = (ushort)integer;
            result[index * 2] = (byte)bits;
            result[index * 2 + 1] = (byte)(bits >> 8);
        }
        return result;
    }
}
