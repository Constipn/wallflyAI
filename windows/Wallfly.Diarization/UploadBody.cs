using System.Buffers.Binary;
using System.Net;
using System.Text;
using Concentus.Enums;
using Concentus.Oggfile;
using Concentus.Structs;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Formats of the cloud upload; both are produced from the concatenated timeline after the stop.</summary>
public enum CloudUploadFormat
{
    /// <summary>Ogg/Opus, mono 16 kHz, at <see cref="DeepgramPrerecordedOptions.OpusBitrate"/> (about 21 MB per hour at 48 kbit/s).</summary>
    OggOpus,
    /// <summary>16-bit PCM WAV, mono 16 kHz (115 MB per hour), lossless.</summary>
    Pcm16Wav,
    /// <summary>
    /// PCM16 WAV while the connection carries it in time; on a slow connection the PCM upload is abandoned after a probe
    /// window and the track is sent as Ogg/Opus instead (<see cref="UploadProbe"/>). An Ogg/Opus file an earlier attempt of the
    /// same request left in <see cref="DeepgramPrerecordedOptions.UploadCachePath"/> is sent at once, without a PCM probe.
    /// </summary>
    Auto
}

/// <summary>
/// The throughput watch of a PCM upload in <see cref="CloudUploadFormat.Auto"/>: measures the bytes the HTTP stack takes from
/// the first body write on and, once the probe window is over (<see cref="DeepgramPrerecordedOptions.AutoProbeDuration"/> and
/// <see cref="DeepgramPrerecordedOptions.AutoProbeBytes"/>), projects the rest of the PCM upload at the measured rate. It asks
/// for the switch to Ogg/Opus when that projection exceeds <see cref="DeepgramPrerecordedOptions.AutoSwitchAfter"/> and the
/// estimated Opus path (encode at <see cref="DeepgramPrerecordedOptions.OpusEncodeSecondsPerAudioHour"/> plus the whole Opus
/// upload at the same rate) would finish sooner. Callbacks run on the sending thread, one at a time.
/// </summary>
internal sealed class UploadProbe(DeepgramPrerecordedOptions options, double audioSeconds, TimeProvider time)
{
    private long started = -1;
    private long startBytes;

    /// <summary>Body bytes taken by the HTTP stack when last observed.</summary>
    public long Sent { get; private set; }
    /// <summary>Time since the first body write when last observed.</summary>
    public TimeSpan Elapsed { get; private set; }
    /// <summary>Measured throughput in bytes per second (null before the second write).</summary>
    public double? BytesPerSecond { get; private set; }
    /// <summary>The projected rest of the PCM upload at the last evaluation after the probe window (null before).</summary>
    public TimeSpan? Projected { get; private set; }

    /// <summary>The estimated Opus path at <paramref name="bytesPerSecond"/>: encode plus the whole upload at the bitrate's size (an upper bound; VBR speech is smaller).</summary>
    public TimeSpan OpusEstimate(double bytesPerSecond) => TimeSpan.FromSeconds(audioSeconds * options.OpusEncodeSecondsPerAudioHour / 3600
        + audioSeconds * options.OpusBitrate / 8.0 / bytesPerSecond);

    /// <returns>True when the PCM upload should be abandoned for Ogg/Opus.</returns>
    public bool Observe(long sent, long total)
    {
        var now = time.GetTimestamp();
        Sent = sent;
        if (started < 0)
        {
            started = now;
            startBytes = sent;
            return false;
        }
        Elapsed = time.GetElapsedTime(started, now);
        var bytes = sent - startBytes;
        if (Elapsed <= TimeSpan.Zero || bytes <= 0) return false;
        var rate = bytes / Elapsed.TotalSeconds;
        BytesPerSecond = rate;
        if (Elapsed < options.AutoProbeDuration || sent < options.AutoProbeBytes) return false;
        var projected = TimeSpan.FromSeconds(Math.Max(0, total - sent) / rate);
        Projected = projected;
        return projected > options.AutoSwitchAfter && projected > OpusEstimate(rate);
    }
}

