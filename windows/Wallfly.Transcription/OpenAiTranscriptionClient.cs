using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using Wallfly.Audio;

namespace Wallfly.Transcription;

/// <summary>One transcription request for a finished audio passage (text only, no previews, no word times).</summary>
public interface ITranscriptionClient
{
    Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken);
}

public sealed class OpenAiTranscriptionOptions
{
    /// <summary>macOS <c>AppSettings.openAITranscriptionModel</c>; sends <c>languages[]</c>.</summary>
    public const string DefaultModel = "gpt-transcribe";
    /// <summary>The model that returns word times; sends <c>language</c>.</summary>
    public const string WordTimestampModel = "whisper-1";
    public const string Endpoint = "https://api.openai.com/v1/audio/transcriptions";

    /// <summary>Empty selects <see cref="DefaultModel"/>, as on macOS.</summary>
    public string Model { get; init; } = DefaultModel;
    /// <summary>"auto", or a BCP-47 tag such as "de-DE"; only the language subtag is sent.</summary>
    public string Locale { get; init; } = "auto";
    /// <summary>Per attempt (spec 4.2: 90 s per request).</summary>
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>For one passage including retries and their waits (spec 4.2: 180 s overall).</summary>
    public TimeSpan TotalTimeout { get; init; } = TimeSpan.FromSeconds(180);
    public TimeProvider? Time { get; init; }

    /// <summary>
    /// macOS <c>AppSettings.openAIModel(for:)</c>: with Teams speaker names the Others channel needs word
    /// times and therefore whisper-1; Me always uses gpt-transcribe.
    /// </summary>
    public static string ModelFor(AudioSource source, bool wordTimesRequired) =>
        wordTimesRequired && source == AudioSource.Others ? WordTimestampModel : DefaultModel;
}

/// <summary>
/// <c>POST https://api.openai.com/v1/audio/transcriptions</c>, a port of the macOS
/// <c>ChunkedTranscriptionPipeline.transcribe</c>: multipart <c>model</c>, <c>response_format</c>
/// (<c>json</c> / <c>verbose_json</c>), <c>timestamp_granularities[]=word</c> only with word times, the language
/// as <c>languages[]</c> for gpt-transcribe and <c>language</c> otherwise (omitted for "auto"), and
/// <c>file=meeting.wav</c>. Final passages are retried at most twice on 429/5xx after
/// <c>min(10, max(1, Retry-After or 2^attempt))</c> seconds; previews and transport errors are never retried.
/// No redirects, no cookies, and a response body never reaches an exception.
/// </summary>
public sealed class OpenAiTranscriptionClient : ITranscriptionClient, IChunkTranscriber, IDisposable
{
    private readonly HttpClient http;
    private readonly string key;
    private readonly OpenAiTranscriptionOptions options;
    private readonly TimeProvider time;

