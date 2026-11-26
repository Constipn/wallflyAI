using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Wallfly.Core.Diagnostics;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Settings of <see cref="DeepgramPrerecordedDiarizer"/>. The key is not part of them (records print their members).</summary>
public sealed record DeepgramPrerecordedOptions
{
    /// <summary>
    /// The upload format when nothing else is chosen: <see cref="CloudUploadFormat.Auto"/>, i.e. uncompressed PCM16 and Ogg/Opus
    /// only on a slow connection (the user's decision of 25.11.2026). Measured live that day on a 23-minute meeting (Release):
    /// PCM16 43.5 MB took 13.6 s upload + 4.0 s processing = 17.6 s without CPU work; Ogg/Opus 48 kbit/s took 42.8 s encode
    /// (one core) + 1.9 s upload + 2.8 s = 47.5 s for 4.6 MB; both gave 3 speakers and agree on 94.9 % of the words.
    /// </summary>
    public const CloudUploadFormat DefaultUploadFormat = CloudUploadFormat.Auto;

    /// <summary>Deepgram's pre-recorded endpoint.</summary>
    public Uri Endpoint { get; init; } = new("https://api.deepgram.com/v1/listen");
    /// <summary>The batch model; nova-3 as live (keyterms are a Nova-3 feature).</summary>
    public string Model { get; init; } = "nova-3";
    /// <summary>
    /// The query parameter that switches the diarizer on. Deepgram's diarization page (developers.deepgram.com/docs/diarization,
    /// read 25.11.2026): <c>diarize</c> is deprecated and always routes to the v1 diarizer; <c>diarize_model</c> enables
    /// diarization and selects the model; a request that sets both is rejected. Never send both.
    /// </summary>
    public string DiarizerParameter { get; init; } = "diarize_model";
    /// <summary>Value of <see cref="DiarizerParameter"/>: <c>latest</c> resolves to the latest GA batch diarizer (v2 on 25.11.2026); <c>v1</c>/<c>v2</c> pin one.</summary>
    public string DiarizerValue { get; init; } = "latest";
    /// <summary><c>language</c> (e.g. <c>multi</c> or <c>de</c>); null leaves Deepgram's default. Supplied by the caller, as live.</summary>
    public string? Language { get; init; }
    /// <summary>Repeated <c>language_hint</c> (Deepgram applies them only with <c>language=multi</c> on Nova-3).</summary>
    public IReadOnlyList<string> LanguageHints { get; init; } = [];
    /// <summary>Repeated <c>keyterm</c> (Nova-3 only; plain terms, no weights).</summary>
    public IReadOnlyList<string> Keyterms { get; init; } = [];
    /// <summary>Further query parameters, appended as given (e.g. <c>mip_opt_out=true</c> once the user decides).</summary>
    public IReadOnlyList<KeyValuePair<string, string>> ExtraParameters { get; init; } = [];

    /// <summary>PCM16, Ogg/Opus, or PCM16 with the switch to Ogg/Opus on a slow connection (<see cref="DefaultUploadFormat"/>).</summary>
    public CloudUploadFormat UploadFormat { get; init; } = DefaultUploadFormat;
    /// <summary>
    /// <see cref="CloudUploadFormat.Auto"/>: the PCM upload is judged only after this long from its first byte. TCP slow start
    /// makes the first seconds look slower than the link and the socket's send buffer makes the first megabyte look faster;
    /// 8 s lies well past both, and is still under half of the whole PCM upload of the measured 23-minute meeting (13.6 s).
    /// </summary>
    public TimeSpan AutoProbeDuration { get; init; } = TimeSpan.FromSeconds(8);
    /// <summary><see cref="CloudUploadFormat.Auto"/>: and only after this many body bytes (1 MB, beyond a typical socket send buffer).</summary>
    public long AutoProbeBytes { get; init; } = 1_000_000;
    /// <summary>
    /// <see cref="CloudUploadFormat.Auto"/>: the PCM upload is abandoned for Ogg/Opus when the rest of it, projected at the
    /// measured rate, would take longer than this (and longer than the estimated Opus path). On the 25.11.2026 measurement the
    /// whole PCM upload took 13.6 s; a remaining 90 s means a link several times slower (about 0.5 MB/s for such a meeting),
    /// where the encode (about 2 min of one core per audio hour, at low priority) plus an upload 9× smaller is faster and frees
    /// the uplink sooner. Below it the lossless upload is kept: the gain would be small.
    /// </summary>
    public TimeSpan AutoSwitchAfter { get; init; } = TimeSpan.FromSeconds(90);
    /// <summary>
    /// <see cref="CloudUploadFormat.Auto"/>: the encode time assumed for the estimated Opus path. Measured 42.8 s for 23 minutes
    /// (112 s per audio hour, Release, 25.11.2026) and 96–144 s per hour in the Debug test (<c>OpusEncodeSpeed</c>); 150 s
    /// leaves room for a busy laptop, so the switch happens only when Opus is clearly faster.
    /// </summary>
    public double OpusEncodeSecondsPerAudioHour { get; init; } = 150;
    /// <summary>
    /// Opus bitrate in bit/s. 48 kbit/s (the plan's start value, about 22 MB per hour) is far above what wideband speech needs
    /// (Opus reaches transparent wideband speech around 24–32 kbit/s); Phase B decides whether it can go lower.
    /// </summary>
    public int OpusBitrate { get; init; } = 48_000;
    /// <summary>
    /// Opus encoder complexity 0–10; 10 is libopus' default. On the development laptop the complexity hardly changed the
    /// encode time of 16 kHz mono (<c>DeepgramDiarizerTests.OpusEncodeSpeed</c>, 25.11.2026: 91–124 s per audio hour at 5,
    /// 96–144 s at 10, one core, Debug test build), so the best quality setting costs nothing measurable.
    /// </summary>
    public int OpusComplexity { get; init; } = 10;
    /// <summary>Priority of the encoding thread (as the local diarizer's worker).</summary>
    public ThreadPriority EncoderPriority { get; init; } = ThreadPriority.BelowNormal;
    /// <summary>Directory of the temporary Ogg file (deleted on close in every path); null: the user's temp directory.</summary>
    public string? TemporaryDirectory { get; init; }
    /// <summary>
    /// Ogg/Opus only (explicit, or <see cref="CloudUploadFormat.Auto"/> after the switch): the encoded upload of this meeting is
    /// kept at this path (with a <c>.key</c> file naming the archives and encoder settings it was made from), so a later attempt
    /// of the same request, and the parameter fallbacks of one attempt, upload it again without encoding again (in Auto without
    /// a PCM probe either). A PCM upload writes nothing here. The caller deletes it when the request ends (<c>DiarizationStep</c>). Null:
    /// a self-deleting temporary file per run (<see cref="TemporaryDirectory"/>). A cache that cannot be written falls back to
    /// the temporary file.
    /// </summary>
    public string? UploadCachePath { get; init; }

