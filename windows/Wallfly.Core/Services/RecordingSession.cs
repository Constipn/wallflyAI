using System.Collections.Concurrent;
using System.Diagnostics;
using Wallfly.Audio;
using Wallfly.Core.Text;
using Wallfly.Transcription;

namespace Wallfly.Core.Services;

/// <summary>Outcome of the stop sequence; <see cref="Error"/> carries every distinct reason.</summary>
public sealed record DrainResult(bool Complete, string? Error);

/// <summary>What one inbox drain produced for the UI thread.</summary>
/// <param name="StopRequest">
/// Set once when a failure ends the recording (a pipeline's <c>Failed</c>, spec 4.2; a fatal capture failure of the meeting
/// channel or the session, spec 3.2): the message the user sees. A paused Deepgram channel is never a stop request.
/// </param>
/// <param name="MicrophoneAcquired">
/// The latest Me <see cref="AudioEngineEventKind.DeviceAcquired"/> since the previous drain (spec 3.1: which microphone runs,
/// and whether the system default replaced the configured one); null when none arrived.
/// </param>
/// <param name="EngineEvents">
/// Every engine event of the current engine since the previous drain except gaps (which are counted in
/// <see cref="RecordingSession.Statistics"/>): device acquired / switched / lost, degradations, failures. For the log and
/// the device notice; the failures also arrive through <see cref="Errors"/> and <see cref="StopRequest"/>.
/// </param>
/// <param name="StopOrigin">With <see cref="StopRequest"/>: <see cref="RecordingStopReasons.PipelineFailed"/> or <see cref="RecordingStopReasons.EngineFailed"/>.</param>
/// <param name="EngineEventGenerations">
/// The engine generation (<see cref="RecordingSession.EngineGeneration"/>) of each entry of <see cref="EngineEvents"/>, in the
/// same order: a replacement engine's events from before its cutover are delivered right after the cutover, and the owner
/// applies them only once it knows that engine's configuration.
/// </param>
public sealed record RecordingUpdate(
    IReadOnlyList<TranscriptSegment> Segments,
    IReadOnlyList<RecordingGap> RecordingGaps,
    IReadOnlyList<TranscriptionGap> TranscriptionGaps,
    IReadOnlyList<AudioAnchor> Anchors,
    IReadOnlyList<string> Errors,
    double MeLevel,
    double OthersLevel,
    IReadOnlyList<TranscriptionEvent>? TranscriptionStates = null,
    string? StopRequest = null,
    AudioEngineEvent? MicrophoneAcquired = null,
    IReadOnlyList<AudioEngineEvent>? EngineEvents = null,
    string? StopOrigin = null,
    IReadOnlyList<int>? EngineEventGenerations = null);

/// <summary>Why a recording ended (the stop line of the app log).</summary>
public static class RecordingStopReasons
{
    public const string Manual = "manual";
    public const string MeetingEnded = "meeting ended";
    public const string Sleep = "sleep";
    public const string PipelineFailed = "pipeline failed";
    public const string EngineFailed = "engine failed";
    public const string Quit = "quit";
}

/// <summary>What one channel delivered so far: accepted frames, their samples and the samples of audible frames.</summary>
/// <param name="AudibleSamples">Samples of frames whose peak reached <see cref="RecordingSession.SilenceThreshold"/> (−60 dBFS).</param>
public readonly record struct ChannelStatistics(long Frames, long Samples, long AudibleSamples)
{
    public double Seconds => Samples / (double)AudioFormat.SampleRate;
    public double AudibleSeconds => AudibleSamples / (double)AudioFormat.SampleRate;
    public static ChannelStatistics operator -(ChannelStatistics now, ChannelStatistics before) =>
        new(now.Frames - before.Frames, now.Samples - before.Samples, now.AudibleSamples - before.AudibleSamples);
}

/// <summary>Capture gaps of one source and reason (cumulative).</summary>
public readonly record struct GapTotal(AudioSource Source, AudioGapReason Reason, int Count, double Seconds);

/// <summary>A snapshot of the counters of one recording attempt (health line and stop summary of the app log).</summary>
public sealed record CaptureStatistics(
    ChannelStatistics Me,
    ChannelStatistics Others,
    IReadOnlyList<GapTotal> Gaps,
    int MeTranscriptionGaps,
    double MeTranscriptionGapSeconds,
    int OthersTranscriptionGaps,
    double OthersTranscriptionGapSeconds,
    long StaleFramesDropped,
    int EngineGeneration)
{
    public (int Count, double Seconds) GapsOf(AudioSource source) =>
        (Gaps.Where(gap => gap.Source == source).Sum(gap => gap.Count), Gaps.Where(gap => gap.Source == source).Sum(gap => gap.Seconds));
}

