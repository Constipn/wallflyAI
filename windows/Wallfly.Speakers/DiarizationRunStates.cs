using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// The pure state transitions of an acoustic run as the macOS <c>MeetingStore</c> records them in
/// <c>Meeting.Diarization</c> (rediarize / runDiarization / finishDiarization). Only a completed run changes labels;
/// every other outcome keeps the earlier segments and labels. Windows runs one pass per channel (Others as on macOS, and
/// the microphone); the top-level fields describe the Others pass, <see cref="DiarizationState.Channels"/> each channel.
/// </summary>
public static class DiarizationRunStates
{
    public const string NoWordTimingsMessage = "The transcript of “Others” has no word timings.";
    public const string NoAudioMessage = "No audio recording of “Others” available (keeping audio files is off or the files are missing).";
    /// <summary>The Others archives hold digital silence (e.g. a capture that delivered nothing): nothing to diarize.</summary>
    public const string OthersSilentMessage = "Others archive is silent.";
    public const string MeNoWordTimingsMessage = "The transcript of “Me” has no word timings.";
    public const string MeNoAudioMessage = "No audio recording of “Me” available (keeping audio files is off or the files are missing).";
    public const string MeSilentMessage = "Microphone archive is silent.";
    /// <summary>The microphone pass is switched off: the microphone track is the user ("Me") and is never split.</summary>
    public const string MicrophonePassOffMessage = "Speaker detection on the microphone is off.";
    /// <summary>A meeting from the Mac library whose remote channel exists only as <c>-others.caf</c>.</summary>
    public const string MacArchiveMessage = "Mac audio archive (.caf) not supported on Windows.";
    public const string CancelledMessage = "Cancelled.";
    public const string InterruptedMessage = "Interrupted because the app was quit.";
    public const string ResumedMessage = "Recording was resumed; matching will follow after it stops.";
    public const string RecordingsChangedMessage = "The recordings changed during matching. Please choose “Re-detect speakers”.";
    /// <summary>A cloud pass failed for a transient reason (network, timeout, server) and runs again automatically.</summary>
    public const string WaitingForNetworkMessage = "Waiting for the network: speaker separation in the cloud will try again automatically.";
    /// <summary>A run held back while a recording is active (both engines): it runs after the recording, also after a restart.</summary>
    public const string WaitingForRecordingMessage = "Waiting for the recording to end: speaker separation runs afterwards.";
    /// <summary><see cref="DiarizationChannelState.WaitingFor"/> of a transient cloud failure (also when absent).</summary>
    public const string WaitingForNetwork = "network";
    /// <summary><see cref="DiarizationChannelState.WaitingFor"/> of a run held back by a recording.</summary>
    public const string WaitingForRecording = "recording";
    /// <summary>A cloud pass the app quit interrupted: it is queued again at the next launch.</summary>
    public const string InterruptedRequeuedMessage = "Interrupted because the app was quit; speaker separation in the cloud will run again.";
    /// <summary>The setting chooses Deepgram, but no key is set: the pass cannot run in the cloud (and never runs locally instead).</summary>
    public const string CloudKeyMissingMessage =
        "Speaker separation is set to Deepgram (cloud), but no Deepgram API key is set. Add the key in Settings or choose “On this device”.";

    /// <summary>The automatic retries of a cloud pass are used up.</summary>
    public static string CloudRetriesExhaustedMessage(int attempts) =>
        $"Speaker separation in the cloud failed {attempts} times (network or server). Choose “Re-detect speakers” to try again.";

    /// <summary>The per-channel record of a cloud pass that waits for its next automatic attempt; earlier segments stay.</summary>
    public static DiarizationChannelState Waiting(string message, DateTimeOffset now, DateTimeOffset nextAttempt, int attempts, string engine,
        DiarizationChannelState? previous) => new()
    {
        Status = DiarizationStatus.Waiting, Message = message, FinishedAt = now, Engine = engine, Attempts = attempts, NextAttemptAt = nextAttempt,
        WaitingFor = WaitingForNetwork, Segments = previous?.Segments
    };

    /// <summary>
    /// The per-channel record of a run held back while a recording is active (<see cref="WaitingForRecording"/>): it runs after
    /// the recording, and at the next launch when the app is quit before. <paramref name="attempts"/>: the cloud attempts
    /// already made for the request (the held-back one does not count); earlier segments stay.
    /// </summary>
    public static DiarizationChannelState WaitingForRecordingState(DateTimeOffset now, int? attempts, string engine, bool onDevice,
        DiarizationChannelState? previous) => new()
    {
        Status = DiarizationStatus.Waiting, Message = WaitingForRecordingMessage, FinishedAt = now, Engine = engine, Attempts = attempts is > 0 ? attempts : null,
        NextAttemptAt = now, WaitingFor = WaitingForRecording, OnDevice = onDevice ? true : null, Segments = previous?.Segments
    };

