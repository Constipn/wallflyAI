using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>
/// Maps a sample position in one saved per-channel archive file to a meeting offset. Capture gaps are
/// not written as silence, so a new anchor starts whenever the written audio jumps in time; between
/// anchors <c>offset(frame) = anchor.offset + (frame - anchor.frame) / 16000</c>.
/// </summary>
public sealed class AudioAnchor
{
    /// <summary>File name of the archive file (no directory).</summary>
    public string File { get; set; } = "";
    /// <summary>Sample index in that file at which the anchor applies.</summary>
    public long Frame { get; set; }
    /// <summary>Meeting offset in seconds for that frame.</summary>
    public double Offset { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
