using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

/// <summary>How an acoustic cluster was bound to a participant.</summary>
[JsonConverter(typeof(RawValueEnumConverter<AcousticBindingSource>))]
public enum AcousticBindingSource
{
    /// <summary>Bound by the Teams UI votes of the cluster's words.</summary>
    [EnumMember(Value = "vote")] Vote,
    /// <summary>Bound by the user renaming a "Speaker k".</summary>
    [EnumMember(Value = "manual")] Manual
}

/// <summary>One cluster of the offline acoustic diarization and its binding to a participant.</summary>
public sealed class AcousticSpeaker
{
    /// <summary>Cluster index, stable within one <see cref="DiarizationState"/> run.</summary>
    public int Id { get; set; }
    public double SpeechSeconds { get; set; }
    public int WordCount { get; set; }
    public Guid? ParticipantID { get; set; }
    public AcousticBindingSource? BindingSource { get; set; }
    /// <summary>Too little or too fragmented speech to show as its own speaker.</summary>
    public bool Suppressed { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
