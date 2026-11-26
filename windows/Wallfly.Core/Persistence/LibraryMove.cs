using Wallfly.Core.Text;

namespace Wallfly.Core.Persistence;

/// <summary>
/// Moving the meeting library (spec 8, port of <c>LibraryMigration</c>): everything except the app's own entries
/// (<see cref="MeetingLibrary.StaysWithApp"/>, above all <c>Detection</c>) is copied to the destination, the copy is
/// loaded, absolute audio paths inside the old library are rebased and the result is verified. Only then does the caller
/// flip the pointer and delete the originals. On any failure only the newly created copy is removed.
/// </summary>
public sealed class LibraryMove
{
    private LibraryMove(MeetingLibrary source, MeetingLibrary destination, IReadOnlyList<string> movedNames)
    {
        Source = source; Destination = destination; MovedNames = movedNames;
    }

    public MeetingLibrary Source { get; }
    public MeetingLibrary Destination { get; }
    /// <summary>The top-level entries that were copied and are deleted by <see cref="RemoveOriginals"/>.</summary>
    public IReadOnlyList<string> MovedNames { get; }

    /// <param name="expectedMeetings">Meetings the app has loaded; each must be readable from the copy.</param>
    /// <exception cref="IOException">The destination exists, overlaps the library, or the copy could not be verified.</exception>
    public static LibraryMove Prepare(MeetingLibrary source, string destinationRoot, IReadOnlyCollection<Guid> expectedMeetings)
    {
        var old = Normalized(source.Root);
        var target = Normalized(destinationRoot);
        if (string.Equals(old, target, StringComparison.OrdinalIgnoreCase) || IsInside(target, old) || IsInside(old, target)
            || Directory.Exists(target) || File.Exists(target))
            throw new IOException(CoreMessages.LibraryMoveDestinationExists);
        var names = Directory.Exists(old)
            ? Directory.EnumerateFileSystemEntries(old).Select(path => Path.GetFileName(path))
                .Where(name => !MeetingLibrary.StaysWithApp.Contains(name, StringComparer.OrdinalIgnoreCase)
                    && !name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToList()
            : [];
        Directory.CreateDirectory(target);
        try
        {
            foreach (var name in names) Copy(Path.Combine(old, name), Path.Combine(target, name));
            var destination = new MeetingLibrary(target);
            var meetings = destination.Load(createIfMissing: false);
            if (expectedMeetings.Any(id => meetings.All(meeting => meeting.Id != id)))
                throw new IOException(CoreMessages.LibraryMoveVerifyFailed);
            var move = new LibraryMove(source, destination, names);
            foreach (var meeting in meetings)
            {
                if (!move.RebaseAudio(meeting)) continue;
                destination.Save(meeting);
            }
            _ = destination.LoadSettings();
            _ = destination.LoadFolders();
            return move;
        }
        catch
        {
            // Only our newly created destination is removed; the original library is untouched.
            try { Directory.Delete(target, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
            throw;
        }
    }

    /// <summary>
    /// An absolute path inside the old library's <c>Audio</c> folder, rewritten for the destination; anything else
    /// (relative values, paths elsewhere) is returned unchanged.
    /// </summary>
    public string Rebase(string stored)
    {
        if (string.IsNullOrEmpty(stored) || !Path.IsPathRooted(stored)) return stored;
        string full;
        try { full = Path.GetFullPath(stored); }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { return stored; }
        var audio = Path.Combine(Normalized(Source.Root), "Audio") + Path.DirectorySeparatorChar;
        if (!full.StartsWith(audio, StringComparison.OrdinalIgnoreCase)) return stored;
        return Path.Combine(Destination.Root, "Audio", full[audio.Length..]);
    }

    /// <summary>Rebases <see cref="Meeting.AudioFiles"/> and <see cref="Meeting.AudioDeletionPending"/>; true when one changed.</summary>
    public bool RebaseAudio(Meeting meeting)
    {
        var changed = false;
        List<string> Map(List<string> paths)
        {
            var result = paths.Select(Rebase).ToList();
            changed |= !result.SequenceEqual(paths, StringComparer.Ordinal);
            return result;
        }
        meeting.AudioFiles = Map(meeting.AudioFiles);
        if (meeting.AudioDeletionPending is { } pending) meeting.AudioDeletionPending = Map(pending);
        return changed;
    }

    /// <summary>Call only after the pointer was switched to <see cref="Destination"/>.</summary>
    public void RemoveOriginals()
    {
        List<Exception> failures = [];
        foreach (var name in MovedNames)
        {
            var path = Path.Combine(Source.Root, name);
            try
            {
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException) { failures.Add(error); }
        }
        if (failures.Count > 0) throw new IOException(string.Join(" ", failures.Select(failure => failure.Message).Distinct()));
    }

    private static string Normalized(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool IsInside(string path, string folder) =>
        path.StartsWith(folder + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static void Copy(string source, string destination)
    {
        if (File.Exists(source))
        {
            File.Copy(source, destination, overwrite: false);
            return;
        }
        Directory.CreateDirectory(destination);
        foreach (var entry in Directory.EnumerateFileSystemEntries(source))
            Copy(entry, Path.Combine(destination, Path.GetFileName(entry)));
    }
}
