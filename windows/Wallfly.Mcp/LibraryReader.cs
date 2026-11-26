using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Wallfly.Core;
using Wallfly.Core.Json;
using Wallfly.Core.Persistence;
using Wallfly.Core.Search;
using Wallfly.Core.Text;

namespace Wallfly.Mcp;

/// <summary>One search source of a meeting (title, notes, enhanced notes, transcript) with its folded full text.</summary>
public sealed record SearchSource(string Name, IReadOnlyList<(string Text, TranscriptRun? Run)> Passages, string Folded);

/// <summary>
/// One readable meeting of a snapshot. The transcript view and the case/accent-folded search text are computed
/// once per file version, not on every tool call.
/// </summary>
public sealed class ReadMeeting
{
    private readonly Lazy<IReadOnlyList<TranscriptRun>> runs;
    private readonly Lazy<IReadOnlyList<SearchSource>> sources;

    public ReadMeeting(Meeting meeting)
    {
        Meeting = meeting;
        runs = new(() => TranscriptRuns.Build(meeting));
        sources = new(BuildSources);
    }

    public Meeting Meeting { get; }
    public Guid Id => Meeting.Id;
    public DateTimeOffset Date => Meeting.StartedAt ?? Meeting.CreatedAt;
    public IReadOnlyList<TranscriptRun> Runs => runs.Value;
    public IReadOnlyList<SearchSource> Sources => sources.Value;

    private IReadOnlyList<SearchSource> BuildSources()
    {
        static SearchSource Source(string name, List<(string Text, TranscriptRun? Run)> passages) =>
            new(name, passages, MeetingSearch.Fold(string.Join('\n', passages.Select(passage => passage.Text))).Text);
        return
        [
            Source("title", [(Meeting.Title ?? "", null)]),
            Source("notes", [(Meeting.Notes ?? "", null)]),
            Source("enhanced", [(TimestampCitations.Strip(Meeting.EnhancedNotes), null)]),
            Source("transcript", [.. Runs.Select(run => (run.Text, (TranscriptRun?)run))])
        ];
    }
}

/// <summary>
/// An immutable view of the library at one moment. <see cref="Revision"/> changes whenever a meeting file,
/// the set of meeting files or the folder catalog changes, which invalidates pagination cursors.
/// </summary>
public sealed class LibrarySnapshot(string root, IReadOnlyList<ReadMeeting> meetings, IReadOnlySet<Guid> damaged,
    FolderCatalog? folders, string revision, IReadOnlyList<LibraryReader.FileStamp> listing)
{
    public string Root { get; } = root;
    public IReadOnlyList<ReadMeeting> Meetings { get; } = meetings;
    /// <summary>Meeting files (UUID-named) that exist but could not be read or decoded.</summary>
    public IReadOnlySet<Guid> Damaged { get; } = damaged;
    public int Skipped => Damaged.Count;
    public FolderCatalog? Folders { get; } = folders;
    public string Revision { get; } = revision;
    internal IReadOnlyList<LibraryReader.FileStamp> Listing { get; } = listing;

    public string? FolderName(Guid meetingID) =>
        Folders?.FolderID(meetingID) is Guid id ? Folders.Folders.First(folder => folder.Id == id).Name : null;
}

/// <summary>
/// Read-only access to the meeting library (port of <c>ReadLibrary</c>, WallflyReadCore). The root is resolved on
/// every call (<c>--library</c> or the app's <see cref="LibraryPointer"/>), so moving the library needs no
/// restart. Nothing is ever created or written: a missing root is <see cref="ReadFailure.Unavailable"/>.
/// Files are opened with full sharing so the app's atomic <c>File.Replace</c> never fails because of a reader.
/// A corrupt or foreign meeting file is isolated and counted, never fatal. Parsed meetings are cached per file
/// version (name, size, write time); a debounced <see cref="FileSystemWatcher"/> invalidates and preloads
/// changes, and every call re-checks the directory listing, so a missed watcher event cannot serve stale data.
/// </summary>
public sealed class LibraryReader : IDisposable
{
    public readonly record struct FileStamp(string Name, long Length, long WriteTicks, bool Link);

    private sealed record FileEntry(FileStamp Stamp, byte[] Hash, ReadMeeting? Meeting, FolderCatalog? Folders = null);

    private const string FoldersName = "Library\\folders.json";
    private static readonly EnumerationOptions TopLevel = new()
    {
        RecurseSubdirectories = false, IgnoreInaccessible = true, AttributesToSkip = 0, MatchType = MatchType.Simple
    };

