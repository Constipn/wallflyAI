using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>A named speaker of the Others channel. The Me channel is never named.</summary>
public sealed class MeetingParticipant
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "";
    /// <summary>Stable key of the source that produced the name (for example a Teams roster entry).</summary>
    public string? SourceKey { get; set; }
    public Guid? SessionID { get; set; }
    public int ColorIndex { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
