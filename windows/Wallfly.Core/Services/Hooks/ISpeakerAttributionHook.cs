namespace Wallfly.Core.Services;

/// <summary>
/// What a live speaker attribution gets when the capture of a recording session runs with Teams names.
/// </summary>
/// <param name="MeetingID">The recording meeting.</param>
/// <param name="SessionID">The recording session (<c>TranscriptSegment.SessionID</c>); only its remote words are labelled.</param>
/// <param name="BaseDuration">Meeting offset at which the session starts (the duration of earlier sessions).</param>
/// <param name="Clock">
/// The engine clock (<see cref="RecordingSession.MeetingClock"/>: <c>IAudioEngine.Clock</c>, forwarded across an engine
/// switch) in meeting seconds. It reads <paramref name="BaseDuration"/> at the session origin, so an observation stamped
/// with it is directly a meeting offset on the same clock as the audio frames.
/// </param>
/// <param name="TeamsProcessId">Root PID of the Teams process tree whose audio is recorded; 0 when unknown.</param>
public sealed record SpeakerAttributionStart(Guid MeetingID, Guid SessionID, double BaseDuration, Func<double> Clock, int TeamsProcessId = 0)
{
    /// <summary>Clock value of the session origin (the same as <see cref="BaseDuration"/>, see <see cref="Clock"/>).</summary>
    public double Origin => BaseDuration;
}

/// <summary>A relabelled segment: <see cref="Index"/> into the transcript list that was passed in.</summary>
public readonly record struct TranscriptRevision(int Index, TranscriptSegment Segment);

/// <summary>The final pass at stop: relabelled segments and the session's evidence timeline (<c>speakerTimeline</c>).</summary>
public sealed record SpeakerAttributionResult(IReadOnlyList<TranscriptRevision> Revisions, IReadOnlyList<SpeakerTimelineEntry> Timeline)
{
    public static SpeakerAttributionResult Empty { get; } = new([], []);
}

/// <summary>
/// Live Teams speaker names (spec 5.2; macOS <c>beginTeamsAttribution</c>, <c>drainTeamsObservations</c>,
/// <c>reconcileTeamsTranscript</c> and the timeline export of <c>stopRecording</c>). Implemented in Wallfly.Integration
/// (<c>SpeakerAttributionHook</c>) so Core stays free of Wallfly.Speakers and Wallfly.Teams. Every member runs on the UI
/// thread (the Teams reads happen on the hook's own UIA thread and are queued); one recording session at a time.
/// </summary>
public interface ISpeakerAttributionHook
{
    /// <summary>True between <see cref="OnRecordingStarted"/> and <see cref="OnStopped"/> / <see cref="Cancel"/>.</summary>
    bool IsActive { get; }

    /// <summary>The Teams speaker status line ("Teams speaker detection active · 3 participants"); empty when inactive.</summary>
    string Status { get; }

    /// <summary>Creates the attribution session and starts the Teams speaker watcher (500 ms + change events).</summary>
    void OnRecordingStarted(SpeakerAttributionStart start);

    /// <summary>
    /// Ingests the Teams observations that arrived since the last call into <paramref name="participants"/> (the meeting's
    /// list, changed in place: new or renamed Teams participants). Returns true when at least one was ingested.
    /// </summary>
    bool IngestObservations(List<MeetingParticipant> participants);

    /// <summary>
    /// Reconciles the live revision window (15 s, 30 s context). <paramref name="arrived"/> is a remote segment that was just
    /// received: it is labelled even when it arrives after the window. Returns only the segments whose labels changed.
    /// </summary>
    IReadOnlyList<TranscriptRevision> OnTranscriptChanged(IReadOnlyList<TranscriptSegment> transcript, Guid? arrived = null);

    /// <summary>Stage "stopping speaker detection" of the stop sequence: stops the watcher after one last read.</summary>
    Task StopObservingAsync();

    /// <summary>
    /// The final pass (every remote word of the session, flicker identities suppressed) and the session's evidence up to
    /// <paramref name="until"/> (the stop offset). Ends the session. The caller ingests the last observations first.
    /// </summary>
    SpeakerAttributionResult OnStopped(IReadOnlyList<TranscriptSegment> transcript, double until);

    /// <summary>Ends the session without a result (start failure, quit); late observations are dropped.</summary>
    void Cancel();
}
