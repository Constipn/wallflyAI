using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>
/// One recognized passage of one channel. Identity is the id plus the audio interval, never the text.
/// Unknown fields of newer or older writers round-trip through <see cref="AdditionalData"/>.
/// </summary>
public sealed class TranscriptSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Speaker Speaker { get; set; }
    /// <summary>Seconds from the start of the meeting, including earlier recording sessions.</summary>
    public double Offset { get; set; }
    public string Text { get; set; } = "";
    public double Duration { get; set; }
    public bool IsFinal { get; set; } = true;
    public Guid? SessionID { get; set; }
    public List<TranscriptWord>? Words { get; set; }
    public Guid? ParticipantID { get; set; }
    public WordAttribution? Attribution { get; set; }
    /// <summary>Streaming connection the passage arrived on (Deepgram); null for chunked providers.</summary>
    public Guid? ConnectionID { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
