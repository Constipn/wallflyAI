using System.Text.Json;
using Wallfly.Core.Json;
using Wallfly.Core.Text;

namespace Wallfly.Core.Persistence;

/// <summary>One meeting file that could not be decoded; it is reported and never touched again.</summary>
public sealed record LoadFailure(string FileName, string Message);

/// <summary>
/// The meeting library on disk. Writes are atomic (temporary file, then <c>File.Replace</c>), a
/// corrupt meeting file is isolated instead of failing the whole load (the known macOS release
/// blocker), and deletion only ever touches <c>Audio\&lt;MeetingID&gt;\</c>.
/// </summary>
public class MeetingLibrary
{
    /// <summary>Root files that are not meetings.</summary>
    public static readonly string[] ReservedFiles = ["settings.json", "library.json", "migrated.json", "teams-selectors.json"];

    /// <summary>
    /// Entries of the data root that belong to the app, not to the library, and never move with it (spec 8: the helper's
    /// <c>Detection</c> folder; on Windows also the library pointer, the migration receipt, the window state (<c>UI</c>),
    /// models and overrides).
    /// </summary>
    public static readonly string[] StaysWithApp =
        ["Detection", "library.json", "migrated.json", "UI", "models", "whisper", "teams-selectors.json", "uiadump-salt.bin", "logs"];

    public string Root { get; }
    public List<LoadFailure> LoadFailures { get; } = [];

    public MeetingLibrary(string? root = null) => Root = Path.GetFullPath(root ?? DefaultRoot);

    /// <summary>%LOCALAPPDATA%\Wallfly — never %APPDATA%, which may be a roaming profile.</summary>
    public static string DefaultRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Wallfly");

    public void Prepare() => Directory.CreateDirectory(Root);

