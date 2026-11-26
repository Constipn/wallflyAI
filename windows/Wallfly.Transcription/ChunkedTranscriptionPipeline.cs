using Wallfly.Audio;

namespace Wallfly.Transcription;

/// <summary>User-visible wording of the chunk pipeline; English, see the UI language decision.</summary>
public static class TranscriptionMessages
{
    public const string BufferFull = "Audio buffer full. Recording is ending; the transcript stays incomplete.";
    /// <summary>macOS: "Transcription can no longer keep up with the meeting. The recording was stopped."</summary>
    public const string BacklogFull = "Transcription can no longer keep up with the meeting (too many passages waiting). The recording is ending.";
    public const string ProcessingFailed = "Audio processing failed. Check the Windows audio device and free disk space.";
    public const string TranscriptionTimeout = "The transcription request timed out.";
    public const string TranscriptionFailed = "Transcription failed: ";
    public const string NetworkOrResponse = "Network error or invalid API response.";
    /// <summary>Recovery sentence when Keep audio is on (macOS: "The saved audio stays on this Mac for recovery.").</summary>
    public const string AudioKept = "The saved audio remains on this PC.";
    /// <summary>Recovery sentence when Keep audio is off (macOS: "This audio was not saved.").</summary>
    public const string AudioNotKept = "This audio was not retained because Keep audio is off.";
}

/// <summary>Per-pipeline switches of <see cref="ChunkedTranscriptionPipeline"/>.</summary>
public sealed record ChunkedPipelineOptions
{
    /// <summary>Ask the backend for word times on final passages (Teams speaker names).</summary>
    public bool WordTimestamps { get; init; }
    /// <summary>Send a provisional request every 2 s of speech. Off only for the text-only <see cref="ITranscriptionClient"/> path.</summary>
    public bool Previews { get; init; } = true;
    /// <summary>Absolute deadline of one preview request (macOS <c>previewTimeout</c>).</summary>
    public TimeSpan PreviewTimeout { get; init; } = TimeSpan.FromSeconds(4);
    /// <summary>Open requests (queued plus in flight) at which the recording can no longer keep up.</summary>
    public int MaximumBacklog { get; init; } = ChunkedTranscriptionPipeline.DefaultMaximumBacklog;
    /// <summary>Whether the original audio is archived: picks the recovery sentence of a failure; null adds none.</summary>
    public bool? AudioRetained { get; init; }
    public TimeProvider? Time { get; init; }
    /// <summary>False in tests that drive the idle check through <see cref="ChunkedTranscriptionPipeline.CheckIdle"/>.</summary>
    public bool AutomaticIdleTimer { get; init; } = true;
    /// <summary>Dispose the backend when the pipeline finishes or is cancelled (it owns it, e.g. a local-server lease).</summary>
    public bool DisposeTranscriber { get; init; }
}

/// <summary>
/// RMS-chunked HTTP transcription (OpenAI or local whisper-server), a port of the macOS
/// <c>ChunkedTranscriptionPipeline.swift</c> with the engine's 16 kHz mono float frames as input (archiving and
/// anchors are done by the recording session). Rules (spec 4.2):
/// <list type="bullet">
/// <item>speech is a 20 ms window with RMS &gt;= 0.001778 (-55 dBFS); windows continue across the engine's 10 ms frames;</item>
/// <item>a passage is finalized at 4 s, or at &gt;= 0.5 s once 0.35 s of trailing silence followed; silent passages are never sent;</item>
/// <item>while speech continues, every further 2 s send a preview of the whole passage so far (same segment id,
/// <see cref="TranscriptionSegment.IsFinal"/> false), only while fewer than 2 requests are open, with a 4 s deadline;
/// a failed or late preview is dropped silently and never retried;</item>
/// <item>0.4 s without incoming audio, or a timeline jump of more than 1.5 samples, finalizes the passage;</item>
/// <item>requests of one channel run strictly one after another; at 10 open requests the recording can no longer keep up;</item>
/// <item>a failed final passage fails the pipeline (the recording stops) with a message saying where the audio is.</item>
/// </list>
/// </summary>
public sealed class ChunkedTranscriptionPipeline : ITranscriptionPipeline
{
    public const double SpeechRms = .001778;
    public const double WindowSeconds = .02;
    public const double MaximumChunkSeconds = 4;
    public const double PreviewSeconds = 2;
    public const double MinimumChunkSeconds = .5;
    public const double TrailingSilenceSeconds = .35;
    public const double IdleFlushSeconds = .4;
    public const double IdleTimerPeriodSeconds = .15;
    /// <summary>Sample-exact engine timestamps: even a short explicit gap splits a passage so word times stay true.</summary>
    public const double GapToleranceSeconds = 1.5 / AudioFormat.SampleRate;
    public const int DefaultMaximumBacklog = 10;
    public const int MaximumOpenForPreview = 2;