    /// <summary>The Others pass waits: for an automatic cloud retry, or for the end of a recording.</summary>
    public static bool IsWaiting(Meeting meeting) => meeting.Diarization?.Channels?.Others is { Status: DiarizationStatus.Waiting };

    /// <summary>The Others pass waits for the end of a recording (<see cref="WaitingForRecordingState"/>).</summary>
    public static bool IsWaitingForRecording(Meeting meeting) =>
        meeting.Diarization?.Channels?.Others is { Status: DiarizationStatus.Waiting, WaitingFor: WaitingForRecording };

    /// <summary>The Others pass waits for the network (a transient cloud failure).</summary>
    public static bool IsWaitingForNetwork(Meeting meeting) => IsWaiting(meeting) && !IsWaitingForRecording(meeting);

    /// <summary>Why a channel's pass is skipped before it starts (status Skipped), or null when it can run.</summary>
    public static string? SkipReason(Meeting meeting, IReadOnlyCollection<string> existingFiles, Speaker channel = Speaker.Others)
    {
        var me = channel == Speaker.Me;
        if (!meeting.HasTimedWords(channel)) return me ? MeNoWordTimingsMessage : NoWordTimingsMessage;
        // Mac recordings (.caf) are kept and listed but never decoded here: a clear skip instead of a failed run.
        if (existingFiles.Count == 0)
            return DiarizationTimeline.MacFiles(meeting, channel).Count > 0 ? MacArchiveMessage : me ? MeNoAudioMessage : NoAudioMessage;
        return null;
    }

    /// <summary>The message of a channel whose archives are silent.</summary>
    public static string SilentMessage(Speaker channel) => channel == Speaker.Me ? MeSilentMessage : OthersSilentMessage;

    /// <summary>The state while a run is going; earlier segments stay until the run completes.</summary>
    public static DiarizationState Running(IDiarizer diarizer, DiarizationState? previous, DateTimeOffset now, DiarizationChannels? channels = null) => new()
    {
        Engine = diarizer.Engine, ModelVersion = diarizer.ModelVersion, Threshold = diarizer.Threshold,
        Status = DiarizationStatus.Running, StartedAt = now, Segments = previous?.Segments, Channels = channels ?? previous?.Channels
    };

    /// <summary>A run that ended without new labels (skipped or failed); earlier segments and labels stay as they are.</summary>
    public static DiarizationState Finished(IDiarizer diarizer, DiarizationState? previous, DiarizationStatus status, string message, DateTimeOffset now,
        DiarizationChannels? channels = null) => new()
    {
        Engine = diarizer.Engine, ModelVersion = diarizer.ModelVersion, Threshold = diarizer.Threshold, Status = status,
        StartedAt = previous?.Status == DiarizationStatus.Running ? previous.StartedAt : null, FinishedAt = now,
        Message = message, Segments = previous?.Segments, Channels = channels ?? FinishedChannels(previous?.Channels, status, message, now)
    };

    /// <summary>The per-channel record of an ended pass; a pass that did not complete keeps the channel's earlier segments.</summary>
    public static DiarizationChannelState Channel(DiarizationStatus status, string? message, DateTimeOffset now, DiarizationChannelState? previous) => new()
    {
        Status = status, Message = message, FinishedAt = now, Segments = previous?.Segments
    };

    /// <summary>
    /// Status and message for a run that threw: models unavailable and audio over 4 h skip, cancellation and every other
    /// error fail (the transcript stays unchanged in every case).
    /// </summary>
    public static (DiarizationStatus Status, string Message) ForFailure(Exception error) => error switch
    {
        DiarizationException { Kind: DiarizationErrorKind.ModelsUnavailable } models => (DiarizationStatus.Skipped, models.Message),
        DiarizationException { Kind: DiarizationErrorKind.AudioTooLong } tooLong => (DiarizationStatus.Skipped, tooLong.Message),
        OperationCanceledException => (DiarizationStatus.Failed, CancelledMessage),
        _ => (DiarizationStatus.Failed, error.Message)
    };

    /// <summary>
    /// A completed Others pass: maps the archive segments to meeting offsets, fuses them with the Teams labels and records
    /// the run (the per-channel records of the previous state are kept). Changes the meeting in place and returns the stored segments.
    /// </summary>
    public static List<AcousticSegment> ApplyResult(this Meeting meeting, DiarizationResult result, DateTimeOffset? startedAt, DateTimeOffset now,
        FusionPolicy? policy = null)
    {
        var segments = DiarizationTimeline.ToMeetingSegments(result, meeting);
        // The new run numbers its voices on its own: manual bindings are kept by their words and by time overlap, never by id.
        meeting.PreserveManualBindings(meeting.Diarization?.Segments, segments);
        meeting.ApplyAcousticFusion(segments, policy);
        meeting.Diarization = new DiarizationState
        {
            Engine = result.Engine, ModelVersion = result.ModelVersion, Threshold = result.Threshold, Status = DiarizationStatus.Completed,
            StartedAt = startedAt, FinishedAt = now, Segments = segments, Channels = meeting.Diarization?.Channels
        };
        return segments;
    }