    /// <summary>
    /// Time allowed for the answer once the upload is complete. Deepgram ends Nova requests that run longer than 10 minutes with
    /// 504 (pre-recorded audio page); 12 minutes leaves room for that answer to arrive. The upload itself has no total limit,
    /// only <see cref="UploadStallTimeout"/>.
    /// </summary>
    public TimeSpan ResponseBudget { get; init; } = TimeSpan.FromMinutes(12);
    /// <summary>An upload (or connection attempt) without progress for this long counts as a transient network failure.</summary>
    public TimeSpan UploadStallTimeout { get; init; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Consecutive words of one speaker separated by at most this gap form one segment; between different speakers within this
    /// gap the cut lies in the middle of the gap. The fusion's phantom rule drops identities whose longest run is ≤ 3 s, so
    /// one segment per word would erase real speakers; 2 s bridges normal pauses within a turn.
    /// </summary>
    public double SegmentJoinGap { get; init; } = 2.0;
    /// <summary>
    /// A word whose <c>speaker_confidence</c> is below this value does not open a run of its own speaker: it joins the run of
    /// the word before it (within <see cref="SegmentJoinGap"/>). 0 (default) is off: the value is to be tuned with real meetings
    /// (Phase B), and the fusion's smoothing already absorbs uncorroborated one- and two-word runs.
    /// </summary>
    public double MinSpeakerConfidence { get; init; }
    /// <summary>The answer's audio duration may differ from the uploaded timeline by this much (Opus frame padding is 20 ms).</summary>
    public double DurationTolerance { get; init; } = 1.0;
}

/// <summary>One word of the pre-recorded answer on the uploaded timeline (seconds), with its normalised token for the self-check.</summary>
public readonly record struct DeepgramWord(double Start, double End, int Speaker, double? Confidence, string Token);

/// <summary>A pre-recorded word in archive time (file of <see cref="DiarizationResult.Files"/>, 16 kHz frames).</summary>
public readonly record struct ArchiveWord(int File, long StartFrame, long EndFrame, int Speaker, double? Confidence, string Token);

/// <summary>
/// Speaker separation of the Others archives by Deepgram's pre-recorded API (plan Phase C, step 3). After the stop the
/// concatenated timeline (the one <see cref="ConcatenatedWav.ToArchiveSegments(IEnumerable{TimelineSegment})"/> maps back)
/// is uploaded in one request, as PCM16 WAV converted while sending or as Ogg/Opus encoded to a self-deleting temporary file
/// on a low-priority thread; by default (<see cref="CloudUploadFormat.Auto"/>) PCM16, switched to Ogg/Opus within the same
/// run when the measured throughput is too low (not an attempt, not a failure). No splitting, no silence cutting (speaker
/// numbers are valid per request only). The words'
/// speaker numbers become segments (<see cref="Segments"/>). Failures are <see cref="CloudDiarizationException"/>s classified
/// for "wait for network and retry" versus "give up".
/// </summary>
/// <remarks>
/// The result has no voice merge (<see cref="DiarizationResult.Unmerged"/>/<see cref="DiarizationResult.Voices"/> stay null),
/// so <see cref="TeamsVoiceGuard.Apply"/> leaves it unchanged; <see cref="DeepgramTeamsGuard.Apply"/> is the check for this
/// path. The words of the last run (<see cref="LastWords"/>) feed <see cref="DeepgramSelfCheck.Check(Wallfly.Core.Meeting, DiarizationResult, IReadOnlyList{ArchiveWord}, SelfCheckOptions?)"/>.
/// One run at a time per instance. Progress: an <see cref="IProgress{T}"/> of <see cref="CloudDiarizationProgress"/> passed as
/// the progress object gets the phases; a plain <c>IProgress&lt;double&gt;</c> gets encode 0–0.25 (Opus only), upload up
/// to 0.9, then 0.9 while Deepgram works, 1 at the end. When Auto switches, the phases go Uploading → Encoding → Uploading
/// (the new body from 0 %) → Processing; the plain value never goes back: encode and the new upload share what is left up to 0.9.
/// <para>
/// The Opus encoder's look-ahead (the OpusHead pre-skip, at most 312 samples at 48 kHz = 104 samples at 16 kHz = 6.5 ms) is
/// deliberately not compensated in the word times: a decoder that honours the pre-skip, as the Ogg/Opus mapping requires,
/// removes it, and one that does not shifts every word by at most 6.5 ms, far below the 0.25 s the self-check allows and the
/// 2-s join gap of the segments (<c>DeepgramDiarizerTests.TheOpusBodyIsAnOggStreamOfTheTimelineDuration</c> asserts the bound).
/// </para>
/// </remarks>
public sealed class DeepgramPrerecordedDiarizer : ConcatenatingDiarizer, IDisposable
{
    public const string EngineName = "deepgram-prerecorded";
    /// <summary>Share of the plain progress for the Opus encode and the end of the upload share.</summary>
    public const double EncodeShare = 0.25, UploadEnd = 0.9;

