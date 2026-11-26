using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wallfly.Ai;

public sealed record TaskExtractionInput(
    string Notes,
    string TranscriptText,
    string SpokenText,
    IReadOnlyList<ExistingTaskReference> ExistingTasks,
    string TaskOwnerName = "");

/// <summary>
/// Port of <c>TaskExtractor</c>, the "Retry to-do recognition" path: typed notes, then the transcript, in 1 600-byte
/// chunks with 100 characters of overlap, each event backed by a verbatim quote that is checked here, then matched
/// against existing tasks by exact key or a separate match request. Each request is bounded to 1 800 tokens; no
/// source or task list is silently truncated.
/// </summary>
public sealed class TaskExtractor(IChatProvider provider, ILanguageDetector? detector = null)
{
    public const int MaximumResponseTokens = 1800;
    public const int ChunkBytes = 1600;
    public const int OverlapCharacters = 100;
    public const int MatchBatchBytes = 3000;

    /// <summary>The system instructions, verbatim from <c>TaskExtractor.instructions</c>.</summary>
    public static string Instructions => Prompts.Get("TaskExtractorInstructions");

    public enum Status { Open, Completed, Cancelled }

    public sealed record ActionEvent(string Text, string? Owner, Status Status, string Evidence);

    private sealed class Group(string text, string? owner, Guid? existingId = null)
    {
        public string Text { get; } = text;
        public string? Owner { get; set; } = owner;
        public Guid? ExistingId { get; } = existingId;
        public Status? Status { get; set; }
    }