    private const int SampleRate = AudioFormat.SampleRate;
    private const int WindowSamples = SampleRate / 50;
    private const int MaximumSamples = SampleRate * 4;
    private const int PreviewSamples = SampleRate * 2;
    private const int MinimumSamples = SampleRate / 2;
    private const int SilenceSamples = SampleRate * 35 / 100;
    private const float SpeechThreshold = (float)SpeechRms;

    private sealed record Job(Guid SegmentId, byte[] Wav, double Offset, double Duration, bool IsFinal);

    private readonly object gate = new();
    private readonly IChunkTranscriber transcriber;
    private readonly ChunkedPipelineOptions options;
    private readonly bool wordTimestamps;
    private readonly TimeProvider time;
    private readonly Guid sessionId;
    private readonly CancellationTokenSource cancellation = new();
    private readonly ITimer? idleTimer;
    // Everything below is guarded by gate.
    private readonly List<float> samples = new(SampleRate * 4);
    private double chunkOffset;
    private double? expectedOffset;
    private long lastArrival;
    private Guid segmentId = Guid.NewGuid();
    private int lastPreviewSampleCount;
    private bool containsSpeech;
    private int trailingSilence;
    private int windowCount;
    private float windowEnergy;
    private int open;
    private Task tail = Task.CompletedTask;
    private bool stopped;
    private bool cancelled;
    private bool failed;
    private string? failureReason;
    private string? unpublishedFailure;
    private int transcriberReleased;

    public AudioSource Source { get; }
    public string? FailureReason { get { lock (gate) return failureReason; } }
    public bool HasFailed => FailureReason != null;
    /// <summary>Requests queued or in flight (macOS <c>tasks.count</c>).</summary>
    public int OpenRequests { get { lock (gate) return open; } }
    public IChunkTranscriber Transcriber => transcriber;
    public ChunkedPipelineOptions Options => options;
    /// <summary>Word times are requested for final passages (the option is set and the backend supports them).</summary>
    public bool RequestsWordTimestamps => wordTimestamps;

    public event Action<TranscriptionSegment>? SegmentReceived;
    public event Action<TranscriptionEvent>? EventRaised;
    public event Action<string>? Failed;

    /// <summary>The text-only path: every passage is final, no previews, no word times.</summary>
    public ChunkedTranscriptionPipeline(AudioSource source, ITranscriptionClient client, Guid sessionId, int maximumBacklog = DefaultMaximumBacklog)
        : this(source, new TranscriptionClientAdapter(client), sessionId, new ChunkedPipelineOptions { MaximumBacklog = maximumBacklog, Previews = false })
    {
    }

    public ChunkedTranscriptionPipeline(AudioSource source, IChunkTranscriber transcriber, Guid sessionId, ChunkedPipelineOptions? options = null)
    {
        Source = source;
        this.transcriber = transcriber;
        this.sessionId = sessionId;
        this.options = options ?? new();
        wordTimestamps = this.options.WordTimestamps && transcriber.SupportsWordTimestamps;
        time = this.options.Time ?? TimeProvider.System;
        lastArrival = time.GetTimestamp();
        if (this.options.AutomaticIdleTimer)
            idleTimer = time.CreateTimer(_ => CheckIdle(), null, TimeSpan.FromSeconds(IdleFlushSeconds), TimeSpan.FromSeconds(IdleTimerPeriodSeconds));
    }

