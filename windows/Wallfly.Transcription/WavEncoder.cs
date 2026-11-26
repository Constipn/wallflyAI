using System.Text;

namespace Wallfly.Transcription;

/// <summary>
/// 16-bit PCM WAV for the HTTP chunk providers, byte-identical to the macOS <c>encodeWAV</c>: non-finite
/// values become silence, values are clamped to [-1, 1] and scaled by 32768 (negative) or 32767 (positive),
/// rounded half away from zero like Swift's <c>rounded()</c>.
/// </summary>
public static class WavEncoder
{
    public static byte[] Encode(IReadOnlyList<float> samples, int sampleRate = Audio.AudioFormat.SampleRate)
    {
        using var stream = new MemoryStream(44 + samples.Count * 2);
        using var writer = new BinaryWriter(stream, Encoding.ASCII);
        writer.Write(Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples.Count * 2); writer.Write(Encoding.ASCII.GetBytes("WAVEfmt "));
        writer.Write(16); writer.Write((short)1); writer.Write((short)1); writer.Write(sampleRate); writer.Write(sampleRate * 2); writer.Write((short)2); writer.Write((short)16);
        writer.Write(Encoding.ASCII.GetBytes("data")); writer.Write(samples.Count * 2);
        for (var index = 0; index < samples.Count; index++)
        {
            var value = samples[index];
            var finite = float.IsFinite(value) ? Math.Clamp(value, -1f, 1f) : 0f;
            writer.Write((short)MathF.Round(finite * (finite < 0 ? 32768f : 32767f), MidpointRounding.AwayFromZero));
        }
        writer.Flush();
        return stream.ToArray();
    }
}
