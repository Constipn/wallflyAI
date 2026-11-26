using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core.Json;

/// <summary>
/// Library root in effect while a meeting is (de)serialized. Set by <c>MeetingLibrary</c> around every
/// read and write; the writer thread serializes on the caller's thread, so an async-local value is
/// enough and no lock is needed.
/// </summary>
public static class AudioPathContext
{
    private static readonly AsyncLocal<string?> Current = new();

    public static string? Root => Current.Value;

    /// <summary>Scopes <paramref name="root"/> for the duration of the returned handle.</summary>
    public static Scope Use(string? root) => new(root);

    public readonly struct Scope : IDisposable
    {
        private readonly string? previous;
        internal Scope(string? root) { previous = Current.Value; Current.Value = root; }
        public void Dispose() => Current.Value = previous;
    }

    /// <summary>Relative to the library root when the file lives inside it, otherwise unchanged.</summary>
    public static string ToStored(string path)
    {
        var root = Root;
        if (string.IsNullOrEmpty(root) || string.IsNullOrEmpty(path) || !Path.IsPathRooted(path)) return path;
        string full, fullRoot;
        try { full = Path.GetFullPath(path); fullRoot = Path.GetFullPath(root); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
        if (!full.StartsWith(fullRoot.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return path;
        return Path.GetRelativePath(fullRoot, full);
    }

    /// <summary>Absolute path of a stored value; relative values resolve against the library root.</summary>
    public static string Resolve(string? root, string stored) =>
        string.IsNullOrEmpty(stored) || Path.IsPathRooted(stored) || string.IsNullOrEmpty(root)
            ? stored
            : Path.GetFullPath(Path.Combine(root, stored));
}

/// <summary>Writes audio paths relative to the library root; accepts absolute ones on read.</summary>
public sealed class AudioPathListConverter : JsonConverter<List<string>>
{
    public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null) return [];
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("Expected an array of audio paths.");
        var result = new List<string>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
            result.Add(reader.TokenType == JsonTokenType.String ? reader.GetString() ?? "" : throw new JsonException("Expected an audio path string."));
        return result;
    }

    public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        foreach (var path in value) writer.WriteStringValue(AudioPathContext.ToStored(path));
        writer.WriteEndArray();
    }
}

/// <summary>Same contract for the optional deletion list.</summary>
public sealed class NullableAudioPathListConverter : JsonConverter<List<string>?>
{
    private static readonly AudioPathListConverter Inner = new();

    public override List<string>? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        reader.TokenType == JsonTokenType.Null ? null : Inner.Read(ref reader, typeof(List<string>), options);

    public override void Write(Utf8JsonWriter writer, List<string>? value, JsonSerializerOptions options)
    {
        if (value == null) writer.WriteNullValue();
        else Inner.Write(writer, value, options);
    }
}