    public void Append(ReadOnlyMemory<float> samples16k, double meetingOffset)
    {
        if (samples16k.IsEmpty) return;
        lock (gate)
        {
            if (stopped || cancelled || failed) return;
            try { Process(samples16k.Span, meetingOffset); }
            catch (Exception) { MarkFailed(TranscriptionMessages.ProcessingFailed); }
        }
        PublishFailure();
    }

    private void Process(ReadOnlySpan<float> input, double offset)
    {
        lastArrival = time.GetTimestamp();
        // Preserve even short explicit gaps for Whisper word alignment: the engine's timestamps are sample-exact.
        if (expectedOffset is double expected && Math.Abs(offset - expected) > GapToleranceSeconds && samples.Count > 0)
        {
            CloseWindow(applyRules: false);
            Flush();
        }
        expectedOffset = offset + input.Length / (double)SampleRate;
        for (var index = 0; index < input.Length && !failed; index++)
        {
            if (samples.Count == 0) chunkOffset = Math.Max(0, offset + index / (double)SampleRate);
            var value = float.IsFinite(input[index]) ? input[index] : 0f;
            samples.Add(value);
            windowEnergy += value * value;
            // A 20 ms RMS window avoids letting a long, mostly silent buffer hide speech.
            if (++windowCount == WindowSamples) CloseWindow(applyRules: true);
        }
    }

    /// <summary>Classifies the current (possibly partial) window; with rules, finalizes or previews like macOS after each window.</summary>
    private void CloseWindow(bool applyRules)
    {
        if (windowCount == 0) return;
        if (MathF.Sqrt(windowEnergy / windowCount) >= SpeechThreshold) { containsSpeech = true; trailingSilence = 0; }
        else trailingSilence += windowCount;
        windowCount = 0;
        windowEnergy = 0;
        if (!applyRules) return;
        if (samples.Count >= MaximumSamples || samples.Count >= MinimumSamples && trailingSilence >= SilenceSamples)
            Flush();
        else if (options.Previews && containsSpeech && samples.Count - lastPreviewSampleCount >= PreviewSamples && open < MaximumOpenForPreview)
        {
            Enqueue(isFinal: false);
            lastPreviewSampleCount = samples.Count;
        }
    }

    /// <summary>Two-second previews reuse one segment id; four-second blocks or a pause finalize it.</summary>
    private void Flush()
    {
        Enqueue(isFinal: true);
        samples.Clear();
        segmentId = Guid.NewGuid();
        lastPreviewSampleCount = 0;
        containsSpeech = false;
        trailingSilence = 0;
        windowCount = 0;
        windowEnergy = 0;
    }

    private void Enqueue(bool isFinal)
    {
        if (!containsSpeech || samples.Count == 0 || failed || cancelled) return;
        if (open >= options.MaximumBacklog)
        {
            MarkFailed(TranscriptionMessages.BacklogFull);
            return;
        }
        var job = new Job(segmentId, WavEncoder.Encode(samples), chunkOffset, samples.Count / (double)SampleRate, isFinal);
        open++;
        // Strictly one request after another per channel; the continuation always runs on the pool, never under the lock.
        tail = tail.ContinueWith(_ => RunAsync(job), CancellationToken.None, TaskContinuationOptions.DenyChildAttach, TaskScheduler.Default).Unwrap();
    }

    private async Task RunAsync(Job job)
    {
        var token = cancellation.Token;
        ChunkTranscription? result = null;
        try
        {
            bool allowed;
            lock (gate) allowed = !failed && !cancelled;
            if (!allowed || token.IsCancellationRequested) return;
            try { result = await RecognizeAsync(job, token).ConfigureAwait(false); }
            catch (Exception) when (token.IsCancellationRequested)
            {
                // Cancellation is a deliberate teardown, never a transcript result.
            }
            catch (Exception error) when (job.IsFinal)
            {
                lock (gate) MarkFailed(TranscriptionMessages.TranscriptionFailed + Explain(error));
                PublishFailure();
            }
            catch (Exception)
            {
                // Previews are replaceable guesses. Their failure must not discard the retained audio or
                // prevent the authoritative final request.
            }
            // An empty final is published on purpose: it retracts the passage's provisional guess.
            if (result != null && !token.IsCancellationRequested && (job.IsFinal || !string.IsNullOrWhiteSpace(result.Text)))
                SegmentReceived?.Invoke(new()
                {
                    Id = job.SegmentId, Source = Source, Offset = job.Offset, Duration = job.Duration,
                    Text = result.Text, IsFinal = job.IsFinal, SessionId = sessionId, Words = job.IsFinal ? result.Words : null
                });
        }
        finally { lock (gate) open--; }
    }

