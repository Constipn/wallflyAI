using Wallfly.Audio;
using Wallfly.Transcription.Deepgram;
using Wallfly.Transcription.Local;

namespace Wallfly.Transcription;

/// <summary>The backend of the chunk pipeline when Deepgram is not used.</summary>
public enum ChunkedTranscriptionProvider
{
    /// <summary>gpt-transcribe (Others: whisper-1 when word times are required).</summary>
    OpenAi,
    /// <summary>The bundled whisper-server.exe on loopback.</summary>
    LocalWhisper
}

/// <summary>
/// What a recording attempt needs to build its two pipelines, taken from the settings at start:
/// the provider is fixed for the whole attempt and is never switched automatically.
/// </summary>
/// <param name="UseDeepgram">Deepgram Nova-3 streaming; otherwise the chunked HTTP pipeline.</param>
/// <param name="DeepgramKey">Only used for Deepgram.</param>
/// <param name="Locale">"auto", "de-DE" or "en-US".</param>
public sealed record TranscriptionPipelineSettings(bool UseDeepgram, string DeepgramKey = "", string Locale = "auto")
{
    public ChunkedTranscriptionProvider ChunkedProvider { get; init; } = ChunkedTranscriptionProvider.OpenAi;
    /// <summary>OpenAI key for <see cref="ChunkedTranscriptionProvider.OpenAi"/> when no client is passed to Create.</summary>
    public string OpenAiKey { get; init; } = "";
    /// <summary>
    /// Teams speaker names need word times (macOS <c>usesTeamsNames</c>): OpenAI then transcribes Others with
    /// whisper-1 and word times (Me unchanged); local Whisper asks for word times on both channels and starts
    /// the server without VAD.
    /// </summary>
    public bool WordTimesRequired { get; init; }
    /// <summary>Keep audio: chooses the recovery sentence of a failure ("remains on this PC" / "not retained"); null adds none.</summary>
    public bool? KeepAudio { get; init; }
    /// <summary>Empty: <c>models\whisper\ggml-large-v3-turbo.bin</c> next to the app.</summary>
    public string WhisperModelPath { get; init; } = "";
    /// <summary>Empty: <c>whisper\whisper-server.exe</c> next to the app.</summary>
    public string WhisperServerPath { get; init; } = "";
    /// <summary>
    /// Deepgram only: the keyterms (names, vocabulary) of the next connection, read on the pipeline's queue at every
    /// connect and reconnect of either channel, so it must be thread-safe; null sends none. Never printed.
    /// </summary>
    public Func<IReadOnlyList<string>>? Keyterms { get; init; }

    public WhisperServerOptions WhisperServer => new()
    {
        ExecutablePath = WhisperServerPath,
        ModelPath = WhisperModelPath,
        WordTimestamps = WordTimesRequired
    };

    /// <summary>ToString never prints a key.</summary>
    private bool PrintMembers(System.Text.StringBuilder builder)
    {
        builder.Append($"UseDeepgram = {UseDeepgram}, Locale = {Locale}, ChunkedProvider = {ChunkedProvider}, WordTimesRequired = {WordTimesRequired}, KeepAudio = {KeepAudio}");
        return true;
    }
}

/// <summary>
/// Picks the pipeline per channel: <see cref="DeepgramStreamingPipeline"/> (one socket per source) or
/// <see cref="ChunkedTranscriptionPipeline"/> over OpenAI or the local whisper-server. Both local channels
/// share one server through <see cref="LocalWhisperHost"/>; it stops when both have finished. The socket
/// factory, the clock and the server host are injectable for tests.
/// </summary>
public sealed class TranscriptionPipelineFactory(IWebSocketFactory? sockets = null, TimeProvider? time = null,
    DeepgramStreamingPipeline.Policy? deepgramPolicy = null, LocalWhisperHost? localWhisper = null)
{
    private sealed class NoLease : IDisposable { public void Dispose() { } }

    private readonly LocalWhisperHost whisper = localWhisper ?? LocalWhisperHost.Shared;

    /// <summary>
    /// For local Whisper, starts the shared server and waits until the model is loaded (up to 60 s), so a
    /// recording does not start when the server cannot (macOS prepares before capture). Dispose the returned
    /// lease after both pipelines were created, or on failure; other providers return a no-op lease.
    /// </summary>
    /// <exception cref="ChunkTranscriptionException">The server could not be started; it has been stopped again.</exception>
    public async Task<IDisposable> PrepareAsync(TranscriptionPipelineSettings settings, CancellationToken cancellationToken = default)
    {
        if (settings.UseDeepgram || settings.ChunkedProvider != ChunkedTranscriptionProvider.LocalWhisper) return new NoLease();
        var lease = whisper.Acquire(settings.WhisperServer);
        try
        {
            await lease.ReadyAsync(cancellationToken).ConfigureAwait(false);
            return lease;
        }
        catch
        {
            lease.Dispose();
            throw;
        }
    }

    /// <param name="chunkedClient">
    /// An externally owned client for the chunk pipeline (not disposed by the pipeline). A client that also
    /// implements <see cref="IChunkTranscriber"/> gets previews; a text-only one runs final passages only.
    /// Without one, the pipeline builds and owns its backend from <paramref name="settings"/>.
    /// </param>
    public ITranscriptionPipeline Create(AudioSource source, Guid sessionId, TranscriptionPipelineSettings settings, ITranscriptionClient? chunkedClient = null)
    {
        if (settings.UseDeepgram)
            return new DeepgramStreamingPipeline(source, settings.DeepgramKey, sessionId, settings.Locale, sockets, time, deepgramPolicy, keyterms: settings.Keyterms);
        var local = settings.ChunkedProvider == ChunkedTranscriptionProvider.LocalWhisper;
        var options = new ChunkedPipelineOptions
        {
            // macOS: local Whisper times both channels, OpenAI only Others (Me stays on gpt-transcribe).
            WordTimestamps = settings.WordTimesRequired && (local || source == AudioSource.Others),
            AudioRetained = settings.KeepAudio,
            Time = time
        };
        if (chunkedClient != null)
            return chunkedClient is IChunkTranscriber transcriber
                ? new ChunkedTranscriptionPipeline(source, transcriber, sessionId, options)
                : new ChunkedTranscriptionPipeline(source, chunkedClient, sessionId);
        if (local)
        {
            var lease = whisper.Acquire(settings.WhisperServer);
            return new ChunkedTranscriptionPipeline(source, new LocalWhisperClient(lease, settings.Locale, time: time), sessionId,
                options with { DisposeTranscriber = true });
        }
        if (string.IsNullOrWhiteSpace(settings.OpenAiKey)) throw new ArgumentNullException(nameof(chunkedClient), OpenAiHttp.MissingKey);
        var client = new OpenAiTranscriptionClient(settings.OpenAiKey, new()
        {
            Model = OpenAiTranscriptionOptions.ModelFor(source, settings.WordTimesRequired),
            Locale = settings.Locale,
            Time = time
        });
        return new ChunkedTranscriptionPipeline(source, client, sessionId, options with { DisposeTranscriber = true });
    }
}