/// <summary>The PCM upload of <see cref="CloudUploadFormat.Auto"/> was abandoned on purpose to send Ogg/Opus instead (never a failure).</summary>
internal sealed class UploadSwitchException() : Exception("The PCM upload was abandoned for Ogg/Opus.");

/// <summary>
/// Counts the body bytes as the HTTP stack takes them and remembers a failure of the body itself (an unreadable archive),
/// which the HTTP stack would otherwise wrap into a network error. Callbacks run on the sending thread.
/// </summary>
internal sealed class UploadMonitor
{
    private long sent;

    public long Sent => Interlocked.Read(ref sent);
    public long? Total { get; set; }
    public bool Completed { get; private set; }
    public Exception? Failure { get; private set; }
    public Action<long>? Written { get; set; }
    public Action? Finished { get; set; }

    public void Add(int bytes) => Written?.Invoke(Interlocked.Add(ref sent, bytes));

    public void Complete()
    {
        Completed = true;
        Finished?.Invoke();
    }

    public void Fail(Exception error) => Failure ??= error;
}

/// <summary>
/// The timeline as a 16-bit PCM WAV body, converted from the float32 archives while the HTTP stack sends it (one 64-k-sample
/// buffer, never a second copy of the audio). The length is known up front, so the upload progress is exact.
/// </summary>
internal sealed class Pcm16WavContent : HttpContent
{
    public const int HeaderBytes = 44;
    private const int ChunkSamples = 1 << 16;
    private readonly ConcatenatedWav audio;
    private readonly UploadMonitor monitor;

    public Pcm16WavContent(ConcatenatedWav audio, UploadMonitor monitor)
    {
        this.audio = audio; this.monitor = monitor;
        monitor.Total = Length(audio.SampleCount);
        Headers.ContentType = new("audio/wav");
    }

    public static long Length(long samples) => HeaderBytes + 2 * samples;

    /// <summary>The canonical 44-byte header of a 16 kHz mono PCM16 file with <paramref name="samples"/> samples.</summary>
    public static byte[] Header(long samples)
    {
        var data = checked((uint)(2 * samples));
        var header = new byte[HeaderBytes];
        Encoding.ASCII.GetBytes("RIFF").CopyTo(header, 0);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(4), checked(36 + data));
        Encoding.ASCII.GetBytes("WAVEfmt ").CopyTo(header, 8);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(16), 16);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(20), 1);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(22), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(24), (uint)DiarizationTimeline.SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(28), (uint)DiarizationTimeline.SampleRate * 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(32), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(header.AsSpan(34), 16);
        Encoding.ASCII.GetBytes("data").CopyTo(header, 36);
        BinaryPrimitives.WriteUInt32LittleEndian(header.AsSpan(40), data);
        return header;
    }

    /// <summary>Float sample → PCM16 with rounding and clipping (non-finite samples are already zero, see <see cref="WavArchive.Read"/>).</summary>
    public static short ToPcm16(float sample) => (short)Math.Clamp(MathF.Round(sample * 32767f), -32768f, 32767f);

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var samples = new float[ChunkSamples];
        var bytes = new byte[ChunkSamples * 2];
        var header = Header(audio.SampleCount);
        await stream.WriteAsync(header, cancellationToken).ConfigureAwait(false);
        monitor.Add(header.Length);
        for (long offset = 0; offset < audio.SampleCount; offset += ChunkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ChunkSamples, audio.SampleCount - offset);
            // A failing archive read is not a network error: remembered, so the caller reports it as such.
            try { audio.CopySamples(samples.AsSpan(0, count), offset); }
            catch (Exception error) when (error is not OperationCanceledException) { monitor.Fail(error); throw; }
            for (var index = 0; index < count; index++) BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(index * 2), ToPcm16(samples[index]));
            await stream.WriteAsync(bytes.AsMemory(0, count * 2), cancellationToken).ConfigureAwait(false);
            monitor.Add(count * 2);
        }
        monitor.Complete();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = Length(audio.SampleCount);
        return true;
    }
}

