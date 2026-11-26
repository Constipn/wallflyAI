using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;

namespace Wallfly.Core.Persistence;

/// <summary>
/// Where the meeting library lives. The pointer itself stays in <c>%LOCALAPPDATA%\Wallfly\library.json</c>
/// even when the library is moved to another drive, so a missing drive is reported instead of silently
/// recreating an empty library.
/// </summary>
public sealed class LibraryPointer(string? directory = null)
{
    private sealed class Document
    {
        [JsonPropertyName("path")] public string Path { get; set; } = "";
    }

    /// <summary>The fixed folder holding the pointer; never moves with the library.</summary>
    public string Directory { get; } = directory ?? MeetingLibrary.DefaultRoot;

    public string FilePath => System.IO.Path.Combine(Directory, "library.json");

    /// <summary>The configured library root, or the default when nothing was chosen.</summary>
    public string Resolve()
    {
        try
        {
            if (File.Exists(FilePath)
                && JsonSerializer.Deserialize<Document>(File.ReadAllText(FilePath), WallflyJson.Options) is { Path.Length: > 0 } document)
                return System.IO.Path.GetFullPath(document.Path);
        }
        catch (Exception error) when (error is IOException or JsonException or UnauthorizedAccessException or ArgumentException) { }
        return Directory;
    }

    /// <summary>Commits the new location before any original data is removed.</summary>
    public void Remember(string root) =>
        MeetingLibrary.AtomicWrite(FilePath, JsonSerializer.SerializeToUtf8Bytes(new Document { Path = System.IO.Path.GetFullPath(root) }, WallflyJson.Options));
}
