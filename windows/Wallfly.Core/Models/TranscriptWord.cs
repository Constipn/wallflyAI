using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

/// <summary>How a word got its participant.</summary>
[JsonConverter(typeof(RawValueEnumConverter<WordAttribution>))]
public enum WordAttribution
{
    [EnumMember(Value = "teams-ax")] TeamsAccessibility,
    [EnumMember(Value = "manual")] Manual
}

/// <summary>
/// One recognized word. Times are seconds relative to <see cref="TranscriptSegment.Offset"/>,
/// including silence inside that audio interval.
/// </summary>
public sealed class TranscriptWord
{
    public string Text { get; set; } = "";
    public double Start { get; set; }
    public double End { get; set; }
    public Guid? ParticipantID { get; set; }
    public WordAttribution? Attribution { get; set; }
    /// <summary>Raw per-word Teams UI decision, kept apart from the utterance-level participant.</summary>
    public Guid? UiParticipantID { get; set; }
    /// <summary>Cluster id of the offline acoustic diarization, null outside every diarized segment.</summary>
    public int? AcousticSpeaker { get; set; }
    /// <summary>Teams utterance label before acoustic fusion replaced <see cref="ParticipantID"/>.</summary>
    public Guid? UtteranceParticipantID { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
