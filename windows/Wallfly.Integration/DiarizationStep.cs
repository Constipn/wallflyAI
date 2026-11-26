using System.Diagnostics;
using System.Globalization;
using System.Net.NetworkInformation;
using Wallfly.Core;
using Wallfly.Core.Diagnostics;
using Wallfly.Core.Json;
using Wallfly.Core.Services;
using Wallfly.Core.Text;
using Wallfly.Diarization;
using Wallfly.Speakers;
using Wallfly.Transcription.Deepgram;

namespace Wallfly.Integration;

/// <summary>When the microphone archive is diarized as well (AP5).</summary>
public enum MicrophonePass
{
    /// <summary>Whenever the microphone archive exists and the Me channel has timed words (several people in one room).</summary>
    Always,
    /// <summary>Only when the Others pass cannot run (no timed remote words, no or silent Others archive).</summary>
    WhenOthersUnusable,
    /// <summary>
    /// Never (the default since 25.11.2026): the microphone track is always the user ("Me"), never split; the run's progress
    /// covers the Others pass alone.
    /// </summary>
    Never
}

/// <summary>
/// Automatic retries of a cloud pass that failed for a transient reason (plan step 6). Defaults: retries after 1, 5, 15 and
/// then every 60 minutes, at most <see cref="MaxAttempts"/> attempts per request, again at each launch and when Windows
/// reports the network back (those extra attempts do not count, but come at most every <see cref="NetworkRetrySpacing"/>).
/// </summary>
public sealed record CloudRetryPolicy
{
    /// <summary>Delay after the n-th failed attempt (the last value repeats).</summary>
    public IReadOnlyList<TimeSpan> Delays { get; init; } = [TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(5), TimeSpan.FromMinutes(15), TimeSpan.FromMinutes(60)];
    /// <summary>
    /// Attempts per request, the first run and runs re-queued after an interrupting quit included: 8 (the first run plus 7
    /// retries, about 4 h 20 min of waiting with the default delays). Then the pass fails; "Re-detect speakers" starts over.
    /// </summary>
    public int MaxAttempts { get; init; } = 8;
    /// <summary>A server's <c>Retry-After</c> longer than the schedule is honoured up to this long.</summary>
    public TimeSpan MaxRetryAfter { get; init; } = TimeSpan.FromHours(2);
    /// <summary>Passes still waiting at a launch (or after a quit was called off) run this long after it.</summary>
    public TimeSpan StartupDelay { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A run held back (or paused) by a recording checks again this often (a stop releases it at once).</summary>
    public TimeSpan RecordingRecheck { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Waiting passes run at once when Windows reports the network available again (<see cref="NetworkChange"/>).</summary>
    public bool RetryWhenNetworkReturns { get; init; } = true;
    /// <summary>
    /// An attempt because the network returned does not count toward <see cref="MaxAttempts"/> (a flapping connection must
    /// not use the attempts up) and does not move the scheduled next attempt; it comes at most this often per meeting and never
    /// sooner than this after the meeting's last attempt ended (a flapping adapter fires the event many times a minute).
    /// </summary>
    public TimeSpan NetworkRetrySpacing { get; init; } = TimeSpan.FromSeconds(60);

    /// <summary>The wait after <paramref name="attempts"/> failed attempts: the schedule, or a longer (capped) <c>Retry-After</c>.</summary>
    public TimeSpan DelayAfter(int attempts, TimeSpan? retryAfter)
    {
        var schedule = Delays.Count == 0 ? TimeSpan.FromMinutes(1) : Delays[Math.Clamp(attempts - 1, 0, Delays.Count - 1)];
        if (retryAfter is not { } wait || wait <= schedule) return schedule;
        return wait < MaxRetryAfter ? wait : MaxRetryAfter;
    }
}

/// <summary>The engines and policies of <see cref="DiarizationStep"/> beyond the local diarizer.</summary>
public sealed record DiarizationStepOptions
{
    /// <summary>
    /// Creates the cloud diarizer of one run from the current Deepgram key and that run's options (language, hints, keyterms);
    /// null: <see cref="DeepgramPrerecordedDiarizer"/> with its own HTTP handler. A test seam (fake <see cref="HttpMessageHandler"/>).
    /// </summary>
    public Func<string, DeepgramPrerecordedOptions, DeepgramPrerecordedDiarizer>? CloudDiarizer { get; init; }
    public CloudRetryPolicy Retry { get; init; } = new();
    /// <summary>
    /// The Teams names alone may settle the Others channel (<see cref="Diarization.TeamsOnlyNaming"/>, plan step 5); false always
    /// runs the engine (tests of the voice path).
    /// </summary>
    public bool TeamsOnlyNaming { get; init; } = true;
    public TeamsNamingOptions? TeamsNaming { get; init; }
    public SelfCheckOptions? SelfCheck { get; init; }
    /// <summary>
    /// Where the encoded cloud upload of a meeting is kept between the attempts of one request
    /// (<see cref="DeepgramPrerecordedOptions.UploadCachePath"/>: <c>&lt;meeting id&gt;.opus</c>; written only when the track was
    /// compressed, i.e. the default <see cref="CloudUploadFormat.Auto"/> switched to Ogg/Opus on a slow connection, and then
    /// sent by the next attempt without a PCM probe). Deleted when the request
    /// ends (completed, failed for good, attempts used up, engine changed), when the meeting or its processed audio is removed,
    /// and at the next launch for every meeting that no longer waits. Null (the default of the class, for tests): no cache,
    /// each attempt encodes into a self-deleting temporary file. The app uses <see cref="DefaultUploadCacheDirectory"/>.
    /// </summary>
    public string? UploadCacheDirectory { get; init; }

    /// <summary>%TEMP%\Wallfly\upload-cache: the app's own temporary area (never inside the library, which may move or sync).</summary>
    public static string DefaultUploadCacheDirectory => Path.Combine(Path.GetTempPath(), "Wallfly", "upload-cache");
}

/// <summary>
/// Speaker matching after a stop and for "Re-detect speakers" (spec 5.3 / 5.4; macOS <c>rediarize</c>,
/// <c>runDiarization</c>, <c>finishDiarization</c>): runs are serial, never block a recording and can be cancelled; the
/// diarizer works off the UI thread. Each channel is handled on its own: the Others archives (as on macOS) and,
/// only when <see cref="MicrophonePass"/> asks for it, the microphone archives (default: never; the microphone stays "Me").
/// A channel's pass runs only with acoustic speaker recognition on, kept audio, timed words on that channel and existing
/// archives that are not silent; otherwise that channel is recorded as skipped (<c>Diarization.Channels</c>; the top-level
/// fields describe the Others pass).
/// </summary>
/// <remarks>
/// The Others pass picks its engine per run (settings and key are read when the run starts, so a change applies to the next
/// run without a restart):
/// <list type="number">
/// <item>Teams alone (<see cref="TeamsOnlyNaming"/>): when the stored Teams evidence names the remote speech clearly enough,
/// no diarizer runs; the words get the tile names and the pass completes with engine <see cref="TeamsEngine"/>.</item>
/// <item>Deepgram (cloud, <see cref="SpeakerSeparationEngine.Deepgram"/>): <see cref="DeepgramPrerecordedDiarizer"/>, then
/// <see cref="DeepgramSelfCheck"/>, <see cref="DeepgramTeamsGuard"/> and the fusion. Without a key the pass is skipped (it never
/// falls back to the local model). Transient failures wait for the network and retry (<see cref="CloudRetryPolicy"/>; the
/// upload goes uncompressed and is compressed only on a slow connection; such an encoded upload is kept between the attempts,
/// <see cref="DiarizationStepOptions.UploadCacheDirectory"/>); a quit that
/// interrupts a cloud pass queues it again at the next launch; permanent or untrustworthy results fail.</item>
/// <item>On this device (<see cref="SpeakerSeparationEngine.OnDevice"/>, or "Re-detect speakers on this device", which skips the
/// Teams-only shortcut): the local diarizer, then <see cref="TeamsVoiceGuard"/> and the fusion.</item>
/// </list>
/// Neither engine loads the laptop while any recording is active: a run that would start then is held back (persisted as
/// waiting for the recording, so a quit does not lose it; released by the next stop or a recheck). A recording that starts
/// while a run is going: a cloud run that is still encoding or uploading is cancelled and held back the same way (the
/// attempt does not count); a cloud run that only waits for Deepgram's answer finishes. A local run is paused
/// (<see cref="IPausableDiarizer"/>: the native sherpa-onnx call cannot be cancelled, but it is held at its next progress
/// callback and its threads drop to the idle priority) and continues after the recording; when the recording is of the same
/// meeting, its result would be discarded anyway, so it is cancelled and held back instead. The Teams-only path costs nothing
/// and is never held. The microphone track is the user ("Me"): with <see cref="MicrophonePass.Never"/> the microphone labels
/// of an older version are removed at the launch and by every run (<see cref="AcousticSpeakerLabels.ClearMicrophoneLabels"/>).
/// A run with at least one completed pass is applied to a copy (archive → meeting time, fusion with the Teams labels, the
/// microphone rule) and saved first; only then it replaces the live meeting and, when the Others pass completed and the
/// microphone pass did not fail, the host evaluates the audio deletion policy. Failures keep the transcript and the audio.
/// </remarks>
public sealed class DiarizationStep : IPostRecordingStep, IDisposable
{
    public const string ProgressText = "Matching speakers…";
    /// <summary>
    /// Cloud pass: before the first upload byte, and while the Others track is compressed (explicit Ogg/Opus, or the default
    /// <see cref="CloudUploadFormat.Auto"/> after it abandoned a PCM upload that was too slow: "Uploading… 12%" then becomes
    /// "Preparing upload… 0%", then "Uploading…" from 0 % for the smaller file).
    /// </summary>
    public const string CloudPreparingText = "Preparing upload…";
    /// <summary>Cloud pass: "Uploading… 40%".</summary>
    public const string CloudUploadingText = "Uploading…";
    /// <summary>Cloud pass: the upload is done, Deepgram works.</summary>
    public const string CloudProcessingText = "Separating speakers (cloud)…";
    /// <summary>Engine id of an Others pass the Teams names alone settled (no diarizer ran).</summary>
    public const string TeamsEngine = "teams";
    /// <summary>A local run paused by a recording.</summary>
    public const string PausedText = "Matching speakers paused until the recording ends…";

    /// <summary>
    /// Why a run was queued: after a stop or by the user, as an automatic cloud retry, explicitly on this device, or as an extra
    /// cloud attempt because the network returned (it does not count toward the attempts).
    /// </summary>
    private enum RunKind { Normal, Retry, OnDevice, NetworkRetry }

    /// <summary>How the Others pass runs; None: it is skipped.</summary>
    private enum OthersPath { None, Teams, Cloud, Local }

    /// <param name="OthersSkip">Why the Others pass does not run (null: it runs on <paramref name="OthersFiles"/>).</param>
    /// <param name="Diarizer">The Others engine (also only a label: Teams, or a skipped pass).</param>
    /// <param name="KeepNextAttemptAt">A network-return attempt keeps the scheduled next attempt when it fails again.</param>
    private sealed record Job(Guid MeetingID, List<string> OthersFiles, List<string> MeFiles, string? OthersSkip, string? MeSkip, IDiarizer Diarizer, IDiarizer MeDiarizer,
        DateTimeOffset StartedAt, OthersPath Path, DeepgramPrerecordedDiarizer? Cloud, int? Attempts, RunKind Kind, RunControl Control, DateTimeOffset? KeepNextAttemptAt);

    /// <summary>The run that is going: what a recording that starts must hold back.</summary>
    private sealed class RunControl(Guid meetingID, OthersPath path, bool localMicrophone, CancellationToken stepToken) : IDisposable
    {
        private int phase = -1;
        private int preempted;

        public Guid MeetingID => meetingID;
        public OthersPath Path => path;
        /// <summary>The local engine runs in this run (the Others pass on this device, or the microphone pass).</summary>
        public bool UsesLocalEngine => path == OthersPath.Local || localMicrophone;
        /// <summary>Cancels the passes of this run (linked to the step's cancellation).</summary>
        public CancellationTokenSource Source { get; } = CancellationTokenSource.CreateLinkedTokenSource(stepToken);
        /// <summary>The cloud phase last reported (null before the first report).</summary>
        public CloudDiarizationPhase? Phase
        {
            get => Volatile.Read(ref phase) is var value && value >= 0 ? (CloudDiarizationPhase)value : null;
            set => Volatile.Write(ref phase, value is { } known ? (int)known : -1);
        }
        /// <summary>Cancelled because a recording started: the run is held back, not failed.</summary>
        public bool Preempted => Volatile.Read(ref preempted) == 1;

        public void Preempt()
        {
            Volatile.Write(ref preempted, 1);
            try { Source.Cancel(); }
            catch (ObjectDisposedException) { }
        }

        public void Dispose() => Source.Dispose();
    }

    /// <summary>One channel's pass: a result, a skip reason (<see cref="Status"/> Skipped) or a failure.</summary>
    private sealed record Outcome(DiarizationResult? Result, DiarizationStatus Status, string? Message, Exception? Failure, TimeSpan Elapsed);

    /// <summary>Engine id, model and threshold for a run state when no diarizer instance stands behind them.</summary>
    private sealed class EngineLabel(string engine, string? modelVersion = null, double? threshold = null) : IDiarizer
    {
        public string Engine => engine;
        public string? ModelVersion => modelVersion;
        public double? Threshold => threshold;
        public Task<DiarizationResult> RunAsync(IReadOnlyList<string> othersWavPaths, IProgress<double>? progress, CancellationToken cancellationToken) =>
            throw new NotSupportedException("An engine label cannot diarize.");
    }

    private static readonly IDiarizer TeamsLabel = new EngineLabel(TeamsEngine);
    private static readonly IDiarizer CloudLabel = new EngineLabel(DeepgramPrerecordedDiarizer.EngineName);

    private readonly Lazy<IDiarizer> diarizer;
    private readonly Lazy<IDiarizer>? microphoneDiarizer;
    private readonly FusionPolicy? fusionPolicy;
    private readonly MicrophonePass microphonePass;
    private readonly DiarizationStepOptions options;
    private readonly object gate = new();
    private readonly List<Guid> queue = [];
    /// <summary>The kind of each queued run.</summary>
    private readonly Dictionary<Guid, RunKind> kinds = [];
    private readonly HashSet<Guid> requeue = [];
    private readonly List<(Guid MeetingID, RunKind Kind)> abandoned = [];
    /// <summary>Runs held back while a recording is active (both engines).</summary>
    private readonly Dictionary<Guid, RunKind> deferred = [];
    /// <summary>When the network-return attempt of a meeting last started (rate limit).</summary>
    private readonly Dictionary<Guid, DateTimeOffset> networkRetries = [];
    /// <summary>The run that is going (null between runs).</summary>
    private RunControl? active;
    /// <summary>The local engines are paused by a recording (released when none is active).</summary>
    private bool localHeld;
    private IMeetingHost? host;
    private Task tail = Task.CompletedTask;
    private CancellationTokenSource cancellation = new();
    /// <summary>Cancels the pending retry and recheck timers (quit, dispose).</summary>
    private CancellationTokenSource timers = new();
    private bool cancelled;
    private bool watchingNetwork;
    private string progress = "";

    /// <param name="diarizerFactory">The local diarizer (created once, on first use by a local pass).</param>
    /// <param name="microphoneDiarizerFactory">The diarizer of the microphone pass (its own clustering threshold); null: the same as Others.</param>
    public DiarizationStep(Func<IDiarizer> diarizerFactory, FusionPolicy? fusionPolicy = null, MicrophonePass microphonePass = MicrophonePass.Never,
        Func<IDiarizer>? microphoneDiarizerFactory = null, DiarizationStepOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(diarizerFactory);
        diarizer = new Lazy<IDiarizer>(diarizerFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        if (microphoneDiarizerFactory is not null) microphoneDiarizer = new Lazy<IDiarizer>(microphoneDiarizerFactory, LazyThreadSafetyMode.ExecutionAndPublication);
        this.fusionPolicy = fusionPolicy;
        this.microphonePass = microphonePass;
        this.options = options ?? new DiarizationStepOptions();
    }

    public MicrophonePass MicrophonePass => microphonePass;

    public string Progress { get { lock (gate) return progress; } }

    /// <summary>Queued or running runs, and local runs held back by a recording.</summary>
    public bool HasPendingWork { get { lock (gate) return queue.Count > 0 || deferred.Count > 0; } }

    public bool IsPending(Guid meetingID) { lock (gate) return queue.Contains(meetingID) || deferred.ContainsKey(meetingID); }

    /// <summary>
    /// Attaches the host and repairs runs a quit interrupted (macOS: shown as failed instead of running forever, saved
    /// once so the repair does not repeat at every launch). An interrupted cloud pass is queued again instead (within the
    /// attempt cap), and passes still waiting for the network get their retry shortly after the launch.
    /// </summary>
    public void Attach(IMeetingHost host)
    {
        this.host = host;
        var now = host.Now;
        var cleared = 0;
        foreach (var meeting in host.Meetings.ToList())
        {
            var repair = DiarizationRunStates.RepairInterrupted(meeting, DeepgramPrerecordedDiarizer.EngineName, options.Retry.MaxAttempts, now);
            // The microphone is never split (MicrophonePass.Never): an older version's split must not come back with the next
            // re-apply or show when the meeting is opened. Manual labels stay (AcousticSpeakerLabels.ClearMicrophoneLabels).
            var microphone = microphonePass == MicrophonePass.Never && meeting.ClearMicrophoneLabels();
            if (microphone) cleared++;
            if (repair != DiarizationRunStates.InterruptedRepair.None || microphone) host.Persist(meeting);
            if (repair == DiarizationRunStates.InterruptedRepair.Requeued)
                WallflyLog.Info($"Wallfly: speaker matching meeting={meeting.Id:D} channel=others interrupted cloud pass queued again");
            if (DiarizationRunStates.IsWaiting(meeting)) ScheduleRetry(meeting.Id, options.Retry.StartupDelay, deferredLocal: false);
        }
        if (cleared > 0) WallflyLog.Info(string.Create(CultureInfo.InvariantCulture, $"Wallfly: speaker matching removed the microphone split of {cleared} older meetings"));
        CleanUploadCache(host);
        if (options.Retry.RetryWhenNetworkReturns)
        {
            NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
            watchingNetwork = true;
        }
    }

    /// <summary>
    /// A recording is starting (UI thread). A cloud run still encoding or uploading is cancelled and held back until no
    /// recording is active (the attempt does not count); one that only waits for Deepgram's answer finishes. A local run is
    /// paused (its native call cannot be cancelled; see <see cref="IPausableDiarizer"/>) and continues afterwards. A run of the
    /// meeting that now records is cancelled and held back whatever its engine (its result would be discarded).
    /// </summary>
    public void RecordingStarted(Guid meetingID)
    {
        if (host is not { } current || cancelled) return;
        RunControl? run;
        lock (gate) run = active;
        if (run is null || run.Path == OthersPath.Teams && !run.UsesLocalEngine) return;
        if (run.UsesLocalEngine) HoldLocalEngines();
        var preempt = run.MeetingID == meetingID || run.Path == OthersPath.Cloud && run.Phase != CloudDiarizationPhase.Processing;
        if (preempt)
        {
            WallflyLog.Info($"Wallfly: speaker matching meeting={run.MeetingID:D} held back: a recording started (engine={(run.Path == OthersPath.Cloud ? "cloud" : "local")} phase={run.Phase?.ToString() ?? "-"})");
            run.Preempt();
        }
        else if (run.UsesLocalEngine)
        {
            WallflyLog.Info($"Wallfly: speaker matching meeting={run.MeetingID:D} paused: a recording started (local engine)");
            lock (gate) progress = PausedText;
            current.StateChanged();
        }
        // The pause and the held-back run are released by the next stop or this recheck.
        ScheduleRetry(run.MeetingID, options.Retry.RecordingRecheck, deferredLocal: true);
    }

    /// <summary>The meeting or its processed audio is gone: its cached upload goes too.</summary>
    public void MeetingAudioRemoved(Guid meetingID) => DropUploadCache(meetingID);

    private void HoldLocalEngines()
    {
        localHeld = true;
        if (diarizer.IsValueCreated) (diarizer.Value as IPausableDiarizer)?.Pause();
        if (microphoneDiarizer is { IsValueCreated: true }) (microphoneDiarizer.Value as IPausableDiarizer)?.Pause();
    }

    private void ReleaseLocalEngines()
    {
        if (!localHeld) return;
        localHeld = false;
        if (diarizer.IsValueCreated) (diarizer.Value as IPausableDiarizer)?.Resume();
        if (microphoneDiarizer is { IsValueCreated: true }) (microphoneDiarizer.Value as IPausableDiarizer)?.Resume();
        lock (gate) if (progress == PausedText) progress = ProgressText;
    }

    /// <summary>The cached upload of a meeting (null without a cache directory).</summary>
    private string? UploadCachePath(Guid meetingID) =>
        options.UploadCacheDirectory is { Length: > 0 } directory ? Path.Combine(directory, meetingID.ToString("N", CultureInfo.InvariantCulture) + ".opus") : null;

    /// <summary>Deletes the meeting's cached upload and its key (the request ended or the audio is gone).</summary>
    private void DropUploadCache(Guid meetingID)
    {
        if (UploadCachePath(meetingID) is not { } path) return;
        foreach (var file in new[] { path, path + ".key", path + ".partial" })
        {
            try { if (File.Exists(file)) File.Delete(file); }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            {
                Trace.TraceWarning("Wallfly: removing a cached cloud upload failed: " + error.GetType().Name);
            }
        }
    }

    /// <summary>At the launch: every cached upload except those of meetings still waiting for a cloud attempt is removed.</summary>
    private void CleanUploadCache(IMeetingHost current)
    {
        if (options.UploadCacheDirectory is not { Length: > 0 } directory || !Directory.Exists(directory)) return;
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                var name = Path.GetFileName(file);
                var keep = name.Length > 32 && Guid.TryParseExact(name[..32], "N", out var id) && current.Find(id) is { } meeting && DiarizationRunStates.IsWaiting(meeting);
                if (keep) continue;
                try { File.Delete(file); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
            }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            Trace.TraceWarning("Wallfly: cleaning the cloud upload cache failed: " + error.GetType().Name);
        }
    }

    public bool CanRun(Guid meetingID)
    {
        if (host is not { } current || cancelled || current.IsClosing || current.IsMovingLibrary || IsPending(meetingID)) return false;
        if (!current.Settings.AcousticSpeakerRecognition || !current.Settings.KeepAudio || current.RecordingMeetingID == meetingID) return false;
        if (current.Find(meetingID) is not { } meeting) return false;
        var (othersSkip, meSkip) = SkipReasons(current, meeting);
        return othersSkip is null || meSkip is null;
    }

    public bool Enqueue(Guid meetingID)
    {
        var queued = EnqueueCore(meetingID, RunKind.Normal);
        ReleaseDeferred();
        return queued;
    }

    /// <summary>"Re-detect speakers on this device": the local engine, whatever the speaker separation setting says.</summary>
    public bool EnqueueOnDevice(Guid meetingID)
    {
        var queued = EnqueueCore(meetingID, RunKind.OnDevice);
        ReleaseDeferred();
        return queued;
    }

    private bool EnqueueCore(Guid meetingID, RunKind kind)
    {
        if (host is not { } current || cancelled || current.IsClosing || current.IsMovingLibrary) return false;
        // A run already queued for this meeting checks at its end whether the recordings changed meanwhile.
        if (IsPending(meetingID) || current.RecordingMeetingID == meetingID || current.Find(meetingID) is not { } meeting) return false;
        // Switched off: nothing runs and nothing is recorded; earlier results stay as they are.
        if (!current.Settings.AcousticSpeakerRecognition) return false;
        var (othersSkip, meSkip) = SkipReasons(current, meeting);
        if (othersSkip != null && meSkip != null)
        {
            SkipBoth(current, meeting, othersSkip, meSkip, ConfiguredEngine(current, kind));
            return false;
        }
        Task previous;
        CancellationToken token;
        lock (gate)
        {
            queue.Add(meetingID);
            kinds[meetingID] = kind;
            previous = tail;
            token = cancellation.Token;
            tail = RunAfterAsync(previous, meetingID, token);
        }
        current.StateChanged();
        return true;
    }

    public async Task WhenIdle()
    {
        Task? awaited = null;
        while (true)
        {
            Task current;
            lock (gate) current = tail;
            if (current == awaited) return;
            awaited = current;
            try { await current.ConfigureAwait(false); }
            catch (Exception) { }
        }
    }

    public void Cancel()
    {
        lock (gate)
        {
            if (cancelled) return;
            cancelled = true;
            abandoned.Clear();
            abandoned.AddRange(queue.Select(id => (id, kinds.GetValueOrDefault(id))));
            abandoned.AddRange(deferred.Select(pair => (pair.Key, pair.Value)));
            deferred.Clear();
            cancellation.Cancel();
            timers.Cancel();
        }
    }

    public void Resume()
    {
        List<(Guid MeetingID, RunKind Kind)> again;
        lock (gate)
        {
            if (!cancelled) return;
            cancelled = false;
            cancellation.Dispose();
            cancellation = new();
            timers.Dispose();
            timers = new();
            again = [.. abandoned];
            abandoned.Clear();
        }
        foreach (var (meetingID, kind) in again)
        {
            // A run that is still unwinding queues the meeting again when it ends.
            if (IsPending(meetingID)) { lock (gate) requeue.Add(meetingID); }
            else EnqueueCore(meetingID, kind);
        }
        // The quit cancelled the retry timers of passes waiting for the network (or for a recording) and the recheck of a pause.
        if (host is { } current)
        {
            foreach (var meeting in current.Meetings.Where(DiarizationRunStates.IsWaiting).ToList())
                ScheduleRetry(meeting.Id, options.Retry.StartupDelay, deferredLocal: false);
            if (localHeld) ScheduleRetry(Guid.Empty, options.Retry.RecordingRecheck, deferredLocal: true);
        }
    }

    public void Dispose()
    {
        Cancel();
        if (watchingNetwork) NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        watchingNetwork = false;
        if (diarizer.IsValueCreated) (diarizer.Value as IDisposable)?.Dispose();
        if (microphoneDiarizer is { IsValueCreated: true }) (microphoneDiarizer.Value as IDisposable)?.Dispose();
    }

    /// <summary>
    /// The cloud options of one run: language and hints as the live stream chooses them (<see cref="DeepgramUrlBuilder"/>),
    /// keyterms as live (<see cref="RecordingKeyterms"/>: own name, the meeting's participants, custom vocabulary, title words;
    /// synthetic "Speaker k" participants are not names). The keyterms are names: log their count only.
    /// </summary>
    public static DeepgramPrerecordedOptions CloudOptionsFor(AppSettings settings, Meeting meeting, DeepgramPrerecordedOptions? template = null)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(meeting);
        var language = DeepgramUrlBuilder.Language(settings.Locale);
        var names = RecordingKeyterms.ParticipantNames(meeting.Participants);
        return (template ?? new DeepgramPrerecordedOptions()) with
        {
            Language = language,
            LanguageHints = language == "multi" ? DeepgramUrlBuilder.LanguageHints : [],
            Keyterms = DeepgramUrlBuilder.Keyterms(RecordingKeyterms.For(settings, names, meeting.Title))
        };
    }

    private static List<string> ExistingFiles(IMeetingHost host, Meeting meeting, Speaker channel) =>
        DiarizationTimeline.Files(meeting, channel).Select(host.ResolveAudioPath).Where(File.Exists).ToList();

    /// <summary>The cheap pre-checks of both channels (word timings, archives present); silence is checked off the UI thread.</summary>
    private (string? Others, string? Me) SkipReasons(IMeetingHost current, Meeting meeting)
    {
        // Without kept audio there is nothing to match against (the archives are the only input).
        var keep = current.Settings.KeepAudio;
        var others = DiarizationRunStates.SkipReason(meeting, keep ? ExistingFiles(current, meeting, Speaker.Others) : [], Speaker.Others);
        var me = microphonePass == MicrophonePass.Never ? MicrophonePassOffMessage
            : DiarizationRunStates.SkipReason(meeting, keep ? ExistingFiles(current, meeting, Speaker.Me) : [], Speaker.Me);
        return (others, me);
    }

    /// <summary>The microphone pass is switched off for this step.</summary>
    public const string MicrophonePassOffMessage = DiarizationRunStates.MicrophonePassOffMessage;
    /// <summary><see cref="MicrophonePass.WhenOthersUnusable"/>: the Others pass carries the remote voices.</summary>
    public const string MicrophoneNotNeededMessage = "Not needed: the remote voices are on the Others channel.";

    /// <summary>The engine the settings choose, for the records of a run that does not get to pick one (both passes skipped).</summary>
    private IDiarizer ConfiguredEngine(IMeetingHost current, RunKind kind) =>
        kind != RunKind.OnDevice && current.Settings.EffectiveSpeakerSeparation == SpeakerSeparationEngine.Deepgram ? CloudLabel : diarizer.Value;

    /// <summary>The engine a stored run state names (for records written outside a run).</summary>
    private static IDiarizer EngineOf(DiarizationState? state) => new EngineLabel(state?.Engine ?? "", state?.ModelVersion, state?.Threshold);

    private void SkipBoth(IMeetingHost current, Meeting meeting, string othersSkip, string meSkip, IDiarizer engine)
    {
        var now = current.Now;
        var channels = new DiarizationChannels
        {
            Others = DiarizationRunStates.Channel(DiarizationStatus.Skipped, othersSkip, now, meeting.Diarization?.Channels?.Others),
            Me = DiarizationRunStates.Channel(DiarizationStatus.Skipped, meSkip, now, MeBefore(meeting.Diarization?.Channels?.Me))
        };
        Log(meeting.Id, "others", DiarizationStatus.Skipped, othersSkip, null, null, null);
        Log(meeting.Id, "me", DiarizationStatus.Skipped, meSkip, null, null, null);
        meeting.Diarization = DiarizationRunStates.Finished(engine, meeting.Diarization, DiarizationStatus.Skipped, othersSkip, now, channels);
        current.Persist(meeting);
    }

    private static void Finish(IMeetingHost current, Meeting meeting, DiarizationStatus status, string message, IDiarizer engine)
    {
        meeting.Diarization = DiarizationRunStates.Finished(engine, meeting.Diarization, status, message, current.Now);
        current.Persist(meeting);
    }

    /// <summary>One line per channel and run: ids, status and statistics only (never names or transcript text).</summary>
    private static void Log(Guid meetingID, string channel, DiarizationStatus status, string? reason, int? clusters, double? speechSeconds, TimeSpan? runtime,
        int? retained = null, VoiceMergeSummary? merge = null, int? separated = null, string? engine = null, string? detail = null)
    {
        var line = new System.Text.StringBuilder()
            .Append(CultureInfo.InvariantCulture, $"Wallfly: speaker matching meeting={meetingID:D} channel={channel} status={status.ToString().ToLowerInvariant()}");
        if (engine is not null) line.Append(" engine=").Append(engine);
        if (clusters is int count) line.Append(CultureInfo.InvariantCulture, $" clusters={count}");
        if (merge is not null)
            line.Append(CultureInfo.InvariantCulture, $" merged={merge.Merged} absorbed={merge.Absorbed} mergeRuntime={merge.Elapsed.TotalSeconds:0.0}s");
        if (separated is int split) line.Append(CultureInfo.InvariantCulture, $" separated={split}");
        if (retained is int kept) line.Append(CultureInfo.InvariantCulture, $" retained={kept}");
        if (speechSeconds is double speech) line.Append(CultureInfo.InvariantCulture, $" speech={speech:0.0}s");
        if (runtime is TimeSpan elapsed) line.Append(CultureInfo.InvariantCulture, $" runtime={elapsed.TotalSeconds:0.0}s");
        if (detail is not null) line.Append(' ').Append(detail);
        if (reason is not null) line.Append(" reason=\"").Append(reason).Append('"');
        Trace.TraceInformation(line.ToString());
    }

    private async Task RunAfterAsync(Task previous, Guid meetingID, CancellationToken token)
    {
        // Never run inside Enqueue: the caller holds the queue lock and expects to return at once.
        await Task.Yield();
        try { await previous.ConfigureAwait(false); }
        catch (Exception) { }
        var again = false;
        try { again = await RunAsync(meetingID, token).ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceError("Wallfly: speaker matching failed: " + error.GetType().Name); }
        finally
        {
            if (host is { } current)
            {
                try { await current.DispatchAsync(() => Ended(current, meetingID, again)).ConfigureAwait(false); }
                catch (Exception error) { Trace.TraceError("Wallfly: ending a speaker matching run failed: " + error.GetType().Name); }
            }
        }
    }

    /// <summary>On the UI thread, after a run: dequeue, then queue again when the recordings changed meanwhile.</summary>
    private void Ended(IMeetingHost current, Guid meetingID, bool recordingsChanged)
    {
        bool idle, resumeQueued;
        RunKind kind;
        lock (gate)
        {
            queue.Remove(meetingID);
            kind = kinds.GetValueOrDefault(meetingID);
            if (!queue.Contains(meetingID)) kinds.Remove(meetingID);
            idle = queue.Count == 0;
            if (idle) progress = "";
            resumeQueued = requeue.Remove(meetingID);
        }
        current.StateChanged();
        if (!recordingsChanged && !resumeQueued) return;
        // The repeated run keeps its kind (an automatic retry stays one, an explicit run on this device stays local).
        if (!EnqueueCore(meetingID, kind) && recordingsChanged && current.Find(meetingID) is { Diarization.Status: DiarizationStatus.Running } meeting)
            Finish(current, meeting, DiarizationStatus.Skipped, DiarizationRunStates.RecordingsChangedMessage, EngineOf(meeting.Diarization));
    }

    /// <returns>True when the recordings changed during the run and it must run again.</returns>
    private async Task<bool> RunAsync(Guid meetingID, CancellationToken token)
    {
        if (host is not { } current) return false;
        RunKind kind;
        lock (gate) kind = kinds.GetValueOrDefault(meetingID);
        Job? job = null;
        await current.DispatchAsync(() => job = Prepare(current, meetingID, kind, token)).ConfigureAwait(false);
        if (job is null) return false;
        var passes = job.Control.Source.Token;
        try
        {
            var reporter = new ProgressReporter(this, current, meetingID);
            var both = job.OthersSkip is null && job.MeSkip is null;
            // The microphone pass is off by default: the Others pass alone runs 0–100 %. A cloud pass reports its phases.
            IProgress<double> othersProgress = both ? new ScaledProgress(reporter, 0, 0.5)
                : job.Path == OthersPath.Cloud ? new CloudProgressReporter(this, current, meetingID, job.Control) : reporter;
            // File reading, model loading and clustering stay off the UI thread; the channels run one after the other.
            var others = await Task.Run(() => PassAsync(job, Speaker.Others, job.OthersFiles, job.OthersSkip, othersProgress, passes), CancellationToken.None)
                .ConfigureAwait(false);
            var meSkip = job.MeSkip;
            // Only when the Others pass could not use the remote channel (MicrophonePass.WhenOthersUnusable).
            if (meSkip is null && microphonePass == MicrophonePass.WhenOthersUnusable && others.Status != DiarizationStatus.Skipped)
                meSkip = MicrophoneNotNeededMessage;
            var me = passes.IsCancellationRequested
                ? new Outcome(null, DiarizationStatus.Failed, DiarizationRunStates.CancelledMessage, new OperationCanceledException(passes), TimeSpan.Zero)
                : await Task.Run(() => PassAsync(job, Speaker.Me, job.MeFiles, meSkip, both ? new ScaledProgress(reporter, 0.5, 0.5) : reporter, passes),
                    CancellationToken.None).ConfigureAwait(false);
            var again = false;
            await current.DispatchAsync(() => again = Complete(current, job, others, me, token)).ConfigureAwait(false);
            return again;
        }
        finally
        {
            job.Cloud?.Dispose();
            lock (gate) if (active == job.Control) active = null;
            job.Control.Dispose();
        }
    }

    /// <summary>
    /// Off the UI thread: one channel's pass (skipped, silent, completed or failed). A channel without timed words whose
    /// archives are silent is reported as silent: that is the actual cause (a capture that delivered nothing). An Others pass
    /// the Teams names settle completes at once (the names are written on the UI thread).
    /// </summary>
    private static async Task<Outcome> PassAsync(Job job, Speaker channel, List<string> files, string? skip, IProgress<double> progress, CancellationToken token)
    {
        if (skip is not null)
        {
            var noWords = skip is DiarizationRunStates.NoWordTimingsMessage or DiarizationRunStates.MeNoWordTimingsMessage;
            try
            {
                if (noWords && files.Count > 0 && !token.IsCancellationRequested && ArchiveLevel.IsSilent(files, token))
                    skip = DiarizationRunStates.SilentMessage(channel);
            }
            catch (OperationCanceledException) { }
            return new Outcome(null, DiarizationStatus.Skipped, skip, null, TimeSpan.Zero);
        }
        if (channel == Speaker.Others && job.Path == OthersPath.Teams) return new Outcome(null, DiarizationStatus.Completed, null, null, TimeSpan.Zero);
        var clock = Stopwatch.StartNew();
        try
        {
            if (ArchiveLevel.IsSilent(files, token))
                return new Outcome(null, DiarizationStatus.Skipped, DiarizationRunStates.SilentMessage(channel), null, clock.Elapsed);
            var result = await (channel == Speaker.Me ? job.MeDiarizer : job.Diarizer).RunAsync(files, progress, token).ConfigureAwait(false);
            return new Outcome(result, DiarizationStatus.Completed, null, null, clock.Elapsed);
        }
        catch (Exception error)
        {
            var (status, message) = DiarizationRunStates.ForFailure(error);
            return new Outcome(null, status, message, error, clock.Elapsed);
        }
    }

    /// <summary>
    /// On the UI thread: how the Others pass runs. Teams first (no engine at all), then the engine the settings choose; an
    /// explicit run on this device always uses the local engine. Cloud without a key: <paramref name="blocked"/> says why the
    /// pass cannot run, and it never runs locally instead.
    /// </summary>
    private OthersPath ChooseOthersPath(IMeetingHost current, Meeting meeting, RunKind kind, out string? blocked)
    {
        blocked = null;
        // "Re-detect speakers on this device" is the user's explicit choice: the local engine really runs, even where the Teams
        // names alone would do. Its result still keeps one tile = one speaker: TeamsVoiceGuard undoes joins of clusters Teams
        // names as different people, and the fusion binds every cluster with a tile's votes to that tile's name (several
        // clusters of one tile all carry its name; never "Name (Speaker k)").
        if (options.TeamsOnlyNaming && kind != RunKind.OnDevice)
        {
            // Decided on the meeting as the run will apply it (one participant per Teams person, as Complete merges).
            var probe = WallflyJson.Copy(meeting);
            TeamsParticipantMerge.Merge(probe);
            var decision = TeamsOnlyNaming.Decide(probe, options.TeamsNaming);
            WallflyLog.Info($"Wallfly: speaker matching meeting={meeting.Id:D} channel=others {decision.Describe()}");
            if (decision.Sufficient) return OthersPath.Teams;
        }
        if (kind == RunKind.OnDevice || current.Settings.EffectiveSpeakerSeparation == SpeakerSeparationEngine.OnDevice) return OthersPath.Local;
        if (string.IsNullOrWhiteSpace(current.DeepgramKey))
        {
            blocked = DiarizationRunStates.CloudKeyMissingMessage;
            return OthersPath.None;
        }
        return OthersPath.Cloud;
    }

    /// <summary>On the UI thread: the run starts (status running, saved), is held back by a recording, or nothing happens.</summary>
    private Job? Prepare(IMeetingHost current, Guid meetingID, RunKind kind, CancellationToken token)
    {
        if (token.IsCancellationRequested || cancelled || current.IsClosing) return null;
        if (current.RecordingMeetingID == meetingID || current.Find(meetingID) is not { } meeting) return null;
        var (othersSkip, meSkip) = SkipReasons(current, meeting);
        if (othersSkip != null && meSkip != null)
        {
            DropUploadCache(meetingID);
            SkipBoth(current, meeting, othersSkip, meSkip, ConfiguredEngine(current, kind));
            return null;
        }
        var path = OthersPath.None;
        if (othersSkip is null)
        {
            path = ChooseOthersPath(current, meeting, kind, out var blocked);
            othersSkip = blocked;
            if (othersSkip != null && meSkip != null)
            {
                DropUploadCache(meetingID);
                SkipBoth(current, meeting, othersSkip, meSkip, CloudLabel);
                return null;
            }
        }
        // Neither engine loads the laptop while any recording runs (the cloud pass encodes and uploads): the run waits until
        // none is active. The Teams-only path costs nothing and runs.
        if ((path is OthersPath.Local or OthersPath.Cloud || meSkip is null) && current.RecordingMeetingID is not null)
        {
            Defer(current, meeting, kind, path);
            return null;
        }
        // Another engine than the cloud: a cached upload of an earlier request is of no use any more.
        if (path != OthersPath.Cloud) DropUploadCache(meetingID);
        DeepgramPrerecordedDiarizer? cloud = null;
        int? attempts = null;
        DateTimeOffset? keepNextAttempt = null;
        IDiarizer engine;
        switch (path)
        {
            case OthersPath.Teams: engine = TeamsLabel; break;
            case OthersPath.Local: engine = diarizer.Value; break;
            case OthersPath.Cloud:
                var cloudOptions = CloudOptionsFor(current.Settings, meeting) with { UploadCachePath = UploadCachePath(meetingID) };
                cloud = (options.CloudDiarizer ?? ((key, settings) => new DeepgramPrerecordedDiarizer(key, settings)))(current.DeepgramKey, cloudOptions);
                engine = cloud;
                // An automatic retry continues the request's attempt count; any other run starts a new request. An attempt
                // because the network returned does not count and keeps the scheduled next attempt.
                var waiting = meeting.Diarization?.Channels?.Others;
                attempts = kind switch
                {
                    RunKind.Retry => (waiting?.Attempts ?? 0) + 1,
                    RunKind.NetworkRetry => Math.Max(1, waiting?.Attempts ?? 1),
                    _ => 1
                };
                if (kind == RunKind.NetworkRetry) keepNextAttempt = waiting?.NextAttemptAt;
                WallflyLog.Info(string.Create(CultureInfo.InvariantCulture,
                    $"Wallfly: speaker matching meeting={meetingID:D} channel=others engine={cloud.Engine} attempt={attempts} networkReturn={kind == RunKind.NetworkRetry} language={cloudOptions.Language} hints={cloudOptions.LanguageHints.Count} keyterms={cloudOptions.Keyterms.Count}"));
                break;
            default: engine = othersSkip == DiarizationRunStates.CloudKeyMissingMessage ? CloudLabel : ConfiguredEngine(current, kind); break;
        }
        var meEngine = meSkip is null ? microphoneDiarizer?.Value ?? diarizer.Value : engine;
        var startedAt = current.Now;
        var previous = meeting.Diarization?.Channels;
        DiarizationChannelState Pending(string? skip, DiarizationChannelState? before, IDiarizer channelEngine, int? tries) => skip is null
            ? new DiarizationChannelState { Status = DiarizationStatus.Running, Segments = before?.Segments, Engine = channelEngine.Engine, Attempts = tries }
            : DiarizationRunStates.Channel(DiarizationStatus.Skipped, skip, startedAt, before);
        var channels = new DiarizationChannels { Others = Pending(othersSkip, previous?.Others, engine, attempts), Me = Pending(meSkip, MeBefore(previous?.Me), meEngine, null) };
        meeting.Diarization = DiarizationRunStates.Running(engine, meeting.Diarization, startedAt, channels);
        current.Persist(meeting);
        var control = new RunControl(meetingID, path, meSkip is null, token);
        lock (gate)
        {
            progress = path == OthersPath.Cloud && meSkip is not null ? CloudPreparingText : ProgressText;
            active = control;
        }
        current.StateChanged();
        return new Job(meetingID, ExistingFiles(current, meeting, Speaker.Others), ExistingFiles(current, meeting, Speaker.Me), othersSkip, meSkip, engine,
            meEngine, startedAt, path, cloud, attempts, kind, control, keepNextAttempt);
    }

    /// <summary>
    /// A run while a recording is active: held back (shown as waiting), released by the next stop or a recheck, and persisted as
    /// waiting for the recording (<see cref="DiarizationRunStates.WaitingForRecordingState"/>), so a quit does not lose it: the
    /// next launch queues it again. The cloud attempts made so far are kept; the held-back one does not count.
    /// </summary>
    private void Defer(IMeetingHost current, Meeting meeting, RunKind kind, OthersPath path, int? attemptsMade = null)
    {
        var meetingID = meeting.Id;
        lock (gate) deferred[meetingID] = kind;
        WallflyLog.Info($"Wallfly: speaker matching meeting={meetingID:D} held back: a recording is active (engine={(path == OthersPath.Cloud ? "cloud" : "local")})");
        var others = meeting.Diarization?.Channels?.Others;
        if (others is not { Status: DiarizationStatus.Waiting, WaitingFor: DiarizationRunStates.WaitingForRecording })
        {
            var now = current.Now;
            var made = attemptsMade ?? (kind is RunKind.Retry or RunKind.NetworkRetry ? others?.Attempts : null);
            var engine = path == OthersPath.Cloud ? CloudLabel : diarizer.Value;
            var channels = new DiarizationChannels
            {
                Others = DiarizationRunStates.WaitingForRecordingState(now, made, engine.Engine, kind == RunKind.OnDevice, others),
                Me = MeBefore(meeting.Diarization?.Channels?.Me) is { Status: DiarizationStatus.Running } running
                    ? DiarizationRunStates.Channel(DiarizationStatus.Skipped, DiarizationRunStates.WaitingForRecordingMessage, now, running)
                    : MeBefore(meeting.Diarization?.Channels?.Me)
            };
            meeting.Diarization = DiarizationRunStates.Finished(engine, meeting.Diarization, DiarizationStatus.Failed, DiarizationRunStates.WaitingForRecordingMessage,
                now, channels);
            current.Persist(meeting);
        }
        ScheduleRetry(meetingID, options.Retry.RecordingRecheck, deferredLocal: true);
        current.StateChanged();
    }

    /// <summary>
    /// The Me channel's earlier record as a new state may carry it: with <see cref="MicrophonePass.Never"/> without stored
    /// microphone segments (a copy; the live record is not changed), so no re-apply can bring an old split back.
    /// </summary>
    private DiarizationChannelState? MeBefore(DiarizationChannelState? state)
    {
        if (microphonePass != MicrophonePass.Never || state?.Segments is null) return state;
        var copy = WallflyJson.Copy(state);
        copy.Segments = null;
        return copy;
    }

    /// <summary>On the UI thread, when no recording is active: a paused local run continues and the held-back runs are queued.</summary>
    private void ReleaseDeferred()
    {
        if (host is not { } current || current.RecordingMeetingID is not null) return;
        var paused = localHeld;
        ReleaseLocalEngines();
        List<KeyValuePair<Guid, RunKind>> due;
        lock (gate)
        {
            if (deferred.Count == 0)
            {
                if (paused) current.StateChanged();
                return;
            }
            due = [.. deferred];
            deferred.Clear();
        }
        foreach (var (meetingID, kind) in due) EnqueueCore(meetingID, kind);
        current.StateChanged();
    }

    private void ScheduleRetry(Guid meetingID, TimeSpan delay, bool deferredLocal)
    {
        CancellationToken token;
        lock (gate)
        {
            if (cancelled) return;
            token = timers.Token;
        }
        _ = RetryLaterAsync(meetingID, delay, deferredLocal, token);
    }

    private async Task RetryLaterAsync(Guid meetingID, TimeSpan delay, bool deferredLocal, CancellationToken token)
    {
        try
        {
            await Task.Yield();
            if (delay > TimeSpan.Zero) await Task.Delay(delay, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return; }
        if (token.IsCancellationRequested || host is not { } current) return;
        try { await current.DispatchAsync(() => { if (!token.IsCancellationRequested) RetryDue(current, meetingID, deferredLocal); }).ConfigureAwait(false); }
        catch (Exception error) { Trace.TraceError("Wallfly: a speaker matching retry failed: " + error.GetType().Name); }
    }

    /// <summary>
    /// On the UI thread: a run held back (or paused) by a recording checks the recordings again; a waiting pass gets its next
    /// attempt (a pass that waits for a recording, e.g. after a restart, runs with the kind it was held back with).
    /// </summary>
    private void RetryDue(IMeetingHost current, Guid meetingID, bool deferredLocal)
    {
        if (cancelled || current.IsClosing) return;
        if (deferredLocal)
        {
            if (current.RecordingMeetingID is not null)
            {
                bool still;
                lock (gate) still = deferred.ContainsKey(meetingID) || localHeld;
                if (still) ScheduleRetry(meetingID, options.Retry.RecordingRecheck, deferredLocal: true);
                return;
            }
            ReleaseDeferred();
            return;
        }
        // Only a pass that still waits: a run the user started meanwhile, or a completed one, cancels the retry.
        if (current.Find(meetingID) is not { } meeting)
        {
            DropUploadCache(meetingID);
            return;
        }
        if (!DiarizationRunStates.IsWaiting(meeting) || IsPending(meetingID)) return;
        var waiting = meeting.Diarization!.Channels!.Others!;
        var kind = waiting.WaitingFor != DiarizationRunStates.WaitingForRecording ? RunKind.Retry
            : waiting.OnDevice == true ? RunKind.OnDevice
            : waiting.Attempts is > 0 ? RunKind.Retry : RunKind.Normal;
        EnqueueCore(meetingID, kind);
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs args)
    {
        if (!args.IsAvailable || host is not { } current || cancelled) return;
        _ = current.DispatchAsync(NetworkReturned);
    }

    /// <summary>
    /// On the UI thread, when Windows reports the network back: every pass waiting for the network gets an extra attempt that
    /// does not count toward <see cref="CloudRetryPolicy.MaxAttempts"/>, at most once per <see cref="CloudRetryPolicy.NetworkRetrySpacing"/>
    /// and meeting and not sooner than that after its last attempt ended. Passes waiting for a recording are not touched.
    /// </summary>
    internal void NetworkReturned()
    {
        if (host is not { } current || cancelled || current.IsClosing) return;
        var now = current.Now;
        var spacing = options.Retry.NetworkRetrySpacing;
        foreach (var meeting in current.Meetings.Where(DiarizationRunStates.IsWaitingForNetwork).ToList())
        {
            if (IsPending(meeting.Id)) continue;
            var last = meeting.Diarization?.Channels?.Others?.FinishedAt;
            if (last is { } ended && now - ended < spacing) continue;
            lock (gate)
            {
                if (networkRetries.TryGetValue(meeting.Id, out var started) && now - started < spacing) continue;
                networkRetries[meeting.Id] = now;
            }
            EnqueueCore(meeting.Id, RunKind.NetworkRetry);
        }
    }

    /// <summary>On the UI thread: a waiting cloud pass of the meeting gets its retry timer.</summary>
    private void ScheduleIfWaiting(IMeetingHost current, Guid meetingID)
    {
        if (current.Find(meetingID)?.Diarization?.Channels?.Others is not { Status: DiarizationStatus.Waiting } waiting) return;
        var delay = (waiting.NextAttemptAt ?? current.Now) - current.Now;
        ScheduleRetry(meetingID, delay > TimeSpan.Zero ? delay : TimeSpan.Zero, deferredLocal: false);
    }

    /// <summary>On the UI thread: applies, records or drops the outcome. True when the run must be repeated.</summary>
    private bool Complete(IMeetingHost current, Job job, Outcome others, Outcome me, CancellationToken token)
    {
        lock (gate) if (active == job.Control) active = null;
        // Quitting: the result is dropped; the meeting keeps "running" and the next launch shows it as interrupted (a cloud
        // pass is queued again then).
        if (cancelled || current.IsClosing || current.Find(job.MeetingID) is not { } meeting) return false;
        var now = current.Now;
        // A recording started during the run and cancelled it: held back until no recording is active (persisted), the
        // attempt does not count. A result that arrived before the cancellation is used as usual.
        if (job.Control.Preempted && others.Result is null && me.Result is null && !token.IsCancellationRequested)
        {
            var made = job.Path != OthersPath.Cloud ? null : job.Kind == RunKind.NetworkRetry ? job.Attempts : job.Attempts - 1;
            Defer(current, meeting, job.Kind, job.Path, made ?? 0);
            return false;
        }
        // Plan step 3, self-check: the cloud words must line up with the live words before anything uses them.
        if (others.Result is { } cloudRun && job.Cloud is { } cloud && !token.IsCancellationRequested)
        {
            var check = DeepgramSelfCheck.Check(meeting, cloudRun, cloud.LastWords ?? [], options.SelfCheck);
            WallflyLog.Info($"Wallfly: speaker matching meeting={job.MeetingID:D} channel=others selfCheck {check.Describe()}");
            if (check.ToException() is { } rejected) others = new Outcome(null, DiarizationStatus.Failed, rejected.Message, rejected, others.Elapsed);
        }
        var teams = job.Path == OthersPath.Teams && others.Status == DiarizationStatus.Completed;
        if (others.Result is not null || teams || me.Result is not null)
        {
            if (token.IsCancellationRequested)
            {
                Finish(current, meeting, DiarizationStatus.Failed, DiarizationRunStates.CancelledMessage, job.Diarizer);
                return false;
            }
            if (current.RecordingMeetingID == job.MeetingID)
            {
                Finish(current, meeting, DiarizationStatus.Skipped, DiarizationRunStates.ResumedMessage, job.Diarizer);
                return false;
            }
            // Recording resumed and stopped during the run: the result misses the new audio. Discard it and run again.
            if (!ExistingFiles(current, meeting, Speaker.Others).SequenceEqual(job.OthersFiles, StringComparer.OrdinalIgnoreCase)
                || !ExistingFiles(current, meeting, Speaker.Me).SequenceEqual(job.MeFiles, StringComparer.OrdinalIgnoreCase)) return true;
            var edited = WallflyJson.Copy(meeting);
            // The microphone is never split: an older version's microphone labels go before anything re-applies them.
            if (microphonePass == MicrophonePass.Never) edited.ClearMicrophoneLabels();
            // Windows recordings before 2026-11-24: one Teams participant per transient UIA element; one per person before
            // the fusion binds names to voices (saved only with the result).
            var merged = TeamsParticipantMerge.Merge(edited);
            if (merged > 0) WallflyLog.Info($"Speakers meeting={job.MeetingID:D}: merged {merged} duplicate Teams participant entries");
            var previous = meeting.Diarization?.Channels;
            var meBefore = MeBefore(previous?.Me);
            var startedAt = meeting.Diarization?.StartedAt ?? job.StartedAt;
            if (teams)
            {
                var decision = TeamsOnlyNaming.Decide(edited, options.TeamsNaming);
                // The meeting changed during the run (a manual edit): decide again from the start.
                if (!decision.Sufficient) return true;
                ApplyTeamsNames(edited, decision, job.MeetingID, startedAt, now, meBefore, others.Elapsed);
            }
            else if (others.Result is { } othersRun)
            {
                DiarizationResult othersResult;
                int? split = null;
                string? detail = null;
                if (job.Path == OthersPath.Cloud)
                {
                    // Cloud results have no voice merge: tiles are checked by the Deepgram guard (plan step 4).
                    othersResult = DeepgramTeamsGuard.Apply(edited, othersRun, fusionPolicy, out var guard);
                    detail = guard.Describe();
                }
                else
                {
                    // The voice merge joins clusters by voice alone; joins of clusters Teams names as different people are undone
                    // first, so a wrong merge never overwrites a correct Teams name (TeamsVoiceGuard).
                    othersResult = TeamsVoiceGuard.Apply(edited, othersRun, fusionPolicy, out var separated);
                    split = othersResult.Merge is null ? null : separated;
                }
                var segments = edited.ApplyResult(othersResult, startedAt, now, fusionPolicy);
                AcousticSpeakerLabels.OverlapShare(segments, out var speech);
                var retained = (edited.AcousticSpeakers ?? []).Count(speaker => !AcousticSpeakerLabels.IsMicrophoneSpeaker(speaker.Id) && !speaker.Suppressed
                    && segments.Any(segment => segment.Speaker == speaker.Id));
                edited.Diarization!.Channels = new DiarizationChannels
                {
                    Me = meBefore,
                    Others = new DiarizationChannelState
                    {
                        Status = DiarizationStatus.Completed, FinishedAt = now, Threshold = othersResult.Threshold,
                        Clusters = othersResult.Merge?.Clusters ?? othersResult.SpeakerCount,
                        Merged = othersResult.Merge?.Merged, Absorbed = othersResult.Merge?.Absorbed, Separated = split, Retained = retained,
                        SpeechSeconds = Millis(speech), ElapsedSeconds = Millis(others.Elapsed.TotalSeconds), Engine = othersResult.Engine, Attempts = job.Attempts
                    }
                };
                Log(job.MeetingID, "others", DiarizationStatus.Completed, null, othersResult.Merge?.Clusters ?? othersResult.SpeakerCount, speech, others.Elapsed,
                    retained, othersResult.Merge, split, othersResult.Engine, detail);
            }
            else
            {
                // The Others pass did not complete: its earlier segments and labels stay; the top level records why.
                var (channel, status, message) = EndedOthers(job, others, now, previous?.Others);
                edited.Diarization = DiarizationRunStates.Finished(job.Diarizer, meeting.Diarization, status, message, now,
                    new DiarizationChannels { Me = meBefore, Others = channel });
                LogEnded(job.MeetingID, "others", others, channel);
            }
            var channels = edited.Diarization!.Channels!;
            if (me.Result is { } meResult)
            {
                var labelling = edited.ApplyMicrophoneResult(meResult, now, fusionPolicy);
                channels.Me!.ElapsedSeconds = Millis(me.Elapsed.TotalSeconds);
                channels.Me.Engine = meResult.Engine;
                Log(job.MeetingID, "me", DiarizationStatus.Completed, null, meResult.Merge?.Clusters ?? meResult.SpeakerCount, labelling.SpeechSeconds, me.Elapsed,
                    labelling.Retained, meResult.Merge, engine: meResult.Engine);
            }
            else
            {
                channels.Me = DiarizationRunStates.Channel(me.Status, me.Message, now, meBefore);
                LogEnded(job.MeetingID, "me", me, null);
            }
            // Saved first: only a stored result replaces the live meeting; then the audio may be deleted (spec 8) - but only
            // when the run processed it as a whole run did before the microphone pass existed (AudioProcessed only after a
            // completed Others pass, the Teams-only one included): a failed or skipped Others pass, and a failed microphone pass,
            // keep the audio exactly as a failed or skipped run always did, so "Re-detect speakers" can still recover that channel.
            // The cloud request ended (completed, failed for good, attempts used up): its cached upload goes; a waiting one keeps it.
            if (edited.Diarization?.Channels?.Others is not { Status: DiarizationStatus.Waiting }) DropUploadCache(job.MeetingID);
            if (current.SaveEdited(meeting, edited, CoreMessages.SpeakerMatchingNotSaved) && AudioProcessed(others, me))
                current.AudioProcessed(job.MeetingID);
            ScheduleIfWaiting(current, job.MeetingID);
            return false;
        }
        // No pass completed: earlier segments and labels stay; the top level describes the Others pass. The microphone is still
        // never split (an older version's labels go).
        if (microphonePass == MicrophonePass.Never) meeting.ClearMicrophoneLabels();
        var (othersChannel, othersStatus, othersMessage) = EndedOthers(job, others, now, meeting.Diarization?.Channels?.Others);
        LogEnded(job.MeetingID, "others", others, othersChannel);
        LogEnded(job.MeetingID, "me", me, null);
        var ended = new DiarizationChannels
        {
            Others = othersChannel,
            Me = DiarizationRunStates.Channel(me.Status, me.Message, now, MeBefore(meeting.Diarization?.Channels?.Me))
        };
        meeting.Diarization = DiarizationRunStates.Finished(job.Diarizer, meeting.Diarization, othersStatus, othersMessage, now, ended);
        current.Persist(meeting);
        if (othersChannel.Status != DiarizationStatus.Waiting) DropUploadCache(job.MeetingID);
        ScheduleIfWaiting(current, job.MeetingID);
        return false;
    }

    /// <summary>
    /// The record of an Others pass that did not complete. A transient cloud failure waits for the network (top level failed
    /// with <see cref="DiarizationRunStates.WaitingForNetworkMessage"/>, channel <see cref="DiarizationStatus.Waiting"/>) until the
    /// attempts are used up; every other outcome is recorded as it is. Nothing here ever starts the local engine.
    /// </summary>
    private (DiarizationChannelState Channel, DiarizationStatus Status, string Message) EndedOthers(Job job, Outcome others, DateTimeOffset now,
        DiarizationChannelState? previous)
    {
        var message = others.Message ?? DiarizationRunStates.CancelledMessage;
        if (job.Path == OthersPath.Cloud && others.Failure is CloudDiarizationException { IsTransient: true } transient)
        {
            var attempts = job.Attempts ?? 1;
            if (attempts < options.Retry.MaxAttempts)
            {
                // A network-return attempt did not count and keeps the attempt already scheduled.
                var next = job.Kind == RunKind.NetworkRetry && job.KeepNextAttemptAt is { } kept && kept > now
                    ? kept : now + options.Retry.DelayAfter(attempts, transient.RetryAfter);
                return (DiarizationRunStates.Waiting(DiarizationRunStates.WaitingForNetworkMessage, now, next, attempts, job.Diarizer.Engine, previous),
                    DiarizationStatus.Failed, DiarizationRunStates.WaitingForNetworkMessage);
            }
            message = DiarizationRunStates.CloudRetriesExhaustedMessage(attempts);
        }
        else if (others.Failure is CloudDiarizationException { Reason: CloudFailureReason.Unauthorized })
            message += " Check the Deepgram API key in Settings.";
        var channel = DiarizationRunStates.Channel(others.Status, message, now, previous);
        if (job.OthersSkip is null) channel.Engine = job.Diarizer.Engine;
        channel.Attempts = job.Attempts;
        return (channel, others.Status, message);
    }

    /// <summary>
    /// Plan step 5: the Teams names settle the Others channel. The words get the tile names; an earlier acoustic result of this
    /// channel (segments, identities, "Speaker k" participants no word refers to) is removed, so no later re-apply
    /// (<see cref="AcousticSpeakerLabels.ReapplyAcousticLabels"/>, a rename) can bring its labels back over the Teams names.
    /// </summary>
    /// <remarks>
    /// Manual wins: the words that show the name of a manually bound "Speaker k" (a rename) keep it as their own manual label
    /// before the identities go (<see cref="AcousticSpeakerLabels.PreserveManualBindings"/>), so the Teams names never replace it.
    /// </remarks>
    private void ApplyTeamsNames(Meeting edited, TeamsNamingDecision decision, Guid meetingID, DateTimeOffset? startedAt, DateTimeOffset now,
        DiarizationChannelState? me, TimeSpan elapsed)
    {
        var bindings = edited.PreserveManualBindings(edited.Diarization?.Segments, null);
        var changed = TeamsOnlyNaming.Apply(edited, decision);
        var removed = ClearOthersAcoustics(edited);
        var named = decision.Assignment.Where(name => name.ParticipantID is not null).Select(name => name.ParticipantID).Distinct().Count();
        edited.Diarization = new DiarizationState
        {
            Engine = TeamsEngine, Status = DiarizationStatus.Completed, StartedAt = startedAt, FinishedAt = now, Segments = null,
            Channels = new DiarizationChannels
            {
                Me = me,
                Others = new DiarizationChannelState
                {
                    Status = DiarizationStatus.Completed, FinishedAt = now, Engine = TeamsEngine, Retained = named, ElapsedSeconds = Millis(elapsed.TotalSeconds)
                }
            }
        };
        // The microphone's "Speaker k" numbers follow the Others ones: renumber them (there are no Others segments any more).
        edited.ReapplyAcousticLabels(fusionPolicy);
        Log(meetingID, "others", DiarizationStatus.Completed, null, null, null, elapsed, named, engine: TeamsEngine,
            detail: string.Create(CultureInfo.InvariantCulture, $"wordsChanged={changed} staleRemoved={removed} manualPinned={bindings.Pinned} {decision.Describe()}"));
    }

    /// <summary>Removes the acoustic identities of the Others channel (the microphone's stay). Returns the participants removed.</summary>
    private static int ClearOthersAcoustics(Meeting meeting)
    {
        foreach (var segment in meeting.Transcript.Where(segment => segment.Speaker == Speaker.Others))
            foreach (var word in segment.Words ?? []) word.AcousticSpeaker = null;
        if (meeting.AcousticSpeakers is { } speakers)
        {
            var kept = speakers.Where(speaker => AcousticSpeakerLabels.IsMicrophoneSpeaker(speaker.Id)).ToList();
            meeting.AcousticSpeakers = kept.Count == 0 ? null : kept;
        }
        if (meeting.Participants is not { } people) return 0;
        var referenced = meeting.Transcript.SelectMany(segment => (segment.Words ?? []).Select(word => word.ParticipantID).Append(segment.ParticipantID))
            .Where(id => id is not null).Select(id => id!.Value).ToHashSet();
        var remaining = people.Where(person => !(AcousticSpeakerLabels.AcousticSpeakerId(person) is int id && !AcousticSpeakerLabels.IsMicrophoneSpeaker(id)
            && !referenced.Contains(person.Id))).ToList();
        var removed = people.Count - remaining.Count;
        if (removed > 0) meeting.Participants = remaining;
        return removed;
    }

    /// <summary>
    /// Whether the run counts as processing the audio (the deletion policy may run): the Others pass completed (before AP5
    /// the only pass; a skipped run always kept the audio; the Teams-only pass counts), and the microphone pass completed or
    /// was skipped for a reason that is not a failure (no timed words, silent archive, not needed, off). A failed or cancelled
    /// microphone pass, or one skipped by an exception (models unavailable, audio too long), keeps the audio.
    /// </summary>
    private static bool AudioProcessed(Outcome others, Outcome me) =>
        others.Status == DiarizationStatus.Completed
        && (me.Status == DiarizationStatus.Completed || me.Status == DiarizationStatus.Skipped && me.Failure is null);

    /// <summary>Logs a pass that did not complete; a failure is logged by its exception type (its message may contain paths).</summary>
    private static void LogEnded(Guid meetingID, string channel, Outcome outcome, DiarizationChannelState? state)
    {
        string? detail = null;
        if (outcome.Failure is CloudDiarizationException cloud)
            detail = string.Create(CultureInfo.InvariantCulture,
                $"cloudKind={cloud.Kind} cloudReason={cloud.Reason} httpStatus={(cloud.StatusCode is int code ? code.ToString(CultureInfo.InvariantCulture) : "-")} attempts={state?.Attempts?.ToString(CultureInfo.InvariantCulture) ?? "-"}");
        Log(meetingID, channel, state?.Status ?? outcome.Status, outcome.Failure is null ? outcome.Message : outcome.Failure.GetType().Name, null, null,
            outcome.Elapsed > TimeSpan.Zero ? outcome.Elapsed : null, engine: state?.Engine, detail: detail);
    }

    private static double Millis(double seconds) => Math.Round(seconds, 3, MidpointRounding.AwayFromZero);

    /// <summary>Shows <paramref name="text"/> as the progress while the meeting's run is the current one (posted to the UI thread).</summary>
    private void ShowProgress(IMeetingHost target, Guid meetingID, string text) =>
        _ = target.DispatchAsync(() =>
        {
            lock (gate)
            {
                if (cancelled || queue.Count == 0 || queue[0] != meetingID) return;
                progress = text;
            }
            target.StateChanged();
        });

    /// <summary>Maps a pass's progress into its share of the run.</summary>
    private sealed class ScaledProgress(IProgress<double> target, double offset, double scale) : IProgress<double>
    {
        public void Report(double value) => target.Report(offset + scale * Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1));
    }

    private sealed class ProgressReporter(DiarizationStep owner, IMeetingHost host, Guid meetingID) : IProgress<double>
    {
        private int lastPercent = -1;

        public void Report(double value)
        {
            var percent = (int)Math.Round(Math.Clamp(double.IsFinite(value) ? value : 0, 0, 1) * 100, MidpointRounding.AwayFromZero);
            if (Interlocked.Exchange(ref lastPercent, percent) == percent) return;
            owner.ShowProgress(host, meetingID, ProgressText + " " + percent.ToString(CultureInfo.InvariantCulture) + "%");
        }
    }

    /// <summary>
    /// The phases of a cloud pass: "Preparing upload… 40%", "Uploading… 40%", then "Separating speakers (cloud)…"; on a slow
    /// connection the default upload goes Uploading → Preparing upload (the switch to Ogg/Opus) → Uploading → Separating. The
    /// phase is also recorded in the run's control (a recording that starts holds back encoding and uploading only).
    /// </summary>
    private sealed class CloudProgressReporter(DiarizationStep owner, IMeetingHost host, Guid meetingID, RunControl control) : IProgress<double>, IProgress<CloudDiarizationProgress>
    {
        private readonly object sync = new();
        private string last = "";

        /// <summary>The phases carry the text; the plain value (1 at the end) adds nothing.</summary>
        void IProgress<double>.Report(double value) { }

        void IProgress<CloudDiarizationProgress>.Report(CloudDiarizationProgress value)
        {
            control.Phase = value.Phase;
            var percent = value.Fraction is double fraction && double.IsFinite(fraction)
                ? " " + ((int)Math.Round(Math.Clamp(fraction, 0, 1) * 100, MidpointRounding.AwayFromZero)).ToString(CultureInfo.InvariantCulture) + "%"
                : "";
            var text = value.Phase switch
            {
                CloudDiarizationPhase.Encoding => CloudPreparingText + percent,
                CloudDiarizationPhase.Uploading => CloudUploadingText + percent,
                _ => CloudProcessingText
            };
            lock (sync)
            {
                if (text == last) return;
                last = text;
            }
            owner.ShowProgress(host, meetingID, text);
        }
    }
}