    private readonly string apiKey;
    private readonly HttpClient http;

    /// <param name="apiKey">The Deepgram key (never logged).</param>
    /// <param name="handler">Test seam or a shared handler (not disposed here); null creates an own handler.</param>
    public DeepgramPrerecordedDiarizer(string apiKey, DeepgramPrerecordedOptions? options = null, HttpMessageHandler? handler = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(apiKey);
        this.apiKey = apiKey.Trim();
        Options = options ?? new DeepgramPrerecordedOptions();
        http = handler is null
            ? new HttpClient(new SocketsHttpHandler { PooledConnectionLifetime = TimeSpan.FromMinutes(5) }, disposeHandler: true)
            : new HttpClient(handler, disposeHandler: false);
        // The 100-s default would end every longer upload; the budgets in the options apply instead.
        http.Timeout = Timeout.InfiniteTimeSpan;
    }

    public DeepgramPrerecordedOptions Options { get; }
    /// <summary>The clock of the Auto throughput probe (a test seam); the stall and answer timers always run on real time.</summary>
    internal TimeProvider Time { get; init; } = TimeProvider.System;
    public override string Engine => EngineName;
    /// <summary>
    /// Model, diarizer and the upload format: the one actually sent by the last run (<see cref="LastUploadFormat"/>:
    /// <c>pcm16</c> or <c>opus48k</c>), before any request the configured one (<c>auto</c> for <see cref="CloudUploadFormat.Auto"/>).
    /// </summary>
    public override string? ModelVersion => $"{Options.Model}/{Options.DiarizerParameter}={Options.DiarizerValue}/{FormatLabel(LastUploadFormat ?? Options.UploadFormat)}";
    public override double? Threshold => null;

    private string FormatLabel(CloudUploadFormat format) => format switch
    {
        CloudUploadFormat.OggOpus => $"opus{Options.OpusBitrate / 1000}k",
        CloudUploadFormat.Pcm16Wav => "pcm16",
        _ => "auto"
    };

    /// <summary>The format of the last request body sent (null before the first request of a run).</summary>
    public CloudUploadFormat? LastUploadFormat { get; private set; }
    /// <summary>The last run abandoned its PCM upload for Ogg/Opus (<see cref="CloudUploadFormat.Auto"/> on a slow connection).</summary>
    public bool LastUploadSwitched { get; private set; }

    /// <summary>The words of the last successful run in archive time (null before), for <see cref="DeepgramSelfCheck"/>.</summary>
    public IReadOnlyList<ArchiveWord>? LastWords { get; private set; }
    /// <summary>Diagnostics of the last run (numbers only).</summary>
    public DeepgramRunStatistics? LastStatistics { get; private set; }
    /// <summary>The last run uploaded the encoded file kept from an earlier attempt (<see cref="DeepgramPrerecordedOptions.UploadCachePath"/>) instead of encoding.</summary>
    public bool LastUploadReused { get; private set; }
    /// <summary>The parameter level of the last request sent (<see cref="CloudRequestLevel"/>).</summary>
    public CloudRequestLevel LastRequestLevel { get; private set; }

    /// <summary>The request address: model, diarizer, language, hints, keyterms and extras, each value escaped.</summary>
    public Uri RequestUri() => RequestUri(CloudRequestLevel.Full);

    /// <summary>The request address of a parameter level: <see cref="CloudRequestLevel.WithoutLanguageHints"/> drops the hints, <see cref="CloudRequestLevel.Bare"/> the keyterms too.</summary>
    public Uri RequestUri(CloudRequestLevel level)
    {
        var query = new List<KeyValuePair<string, string>> { new("model", Options.Model), new(Options.DiarizerParameter, Options.DiarizerValue) };
        if (!string.IsNullOrWhiteSpace(Options.Language)) query.Add(new("language", Options.Language.Trim()));
        if (level == CloudRequestLevel.Full)
            query.AddRange(Options.LanguageHints.Where(hint => !string.IsNullOrWhiteSpace(hint)).Select(hint => new KeyValuePair<string, string>("language_hint", hint.Trim())));
        if (level != CloudRequestLevel.Bare)
            query.AddRange(Options.Keyterms.Where(term => !string.IsNullOrWhiteSpace(term)).Select(term => new KeyValuePair<string, string>("keyterm", term.Trim())));
        query.AddRange(Options.ExtraParameters);
        var text = string.Join('&', query.Select(pair => Uri.EscapeDataString(pair.Key) + "=" + Uri.EscapeDataString(pair.Value)));
        return new UriBuilder(Options.Endpoint) { Query = text }.Uri;
    }