    private async Task<ChunkTranscription> RecognizeAsync(Job job, CancellationToken token)
    {
        var audio = new ChunkAudio(job.Wav, job.Duration, job.IsFinal, wordTimestamps && job.IsFinal);
        if (job.IsFinal)
        {
            // WaitAsync: cancellation ends the wait even if a backend ignores its token.
            var request = transcriber.TranscribeChunkAsync(audio, token);
            try { return await request.WaitAsync(token).ConfigureAwait(false); }
            finally { Observe(request); }
        }
        // A request timeout is an inactivity timeout at best; an absolute deadline also bounds a slowly
        // trickling preview, so it cannot starve final passages.
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        var preview = transcriber.TranscribeChunkAsync(audio, deadline.Token);
        try { return await preview.WaitAsync(options.PreviewTimeout, time, token).ConfigureAwait(false); }
        finally
        {
            deadline.Cancel();
            Observe(preview);
        }
    }

    private static void Observe(Task task)
    {
        if (!task.IsCompleted)
            _ = task.ContinueWith(static done => _ = done.Exception, CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private static string Explain(Exception error) => error switch
    {
        InvalidOperationException readable => readable.Message,   // ChunkTranscriptionException and the text-only clients
        TimeoutException or OperationCanceledException => TranscriptionMessages.TranscriptionTimeout,
        _ => TranscriptionMessages.NetworkOrResponse
    };

    /// <summary>The 0.4 s idle flush; runs on the clock's timer every 0.15 s (tests call it directly).</summary>
    internal void CheckIdle()
    {
        lock (gate)
        {
            if (stopped || samples.Count == 0) return;
            if (time.GetElapsedTime(lastArrival) <= TimeSpan.FromSeconds(IdleFlushSeconds)) return;
            CloseWindow(applyRules: false);
            if (!containsSpeech) return;
            Flush();
        }
        PublishFailure();
    }

    /// <summary>Must be called under the lock; the caller publishes with <see cref="PublishFailure"/> after leaving it.</summary>
    private void MarkFailed(string message)
    {
        if (failed || cancelled) return;
        failed = true;
        var recovery = options.AudioRetained switch
        {
            true => " " + TranscriptionMessages.AudioKept,
            false => " " + TranscriptionMessages.AudioNotKept,
            null => ""
        };
        failureReason = $"{Source}: {message}{recovery}";
        unpublishedFailure = failureReason;
    }

    private void PublishFailure()
    {
        string? reason;
        lock (gate) { reason = unpublishedFailure; unpublishedFailure = null; }
        if (reason == null) return;
        EventRaised?.Invoke(new(TranscriptionEventKind.State, Source, reason));
        Failed?.Invoke(reason);
    }

    public async Task FinishAsync()
    {
        Task pending;
        lock (gate)
        {
            if (!stopped)
            {
                stopped = true;
                CloseWindow(applyRules: false);
                Flush();
            }
            pending = tail;
        }
        idleTimer?.Dispose();
        PublishFailure();
        await pending.ConfigureAwait(false);
        ReleaseTranscriber();
    }

    public async Task CancelAsync()
    {
        Task pending;
        lock (gate)
        {
            stopped = true;
            cancelled = true;
            samples.Clear();
            windowCount = 0;
            windowEnergy = 0;
            pending = tail;
        }
        idleTimer?.Dispose();
        try { cancellation.Cancel(); } catch (AggregateException) { }
        try { await pending.ConfigureAwait(false); } catch (Exception) { }
        ReleaseTranscriber();
    }

    private void ReleaseTranscriber()
    {
        if (options.DisposeTranscriber && Interlocked.Exchange(ref transcriberReleased, 1) == 0)
            (transcriber as IDisposable)?.Dispose();
    }

    public void Dispose() => CancelAsync().GetAwaiter().GetResult();
}
