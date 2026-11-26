using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core.Json;

/// <summary>The one serializer configuration of the whole app: the macOS meeting JSON shape.</summary>
public static class WallflyJson
{
    public static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DictionaryKeyPolicy = null,
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new AppleDateConverter(), new UppercaseGuidConverter() }
    };

    /// <summary>Deep copy through the same shape that is written to disk.</summary>
    public static T Copy<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value, Options), Options)!;
}