    /// <summary>
    /// The parameter levels tried in order when Deepgram rejects a request with 400 or 422 (a hint or keyterm it does not
    /// accept): the full request, then without language hints (keyterms kept), then without keyterms too. Levels that would
    /// send the same request as the one before are left out; a rejection of the last level is permanent.
    /// </summary>
    public IReadOnlyList<CloudRequestLevel> RequestLevels()
    {
        var hints = Options.LanguageHints.Any(hint => !string.IsNullOrWhiteSpace(hint));
        var terms = Options.Keyterms.Any(term => !string.IsNullOrWhiteSpace(term));
        var levels = new List<CloudRequestLevel> { CloudRequestLevel.Full };
        if (hints && terms) levels.Add(CloudRequestLevel.WithoutLanguageHints);
        if (hints || terms) levels.Add(CloudRequestLevel.Bare);
        return levels;
    }

    protected override async Task<IReadOnlyList<TimelineSegment>> DiarizeAsync(ConcatenatedWav audio, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var mode = Options.UploadFormat;
        var reporter = new Reporter(progress, mode == CloudUploadFormat.OggOpus);
        var clock = Stopwatch.StartNew();
        TimeSpan encodeTime = TimeSpan.Zero;
        FileStream? encoded = null;
        UploadProbe? abandoned = null;
        LastUploadReused = false;
        LastUploadSwitched = false;
        LastUploadFormat = null;
        try
        {
            if (mode == CloudUploadFormat.OggOpus)
            {
                (encoded, LastUploadReused) = await OpenEncodedAsync(audio, reporter, cancellationToken).ConfigureAwait(false);
                encodeTime = LastUploadReused ? TimeSpan.Zero : clock.Elapsed;
            }
            else if (mode == CloudUploadFormat.Auto && OpenCached(audio) is { } cached)
            {
                // An earlier attempt of this request already switched to Opus on a slow link: send that file, no PCM probe.
                encoded = cached;
                LastUploadReused = true;
            }
            // The same body (the encoded file, or the PCM converted again) for every parameter level of this attempt.
            var levels = RequestLevels();
            for (var index = 0; ;)
            {
                var level = LastRequestLevel = levels[index];
                var monitor = new UploadMonitor();
                HttpContent content = encoded is not null ? new MonitoredStreamContent(encoded, "audio/ogg", monitor) : new Pcm16WavContent(audio, monitor);
                var probe = encoded is null && mode == CloudUploadFormat.Auto ? new UploadProbe(Options, audio.Seconds, Time) : null;
                LastUploadFormat = encoded is not null ? CloudUploadFormat.OggOpus : CloudUploadFormat.Pcm16Wav;
                var uploadStart = clock.Elapsed;
                TimeSpan? uploadDone = null;
                monitor.Written = sent => reporter.Uploading(sent, monitor.Total);
                monitor.Finished = () => { uploadDone = clock.Elapsed; reporter.Processing(monitor.Sent, monitor.Total); };
                JsonDocument document;
                try { document = await SendAsync(content, monitor, RequestUri(level), probe, cancellationToken).ConfigureAwait(false); }
                catch (CloudDiarizationException rejected) when (rejected.StatusCode is 400 or 422 && index + 1 < levels.Count)
                {
                    // A parameter Deepgram does not accept (a hint, a keyterm): degrade instead of failing for good. Counts only.
                    WallflyLog.Info(string.Create(CultureInfo.InvariantCulture,
                        $"Wallfly: cloud diarization request rejected httpStatus={rejected.StatusCode} level={level}; retrying level={levels[index + 1]} hints={(levels[index + 1] == CloudRequestLevel.Full ? Options.LanguageHints.Count : 0)} keyterms={(levels[index + 1] == CloudRequestLevel.Bare ? 0 : Options.Keyterms.Count)}"));
                    index++;
                    continue;
                }
                catch (UploadSwitchException) when (probe is not null)
                {
                    // The link is too slow for PCM: compress and send the same level again. Part of this attempt (not counted,
                    // not a failure); the encoded file goes to the upload cache, so a later attempt skips the probe. Counts only.
                    abandoned = probe;
                    LastUploadSwitched = true;
                    WallflyLog.Info(string.Create(CultureInfo.InvariantCulture,
                        $"Wallfly: cloud diarization upload switched to opus: sent={probe.Sent} of {monitor.Total ?? 0} bytes in {probe.Elapsed.TotalSeconds:0.0}s rate={(probe.BytesPerSecond ?? 0) / 1000:0.0}kB/s projected={probe.Projected?.TotalSeconds ?? 0:0}s opusEstimate={probe.OpusEstimate(probe.BytesPerSecond ?? 1).TotalSeconds:0}s audio={audio.Seconds:0.0}s"));
                    reporter.Switched();
                    var encodeStart = clock.Elapsed;
                    (encoded, LastUploadReused) = await OpenEncodedAsync(audio, reporter, cancellationToken).ConfigureAwait(false);
                    encodeTime = LastUploadReused ? TimeSpan.Zero : clock.Elapsed - encodeStart;
                    continue;
                }
                finally
                {
                    monitor.Written = null;
                    monitor.Finished = null;
                }
                using (document)
                {
                    var (words, duration, requestId) = Parse(document.RootElement);
                    if (duration is double reported && Math.Abs(reported - audio.Seconds) > Options.DurationTolerance)
                        throw CloudDiarizationException.Untrustworthy(CloudFailureReason.DurationMismatch,
                            string.Create(CultureInfo.InvariantCulture, $"audio duration {reported:0.0} s instead of {audio.Seconds:0.0} s"));
                    var segments = Segments(words, Options.SegmentJoinGap, Options.MinSpeakerConfidence);
                    LastWords = ToArchiveWords(words, audio);
                    var upload = (uploadDone ?? clock.Elapsed) - uploadStart;
                    LastStatistics = new DeepgramRunStatistics(requestId, LastUploadFormat.Value, monitor.Sent, audio.Seconds, encodeTime, upload,
                        clock.Elapsed - (uploadDone ?? clock.Elapsed), words.Count, words.Select(word => word.Speaker).Distinct().Count(), segments.Count)
                    {
                        Level = level, UploadReused = LastUploadReused, Mode = mode, Switched = abandoned is not null,
                        ProbeBytes = abandoned?.Sent, ProbeTime = abandoned?.Elapsed, ProbeBytesPerSecond = abandoned?.BytesPerSecond
                    };
                    WallflyLog.Info(LastStatistics.Describe());
                    reporter.Done();
                    return segments;
                }
            }
        }
        finally
        {
            // The temporary file is DeleteOnClose (it disappears with its handle, also when the process dies); the cache stays.
            if (encoded is not null) await encoded.DisposeAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The encoded upload, open for reading from position 0: the cached file of an earlier attempt when its key still matches
    /// the archives and encoder settings, otherwise encoded now (into the cache, or a self-deleting temporary file).
    /// </summary>
    private async Task<(FileStream Stream, bool Reused)> OpenEncodedAsync(ConcatenatedWav audio, Reporter reporter, CancellationToken cancellationToken)
    {
        if (OpenCached(audio) is { } cached) return (cached, true);
        if (Options.UploadCachePath is { Length: > 0 } cache)
        {
            var key = CacheKey(audio);
            var keyPath = cache + ".key";
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(cache))!);
                File.Delete(keyPath);
                var partial = cache + ".partial";
                await using (var output = new FileStream(partial, FileMode.Create, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.SequentialScan))
                {
                    try { await EncodeAsync(audio, output, reporter, cancellationToken).ConfigureAwait(false); }
                    catch
                    {
                        await output.DisposeAsync().ConfigureAwait(false);
                        TryDelete(partial);
                        throw;
                    }
                }
                File.Move(partial, cache, overwrite: true);
                File.WriteAllText(keyPath, key);
                return (new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan), false);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                // The cache is an optimisation: encode into a temporary file instead (no path in the log).
                WallflyLog.Info("Wallfly: cloud diarization upload cache unavailable: " + error.GetType().Name);
            }
        }
        var temporary = CreateTemporaryFile();
        try
        {
            await EncodeAsync(audio, temporary, reporter, cancellationToken).ConfigureAwait(false);
            return (temporary, false);
        }
        catch
        {
            await temporary.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>The cached encoded upload of an earlier attempt, open from position 0, when its key still matches; otherwise null.</summary>
    private FileStream? OpenCached(ConcatenatedWav audio)
    {
        if (Options.UploadCachePath is not { Length: > 0 } cache) return null;
        var keyPath = cache + ".key";
        try
        {
            return File.Exists(cache) && File.Exists(keyPath) && File.ReadAllText(keyPath) == CacheKey(audio)
                ? new FileStream(cache, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 16, FileOptions.SequentialScan)
                : null;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            WallflyLog.Info("Wallfly: cloud diarization upload cache unavailable: " + error.GetType().Name);
            return null;
        }
    }

    private Task EncodeAsync(ConcatenatedWav audio, Stream output, Reporter reporter, CancellationToken cancellationToken)
    {
        reporter.Encoding(0);
        return OggOpusEncoder.EncodeAsync(audio, output, Options.OpusBitrate, Options.OpusComplexity, Options.EncoderPriority, reporter.Encoding, cancellationToken);
    }

    /// <summary>What the cached upload was made from: format, encoder settings and each archive's name, length and last write.</summary>
    private string CacheKey(ConcatenatedWav audio) => string.Join('|', new[] { "opus-v1", Options.OpusBitrate.ToString(CultureInfo.InvariantCulture),
            Options.OpusComplexity.ToString(CultureInfo.InvariantCulture) }
        .Concat(audio.Parts.Select(part => string.Create(CultureInfo.InvariantCulture,
            $"{Path.GetFileName(part.Path)}:{part.FrameCount}:{part.Start}:{File.GetLastWriteTimeUtc(part.Path).Ticks}"))));

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }

