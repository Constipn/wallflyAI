using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using SherpaOnnx;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Settings of <see cref="SherpaOnnxDiarizer"/>.</summary>
public sealed record SherpaOnnxDiarizerOptions
{
    /// <summary>The <c>…\diarization</c> model directory; null searches <see cref="ModelLocator.CandidateDirectories"/>.</summary>
    public string? ModelDirectory { get; init; }
    /// <summary>
    /// Cosine-distance threshold of the agglomerative clustering: smaller values give more speakers. 0.6 is the start
    /// value (the macOS FluidAudio/VBx 0.6 has different semantics); tune on real meetings.
    /// </summary>
    public double Threshold { get; init; } = 0.6;
    /// <summary>A positive value fixes the number of speakers and ignores <see cref="Threshold"/>.</summary>
    public int NumClusters { get; init; } = -1;
    /// <summary>Shorter speech segments are dropped (seconds; sherpa-onnx default).</summary>
    public float MinDurationOn { get; init; } = 0.3f;
    /// <summary>Gaps up to this long between segments of one speaker are bridged (seconds; sherpa-onnx default).</summary>
    public float MinDurationOff { get; init; } = 0.5f;
    /// <summary>
    /// Step of the 10-s segmentation window as a share of the window (sherpa-onnx default 0.1 = 1 s, as in pyannote).
    /// Embeddings are computed per window and speaker, so the cost falls roughly in proportion to this value.
    /// </summary>
    public float WindowShiftRatio { get; init; } = 0.1f;
    /// <summary>
    /// ONNX Runtime threads per model (default: half the logical processors, 1–4). Measured on the development laptop:
    /// 1 thread ≈ 1.0 × real time, 4 threads ≈ 0.45 × real time. The pool threads ONNX Runtime creates are lowered to
    /// <see cref="Priority"/> as well.
    /// </summary>
    public int NumThreads { get; init; } = Math.Clamp(Environment.ProcessorCount / 2, 1, 4);
    /// <summary>Priority of the worker thread and of ONNX Runtime's pool threads.</summary>
    public ThreadPriority Priority { get; init; } = ThreadPriority.BelowNormal;
    /// <summary>Check the SHA-256 of the models (once per process) before loading them.</summary>
    public bool VerifyModelHashes { get; init; } = true;
    /// <summary>
    /// Scales the input like a slow AGC before diarizing: one gain per run that brings the 95th percentile of the
    /// 20-ms frame RMS to <see cref="SherpaOnnxDiarizer.NormalizeTargetRms"/>, limited to 1–8 (quiet far-field speech on the
    /// microphone). Off by default: on the far-field meeting of 20.11. (p95 frame RMS 0.0048, gain 8) it did not give fewer
    /// or clearer voices (threshold 0.8: 28 clusters, 7 with ≥ 10 s, vs. 35 / 8 without) and pyannote normalises each
    /// window's waveform itself, so the gain mainly reshuffles the clusters.
    /// </summary>
    public bool NormalizeInput { get; init; }

    /// <summary>
    /// Clustering threshold of the microphone pass (AP5). Far-field speech that reached the microphone through the
    /// loudspeakers varies more within one voice: on the 23-min meeting of 20.11. the default 0.6 gave 52 clusters (14 with
    /// at least 10 s), 0.8 gave 35 (8) and 0.9 gave 26 (6, four of them with 210–400 s). Three synthetic voices on one
    /// track are still separated at 0.9 (<c>DiarizationSherpaTests.ThreeVoicesOnOneTrackAreSeparated</c>). The meeting had
    /// three speakers: the voice merge (<see cref="Merge"/>) joins the user's two large clusters (centroid similarity 0.95)
    /// and the fragments of one remote voice (0.89, 0.81); different people stay at 0.22–0.46. Threshold 1.0 alone gives 13
    /// clusters (4 with ≥ 10 s: the same three voices plus one 18-s fragment), so the merge, not a higher threshold, decides.
    /// </summary>
    public const double MicrophoneThreshold = 0.9;