    private readonly string? overridePath;
    private readonly Func<string> resolveDefault;
    private readonly ILogger logger;
    private readonly object gate = new();
    private readonly Dictionary<string, FileEntry> cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly Timer debounce;
    private string? root;
    private LibrarySnapshot? snapshot;
    private FileSystemWatcher? watcher;
    private bool disposed;
    private int reportedDamaged;

    public LibraryReader(string? overridePath = null, ILogger<LibraryReader>? logger = null, Func<string>? resolveDefault = null)
    {
        this.overridePath = overridePath;
        this.resolveDefault = resolveDefault ?? (() => new LibraryPointer().Resolve());
        this.logger = (ILogger?)logger ?? NullLogger.Instance;
        debounce = new Timer(_ => Preload(), null, Timeout.Infinite, Timeout.Infinite);
    }

    /// <summary>Delay after the last file event before the changed files are parsed in the background.</summary>
    public TimeSpan DebounceDelay { get; init; } = TimeSpan.FromMilliseconds(300);

    /// <summary>Raised after a watcher-triggered background reload (tests and diagnostics).</summary>
    public event Action? Reloaded;

    /// <summary>The library root currently in effect; never created.</summary>
    public string ResolveRoot()
    {
        try { return Path.GetFullPath(overridePath ?? resolveDefault()); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        { throw new ReadException(ReadFailure.Unavailable); }
    }

    public LibrarySnapshot Snapshot()
    {
        var current = ResolveRoot();
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (!string.Equals(current, root, StringComparison.OrdinalIgnoreCase))
            {
                StopWatching();
                cache.Clear();
                snapshot = null;
                root = current;
            }
            if (!Directory.Exists(current))
            {
                StopWatching();
                throw new ReadException(ReadFailure.Unavailable);
            }
            Watch(current);
            var listing = List(current);
            if (snapshot is not null && snapshot.Listing.SequenceEqual(listing)) return snapshot;
            snapshot = Load(current, listing);
            return snapshot;
        }
    }

    private static List<FileStamp> List(string root)
    {
        try
        {
            var stamps = new List<FileStamp>();
            foreach (var file in new DirectoryInfo(root).EnumerateFiles("*.json", TopLevel))
            {
                if (!string.Equals(file.Extension, ".json", StringComparison.OrdinalIgnoreCase)
                    || !Guid.TryParseExact(Path.GetFileNameWithoutExtension(file.Name), "D", out _)) continue;
                stamps.Add(Stamp(file, file.Name));
            }
            stamps.Sort((left, right) => string.CompareOrdinal(left.Name, right.Name));
            var folders = new FileInfo(Path.Combine(root, FoldersName));
            if (folders.Exists) stamps.Add(Stamp(folders, FoldersName));
            return stamps;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        { throw new ReadException(ReadFailure.Unavailable); }
    }

    private static FileStamp Stamp(FileInfo file, string name) =>
        new(name, file.Length, file.LastWriteTimeUtc.Ticks, (file.Attributes & FileAttributes.ReparsePoint) != 0);

    private LibrarySnapshot Load(string root, List<FileStamp> listing)
    {
        var meetings = new List<ReadMeeting>();
        var damaged = new HashSet<Guid>();
        FolderCatalog? folders = null;
        using var revision = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        revision.AppendData(Encoding.UTF8.GetBytes(root));
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var stamp in listing)
        {
            seen.Add(stamp.Name);
            if (!cache.TryGetValue(stamp.Name, out var entry) || entry.Stamp != stamp)
                cache[stamp.Name] = entry = Read(root, stamp);
            revision.AppendData(Encoding.UTF8.GetBytes(stamp.Name));
            revision.AppendData(entry.Hash);
            if (stamp.Name == FoldersName) folders = entry.Folders;
            else if (entry.Meeting is { } meeting) meetings.Add(meeting);
            else damaged.Add(Guid.ParseExact(Path.GetFileNameWithoutExtension(stamp.Name), "D"));
        }
        foreach (var stale in cache.Keys.Where(name => !seen.Contains(name)).ToList()) cache.Remove(stale);
        // Reported once per change of the count, not on every reload while the app keeps saving.
        if (damaged.Count != reportedDamaged && damaged.Count > 0)
            logger.LogWarning("{Count} meeting file(s) could not be read and are skipped.", damaged.Count);
        reportedDamaged = damaged.Count;
        return new LibrarySnapshot(root, meetings, damaged, folders, Convert.ToHexStringLower(revision.GetHashAndReset()), listing);
    }

    private static readonly byte[] Unreadable = SHA256.HashData("unreadable"u8);