/// <summary>
/// One recording attempt: it owns the engine (or, after a switch of the silent-meeting-audio guard, its successor), the two
/// transcription pipelines and the audio archives, fences every asynchronous result with the meeting / session /
/// engine generation / attempt ids, and buffers results in lock-free inboxes that the UI thread drains every 100 ms.
/// Nothing here touches the meeting model.
/// </summary>
/// <remarks>
/// Engine generations: <see cref="SwitchEngineAsync"/> starts a replacement engine while the current one keeps running.
/// The first frame of the replacement (or the cutover deadline) makes it current; from then on every frame and event of
/// an older generation is dropped and the older engine is stopped and disposed in the background. The replacement's
/// non-fatal events from before its cutover (its DeviceAcquired, a <c>fallback:</c>) are kept and delivered right after the
/// cutover; a fatal one abandons it. Once <see cref="FinishAsync"/> or <see cref="AbortAsync"/> began, no cutover happens:
/// a pending replacement is abandoned under the same lock, then stopped and disposed. The replacement's first
/// frames are trimmed to where the previous generation left off, so neither channel sees overlapping or repeated audio,
/// and <see cref="MeetingClock"/> forwards to the current engine's clock (the replacement is started with the meeting
/// time of the switch as its base, so the clock continues).
/// </remarks>
public sealed class RecordingSession
{
    /// <summary>
    /// A frame whose first valid sample differs from the end of what was written to the archive by more than
    /// this many samples starts a new anchor (spec 3.6).
    /// </summary>
    public const double AnchorToleranceSamples = 1.5;
    /// <summary>The level meters are refreshed this often, apart from the 100 ms inbox drain.</summary>
    public static readonly TimeSpan LevelInterval = TimeSpan.FromMilliseconds(50);
    /// <summary>−60 dBFS: an Others frame whose peak stays below counts as silent (silent-meeting-audio guard).</summary>
    public const float SilenceThreshold = 0.001f;
    /// <summary>A retired or abandoned engine gets this long to stop before it is disposed anyway.</summary>
    public static readonly TimeSpan RetireBudget = TimeSpan.FromSeconds(3);
    /// <summary>
    /// <see cref="OthersAudibleSeconds"/> keeps this many one-second buckets of the Others channel, so its window can be at
    /// most this long minus one second.
    /// </summary>
    public const int AudibleBuckets = 64;
    /// <summary>
    /// A one-second bucket of the Others channel counts as audible only when at least this share of it was audible (a frame
    /// peak at or above <see cref="SilenceThreshold"/>): a notification chime of a few hundred milliseconds does not, speech
    /// on a working capture does.
    /// </summary>
    public const double AudibleBucketShare = 0.5;

    private sealed class Generation(int index, IAudioEngine engine)
    {
        public int Index { get; } = index;
        public IAudioEngine Engine { get; } = engine;
        public Dictionary<AudioSource, uint> Attempts { get; } = new() { [AudioSource.Me] = 0, [AudioSource.Others] = 0 };
        public Action<AudioFrame>? FrameHandler { get; set; }
        public Action<AudioEngineEvent>? EventHandler { get; set; }
        /// <summary>Stopped and disposed by a background retirement (or about to be); <see cref="Release"/> skips it.</summary>
        public bool Retired { get; set; }
        /// <summary>Completed with true at the cutover, with false when the replacement is abandoned.</summary>
        public TaskCompletionSource<bool> Cutover { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        /// <summary>The replacement's start; a retirement waits for it, so a stop never overtakes a start.</summary>
        public Task Starting { get; set; } = Task.CompletedTask;
        /// <summary>
        /// Non-fatal events of a replacement before its cutover (its DeviceAcquired with the endpoint it really opened, a
        /// <c>fallback:</c>), under <see cref="frameGate"/>; processed right after the cutover, in order.
        /// </summary>
        public List<AudioEngineEvent> EarlyEvents { get; } = [];
    }

    private sealed class ForwardingClock(RecordingSession owner) : IAudioClock
    {
        public double NowSeconds => Volatile.Read(ref owner.currentGeneration).Engine.Clock.NowSeconds;
    }

    private readonly ITranscriptionPipeline me;
    private readonly ITranscriptionPipeline others;
    private readonly Dictionary<AudioSource, AudioArchiveWriter?> archives;
    private readonly Dictionary<AudioSource, double> expected = [];
    /// <summary>End (meeting time) of the last accepted frame per channel and the generation that delivered it.</summary>
    private readonly Dictionary<AudioSource, (double End, int Generation)> highWater = [];
    private readonly List<Generation> generations = [];
    private readonly ConcurrentQueue<TranscriptSegment> segmentInbox = new();
    private readonly ConcurrentQueue<RecordingGap> recordingGapInbox = new();
    private readonly ConcurrentQueue<TranscriptionGap> transcriptionGapInbox = new();
    private readonly ConcurrentQueue<AudioAnchor> anchorInbox = new();
    private readonly ConcurrentQueue<string> errorInbox = new();
    private readonly ConcurrentQueue<string> failureReasons = new();
    private readonly ConcurrentQueue<AudioEngineEvent> microphoneInbox = new();
    private readonly ConcurrentQueue<(AudioEngineEvent Event, int Generation)> engineEventInbox = new();
    private readonly object statisticsGate = new();
    private readonly Dictionary<(AudioSource Source, AudioGapReason Reason), (int Count, double Seconds)> gapTotals = [];
    private readonly Dictionary<AudioSource, (int Count, double Seconds)> transcriptionGapTotals = [];
    private readonly long[] frames = new long[2];
    private readonly long[] samplesDelivered = new long[2];
    private readonly long[] audibleSamples = new long[2];
    private string? stopOrigin;
    /// <summary>Latest pipeline state per channel ("Live · Nova-3", "Paused · …"); older states are superseded.</summary>
    private readonly ConcurrentDictionary<AudioSource, TranscriptionEvent> stateInbox = new();
    private readonly object frameGate = new();
    private Generation currentGeneration;
    private Generation? pending;
    private bool finishing;
    private bool released;
    private int failed;
    private int stopRequested;
    private string? undeliveredStopRequest;
    private long staleFramesDropped;
    private long overlapSamplesTrimmed;
    private double othersLastAudible;
    /// <summary>Meeting time from which the current engine's Others channel is measured (capture start or the last cutover).</summary>
    private double othersMeasuredFrom;
    /// <summary>
    /// Meeting time at which the Others channel was last heard (a mostly audible second, as <see cref="OthersAudibleSeconds"/>
    /// counts it) or capture started; never reset by a cutover or a native endpoint move (<see cref="OthersUnheardSeconds"/>).
    /// </summary>
    private double othersLastHeard;
    /// <summary>Audible Others samples per meeting second (ring of <see cref="AudibleBuckets"/>), under <see cref="frameGate"/>.</summary>
    private readonly long[] audibleBucketSecond = new long[AudibleBuckets];
    private readonly int[] audibleBucketSamples = new int[AudibleBuckets];