    private FileStream CreateTemporaryFile()
    {
        var directory = Options.TemporaryDirectory ?? Path.GetTempPath();
        var path = Path.Combine(directory, $"wallfly-upload-{Guid.NewGuid():N}.opus");
        return new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 1 << 16, FileOptions.DeleteOnClose | FileOptions.SequentialScan);
    }

    /// <summary>
    /// Sends the body and reads the JSON answer. A stalled upload and an answer later than <see cref="DeepgramPrerecordedOptions.ResponseBudget"/>
    /// after the upload are transient timeouts; the caller's token ends the run with <see cref="OperationCanceledException"/>.
    /// </summary>
    /// <remarks>
    /// One deadline, two phases. While bytes remain unsent only the stall watchdog runs (re-armed by every write); once the
    /// last byte is out, or the answer has begun (whichever comes first), only the response budget runs and no later write
    /// re-arms the watchdog. The timeout names the phase that was running, so a finished upload waiting for a slow answer is
    /// never reported as a stalled upload.
    /// <para>
    /// With a <paramref name="probe"/> (a PCM upload in <see cref="CloudUploadFormat.Auto"/>) every write is also measured; when
    /// the probe asks for Ogg/Opus while the body is still being sent, the request is cancelled on purpose and ends with
    /// <see cref="UploadSwitchException"/> instead of a timeout or network failure (the caller's cancellation still wins).
    /// </para>
    /// </remarks>
    private async Task<JsonDocument> SendAsync(HttpContent content, UploadMonitor monitor, Uri address, UploadProbe? probe, CancellationToken cancellationToken)
    {
        using var switching = new CancellationTokenSource();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, switching.Token);
        var gate = new object();
        var answering = false;
        void Arm(TimeSpan after) { try { deadline.CancelAfter(after); } catch (ObjectDisposedException) { } }
        void Stall() { lock (gate) { if (!answering) Arm(Options.UploadStallTimeout); } }
        void Answer() { lock (gate) { if (answering) return; answering = true; Arm(Options.ResponseBudget); } }
        bool Uploading() { lock (gate) return !answering; }
        void Switch() { try { switching.Cancel(); } catch (ObjectDisposedException) { } }
        var written = monitor.Written;
        var finished = monitor.Finished;
        monitor.Written = sent =>
        {
            Stall();
            if (probe is not null && !switching.IsCancellationRequested && Uploading() && probe.Observe(sent, monitor.Total ?? sent))
            {
                Switch();
                return;
            }
            written?.Invoke(sent);
        };
        monitor.Finished = () => { Answer(); finished?.Invoke(); };
        Stall();
        using var request = new HttpRequestMessage(HttpMethod.Post, address) { Content = content };
        request.Headers.Authorization = new AuthenticationHeaderValue("Token", apiKey);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        try
        {
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token).ConfigureAwait(false);
            var status = (int)response.StatusCode;
            if (status is < 200 or > 299)
            {
                var retry = response.Headers.RetryAfter is { } header
                    ? header.Delta ?? (header.Date is { } date ? date - DateTimeOffset.UtcNow : null)
                    : null;
                throw CloudDiarizationException.ForStatus(status, retry is { } wait && wait > TimeSpan.Zero ? wait : null);
            }
            // The answer may arrive before the stack reports the end of the body (a small body sent in one write).
            Answer();
            await using var stream = await response.Content.ReadAsStreamAsync(deadline.Token).ConfigureAwait(false);
            return await JsonDocument.ParseAsync(stream, cancellationToken: deadline.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception error) when (switching.IsCancellationRequested && !cancellationToken.IsCancellationRequested && error is not CloudDiarizationException)
        {
            // Abandoned on purpose (however the stack reports the aborted body): neither a stall nor a network failure.
            throw new UploadSwitchException();
        }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            throw CloudDiarizationException.Timeout(uploading: Uploading());
        }
        catch (JsonException error)
        {
            throw CloudDiarizationException.Malformed("not JSON", error);
        }
        catch (Exception error) when (monitor.Failure is { } failure && error is not CloudDiarizationException)
        {
            // The body could not be produced (an archive became unreadable): not a network problem.
            throw failure is DiarizationException ? failure : DiarizationException.UnreadableAudio("archive", failure.GetType().Name);
        }
        catch (Exception error) when (error is HttpRequestException or IOException)
        {
            throw CloudDiarizationException.Network(error);
        }
        finally
        {
            monitor.Written = written;
            monitor.Finished = finished;
        }
    }

    /// <summary>
    /// The words of <c>results.channels[0].alternatives[0].words</c> (start, end, speaker, speaker_confidence, word) plus
    /// <c>metadata.duration</c> and <c>metadata.request_id</c>. Missing structure, a word without times or a word without a
    /// speaker number (diarization did not run) is a malformed answer.
    /// </summary>
    public static (List<DeepgramWord> Words, double? Duration, string? RequestId) Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw CloudDiarizationException.Malformed("no object");
        double? duration = null;
        string? requestId = null;
        if (root.TryGetProperty("metadata", out var metadata) && metadata.ValueKind == JsonValueKind.Object)
        {
            if (metadata.TryGetProperty("duration", out var value) && value.ValueKind == JsonValueKind.Number) duration = value.GetDouble();
            if (metadata.TryGetProperty("request_id", out var id) && id.ValueKind == JsonValueKind.String) requestId = id.GetString();
        }
        if (!root.TryGetProperty("results", out var results) || !results.TryGetProperty("channels", out var channels)
            || channels.ValueKind != JsonValueKind.Array || channels.GetArrayLength() == 0
            || !channels[0].TryGetProperty("alternatives", out var alternatives) || alternatives.ValueKind != JsonValueKind.Array
            || alternatives.GetArrayLength() == 0)
            throw CloudDiarizationException.Malformed("no transcript");
        var words = new List<DeepgramWord>();
        if (!alternatives[0].TryGetProperty("words", out var list) || list.ValueKind != JsonValueKind.Array) return (words, duration, requestId);
        foreach (var word in list.EnumerateArray())
        {
            if (!Number(word, "start", out var start) || !Number(word, "end", out var end) || !double.IsFinite(start) || !double.IsFinite(end))
                throw CloudDiarizationException.Malformed("word without times");
            if (!word.TryGetProperty("speaker", out var speaker) || speaker.ValueKind != JsonValueKind.Number || !speaker.TryGetInt32(out var id))
                throw CloudDiarizationException.Malformed("word without speaker");
            double? confidence = Number(word, "speaker_confidence", out var value) ? value : null;
            var text = word.TryGetProperty("word", out var token) && token.ValueKind == JsonValueKind.String ? token.GetString() ?? "" : "";
            words.Add(new DeepgramWord(start, Math.Max(start, end), id, confidence, DeepgramSelfCheck.Normalize(text)));
        }
        return (words, duration, requestId);

        static bool Number(JsonElement element, string name, out double value)
        {
            value = 0;
            if (!element.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Number) return false;
            value = property.GetDouble();
            return true;
        }
    }

    /// <summary>
    /// Segments from the words' speaker numbers: consecutive words of one speaker with gaps up to <paramref name="joinGap"/>
    /// form one segment; between two speakers whose words are at most <paramref name="joinGap"/> apart the cut lies in the
    /// middle of the gap (overlapping words: in the middle of the overlap), longer gaps stay uncovered. A word below
    /// <paramref name="minConfidence"/> continues the previous word's run instead of opening its own. Never one segment per word.
    /// </summary>
    public static List<TimelineSegment> Segments(IEnumerable<DeepgramWord> words, double joinGap = 2.0, double minConfidence = 0)
    {
        var ordered = words.Where(word => double.IsFinite(word.Start) && double.IsFinite(word.End))
            .OrderBy(word => word.Start).ThenBy(word => word.End).ToList();
        var runs = new List<(int Speaker, double Start, double End)>();
        foreach (var word in ordered)
        {
            var end = Math.Max(word.Start, word.End);
            if (runs.Count > 0)
            {
                var last = runs[^1];
                var close = word.Start - last.End <= joinGap + 1e-9;
                var weak = minConfidence > 0 && word.Confidence is double confidence && confidence < minConfidence;
                if (close && (last.Speaker == word.Speaker || weak))
                {
                    runs[^1] = last with { End = Math.Max(last.End, end) };
                    continue;
                }
            }
            runs.Add((word.Speaker, word.Start, end));
        }
        for (var index = 1; index < runs.Count; index++)
        {
            var (previous, current) = (runs[index - 1], runs[index]);
            if (previous.Speaker == current.Speaker || current.Start - previous.End > joinGap + 1e-9) continue;
            var cut = Math.Clamp((previous.End + current.Start) / 2, previous.Start, Math.Max(previous.Start, current.End));
            runs[index - 1] = previous with { End = cut };
            runs[index] = current with { Start = cut };
        }
        return runs.Where(run => run.End > run.Start).Select(run => new TimelineSegment(run.Speaker, run.Start, run.End)).ToList();
    }

    /// <summary>Timeline words → archive words (the file holding the word's start, else its end; words in a separator are dropped).</summary>
    public static List<ArchiveWord> ToArchiveWords(IEnumerable<DeepgramWord> words, ConcatenatedWav audio) => ToArchiveWords(words, audio.Parts);

    internal static List<ArchiveWord> ToArchiveWords(IEnumerable<DeepgramWord> words, IReadOnlyList<ConcatenatedWav.Part> parts)
    {
        var result = new List<ArchiveWord>();
        foreach (var word in words)
        {
            var start = (long)Math.Round(word.Start * DiarizationTimeline.SampleRate, MidpointRounding.AwayFromZero);
            var end = (long)Math.Round(word.End * DiarizationTimeline.SampleRate, MidpointRounding.AwayFromZero);
            var file = Find(start) ?? Find(Math.Max(start, end - 1));
            if (file is not int index) continue;
            var part = parts[index];
            result.Add(new ArchiveWord(index, Math.Clamp(start - part.Start, 0, part.FrameCount), Math.Clamp(end - part.Start, 0, part.FrameCount),
                word.Speaker, word.Confidence, word.Token));
        }
        return result;

        int? Find(long frame)
        {
            for (var index = 0; index < parts.Count; index++)
                if (parts[index].FrameCount > 0 && frame >= parts[index].Start && frame < parts[index].Start + parts[index].FrameCount) return index;
            return null;
        }
    }

    public void Dispose() => http.Dispose();

    /// <summary>Maps the run's phases onto the caller's progress object (see the class remarks).</summary>
    private sealed class Reporter(IProgress<double>? plain, bool encodes)
    {
        private readonly IProgress<CloudDiarizationProgress>? detailed = plain as IProgress<CloudDiarizationProgress>;
        private bool encodes = encodes;
        /// <summary>The plain value reached when the upload switched to Opus; encode and the new upload continue from it.</summary>
        private double floor;
        private double lastOverall;
        private int lastPermille = -1;
        private CloudDiarizationPhase? lastPhase;

        public void Encoding(double fraction) => Report(CloudDiarizationPhase.Encoding, fraction, 0, null, EncodeShare * fraction);

        public void Uploading(long sent, long? total)
        {
            var fraction = total is long size && size > 0 ? Math.Clamp((double)sent / size, 0, 1) : (double?)null;
            var start = encodes ? EncodeShare : 0;
            Report(CloudDiarizationPhase.Uploading, fraction, sent, total, fraction is double share ? start + (UploadEnd - start) * share : null);
        }

        /// <summary>
        /// The PCM upload was abandoned for Ogg/Opus: the phases start again with Encoding (a phase change the caller shows as
        /// "Preparing upload…"), the plain value keeps what it reached and maps encode and the new upload onto the rest up to <see cref="UploadEnd"/>.
        /// </summary>
        public void Switched()
        {
            encodes = true;
            floor = lastOverall;
        }

        public void Processing(long sent, long? total) => Report(CloudDiarizationPhase.Processing, null, sent, total, UploadEnd);

        public void Done()
        {
            try { plain?.Report(1); }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }

        private void Report(CloudDiarizationPhase phase, double? fraction, long sent, long? total, double? overall)
        {
            // At most one report per phase change and per 0.1 % (an hour of Opus is about 330 upload chunks).
            var permille = (int)Math.Round((fraction ?? 0) * 1000);
            if (phase == lastPhase && permille == lastPermille) return;
            lastPhase = phase; lastPermille = permille;
            try
            {
                if (detailed is not null) detailed.Report(new CloudDiarizationProgress(phase, fraction, sent, total));
                else if (overall is double value)
                {
                    // Exact at both ends: unchanged without a switch, UploadEnd stays UploadEnd.
                    lastOverall = floor <= 0 || value >= UploadEnd ? value : Math.Min(UploadEnd, floor + (1 - floor / UploadEnd) * value);
                    plain?.Report(lastOverall);
                }
            }
            catch (Exception error) when (error is not OutOfMemoryException) { }
        }
    }
}

