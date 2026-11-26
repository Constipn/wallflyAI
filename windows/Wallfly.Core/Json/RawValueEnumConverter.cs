using System.Reflection;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core.Json;

/// <summary>
/// Writes the <see cref="EnumMemberAttribute"/> value of an enum member (the Swift <c>rawValue</c>),
/// falling back to the member name. An unknown value is a decoding error, never a silent default, so
/// a library written by a newer version is isolated instead of being rewritten with wrong data.
/// </summary>
public sealed class RawValueEnumConverter<T> : JsonConverter<T> where T : struct, Enum
{
    private static readonly Dictionary<string, T> FromRaw = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<T, string> ToRaw = [];

    static RawValueEnumConverter()
    {
        foreach (var value in Enum.GetValues<T>())
        {
            var name = Enum.GetName(value)!;
            var raw = typeof(T).GetField(name, BindingFlags.Public | BindingFlags.Static)
                ?.GetCustomAttribute<EnumMemberAttribute>()?.Value ?? name;
            FromRaw[raw] = value;
            FromRaw.TryAdd(name, value);
            ToRaw[value] = raw;
        }
    }

    public static string RawValue(T value) => ToRaw[value];
    public static bool TryParse(string? raw, out T value) => FromRaw.TryGetValue(raw ?? "", out value);

    public override T Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var raw = reader.TokenType == JsonTokenType.String ? reader.GetString() : throw new JsonException($"Expected a string for {typeof(T).Name}.");
        return FromRaw.TryGetValue(raw ?? "", out var value) ? value : throw new JsonException($"Unknown {typeof(T).Name}: {raw}");
    }

    public override void Write(Utf8JsonWriter writer, T value, JsonSerializerOptions options) => writer.WriteStringValue(ToRaw[value]);
}
