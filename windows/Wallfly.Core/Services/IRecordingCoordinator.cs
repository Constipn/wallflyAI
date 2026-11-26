using System.ComponentModel;
using Wallfly.Audio;
using Wallfly.Core.Persistence;

namespace Wallfly.Core.Services;

/// <summary>
/// Why a recording ended. <see cref="Manual"/> is the user's stop (and every caller that names none); every other reason is
/// recorded in the stop status and as a note in <see cref="Meeting.ProcessingErrors"/>.
/// </summary>
public enum RecordingStopReason
{
    /// <summary>"Stop recording" (or a caller that gives no reason).</summary>
    Manual,
    /// <summary>The auto-stop (spec 6): the Teams call has been gone long enough.</summary>
    MeetingEnded,
    /// <summary>The computer is going to sleep (PBT_APMSUSPEND).</summary>
    Sleep,
    /// <summary>A transcription pipeline failed for good (spec 4.2).</summary>
    PipelineFailed,
    /// <summary>A fatal capture failure of the meeting channel or the session (spec 3.2).</summary>
    EngineFailed,
    /// <summary>Wallfly quits (<see cref="MeetingLibraryService.CloseAsync"/>).</summary>
    Quit
}

/// <summary>
/// The recording lifecycle behind <see cref="MeetingLibraryService"/>: start (source, transcription, archives, capture),
/// inbox drains, levels, Teams speaker names, the Teams guards (auto-stop, include-mode guard), the microphone, and the stop
/// sequence. Its observable properties carry the service's property names; the service forwards their notifications.
/// Every member runs on the UI thread.
/// </summary>
internal interface IRecordingCoordinator : INotifyPropertyChanged, IDisposable
{
    Guid? ActiveID { get; }
    bool IsRecording { get; }
    bool IsStarting { get; }
    bool IsStopping { get; }
    bool PresentationMode { get; set; }
    double MeLevel { get; }
    double OthersLevel { get; }
    string TranscriptionState { get; }
    IReadOnlyDictionary<AudioSource, string> TranscriptionStates { get; }
    bool CanReconnectTranscription { get; }
    MeetingAudioSelection? RecordingAudioSelection { get; }
    MicrophoneStatus? MicrophoneStatus { get; }
    AudioMetrics? RecordingMetrics { get; }
    string TeamsSpeakerStatus { get; }
    /// <summary>Why the last recording ended; null before the first stop and while a recording starts or runs.</summary>
    RecordingStopReason? LastStopReason { get; }

    /// <summary>Keeps the duration of the running meeting current.</summary>
    void Tick();
    /// <summary>Starts a recording of <paramref name="meeting"/>; the library's own checks (busy, keys, saved) have passed.</summary>
    Task StartAsync(Meeting meeting, AppSettings settings);
    /// <summary>Applies one inbox drain to the running meeting.</summary>
    void Drain();
    /// <summary>Stops within <paramref name="timeout"/> (default 60 s); a second stop waits for the running one and returns false.</summary>
    Task<bool> StopAsync(RecordingStopReason reason, TimeSpan? timeout);
    void ReconnectTranscription(string deepgramKey);
    /// <summary>Switches the running engine's microphone; true when nothing records. False (with the banner) when the switch failed.</summary>
    Task<bool> SwitchMicrophoneAsync(string microphoneUID);
    /// <summary>Cancels the Teams watches (quit, dispose).</summary>
    void StopWatches();
    /// <summary>
    /// Quit: abandons a start, cancels a running stop, or stops a running recording (<see cref="RecordingStopReason.Quit"/>),
    /// each within <paramref name="budget"/>. False when a start or a stop did not end in time.
    /// </summary>
    Task<bool> CloseAsync(TimeSpan budget);
}

/// <summary>
/// What <see cref="RecordingCoordinator"/> needs from <see cref="MeetingLibraryService"/> beyond <see cref="IMeetingHost"/>:
/// keys, devices, the library and its writer, the banners and the post-stop hand-off. UI thread only.
/// </summary>
internal interface IRecordingHost : IMeetingHost
{
    /// <summary>The OpenAI key (enhancement and OpenAI transcription).</summary>
    string ApiKey { get; }
    IAudioDeviceCatalog Devices { get; }
    MeetingLibrary Library { get; }
    MeetingWriter Writer { get; }
    string Status { get; set; }
    string Error { get; set; }
    void MarkDirty(Meeting meeting);
    /// <summary>Busy, IsRecording, IsStarting, IsStopping and ActiveID changed: notify them and raise Updated.</summary>
    void Notify();
    /// <summary>The meeting or the recording changed: raise Updated only.</summary>
    void RaiseUpdated();
    /// <summary>A stop finished: the post-recording steps run (deferred while quitting).</summary>
    void RecordingStopped(Guid meetingID, bool complete);
}
