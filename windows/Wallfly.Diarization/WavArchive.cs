using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Sample encodings the reader accepts.</summary>
public enum WavSampleFormat { Float32, Pcm16 }

/// <summary>
/// Read-only view of one 16 kHz mono archive file as written by <c>AudioArchiveWriter</c> (IEEE float32) or any other
/// 16 kHz mono PCM16/float32 WAV. Robust against a stale RIFF header after a crash: the writer patches the sizes only
/// every 30 s, so when the data chunk is the last declared chunk the file length wins.
/// </summary>
public sealed class WavArchive : IDisposable
{
    private readonly FileStream stream;
    private readonly int bytesPerFrame;
    private byte[] scratch = [];

    private WavArchive(string path, FileStream stream, WavSampleFormat format, int sampleRate, int channels, long dataOffset, long frameCount)
    {
        Path = path; this.stream = stream; Format = format; SampleRate = sampleRate; Channels = channels; DataOffset = dataOffset; FrameCount = frameCount;
        bytesPerFrame = format == WavSampleFormat.Float32 ? 4 : 2;
    }

    public string Path { get; }
    public WavSampleFormat Format { get; }
    public int SampleRate { get; }
    public int Channels { get; }
    public long DataOffset { get; }
    public long FrameCount { get; }

    /// <summary>Opens and validates the header. Throws <see cref="DiarizationException"/> (UnreadableAudio) for anything but 16 kHz mono.</summary>
    public static WavArchive Open(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        FileStream stream;
        try { stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        { throw DiarizationException.UnreadableAudio(name, error.Message); }
        try
        {
            var header = Parse(stream, name);
            if (header.SampleRate != (int)DiarizationTimeline.SampleRate || header.Channels != 1)
                throw DiarizationException.UnreadableAudio(name, $"{header.SampleRate} Hz, {header.Channels} channels instead of 16 kHz mono");
            return new WavArchive(path, stream, header.Format, header.SampleRate, header.Channels, header.DataOffset, header.DataBytes / (header.Format == WavSampleFormat.Float32 ? 4 : 2));
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    /// <summary>Number of 16 kHz frames of a file without keeping it open.</summary>
    public static long FrameCountOf(string path)
    {
        using var archive = Open(path);
        return archive.FrameCount;
    }

    /// <summary>Reads frames <c>[frame, frame + destination.Length)</c>; frames past the end stay untouched. Returns the frames read.</summary>
    public int Read(long frame, Span<float> destination)
    {
        if (frame < 0 || frame >= FrameCount || destination.IsEmpty) return 0;
        var count = (int)Math.Min(destination.Length, FrameCount - frame);
        var bytes = count * bytesPerFrame;
        if (scratch.Length < Math.Min(bytes, 1 << 20)) scratch = new byte[Math.Min(bytes, 1 << 20)];
        stream.Position = DataOffset + frame * bytesPerFrame;
        var done = 0;
        while (done < count)
        {
            var chunkFrames = Math.Min(count - done, scratch.Length / bytesPerFrame);
            var wanted = chunkFrames * bytesPerFrame;
            var read = 0;
            while (read < wanted)
            {
                var got = stream.Read(scratch, read, wanted - read);
                if (got == 0) break;
                read += got;
            }
            var frames = read / bytesPerFrame;
            if (frames == 0) break;
            var target = destination.Slice(done, frames);
            if (Format == WavSampleFormat.Float32)
            {
                var source = MemoryMarshal.Cast<byte, float>(scratch.AsSpan(0, frames * 4));
                for (var index = 0; index < frames; index++) target[index] = float.IsFinite(source[index]) ? source[index] : 0f;
            }
            else
            {
                for (var index = 0; index < frames; index++) target[index] = BinaryPrimitives.ReadInt16LittleEndian(scratch.AsSpan(index * 2, 2)) / 32768f;
            }
            done += frames;
            if (read < wanted) break;
        }
        return done;
    }

    public void Dispose() => stream.Dispose();

    private readonly record struct Header(WavSampleFormat Format, int SampleRate, int Channels, long DataOffset, long DataBytes);

    private static Header Parse(FileStream stream, string name)
    {
        var length = stream.Length;
        Span<byte> head = stackalloc byte[12];
        if (length < 12 || stream.Read(head) != 12 || !Ascii(head[..4], "RIFF") || !Ascii(head[8..12], "WAVE"))
            throw DiarizationException.UnreadableAudio(name, "not a RIFF/WAVE file");
        var declaredEnd = 8L + BinaryPrimitives.ReadUInt32LittleEndian(head[4..8]);
        int? tag = null, channels = null, sampleRate = null, bits = null;
        long? dataOffset = null, dataBytes = null;
        var position = 12L;
        Span<byte> chunk = stackalloc byte[8];
        while (position + 8 <= length)
        {
            stream.Position = position;
            if (stream.Read(chunk) != 8) break;
            var size = (long)BinaryPrimitives.ReadUInt32LittleEndian(chunk[4..8]);
            if (Ascii(chunk[..4], "fmt "))
            {
                var format = new byte[Math.Min(size, 64)];
                if (size < 16 || stream.Read(format) != format.Length) throw DiarizationException.UnreadableAudio(name, "invalid fmt chunk");
                tag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(0, 2));
                channels = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(2, 2));
                sampleRate = (int)BinaryPrimitives.ReadUInt32LittleEndian(format.AsSpan(4, 4));
                bits = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(14, 2));
                // WAVE_FORMAT_EXTENSIBLE: the sub-format GUID starts with the real format tag.
                if (tag == 0xFFFE && format.Length >= 26) tag = BinaryPrimitives.ReadUInt16LittleEndian(format.AsSpan(24, 2));
            }
            else if (Ascii(chunk[..4], "data") && dataOffset is null)
            {
                dataOffset = position + 8;
                dataBytes = size;
            }
            if (tag is not null && dataOffset is not null) break;
            position += 8 + size + (size & 1);
        }
        if (tag is null || channels is null || sampleRate is null || bits is null) throw DiarizationException.UnreadableAudio(name, "no fmt chunk");
        if (dataOffset is not long offset || dataBytes is not long bytes) throw DiarizationException.UnreadableAudio(name, "no data chunk");
        WavSampleFormat sampleFormat;
        if (tag == 3 && bits == 32) sampleFormat = WavSampleFormat.Float32;
        else if (tag == 1 && bits == 16) sampleFormat = WavSampleFormat.Pcm16;
        else throw DiarizationException.UnreadableAudio(name, $"unsupported sample format (tag {tag}, {bits} bit)");
        var available = Math.Max(0, length - offset);
        // A crash leaves the sizes of the last header patch: when data is the last declared chunk, the file length wins.
        if (bytes == 0 || bytes == uint.MaxValue || bytes > available || (length > declaredEnd && offset + bytes >= declaredEnd - 1)) bytes = available;
        var frameBytes = sampleFormat == WavSampleFormat.Float32 ? 4 : 2;
        return new Header(sampleFormat, sampleRate.Value, channels.Value, offset, bytes / frameBytes * frameBytes);
    }

    private static bool Ascii(ReadOnlySpan<byte> bytes, string text) => Encoding.ASCII.GetString(bytes) == text;
}
