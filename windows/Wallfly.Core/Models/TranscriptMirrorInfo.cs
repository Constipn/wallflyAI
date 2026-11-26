using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>
/// Optional external transcript copy. <c>bookmark</c> is macOS security-scope data; Windows keeps the
/// value untouched so a library shared with a Mac keeps working.
/// </summary>
public sealed class TranscriptMirrorInfo
{
    public string Path { get; set; } = "";
    public string? Bookmark { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>Legacy per-person summary run; kept so macOS files round-trip unchanged.</summary>
public sealed class PersonSummaryInfo
{
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