/// <summary>The query parameters a cloud request carries beyond model, diarizer and language (<see cref="DeepgramPrerecordedDiarizer.RequestLevels"/>).</summary>
public enum CloudRequestLevel
{
    /// <summary>Language hints and keyterms as the options give them.</summary>
    Full,
    /// <summary>Without language hints, keyterms kept (Deepgram rejected the full request).</summary>
    WithoutLanguageHints,
    /// <summary>Neither hints nor keyterms.</summary>
    Bare
}

/// <summary>Numbers of one cloud run for the log (no text, no names, no key).</summary>
/// <param name="Format">The format actually uploaded in the answered request (<see cref="CloudUploadFormat.Pcm16Wav"/> or <see cref="CloudUploadFormat.OggOpus"/>).</param>
public sealed record DeepgramRunStatistics(string? RequestId, CloudUploadFormat Format, long UploadBytes, double AudioSeconds, TimeSpan Encode, TimeSpan Upload,
    TimeSpan Processing, int Words, int Speakers, int Segments)
{
    /// <summary>The parameter level of the request that was answered.</summary>
    public CloudRequestLevel Level { get; init; }
    /// <summary>The encoded upload of an earlier attempt was sent again (no encode in this run).</summary>
    public bool UploadReused { get; init; }
    /// <summary>The configured upload format (<see cref="CloudUploadFormat.Auto"/> by default).</summary>
    public CloudUploadFormat Mode { get; init; }
    /// <summary>The PCM upload was abandoned for Ogg/Opus in this run (slow connection).</summary>
    public bool Switched { get; init; }
    /// <summary>PCM bytes sent before the switch (null without one).</summary>
    public long? ProbeBytes { get; init; }
    /// <summary>Time from the first PCM byte to the switch (null without one).</summary>
    public TimeSpan? ProbeTime { get; init; }
    /// <summary>The PCM throughput measured before the switch, bytes per second (null without one).</summary>
    public double? ProbeBytesPerSecond { get; init; }

    /// <summary>Throughput of the answered upload in bytes per second (null when it took no measurable time).</summary>
    public double? BytesPerSecond => Upload > TimeSpan.Zero ? UploadBytes / Upload.TotalSeconds : null;

    private static string Label(CloudUploadFormat format) => format switch
    {
        CloudUploadFormat.OggOpus => "opus",
        CloudUploadFormat.Pcm16Wav => "pcm16",
        _ => "auto"
    };

    private static string Rate(double? bytesPerSecond) => bytesPerSecond is double rate ? string.Create(CultureInfo.InvariantCulture, $"{rate / 1000:0.0}kB/s") : "-";

    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"Wallfly: cloud diarization request={RequestId ?? "-"} format={Label(Format)} mode={Label(Mode)} switched={Switched} bytes={UploadBytes} audio={AudioSeconds:0.0}s encode={Encode.TotalSeconds:0.0}s reused={UploadReused} upload={Upload.TotalSeconds:0.0}s rate={Rate(BytesPerSecond)} ")
        + (Switched ? string.Create(CultureInfo.InvariantCulture, $"probeBytes={ProbeBytes ?? 0} probe={ProbeTime?.TotalSeconds ?? 0:0.0}s probeRate={Rate(ProbeBytesPerSecond)} ") : "")
        + string.Create(CultureInfo.InvariantCulture, $"processing={Processing.TotalSeconds:0.0}s level={Level} words={Words} speakers={Speakers} segments={Segments}");
}