    /// <summary>
    /// A JSON array is required; single malformed, oversized, unsupported or unquoted events are dropped. An event
    /// survives only if its evidence occurs verbatim (whitespace-normalised) in <paramref name="source"/>.
    /// </summary>
    public static List<ActionEvent> DecodeEvents(string response, string source)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(response); }
        catch (JsonException failure) { throw AiError.Transient(AiMessages.UnreadableData, failure); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Array) throw AiError.Transient(AiMessages.UnreadableData);
            var normalizedSource = Whitespace(source);
            var events = new List<ActionEvent>();
            foreach (var element in document.RootElement.EnumerateArray())
            {
                if (TryEvent(element) is not { } action) continue;
                if (action.Text.Trim().Length == 0 || Encoding.UTF8.GetByteCount(action.Text) > 1200 || Encoding.UTF8.GetByteCount(action.Owner ?? "") > 300
                    || action.Evidence.Trim().Length == 0 || !normalizedSource.Contains(Whitespace(action.Evidence), StringComparison.Ordinal))
                    continue;
                events.Add(action);
            }
            return events;
        }
    }

    private static ActionEvent? TryEvent(JsonElement element)
    {
        if (element.ValueKind != JsonValueKind.Object) return null;
        if (!element.TryGetProperty("text", out var text) || text.ValueKind != JsonValueKind.String) return null;
        if (!element.TryGetProperty("evidence", out var evidence) || evidence.ValueKind != JsonValueKind.String) return null;
        if (!element.TryGetProperty("status", out var status) || status.ValueKind != JsonValueKind.String) return null;
        string? owner = null;
        if (element.TryGetProperty("owner", out var ownerValue) && ownerValue.ValueKind != JsonValueKind.Null)
        {
            if (ownerValue.ValueKind != JsonValueKind.String) return null;
            owner = ownerValue.GetString();
        }
        Status? parsed = status.GetString() switch { "open" => Status.Open, "completed" => Status.Completed, "cancelled" => Status.Cancelled, _ => null };
        return parsed is { } value ? new(text.GetString()!, owner, value, evidence.GetString()!) : null;
    }

    private static string Whitespace(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    public async Task<List<TaskDraft>> ExtractAsync(TaskExtractionInput meeting, Action<string> progress, CancellationToken cancellationToken)
    {
        var language = new MeetingOutputLanguage(meeting.SpokenText, meeting.Notes, detector);
        var instructions = Instructions + "\n" + language.Instruction + "\n" + PersonalTasks.Instructions(meeting.TaskOwnerName)
            + "\n" + Prompts.Get("TaskExtractorJsonRule");
        var events = new List<ActionEvent>();
        // Overlap retains commitments crossing a chunk boundary. Transcript follows notes so a later explicit
        // cancellation or completion wins when reconciling the same action.
        foreach (var (kind, source) in new[] { ("Typed notes", meeting.Notes), ("Transcript", meeting.TranscriptText) })
        {
            if (string.IsNullOrEmpty(source)) continue;
            var chunks = NotesEnhancer.Chunks(source, ChunkBytes);
            for (var index = 0; index < chunks.Count; index++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var text = (index == 0 ? "" : Suffix(chunks[index - 1], OverlapCharacters)) + chunks[index];
                progress(AiMessages.LookingForTasks(index + 1, chunks.Count));
                var prompt = Prompts.Fill("TaskChunkPrompt", ("KIND", kind), ("SOURCE", text));
                var response = await provider.GenerateAsync(new(instructions, language.Contextualize(prompt), MaximumResponseTokens), cancellationToken).ConfigureAwait(false);
                events.AddRange(DecodeEvents(response, text));
            }
        }

        var groups = meeting.ExistingTasks.Select(task => new Group(task.Text, task.Owner, task.Id)).ToList();
        for (var index = 0; index < events.Count; index++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var action = events[index];
            progress(AiMessages.MatchingTasks(index + 1, events.Count));
            var match = groups.FindIndex(group => TaskTextKey.Normalize(group.Text) == TaskTextKey.Normalize(action.Text)
                && TaskTextKey.Normalize(group.Owner) == TaskTextKey.Normalize(action.Owner));
            if (match < 0) match = await MatchingGroupAsync(action, groups, instructions, cancellationToken).ConfigureAwait(false);
            if (match >= 0)
            {
                groups[match].Status = action.Status;
                groups[match].Owner ??= action.Owner;
            }
            else groups.Add(new(action.Text, action.Owner) { Status = action.Status });
        }
        return groups.Where(group => group.Status == Status.Open).Select(group => new TaskDraft(group.Text, group.Owner, group.ExistingId)).ToList();
    }

    /// <summary>The last <paramref name="count"/> grapheme clusters (<c>String.suffix</c>).</summary>
    private static string Suffix(string text, int count)
    {
        var info = new StringInfo(text);
        return info.LengthInTextElements <= count ? text : info.SubstringByTextElements(info.LengthInTextElements - count);
    }

    /// <summary>
    /// Compares against bounded batches, including completed local tasks, so their IDs survive even if a resumed
    /// recording describes an old action in different words. A reply without <c>match</c> is invalid, rather than
    /// silently treating a malformed answer as "no match".
    /// </summary>
    private async Task<int> MatchingGroupAsync(ActionEvent action, List<Group> groups, string instructions, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < groups.Count)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entries = new List<string>();
            var length = 0;
            while (offset + entries.Count < groups.Count)
            {
                var group = groups[offset + entries.Count];
                var entry = SwiftText.TaskEntry(entries.Count, group.Text, group.Owner);
                var size = Encoding.UTF8.GetByteCount(entry);
                if (entries.Count > 0 && length + size > MatchBatchBytes) break;
                entries.Add(entry);
                length += size;
            }
            var prompt = Prompts.Fill("TaskMatchPrompt", ("CANDIDATE", Candidate(action)), ("EXISTING", "[" + string.Join(",", entries) + "]"));
            var response = await provider.GenerateAsync(new(instructions, prompt, MaximumResponseTokens), cancellationToken).ConfigureAwait(false);
            var found = ParseMatch(response);
            if (found is int index)
            {
                if (index < 0 || index >= entries.Count) throw AiError.Transient(AiMessages.InvalidMatchReference);
                return offset + index;
            }
            offset += entries.Count;
        }
        return -1;
    }

    /// <summary><c>{"match":null}</c> or <c>{"match":0}</c>; anything else is an error.</summary>
    public static int? ParseMatch(string response)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(response); }
        catch (JsonException failure) { throw AiError.Transient(AiMessages.UnreadableData, failure); }
        using (document)
        {
            if (document.RootElement.ValueKind != JsonValueKind.Object || !document.RootElement.TryGetProperty("match", out var match))
                throw AiError.Transient(AiMessages.InvalidMatch);
            if (match.ValueKind == JsonValueKind.Null) return null;
            if (match.ValueKind == JsonValueKind.Number && match.TryGetInt32(out var index)) return index;
            throw AiError.Transient(AiMessages.UnreadableData);
        }
    }

    /// <summary>The candidate as <c>JSONEncoder</c> writes the event: a missing owner is omitted.</summary>
    private static string Candidate(ActionEvent action) =>
        "{\"text\":" + SwiftText.JsonString(action.Text) + (action.Owner == null ? "" : ",\"owner\":" + SwiftText.JsonString(action.Owner))
        + ",\"status\":\"" + action.Status.ToString().ToLowerInvariant() + "\",\"evidence\":" + SwiftText.JsonString(action.Evidence) + "}";
}