    public Guid MeetingId { get; }
    public Guid SessionId { get; }
    /// <summary>Duration of the earlier sessions of this meeting; already contained in the frame offsets.</summary>
    public double BaseDuration { get; }
    public bool AudioFailed => Volatile.Read(ref failed) != 0;
    /// <summary>Frames of a superseded attempt or engine generation that were dropped.</summary>
    public long StaleFramesDropped => Interlocked.Read(ref staleFramesDropped);
    /// <summary>Samples of a replacement engine's first frames that overlapped what the previous engine already delivered.</summary>
    public long OverlapSamplesTrimmed => Interlocked.Read(ref overlapSamplesTrimmed);
    /// <summary>The current engine (after an engine switch: the replacement).</summary>
    public IAudioEngine Engine => Volatile.Read(ref currentGeneration).Engine;
    /// <summary>0 for the engine of the constructor, 1 after the first switch, …</summary>
    public int EngineGeneration => Volatile.Read(ref currentGeneration).Index;
    /// <summary>
    /// Meeting seconds on the audio clock: <see cref="IAudioEngine.Clock"/> of the current engine (it reads
    /// <see cref="BaseDuration"/> at the session origin and is the clock the frame offsets are measured on).
    /// </summary>
    public IAudioClock MeetingClock { get; }
    public AudioLevels Levels => Engine.Levels;

    public RecordingSession(IAudioEngine engine, ITranscriptionPipeline me, ITranscriptionPipeline others,
        Guid meetingId, Guid sessionId, double baseDuration = 0,
        AudioArchiveWriter? meArchive = null, AudioArchiveWriter? othersArchive = null)
    {
        this.me = me; this.others = others;
        MeetingId = meetingId; SessionId = sessionId; BaseDuration = baseDuration;
        archives = new() { [AudioSource.Me] = meArchive, [AudioSource.Others] = othersArchive };
        currentGeneration = new Generation(0, engine);
        generations.Add(currentGeneration);
        MeetingClock = new ForwardingClock(this);
        othersLastAudible = baseDuration;
        othersMeasuredFrom = baseDuration;
        othersLastHeard = baseDuration;
        this.me.SegmentReceived += OnSegment;
        this.others.SegmentReceived += OnSegment;
        this.me.Failed += OnPipelineFailed;
        this.others.Failed += OnPipelineFailed;
        this.me.EventRaised += OnPipelineEvent;
        this.others.EventRaised += OnPipelineEvent;
    }

    public async Task StartAsync(AudioEngineConfig config)
    {
        var generation = Volatile.Read(ref currentGeneration);
        Attach(generation);
        try { await generation.Engine.StartAsync(config).ConfigureAwait(false); }
        catch
        {
            Detach(generation);
            throw;
        }
        var started = Math.Max(config.BaseDurationSeconds, SafeNow(generation.Engine));
        lock (frameGate) othersLastHeard = Math.Max(othersLastHeard, started);
        Volatile.Write(ref othersLastAudible, started);
        Volatile.Write(ref othersMeasuredFrom, started);
    }

    /// <summary>A replacement engine is starting and not yet current (<see cref="SwitchEngineAsync"/> would reject another one).</summary>
    public bool SwitchPending
    {
        get { lock (frameGate) return pending != null; }
    }

    /// <summary>
    /// Replaces the running engine (the silent-meeting-audio guard: switch to device loopback, retarget it to another output
    /// endpoint, or reopen it). <paramref name="next"/> is
    /// started with <paramref name="config"/> and the current meeting time as its base while the current engine keeps
    /// capturing; its first frame, or <paramref name="cutover"/> without one, makes it current. Returns false and keeps the
    /// current engine when the replacement cannot start, fails before the cutover, or the session is already finishing.
    /// </summary>
    public async Task<bool> SwitchEngineAsync(IAudioEngine next, AudioEngineConfig config, TimeSpan cutover, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(config);
        Generation? generation = null;
        bool known;
        lock (frameGate)
        {
            known = generations.Any(item => ReferenceEquals(item.Engine, next));
            if (!finishing && !released && pending == null && !known)
            {
                generation = new Generation(generations.Count, next);
                generations.Add(generation);
                pending = generation;
            }
        }
        if (generation is null)
        {
            // Not taken over: an unknown engine is ours to release, one of our own generations is not.
            if (!known) next.Dispose();
            return false;
        }
        Attach(generation);
        config.BaseDurationSeconds = MeetingClock.NowSeconds;
        try
        {
            generation.Starting = next.StartAsync(config, cancellationToken);
            await generation.Starting.ConfigureAwait(false);
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Wallfly: the replacement engine did not start: " + error.Message);
            Abandon(generation);
            return generation == Volatile.Read(ref currentGeneration);
        }
        var deadline = Task.Delay(cutover, CancellationToken.None);
        await Task.WhenAny(generation.Cutover.Task, deadline).ConfigureAwait(false);
        lock (frameGate)
        {
            // A session that is finishing or aborting takes no new engine: FinishAsync / AbortAsync abandon it.
            if (pending == generation && !finishing) CutoverLocked(generation);
        }
        return await generation.Cutover.Task.ConfigureAwait(false);
    }

