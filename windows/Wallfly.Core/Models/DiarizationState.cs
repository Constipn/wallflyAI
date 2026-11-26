using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

[JsonConverter(typeof(RawValueEnumConverter<DiarizationStatus>))]
public enum DiarizationStatus
{
    [EnumMember(Value = "running")] Running,
    [EnumMember(Value = "completed")] Completed,
    [EnumMember(Value = "failed")] Failed,
    [EnumMember(Value = "skipped")] Skipped,
    /// <summary>
    /// Windows only, and only in a channel's state (<see cref="DiarizationChannels"/>, a key the Mac ignores): a cloud pass
    /// failed for a transient reason and runs again later (<see cref="DiarizationChannelState.NextAttemptAt"/>). The top level
    /// of such a run is <see cref="Failed"/>, so a Mac reading the library never meets this value.
    /// </summary>
    [EnumMember(Value = "waiting")] Waiting
}

/// <summary>One interval of a diarization run; <c>speaker</c> is the 0-based cluster index.</summary>
public sealed class AcousticSegment
{
    public int Speaker { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>Metadata of the last acoustic diarization run of a meeting; segments are meeting offsets.</summary>
public sealed class DiarizationState
{
    public string Engine { get; set; } = "";
    public string? ModelVersion { get; set; }
    public double? Threshold { get; set; }
    public DiarizationStatus Status { get; set; }
    public DateTimeOffset? StartedAt { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    public string? Message { get; set; }
    public List<AcousticSegment>? Segments { get; set; }
    /// <summary>
    /// Windows only (absent in Mac files): the outcome of each channel's pass. The fields above keep describing the
    /// Others pass (while a run is going, <see cref="Status"/> is Running for the whole run).
    /// </summary>
    public DiarizationChannels? Channels { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>Per-channel outcomes of a diarization run (<c>channels: { me, others }</c>).</summary>
public sealed class DiarizationChannels
{
    public DiarizationChannelState? Me { get; set; }
    public DiarizationChannelState? Others { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>
/// One channel's pass. <see cref="Segments"/> (meeting offsets, 0-based cluster per channel) are stored for the Me channel
/// only; the Others segments stay in <see cref="DiarizationState.Segments"/>.
/// </summary>
public sealed class DiarizationChannelState
{
    public DiarizationStatus Status { get; set; }
    public string? Message { get; set; }
    public DateTimeOffset? FinishedAt { get; set; }
    /// <summary>Clustering threshold of this channel's pass (the microphone pass uses its own).</summary>
    public double? Threshold { get; set; }
    /// <summary>Clusters the diarizer found (before a voice merge).</summary>
    public int? Clusters { get; set; }
    /// <summary>Clusters the voice merge joined to a voice with a similar fingerprint (null when no merge ran).</summary>
    public int? Merged { get; set; }
    /// <summary>Clusters below the minimum voice size that the voice merge joined to the most similar voice (null when no merge ran).</summary>
    public int? Absorbed { get; set; }
    /// <summary>
    /// Others pass: voices restored because the merge had joined clusters that Teams names as different people (null when no
    /// merge ran or on the microphone channel).
    /// </summary>
    public int? Separated { get; set; }
    /// <summary>Identities left after the 10-s and phantom filters.</summary>
    public int? Retained { get; set; }
    public double? SpeechSeconds { get; set; }
    public double? ElapsedSeconds { get; set; }
    /// <summary>
    /// The engine of this channel's pass: the diarizer's engine id (e.g. <c>sherpa-onnx-pyannote-3.0</c>,
    /// <c>deepgram-prerecorded</c>) or <c>teams</c> when the Teams names alone were enough (null in files before 25.11.2026).
    /// </summary>
    public string? Engine { get; set; }
    /// <summary>Cloud pass: attempts made for the current request (the first run counts; reset by a run the user starts).</summary>
    public int? Attempts { get; set; }
    /// <summary><see cref="DiarizationStatus.Waiting"/>: when the next automatic attempt is due.</summary>
    public DateTimeOffset? NextAttemptAt { get; set; }
    /// <summary>
    /// <see cref="DiarizationStatus.Waiting"/>: what the pass waits for, <c>network</c> (a transient cloud failure; also when
    /// absent, files before 26.11.2026) or <c>recording</c> (held back while a recording is active, cloud or local).
    /// </summary>
    public string? WaitingFor { get; set; }
    /// <summary><see cref="DiarizationStatus.Waiting"/>: the held-back run was an explicit "Re-detect speakers on this device".</summary>
    public bool? OnDevice { get; set; }
    public List<AcousticSegment>? Segments { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
