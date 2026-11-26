using System.Text;

namespace Wallfly.Transcription.Local;

/// <summary>
/// <c>POST &lt;base&gt;/inference</c> on the loopback whisper-server, a port of the macOS
/// <c>LocalWhisperServer.transcribeResult</c>: exactly its multipart fields (<c>response_format</c>,
/// <c>token_timestamps</c>, with word times also <c>no_timestamps=false</c> and <c>vad=false</c>,
/// <c>language</c> (subtag or "auto"), <c>translate/detect_language/diarize/tinydiarize=false</c>, an empty
/// <c>prompt</c>, <c>temperature=0.0</c>, <c>temperature_inc=0.0</c>, <c>no_context=true</c>, and the file).
/// No retries; 30 s until the answer starts and 45 s overall, as on macOS. Audio never leaves 127.0.0.1.
/// </summary>
public sealed class LocalWhisperClient : IChunkTranscriber, IDisposable
{
    private readonly Func<CancellationToken, Task<Uri>> endpoint;
    private readonly Func<bool> isRunning;
    private readonly LocalWhisperLease? lease;
    private readonly HttpClient http;
    private readonly TimeProvider time;

    /// <summary>A pipeline backend on a shared server; disposing the client releases the lease.</summary>
    public LocalWhisperClient(LocalWhisperLease lease, string locale = "auto", HttpMessageHandler? handler = null, TimeProvider? time = null)
        : this(token => lease.ReadyAsync(token), () => lease.Server.IsRunning, locale, handler, time) => this.lease = lease;

    /// <summary>A fixed endpoint (macOS <c>testEndpoint</c>): tests and an externally managed server.</summary>
    public LocalWhisperClient(Uri inferenceUri, string locale = "auto", HttpMessageHandler? handler = null, TimeProvider? time = null)
        : this(_ => Task.FromResult(inferenceUri), () => true, locale, handler, time)
    {
    }

    private LocalWhisperClient(Func<CancellationToken, Task<Uri>> endpoint, Func<bool> isRunning, string locale, HttpMessageHandler? handler, TimeProvider? time)
    {
        this.endpoint = endpoint;
        this.isRunning = isRunning;
        Language = OpenAiTranscriptionClient.LanguageHint(locale) ?? "auto";
        this.time = time ?? TimeProvider.System;
        http = new(handler ?? new SocketsHttpHandler { UseProxy = false, AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    /// <summary>The <c>language</c> field: "de", "en" or "auto".</summary>
    public string Language { get; }
    public TimeSpan ResponseTimeout { get; init; } = TimeSpan.FromSeconds(30);
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(45);
    public bool SupportsWordTimestamps => true;

    public async Task<ChunkTranscription> TranscribeChunkAsync(ChunkAudio chunk, CancellationToken cancellationToken)
    {
        var url = await endpoint(cancellationToken).ConfigureAwait(false);
        if (!isRunning()) throw new ChunkTranscriptionException(LocalWhisperMessages.NotRunning);
        if (url.Host != "127.0.0.1" || url.Scheme != Uri.UriSchemeHttp) throw new ChunkTranscriptionException(LocalWhisperMessages.MustStayLocal);
        using var total = new CancellationTokenSource(TotalTimeout, time);
        using var answer = new CancellationTokenSource(ResponseTimeout, time);
        using var overall = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total.Token);
        using var headers = CancellationTokenSource.CreateLinkedTokenSource(overall.Token, answer.Token);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = Body(chunk) };
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, headers.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status != 200) throw new ChunkTranscriptionException(LocalWhisperMessages.HttpFailed(status), status);
            var payload = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(overall.Token).ConfigureAwait(false));
            try { return WordTimeValidator.Decode(payload, chunk.Duration, chunk.IncludeWords); }
            catch (FormatException error) { throw new ChunkTranscriptionException(LocalWhisperMessages.Unreadable, inner: error); }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new ChunkTranscriptionException(LocalWhisperMessages.TimedOut, inner: error); }
        catch (HttpRequestException error)
        { throw new ChunkTranscriptionException(isRunning() ? LocalWhisperMessages.ConnectionFailed : LocalWhisperMessages.NotRunning, inner: error); }
        catch (IOException error)
        { throw new ChunkTranscriptionException(LocalWhisperMessages.ConnectionFailed, inner: error); }
    }

    /// <summary>
    /// A shared server may have been started for translation, language detection or another prompt: every
    /// request restates this meeting's language and keeps the two channels independent.
    /// </summary>
    internal ByteArrayContent Body(ChunkAudio chunk)
    {
        var body = new SwiftMultipart()
            .Field("response_format", chunk.IncludeWords ? "verbose_json" : "json")
            .Field("token_timestamps", chunk.IncludeWords ? "true" : "false");
        if (chunk.IncludeWords) body.Field("no_timestamps", "false").Field("vad", "false");
        return body
            .Field("language", Language)
            .Field("translate", "false")
            .Field("detect_language", "false")
            .Field("diarize", "false")
            .Field("tinydiarize", "false")
            .Field("prompt", "")
            // Avoid multi-pass temperature fallback on short, continuously arriving chunks.
            .Field("temperature", "0.0")
            .Field("temperature_inc", "0.0")
            .Field("no_context", "true")
            .Finish(chunk.Wav);
    }

    public void Dispose()
    {
        http.Dispose();
        lease?.Dispose();
    }
}