    public OpenAiTranscriptionClient(string key, OpenAiTranscriptionOptions? options = null, HttpMessageHandler? handler = null)
    {
        this.key = key.Trim();
        this.options = options ?? new();
        Model = string.IsNullOrWhiteSpace(this.options.Model) ? OpenAiTranscriptionOptions.DefaultModel : this.options.Model.Trim();
        time = this.options.Time ?? TimeProvider.System;
        http = new(handler ?? new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }

    public string Model { get; }

    /// <summary>The gpt-* transcription models accept only <c>json</c>; word times come from whisper-1.</summary>
    public bool SupportsWordTimestamps => !Model.StartsWith("gpt-", StringComparison.OrdinalIgnoreCase);

    /// <summary>"de-DE" and "de" both become "de"; "auto" and empty send no language hint.</summary>
    public static string? LanguageHint(string? locale)
    {
        if (string.IsNullOrWhiteSpace(locale) || locale.Equals("auto", StringComparison.OrdinalIgnoreCase)) return null;
        var language = locale.Split('-', '_')[0].Trim().ToLowerInvariant();
        return language.Length == 0 || language == "und" ? null : language;
    }

    /// <summary>The older text-only contract: a final passage without word times.</summary>
    public async Task<string> TranscribeAsync(byte[] wav, CancellationToken cancellationToken) =>
        (await TranscribeChunkAsync(new(wav, 0, IsFinal: true, IncludeWords: false), cancellationToken).ConfigureAwait(false)).Text;

    public async Task<ChunkTranscription> TranscribeChunkAsync(ChunkAudio chunk, CancellationToken cancellationToken)
    {
        if (key.Length == 0) throw new ChunkTranscriptionException(OpenAiHttp.MissingKey);
        var retries = chunk.IsFinal ? 2 : 0;
        using var total = new CancellationTokenSource(options.TotalTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, total.Token);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                linked.Token.ThrowIfCancellationRequested();
                var (status, retryAfter, payload) = await SendOnceAsync(chunk, linked.Token).ConfigureAwait(false);
                if (status is >= 200 and < 300)
                {
                    try { return WordTimeValidator.DecodeOpenAi(payload, chunk.Duration, chunk.IncludeWords); }
                    catch (FormatException error) { throw new ChunkTranscriptionException(OpenAiHttp.Unreadable, inner: error); }
                }
                if (attempt < retries && (status == 429 || status is >= 500 and < 600))
                {
                    await Task.Delay(RetryDelay(retryAfter, attempt), time, linked.Token).ConfigureAwait(false);
                    continue;
                }
                // Never include an API error body: it can contain request data or credentials.
                throw OpenAiHttp.StatusError(status);
            }
        }
        catch (OperationCanceledException error) when (!cancellationToken.IsCancellationRequested)
        { throw new ChunkTranscriptionException(OpenAiHttp.TimedOut, inner: error); }
        catch (HttpRequestException error)
        { throw new ChunkTranscriptionException(OpenAiHttp.ConnectionDropped, inner: error); }
        catch (IOException error)
        { throw new ChunkTranscriptionException(OpenAiHttp.ConnectionDropped, inner: error); }
    }

    /// <summary>
    /// <c>min(10, max(1, Retry-After or 2^attempt))</c>. Like Swift's <c>Double(String)</c>, only a numeric
    /// Retry-After counts; an HTTP date falls back to the exponential delay.
    /// </summary>
    public static TimeSpan RetryDelay(string? retryAfter, int attempt)
    {
        var seconds = double.TryParse(retryAfter, NumberStyles.Float, CultureInfo.InvariantCulture, out var server) && !double.IsNaN(server)
            ? server
            : Math.Pow(2, attempt);
        return TimeSpan.FromSeconds(Math.Min(10, Math.Max(1, seconds)));
    }

    private async Task<(int Status, string? RetryAfter, string Payload)> SendOnceAsync(ChunkAudio chunk, CancellationToken token)
    {
        using var attempt = new CancellationTokenSource(options.RequestTimeout, time);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, attempt.Token);
        using var request = new HttpRequestMessage(HttpMethod.Post, OpenAiTranscriptionOptions.Endpoint) { Content = Body(chunk) };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseContentRead, linked.Token).ConfigureAwait(false);
        var status = (int)response.StatusCode;
        var retryAfter = response.Headers.TryGetValues("Retry-After", out var values) ? values.FirstOrDefault() : null;
        var payload = status is >= 200 and < 300
            ? Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync(linked.Token).ConfigureAwait(false))
            : "";
        return (status, retryAfter, payload);
    }

    /// <summary>Field order and spelling exactly as the macOS client writes them.</summary>
    internal ByteArrayContent Body(ChunkAudio chunk)
    {
        var body = new SwiftMultipart()
            .Field("model", Model)
            .Field("response_format", chunk.IncludeWords ? "verbose_json" : "json");
        if (chunk.IncludeWords) body.Field("timestamp_granularities[]", "word");
        if (LanguageHint(options.Locale) is string language)
            body.Field(Model == OpenAiTranscriptionOptions.DefaultModel ? "languages[]" : "language", language);
        return body.Finish(chunk.Wav);
    }

    public void Dispose() => http.Dispose();
}

/// <summary>User-facing OpenAI transcription wording (macOS <c>reportUploadFailure</c>); a response body never reaches the user.</summary>
public static class OpenAiHttp
{
    public const string MissingKey = "Add an OpenAI key in Settings. Your notes stay usable locally.";
    public const string ConnectionDropped = "The connection to OpenAI dropped during transcription.";
    public const string TimedOut = "OpenAI did not answer the transcription request in time.";
    public const string Unreadable = "OpenAI returned an unreadable transcription.";

    /// <summary>The explanation for an HTTP status, as on macOS.</summary>
    public static string Explanation(int status) => status switch
    {
        401 => "OpenAI rejects the API key. Check the key in Settings.",
        403 => "OpenAI does not allow audio transcription with this key.",
        429 => "OpenAI’s usage limit is preventing transcription.",
        400 or 404 => "OpenAI rejects the transcription model or the audio request. Check the model in Settings.",
        _ => "OpenAI transcription failed."
    };

    /// <summary>"OpenAI HTTP 401: OpenAI rejects the API key. Check the key in Settings."</summary>
    public static ChunkTranscriptionException StatusError(int status) =>
        new($"OpenAI HTTP {status}: {Explanation(status)}", status);

    public static void EnsureSuccess(HttpResponseMessage response)
    {
        if (!response.IsSuccessStatusCode) throw StatusError((int)response.StatusCode);
    }
}
