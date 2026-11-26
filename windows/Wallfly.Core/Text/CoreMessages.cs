using Wallfly.Audio;

namespace Wallfly.Core.Text;

/// <summary>
/// User-visible wording produced inside the core. English, per the UI language decision.
/// It cannot live in <c>Wallfly.App\Strings.resx</c> because the core must not depend on the UI
/// assembly; the app shows these strings unchanged.
/// </summary>
public static class CoreMessages
{
    // Library and settings
    public const string LoadFailedSuffix = " could not be loaded; the file is kept unchanged.";
    public const string SettingsUnreadable = "Settings could not be read. The file is kept; defaults are active.";
    public const string FoldersUnreadable = "The folder catalog could not be read. The file is kept; folder changes are disabled.";
    public const string CredentialUnreadable = "The stored API key could not be read. Please set it again in Settings.";
    public const string SaveFailed = "Saving failed. Keep the window open; check write permissions and free disk space.";
    public const string UnsavedChanges = "Existing changes could not be saved.";
    public const string FolderSaveFailed = "The folder change could not be saved. The previous assignment is kept.";
    public const string TaskSaveFailed = "The task could not be saved; the checkbox stays unchanged.";
    public const string DeleteFailed = "The meeting could not be deleted completely. Please check the local files.";
    public const string DuplicateFolderId = "The folder file contains duplicate folder IDs.";
    public const string EmptyJson = "Empty JSON file.";
    public const string AudioOutsideArchive = "Audio is outside the meeting archive and was not deleted.";
    public const string MissingMeetingId = "Missing meeting ID.";
    public const string InvalidMeetingData = "Invalid or duplicate meeting ID/data.";
    public const string EmptyMeeting = "Empty meeting.";

    // Lifecycle
    public const string Ready = "Ready for good notes";
    public const string FinishCurrentMeeting = "Finish the current meeting first.";
    public const string SettingsWhileBusy = "Change settings once the recording has finished.";
    public const string EmptyModelNames = "Model names must not be empty.";
    public const string MissingRecordingKey = "No OpenAI key. The note stays usable; add a key in Settings for recording and transcription.";
    public const string PreparingAudio = "Preparing microphone and meeting audio …";
    public const string RecordingStatePrefix = "Recording · ";
    public const string RecordingStartFailed = "Recording could not start. Check the microphone and output device and Windows Settings → Privacy → Microphone → Desktop apps. The note stays saved.";
    public const string RecordingEngineUnavailable = "Recording is unavailable: WallflyAudio.dll missing or incompatible. Notes, library, search and export keep working.";
    public const string NoteSavedAudioNeedsAttention = "Note saved · recording needs attention";
    public const string Stopping = "Finishing the recording and processing the remaining audio …";
    public const string MeetingSaved = "Meeting saved";
    public const string MeetingSavedIncomplete = "Meeting saved · processing incomplete";
    public const string ResumedWithEarlierGap = "This session was processed; the meeting still contains an earlier incomplete recording. Manual enhancement uses the text that was received.";

    // Meeting audio source (spec 3.2)
    public const string ChoosingMeetingAudio = "Choosing meeting audio automatically …";
    public const string PreparingLocalWhisper = "Preparing local Whisper Turbo …";
    public const string MeetingAudioPrefix = "Meeting audio: ";
    public const string MeetingAudioWholeSystem = "whole system";
    public const string MeetingAudioTeams = "Microsoft Teams";
    /// <summary>Device loopback of an output endpoint (the automatic source).</summary>
    public const string MeetingAudioSystemOutput = "system output";
    /// <summary>The Teams view stayed unreadable at the start: the audio is recorded, live Teams names wait for a readable call.</summary>
    public const string TeamsMeetingNotDetectable = "Teams meeting cannot be reliably detected · No Teams speaker names yet";
    /// <summary>The guard moved the meeting audio to device loopback (or another output endpoint); part of the source line.</summary>
    public const string MeetingAudioSwitchedAutomatically = "switched automatically";
    /// <summary>The transient notice of the guard's switch to device loopback (no recording gap is recorded for it).</summary>
    public const string MeetingAudioSwitchedToOutput = "Meeting audio switched to the system output.";
    public static string MeetingAudioSwitchedToOutputDevice(string? device) =>
        string.IsNullOrWhiteSpace(device) ? MeetingAudioSwitchedToOutput : $"Meeting audio switched to the system output · {device.Trim()}";
    public static string MeetingAppNotOpen(string name) =>
        $"“{name}” is selected as meeting audio, but the app is not open. Open it and start recording again. To use a different audio source, change Settings → Recording → Meeting audio.";
    public const string MeetingEndedStopped = "Meeting ended · Recording stopped automatically";
    public const string MicrophoneSwitchFailed = "The microphone could not be switched. The recording continues with the previous microphone.";
    /// <summary>Name of the system default capture endpoint when the device list cannot name it.</summary>
    public const string SystemDefaultMicrophone = "System default microphone";
    /// <summary>Name of the configured microphone when the device list no longer contains it.</summary>
    public const string SelectedMicrophone = "Selected microphone";
    public const string MicrophoneUnavailable = "Microphone unavailable";
    /// <summary>Spec 3.1 "Name · Ersatz (Grund)" (macOS <c>actualMicrophoneName</c>): the system default replaced the configured microphone.</summary>
    public static string MicrophoneFallback(string name, string? reason) =>
        $"{name} · Fallback ({(string.IsNullOrWhiteSpace(reason) ? MicrophoneUnavailable : reason)})";
    public const string SettingsSaveFailed = "Settings could not be saved. Check write permissions and free disk space.";