    /// <summary>
    /// Post-clustering voice merge (<see cref="VoiceMerge"/>): null (default) keeps sherpa-onnx' clusters as they are. Both
    /// production passes merge (<see cref="Others"/>, <see cref="Microphone"/>), each with its own instance. Ignored when
    /// <see cref="NumClusters"/> fixes the number of speakers.
    /// </summary>
    public VoiceMergeOptions? Merge { get; init; }

    /// <summary>The options of the microphone pass: <see cref="MicrophoneThreshold"/> plus the voice merge, otherwise the defaults.</summary>
    public static SherpaOnnxDiarizerOptions Microphone => new() { Threshold = MicrophoneThreshold, Merge = new VoiceMergeOptions() };

    /// <summary>
    /// The options of the Others (system audio) pass: the default threshold 0.6 plus the voice merge with the microphone pass's
    /// settings. The first real Teams call (24.11., one remote person) gave 61 clusters at 0.6 and kept 6 acoustic speakers
    /// for that one person; the merge compares whole clusters, as on the microphone. Merges that contradict the Teams names
    /// are undone before the fusion (<see cref="TeamsVoiceGuard"/>).
    /// </summary>
    public static SherpaOnnxDiarizerOptions Others => new() { Merge = new VoiceMergeOptions() };
}

/// <summary>
/// Offline speaker diarization with sherpa-onnx (spec 5.3, Windows replacement of FluidAudio): pyannote
/// segmentation-3.0, 3D-Speaker ERes2Net embeddings and agglomerative (not VBx) clustering. Runs on a dedicated
/// background thread at below-normal priority (ONNX Runtime's pool threads too), one native run at a time.
/// </summary>
/// <remarks>
/// Cancellation releases the caller at once, but the native call cannot be interrupted (sherpa-onnx ignores the progress
/// callback's return value): it finishes in the background and its result is discarded; the next run waits for it.
/// Memory: the whole timeline is passed as one buffer (about 230 MB per hour) plus the models (about 33 MB).
/// <para>
/// <see cref="Pause"/> (a recording started) cannot stop the native call either, but it can hold it: the progress callback
/// runs on the worker thread inside the native loop, so blocking there stops the computation (ONNX Runtime's pool threads
/// only work for that thread). sherpa-onnx calls it during the embedding stage only, not during segmentation: measured on
/// the development laptop (160 s of speech, 4 threads, 26.11.2026) the first call came after 21 % of the run (model loading,
/// segmentation), then at most 0.65 s apart until the end. The part before the first call keeps running, at the idle
/// priority (worker and pool threads); the run continues where it stopped after <see cref="Resume"/>.
/// </para>
/// </remarks>
public sealed class SherpaOnnxDiarizer(SherpaOnnxDiarizerOptions? options = null) : ConcatenatingDiarizer, IPausableDiarizer
{
    private readonly HoldGate hold = new();
    private readonly Lock threads = new();
    /// <summary>Native id of the worker thread of the running native call (0 when none).</summary>
    private uint workerThreadId;

    /// <summary>The pinned NuGet version of <c>org.k2fsa.sherpa.onnx</c> (see the project file).</summary>
    public const string SherpaOnnxVersion = "2.0.1";
    public const string EngineName = "sherpa-onnx-pyannote-3.0";

