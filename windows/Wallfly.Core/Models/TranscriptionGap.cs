using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>Audio that was captured but could not be recognized; shown in the transcript and the export.</summary>
public sealed class TranscriptionGap
{
    public double Start { get; set; }
    public double Duration { get; set; }
    public Speaker Speaker { get; set; }
    public string Reason { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
