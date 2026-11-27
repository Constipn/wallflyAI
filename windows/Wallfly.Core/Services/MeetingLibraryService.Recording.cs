using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using Wallfly.Audio;
using Wallfly.Core.Credentials;
using Wallfly.Core.Diagnostics;
using Wallfly.Core.Json;
using Wallfly.Core.Text;
using Wallfly.Transcription;
using Wallfly.Transcription.Deepgram;

namespace Wallfly.Core.Services;

/// <summary>The recording lifecycle: start (source, transcription, capture), inbox drains, levels, guards, stop.</summary>
public sealed partial class MeetingLibraryService
{
    private static readonly IReadOnlyDictionary<AudioSource, string> NoTranscriptionStates =
        new ReadOnlyDictionary<AudioSource, string>(new Dictionary<AudioSource, string>());

    private readonly Dictionary<AudioSource, string> transcriptionStates = [];
    private readonly Dictionary<AudioSource, string> pausedBanners = [];
    private readonly Stopwatch recordingClock = new();
    private double baseDuration;
    private bool recordingHadIncompleteTranscript;
    private RecordingSession? recording;
    private AudioEngineConfig? recordingConfig;
    private ITranscriptionClient? recordingClient;
    private Timer? drainTimer;
    private Timer? levelTimer;
    private Guid? sessionID;
    private CancellationTokenSource? startCancellation;
    private TaskCompletionSource? startFinished;
    private CancellationTokenSource? stopCancellation;
    private TaskCompletionSource? stopFinished;
    private CancellationTokenSource? watchCancellation;
    private bool attributionActive;

    private string transcriptionState = "";
    private IReadOnlyDictionary<AudioSource, string> transcriptionStatesView = NoTranscriptionStates;
    private string teamsSpeakerStatus = "";
    private MeetingAudioSelection? recordingAudioSelection;
    private MicrophoneStatus? microphoneStatus;
    private Timer? healthTimer;
    /// <summary>The <see cref="Meeting.RecordingSessions"/> entry of the running attempt (UI thread).</summary>
    private RecordingSessionInfo? sessionRecord;
    private string meetingAudioWarning = "";
    private string audioDeviceNotice = "";
    private long audioDeviceNoticeSince;
    /// <summary>Engine events logged per kind in this attempt (UI thread); beyond the cap they are only counted.</summary>
    private readonly Dictionary<AudioEngineEventKind, int> engineEventCounts = [];
    /// <summary>
    /// Channels whose device was lost in this attempt, with the lost device's name ("" when unnamed); their next DeviceAcquired
    /// is a "restored" (same device) or "changed" (another device) notice (UI thread).
    /// </summary>
    private readonly Dictionary<AudioSource, string> lostDevices = [];
    /// <summary>
    /// The output endpoint the Others capture of the running attempt is bound to (the configured id, or for the default the
    /// default console endpoint at the start, a switch or a native render move; empty when unknown). Written on the UI thread,
    /// read by the guard.
    /// </summary>
    private string recordingEndpoint = "";
    /// <summary>Render moves the native layer made by itself in this process (UI thread writes); the guard restarts its checks on a change.</summary>
    private int nativeRenderMoves;
    private const int MaxLoggedEventsPerKind = 50;
    /// <summary>
    /// At most this many <see cref="RecordingSessionInfo.Switches"/> per attempt; later ones are only counted
    /// (<see cref="RecordingSessionInfo.DroppedSwitches"/>), so a flapping device cannot grow the meeting file without bound.
    /// </summary>
    internal const int MaxRecordedSwitches = 200;
    /// <summary>The engine generation <see cref="recordingConfig"/> describes (UI thread): 0 at the start, then the last guard switch's.</summary>
    private int recordingConfigGeneration;
    /// <summary>
    /// Events of a replacement engine delivered (after its cutover) before the guard's switch applied that engine's
    /// configuration (UI thread); applied in order right after it, so a <c>fallback:</c> changes the new configuration, not the old.
    /// </summary>
    private readonly List<(AudioEngineEvent Event, int Generation)> deferredEngineEvents = [];
    /// <summary>Counts native render moves whose endpoint is resolved off the UI thread; an older resolution is stale (UI thread).</summary>
    private int renderFollowVersion;
    /// <summary>
    /// Normalized keyterms of the running Deepgram recording (names, vocabulary, title words). Replaced as a whole on the
    /// UI thread and read by the pipelines at every connect; never logged, only counted.
    /// </summary>
    private string[] liveKeyterms = [];
    /// <summary>Title and participant names <see cref="liveKeyterms"/> was built from; null when no Deepgram recording uses them.</summary>
    private string? liveKeytermSource;

    /// <summary>Latest streaming state of the running recording ("Live · Nova-3", "Paused · …"); empty otherwise.</summary>
    public string TranscriptionState { get => transcriptionState; private set => Set(ref transcriptionState, value); }

    /// <summary>
    /// The latest transcription state per channel (Me / Others) of the running recording; empty otherwise. A new
    /// dictionary instance is published (with a <see cref="Observable.Changed"/> notification) on every change.
    /// </summary>
    public IReadOnlyDictionary<AudioSource, string> TranscriptionStates => transcriptionStatesView;

    /// <summary>"Reconnect" is offered while a streaming recording runs.</summary>
    public bool CanReconnectTranscription => IsRecording && !IsStopping && recording?.CanReconnectTranscription == true;

    /// <summary>The meeting-audio source of the running recording (spec 3.2); null otherwise.</summary>
    public MeetingAudioSelection? RecordingAudioSelection
    {
        get => recordingAudioSelection;
        private set { if (Set(ref recordingAudioSelection, value)) Changed(nameof(RecordingAudioDescription)); }
    }

    /// <summary>
    /// The visible notice of the source: "Meeting audio: system output · Headset", "Meeting audio: system output · Teams
    /// meeting cannot be reliably detected · …", "Meeting audio: Microsoft Teams" (manual), "Meeting audio: system output ·
    /// Speakers · switched automatically" (after the guard's switch); empty when not recording.
    /// </summary>
    public string RecordingAudioDescription => RecordingAudioSelection?.Description ?? "";

    /// <summary>
    /// The microphone the running recording uses (spec 3.1, macOS <c>actualMicrophoneName</c>), set from the engine's Me
    /// <see cref="AudioEngineEventKind.DeviceAcquired"/>; null before the microphone is acquired and when not recording.
    /// </summary>
    public MicrophoneStatus? MicrophoneStatus
    {
        get => microphoneStatus;
        private set { if (Set(ref microphoneStatus, value)) Changed(nameof(MicrophoneDescription)); }
    }

    /// <summary>"USB microphone", or "Microphone array · Fallback (selected microphone missing)"; empty when unknown.</summary>
    public string MicrophoneDescription => MicrophoneStatus?.Description ?? "";

