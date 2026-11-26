namespace Wallfly.Transcription;

/// <summary>One finished or provisional audio passage handed to a chunk backend.</summary>
/// <param name="Wav">16 kHz mono PCM16 WAV (<see cref="WavEncoder"/>).</param>
/// <param name="Duration">Seconds of audio in <paramref name="Wav"/>; word times are validated against it.</param>
/// <param name="IsFinal">False for a 2-s preview: no retries, no word times, failures are dropped silently.</param>
/// <param name="IncludeWords">Request word times (only ever true for final passages).</param>
public sealed record ChunkAudio(byte[] Wav, double Duration, bool IsFinal, bool IncludeWords);

/// <summary>What a backend recognized; <see cref="Words"/> are relative to the passage start and already validated.</summary>
public sealed record ChunkTranscription(string Text, IReadOnlyList<TranscriptionWord>? Words = null);

/// <summary>
/// A chunk backend of <see cref="ChunkedTranscriptionPipeline"/>: <see cref="OpenAiTranscriptionClient"/>
/// (gpt-transcribe / whisper-1) or <see cref="Local.LocalWhisperClient"/> (whisper-server.exe).
/// Implementations throw <see cref="ChunkTranscriptionException"/> with a user-facing explanation and
/// must never put a response body into it.
/// </summary>
public interface IChunkTranscriber
{
    /// <summary>False when the backend cannot return word times (gpt-transcribe); the pipeline then never asks.</summary>
    bool SupportsWordTimestamps { get; }

    Task<ChunkTranscription> TranscribeChunkAsync(ChunkAudio chunk, CancellationToken cancellationToken);
}

/// <summary>
/// A recognition failure with a readable explanation. Derives from <see cref="InvalidOperationException"/>
/// so the older <see cref="ITranscriptionClient"/> callers keep surfacing the message unchanged.
/// </summary>
public sealed class ChunkTranscriptionException : InvalidOperationException
{
    public ChunkTranscriptionException(string message, int? statusCode = null, Exception? inner = null) : base(message, inner) => StatusCode = statusCode;

    /// <summary>The HTTP status that caused the failure, if any.</summary>
    public int? StatusCode { get; }
}

/// <summary>
/// The older text-only contract (<see cref="ITranscriptionClient"/>) as a chunk backend: every passage is
/// sent as if final and no word times exist. Used by <see cref="ChunkedTranscriptionPipeline(Audio.AudioSource, ITranscriptionClient, Guid, int)"/>,
/// which therefore sends no previews either.
/// </summary>
internal sealed class TranscriptionClientAdapter(ITranscriptionClient client) : IChunkTranscriber
{
    public bool SupportsWordTimestamps => false;

    public async Task<ChunkTranscription> TranscribeChunkAsync(ChunkAudio chunk, CancellationToken cancellationToken) =>
        new(await client.TranscribeAsync(chunk.Wav, cancellationToken).ConfigureAwait(false));
}
