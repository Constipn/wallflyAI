using System.Buffers.Binary;

namespace Wallfly.Audio;

/// <summary>Interleaved PCM to mono float, independent of any audio library.</summary>
public static class Pcm
{
    /// <summary>Averages the channels of one interleaved buffer. Non-finite samples count as silence.</summary>
    public static float[] DecodeMono(ReadOnlySpan<byte> data, int channels, int bitsPerSample, bool isFloat)
    {
        if (channels <= 0 || bitsPerSample % 8 != 0 || bitsPerSample is not (16 or 24 or 32)) throw new InvalidDataException("Unsupported PCM format.");
        if (isFloat && bitsPerSample != 32) throw new InvalidDataException("Unsupported PCM format.");
        var bytesPerSample = bitsPerSample / 8;
        var blockAlign = bytesPerSample * channels;
        if (blockAlign <= 0 || data.Length % blockAlign != 0) throw new InvalidDataException("Incomplete PCM frame.");
        var frames = data.Length / blockAlign;
        var result = new float[frames];
        for (var frame = 0; frame < frames; frame++)
        {
            var sum = 0f;
            for (var channel = 0; channel < channels; channel++)
            {
                var position = frame * blockAlign + channel * bytesPerSample;
                var sample = isFloat
                    ? BitConverter.ToSingle(data[position..(position + 4)])
                    : bytesPerSample switch
                    {
                        2 => BinaryPrimitives.ReadInt16LittleEndian(data.Slice(position, 2)) / 32768f,
                        3 => ((data[position] | data[position + 1] << 8 | data[position + 2] << 16) << 8 >> 8) / 8388608f,
                        _ => BinaryPrimitives.ReadInt32LittleEndian(data.Slice(position, 4)) / 2147483648f
                    };
                sum += float.IsFinite(sample) ? sample : 0;
            }
            result[frame] = Math.Clamp(sum / channels, -1, 1);
        }
        return result;
    }
}