    // Live audio diagnostics ("We're not receiving audio from …")
    public const string OtherApps = "other apps";
    /// <summary>The silent-meeting-audio warning; <paramref name="source"/> is <see cref="MeetingAudioTeams"/> or <see cref="OtherApps"/>.</summary>
    public static string NotReceivingAudio(string source) => $"We're not receiving audio from {source}.";
    public static string AudioChannelName(AudioSource source) => source switch
    {
        AudioSource.Me => "Microphone",
        AudioSource.Others => "Meeting audio",
        AudioSource.Reference => "Output device",
        _ => "Audio"
    };
    /// <summary>The device of an output event (device loopback, keep-alive, echo path): "Output device changed · Headset".</summary>
    public const string OutputDevice = "Output device";
    /// <summary>"Output device changed · Speakers", "Microphone changed · Headset"; <paramref name="device"/> is <see cref="OutputDevice"/> or <see cref="AudioChannelName"/>.</summary>
    public static string AudioDeviceChanged(string device, string? name) => device + " changed" + Detail(name);
    /// <summary>"Output device disconnected · Headset", "Microphone disconnected · USB headset".</summary>
    public static string AudioDeviceDisconnected(string device, string? name) => device + " disconnected" + Detail(name);
    /// <summary>"Microphone restored · USB headset": the same device is back after it was disconnected.</summary>
    public static string AudioDeviceRestored(string device, string? name) => device + " restored" + Detail(name);
    public static string AudioDeviceLoopbackFallback(string? detail) => "Meeting audio: recording the output device instead" + Detail(detail);
    /// <summary>The silent keep-alive stream could not be started three times in a row (WF_EVENT_WARNING "…|gave up"): the start of new audio may be cut.</summary>
    public static string KeepAliveGaveUp(string? name) => "Output device could not be kept awake" + Detail(name);
    private static string Detail(string? detail) => string.IsNullOrWhiteSpace(detail) ? "" : " · " + detail.Trim();

    // Speakers (spec 5)
    public const string SpeakerEditNotSaved = "Speaker corrections could not be saved: ";
    public const string SpeakerMatchingNotSaved = "Speaker matching could not be saved: ";

    // Audio retention (spec 8)
    public static string AudioDeletionFailed(string reason) => $"Audio could not be deleted automatically: {reason}. Try again under Storage.";

    // Library location (spec 8)
    public const string LibraryMoved = "Meeting library moved";
    public const string LibraryMoveWhileBusy = "Please wait until the recording, AI processing and speaker matching have finished.";
    public const string LibraryMoveDestinationExists = "Choose a different folder without an existing Wallfly library. Existing files are not overwritten.";
    public const string LibraryMoveVerifyFailed = "The copied library could not be verified.";
    public const string WaitForLibraryMove = "Please wait until the meeting library has been moved.";
    public static string LibraryNotMoved(string reason) => $"The library was not moved: {reason}";
    public static string LibraryOriginalsNotDeleted(string path, string reason) =>
        $"The new library is active. Some old copies could not be deleted: {path}. {reason}";

    // Transcript mirror (spec 8)
    public static string TranscriptMirrorFailed(string reason) => $"The meeting is saved internally. Mirroring the transcript failed: {reason}";
    public const string TranscriptMirrorFolderMissing = "The transcript folder is not available.";
    public const string TranscriptMirrorForeignFile = "The destination contains a different file with the same name. It was not changed.";

    // Stop sequence (DrainResult stage wording)
    public const string StageStopAudio = "stopping audio";
    public const string StageStopSpeakerDetection = "stopping speaker detection";
    public const string StageFinishTranscription = "finishing transcription requests";
    public const string StageReleaseAudio = "releasing audio resources";
    public const string DrainIncomplete = "Recording incomplete. The received text and your original notes are saved; manual enhancement works with the existing transcript.";
    public const string DrainTimedOut = "timed out";
    public const string DrainCancelled = "cancelled";
    public const string DrainRecordingError = "recording error";
    public static string DrainStageFailure(string stage, string cause) =>
        $"Processing incomplete ({stage}: {cause}). The received text and your notes are saved; manual enhancement works with the existing transcript.";

    // Enhancement
    public const string NoTranscriptText = "There is no transcript text yet. Your original notes stay saved.";
    public const string EnhancementKeyMissing = "Add an OpenAI key in Settings.";
    public const string LookingForTasks = "Looking for agreed tasks …";
    public const string TaskExtractionFailed = "Task extraction failed. Existing tasks are kept; retry it under To-dos.";
    public const string SuggestingTitle = "Suggesting a meeting title …";
    public const string TitleFailed = "The enhanced notes were saved; the automatic title could not be created.";
    public const string EnhancementDone = "Notes enhanced · original notes kept";
    public const string EnhancementCancelled = "AI processing ended · saved data is kept";
    public const string EnhancementFailed = "AI processing failed. Check the network and the key. Your original notes are kept.";
    public const string EnhancementNeedsAttention = "Meeting saved · AI processing needs attention";

    // Meeting chat (Windows addition)
    public const string ChatMeetingMissing = "The meeting no longer exists.";
    public const string ChatWhileRecording = "Questions about the meeting are possible once the recording has ended.";
    public const string ChatAlreadyAsking = "Wait for the answer to the previous question.";
}