    /// <summary>
    /// A completed microphone pass: maps the archive segments to meeting offsets, labels the Me words
    /// (<see cref="AcousticSpeakerLabels.ApplyMicrophoneLabels"/>) and records the pass in <c>Channels.Me</c>. The meeting
    /// must already have a <see cref="DiarizationState"/> (the run's top-level record). Changes the meeting in place.
    /// </summary>
    public static AcousticSpeakerLabels.MicrophoneLabelling ApplyMicrophoneResult(this Meeting meeting, DiarizationResult result, DateTimeOffset now,
        FusionPolicy? policy = null)
    {
        var segments = DiarizationTimeline.ToMeetingSegments(result, meeting, Speaker.Me);
        var labelling = meeting.ApplyMicrophoneLabels(segments, policy);
        var state = meeting.Diarization ??= new DiarizationState { Engine = result.Engine, ModelVersion = result.ModelVersion, Threshold = result.Threshold };
        var channels = state.Channels ??= new DiarizationChannels();
        channels.Me = new DiarizationChannelState
        {
            Status = DiarizationStatus.Completed, FinishedAt = now, Threshold = result.Threshold, Clusters = result.Merge?.Clusters ?? result.SpeakerCount,
            Merged = result.Merge?.Merged, Absorbed = result.Merge?.Absorbed, Retained = labelling.Retained,
            SpeechSeconds = SwiftMath.RoundedMillis(labelling.SpeechSeconds), ElapsedSeconds = SwiftMath.RoundedMillis(result.Elapsed.TotalSeconds),
            Segments = segments
        };
        return labelling;
    }

    /// <summary>At launch, a run still marked Running was interrupted by quitting: shown as failed, saved once. Returns true if changed.</summary>
    public static bool RepairInterrupted(Meeting meeting)
    {
        if (meeting.Diarization is not { Status: DiarizationStatus.Running } state) return false;
        state.Status = DiarizationStatus.Failed;
        state.Message = InterruptedMessage;
        foreach (var channel in new[] { state.Channels?.Me, state.Channels?.Others })
        {
            if (channel is not { Status: DiarizationStatus.Running }) continue;
            channel.Status = DiarizationStatus.Failed;
            channel.Message = InterruptedMessage;
        }
        return true;
    }

    /// <summary>What <see cref="RepairInterrupted(Meeting, string, int, DateTimeOffset)"/> did.</summary>
    public enum InterruptedRepair
    {
        /// <summary>Nothing was running.</summary>
        None,
        /// <summary>Shown as failed (the local engine, or a cloud pass whose attempts are used up).</summary>
        Failed,
        /// <summary>A cloud Others pass waits for its next attempt (<see cref="DiarizationStatus.Waiting"/>, due at once).</summary>
        Requeued
    }

    /// <summary>
    /// At launch: a run still marked Running was interrupted by quitting. An interrupted Others pass of
    /// <paramref name="cloudEngine"/> with fewer than <paramref name="maxAttempts"/> attempts waits for a new attempt (the
    /// caller queues it); every other interrupted run is shown as failed, as <see cref="RepairInterrupted(Meeting)"/> does.
    /// </summary>
    public static InterruptedRepair RepairInterrupted(Meeting meeting, string cloudEngine, int maxAttempts, DateTimeOffset now)
    {
        if (meeting.Diarization is not { Status: DiarizationStatus.Running } state) return InterruptedRepair.None;
        var others = state.Channels?.Others;
        if (others is { Status: DiarizationStatus.Running } && others.Engine == cloudEngine && (others.Attempts ?? 1) < maxAttempts)
        {
            RepairInterrupted(meeting);
            state.Message = InterruptedRequeuedMessage;
            state.Channels!.Others = Waiting(InterruptedRequeuedMessage, now, now, others.Attempts ?? 1, cloudEngine, others);
            return InterruptedRepair.Requeued;
        }
        RepairInterrupted(meeting);
        return InterruptedRepair.Failed;
    }

    private static DiarizationChannels? FinishedChannels(DiarizationChannels? previous, DiarizationStatus status, string message, DateTimeOffset now)
    {
        if (previous is null) return null;
        DiarizationChannelState? End(DiarizationChannelState? channel) =>
            channel is { Status: DiarizationStatus.Running } ? Channel(status, message, now, channel) : channel;
        return new DiarizationChannels { Me = End(previous.Me), Others = End(previous.Others), AdditionalData = previous.AdditionalData };
    }
}