    /// <param name="createIfMissing">
    /// False for readers (the MCP server): a missing library is an empty list and nothing is created.
    /// </param>
    public List<Meeting> Load(bool createIfMissing = true)
    {
        LoadFailures.Clear();
        if (createIfMissing) Prepare();
        else if (!Directory.Exists(Root)) return [];
        var meetings = new List<Meeting>();
        using var scope = AudioPathContext.Use(Root);
        foreach (var path in Directory.EnumerateFiles(Root, "*.json")
                     .Where(path => !ReservedFiles.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase)))
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(path));
                if (!document.RootElement.TryGetProperty("id", out var identifier) || !identifier.TryGetGuid(out var id) || id == Guid.Empty)
                    throw new InvalidDataException(CoreMessages.MissingMeetingId);
                var meeting = document.RootElement.Deserialize<Meeting>(WallflyJson.Options) ?? throw new InvalidDataException(CoreMessages.EmptyMeeting);
                if (meeting.Title == null || meeting.Notes == null || meeting.EnhancedNotes == null || meeting.Transcript == null
                    || meeting.AudioFiles == null || meetings.Any(existing => existing.Id == meeting.Id))
                    throw new InvalidDataException(CoreMessages.InvalidMeetingData);
                meetings.Add(meeting);
            }
            catch (Exception error) when (error is IOException or JsonException or InvalidDataException or ArgumentException or InvalidOperationException or UnauthorizedAccessException)
            {
                LoadFailures.Add(new(Path.GetFileName(path), Path.GetFileName(path) + CoreMessages.LoadFailedSuffix));
            }
        }
        return meetings.OrderByDescending(meeting => meeting.CreatedAt).ToList();
    }

    public string MeetingPath(Guid id) => Path.Combine(Root, $"{id:D}.json");

    /// <summary>UTF-8 JSON of one meeting, produced on the caller's thread so the writer never races mutations.</summary>
    public byte[] Serialize(Meeting meeting)
    {
        using var scope = AudioPathContext.Use(Root);
        return JsonSerializer.SerializeToUtf8Bytes(meeting, WallflyJson.Options);
    }

    public void Save(Meeting meeting) => WriteBytes(meeting.Id, Serialize(meeting));

    /// <summary>The single disk write of one meeting; overridden in tests to delay or fail it.</summary>
    public virtual void WriteBytes(Guid id, byte[] json)
    {
        Prepare();
        AtomicWrite(MeetingPath(id), json);
    }

    public string AudioDirectory(Guid id)
    {
        var path = Path.Combine(Root, "Audio", id.ToString("D"));
        Directory.CreateDirectory(path);
        return path;
    }

    /// <summary>Absolute path of a stored audio value; relative values resolve against this library.</summary>
    public string ResolveAudioPath(string stored) => AudioPathContext.Resolve(Root, stored);

    /// <summary>The settings with the macOS decode rules (<see cref="AppSettings.Decode"/>); defaults when there is no file.</summary>
    public AppSettings LoadSettings() =>
        File.Exists(Path.Combine(Root, "settings.json")) ? AppSettings.Decode(File.ReadAllText(Path.Combine(Root, "settings.json"))) : new AppSettings().Normalize();

    public void SaveSettings(AppSettings settings) => AtomicWrite(Path.Combine(Root, "settings.json"), Encode(settings));

    public FolderCatalog LoadFolders()
    {
        var path = Path.Combine(Root, "Library", "folders.json");
        var catalog = File.Exists(path) ? Read<FolderCatalog>(path) : new FolderCatalog();
        if (catalog.Folders.Select(folder => folder.Id).Distinct().Count() != catalog.Folders.Count)
            throw new InvalidDataException(CoreMessages.DuplicateFolderId);
        catalog.AssignMissingColors();
        return catalog;
    }

    public void SaveFolders(FolderCatalog catalog) => AtomicWrite(Path.Combine(Root, "Library", "folders.json"), Encode(catalog));

    /// <summary>Removes a meeting with its own audio folder. Stored audio paths are never followed.</summary>
    public void Delete(Meeting meeting)
    {
        var audioRoot = Path.Combine(Root, "Audio") + Path.DirectorySeparatorChar;
        var audio = Path.GetFullPath(Path.Combine(audioRoot, meeting.Id.ToString("D")));
        if (!audio.StartsWith(audioRoot, StringComparison.OrdinalIgnoreCase)) throw new IOException(CoreMessages.AudioOutsideArchive);
        if (Directory.Exists(audio)) Directory.Delete(audio, true);
        File.Delete(MeetingPath(meeting.Id));
        File.Delete(MeetingPath(meeting.Id) + ".bak");
    }

    /// <summary>Archive formats <see cref="RemoveAudioFile"/> may delete: Windows WAV and the macOS CAF of a library from the Mac.</summary>
    public static readonly IReadOnlyList<string> ArchiveExtensions = [".wav", ".caf"];

    /// <summary>
    /// Deletes one archive file, only inside this meeting's audio folder, only a .wav (or a Mac .caf) and never a link. A
    /// file that is already gone counts as deleted (a retry after a crash finds some files removed).
    /// </summary>
    public void RemoveAudioFile(Guid meetingID, string storedPath)
    {
        var directory = Path.GetFullPath(Path.Combine(Root, "Audio", meetingID.ToString("D")));
        var file = Path.GetFullPath(ResolveAudioPath(storedPath));
        if (!string.Equals(Path.GetDirectoryName(file), directory, StringComparison.OrdinalIgnoreCase)
            || !ArchiveExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
            throw new IOException(CoreMessages.AudioOutsideArchive);
        var info = new FileInfo(file);
        if (!info.Exists && !Directory.Exists(file)) return;
        if (Directory.Exists(file) || info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
            throw new IOException(CoreMessages.AudioOutsideArchive);
        File.Delete(file);
    }

    /// <summary>Whether two stored audio values name the same file of this library.</summary>
    public bool SameAudioFile(string left, string right) =>
        string.Equals(Path.GetFullPath(ResolveAudioPath(left)), Path.GetFullPath(ResolveAudioPath(right)), StringComparison.OrdinalIgnoreCase);

    public T Read<T>(string path)
    {
        using var scope = AudioPathContext.Use(Root);
        return JsonSerializer.Deserialize<T>(File.ReadAllText(path), WallflyJson.Options) ?? throw new InvalidDataException(CoreMessages.EmptyJson);
    }

    private byte[] Encode<T>(T value)
    {
        using var scope = AudioPathContext.Use(Root);
        return JsonSerializer.SerializeToUtf8Bytes(value, WallflyJson.Options);
    }

    /// <summary>Temporary file with write-through, then <c>File.Replace</c>; the previous state stays as .bak.</summary>
    public static void AtomicWrite(string path, byte[] content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                stream.Write(content);
                stream.Flush(true);
            }
            if (File.Exists(path)) File.Replace(temporary, path, path + ".bak");
            else File.Move(temporary, path);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
