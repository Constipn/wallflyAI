namespace Wallfly.Core.Services;

/// <summary>
/// Automatic audio deletion (spec 8, macOS <c>markAudioReadyForDeletion</c> / <c>cleanupProcessedAudio</c>): only the
/// recordings enrolled at their start (<see cref="Meeting.AudioDeletionPending"/>) are deleted, only when the transcript
/// is complete, no recovery is needed and, with acoustic speaker recognition on, the diarization run succeeded. The
/// decision (<see cref="Meeting.AudioDeletionReady"/>) is saved before any file is unlinked and retried at the next start;
/// files are only removed inside <c>Audio\&lt;MeetingID&gt;\</c>. Errors, gaps or a skipped diarization keep the audio.
/// </summary>
public static class AudioDeletionPolicy
{
    /// <summary>A recording started with these settings is enrolled for deletion after processing.</summary>
    public static bool Enrolls(AppSettings settings) => settings.KeepAudio && settings.DeleteAudioAfterProcessing;

    /// <summary>Processing is complete enough to delete the enrolled files.</summary>
    public static bool CanMarkReady(Meeting meeting) =>
        meeting.AudioTranscriptionComplete == true && meeting.AudioRecoveryNeeded != true && (meeting.AudioDeletionPending?.Count ?? 0) > 0;

    /// <summary>
    /// After a stop the enrolled files are deleted at once only when no speaker matching follows; otherwise the
    /// diarization step decides after a successful run.
    /// </summary>
    public static bool DeleteAtStop(bool complete, AppSettings settings) => complete && !settings.AcousticSpeakerRecognition;
}
