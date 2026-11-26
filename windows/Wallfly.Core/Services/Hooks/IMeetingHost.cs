namespace Wallfly.Core.Services;

/// <summary>
/// What <see cref="MeetingLibraryService"/> offers the hooks that change meetings outside a recording (diarization,
/// speaker edits). Every member must be called on the UI thread, except <see cref="DispatchAsync"/>, which gets there.
/// </summary>
public interface IMeetingHost
{
    AppSettings Settings { get; }
    /// <summary>The current Deepgram key (empty when none is set), read at each use so a change applies at once. Never log it.</summary>
    string DeepgramKey { get; }
    IReadOnlyList<Meeting> Meetings { get; }
    Meeting? Find(Guid meetingID);
    /// <summary>The meeting that is recording, starting or stopping; nothing else may touch its transcript.</summary>
    Guid? RecordingMeetingID { get; }
    /// <summary>Recording, starting, stopping, AI processing or moving the library (macOS <c>busy</c>).</summary>
    bool IsBusy { get; }
    /// <summary>The app is quitting: abandon work, never wait for it.</summary>
    bool IsClosing { get; }
    bool IsMovingLibrary { get; }
    DateTimeOffset Now { get; }
    /// <summary>Absolute path of a stored audio path (stored values may be relative to the library root).</summary>
    string ResolveAudioPath(string stored);
    /// <summary>Barrier: everything dirty is on disk.</summary>
    bool Flush();
    /// <summary>Marks the meeting dirty and flushes; shows the save error banner on failure.</summary>
    bool Persist(Meeting meeting);
    /// <summary>
    /// Saves <paramref name="edited"/> (a copy of <paramref name="live"/>) and only then publishes its transcript,
    /// participants, acoustic speakers, diarization and timeline into the live meeting. A failed save changes nothing and
    /// shows <paramref name="failurePrefix"/> plus the reason.
    /// </summary>
    bool SaveEdited(Meeting live, Meeting edited, string failurePrefix);
    void ReportError(string message);
    /// <summary>Runs <paramref name="action"/> on the UI thread.</summary>
    Task DispatchAsync(Action action);
    /// <summary>A post-recording step finished successfully: evaluate the audio deletion policy (spec 8) for the meeting.</summary>
    void AudioProcessed(Guid meetingID);
    /// <summary>A step's queue or progress changed: refresh <see cref="MeetingLibraryService.SpeakerMatchingProgress"/>.</summary>
    void StateChanged();
}

/// <summary>A hook that needs the <see cref="IMeetingHost"/>; attached once by the service constructor (after loading).</summary>
public interface IMeetingHostClient
{
    void Attach(IMeetingHost host);
}

/// <summary>
/// Work queued after a recording stopped (macOS <c>rediarize</c> / <c>runDiarization</c>): Wallfly.Integration's
/// <c>DiarizationStep</c> runs the offline acoustic diarization, fuses it with the Teams labels and then lets the host
/// evaluate the audio deletion policy. Runs never block a new recording; quitting abandons them.
/// </summary>
public interface IPostRecordingStep : IMeetingHostClient
{
    /// <summary>Queues a run (after each stop and for "Re-detect speakers"). False when nothing was queued.</summary>
    bool Enqueue(Guid meetingID);
    /// <summary>
    /// "Re-detect speakers on this device": queues a run that uses the local engine whatever the speaker separation setting
    /// says (the user's explicit choice). Steps without engines treat it as <see cref="Enqueue"/>.
    /// </summary>
    bool EnqueueOnDevice(Guid meetingID) => Enqueue(meetingID);
    /// <summary>Queued or running for this meeting.</summary>
    bool IsPending(Guid meetingID);
    /// <summary>
    /// A recording of <paramref name="meetingID"/> is starting (UI thread): work that loads the laptop must yield until no
    /// recording is active (the speaker matching holds its engines back).
    /// </summary>
    void RecordingStarted(Guid meetingID) { }
    /// <summary>
    /// The meeting was deleted or its processed audio archives were removed (UI thread): drop anything derived from them
    /// (e.g. a cached cloud upload).
    /// </summary>
    void MeetingAudioRemoved(Guid meetingID) { }
    /// <summary>Whether "Re-detect speakers" can start for the meeting now.</summary>
    bool CanRun(Guid meetingID);
    /// <summary>Any run queued or running (blocks moving the library).</summary>
    bool HasPendingWork { get; }
    /// <summary>"Matching speakers… 40%"; empty when idle.</summary>
    string Progress { get; }
    /// <summary>Completes once every queued run (including runs queued meanwhile) has ended.</summary>
    Task WhenIdle();
    /// <summary>Quit: abandons queued and running work; results that arrive later are dropped.</summary>
    void Cancel();
    /// <summary>The quit was called off (<see cref="MeetingLibraryService.CancelClose"/>): runs abandoned by <see cref="Cancel"/> are queued again.</summary>
    void Resume();
}

/// <summary>Manual speaker corrections (spec 5.5), persisted through <see cref="Persistence.MeetingWriter"/>.</summary>
public interface ISpeakerEditor
{
    /// <summary>False while the meeting records (its transcript is live), while the library moves or the app quits.</summary>
    bool CanEdit(Guid meetingID);

    /// <summary>
    /// "Rename speaker…": renames the participant in the whole meeting; a synthetic "Speaker k" is instead bound to the
    /// name (its voice stays bound across later diarization runs; an existing name merges into that participant).
    /// </summary>
    bool RenameParticipant(Guid meetingID, Guid participantID, string name);

    /// <summary>
    /// "Assign this segment": <paramref name="participantID"/> (null = Others) with <c>attribution = manual</c> for exactly
    /// the referenced remote words; manual words are protected from every later pass.
    /// </summary>
    bool AssignTranscript(Guid meetingID, IReadOnlyList<TranscriptReference> references, Guid? participantID);

    /// <summary>"Name this passage…": creates a participant (next colour) and assigns exactly the references to it.</summary>
    bool AddParticipant(Guid meetingID, string name, IReadOnlyList<TranscriptReference> references);
}
