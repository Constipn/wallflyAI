using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Teams;

/// <summary>
/// JSON of the detection files: the macOS schema (dates as seconds since 2001-01-01 UTC, uppercase UUIDs),
/// like the rest of the Windows port (Wallfly.Core.Json). Kept local so the helper does not load Wallfly.Core.
/// </summary>
public static class DetectionJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = false,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        Converters = { new AppleDateJsonConverter(), new UppercaseGuidJsonConverter() },
    };
}

/// <summary>Seconds since 2001-01-01 UTC (Foundation's JSONEncoder default); ISO strings are accepted on read.</summary>
public sealed class AppleDateJsonConverter : JsonConverter<DateTimeOffset>
{
    public static readonly DateTimeOffset Epoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? Epoch.AddSeconds(reader.GetDouble()) : reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value - Epoch).TotalSeconds);
}

/// <summary>Uppercase "D" UUID strings, as written by Foundation's <c>UUID.uuidString</c>.</summary>
public sealed class UppercaseGuidJsonConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Expected a UUID string.");

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());
}
