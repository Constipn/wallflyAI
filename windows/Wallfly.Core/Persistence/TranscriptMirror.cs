using System.Text.Json;
using System.Text.Json.Serialization;
using Wallfly.Core.Json;
using Wallfly.Core.Text;

namespace Wallfly.Core.Persistence;

/// <summary>
/// <c>&lt;UUID&gt;.transcript.json</c> (format <c>meetingnotes.transcript.v1</c>, port of <c>TranscriptMirrorDocument</c>):
/// metadata, segments with words, participants and gaps. Deliberately neither notes nor audio paths.
/// </summary>
public sealed class TranscriptMirrorDocument
{
    public const string CurrentFormat = "meetingnotes.transcript.v1";

    [JsonPropertyName("createdAt")] public DateTimeOffset CreatedAt { get; set; }
    [JsonPropertyName("duration")] public double Duration { get; set; }
    [JsonPropertyName("format")] public string Format { get; set; } = CurrentFormat;
    [JsonPropertyName("meetingID")] public Guid MeetingID { get; set; }
    [JsonPropertyName("participants")] public List<MeetingParticipant>? Participants { get; set; }
    [JsonPropertyName("startedAt")] public DateTimeOffset? StartedAt { get; set; }
    [JsonPropertyName("title")] public string Title { get; set; } = "";
    [JsonPropertyName("transcript")] public List<TranscriptSegment> Transcript { get; set; } = [];
    [JsonPropertyName("transcriptionGaps")] public List<TranscriptionGap>? TranscriptionGaps { get; set; }

    public static TranscriptMirrorDocument From(Meeting meeting) => new()
    {
        MeetingID = meeting.Id, Title = meeting.Title, CreatedAt = meeting.CreatedAt, StartedAt = meeting.StartedAt,
        Duration = meeting.Duration, Transcript = meeting.Transcript, Participants = meeting.Participants,
        TranscriptionGaps = meeting.TranscriptionGaps
    };
}

/// <summary>
/// The optional external transcript copy (spec 8, port of <c>TranscriptMirror</c>): after every successful internal save
/// of a meeting that was created while mirroring was on (<see cref="Meeting.TranscriptMirror"/> pins its folder), the
/// transcript is written to <c>&lt;folder&gt;\&lt;UUID&gt;.transcript.json</c>. Runs on the writer thread; its failures are
/// reported separately and never fail the internal save. Only its own files are ever replaced.
/// </summary>
public sealed class TranscriptMirrorWriter
{
    private volatile bool enabled;

    /// <summary>The global switch (<see cref="AppSettings.TranscriptMirrorEnabled"/>); off stops all updates.</summary>
    public bool Enabled { get => enabled; set => enabled = value; }

    /// <summary>(meeting, null on success or the user-facing failure). Raised on the writer thread.</summary>
    public event Action<Guid, string?>? Reported;

    /// <summary>Called with the bytes the writer just saved for <paramref name="id"/>.</summary>
    public void OnSaved(Guid id, byte[] json, string libraryRoot)
    {
        if (!Enabled) return;
        Meeting? meeting;
        try
        {
            using var scope = AudioPathContext.Use(libraryRoot);
            meeting = JsonSerializer.Deserialize<Meeting>(json, WallflyJson.Options);
        }
        catch (JsonException) { return; }
        if (meeting?.TranscriptMirror is not { Path.Length: > 0 } destination || meeting.Transcript.Count == 0) return;
        string? message = null;
        try { Write(meeting, destination.Path); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or ArgumentException or NotSupportedException)
        {
            message = CoreMessages.TranscriptMirrorFailed(error.Message);
        }
        Reported?.Invoke(id, message);
    }

    private const int ReplaceAttempts = 5;
    private static readonly TimeSpan ReplaceRetryDelay = TimeSpan.FromMilliseconds(40);

    public static string FileName(Guid id) => id.ToString("D").ToUpperInvariant() + ".transcript.json";

    /// <summary>Writes the mirror file atomically; refuses a missing folder and any file it did not write itself.</summary>
    /// <exception cref="IOException">The folder is missing or the destination holds a foreign file.</exception>
    public static void Write(Meeting meeting, string directory)
    {
        if (!Directory.Exists(directory)) throw new IOException(CoreMessages.TranscriptMirrorFolderMissing);
        var path = Path.Combine(directory, FileName(meeting.Id));
        var document = TranscriptMirrorDocument.From(meeting);
        if (File.Exists(path) || Directory.Exists(path))
        {
            // Do not replace unrelated files or follow user-created links.
            var info = new FileInfo(path);
            if (Directory.Exists(path) || info.LinkTarget != null || info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                throw new IOException(CoreMessages.TranscriptMirrorForeignFile);
            TranscriptMirrorDocument? previous;
            try { previous = JsonSerializer.Deserialize<TranscriptMirrorDocument>(File.ReadAllText(path), WallflyJson.Options); }
            catch (JsonException) { previous = null; }
            if (previous is null || previous.Format != document.Format || previous.MeetingID != meeting.Id)
                throw new IOException(CoreMessages.TranscriptMirrorForeignFile);
        }
        // Temporary file, then an atomic replace without a backup copy: the folder holds exactly one file per meeting.
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(document, WallflyJson.Options));
            for (var attempt = 1; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    break;
                }
                // Another program reading the mirror (an indexer, a sync client, the user's editor) blocks the replace briefly.
                catch (Exception error) when (attempt < ReplaceAttempts && error is IOException or UnauthorizedAccessException)
                {
                    Thread.Sleep(ReplaceRetryDelay);
                }
            }
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