    /// <summary>Switches the microphone of the current engine while running (spec 3.1).</summary>
    public Task SetMicrophoneAsync(string? deviceId, CancellationToken cancellationToken = default) =>
        Engine.SetMicrophoneAsync(deviceId, cancellationToken);

    /// <summary>
    /// Seconds since the Others channel of the current engine last delivered a sample at or above
    /// <see cref="SilenceThreshold"/> (or since capture started), at meeting time <paramref name="now"/>.
    /// </summary>
    public double OthersSilentSeconds(double now) => Math.Max(0, now - Volatile.Read(ref othersLastAudible));

    /// <summary>
    /// Seconds the Others channel of the current engine has been measured at meeting time <paramref name="now"/>: since
    /// capture started or since the last engine cutover (a switched or reopened capture starts a fresh measurement).
    /// </summary>
    public double OthersObservedSeconds(double now) => Math.Max(0, now - Volatile.Read(ref othersMeasuredFrom));

    /// <summary>
    /// Audible seconds of the Others channel of the current engine within the last <paramref name="window"/> seconds before
    /// meeting time <paramref name="now"/> (at most <see cref="AudibleBuckets"/> − 1). The channel is counted per meeting
    /// second; a second contributes its audible samples only when at least <see cref="AudibleBucketShare"/> of it was audible,
    /// so short notification chimes every few seconds do not make a dead capture look alive (unlike
    /// <see cref="OthersSilentSeconds"/>, which a single loud frame resets). Cheap; any thread.
    /// </summary>
    public double OthersAudibleSeconds(double now, double window)
    {
        window = Math.Clamp(window, 0, AudibleBuckets - 1);
        var first = (long)Math.Floor(now - window);
        var last = (long)Math.Floor(now);
        var minimum = (int)(AudioFormat.SampleRate * AudibleBucketShare);
        long samples = 0;
        lock (frameGate)
        {
            for (var index = 0; index < AudibleBuckets; index++)
            {
                var second = audibleBucketSecond[index];
                var count = audibleBucketSamples[index];
                if (second >= first && second <= last && count >= minimum) samples += count;
            }
        }
        return samples / (double)AudioFormat.SampleRate;
    }

    /// <summary>
    /// Seconds since the Others channel was last heard (a second that was at least <see cref="AudibleBucketShare"/> audible,
    /// so chimes do not count) or capture started, at meeting time <paramref name="now"/>, across every engine generation:
    /// unlike <see cref="OthersObservedSeconds"/> and <see cref="OthersAudibleSeconds"/> it is never restarted by a cutover or
    /// a native endpoint move, so a device that keeps flapping cannot postpone the guard's verdict forever. Any thread.
    /// </summary>
    public double OthersUnheardSeconds(double now)
    {
        lock (frameGate) return Math.Max(0, now - othersLastHeard);
    }

    /// <summary>Must be called under <see cref="frameGate"/>; allocation-free (the frame path).</summary>
    private void CountAudibleOthers(double offset, int samples)
    {
        var second = (long)Math.Floor(offset);
        var index = (int)(((second % AudibleBuckets) + AudibleBuckets) % AudibleBuckets);
        if (audibleBucketSecond[index] != second)
        {
            audibleBucketSecond[index] = second;
            audibleBucketSamples[index] = 0;
        }
        audibleBucketSamples[index] += samples;
        if (audibleBucketSamples[index] >= (int)(AudioFormat.SampleRate * AudibleBucketShare))
            othersLastHeard = Math.Max(othersLastHeard, offset + samples / (double)AudioFormat.SampleRate);
    }

    public void MarkFailed(string? message = null)
    {
        if (!string.IsNullOrWhiteSpace(message))
        {
            failureReasons.Enqueue(message);
            errorInbox.Enqueue(message);
        }
        Interlocked.Exchange(ref failed, 1);
    }

    /// <summary>Asks the owner to end the recording (delivered once, through <see cref="RecordingUpdate.StopRequest"/>).</summary>
    /// <param name="origin">What failed (<see cref="RecordingStopReasons"/>), for the stop line of the log.</param>
    public void RequestStop(string reason, string origin = RecordingStopReasons.PipelineFailed)
    {
        if (Interlocked.CompareExchange(ref stopRequested, 1, 0) != 0) return;
        Volatile.Write(ref stopOrigin, origin);
        Volatile.Write(ref undeliveredStopRequest, reason);
    }

    /// <summary>Moves everything the producers collected to the caller; call it on the UI thread.</summary>
    public RecordingUpdate Drain()
    {
        var levels = Levels;
        var states = new List<TranscriptionEvent>();
        foreach (var source in new[] { AudioSource.Me, AudioSource.Others })
            if (stateInbox.TryRemove(source, out var state)) states.Add(state);
        var microphones = Take(microphoneInbox);
        var stop = Interlocked.Exchange(ref undeliveredStopRequest, null);
        var engineEvents = Take(engineEventInbox);
        return new(
            Take(segmentInbox), Take(recordingGapInbox), Take(transcriptionGapInbox),
            Take(anchorInbox), Take(errorInbox), levels.Me, levels.Others, states,
            stop,
            microphones.Count > 0 ? microphones[^1] : null,
            engineEvents.Select(item => item.Event).ToList(),
            stop is null ? null : Volatile.Read(ref stopOrigin),
            engineEvents.Select(item => item.Generation).ToList());
    }

