using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace Wallfly.Ai;

/// <summary>
/// The prompt texts, embedded verbatim from the macOS sources as <c>Prompts\*.txt</c>. Line endings are normalised
/// to LF and the trailing newline is dropped, matching a Swift multi-line string literal.
/// </summary>
internal static partial class Prompts
{
    private static readonly ConcurrentDictionary<string, string> Cache = new();

    public static string Get(string name) => Cache.GetOrAdd(name, static name =>
    {
        using var stream = typeof(Prompts).Assembly.GetManifestResourceStream($"Wallfly.Ai.Prompts.{name}.txt")
            ?? throw new InvalidOperationException($"Missing prompt resource {name}.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd().Replace("\r\n", "\n").TrimEnd('\n');
    });

    [GeneratedRegex(@"\{\{([A-Z_]+)\}\}")] private static partial Regex Placeholder();

    /// <summary>Fills <c>{{NAME}}</c> placeholders in one pass, so inserted meeting text is never re-scanned.</summary>
    public static string Fill(string name, params (string Key, string Value)[] values)
    {
        var lookup = values.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return Placeholder().Replace(Get(name), match => lookup[match.Groups[1].Value]);
    }
}
