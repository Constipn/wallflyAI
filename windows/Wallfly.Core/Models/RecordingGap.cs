using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>Audio that was never captured. Gaps are not written as silence into the archive.</summary>
public sealed class RecordingGap
{
    public double Start { get; set; }
    public double Duration { get; set; }
    public CaptureSource Source { get; set; }
    public string Reason { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