    /// <summary>The counters of this attempt so far (any thread).</summary>
    public CaptureStatistics Statistics
    {
        get
        {
            ChannelStatistics Channel(int index) =>
                new(Interlocked.Read(ref frames[index]), Interlocked.Read(ref samplesDelivered[index]), Interlocked.Read(ref audibleSamples[index]));
            lock (statisticsGate)
            {
                var gaps = gapTotals.OrderBy(pair => pair.Key.Source).ThenBy(pair => pair.Key.Reason)
                    .Select(pair => new GapTotal(pair.Key.Source, pair.Key.Reason, pair.Value.Count, pair.Value.Seconds)).ToList();
                var me = transcriptionGapTotals.GetValueOrDefault(AudioSource.Me);
                var others = transcriptionGapTotals.GetValueOrDefault(AudioSource.Others);
                return new(Channel(0), Channel(1), gaps, me.Count, me.Seconds, others.Count, others.Seconds, StaleFramesDropped, EngineGeneration);
            }
        }
    }

    /// <summary>True when at least one channel streams and can be reconnected by hand.</summary>
    public bool CanReconnectTranscription => me is IReconnectableTranscriptionPipeline || others is IReconnectableTranscriptionPipeline;

    /// <summary>"Reconnect": restarts both streaming channels, optionally with a new key. Capture is not touched.</summary>
    public void ReconnectTranscription(string? apiKey = null)
    {
        (me as IReconnectableTranscriptionPipeline)?.Reconnect(apiKey);
        (others as IReconnectableTranscriptionPipeline)?.Reconnect(apiKey);
    }

    private static List<T> Take<T>(ConcurrentQueue<T> queue)
    {
        var result = new List<T>();
        while (queue.TryDequeue(out var item)) result.Add(item);
        return result;
    }

    private static double SafeNow(IAudioEngine engine)
    {
        try { return engine.Clock.NowSeconds; }
        catch (Exception) { return 0; }
    }

    private void Attach(Generation generation)
    {
        lock (frameGate)
        {
            if (generation.FrameHandler != null) return;
            generation.FrameHandler = frame => OnFrame(generation, frame);
            generation.EventHandler = engineEvent => OnEngineEvent(generation, engineEvent);
        }
        generation.Engine.FrameReceived += generation.FrameHandler;
        generation.Engine.EventRaised += generation.EventHandler;
    }

    private static void Detach(Generation generation)
    {
        if (generation.FrameHandler is { } frames) generation.Engine.FrameReceived -= frames;
        if (generation.EventHandler is { } events) generation.Engine.EventRaised -= events;
    }

    /// <summary>Must be called under <see cref="frameGate"/>.</summary>
    private void CutoverLocked(Generation generation)
    {
        pending = null;
        var retiring = generations.Where(item => item.Index < generation.Index && !item.Retired).ToList();
        foreach (var item in retiring) item.Retired = true;
        Volatile.Write(ref currentGeneration, generation);
        // The new engine's Others channel starts its own silence measurement.
        var now = SafeNow(generation.Engine);
        Volatile.Write(ref othersLastAudible, Math.Max(Volatile.Read(ref othersLastAudible), now));
        Volatile.Write(ref othersMeasuredFrom, Math.Max(Volatile.Read(ref othersMeasuredFrom), now));
        Array.Clear(audibleBucketSamples);
        // The replacement's own events from before this moment (which endpoint it really opened, a fallback) come first,
        // in order, still under the gate: no later event of this generation can overtake them.
        var early = generation.EarlyEvents.ToList();
        generation.EarlyEvents.Clear();
        foreach (var engineEvent in early) Process(generation, engineEvent);
        generation.Cutover.TrySetResult(true);
        // Never stop an engine on a drain thread or under the gate: a native stop waits for its own threads.
        foreach (var item in retiring) _ = Task.Run(() => RetireAsync(item));
    }

    private void Abandon(Generation generation)
    {
        bool retire;
        lock (frameGate) retire = AbandonLocked(generation);
        if (retire) Retire(generation);
    }

    /// <summary>
    /// Must be called under <see cref="frameGate"/>: the replacement is no longer pending; true when it must be retired
    /// (<see cref="Retire"/>, outside the gate). One that is already current (its first frame arrived) stays.
    /// </summary>
    private bool AbandonLocked(Generation generation)
    {
        if (pending == generation) pending = null;
        if (generation.Retired || generation == currentGeneration) return false;
        generation.Retired = true;
        generation.EarlyEvents.Clear();
        return true;
    }

    private static void Retire(Generation generation)
    {
        generation.Cutover.TrySetResult(false);
        _ = Task.Run(() => RetireAsync(generation));
    }

    private static async Task RetireAsync(Generation generation)
    {
        Detach(generation);
        try { await generation.Starting.WaitAsync(RetireBudget).ConfigureAwait(false); }
        catch (Exception) { }   // a failed or hanging start is stopped and disposed all the same
        try { await generation.Engine.StopAsync().WaitAsync(RetireBudget).ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceWarning("Wallfly: a retired engine did not stop cleanly: " + error.Message); }
        finally
        {
            try { generation.Engine.Dispose(); }
            catch (Exception error) { Trace.TraceWarning("Wallfly: disposing a retired engine failed: " + error.Message); }
        }
    }

