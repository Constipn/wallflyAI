namespace Wallfly.Speakers;

/// <summary>Status of the Teams speaker reader (port of <c>TeamsSpeakerStatus</c>). Only Active and WaitingForSpeaker yield evidence.</summary>
public enum TeamsSpeakerStatus
{
    Automatic, Disabled, Ready, WrongAudioSource, WaitingForSpeaker, NeedsAccessibility, TeamsNotRunning,
    MeetingNotFound, Active, Unavailable, Ambiguous, SelfSpeaking, MissingWordTimes
}

public static class TeamsSpeakerStatusText
{
    /// <summary>The user-facing text of the macOS <c>displayName</c>.</summary>
    public static string DisplayName(this TeamsSpeakerStatus status) => status switch
    {
        TeamsSpeakerStatus.Automatic => "Teams audio and speaker names are chosen automatically when recording starts",
        TeamsSpeakerStatus.Disabled => "Teams speaker names are off",
        TeamsSpeakerStatus.Ready => "Ready for Teams · Start recording to detect speakers",
        TeamsSpeakerStatus.WrongAudioSource => "Choose Microsoft Teams as the audio source for speaker names",
        TeamsSpeakerStatus.WaitingForSpeaker => "Teams names found · waiting for a speaker signal",
        TeamsSpeakerStatus.NeedsAccessibility => "Accessibility access missing · Recording still works",
        TeamsSpeakerStatus.TeamsNotRunning => "Microsoft Teams is not running",
        TeamsSpeakerStatus.MeetingNotFound => "No Teams meeting detected",
        TeamsSpeakerStatus.Active => "Teams speaker detection active",
        TeamsSpeakerStatus.Unavailable => "Teams speaker signal missing · Assigned as Others",
        TeamsSpeakerStatus.Ambiguous => "Multiple or unclear speakers · Assigned as Others",
        TeamsSpeakerStatus.SelfSpeaking => "You are speaking · Assigning other speakers paused",
        TeamsSpeakerStatus.MissingWordTimes => "Teams speaker found · Word timings missing · Assigned as Others",
        _ => status.ToString()
    };
}

/// <summary>One tile of the Teams call as read from the UI. <see cref="Key"/> identifies the UI element, <see cref="StableKey"/> a person.</summary>
public sealed record TeamsObservedParticipant(string Key, string Name, bool Active, bool IsSelf = false, string? StableKey = null);

/// <summary>Why a read produced no usable speaker (port of <c>TeamsObservationFailure</c>).</summary>
public enum TeamsObservationFailure
{
    Accessibility, NotRunning, MeetingNotFound, Unreadable, Truncated, ConflictingViews, UnidentifiedSpeaker, LocalUserSpeaking
}

/// <summary>
/// One read of the Teams UI (port of <c>TeamsSpeakerObservation</c>). <see cref="Timestamp"/> is the middle of the read
/// on the same monotonic clock as the audio, in seconds.
/// </summary>
public sealed record TeamsSpeakerObservation
{
    public double Timestamp { get; init; }
    public string? SurfaceID { get; init; }
    public IReadOnlyList<TeamsObservedParticipant> Participants { get; init; } = [];
    public TeamsSpeakerStatus Status { get; init; }
    /// <summary>Process generation, independent of gallery/compact window identity.</summary>
    public string? ContextID { get; init; }
    public double ReadDuration { get; init; }
    public TeamsObservationFailure? Failure { get; init; }
}

/// <summary>Why a word or turn stays unnamed (port of <c>TeamsAttributionFailure</c>). Raw values persist in the timeline.</summary>
public enum TeamsAttributionFailure
{
    MissingWordTimes, NoObservation, Stale, Ambiguous, LocalUserSpeaking, Unavailable, ContextChanged, InsufficientCoverage
}

public static class TeamsAttributionFailureRaw
{
    /// <summary>The macOS raw value (the Swift case name), as stored in <c>speakerTimeline[].barrier</c>.</summary>
    public static string RawValue(this TeamsAttributionFailure failure) => failure switch
    {
        TeamsAttributionFailure.MissingWordTimes => "missingWordTimes",
        TeamsAttributionFailure.NoObservation => "noObservation",
        TeamsAttributionFailure.Stale => "stale",
        TeamsAttributionFailure.Ambiguous => "ambiguous",
        TeamsAttributionFailure.LocalUserSpeaking => "localUserSpeaking",
        TeamsAttributionFailure.Unavailable => "unavailable",
        TeamsAttributionFailure.ContextChanged => "contextChanged",
        TeamsAttributionFailure.InsufficientCoverage => "insufficientCoverage",
        _ => throw new ArgumentOutOfRangeException(nameof(failure))
    };

    public static bool TryParse(string? raw, out TeamsAttributionFailure failure)
    {
        foreach (var value in Enum.GetValues<TeamsAttributionFailure>())
        {
            if (string.Equals(value.RawValue(), raw, StringComparison.Ordinal)) { failure = value; return true; }
        }
        failure = default;
        return false;
    }
}

/// <summary>A participant, or the reason why there is none.</summary>
public readonly record struct TeamsWordDecision(Guid? ParticipantID, TeamsAttributionFailure? Failure = null)
{
    public static TeamsWordDecision Unknown(TeamsAttributionFailure reason) => new(null, reason);
}

/// <summary>In-memory counters only (port of <c>TeamsSpeakerDiagnostics</c>). Coverage is not an estimate of accuracy.</summary>
public sealed class TeamsSpeakerDiagnostics
{
    public int ParticipantCount { get; set; }
    public double? ObservedAt { get; set; }
    public double ReadDuration { get; set; }
    public double ObservationAge { get; set; }
    public TeamsObservationFailure? ObservationFailure { get; set; }
    public int TimedWords { get; set; }
    public int NamedWords { get; set; }
    public int UntimedChunks { get; set; }
    public bool? LatestRemoteHasWordTimes { get; set; }
    public Dictionary<TeamsAttributionFailure, int> UnknownReasons { get; } = [];
}

/// <summary>What the UI showed at one moment, after the detection lag correction.</summary>
public enum TeamsEvidenceKind { Speaker, HealthyGap, Barrier }

/// <summary>Port of <c>TeamsAttributionSession.Evidence</c>: a speaker, a healthy no-speaker stretch, or a barrier with its reason.</summary>
public readonly record struct TeamsEvidence(TeamsEvidenceKind Kind, Guid Participant = default, TeamsAttributionFailure Reason = default)
{
    public static TeamsEvidence Speaker(Guid participant) => new(TeamsEvidenceKind.Speaker, participant);
    public static TeamsEvidence HealthyGap { get; } = new(TeamsEvidenceKind.HealthyGap);
    public static TeamsEvidence Barrier(TeamsAttributionFailure reason) => new(TeamsEvidenceKind.Barrier, default, reason);
}

/// <summary>One evidence sample in meeting offsets (seconds).</summary>
public readonly record struct TeamsEvidenceSample(double Offset, TeamsEvidence Evidence);