    private static readonly SemaphoreSlim NativeGate = new(1, 1);
    private static readonly FieldInfo? HandleField = typeof(OfflineSpeakerDiarization).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance);
    private static readonly FieldInfo? ExtractorHandleField = typeof(SpeakerEmbeddingExtractor).GetField("_handle", BindingFlags.NonPublic | BindingFlags.Instance);

    public SherpaOnnxDiarizerOptions Options { get; } = options ?? new SherpaOnnxDiarizerOptions();

    public override string Engine => EngineName;
    public override string? ModelVersion =>
        $"sherpa-onnx@{SherpaOnnxVersion}/{Path.GetDirectoryName(ModelLocator.Segmentation.RelativePath)}+{Path.GetFileNameWithoutExtension(ModelLocator.Embedding.RelativePath)}";

    /// <summary>Diagnostics: ONNX Runtime pool threads lowered to <see cref="SherpaOnnxDiarizerOptions.Priority"/> in the last run.</summary>
    public int LastLoweredThreads { get; private set; }
    public override double? Threshold => Options.NumClusters > 0 ? null : Options.Threshold;

    /// <summary>True when the models are present and intact (for "Re-detect speakers" availability and test gating).</summary>
    public bool ModelsAvailable(out string reason) => ModelLocator.TryLocate(out reason, Options.ModelDirectory, Options.VerifyModelHashes) is not null;

    /// <summary>True while <see cref="Pause"/> holds the computation.</summary>
    public bool IsPaused => hold.IsHeld;

    /// <summary>
    /// Holds the computation at the next progress callback (or before the native call starts, or between two voice fingerprints)
    /// and lowers the worker and ONNX Runtime's pool threads to <see cref="NativeThreadPriority.Idle"/> for what still runs.
    /// </summary>
    public void Pause()
    {
        hold.Hold();
        lock (threads)
        {
            if (workerThreadId != 0) NativeThreadPriority.SetThread(workerThreadId, NativeThreadPriority.Idle);
            NativeThreadPriority.Set("onnxruntime.dll", NativeThreadPriority.Idle);
        }
    }

    /// <summary>Lets the computation continue at <see cref="SherpaOnnxDiarizerOptions.Priority"/>.</summary>
    public void Resume()
    {
        lock (threads)
        {
            if (workerThreadId != 0) NativeThreadPriority.SetThread(workerThreadId, NativeThreadPriority.Win32(Options.Priority));
            NativeThreadPriority.Lower("onnxruntime.dll", Options.Priority);
        }
        hold.Release();
    }

    protected override async Task<IReadOnlyList<TimelineSegment>> DiarizeAsync(ConcatenatedWav audio, IProgress<double>? progress, CancellationToken cancellationToken)
    {
        var models = ModelLocator.Locate(Options.ModelDirectory, Options.VerifyModelHashes);
        var samples = audio.ReadAll();
        cancellationToken.ThrowIfCancellationRequested();
        if (Options.NormalizeInput) Normalize(samples);
        var completion = new TaskCompletionSource<IReadOnlyList<TimelineSegment>>(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() => Run(samples, models, progress, cancellationToken, completion))
        {
            IsBackground = true, Priority = Options.Priority, Name = "Wallfly diarization (sherpa-onnx)"
        };
        worker.Start();
        using var registration = cancellationToken.Register(() => completion.TrySetCanceled(cancellationToken));
        return await completion.Task.ConfigureAwait(false);
    }

    /// <summary>The AGC target: the 95th-percentile frame RMS after scaling.</summary>
    public const double NormalizeTargetRms = 0.06;
    /// <summary>Gain limits of the input normalisation.</summary>
    public const double NormalizeMinGain = 1, NormalizeMaxGain = 8;

    /// <summary>The gain <see cref="SherpaOnnxDiarizerOptions.NormalizeInput"/> applies: target / p95 of the 20-ms frame RMS, clamped to 1–8.</summary>
    public static double NormalizationGain(ReadOnlySpan<float> samples)
    {
        const int frame = 320;
        var count = samples.Length / frame;
        if (count == 0) return NormalizeMinGain;
        var rms = new double[count];
        for (var index = 0; index < count; index++)
        {
            double sum = 0;
            foreach (var sample in samples.Slice(index * frame, frame)) sum += sample * (double)sample;
            rms[index] = Math.Sqrt(sum / frame);
        }
        Array.Sort(rms);
        var p95 = rms[Math.Min(count - 1, (int)(0.95 * count))];
        return p95 > 0 ? Math.Clamp(NormalizeTargetRms / p95, NormalizeMinGain, NormalizeMaxGain) : NormalizeMinGain;
    }

    private static void Normalize(float[] samples)
    {
        var gain = (float)NormalizationGain(samples);
        if (gain == 1) return;
        for (var index = 0; index < samples.Length; index++) samples[index] = Math.Clamp(samples[index] * gain, -1f, 1f);
    }

    private void Run(float[] samples, DiarizationModels models, IProgress<double>? progress, CancellationToken cancellationToken,
        TaskCompletionSource<IReadOnlyList<TimelineSegment>> completion)
    {
        NativeGate.Wait();
        lock (threads)
        {
            workerThreadId = NativeThreadPriority.CurrentThreadId();
            if (hold.IsHeld) NativeThreadPriority.SetThread(workerThreadId, NativeThreadPriority.Idle);
        }
        try
        {
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
            var config = new OfflineSpeakerDiarizationConfig();
            config.Segmentation.Pyannote.Model = models.SegmentationModel;
            config.Segmentation.Pyannote.WindowShiftRatio = Options.WindowShiftRatio;
            config.Segmentation.NumThreads = Math.Max(1, Options.NumThreads);
            config.Segmentation.Provider = "cpu";
            config.Segmentation.Debug = 0;
            config.Embedding.Model = models.EmbeddingModel;
            config.Embedding.NumThreads = Math.Max(1, Options.NumThreads);
            config.Embedding.Provider = "cpu";
            config.Embedding.Debug = 0;
            config.Clustering.NumClusters = Options.NumClusters > 0 ? Options.NumClusters : -1;
            config.Clustering.Threshold = (float)Options.Threshold;
            config.Clustering.ComputeConfidence = 0;
            config.MinDurationOn = Options.MinDurationOn;
            config.MinDurationOff = Options.MinDurationOff;
            using var diarizer = new OfflineSpeakerDiarization(config);
            // A failed load leaves a null native handle; any further call would crash the process.
            if (HandleField?.GetValue(diarizer) is HandleRef handle && handle.Handle == IntPtr.Zero)
                throw DiarizationException.ModelsUnavailable("sherpa-onnx could not load the models");
            // The sessions have created their thread pools; they run at normal priority unless lowered here.
            lock (threads) LastLoweredThreads = hold.IsHeld ? NativeThreadPriority.Set("onnxruntime.dll", NativeThreadPriority.Idle)
                : NativeThreadPriority.Lower("onnxruntime.dll", Options.Priority);
            if (diarizer.SampleRate != (int)DiarizationTimeline.SampleRate)
                throw DiarizationException.ProcessingFailed($"the segmentation model expects {diarizer.SampleRate} Hz");
            // A recording started meanwhile: wait before the native call (cheap) rather than inside it.
            if (!hold.WaitWhileHeld(cancellationToken)) { completion.TrySetCanceled(cancellationToken); return; }
            OfflineSpeakerDiarizationProgressCallback callback = (done, total, _) =>
            {
                try { if (total > 0) progress?.Report(Math.Clamp((double)done / total, 0, 1)); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                // Held by Pause: blocking here stops the native loop until Resume (or the run is cancelled; the native call
                // then finishes in the background as after any cancellation). Nothing may throw into native code.
                try { hold.WaitWhileHeld(cancellationToken); }
                catch (Exception error) when (error is not OutOfMemoryException) { }
                return 0;
            };
            var segments = diarizer.ProcessWithCallback(samples, callback, IntPtr.Zero);
            GC.KeepAlive(callback);
            if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
            IReadOnlyList<TimelineSegment> timeline = segments.Select(segment => new TimelineSegment(segment.Speaker, segment.Start, segment.End)).ToList();
            // A fixed number of speakers is the caller's decision: no merge then.
            if (Options.Merge is { } merge && Options.NumClusters <= 0 && timeline.Select(segment => segment.Speaker).Distinct().Count() > 1)
            {
                timeline = MergeVoices(samples, timeline, models, merge, cancellationToken);
                if (cancellationToken.IsCancellationRequested) { completion.TrySetCanceled(cancellationToken); return; }
            }
            progress?.Report(1);
            completion.TrySetResult(timeline);
        }
        catch (DiarizationException error) { completion.TrySetException(error); }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException or EntryPointNotFoundException)
        { completion.TrySetException(DiarizationException.ModelsUnavailable($"sherpa-onnx native runtime missing: {error.Message}")); }
        catch (Exception error) { completion.TrySetException(DiarizationException.ProcessingFailed(error.Message)); }
        finally
        {
            lock (threads) workerThreadId = 0;
            NativeGate.Release();
        }
    }

    /// <summary>
    /// The voice merge (<see cref="VoiceMerge"/>): ERes2Net fingerprints of each cluster's clean speech with the model the
    /// diarization already loaded, then centroid merge and minimum voice size. Runs inside the native gate on the worker thread.
    /// </summary>
    private DiarizedTimeline MergeVoices(float[] samples, IReadOnlyList<TimelineSegment> timeline, DiarizationModels models, VoiceMergeOptions merge,
        CancellationToken cancellationToken)
    {
        var clock = Stopwatch.StartNew();
        var spans = VoiceMerge.Spans(timeline, merge);
        var fingerprints = new List<VoiceFingerprint>(spans.Count);
        using (var extractor = new SpeakerEmbeddingExtractor(new SpeakerEmbeddingExtractorConfig
               {
                   Model = models.EmbeddingModel, NumThreads = Math.Max(1, Options.NumThreads), Provider = "cpu", Debug = 0
               }))
        {
            // A failed load leaves a null native handle; any further call would crash the process.
            if (ExtractorHandleField?.GetValue(extractor) is HandleRef handle && handle.Handle == IntPtr.Zero)
                throw DiarizationException.ModelsUnavailable("sherpa-onnx could not load the embedding model");
            LastLoweredThreads += NativeThreadPriority.Lower("onnxruntime.dll", Options.Priority);
            foreach (var span in spans)
            {
                if (!hold.WaitWhileHeld(cancellationToken) || cancellationToken.IsCancellationRequested) break;
                var start = (int)Math.Clamp(span.Start, 0, samples.Length);
                var end = (int)Math.Clamp(span.End, start, samples.Length);
                using var stream = extractor.CreateStream();
                stream.AcceptWaveform((int)DiarizationTimeline.SampleRate, samples[start..end]);
                stream.InputFinished();
                if (!extractor.IsReady(stream)) continue;
                fingerprints.Add(new VoiceFingerprint(span.Cluster, (end - start) / DiarizationTimeline.SampleRate, extractor.Compute(stream)));
            }
        }
        var plan = VoiceMerge.Plan(timeline, fingerprints, merge);
        var clusters = timeline.Select(segment => segment.Speaker).Distinct().Count();
        return new DiarizedTimeline(VoiceMerge.Apply(timeline, plan), new VoiceMergeSummary(clusters, plan.Merged, plan.Absorbed, clock.Elapsed), timeline, plan.Voices);
    }
}

/// <summary>The hold of <see cref="IPausableDiarizer"/>: open while the engine may compute, closed while a recording is active.</summary>
internal sealed class HoldGate
{
    private readonly ManualResetEventSlim open = new(true);

    public bool IsHeld => !open.IsSet;

    public void Hold() => open.Reset();

    public void Release() => open.Set();

    /// <summary>Blocks while held. False when <paramref name="cancellationToken"/> ended the wait.</summary>
    public bool WaitWhileHeld(CancellationToken cancellationToken)
    {
        if (open.IsSet) return true;
        try
        {
            open.Wait(cancellationToken);
            return true;
        }
        catch (OperationCanceledException) { return false; }
    }
}