    private FileEntry Read(string root, FileStamp stamp)
    {
        // A link could point anywhere; like the macOS reader only regular files inside the library are read.
        if (stamp.Link) return new FileEntry(stamp, Unreadable, null);
        byte[] bytes;
        try { bytes = ReadShared(Path.Combine(root, stamp.Name)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { return new FileEntry(stamp, Unreadable, null); }
        var hash = SHA256.HashData(bytes);
        if (stamp.Name == FoldersName) return new FileEntry(stamp, hash, null, ParseFolders(bytes));
        var expected = Guid.ParseExact(Path.GetFileNameWithoutExtension(stamp.Name), "D");
        try
        {
            using var scope = AudioPathContext.Use(root);
            var meeting = JsonSerializer.Deserialize<Meeting>(bytes, WallflyJson.Options);
            // Same acceptance rules as MeetingLibrary.Load, plus the macOS rule that the id matches the file name.
            if (meeting is null || meeting.Id != expected || meeting.Title is null || meeting.Notes is null
                || meeting.EnhancedNotes is null || meeting.Transcript is null || meeting.AudioFiles is null)
                return new FileEntry(stamp, hash, null);
            return new FileEntry(stamp, hash, new ReadMeeting(meeting));
        }
        catch (Exception error) when (error is JsonException or InvalidDataException or ArgumentException or InvalidOperationException or NotSupportedException)
        {
            return new FileEntry(stamp, hash, null);
        }
    }

    /// <summary>The folder catalog with the acceptance rule of <c>MeetingLibrary.LoadFolders</c> (unique ids).</summary>
    private FolderCatalog? ParseFolders(byte[] bytes)
    {
        try
        {
            var catalog = JsonSerializer.Deserialize<FolderCatalog>(bytes, WallflyJson.Options);
            if (catalog is not null && catalog.Folders.Select(folder => folder.Id).Distinct().Count() == catalog.Folders.Count) return catalog;
        }
        catch (Exception error) when (error is JsonException or ArgumentException or InvalidOperationException or NotSupportedException) { }
        logger.LogWarning("The folder catalog could not be read; folder filters report the folder as not found.");
        return null;
    }

    /// <summary>Read with every share flag, so the app can still replace or delete the file meanwhile.</summary>
    public static byte[] ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1, FileOptions.SequentialScan);
        using var memory = new MemoryStream(stream.CanSeek ? (int)Math.Min(stream.Length, int.MaxValue) : 0);
        stream.CopyTo(memory);
        return memory.ToArray();
    }

    private void Watch(string root)
    {
        if (watcher is not null) return;
        try
        {
            var created = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
                InternalBufferSize = 64 * 1024
            };
            created.Changed += OnChanged;
            created.Created += OnChanged;
            created.Deleted += OnChanged;
            created.Renamed += OnChanged;
            created.Error += OnError;
            created.EnableRaisingEvents = true;
            watcher = created;
        }
        catch (Exception error) when (error is IOException or ArgumentException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Without notifications the listing check on every call still keeps results current.
            logger.LogDebug("Library change notifications are unavailable: {Error}", error.GetType().Name);
        }
    }

    private void StopWatching()
    {
        if (watcher is null) return;
        watcher.EnableRaisingEvents = false;
        watcher.Dispose();
        watcher = null;
    }

    private void OnChanged(object sender, FileSystemEventArgs change)
    {
        var relevant = new[] { change.Name, (change as RenamedEventArgs)?.OldName }
            .Where(name => name is not null)
            .Select(name => name!.Replace('/', '\\'))
            .Where(name => string.Equals(name, FoldersName, StringComparison.OrdinalIgnoreCase)
                || (!name.Contains('\\') && name.EndsWith(".json", StringComparison.OrdinalIgnoreCase)))
            .ToList();
        if (relevant.Count == 0) return; // audio, detector state, temporary files of the atomic writer
        lock (gate)
        {
            foreach (var name in relevant) cache.Remove(name);
            snapshot = null;
        }
        Schedule();
    }

    private void OnError(object sender, ErrorEventArgs error)
    {
        lock (gate)
        {
            cache.Clear();
            snapshot = null;
            StopWatching();
        }
        Schedule();
    }

    private void Schedule()
    {
        lock (gate) { if (!disposed) debounce.Change(DebounceDelay, Timeout.InfiniteTimeSpan); }
    }

    private void Preload()
    {
        try { Snapshot(); }
        catch (Exception error) when (error is ReadException or ObjectDisposedException) { return; }
        Reloaded?.Invoke();
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
            StopWatching();
            debounce.Dispose();
        }
    }
}
