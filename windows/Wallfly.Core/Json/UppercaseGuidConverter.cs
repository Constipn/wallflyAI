using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core.Json;

/// <summary>UUIDs are uppercase "D" strings, as written by Foundation's <c>UUID.uuidString</c>.</summary>
public sealed class UppercaseGuidConverter : JsonConverter<Guid>
{
    public override Guid Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.String && Guid.TryParse(reader.GetString(), out var value)
            ? value
            : throw new JsonException("Expected a UUID string.");

    public override void Write(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.ToString("D").ToUpperInvariant());

    public override Guid ReadAsPropertyName(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        Guid.Parse(reader.GetString()!);

    public override void WriteAsPropertyName(Utf8JsonWriter writer, Guid value, JsonSerializerOptions options) =>
        writer.WritePropertyName(value.ToString("D").ToUpperInvariant());
}
