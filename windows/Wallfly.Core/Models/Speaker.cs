using System.Runtime.Serialization;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core;

/// <summary>Channel label, never an identity. Raw values match the macOS <c>Speaker</c> enum.</summary>
[JsonConverter(typeof(RawValueEnumConverter<Speaker>))]
public enum Speaker
{
    [EnumMember(Value = "Me")] Me,
    [EnumMember(Value = "Others")] Others
}

/// <summary>Capture channel of a <see cref="RecordingGap"/>; raw values match macOS <c>CaptureSource</c>.</summary>
[JsonConverter(typeof(RawValueEnumConverter<CaptureSource>))]
public enum CaptureSource
{
    [EnumMember(Value = "microphone")] Microphone,
    [EnumMember(Value = "reference")] Reference,
    [EnumMember(Value = "meeting")] Meeting
}