    private void OnFrame(Generation generation, AudioFrame frame)
    {
        if (frame.Source is not (AudioSource.Me or AudioSource.Others)) return;
        // Only Samples[ValidLo..ValidHi) was captured. The rest of a partial frame (stream start, resume,
        // end) is neither archived nor transcribed: gaps are never written as silence (spec 3.6), and the
        // timeline jump shows up as a new anchor instead.
        var samples = frame.ValidSamples;
        var offset = frame.ValidOffsetSeconds;
        lock (frameGate)
        {
            if (released) return;
            var current = currentGeneration;
            if (generation.Index < current.Index || generation.Retired) { Interlocked.Increment(ref staleFramesDropped); return; }
            if (generation.Index > current.Index)
            {
                // Once the session is finishing or aborting, no replacement takes over (it is being abandoned).
                if (pending != generation || finishing) { Interlocked.Increment(ref staleFramesDropped); return; }
                CutoverLocked(generation);
            }
            // Fencing: a frame of a stream attempt that has already been superseded is dropped.
            if (frame.AttemptId < generation.Attempts[frame.Source]) { Interlocked.Increment(ref staleFramesDropped); return; }
            generation.Attempts[frame.Source] = frame.AttemptId;
            if (samples.IsEmpty) return;
            // Seamless hand-over: the replacement's first frames start where the previous engine stopped delivering.
            if (highWater.TryGetValue(frame.Source, out var mark) && mark.Generation < generation.Index)
            {
                var skip = (int)Math.Round((mark.End - offset) * AudioFormat.SampleRate);
                if (skip >= samples.Length) { Interlocked.Add(ref overlapSamplesTrimmed, samples.Length); return; }
                if (skip > 0)
                {
                    Interlocked.Add(ref overlapSamplesTrimmed, skip);
                    samples = samples[skip..];
                    offset += skip / (double)AudioFormat.SampleRate;
                }
            }
            var end = offset + samples.Length / (double)AudioFormat.SampleRate;
            var archive = archives[frame.Source];
            if (archive != null)
            {
                if (!expected.TryGetValue(frame.Source, out var next) || Math.Abs(offset - next) * AudioFormat.SampleRate > AnchorToleranceSamples)
                    anchorInbox.Enqueue(new() { File = Path.GetFileName(archive.Path), Frame = archive.SampleCount, Offset = offset });
                archive.Write(samples.Span);
                expected[frame.Source] = end;
            }
            highWater[frame.Source] = (end, generation.Index);
            var channel = frame.Source == AudioSource.Me ? 0 : 1;
            var audible = Peak(samples.Span) >= SilenceThreshold;
            Interlocked.Increment(ref frames[channel]);
            Interlocked.Add(ref samplesDelivered[channel], samples.Length);
            if (audible) Interlocked.Add(ref audibleSamples[channel], samples.Length);
            if (frame.Source == AudioSource.Others && audible)
            {
                Volatile.Write(ref othersLastAudible, end);
                CountAudibleOthers(offset, samples.Length);
            }
            // Under the gate: around an engine switch two drain threads deliver, and each pipeline needs one ordered stream.
            (frame.Source == AudioSource.Me ? me : others).Append(samples, offset);
        }
    }

    private static float Peak(ReadOnlySpan<float> samples)
    {
        var peak = 0f;
        foreach (var sample in samples)
        {
            var magnitude = Math.Abs(sample);
            if (magnitude > peak && float.IsFinite(magnitude)) peak = magnitude;
        }
        return peak;
    }

    /// <summary>A capture failure that ends the recording (macOS: <c>onError</c> → <c>handleCaptureFailure</c>).</summary>
    private static bool IsFatal(AudioEngineEvent engineEvent) => engineEvent.Kind switch
    {
        AudioEngineEventKind.OthersFailed or AudioEngineEventKind.Error => true,
        // The microphone degrades instead: Others keeps recording (macOS "Microphone disconnected – recording Others only").
        AudioEngineEventKind.PermissionDenied or AudioEngineEventKind.DriverBlocked => engineEvent.Source != AudioSource.Me,
        _ => false
    };

    /// <summary>
    /// The native layer moved the device loopback of the Others channel to another output endpoint by itself (2026-11-23):
    /// a followed default change (<see cref="AudioEngineEventKind.DeviceSwitched"/>), an invalidated loopback
    /// (<see cref="AudioEngineEventKind.DeviceLost"/>) or a lost pinned endpoint's fallback to the default
    /// (<see cref="AudioEngineEventKind.DeviceAcquired"/> with <c>fallback:</c>). A Reference switch (process-loopback modes:
    /// only the keep-alive and the echo path moved) leaves the Others capture as it is.
    /// </summary>
    public static bool MovesOthersCapture(AudioEngineEvent engineEvent) => engineEvent.Source == AudioSource.Others && engineEvent.Kind switch
    {
        AudioEngineEventKind.DeviceSwitched or AudioEngineEventKind.DeviceLost => true,
        AudioEngineEventKind.DeviceAcquired => engineEvent.Fields.IsFallback,
        _ => false
    };

    /// <summary>
    /// Starts a fresh silence measurement of the Others channel at the current meeting time, as a cutover does: the guard judges
    /// a capture only after it ran a whole window. Used when the native layer moved the capture to another endpoint.
    /// </summary>
    public void RestartOthersMeasurement() => RestartOthersMeasurement(Volatile.Read(ref currentGeneration));