/// <summary>A seekable body (the encoded temporary file) sent from position 0 with byte counting; the stream is not disposed here.</summary>
internal sealed class MonitoredStreamContent : HttpContent
{
    private const int ChunkBytes = 1 << 16;
    private readonly Stream source;
    private readonly long length;
    private readonly UploadMonitor monitor;

    public MonitoredStreamContent(Stream source, string mediaType, UploadMonitor monitor)
    {
        this.source = source; this.monitor = monitor;
        length = source.Length;
        monitor.Total = length;
        Headers.ContentType = new(mediaType);
    }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => SerializeToStreamAsync(stream, context, CancellationToken.None);

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context, CancellationToken cancellationToken)
    {
        var buffer = new byte[ChunkBytes];
        source.Position = 0;
        long done = 0;
        while (done < length)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(ChunkBytes, length - done)), cancellationToken).ConfigureAwait(false);
            if (read == 0) break;
            await stream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            done += read;
            monitor.Add(read);
        }
        monitor.Complete();
    }

    protected override bool TryComputeLength(out long length)
    {
        length = this.length;
        return true;
    }
}

/// <summary>
/// Ogg/Opus encoding of the concatenated timeline (Concentus, pure managed): mono, 16 kHz input, VBR at the given bitrate,
/// the library's 20-ms frames and Ogg pages. Streams the timeline in 1-s chunks (no copy of the audio in memory) and checks
/// the token between chunks. Runs synchronously on the caller's thread; <see cref="EncodeAsync"/> moves it to a dedicated
/// low-priority thread.
/// </summary>
public static class OggOpusEncoder
{
    private const int ChunkSamples = 16_000;

    /// <summary>Encodes the timeline into <paramref name="output"/> (left open); reports the encoded share in [0, 1].</summary>
    public static void Encode(ConcatenatedWav audio, Stream output, int bitrate, int complexity, Action<double>? progress, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(audio);
        ArgumentNullException.ThrowIfNull(output);
        // The managed encoder on purpose: OpusCodecFactory probes for a native opus.dll/libopus on the search path first, which
        // Wallfly does not ship (and must not pick up from elsewhere); the managed one is deterministic on every machine.
#pragma warning disable CS0618
        var encoder = new OpusEncoder((int)DiarizationTimeline.SampleRate, 1, OpusApplication.OPUS_APPLICATION_AUDIO)
#pragma warning restore CS0618
        {
            Bitrate = bitrate, Complexity = Math.Clamp(complexity, 0, 10), UseVBR = true, SignalType = OpusSignal.OPUS_SIGNAL_VOICE
        };
        var ogg = new OpusOggWriteStream(encoder, output, new OpusTags(), (int)DiarizationTimeline.SampleRate, leaveOpen: true);
        var floats = new float[ChunkSamples];
        var shorts = new short[ChunkSamples];
        for (long offset = 0; offset < audio.SampleCount; offset += ChunkSamples)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var count = (int)Math.Min(ChunkSamples, audio.SampleCount - offset);
            audio.CopySamples(floats.AsSpan(0, count), offset);
            for (var index = 0; index < count; index++) shorts[index] = Pcm16WavContent.ToPcm16(floats[index]);
            ogg.WriteSamples(shorts, 0, count);
            progress?.Invoke((double)(offset + count) / audio.SampleCount);
        }
        ogg.Finish();
        output.Flush();
    }

    /// <summary>
    /// <see cref="Encode"/> on a dedicated background thread at <paramref name="priority"/>. Completes when the thread has
    /// stopped writing (also after cancellation), so the caller may delete the output afterwards.
    /// </summary>
    public static Task EncodeAsync(ConcatenatedWav audio, Stream output, int bitrate, int complexity, ThreadPriority priority, Action<double>? progress,
        CancellationToken cancellationToken)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                Encode(audio, output, bitrate, complexity, progress, cancellationToken);
                completion.TrySetResult();
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); }
            catch (Exception error) { completion.TrySetException(error); }
        })
        { IsBackground = true, Priority = priority, Name = "Wallfly cloud upload (Opus)" };
        worker.Start();
        return completion.Task;
    }
}