    /// <summary>
    /// Echo-canceller health of the running recording (<c>WfMetrics</c>: ERLE, estimated delay, …); null when not recording.
    /// Read on demand (the (i) popover), never polled.
    /// </summary>
    public AudioMetrics? RecordingMetrics
    {
        get
        {
            if (!IsRecording || recording is not RecordingSession session) return null;
            try { return session.Engine.Metrics; }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or DllNotFoundException) { return null; }
        }
    }

    /// <summary>The Teams speaker status line of the running recording ("Teams speaker detection active · 3 participants").</summary>
    public string TeamsSpeakerStatus { get => teamsSpeakerStatus; private set => Set(ref teamsSpeakerStatus, value); }

    /// <summary>
    /// "No meeting audio from Microsoft Teams. …" / "No meeting audio from your call apps. …" (the silent-audio banner): Others stayed silent
    /// while another app audibly plays, and the guard's switch / reopen did not help (or its attempts are used up). A Teams
    /// call alone never raises it. Cleared as soon as Others is audible again and when the recording ends; empty otherwise.
    /// </summary>
    public string MeetingAudioWarning { get => meetingAudioWarning; private set => Set(ref meetingAudioWarning, value); }

    /// <summary>
    /// A transient device notice of the running recording ("Output device changed · Speakers", "Microphone disconnected · USB
    /// headset", "… restored", "Output device could not be kept awake · …"), shown for
    /// <see cref="RecordingTimings.DeviceNoticeDuration"/>; empty otherwise.
    /// </summary>
    public string AudioDeviceNotice { get => audioDeviceNotice; private set => Set(ref audioDeviceNotice, value); }

    /// <summary>Keeps the duration of the running meeting current.</summary>
    public void Tick()
    {
        if (ActiveID is Guid id && recordingClock.IsRunning && Meetings.FirstOrDefault(meeting => meeting.Id == id) is Meeting active)
        {
            active.Duration = baseDuration + recordingClock.Elapsed.TotalSeconds;
            MarkDirty(active);
        }
    }

    public async Task StartRecordingAsync(Guid meetingID)
    {
        if (Busy || closing) return;
        var meeting = Meetings.First(item => item.Id == meetingID);
        var settings = Settings;
        var provider = settings.TranscriptionProvider;
        // The provider is fixed for the attempt; there is no fallback to another one. Local Whisper needs no key.
        if (provider == TranscriptionProvider.Deepgram && string.IsNullOrWhiteSpace(DeepgramKey)) { Error = DeepgramMessages.MissingKey; return; }
        if (provider == TranscriptionProvider.OpenAi && string.IsNullOrWhiteSpace(ApiKey)) { Error = CoreMessages.MissingRecordingKey; return; }
        if (!Flush()) return;
        Error = ""; IsStarting = true; ActiveID = meetingID; sessionID = Guid.NewGuid();
        var currentSession = sessionID.Value;
        // Speaker matching yields to the recording (no engine loads the laptop while any recording is active).
        foreach (var step in hooks.PostRecordingSteps)
        {
            try { step.RecordingStarted(meetingID); }
            catch (Exception failure) { Trace.TraceError("Wallfly: telling a post-recording step about the recording failed: " + failure.GetType().Name); }
        }
        baseDuration = meeting.Duration;
        recordingHadIncompleteTranscript = meeting.TranscriptIncomplete;
        startCancellation = new();
        startFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = startCancellation.Token;
        RecordingAudioSelection = null; MicrophoneStatus = null;
        ResetRecordingDiagnostics();
        var tag = LogTag(meetingID, currentSession);
        Status = settings.AutomaticMeetingAudioSource && hooks.TeamsCalls != null ? CoreMessages.ChoosingMeetingAudio : CoreMessages.PreparingAudio;
        Notify();
        IAudioEngine? engine = null;
        ITranscriptionPipeline? mePipeline = null;
        ITranscriptionPipeline? othersPipeline = null;
        AudioArchiveWriter? meArchive = null;
        AudioArchiveWriter? othersArchive = null;
        IDisposable? whisperLease = null;
        RecordingSession? session = null;
        var captureStarted = false;
        var meetingTouched = false;
        Task<TeamsCallProbe?>? detection = null;
        try
        {
            var selection = await routing.ResolveSourceAsync(settings, token).ConfigureAwait(true);
            EnsureStarting(currentSession, token);
            // The Teams-call detection decides live names only, never the audio source, and runs beside the start. The
            // chunk providers fix their word times with the pipelines, so they wait for it; Deepgram always delivers word
            // times, so its capture starts at once and the detection is awaited after the capture started.
            if (selection.Automatic && hooks.TeamsCalls != null) detection = routing.DetectTeamsCallAsync(token);
            if (detection != null && provider != TranscriptionProvider.Deepgram)
            {
                selection = MeetingAudioRouting.WithTeamsCall(selection, await detection.ConfigureAwait(true), settings);
                detection = null;
                EnsureStarting(currentSession, token);
            }
            // Deliberate deviation from macOS usesTeamsNames (Teams as the meeting audio): names apply whenever a Teams call
            // is detected (or Teams is the manual source), whatever the Others channel records. They label Others only.
            var namesWanted = settings.TeamsSpeakerNames && hooks.SpeakerAttribution != null;
            var useTeamsNames = namesWanted && selection.TeamsCall;
            var options = PipelineSettings(settings, useTeamsNames);
            liveKeytermSource = null;
            if (provider == TranscriptionProvider.Deepgram)
            {
                RefreshLiveKeyterms(settings, meeting);
                options = options with { Keyterms = () => Volatile.Read(ref liveKeyterms) };
            }
            if (provider == TranscriptionProvider.LocalWhisper) Status = CoreMessages.PreparingLocalWhisper;
            whisperLease = await pipelines.PrepareAsync(options, token).ConfigureAwait(true);
            EnsureStarting(currentSession, token);
            Status = CoreMessages.PreparingAudio;
            selection = routing.Confirm(selection);
            // Device loopback records the chosen endpoint; per-process capture uses the output device as its echo reference.
            var renderId = selection.Kind == MeetingAudioKind.DeviceLoopback ? selection.RenderDeviceId : settings.OutputDeviceID;
            var renderName = RenderDeviceName(renderId);
            if (selection.Kind == MeetingAudioKind.DeviceLoopback) selection = selection with { RenderDeviceName = renderName ?? "" };
            RecordingAudioSelection = selection;
            var probe = detection is null ? routing.LastProbeStatus : null;
            var model = TranscriptionModelName(settings, useTeamsNames);
            WallflyLog.Info($"Recording start {tag} base={Seconds(baseDuration)} {DescribeSelection(selection)} " +
                $"teamsProbe={(detection != null ? "pending" : probe?.ToString() ?? "none")} " +
                $"provider={provider} model={model} locale={settings.Locale} keepAudio={settings.KeepAudio} teamsNames={useTeamsNames}" +
                (provider == TranscriptionProvider.Deepgram ? $" keyterms={Volatile.Read(ref liveKeyterms).Length}" : ""));
            meetingTouched = true;
            engine = engineFactory(settings);
            transcriptionStates.Clear(); pausedBanners.Clear(); PublishTranscriptionStates();
            (mePipeline, othersPipeline) = CreatePipelines(settings, options, currentSession);
            ObservePipeline(mePipeline, tag);
            ObservePipeline(othersPipeline, tag);
            whisperLease.Dispose(); whisperLease = null;
            if (settings.KeepAudio)
            {
                var directory = Library.AudioDirectory(meetingID);
                meArchive = new(Path.Combine(directory, $"{currentSession:D}-me.wav"));
                othersArchive = new(Path.Combine(directory, $"{currentSession:D}-others.wav"));
                meeting.AudioFiles.Add(meArchive.Path);
                meeting.AudioFiles.Add(othersArchive.Path);
                // Spec 8: only recordings enrolled at their start are ever deleted automatically.
                if (AudioDeletionPolicy.Enrolls(settings)) (meeting.AudioDeletionPending ??= []).AddRange([meArchive.Path, othersArchive.Path]);
            }
            if (meeting.AudioDeletionReady == true || meeting.AudioDeletionPending is { Count: > 0 }) meeting.AudioDeletionReady = false;
            session = recording = new(engine, mePipeline, othersPipeline, meetingID, currentSession, baseDuration, meArchive, othersArchive);
            sessionRecord = new RecordingSessionInfo
            {
                Id = currentSession,
                StartedAt = time.GetUtcNow(),
                MeetingOffset = baseDuration,
                AudioKind = Key(selection.Kind),
                OthersMode = Key(selection.OthersMode),
                Automatic = selection.Automatic,
                TeamsCall = probe is { } status ? status == TeamsCallStatus.Active : null,
                TeamsProbe = probe?.ToString(),
                Explanation = selection.Explanation.Length > 0 ? selection.Explanation : null,
                AppName = selection.AppName.Length > 0 ? selection.AppName : null,
                ProcessId = selection.ProcessId > 0 ? selection.ProcessId : null,
                RenderDevice = new() { Id = renderId, Name = renderName },
                Microphone = new() { Id = settings.MicrophoneUID },
                TranscriptionProvider = provider.ToString(),
                TranscriptionModel = model,
                Locale = settings.Locale
            };
            (meeting.RecordingSessions ??= []).Add(sessionRecord);
            meeting.TranscriptIncomplete = true;
            if (!Persist(meeting)) throw new IOException(CoreMessages.UnsavedChanges);
            Writer.RecordingMeetingId = meetingID;
            recordingClock.Restart();
            var config = new AudioEngineConfig
            {
                MicrophoneDeviceId = settings.MicrophoneUID,
                RenderDeviceId = renderId,
                BaseDurationSeconds = baseDuration,
                OthersMode = selection.OthersMode,
                TargetProcessId = (uint)Math.Max(0, selection.ProcessId)
            };
            recordingConfig = config;
            recordingConfigGeneration = 0;
            Volatile.Write(ref recordingEndpoint, BoundEndpoint(renderId));
            await session.StartAsync(config).ConfigureAwait(true);
            captureStarted = true;
            EnsureStarting(currentSession, token);
            WallflyLog.Info($"Recording capture started {tag} othersMode={config.OthersMode} targetPid={config.TargetProcessId} " +
                $"microphone=\"{config.MicrophoneDeviceId}\" render=\"{config.RenderDeviceId}\" renderName=\"{renderName ?? ""}\" capabilities={engine.Capabilities}");
            drainTimer = new(_ => _ = dispatch(DrainRecording), null, DrainInterval, DrainInterval);
            levelTimer = new(_ => _ = dispatch(UpdateLevels), null, RecordingSession.LevelInterval, RecordingSession.LevelInterval);
            var health = new HealthMonitor(session, tag, hooks.Timings.HealthInterval);
            healthTimer = new(_ => health.Tick(), null, hooks.Timings.HealthInterval, hooks.Timings.HealthInterval);
            // The drain, level and health timers already run: the capture is never held up by the detection.
            if (detection != null)
            {
                Status = CoreMessages.ChoosingMeetingAudio;
                var detected = await detection.ConfigureAwait(true);
                detection = null;
                EnsureStarting(currentSession, token);
                selection = MeetingAudioRouting.WithTeamsCall(selection, detected, settings);
                RecordingAudioSelection = selection;
                probe = routing.LastProbeStatus;
                useTeamsNames = namesWanted && selection.TeamsCall;
                sessionRecord.TeamsCall = probe is { } detectedStatus ? detectedStatus == TeamsCallStatus.Active : null;
                sessionRecord.TeamsProbe = probe?.ToString();
                sessionRecord.Explanation = selection.Explanation.Length > 0 ? selection.Explanation : null;
                WallflyLog.Info($"Recording Teams call {tag} teamsProbe={probe?.ToString() ?? "none"} teamsCall={selection.TeamsCall} teamsPid={selection.TeamsProcessId} teamsNames={useTeamsNames}");
            }
            meeting.StartedAt ??= DateTimeOffset.UtcNow;
            Persist(meeting);
            IsRecording = true;
            Status = CoreMessages.RecordingStatePrefix + meeting.Title;
            if (useTeamsNames) StartAttribution(session, meetingID, selection.TeamsProcessId);
            StartWatches(session, settings, lateTeamsNames: namesWanted && !useTeamsNames);
        }
        catch (Exception failure)
        {
            // A detection still running is observed here; it ends with the start's cancellation or its own deadline.
            if (detection != null) _ = detection.ContinueWith(static task => _ = task.Exception, TaskScheduler.Default);
            StopWatches();
            CancelAttribution();
            drainTimer?.Dispose(); drainTimer = null;
            levelTimer?.Dispose(); levelTimer = null;
            healthTimer?.Dispose(); healthTimer = null;
            if (failure is OperationCanceledException) WallflyLog.Info($"Recording start abandoned {tag} meetingTouched={meetingTouched}");
            else WallflyLog.Error($"Recording start failed {tag} meetingTouched={meetingTouched} captureStarted={captureStarted}: {failure.GetType().Name}: {failure.Message}");
            ResetRecordingDiagnostics();
            whisperLease?.Dispose();
            if (session != null && captureStarted) await session.AbortAsync(QuitBudget).ConfigureAwait(true);
            else
            {
                if (mePipeline != null) await mePipeline.CancelAsync().ConfigureAwait(true);
                if (othersPipeline != null) await othersPipeline.CancelAsync().ConfigureAwait(true);
                meArchive?.Dispose(); othersArchive?.Dispose();
                engine?.Dispose();
            }
            recording = null; recordingConfig = null;
            (recordingClient as IDisposable)?.Dispose(); recordingClient = null;
            recordingClock.Stop(); ActiveID = null; sessionID = null; IsRecording = false;
            RecordingAudioSelection = null; MicrophoneStatus = null;
            transcriptionStates.Clear(); pausedBanners.Clear(); PublishTranscriptionStates();
            Writer.RecordingMeetingId = null;
            if (failure is OperationCanceledException && (closing || token.IsCancellationRequested))
            {
                // Quit during the start: nothing is reported; what was saved stays saved.
                if (meetingTouched) MarkDirty(meeting);
                Status = CoreMessages.Ready;
            }
            else if (!meetingTouched)
            {
                // Nothing was recorded or changed yet (meeting app not open, local Whisper could not start).
                Error = failure is MeetingAudioSourceUnavailableException or ChunkTranscriptionException ? failure.Message : CoreMessages.RecordingStartFailed;
                Status = CoreMessages.Ready;
            }
            else
            {
                if (failure is NotSupportedException) Trace.TraceError("Recording unavailable: " + failure.Message);
                Error = failure is NotSupportedException ? CoreMessages.RecordingEngineUnavailable
                    : failure is MeetingAudioSourceUnavailableException ? failure.Message
                    : CoreMessages.RecordingStartFailed;
                meeting.ProcessingErrors.Add(Error);
                Persist(meeting);
                Status = CoreMessages.NoteSavedAudioNeedsAttention;
            }
        }
        finally
        {
            IsStarting = false;
            startCancellation?.Dispose(); startCancellation = null;
            startFinished?.TrySetResult();
            Notify();
        }
    }

    private void EnsureStarting(Guid session, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        if (closing || sessionID != session) throw new OperationCanceledException(token);
    }

    private TranscriptionPipelineSettings PipelineSettings(AppSettings settings, bool wordTimes) =>
        TranscriptionSettingsFor(settings, wordTimes, DeepgramKey, ApiKey);

    /// <summary>
    /// Rebuilds <see cref="liveKeyterms"/> from the settings, the meeting's participants and its title when the participant
    /// names or the title changed (UI thread). The pipelines read the array on their queues at every connect.
    /// </summary>
    private void RefreshLiveKeyterms(AppSettings settings, Meeting meeting)
    {
        // Synthetic "Speaker k" participants of an earlier speaker matching are not names.
        var names = RecordingKeyterms.ParticipantNames(meeting.Participants);
        var source = meeting.Title + '\u001e' + string.Join('\u001f', names);
        if (source == liveKeytermSource) return;
        liveKeytermSource = source;
        Volatile.Write(ref liveKeyterms, DeepgramUrlBuilder.Keyterms(RecordingKeyterms.For(settings, names, meeting.Title)).ToArray());
    }

    /// <summary>
    /// The pipeline settings of one attempt (spec 4), fixed for the attempt and taken from the settings at its start:
    /// provider (Deepgram streaming, OpenAI or local Whisper chunks), locale, keys, kept audio (the recovery sentence of a
    /// failure) and the Whisper model. <paramref name="teamsNames"/> is macOS <c>usesTeamsNames</c> (the setting is on and
    /// Teams is the meeting audio): word times are then required (OpenAI transcribes Others with whisper-1, local Whisper
    /// times both channels without VAD).
    /// </summary>
    public static TranscriptionPipelineSettings TranscriptionSettingsFor(AppSettings settings, bool teamsNames, string deepgramKey, string openAiKey) =>
        new(settings.TranscriptionProvider == TranscriptionProvider.Deepgram, deepgramKey, settings.Locale)
        {
            ChunkedProvider = settings.TranscriptionProvider == TranscriptionProvider.LocalWhisper
                ? ChunkedTranscriptionProvider.LocalWhisper : ChunkedTranscriptionProvider.OpenAi,
            OpenAiKey = openAiKey,
            WordTimesRequired = teamsNames,
            KeepAudio = settings.KeepAudio,
            WhisperModelPath = settings.WhisperModelPath
        };

    private (ITranscriptionPipeline Me, ITranscriptionPipeline Others) CreatePipelines(AppSettings settings, TranscriptionPipelineSettings options, Guid session)
    {
        if (settings.TranscriptionProvider == TranscriptionProvider.OpenAi && (customTranscription != null || customPipeline != null))
        {
            var client = recordingClient = customTranscription?.Invoke(ApiKey, settings)
                ?? new OpenAiTranscriptionClient(ApiKey, new() { Model = settings.TranscriptionModel, Locale = settings.Locale });
            ITranscriptionPipeline Create(AudioSource source) =>
                customPipeline?.Invoke(source, client, session) ?? pipelines.Create(source, session, options, client);
            var me = Create(AudioSource.Me);
            return (me, Create(AudioSource.Others));
        }
        var mePipeline = pipelines.Create(AudioSource.Me, session, options);
        try { return (mePipeline, pipelines.Create(AudioSource.Others, session, options)); }
        catch
        {
            mePipeline.Dispose();
            throw;
        }
    }

    /// <summary>Applies one inbox drain to the running meeting; runs on the UI thread every 100 ms.</summary>
    public void DrainRecording()
    {
        if (recording is not RecordingSession session || ActiveID is not Guid id || sessionID != session.SessionId) return;
        var meeting = Meetings.FirstOrDefault(item => item.Id == id);
        if (meeting == null) return;
        var update = session.Drain();
        MeLevel = update.MeLevel;
        OthersLevel = update.OthersLevel;
        var arrived = new List<Guid>();
        foreach (var segment in update.Segments)
        {
            meeting.Upsert(segment);
            if (segment.Speaker == Speaker.Others) arrived.Add(segment.Id);
        }
        foreach (var gap in update.RecordingGaps) meeting.AppendRecordingGap(gap);
        foreach (var gap in update.TranscriptionGaps) meeting.AppendTranscriptionGap(gap);
        foreach (var anchor in update.Anchors) (meeting.AudioAnchors ??= []).Add(anchor);
        foreach (var message in update.Errors) { meeting.ProcessingErrors.Add(message); Error = message; }
        var recordChanged = ApplyDeferredEngineEvents(session);
        var engineEvents = update.EngineEvents ?? [];
        for (var index = 0; index < engineEvents.Count; index++)
        {
            var generation = update.EngineEventGenerations is { } generations && index < generations.Count ? generations[index] : recordingConfigGeneration;
            // A replacement's events wait until the guard's switch applied that engine's configuration (or the attempt ends).
            if (generation > recordingConfigGeneration || deferredEngineEvents.Count > 0) deferredEngineEvents.Add((engineEvents[index], generation));
            else recordChanged |= ApplyEngineEvent(session, engineEvents[index]);
        }
        if (update.MicrophoneAcquired is { } acquired)
        {
            MicrophoneStatus = ResolveMicrophone(acquired);
            if (sessionRecord != null && MicrophoneStatus is { } microphone)
            {
                sessionRecord.Microphone = new() { Id = recordingConfig?.MicrophoneDeviceId ?? "", Name = microphone.Description };
                recordChanged = true;
            }
        }
        ApplyTranscriptionStates(update.TranscriptionStates);
        var changed = update.Segments.Count > 0 || update.RecordingGaps.Count > 0 || update.TranscriptionGaps.Count > 0 || update.Anchors.Count > 0;
        if (attributionActive && hooks.SpeakerAttribution is { } hook)
        {
            changed |= ApplyAttribution(meeting, hook, arrived);
            // Teams names learnt during the call reach the next Deepgram connection (reconnect or rotation).
            if (liveKeytermSource != null) RefreshLiveKeyterms(Settings, meeting);
        }
        if (changed || recordChanged) MarkDirty(meeting);
        if (changed) Updated?.Invoke();
        // Spec 4.2 / 3.2: a final failure ends the recording; the message stays visible.
        if (update.StopRequest is { } reason && IsRecording && !IsStopping)
            _ = StopAfterFailureAsync(session.SessionId, reason, update.StopOrigin ?? RecordingStopReasons.PipelineFailed);
    }

    /// <summary>
    /// One engine event of the running attempt (UI thread): logged with the detail the native layer gives (the first
    /// <see cref="MaxLoggedEventsPerKind"/> of a kind; the rest only count), device changes become the transient
    /// <see cref="AudioDeviceNotice"/>, and switches are added to the attempt's <see cref="RecordingSessionInfo.Switches"/>.
    /// Returns true when the meeting's session entry changed.
    /// </summary>
    private bool ApplyEngineEvent(RecordingSession session, AudioEngineEvent engineEvent)
    {
        var count = engineEventCounts[engineEvent.Kind] = engineEventCounts.GetValueOrDefault(engineEvent.Kind) + 1;
        if (count <= MaxLoggedEventsPerKind)
        {
            var line = string.Create(CultureInfo.InvariantCulture,
                $"Audio event {LogTag(session.MeetingId, session.SessionId)} kind={engineEvent.Kind} source={engineEvent.Source} attempt={engineEvent.AttemptId} " +
                $"at={engineEvent.AtOffsetSeconds:0.000}s hr=0x{engineEvent.HResult:X8} detail=\"{engineEvent.Detail}\"");
            switch (engineEvent.Kind)
            {
                case AudioEngineEventKind.Error or AudioEngineEventKind.OthersFailed or AudioEngineEventKind.PermissionDenied or AudioEngineEventKind.DriverBlocked:
                    WallflyLog.Error(line); break;
                case AudioEngineEventKind.DeviceLost or AudioEngineEventKind.Degraded or AudioEngineEventKind.ReferenceMissing or AudioEngineEventKind.Restarting
                    or AudioEngineEventKind.WatchdogRecovery or AudioEngineEventKind.ClockJitterWarning or AudioEngineEventKind.FallbackDeviceLoopback
                    or AudioEngineEventKind.Warning:
                    WallflyLog.Warning(line); break;
                default:
                    WallflyLog.Info(line); break;
            }
        }
        else if (count == MaxLoggedEventsPerKind + 1)
            WallflyLog.Warning($"Audio event {LogTag(session.MeetingId, session.SessionId)} kind={engineEvent.Kind}: further events of this kind are only counted");
        var fields = engineEvent.Fields;
        var name = fields.DeviceName;
        var microphone = engineEvent.Source == AudioSource.Me;
        var output = !microphone && IsOutputEvent(engineEvent, fields);
        var device = output ? CoreMessages.OutputDevice : CoreMessages.AudioChannelName(engineEvent.Source);
        string? notice = null;
        RecordingSourceSwitch? change = null;
        var recordChanged = false;
        var at = engineEvent.AtOffsetSeconds > 0 ? engineEvent.AtOffsetSeconds : session.MeetingClock.NowSeconds;
        switch (engineEvent.Kind)
        {
            case AudioEngineEventKind.DeviceSwitched:
                notice = CoreMessages.AudioDeviceChanged(device, name);
                // The process-loopback modes announce a lost pinned endpoint as a Reference switch with "fallback:<reason>"
                // (supervisor abandon_pinned_render): from then on the default is followed, exactly as in device loopback.
                var abandoned = output && fields.IsFallback;
                change = new() { At = at, From = Key(engineEvent.Source), To = Key(engineEvent.Source), Device = name, Reason = abandoned ? "outputFallback" : "deviceSwitched" };
                if (output) FollowNativeRender(session, fallback: abandoned, name, change);
                break;
            case AudioEngineEventKind.DeviceLost:
                lostDevices[engineEvent.Source] = name ?? "";
                notice = CoreMessages.AudioDeviceDisconnected(device, name);
                change = new() { At = at, From = Key(engineEvent.Source), To = Key(engineEvent.Source), Device = name, Reason = "deviceLost" };
                break;
            case AudioEngineEventKind.DeviceAcquired when output && fields.IsFallback:
                // A pinned output endpoint disappeared: the native layer records the default from here and never hops back.
                lostDevices.Remove(engineEvent.Source);
                notice = CoreMessages.AudioDeviceChanged(device, name);
                change = new() { At = at, From = Key(engineEvent.Source), To = Key(engineEvent.Source), Device = name, Reason = "outputFallback" };
                FollowNativeRender(session, fallback: true, name, change);
                break;
            case AudioEngineEventKind.DeviceAcquired when lostDevices.Remove(engineEvent.Source, out var lost):
                notice = name != null && lost.Length > 0 && !string.Equals(name, lost, StringComparison.Ordinal)
                    ? CoreMessages.AudioDeviceChanged(device, name)
                    : CoreMessages.AudioDeviceRestored(device, name);
                // An invalidated device loopback reopens on the default: the bound endpoint is the default now.
                if (output && engineEvent.Source == AudioSource.Others) FollowNativeRender(session, fallback: false, name, null);
                break;
            case AudioEngineEventKind.FallbackDeviceLoopback:
                notice = CoreMessages.AudioDeviceLoopbackFallback(engineEvent.Detail);
                change = new() { At = at, From = recordingConfig is { } config ? Key(config.OthersMode) : null, To = Key(OthersMode.DeviceLoopback),
                    Device = NullIfEmpty(engineEvent.Detail), Reason = "deviceLoopbackFallback" };
                break;
            case AudioEngineEventKind.Warning when fields.KeepAliveGaveUp:
                notice = CoreMessages.KeepAliveGaveUp(fields.Render);
                break;
        }
        // Names the output device the first time the engine reports it (the device list may not have known it at the start).
        if (engineEvent.Kind == AudioEngineEventKind.DeviceAcquired && output && fields.Render is { } acquiredName && sessionRecord is { } record
            && string.IsNullOrEmpty(record.RenderDevice?.Name))
        {
            record.RenderDevice ??= new() { Id = recordingConfig?.RenderDeviceId ?? "" };
            record.RenderDevice.Name = acquiredName;
            WallflyLog.Info($"Recording device {LogTag(session.MeetingId, session.SessionId)} output device named \"{acquiredName}\" at {Seconds(at)}");
            recordChanged = true;
        }
        if (notice != null)
        {
            AudioDeviceNotice = notice;
            audioDeviceNoticeSince = time.GetTimestamp();
        }
        if (change is null || sessionRecord is null) return recordChanged;
        if (AddSwitch(session, change))
            WallflyLog.Info($"Recording device {LogTag(session.MeetingId, session.SessionId)} {change.Reason} source={engineEvent.Source} at={Seconds(at)} " +
                $"device=\"{change.Device ?? ""}\" deviceId=\"{change.DeviceId ?? ""}\"");
        return true;
    }

    /// <summary>
    /// Adds <paramref name="change"/> to the attempt's <see cref="RecordingSessionInfo.Switches"/>, at most
    /// <see cref="MaxRecordedSwitches"/>; beyond that it is only counted in <see cref="RecordingSessionInfo.DroppedSwitches"/>
    /// (one warning line when that starts). False when it was only counted. UI thread.
    /// </summary>
    private bool AddSwitch(RecordingSession session, RecordingSourceSwitch change)
    {
        if (sessionRecord is not { } record) return false;
        var switches = record.Switches ??= [];
        if (switches.Count < MaxRecordedSwitches)
        {
            switches.Add(change);
            return true;
        }
        record.DroppedSwitches = (record.DroppedSwitches ?? 0) + 1;
        if (record.DroppedSwitches == 1)
            WallflyLog.Warning($"Recording device {LogTag(session.MeetingId, session.SessionId)}: {MaxRecordedSwitches} source/device changes recorded; further ones are only counted");
        return false;
    }

    /// <summary>
    /// Applies, in order, the deferred events of a replacement engine whose configuration is known by now
    /// (<see cref="recordingConfigGeneration"/>). UI thread; true when the meeting's session entry changed.
    /// </summary>
    private bool ApplyDeferredEngineEvents(RecordingSession session)
    {
        var changed = false;
        var applied = 0;
        while (applied < deferredEngineEvents.Count && deferredEngineEvents[applied].Generation <= recordingConfigGeneration)
            changed |= ApplyEngineEvent(session, deferredEngineEvents[applied++].Event);
        deferredEngineEvents.RemoveRange(0, applied);
        return changed;
    }

    /// <summary>
    /// Whether a non-microphone event concerns the output device: a <c>render:</c> field, the echo reference (keep-alive and echo
    /// path of the process-loopback modes), or Others recorded by device loopback.
    /// </summary>
    private bool IsOutputEvent(AudioEngineEvent engineEvent, AudioEventDetail fields) =>
        fields.Render != null || engineEvent.Source == AudioSource.Reference
        || engineEvent.Source == AudioSource.Others && Volatile.Read(ref recordingConfig)?.OthersMode == OthersMode.DeviceLoopback;

    /// <summary>
    /// The native layer moved the output endpoint by itself (2026-11-23): it follows the default console render endpoint when
    /// no endpoint is pinned, and when a pinned one disappears (<paramref name="fallback"/>) it records the default from then on
    /// and never hops back, so the selection becomes "default" (<c>RenderDeviceId = ""</c>). The bound endpoint is re-resolved
    /// (the guard's "endpoint we record"), the source line names the new device, and the guard restarts its checks (the
    /// session already restarted the silence measurement); this is not a guard attempt. UI thread.
    /// <para>
    /// The device list is never read on the UI thread here (device events come when the audio service is busiest): when the
    /// bound endpoint or its name needs the list, it is resolved on a background task and applied on the dispatcher, unless
    /// the recording ended, the guard replaced the configuration or a newer native move superseded it meanwhile. The
    /// endpoint then also lands in <paramref name="change"/>'s <see cref="RecordingSourceSwitch.DeviceId"/>.
    /// </para>
    /// </summary>
    private void FollowNativeRender(RecordingSession session, bool fallback, string? name, RecordingSourceSwitch? change)
    {
        if (recording != session || recordingConfig is not { } config) return;
        var pinned = config.RenderDeviceId;
        if (fallback) config.RenderDeviceId = "";
        var configured = config.RenderDeviceId;
        Interlocked.Increment(ref nativeRenderMoves);
        var version = ++renderFollowVersion;
        // The source line names the device the engine reported at once; the list is only needed when it gave no name.
        if (name != null && config.OthersMode == OthersMode.DeviceLoopback && RecordingAudioSelection is { Kind: MeetingAudioKind.DeviceLoopback } selection)
            RecordingAudioSelection = selection with { RenderDeviceId = configured, RenderDeviceName = name };
        if (configured.Length > 0 && name != null)
        {
            ApplyNativeRender(session, config, version, pinned, fallback, name, change, (configured, null));
            return;
        }
        var devices = Devices;
        _ = ResolveAsync();

        async Task ResolveAsync()
        {
            try
            {
                var resolved = await Task.Run(() => ResolveRender(devices, configured, name is null)).ConfigureAwait(false);
                await dispatch(() => ApplyNativeRender(session, config, version, pinned, fallback, name, change, resolved)).ConfigureAwait(false);
            }
            catch (Exception failure) { Trace.TraceWarning("Wallfly: following the output device failed: " + failure.Message); }
        }
    }

    /// <summary>The resolved half of <see cref="FollowNativeRender"/> (UI thread); a stale result is logged and dropped.</summary>
    private void ApplyNativeRender(RecordingSession session, AudioEngineConfig config, int version, string pinned, bool fallback, string? name,
        RecordingSourceSwitch? change, (string Bound, string? Name) resolved)
    {
        var tag = LogTag(session.MeetingId, session.SessionId);
        if (recording != session || !ReferenceEquals(recordingConfig, config) || version != renderFollowVersion)
        {
            WallflyLog.Info($"Recording output follow {tag}: dropped a stale endpoint resolution (bound=\"{resolved.Bound}\"); " +
                (recording != session ? "the recording ended" : "a newer device event or switch superseded it"));
            return;
        }
        Volatile.Write(ref recordingEndpoint, resolved.Bound);
        if (name is null && config.OthersMode == OthersMode.DeviceLoopback && RecordingAudioSelection is { Kind: MeetingAudioKind.DeviceLoopback } selection)
            RecordingAudioSelection = selection with { RenderDeviceId = config.RenderDeviceId, RenderDeviceName = resolved.Name ?? "" };
        WallflyLog.Info($"Recording output follow {tag} fallback={fallback} othersMode={config.OthersMode} " +
            $"configured=\"{pinned}\" -> \"{config.RenderDeviceId}\" bound=\"{resolved.Bound}\" name=\"{name ?? resolved.Name ?? ""}\"");
        if (change != null && change.DeviceId is null && resolved.Bound.Length > 0)
        {
            change.DeviceId = resolved.Bound;
            MarkDirtyMeeting(session.MeetingId);
        }
    }

    /// <summary>Names the microphone of a Me DeviceAcquired against the current device list and the configured microphone.</summary>
    private MicrophoneStatus ResolveMicrophone(AudioEngineEvent acquired)
    {
        IReadOnlyList<AudioDevice> devices;
        try { devices = Devices.List(AudioFlow.Capture); }
        catch (Exception error)
        {
            Trace.TraceWarning("Wallfly: listing microphones failed: " + error.Message);
            devices = [];
        }
        var status = Services.MicrophoneStatus.Resolve(devices, recordingConfig?.MicrophoneDeviceId ?? Settings.MicrophoneUID, acquired);
        if (status.Fallback) Trace.TraceInformation("Wallfly: microphone fallback to the system default: " + status.Reason);
        return status;
    }

    private async Task StopAfterFailureAsync(Guid session, string reason, string origin)
    {
        await Task.Yield();
        if (sessionID != session || !IsRecording || IsStopping) return;
        Error = reason;
        await StopRecordingAsync(reason: origin).ConfigureAwait(true);
    }

    /// <summary>
    /// The level meters every <see cref="RecordingSession.LevelInterval"/>, independent of the inbox drain; also ends the
    /// transient <see cref="AudioDeviceNotice"/>.
    /// </summary>
    private void UpdateLevels()
    {
        if (recording is not RecordingSession session || !IsRecording || IsStopping) return;
        var levels = session.Levels;
        MeLevel = levels.Me;
        OthersLevel = levels.Others;
        if (AudioDeviceNotice.Length > 0 && time.GetElapsedTime(audioDeviceNoticeSince) >= hooks.Timings.DeviceNoticeDuration) AudioDeviceNotice = "";
    }

    /// <summary>
    /// Streaming states go to <see cref="TranscriptionStates"/> / <see cref="TranscriptionState"/>; a paused channel also
    /// raises the error banner (not a processing error: capture continues and the lost audio is recorded as gaps). The
    /// banner clears once that channel reports any other state, e.g. after "Reconnect".
    /// </summary>
    private void ApplyTranscriptionStates(IReadOnlyList<TranscriptionEvent>? states)
    {
        if (states == null || states.Count == 0) return;
        foreach (var state in states)
        {
            if (!transcriptionStates.TryGetValue(state.Source, out var previous) || previous != state.Detail)
            {
                var line = $"Transcription state {(ActiveID is Guid id && sessionID is Guid sid ? LogTag(id, sid) : "")} source={state.Source} kind={state.Kind} state=\"{state.Detail}\"";
                if (state.Kind == TranscriptionEventKind.Paused) WallflyLog.Warning(line); else WallflyLog.Info(line);
            }
            transcriptionStates[state.Source] = state.Detail;
            if (state.Kind == TranscriptionEventKind.Paused)
            {
                pausedBanners[state.Source] = $"{state.Source}: {state.Detail}";
                Error = pausedBanners[state.Source];
            }
            else if (pausedBanners.Remove(state.Source, out var banner) && Error == banner) Error = "";
        }
        PublishTranscriptionStates();
    }

    private void PublishTranscriptionStates()
    {
        if (transcriptionStates.Count == 0 && transcriptionStatesView.Count == 0 && TranscriptionState.Length == 0) return;
        transcriptionStatesView = transcriptionStates.Count == 0
            ? NoTranscriptionStates
            : new ReadOnlyDictionary<AudioSource, string>(new Dictionary<AudioSource, string>(transcriptionStates));
        Changed(nameof(TranscriptionStates));
        var texts = transcriptionStates.Values.Distinct().ToList();
        TranscriptionState = texts.Count switch
        {
            0 => "",
            1 => texts[0],
            _ => string.Join(" · ", transcriptionStates.Select(pair => $"{pair.Key}: {pair.Value}"))
        };
    }

    /// <summary>
    /// "Reconnect" (spec 9.3): optionally stores a new Deepgram key, then restarts both streaming channels
    /// with it. Capture and archiving are never interrupted.
    /// </summary>
    public void ReconnectTranscription(string? deepgramKey = null)
    {
        if (!string.IsNullOrWhiteSpace(deepgramKey) && deepgramKey.Trim() != DeepgramKey)
        {
            Credentials.Write(CredentialAccount.DeepgramTranscription, deepgramKey);
            DeepgramKey = deepgramKey.Trim();
        }
        if (!CanReconnectTranscription || string.IsNullOrWhiteSpace(DeepgramKey)) return;
        recording!.ReconnectTranscription(DeepgramKey);
    }

    /// <summary>
    /// The microphone choice (macOS <c>applyMicrophoneSelection</c>): during a recording the running engine switches its
    /// microphone first (a failure keeps the previous one and shows <see cref="CoreMessages.MicrophoneSwitchFailed"/>), then
    /// the choice is saved in the settings. Returns false when the switch or the save failed.
    /// </summary>
    public async Task<bool> SwitchMicrophoneAsync(string microphoneUID)
    {
        var uid = microphoneUID?.Trim() ?? "";
        if (IsStarting || IsStopping || IsMovingLibrary || closing) return false;
        if (IsRecording && recording is RecordingSession session)
        {
            try { await session.SetMicrophoneAsync(uid).ConfigureAwait(true); }
            catch (Exception failure)
            {
                Trace.TraceWarning("Wallfly: switching the microphone failed: " + failure.Message);
                Error = CoreMessages.MicrophoneSwitchFailed;
                return false;
            }
            if (recordingConfig != null) recordingConfig.MicrophoneDeviceId = uid;
        }
        if (Settings.MicrophoneUID == uid) return true;
        var updated = WallflyJson.Copy(Settings);
        updated.MicrophoneUID = uid;
        try { Library.SaveSettings(updated); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            Error = CoreMessages.SettingsSaveFailed;
            return false;
        }
        Settings = updated;
        Changed(nameof(Settings));
        Updated?.Invoke();
        return true;
    }

    /// <param name="reason">
    /// Why the recording ends (<see cref="RecordingStopReasons"/>), for the app log and the attempt's
    /// <see cref="RecordingSessionInfo.StopReason"/>.
    /// </param>
    /// <param name="evidence">"meeting ended" only: what the auto-stop saw (<see cref="RecordingSessionInfo.StopEvidence"/>).</param>
    public async Task<bool> StopRecordingAsync(TimeSpan? timeout = null, string reason = RecordingStopReasons.Manual, string? evidence = null)
    {
        // A second stop (the user, the auto-stop or a failure) waits for the running one instead of returning early.
        if (IsStopping && stopFinished is { } running)
        {
            await running.Task.ConfigureAwait(true);
            return false;
        }
        if (!IsRecording || IsStopping || recording is not RecordingSession finishing || ActiveID is not Guid id) return false;
        var meeting = Meetings.First(item => item.Id == id);
        StopWatches();
        stopCancellation = new();
        stopFinished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = stopFinished;
        IsStopping = true; PresentationMode = false; Status = CoreMessages.Stopping; Notify();
        var tag = LogTag(id, finishing.SessionId);
        WallflyLog.Info($"Recording stopping {tag} reason={reason}");
        MeetingAudioWarning = "";
        Tick(); recordingClock.Stop();
        var until = meeting.Duration;
        // How this attempt ended, in the meeting file (saved with the stop below).
        if (sessionRecord is { } stopped)
        {
            stopped.StoppedAt = Math.Round(until, 3);
            stopped.StopReason = reason;
            stopped.StopEvidence = reason == RecordingStopReasons.MeetingEnded ? NullIfEmpty(evidence) : null;
        }
        await (drainTimer?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(true);
        drainTimer = null;
        await (levelTimer?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(true);
        levelTimer = null;
        await (healthTimer?.DisposeAsync() ?? ValueTask.CompletedTask).ConfigureAwait(true);
        healthTimer = null;
        // Spec 4.5: save first, then stop capture, the Teams watcher and both pipelines within one budget.
        Flush();
        var attribution = attributionActive ? hooks.SpeakerAttribution : null;
        Func<Task>? stopObserving = attribution is null ? null : attribution.StopObservingAsync;
        var result = await finishing.FinishAsync(timeout ?? StopBudget, stopCancellation.Token, stopObserving).ConfigureAwait(true);
        DrainRecording();
        if (attribution != null) FinalizeAttribution(meeting, attribution, until);
        meeting.Duration = Math.Max(meeting.Duration, meeting.Transcript.Select(segment => segment.Offset + segment.Duration).DefaultIfEmpty().Max());
        sessionID = null; recording = null; recordingConfig = null; ActiveID = null; IsRecording = false; IsStopping = false;
        transcriptionStates.Clear(); pausedBanners.Clear(); PublishTranscriptionStates();
        RecordingAudioSelection = null; MicrophoneStatus = null;
        Writer.RecordingMeetingId = null;
        MeLevel = 0; OthersLevel = 0;
        (recordingClient as IDisposable)?.Dispose(); recordingClient = null;
        var complete = result.Complete && !recordingHadIncompleteTranscript && (meeting.TranscriptionGaps ?? []).Count == 0;
        meeting.AudioTranscriptionComplete = complete;
        meeting.AudioRecoveryNeeded = result.Complete ? null : true;
        if (result.Error != null) { Error = result.Error; meeting.ProcessingErrors.Add(result.Error); }
        else if (recordingHadIncompleteTranscript) Error = CoreMessages.ResumedWithEarlierGap;
        LogStopSummary(tag, reason, finishing, meeting.Duration - finishing.BaseDuration, meeting.Duration, result, complete);
        ResetRecordingDiagnostics();
        var saved = Persist(meeting);
        Status = complete && saved ? CoreMessages.MeetingSaved : CoreMessages.MeetingSavedIncomplete;
        stopCancellation.Dispose(); stopCancellation = null;
        Notify();
        if (closing) deferredAfterStop.Add((id, complete));
        else AfterStop(id, complete);
        finished.TrySetResult();
        return complete && saved;
    }

    // ---- Teams speaker names (spec 5.2) -------------------------------------------------------------------------

    /// <param name="teamsProcess">Root PID of the Teams process tree (<see cref="MeetingAudioSelection.TeamsProcessId"/>), whatever the audio source.</param>
    private void StartAttribution(RecordingSession session, Guid meetingID, int teamsProcess)
    {
        if (hooks.SpeakerAttribution is not { } hook) return;
        var clock = session.MeetingClock;
        try
        {
            hook.OnRecordingStarted(new(meetingID, session.SessionId, session.BaseDuration, () => clock.NowSeconds, teamsProcess));
            attributionActive = true;
            TeamsSpeakerStatus = hook.Status;
        }
        catch (Exception failure)
        {
            // Speaker names are optional: the recording continues as "Others".
            Trace.TraceError("Wallfly: Teams speaker detection could not start: " + failure.Message);
            attributionActive = false;
        }
    }

    private void CancelAttribution()
    {
        if (!attributionActive) return;
        attributionActive = false;
        TeamsSpeakerStatus = "";
        try { hooks.SpeakerAttribution?.Cancel(); }
        catch (Exception failure) { Trace.TraceError("Wallfly: cancelling Teams speaker detection failed: " + failure.Message); }
    }

    /// <summary>Ingests the queued Teams observations and relabels the live window; true when the meeting changed.</summary>
    private bool ApplyAttribution(Meeting meeting, ISpeakerAttributionHook hook, IReadOnlyList<Guid> arrived)
    {
        var changed = false;
        try
        {
            changed |= Ingest(meeting, hook, out var ingested);
            foreach (var segment in arrived) changed |= Revise(meeting, hook.OnTranscriptChanged(meeting.Transcript, segment));
            if (ingested && arrived.Count == 0) changed |= Revise(meeting, hook.OnTranscriptChanged(meeting.Transcript));
            TeamsSpeakerStatus = hook.Status;
        }
        catch (Exception failure)
        {
            // Attribution must never break the recording: the words stay "Others".
            Trace.TraceError("Wallfly: Teams speaker attribution failed: " + failure.Message);
        }
        return changed;
    }

    private static bool Ingest(Meeting meeting, ISpeakerAttributionHook hook, out bool ingested)
    {
        var participants = meeting.Participants ?? [];
        var before = Signature(participants);
        ingested = hook.IngestObservations(participants);
        if (meeting.Participants is null && participants.Count > 0) meeting.Participants = participants;
        return before != Signature(participants);
    }

    private static string Signature(List<MeetingParticipant> participants) =>
        string.Join('\u001f', participants.Select(participant => participant.Id.ToString("N") + participant.Name));

    private static bool Revise(Meeting meeting, IReadOnlyList<TranscriptRevision> revisions) =>
        revisions.Count > 0 && meeting.ReviseSegments(revisions.Select(revision => (revision.Index, revision.Segment)).ToList()) > 0;

    private void FinalizeAttribution(Meeting meeting, ISpeakerAttributionHook hook, double until)
    {
        try
        {
            Ingest(meeting, hook, out _);
            var result = hook.OnStopped(meeting.Transcript, until);
            Revise(meeting, result.Revisions);
            // Spec 5.2: the session's evidence is stored, so every decision can be replayed offline.
            if (result.Timeline.Count > 0) (meeting.SpeakerTimeline ??= []).AddRange(result.Timeline);
        }
        catch (Exception failure)
        {
            Trace.TraceError("Wallfly: finishing the Teams speaker attribution failed: " + failure.Message);
            try { hook.Cancel(); } catch (Exception) { }
        }
        attributionActive = false;
        TeamsSpeakerStatus = "";
    }

    /// <summary>
    /// A Teams call detected after the recording started (the user starts recording, then joins): live names start now,
    /// with the call's Teams process tree, whatever the audio source. UI thread; at most once per attempt.
    /// </summary>
    private void StartLateAttribution(RecordingSession session, TeamsCallProbe probe)
    {
        if (recording != session || sessionID != session.SessionId || !IsRecording || IsStopping || closing || attributionActive) return;
        var pid = probe.ProcessId > 0 ? probe.ProcessId : resolveProcess(ProcessLookup.TeamsExecutable) ?? 0;
        if (RecordingAudioSelection is { } selection)
            RecordingAudioSelection = selection with
            {
                TeamsCall = true, TeamsProcessId = pid,
                Explanation = selection.Explanation == CoreMessages.TeamsMeetingNotDetectable ? "" : selection.Explanation
            };
        // The attempt's entry in the meeting file says that this recording had a Teams call (detected late).
        if (sessionRecord is { } record && Meetings.FirstOrDefault(item => item.Id == session.MeetingId) is { } meeting)
        {
            record.TeamsCall = true;
            record.TeamsProbe = probe.Status.ToString();
            if (record.Explanation == CoreMessages.TeamsMeetingNotDetectable) record.Explanation = null;
            MarkDirty(meeting);
        }
        StartAttribution(session, session.MeetingId, pid);
        WallflyLog.Info($"Recording Teams call {LogTag(session.MeetingId, session.SessionId)} detected at {Seconds(session.MeetingClock.NowSeconds)} " +
            $"teamsPid={pid} teamsNames={attributionActive}");
    }

    // ---- Teams watch (auto-stop, spec 6; late names) and the silent-meeting-audio guard (spec 3.2, Windows) -------------

    /// <param name="lateTeamsNames">Names are wanted but no call was detected at the start: the Teams watch starts them once a call is active.</param>
    private void StartWatches(RecordingSession session, AppSettings settings, bool lateTeamsNames)
    {
        StopWatches();
        if (hooks.TeamsCalls is not { } teams) return;
        var cancellation = watchCancellation = new();
        if (settings.AutoStopWhenMeetingEnds || lateTeamsNames)
            _ = WatchTeamsCallAsync(session, teams, settings.AutoStopWhenMeetingEnds, lateTeamsNames, cancellation.Token);
        _ = GuardSilentOthersAsync(session, teams, cancellation.Token);
    }

    private void StopWatches()
    {
        var cancellation = Interlocked.Exchange(ref watchCancellation, null);
        if (cancellation is null) return;
        try { cancellation.Cancel(); } catch (ObjectDisposedException) { }
        cancellation.Dispose();
    }

    /// <summary>
    /// The recording's Teams watch: Teams is sampled every <see cref="RecordingTimings.AutoStopInterval"/> (2 s).
    /// <list type="bullet">
    /// <item>Auto-stop (spec 6, macOS <c>startMeetingEndWatch</c>): the meeting-end policy arms after two active samples and
    /// fires after ≥ 10 s absent / not running; then the recording stops with "Meeting ended · Recording stopped automatically".
    /// Personal meetings without Teams are never stopped. Windows: UI Automation alone never ends a recording
    /// (<see cref="JudgeMeetingEnd"/>): while Teams still has an active render or capture session, or Others was audible in
    /// the window while Teams has a render session, or the sessions cannot be read, the stop is deferred
    /// (<see cref="IMeetingEndDetector.Veto"/>) and judged again at the next absent sample; an active sample forgets it.
    /// Only a Teams that is not running at all stops on the UI evidence alone.</item>
    /// <item>Late names (<paramref name="lateNames"/>): the first active sample starts live Teams names
    /// (<see cref="StartLateAttribution"/>). Without auto-stop the watch ends there.</item>
    /// </list>
    /// </summary>
    private async Task WatchTeamsCallAsync(RecordingSession recordingSession, ITeamsCallSource teams, bool autoStop, bool lateNames, CancellationToken token)
    {
        var session = recordingSession.SessionId;
        var tag = LogTag(recordingSession.MeetingId, session);
        try
        {
            var detector = teams.CreateMeetingEndDetector();
            double? absentSince = null;
            var deferred = false;
            while (!token.IsCancellationRequested && (autoStop || lateNames))
            {
                TeamsCallProbe probe;
                try { probe = await teams.SampleAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { return; }
                catch (Exception) { probe = new(TeamsCallStatus.Unknown); }
                if (token.IsCancellationRequested) return;
                if (lateNames && probe.Status == TeamsCallStatus.Active)
                {
                    lateNames = false;
                    await dispatch(() => StartLateAttribution(recordingSession, probe)).ConfigureAwait(false);
                }
                var now = monotonic();
                if (probe.Status is TeamsCallStatus.Absent or TeamsCallStatus.NotRunning) absentSince ??= now;
                else
                {
                    // Like the policy: any other sample ends the absence episode.
                    if (deferred && autoStop)
                        WallflyLog.Info($"Recording meeting-end watch {tag}: deferral over, the Teams UI says {probe.Status} again");
                    absentSince = null;
                    deferred = false;
                }
                if (autoStop && detector.Observe(probe.Status, now))
                {
                    var (stop, evidence, why) = JudgeMeetingEnd(recordingSession, teams, probe.Status, now - (absentSince ?? now));
                    if (!stop)
                    {
                        detector.Veto();
                        if (!deferred)
                        {
                            deferred = true;
                            WallflyLog.Info($"Recording meeting-end watch {tag}: meeting end deferred: {why} | {evidence}");
                        }
                    }
                    else
                    {
                        WallflyLog.Info($"Recording meeting-end watch {tag}: the Teams call has ended; stopping | {evidence}");
                        Task? stopping = null;
                        await dispatch(() => stopping = StopBecauseMeetingEndedAsync(session, evidence)).ConfigureAwait(false);
                        if (stopping != null) await stopping.ConfigureAwait(false);
                        return;
                    }
                }
                if (!autoStop && !lateNames) return;
                await Task.Delay(hooks.Timings.AutoStopInterval, token).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { Trace.TraceError("Wallfly: the Teams watch of the recording failed: " + failure.Message); }
    }

    private async Task StopBecauseMeetingEndedAsync(Guid session, string evidence)
    {
        if (sessionID != session || !IsRecording || IsStopping || closing) return;
        await StopRecordingAsync(reason: RecordingStopReasons.MeetingEnded, evidence: evidence).ConfigureAwait(true);
        if (!IsRecording && sessionID == null) Status = CoreMessages.MeetingEndedStopped;
    }

    /// <summary>
    /// The auto-stop's decision once the Teams UI has said Absent / NotRunning for ≥ 10 s (off the UI thread). Teams not
    /// running at all: stop. Otherwise the audio sessions of the Teams process tree must show no running call: no ACTIVE
    /// render session, no ACTIVE capture session, and Others not audible within <see cref="RecordingTimings.MeetingEndAudioWindow"/>
    /// while Teams has a render session. Unreadable sessions never stop. Returns the evidence for the log and the session entry.
    /// </summary>
    private (bool Stop, string Evidence, string Why) JudgeMeetingEnd(RecordingSession session, ITeamsCallSource teams, TeamsCallStatus status, double absentFor)
    {
        var ui = string.Create(CultureInfo.InvariantCulture, $"ui={status} for {absentFor:0}s");
        if (status == TeamsCallStatus.NotRunning) return (true, ui + "; Teams not running", "");
        TeamsCallAudio? audio;
        try { audio = teams.CallAudio(); }
        catch (Exception failure)
        {
            Trace.TraceWarning("Wallfly: reading the Teams audio sessions failed: " + failure.Message);
            audio = null;
        }
        if (audio is null) return (false, ui + "; audio sessions unreadable", "Teams audio sessions unreadable");
        var window = Math.Clamp(hooks.Timings.MeetingEndAudioWindow.TotalSeconds, 1, RecordingSession.AudibleBuckets - 1);
        var others = session.OthersAudibleSeconds(session.MeetingClock.NowSeconds, window);
        var othersAudible = audio.RenderSession && others >= hooks.Timings.OthersAudibleMinimum.TotalSeconds;
        var evidence = string.Create(CultureInfo.InvariantCulture, $"{ui}; {audio.Detail}; others={others:0.0}s audible in {window:0}s");
        var active = new List<string>();
        if (audio.RenderActive) active.Add("render");
        if (audio.CaptureActive) active.Add("capture");
        if (othersAudible) active.Add("others audible");
        return active.Count == 0 ? (true, evidence, "") : (false, evidence, $"Teams audio session active ({string.Join("/", active)})");
    }

    // ---- silent meeting audio: the guard (output-device follow and "No meeting audio from …") ----------------

    private enum GuardAction { SwitchToDeviceLoopback, Retarget, Reopen, Warn }

    /// <summary>
    /// The silent-meeting-audio guard of every recording (Windows; output-device follow plus the "No meeting audio
    /// from …" banner). Every <see cref="RecordingTimings.SilentOthersCheck"/> (3 s) it looks at the Others channel of the
    /// current engine. Only once that capture has run for <see cref="RecordingTimings.SilentOthersWindow"/> (30 s, since the
    /// start or the last switch) with less than <see cref="RecordingTimings.OthersAudibleMinimum"/> (1 s) of audible audio in
    /// that window (<see cref="RecordingSession.OthersAudibleSeconds"/>: notification chimes do not count) are the render
    /// sessions read (<see cref="ITeamsCallSource.RenderActivity"/>; never on the UI or an audio thread).
    /// <list type="bullet">
    /// <item>Nothing audibly plays: nothing happens. Silence is legitimate (nobody speaks, the user talks or presents), and an
    /// active Teams call alone proves nothing.</item>
    /// <item>Something audibly plays in <see cref="RecordingTimings.RequiredAudibleChecks"/> consecutive checks: (a) per-process
    /// capture (whole system, an included app) switches to device loopback on the playing session's endpoint; (b) device
    /// loopback on another endpoint is retargeted to the playing one; (c) device loopback on the playing endpoint is reopened
    /// once, and if it stays silent for the next window, <see cref="MeetingAudioWarning"/> is set and logged once.</item>
    /// </list>
    /// At most <see cref="RecordingTimings.GuardAttemptLimit"/> switches / reopens within <see cref="RecordingTimings.GuardAttemptWindow"/>
    /// (meeting clock), then only the warning. Audible Others clears the warning and resets the episode. For an included app
    /// only that app's sessions count (Teams: its process tree). A switch still pending skips the round.
    /// <para>
    /// Short path for (b) (a manual headset ↔ speaker change while the endpoint is pinned, e.g. because Teams renders to a
    /// non-default communications endpoint): device loopback that has run and stayed silent for
    /// <see cref="RecordingTimings.OtherEndpointSilence"/> (6 s; chimes do not count) while an audible session plays on a
    /// DIFFERENT endpoint than the one recorded in <see cref="RecordingTimings.RequiredAudibleChecks"/> consecutive checks is
    /// retargeted at once. Audio on our own endpoint never takes it; it counts towards the same attempt limit and never warns.
    /// </para>
    /// <para>
    /// The endpoint recorded is <see cref="recordingEndpoint"/>: the configured id, or for "follow the default" the default
    /// CONSOLE render endpoint, the role the native layer follows. When the native layer moves the endpoint by itself (a
    /// followed default change, a lost pinned endpoint), <see cref="FollowNativeRender"/> re-resolves it, the session restarts
    /// the silence measurement and this guard restarts its consecutive checks; that is not one of its attempts.
    /// </para>
    /// <para>
    /// With a Teams call known for the recording (<see cref="MeetingAudioSelection.TeamsCall"/>) the render sessions are read
    /// with Teams preferred: while the Teams process tree has any session, only its loudest session counts, so a louder video
    /// on another endpoint can neither retarget the capture away from the call nor raise the warning in its name.
    /// </para>
    /// <para>
    /// Last resort (<see cref="RecordingTimings.OthersUnheardLimit"/>, 120 s): a device that keeps flapping restarts the
    /// measurement before any window is reached; once Others has not been heard for that long across all restarts
    /// (<see cref="RecordingSession.OthersUnheardSeconds"/>) while something audibly plays in consecutive checks, the warning is
    /// raised (once per episode) without a further switch.
    /// </para>
    /// </summary>
    private async Task GuardSilentOthersAsync(RecordingSession session, ITeamsCallSource teams, CancellationToken token)
    {
        var timings = hooks.Timings;
        var window = timings.SilentOthersWindow.TotalSeconds;
        var minimum = timings.OthersAudibleMinimum.TotalSeconds;
        var quickWindow = Math.Min(timings.OtherEndpointSilence.TotalSeconds, window);
        var attemptWindow = timings.GuardAttemptWindow.TotalSeconds;
        var unheardLimit = timings.OthersUnheardLimit.TotalSeconds;
        var required = Math.Max(1, timings.RequiredAudibleChecks);
        var tag = LogTag(session.MeetingId, session.SessionId);
        var attempts = new Queue<double>();
        var playingChecks = 0;
        var elsewhereChecks = 0;
        var unheardChecks = 0;
        var episode = false;
        var warned = false;
        var limitLogged = false;
        string? reopened = null;
        var renderMoves = Volatile.Read(ref nativeRenderMoves);
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(timings.SilentOthersCheck, token).ConfigureAwait(false);
                var now = session.MeetingClock.NowSeconds;
                var moves = Volatile.Read(ref nativeRenderMoves);
                if (moves != renderMoves)
                {
                    // The native layer moved the endpoint by itself: a fresh capture, judged afresh (not an attempt).
                    renderMoves = moves;
                    playingChecks = 0; elsewhereChecks = 0; reopened = null;
                }
                var audible = session.OthersAudibleSeconds(now, window);
                var observed = session.OthersObservedSeconds(now);
                var config = Volatile.Read(ref recordingConfig);
                // The short path: device loopback silent for quickWindow (a capture that has run that long).
                var quickSilent = config is { OthersMode: OthersMode.DeviceLoopback } && quickWindow > 0 && observed >= quickWindow
                    && session.OthersAudibleSeconds(now, quickWindow) < minimum;
                // A capture that has not run for a whole window yet (start, right after a switch) is not judged silent.
                var longSilent = audible < minimum && observed >= window;
                // Across every restart of the measurement (a flapping device restarts it before any window is reached): the
                // last resort that still warns.
                var unheard = session.OthersUnheardSeconds(now);
                var stalled = unheardLimit > 0 && unheard >= unheardLimit;
                if (audible >= minimum)
                {
                    if (episode) WallflyLog.Info(string.Create(CultureInfo.InvariantCulture,
                        $"Recording guard {tag}: Others audible again at {Seconds(now)} ({audible:0.0}s in the last {window:0}s)"));
                    episode = false; limitLogged = false; reopened = null; playingChecks = 0;
                    attempts.Clear();
                    if (warned)
                    {
                        warned = false;
                        await dispatch(() => ClearMeetingAudioWarning(session, tag, now)).ConfigureAwait(false);
                    }
                }
                if (!longSilent) playingChecks = 0;
                if (!quickSilent) elsewhereChecks = 0;
                if (!stalled) unheardChecks = 0;
                if (!longSilent && !quickSilent && !stalled) continue;
                if (longSilent && !episode)
                {
                    episode = true;
                    WallflyLog.Info(string.Create(CultureInfo.InvariantCulture,
                        $"Recording guard {tag}: Others silent at {Seconds(now)} ({audible:0.0}s audible in the last {window:0}s); checking the render sessions"));
                }
                var selection = Volatile.Read(ref recordingAudioSelection);
                // A Teams call is known for this recording: the Teams process tree's sessions decide (RenderActivity with
                // preferTeams). A louder video elsewhere then never counts while Teams has any session, so the guard never
                // moves away from the call's endpoint to another app's; only without any Teams session the loudest app counts.
                var preferTeams = selection is { TeamsCall: true } && selection.Kind != MeetingAudioKind.App;
                RenderSessionActivity? activity = null;
                try { activity = teams.RenderActivity(preferTeams); }
                catch (Exception failure) { Trace.TraceWarning("Wallfly: reading the render sessions failed: " + failure.Message); }
                if (config is null) continue;
                var playing = activity is { } candidate && candidate.Peak > timings.RenderPeakThreshold && Captures(selection, candidate);
                var bound = Volatile.Read(ref recordingEndpoint);
                var elsewhere = playing && bound.Length > 0 && activity!.EndpointId.Length > 0 && !SameEndpoint(bound, activity.EndpointId);
                playingChecks = longSilent && playing ? playingChecks + 1 : 0;
                elsewhereChecks = quickSilent && elsewhere ? elsewhereChecks + 1 : 0;
                unheardChecks = stalled && playing ? unheardChecks + 1 : 0;
                var quick = elsewhereChecks >= required && playingChecks < required;
                if (playingChecks < required && !quick)
                {
                    // Neither window can judge (the measurement keeps restarting), but Others has not been heard for
                    // OthersUnheardLimit while something audibly plays: the warning, once per episode.
                    if (unheardChecks >= required && !warned)
                    {
                        warned = true;
                        var playingNow = activity!;
                        var text = (playingNow.IsTeams ? CoreMessages.NotReceivingAudio(CoreMessages.MeetingAudioTeams) : CoreMessages.NotReceivingAudioOtherApps);
                        var reason = string.Create(CultureInfo.InvariantCulture,
                            $"othersUnheard={unheard:0.0}s playing pid={playingNow.ProcessId} process={playingNow.ProcessName} peak={playingNow.Peak:0.0000} " +
                            $"endpoint=\"{playingNow.EndpointId}\" teams={playingNow.IsTeams} checks={unheardChecks} othersMode={config.OthersMode} render=\"{config.RenderDeviceId}\"");
                        await dispatch(() => RaiseMeetingAudioWarning(session, tag, text, audible, window, reason)).ConfigureAwait(false);
                    }
                    continue;
                }

                var active = activity!;
                var action = quick ? GuardAction.Retarget
                    : config.OthersMode != OthersMode.DeviceLoopback ? GuardAction.SwitchToDeviceLoopback
                    : active.EndpointId.Length > 0 && !SameEndpoint(bound, active.EndpointId) ? GuardAction.Retarget
                    : reopened != null && reopened == bound ? GuardAction.Warn
                    : GuardAction.Reopen;
                while (attempts.Count > 0 && attempts.Peek() <= now - attemptWindow) attempts.Dequeue();
                if (action != GuardAction.Warn && attempts.Count >= Math.Max(0, timings.GuardAttemptLimit))
                {
                    if (!limitLogged) WallflyLog.Warning($"Recording guard {tag}: {attempts.Count} switch attempts within {Seconds(attemptWindow)}; only warning until Others is audible again");
                    limitLogged = true;
                    // The short path never warns: the warning needs the whole silent window.
                    if (quick) { elsewhereChecks = 0; continue; }
                    action = GuardAction.Warn;
                }
                var checks = quick ? elsewhereChecks : playingChecks;
                var playingDetail = string.Create(CultureInfo.InvariantCulture,
                    $"playing pid={active.ProcessId} process={active.ProcessName} peak={active.Peak:0.0000} endpoint=\"{active.EndpointId}\" teams={active.IsTeams} checks={checks}");
                if (action == GuardAction.Warn)
                {
                    if (warned) continue;
                    warned = true;
                    var text = (active.IsTeams ? CoreMessages.NotReceivingAudio(CoreMessages.MeetingAudioTeams) : CoreMessages.NotReceivingAudioOtherApps);
                    var reason = $"{playingDetail} othersMode={config.OthersMode} render=\"{config.RenderDeviceId}\"";
                    await dispatch(() => RaiseMeetingAudioWarning(session, tag, text, audible, window, reason)).ConfigureAwait(false);
                    continue;
                }
                // SwitchEngineAsync takes one replacement at a time: a switch still pending skips this round.
                if (session.SwitchPending) continue;
                var (endpoint, why) = action switch
                {
                    GuardAction.SwitchToDeviceLoopback => (active.EndpointId.Length > 0 ? active.EndpointId : config.RenderDeviceId, "processLoopbackSilent"),
                    GuardAction.Retarget => (active.EndpointId, "renderEndpointChanged"),
                    _ => (config.RenderDeviceId, "deviceLoopbackSilent")
                };
                var silence = quick
                    ? string.Create(CultureInfo.InvariantCulture, $"{session.OthersAudibleSeconds(now, quickWindow):0.0}s audible in the last {quickWindow:0}s, audio plays on another endpoint")
                    : string.Create(CultureInfo.InvariantCulture, $"{audible:0.0}s audible in the last {window:0}s");
                WallflyLog.Warning(string.Create(CultureInfo.InvariantCulture,
                    $"Recording guard {tag}: Others silent ({silence}) while something plays ({playingDetail}) at {Seconds(now)}; " +
                    $"{action} from {config.OthersMode} render=\"{config.RenderDeviceId}\" bound=\"{bound}\" to deviceLoopback render=\"{endpoint}\""));
                attempts.Enqueue(now);
                playingChecks = 0; elsewhereChecks = 0;
                // The target's bound endpoint and name come from the device list read here, never on the UI thread.
                var resolved = ResolveRender(Devices, endpoint, needName: true);
                Task<bool>? switching = null;
                await dispatch(() => switching = SwitchOthersModeAsync(session, OthersMode.DeviceLoopback, endpoint, why, resolved)).ConfigureAwait(false);
                var switched = switching != null && await switching.ConfigureAwait(false);
                if (switched && action == GuardAction.Reopen) reopened = Volatile.Read(ref recordingEndpoint);
                // The switch's own render move is not a native one; keep the counter in step.
                renderMoves = Volatile.Read(ref nativeRenderMoves);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception failure) { Trace.TraceError("Wallfly: the silent meeting-audio guard failed: " + failure.Message); }
    }

    /// <summary>Whether the capture of <paramref name="selection"/> should hear <paramref name="activity"/>.</summary>
    private static bool Captures(MeetingAudioSelection? selection, RenderSessionActivity activity) => selection?.Kind switch
    {
        MeetingAudioKind.Teams => activity.IsTeams,
        MeetingAudioKind.App => string.Equals(activity.ProcessName, selection.AppName, StringComparison.OrdinalIgnoreCase),
        _ => true
    };

    private static bool SameEndpoint(string bound, string endpoint) =>
        bound.Length > 0 && string.Equals(bound, endpoint, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the running engine by one with <paramref name="mode"/> on <paramref name="renderDeviceId"/> (the guard: switch
    /// to device loopback, retarget, reopen) through <see cref="RecordingSession.SwitchEngineAsync"/>, without a recording gap;
    /// the microphone and the processing settings stay. Records the switch in <see cref="RecordingSessionInfo.Switches"/>,
    /// logs it and, unless it is a reopen of the same source, shows <see cref="CoreMessages.MeetingAudioSwitchedToOutput"/>.
    /// UI thread. False when the replacement did not take over (the current engine keeps recording).
    /// </summary>
    /// <param name="resolved">The bound endpoint and name of <paramref name="renderDeviceId"/>, read off the UI thread by the caller.</param>
    private async Task<bool> SwitchOthersModeAsync(RecordingSession session, OthersMode mode, string renderDeviceId, string reason,
        (string Bound, string? Name) resolved)
    {
        if (recording != session || sessionID != session.SessionId || !IsRecording || IsStopping || closing || recordingConfig is not { } current
            || session.SwitchPending)
            return false;
        IAudioEngine next;
        try { next = engineFactory(Settings); }
        catch (Exception failure)
        {
            Trace.TraceError("Wallfly: no engine for the meeting-audio switch: " + failure.Message);
            return false;
        }
        var config = new AudioEngineConfig
        {
            MicrophoneDeviceId = current.MicrophoneDeviceId,
            RenderDeviceId = renderDeviceId ?? "",
            OthersMode = mode,
            TargetProcessId = mode == OthersMode.ProcessInclude ? current.TargetProcessId : 0,
            NoiseSuppressionLevel = current.NoiseSuppressionLevel,
            EnableEchoCancellation = current.EnableEchoCancellation,
            PreferRawMicrophone = current.PreferRawMicrophone,
            AllowDeviceLoopbackFallback = current.AllowDeviceLoopbackFallback,
            KeepAliveMode = current.KeepAliveMode,
            TimestampMode = current.TimestampMode
        };
        bool switched;
        var at = session.MeetingClock.NowSeconds;
        try { switched = await session.SwitchEngineAsync(next, config, hooks.Timings.EngineCutover).ConfigureAwait(true); }
        catch (Exception failure)
        {
            Trace.TraceError("Wallfly: switching the meeting audio failed: " + failure.Message);
            switched = false;
        }
        var tag = LogTag(session.MeetingId, session.SessionId);
        if (!switched || recording != session)
        {
            WallflyLog.Warning($"Recording source switch {tag}: the {mode} engine on render=\"{config.RenderDeviceId}\" did not take over; the source stays " +
                $"{current.OthersMode} render=\"{current.RenderDeviceId}\" reason={reason}");
            return false;
        }
        var name = resolved.Name;
        WallflyLog.Info($"Recording source switch {tag} at={Seconds(at)} from={current.OthersMode} to={config.OthersMode} render=\"{config.RenderDeviceId}\" " +
            $"renderName=\"{name ?? ""}\" generation={session.EngineGeneration} reason={reason}");
        recordingConfig = config;
        recordingConfigGeneration = session.EngineGeneration;
        Volatile.Write(ref recordingEndpoint, resolved.Bound);
        var reopen = current.OthersMode == config.OthersMode && string.Equals(current.RenderDeviceId, config.RenderDeviceId, StringComparison.OrdinalIgnoreCase);
        if (!reopen && mode == OthersMode.DeviceLoopback && RecordingAudioSelection is { } selection)
            RecordingAudioSelection = selection.SwitchedToDeviceLoopback(config.RenderDeviceId, name);
        if (!reopen)
        {
            AudioDeviceNotice = CoreMessages.MeetingAudioSwitchedToOutputDevice(name);
            audioDeviceNoticeSince = time.GetTimestamp();
        }
        if (sessionRecord != null && Meetings.FirstOrDefault(item => item.Id == session.MeetingId) is { } meeting)
        {
            AddSwitch(session, new()
            {
                At = at, From = Key(current.OthersMode), To = Key(config.OthersMode), Device = NullIfEmpty(name),
                DeviceId = NullIfEmpty(config.RenderDeviceId), Reason = reason
            });
            MarkDirty(meeting);
        }
        // What the replacement reported before its cutover (e.g. that X did not open and it fell back to the default) now
        // applies to the configuration it really runs with.
        _ = dispatch(() => { if (recording == session && ApplyDeferredEngineEvents(session)) MarkDirtyMeeting(session.MeetingId); });
        Updated?.Invoke();
        return true;
    }

    private void MarkDirtyMeeting(Guid meetingID)
    {
        if (Meetings.FirstOrDefault(item => item.Id == meetingID) is { } meeting) MarkDirty(meeting);
    }

    private void RaiseMeetingAudioWarning(RecordingSession session, string tag, string text, double audibleSeconds, double window, string reason)
    {
        if (recording != session || !IsRecording || IsStopping || MeetingAudioWarning == text) return;
        MeetingAudioWarning = text;
        WallflyLog.Warning(string.Create(CultureInfo.InvariantCulture,
            $"Recording warning raised {tag}: Others silent ({audibleSeconds:0.0}s audible in the last {window:0}s); {reason} -> \"{text}\""));
    }

    private void ClearMeetingAudioWarning(RecordingSession session, string tag, double now)
    {
        if (recording != session || MeetingAudioWarning.Length == 0) return;
        MeetingAudioWarning = "";
        WallflyLog.Info($"Recording warning cleared {tag}: Others audible again at {Seconds(now)}");
    }

    /// <summary>The endpoint a capture on <paramref name="id"/> is bound to: the id itself, or for "" the current default render endpoint ("" when unknown).</summary>
    private string BoundEndpoint(string id) => id.Length > 0 ? id : ResolveRender(Devices, id, needName: false).Bound;

    /// <summary>
    /// The endpoint a capture on <paramref name="id"/> is bound to (the id, or for "" the default console render endpoint; ""
    /// when unknown) and, with <paramref name="needName"/>, its name (null when unknown), from ONE device listing; no listing
    /// at all for a pinned id whose name is not needed. Any thread; call it off the UI thread during a recording.
    /// </summary>
    private static (string Bound, string? Name) ResolveRender(IAudioDeviceCatalog devices, string id, bool needName)
    {
        if (id.Length > 0 && !needName) return (id, null);
        try
        {
            var list = devices.List(AudioFlow.Render);
            var device = id.Length == 0
                ? list.FirstOrDefault(item => item.Id.Length > 0 && item.IsDefaultConsole)
                : list.FirstOrDefault(item => string.Equals(item.Id, id, StringComparison.OrdinalIgnoreCase));
            return (id.Length > 0 ? id : device?.Id ?? "", device?.Name);
        }
        catch (Exception error)
        {
            Trace.TraceWarning("Wallfly: listing output devices failed: " + error.Message);
            return (id, null);
        }
    }

    // ---- diagnostics helpers --------------------------------------------------------------------------------------

    /// <summary>Clears the per-attempt diagnostic state (start, start failure, stop).</summary>
    private void ResetRecordingDiagnostics()
    {
        sessionRecord = null;
        MeetingAudioWarning = "";
        AudioDeviceNotice = "";
        engineEventCounts.Clear();
        lostDevices.Clear();
        deferredEngineEvents.Clear();
        recordingConfigGeneration = 0;
        renderFollowVersion++;
        Volatile.Write(ref recordingEndpoint, "");
    }

    private static string LogTag(Guid meeting, Guid session) => $"meeting={meeting:D} session={session:D}";

    private static string Seconds(double value) => value.ToString("0.0##", CultureInfo.InvariantCulture) + "s";

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;

    /// <summary>camelCase of an enum value, the stored form in <see cref="RecordingSessionInfo"/>.</summary>
    private static string Key<T>(T value) where T : struct, Enum => System.Text.Json.JsonNamingPolicy.CamelCase.ConvertName(value.ToString());

    private static string DescribeSelection(MeetingAudioSelection selection) =>
        $"kind={selection.Kind} othersMode={selection.OthersMode} automatic={selection.Automatic} pid={selection.ProcessId} app=\"{selection.AppName}\" " +
        $"render=\"{selection.RenderDeviceId}\" teamsCall={selection.TeamsCall} teamsPid={selection.TeamsProcessId} explanation=\"{selection.Explanation}\"";

    /// <summary>The model of an attempt for the log and <see cref="RecordingSessionInfo.TranscriptionModel"/>.</summary>
    private static string TranscriptionModelName(AppSettings settings, bool teamsNames) => settings.TranscriptionProvider switch
    {
        TranscriptionProvider.Deepgram => DeepgramUrlBuilder.Model,
        TranscriptionProvider.LocalWhisper => settings.WhisperModelPath.Length > 0 ? Path.GetFileName(settings.WhisperModelPath) : "ggml-large-v3-turbo.bin",
        _ => teamsNames
            ? OpenAiTranscriptionOptions.ModelFor(AudioSource.Me, true) + "/" + OpenAiTranscriptionOptions.ModelFor(AudioSource.Others, true)
            : OpenAiTranscriptionOptions.DefaultModel
    };

    /// <summary>The name of the output device <paramref name="id"/> (empty: the default console endpoint); null when unknown.</summary>
    private string? RenderDeviceName(string id) => ResolveRender(Devices, id, needName: true).Name;

    /// <summary>Deepgram's numeric diagnostics (one line per 30 s and per connection change; never text) go to the log.</summary>
    private static void ObservePipeline(ITranscriptionPipeline pipeline, string tag)
    {
        if (pipeline is DeepgramStreamingPipeline deepgram) deepgram.DiagnosticsReported += line => WallflyLog.Info($"Transcription {tag} {line}");
    }

    private static void LogStopSummary(string tag, string reason, RecordingSession session, double attemptSeconds, double totalSeconds, DrainResult result, bool complete)
    {
        var stats = session.Statistics;
        string Channel(string name, ChannelStatistics channel, AudioSource source)
        {
            var (count, seconds) = stats.GapsOf(source);
            return string.Create(CultureInfo.InvariantCulture,
                $"{name}: frames={channel.Frames} audio={channel.Seconds:0.0}s audible={channel.AudibleSeconds:0.0}s gaps={count}/{seconds:0.000}s");
        }
        var reasons = stats.Gaps.Count == 0 ? "none"
            : string.Join(", ", stats.Gaps.Select(gap => string.Create(CultureInfo.InvariantCulture, $"{gap.Source}/{gap.Reason} {gap.Count}x{gap.Seconds:0.000}s")));
        var line = string.Create(CultureInfo.InvariantCulture,
            $"Recording stop {tag} reason={reason} attempt={attemptSeconds:0.0}s total={totalSeconds:0.0}s complete={complete} drainComplete={result.Complete} | " +
            $"{Channel("me", stats.Me, AudioSource.Me)} | {Channel("others", stats.Others, AudioSource.Others)} | " +
            $"reference gaps={stats.GapsOf(AudioSource.Reference).Count}/{stats.GapsOf(AudioSource.Reference).Seconds:0.000}s | gapReasons: {reasons} | " +
            $"transcriptionGaps me={stats.MeTranscriptionGaps}/{stats.MeTranscriptionGapSeconds:0.0}s others={stats.OthersTranscriptionGaps}/{stats.OthersTranscriptionGapSeconds:0.0}s | " +
            $"engineGeneration={stats.EngineGeneration} staleFrames={stats.StaleFramesDropped} | {DescribeDiagnostics(session.Engine)}");
        if (complete) WallflyLog.Info(line); else WallflyLog.Warning(line);
        if (result.Error != null) WallflyLog.Warning($"Recording stop {tag}: {result.Error}");
    }

    /// <summary>
    /// The native recording-health diagnostics (<see cref="IAudioEngine.Diagnostics"/>, cumulative for the engine): longest
    /// DSP tick gap and 100 ms catch-up slices, longest capture gap and ring high water per stream (of the ring's slots),
    /// lost ring intervals, the power request, render endpoint moves, echo-canceller resets for them and the keep-alive's
    /// starts/failures (0 from a DLL with the 72-byte struct); "diag: n/a" for an engine (an older DLL) that cannot tell.
    /// </summary>
    private static string DescribeDiagnostics(IAudioEngine engine)
    {
        AudioDiagnostics? diagnostics;
        try { diagnostics = engine.Diagnostics; }
        catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or DllNotFoundException or EntryPointNotFoundException) { diagnostics = null; }
        if (diagnostics is not { } value) return "diag: n/a";
        return string.Create(CultureInfo.InvariantCulture,
            $"diag: tickGapMax={value.DspTickGapMaxMs:0.0}ms slicesMax={value.DspSlicesMax} " +
            $"captureGapMax={value.CaptureGapMaxMsMe:0.0}/{value.CaptureGapMaxMsOthers:0.0}/{value.CaptureGapMaxMsReference:0.0}ms " +
            $"ringHighWater={value.RingHighWaterMe}/{value.RingHighWaterOthers}/{value.RingHighWaterReference} of {value.RingCapacitySlots} " +
            $"ringLosses={value.RingLossGaps} powerRequest={value.PowerRequest} " +
            $"renderSwitches={value.RenderSwitches} aecRenderResets={value.AecRenderResets} keepAlive={value.KeepAliveStarts}/{value.KeepAliveFailures}");
    }

    /// <summary>
    /// The 30 s health line of a recording (per-source statistics): per channel the audible seconds, silent share
    /// and frames of the interval plus the cumulative gaps; engine counters; the native diagnostics (me/others/reference);
    /// the process's CPU time delta, working set and
    /// power source; and how late this timer itself fired (a frozen process delays it, a stalled DSP does not).
    /// Runs on a thread-pool timer, never on the UI or an audio thread.
    /// </summary>
    private sealed class HealthMonitor(RecordingSession session, string tag, TimeSpan interval)
    {
        private ChannelStatistics lastMe;
        private ChannelStatistics lastOthers;
        private TimeSpan lastCpu = Cpu();
        private long lastTick = Stopwatch.GetTimestamp();
        private int running;

        public void Tick()
        {
            if (Interlocked.Exchange(ref running, 1) != 0) return;
            try { WallflyLog.Info(Line()); }
            catch (Exception error) { Trace.TraceWarning("Wallfly: the recording health line failed: " + error.Message); }
            finally { Volatile.Write(ref running, 0); }
        }

        private string Line()
        {
            var tick = Stopwatch.GetTimestamp();
            var late = Stopwatch.GetElapsedTime(lastTick, tick) - interval;
            lastTick = tick;
            var stats = session.Statistics;
            var me = stats.Me - lastMe;
            var others = stats.Others - lastOthers;
            lastMe = stats.Me; lastOthers = stats.Others;
            var cpu = Cpu();
            var cpuDelta = cpu - lastCpu;
            lastCpu = cpu;
            string Channel(string name, ChannelStatistics channel, AudioSource source)
            {
                var (count, seconds) = stats.GapsOf(source);
                var silentShare = channel.Samples > 0 ? 100.0 * (channel.Samples - channel.AudibleSamples) / channel.Samples : 100.0;
                return string.Create(CultureInfo.InvariantCulture,
                    $"{name}: audible={channel.AudibleSeconds:0.0}s silent={silentShare:0}% frames={channel.Frames} gaps={count}/{seconds:0.000}s");
            }
            string engine;
            try
            {
                var metrics = session.Engine.Metrics;
                engine = string.Create(CultureInfo.InvariantCulture,
                    $"engine: state={metrics.State} erle={metrics.ErleDb:0.0}dB delay={metrics.DelayMs}ms ref={metrics.ReferenceModeActual} inboxOverflows={metrics.InboxOverflows} " +
                    $"ringOverflows={metrics.RingOverflows} lateDropped={metrics.LateDropped} framesDropped={metrics.FramesDropped} eventsDropped={metrics.EventsDropped} " +
                    $"restarts={metrics.Restarts} watchdog={metrics.WatchdogRecoveries} discontinuities={metrics.Discontinuities} aecResets={metrics.EchoCancellerResets} attempt={metrics.AttemptId}");
            }
            catch (Exception error) when (error is InvalidOperationException or ObjectDisposedException or DllNotFoundException) { engine = "engine: n/a"; }
            var diagnostics = DescribeDiagnostics(session.Engine);
            long workingSet = 0;
            try { using var process = Process.GetCurrentProcess(); workingSet = process.WorkingSet64; }
            catch (Exception) { }
            return string.Create(CultureInfo.InvariantCulture,
                $"Recording health {tag} t={session.MeetingClock.NowSeconds:0.0}s | {Channel("me", me, AudioSource.Me)} | {Channel("others", others, AudioSource.Others)} | {engine} | {diagnostics} | " +
                $"cpu=+{cpuDelta.TotalSeconds:0.00}s ws={workingSet / (1024 * 1024)}MB power={PowerStatus.Describe()} timerLate={Math.Max(0, late.TotalMilliseconds):0}ms");
        }

        private static TimeSpan Cpu()
        {
            try { using var process = Process.GetCurrentProcess(); return process.TotalProcessorTime; }
            catch (Exception) { return TimeSpan.Zero; }
        }
    }
}