    private void RestartOthersMeasurement(Generation generation)
    {
        lock (frameGate)
        {
            if (generation != currentGeneration) return;
            var now = SafeNow(generation.Engine);
            Volatile.Write(ref othersLastAudible, Math.Max(Volatile.Read(ref othersLastAudible), now));
            Volatile.Write(ref othersMeasuredFrom, Math.Max(Volatile.Read(ref othersMeasuredFrom), now));
            Array.Clear(audibleBucketSamples);
        }
    }

    private static string Describe(AudioEngineEvent engineEvent) =>
        $"{engineEvent.Source}: {engineEvent.Kind}{(engineEvent.Detail.Length > 0 ? " · " + engineEvent.Detail : "")}";

    /// <summary>A replacement keeps at most this many events before its cutover (a start reports a handful).</summary>
    private const int MaxEarlyEvents = 64;

    private void OnEngineEvent(Generation generation, AudioEngineEvent engineEvent)
    {
        var retire = false;
        lock (frameGate)
        {
            var current = currentGeneration;
            if (generation.Index < current.Index || generation.Retired && generation != current) return;
            if (generation.Index > current.Index)
            {
                // A replacement that fails before its cutover is dropped; the current engine keeps recording.
                if (IsFatal(engineEvent) || engineEvent.Kind is AudioEngineEventKind.PermissionDenied or AudioEngineEventKind.DriverBlocked)
                    retire = AbandonLocked(generation);
                // Its other events wait for the cutover: the owner must learn which endpoint it really opened (fallback:).
                // A gap before the cutover is none of the recording's: the current engine still delivers that time.
                else if (pending == generation && !finishing && engineEvent.Kind != AudioEngineEventKind.Gap && generation.EarlyEvents.Count < MaxEarlyEvents)
                    generation.EarlyEvents.Add(engineEvent);
                if (!retire) return;
            }
        }
        if (retire)
        {
            Retire(generation);
            return;
        }
        Process(generation, engineEvent);
    }

    /// <summary>One event of the current generation (or, at its cutover, a buffered one of the replacement).</summary>
    private void Process(Generation generation, AudioEngineEvent engineEvent)
    {
        if (engineEvent.Kind == AudioEngineEventKind.Gap)
        {
            lock (statisticsGate)
            {
                var key = (engineEvent.Source, engineEvent.Reason);
                var total = gapTotals.GetValueOrDefault(key);
                gapTotals[key] = (total.Count + 1, total.Seconds + Math.Max(0, engineEvent.DurationSeconds));
            }
        }
        else engineEventInbox.Enqueue((engineEvent, generation.Index));
        switch (engineEvent.Kind)
        {
            case AudioEngineEventKind.Gap:
                recordingGapInbox.Enqueue(new()
                {
                    Start = engineEvent.AtOffsetSeconds,
                    Duration = engineEvent.DurationSeconds,
                    Source = engineEvent.Source switch
                    {
                        AudioSource.Me => CaptureSource.Microphone,
                        AudioSource.Reference => CaptureSource.Reference,
                        _ => CaptureSource.Meeting
                    },
                    Reason = engineEvent.Reason.ToString()
                });
                break;
            case AudioEngineEventKind.DeviceAcquired when engineEvent.Source == AudioSource.Me:
                // Spec 3.1: which microphone runs ("Name · Fallback (reason)"); a fallback is a state, not an error.
                microphoneInbox.Enqueue(engineEvent);
                break;
            case AudioEngineEventKind.PermissionDenied:
            case AudioEngineEventKind.DriverBlocked:
            case AudioEngineEventKind.OthersFailed:
            case AudioEngineEventKind.Error:
                var message = Describe(engineEvent);
                MarkFailed(message);
                if (IsFatal(engineEvent)) RequestStop(message, RecordingStopReasons.EngineFailed);
                break;
            case AudioEngineEventKind.DeviceAcquired:
            case AudioEngineEventKind.DeviceSwitched:
            case AudioEngineEventKind.DeviceLost:
            case AudioEngineEventKind.FallbackDeviceLoopback:
                // A state, not an error: the owner shows a transient notice and logs it (EngineEvents). When the native layer
                // moved the Others capture to another output endpoint, that capture is measured afresh, as after a cutover.
                if (MovesOthersCapture(engineEvent)) RestartOthersMeasurement(generation);
                break;
            case AudioEngineEventKind.Warning:
                // Never fatal (WF_EVENT_WARNING, e.g. the keep-alive render stream): logged by the owner, no processing error.
                break;
            default:
                if (engineEvent.Detail.Length > 0) errorInbox.Enqueue($"{engineEvent.Source}: {engineEvent.Detail}");
                break;
        }
    }

    private void OnPipelineFailed(string message)
    {
        MarkFailed(message);
        RequestStop(message, RecordingStopReasons.PipelineFailed);
    }

    private void OnPipelineEvent(TranscriptionEvent pipelineEvent)
    {
        if (pipelineEvent.Kind is TranscriptionEventKind.State or TranscriptionEventKind.Paused)
        {
            // A paused channel is a visible state, not a capture failure: recording continues.
            stateInbox[pipelineEvent.Source] = pipelineEvent;
            return;
        }
        if (pipelineEvent.Kind != TranscriptionEventKind.Gap) return;
        lock (statisticsGate)
        {
            var total = transcriptionGapTotals.GetValueOrDefault(pipelineEvent.Source);
            transcriptionGapTotals[pipelineEvent.Source] = (total.Count + 1, total.Seconds + Math.Max(0, pipelineEvent.Duration));
        }
        transcriptionGapInbox.Enqueue(new()
        {
            Start = pipelineEvent.Start,
            Duration = pipelineEvent.Duration,
            Speaker = pipelineEvent.Source == AudioSource.Me ? Speaker.Me : Speaker.Others,
            Reason = pipelineEvent.Detail
        });
    }

