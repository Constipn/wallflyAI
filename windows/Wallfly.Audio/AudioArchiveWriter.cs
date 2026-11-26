using System.Buffers.Binary;
using System.Text;

namespace Wallfly.Audio;

/// <summary>
/// Per-channel archive of exactly the frames the engine delivered: WAV, 16 kHz, mono, float32 — the
/// bit-identical counterpart of the macOS <c>.caf</c> files. Capture gaps are not written as silence;
/// the mapping back to meeting time comes from the anchors the caller records.
/// The RIFF sizes are patched every 30 s so a crash leaves a playable file.
/// </summary>
public sealed class AudioArchiveWriter : IDisposable
{
    public const int HeaderBytes = 44;
    private static readonly TimeSpan HeaderInterval = TimeSpan.FromSeconds(30);
    private readonly FileStream stream;
    private readonly byte[] scratch = new byte[AudioFormat.FrameSamples * sizeof(float)];
    private long dataBytes;
    private long lastHeader;
    private int disposed;

    public string Path { get; }
    public long SampleCount => dataBytes / sizeof(float);

    public AudioArchiveWriter(string path)
    {
        Path = path;
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(path))!);
        stream = new(path, FileMode.Create, FileAccess.Write, FileShare.Read);
        stream.Write(Header(0));
        lastHeader = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void Write(ReadOnlySpan<float> samples)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        var bytes = samples.Length * sizeof(float);
        var buffer = bytes <= scratch.Length ? scratch.AsSpan(0, bytes) : new byte[bytes].AsSpan();
        for (var index = 0; index < samples.Length; index++)
        {
            var value = float.IsFinite(samples[index]) ? Math.Clamp(samples[index], -1f, 1f) : 0f;
            BinaryPrimitives.WriteSingleLittleEndian(buffer[(index * sizeof(float))..], value);
        }
        stream.Write(buffer);
        dataBytes += bytes;
        if (System.Diagnostics.Stopwatch.GetElapsedTime(lastHeader) >= HeaderInterval) PatchHeader();
    }

    private void PatchHeader()
    {
        var position = stream.Position;
        stream.Position = 0;
        stream.Write(Header(dataBytes));
        stream.Position = position;
        stream.Flush();
        lastHeader = System.Diagnostics.Stopwatch.GetTimestamp();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        try { PatchHeader(); }
        finally { stream.Dispose(); }
    }

    private static byte[] Header(long dataBytes)
    {
        var header = new byte[HeaderBytes];
        var span = header.AsSpan();
        Encoding.ASCII.GetBytes("RIFF", span);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)Math.Min(uint.MaxValue, 36 + dataBytes));
        Encoding.ASCII.GetBytes("WAVEfmt ", span[8..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], 16);
        BinaryPrimitives.WriteUInt16LittleEndian(span[20..], 3);  // WAVE_FORMAT_IEEE_FLOAT
        BinaryPrimitives.WriteUInt16LittleEndian(span[22..], 1);  // mono
        BinaryPrimitives.WriteUInt32LittleEndian(span[24..], AudioFormat.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(span[28..], AudioFormat.SampleRate * sizeof(float));
        BinaryPrimitives.WriteUInt16LittleEndian(span[32..], sizeof(float));
        BinaryPrimitives.WriteUInt16LittleEndian(span[34..], 32);
        Encoding.ASCII.GetBytes("data", span[36..]);
        BinaryPrimitives.WriteUInt32LittleEndian(span[40..], (uint)Math.Min(uint.MaxValue, dataBytes));
        return header;
    }
}
