using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

[JsonConverter(typeof(RawValueEnumConverter<SpeakerTimelineKind>))]
public enum SpeakerTimelineKind
{
    [EnumMember(Value = "speaker")] Speaker,
    [EnumMember(Value = "gap")] Gap,
    [EnumMember(Value = "barrier")] Barrier,
    [EnumMember(Value = "context")] Context
}

/// <summary>
/// One interval of the persisted Teams evidence timeline, in meeting offsets. <c>speaker</c> intervals
/// carry a participant, <c>barrier</c> the failure reason, <c>gap</c> is a healthy stretch without
/// owner, and <c>context</c> marks a Teams process boundary (<c>start == end</c>).
/// </summary>
public sealed class SpeakerTimelineEntry
{
    public SpeakerTimelineKind Kind { get; set; }
    public double Start { get; set; }
    public double End { get; set; }
    public Guid? ParticipantID { get; set; }
    public string? Barrier { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