    private void OnSegment(TranscriptionSegment segment)
    {
        if (segment.SessionId != null && segment.SessionId != SessionId) return;
        segmentInbox.Enqueue(new()
        {
            Id = segment.Id,
            Speaker = segment.Source == AudioSource.Me ? Speaker.Me : Speaker.Others,
            Offset = segment.Offset,
            Duration = segment.Duration,
            Text = segment.Text,
            IsFinal = segment.IsFinal,
            SessionID = SessionId,
            ConnectionID = segment.ConnectionId,
            Words = segment.Words?.Select(word => new TranscriptWord { Text = word.Text, Start = word.Start, End = word.End }).ToList()
        });
    }

    private string WithFailureReasons(string summary) => string.Join('\n', failureReasons.ToArray()
        .Concat([me.FailureReason, others.FailureReason]).Where(message => !string.IsNullOrWhiteSpace(message)).Distinct().Append(summary));

    /// <summary>
    /// Stop sequence of spec 4.5 inside one budget: stop the engine, stop the Teams speaker watcher
    /// (<paramref name="stopObserving"/>, stage "stopping speaker detection"), finish both pipelines, release the
    /// archives. On timeout or cancellation the caller gets the stage that was hanging.
    /// </summary>
    public async Task<DrainResult> FinishAsync(TimeSpan timeout, CancellationToken cancellationToken = default, Func<Task>? stopObserving = null)
    {
        var stage = CoreMessages.StageStopAudio;
        async Task Drain()
        {
            var current = BeginFinishing();
            await current.Engine.StopAsync().ConfigureAwait(false);
            if (stopObserving != null)
            {
                stage = CoreMessages.StageStopSpeakerDetection;
                await stopObserving().ConfigureAwait(false);
            }
            stage = CoreMessages.StageFinishTranscription;
            await Task.WhenAll(me.FinishAsync(), others.FinishAsync()).ConfigureAwait(false);
            stage = CoreMessages.StageReleaseAudio;
            Release();
        }
        var draining = Task.Run(Drain, CancellationToken.None);
        try
        {
            await draining.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            var complete = !AudioFailed && !me.HasFailed && !others.HasFailed;
            return new(complete, complete ? null : WithFailureReasons(CoreMessages.DrainIncomplete));
        }
        catch (Exception error)
        {
            MarkFailed();
            var cause = error is TimeoutException ? CoreMessages.DrainTimedOut
                : error is OperationCanceledException ? CoreMessages.DrainCancelled
                : CoreMessages.DrainRecordingError;
            return new(false, WithFailureReasons(CoreMessages.DrainStageFailure(stage, cause)));
        }
        finally
        {
            await me.CancelAsync().ConfigureAwait(false);
            await others.CancelAsync().ConfigureAwait(false);
            if (!draining.IsCompletedSuccessfully)
                _ = draining.ContinueWith(task => { _ = task.Exception; Release(); }, TaskScheduler.Default);
        }
    }

    /// <summary>
    /// Tears down an attempt that started capturing but is not used (the start was abandoned): stops the engines within
    /// <paramref name="budget"/>, drops pending transcription and releases the archives. Nothing is finished or reported.
    /// </summary>
    public async Task AbortAsync(TimeSpan budget)
    {
        var current = BeginFinishing();
        try { await current.Engine.StopAsync().WaitAsync(budget).ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceWarning("Wallfly: stopping an abandoned recording failed: " + error.Message); }
        await me.CancelAsync().ConfigureAwait(false);
        await others.CancelAsync().ConfigureAwait(false);
        Release();
    }

    /// <summary>
    /// Test seam: runs right after <see cref="FinishAsync"/> / <see cref="AbortAsync"/> marked the session finishing (and
    /// abandoned a pending replacement), before the current engine is stopped.
    /// </summary>
    internal Action? FinishingMarked { get; set; }

    /// <summary>
    /// Marks the session finishing and, under the same gate, abandons a replacement that is still pending, so no cutover can
    /// happen any more: the engine returned is the one the stop drains, and the abandoned replacement is stopped and disposed
    /// by its retirement. Returns the current generation.
    /// </summary>
    private Generation BeginFinishing()
    {
        Generation? abandoned = null;
        Generation current;
        lock (frameGate)
        {
            finishing = true;
            if (pending is { } replacement && AbandonLocked(replacement)) abandoned = replacement;
            current = currentGeneration;
        }
        if (abandoned != null) Retire(abandoned);
        FinishingMarked?.Invoke();
        return current;
    }

    private void Release()
    {
        List<Generation> owned;
        lock (frameGate)
        {
            if (released) return;
            released = true;
            finishing = true;
            owned = generations.Where(item => !item.Retired).ToList();
            foreach (var item in owned) item.Retired = true;
            foreach (var archive in archives.Values) archive?.Dispose();
        }
        foreach (var generation in owned)
        {
            Detach(generation);
            generation.Cutover.TrySetResult(generation == Volatile.Read(ref currentGeneration));
            try { generation.Engine.Dispose(); }
            catch (Exception error) { Trace.TraceWarning("Wallfly: disposing the engine failed: " + error.Message); }
        }
    }
}
