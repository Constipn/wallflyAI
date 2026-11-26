using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core.Json;

/// <summary>
/// Apple reference dates: seconds since 2001-01-01 UTC, exactly what <c>JSONEncoder</c> writes.
/// Unix seconds = value + 978 307 200. ISO strings written by other tools are still accepted.
/// </summary>
public sealed class AppleDateConverter : JsonConverter<DateTimeOffset>
{
    public static readonly DateTimeOffset Epoch = new(2001, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Number ? Epoch.AddSeconds(reader.GetDouble()) : reader.GetDateTimeOffset();

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteNumberValue((value - Epoch).TotalSeconds);
}
