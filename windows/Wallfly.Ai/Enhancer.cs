using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wallfly.Ai;

/// <summary>Everything the enhancer may read; the AI layer never sees persistence models.</summary>
/// <param name="TranscriptText">Lines <c>[mm:ss] Name: Text</c>, as stored and exported.</param>
/// <param name="SpokenText">The ordered passage texts joined by newlines; the language is detected from it.</param>
public sealed record EnhancementInput(
    string Title,
    string Notes,
    string TranscriptText,
    string SpokenText,
    IReadOnlyList<ExistingTaskReference> ExistingTasks,
    string TaskOwnerName = "");

/// <summary>The single-call result. <see cref="Title"/> is null when the model gave no valid title.</summary>
public sealed record EnhancementResult(string Notes, string? Title, IReadOnlyList<TaskDraft> Tasks);

/// <summary>
/// Port of <c>NotesEnhancer.enhanceAll</c> (macOS 1.13.0): one request with typed notes, existing tasks and the
/// whole transcript returns notes, tasks and a title as JSON; at most one correction call follows when the notes
/// come back confidently in another language than the meeting. Nothing here writes to a meeting.
/// </summary>
public sealed class NotesEnhancer(IChatProvider provider, ILanguageDetector? detector = null, Func<string, string>? removeTimestampCitations = null)
{
    public const int MaximumResponseTokens = 16000;

    /// <summary>Default meeting titles that may be replaced by a suggestion (including the legacy German ones).</summary>
    public static readonly string[] PlaceholderTitles = ["New note", "Untitled meeting", "Neue Notiz", "Unbenanntes Meeting"];

    /// <summary>The system instructions, verbatim from <c>NotesEnhancer.instructions</c>.</summary>
    public static string Instructions => Prompts.Get("EnhancerInstructions");

    /// <summary>Port of <c>Meeting.needsSuggestedTitle</c>.</summary>
    public static bool IsPlaceholderTitle(string? title)
    {
        var value = (title ?? "").Trim();
        return value.Length == 0 || PlaceholderTitles.Contains(value, StringComparer.Ordinal);
    }

    /// <summary>Splits on grapheme clusters so no chunk exceeds <paramref name="maximumBytes"/> UTF-8 bytes (unless one cluster does).</summary>
    public static List<string> Chunks(string text, int maximumBytes = 4200)
    {
        var chunks = new List<string>();
        if (string.IsNullOrEmpty(text)) return chunks;
        var current = new StringBuilder();
        var bytes = 0;
        var enumerator = StringInfo.GetTextElementEnumerator(text);
        while (enumerator.MoveNext())
        {
            var part = enumerator.GetTextElement();
            var count = Encoding.UTF8.GetByteCount(part);
            if (bytes + count > maximumBytes && current.Length > 0) { chunks.Add(current.ToString()); current.Clear(); bytes = 0; }
            current.Append(part); bytes += count;
        }
        if (current.Length > 0) chunks.Add(current.ToString());
        return chunks;
    }

    public async Task<EnhancementResult> EnhanceAllAsync(EnhancementInput meeting, Action<string> progress, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(meeting.TranscriptText)) throw AiError.Transient(AiMessages.NoTranscript);
        var language = new MeetingOutputLanguage(meeting.SpokenText, meeting.Notes, detector);
        var instructions = Instructions + "\n" + language.Instruction + "\n" + PersonalTasks.Instructions(meeting.TaskOwnerName)
            + "\n" + Prompts.Get("EnhancementJsonRule");
        var existing = "[" + string.Join(",", meeting.ExistingTasks.Select((task, index) => SwiftText.TaskEntry(index, task.Text, task.Owner))) + "]";
        var prompt = Prompts.Fill("EnhancementPrompt",
            ("MEETING_TITLE", meeting.Title), ("TYPED_NOTES", meeting.Notes), ("EXISTING_TASKS", existing), ("TRANSCRIPT", meeting.TranscriptText));
        progress(AiMessages.Creating);
        cancellationToken.ThrowIfCancellationRequested();
        var response = await provider.GenerateAsync(new(instructions, language.Contextualize(prompt), MaximumResponseTokens, JsonResponse: true), cancellationToken).ConfigureAwait(false);
        var result = DecodeEnhancement(response, meeting.ExistingTasks);
        cancellationToken.ThrowIfCancellationRequested();
        var corrected = await CorrectLanguageAsync(result.Notes, language, Instructions + "\n" + language.Instruction, progress, cancellationToken).ConfigureAwait(false);
        return result with { Notes = removeTimestampCitations?.Invoke(corrected) ?? corrected };
    }

    /// <summary>One correction call when the notes are confidently in the wrong language; a second miss is an error.</summary>
    private async Task<string> CorrectLanguageAsync(string notes, MeetingOutputLanguage language, string instructions, Action<string> progress, CancellationToken cancellationToken)
    {
        if (!language.ConfidentlyDisagrees(notes)) return notes;
        progress(AiMessages.CorrectingLanguage);
        cancellationToken.ThrowIfCancellationRequested();
        var correction = Prompts.Fill("LanguageCorrection", ("DRAFT", notes));
        var corrected = await provider.GenerateAsync(new(instructions, correction, MaximumResponseTokens), cancellationToken).ConfigureAwait(false);
        if (language.ConfidentlyDisagrees(corrected)) throw AiError.Transient(AiMessages.WrongLanguageAgain);
        return corrected;
    }

    /// <summary>
    /// Reads the JSON object from the first <c>{</c> to the last <c>}</c> (tolerating fences and preambles). Invalid
    /// single tasks or titles are dropped instead of discarding the whole response; empty notes are an error.
    /// </summary>
    public static EnhancementResult DecodeEnhancement(string response, IReadOnlyList<ExistingTaskReference> existingTasks)
    {
        var start = response.IndexOf('{');
        var end = response.LastIndexOf('}');
        JsonDocument document;
        try
        {
            if (start < 0 || end <= start) throw new JsonException();
            document = JsonDocument.Parse(response.AsMemory(start, end - start + 1));
        }
        catch (JsonException) { throw AiError.Transient(AiMessages.UnreadableEnhancement); }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw AiError.Transient(AiMessages.UnreadableEnhancement);
            var notes = ValidateOutput(LossyString(root, "notes_markdown") ?? "");
            var referenced = new HashSet<int>();
            var tasks = new List<TaskDraft>();
            if (root.TryGetProperty("tasks", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var element in items.EnumerateArray())
                {
                    if (!TryItem(element, out var text, out var owner, out var status, out var existing)) continue;
                    if (status != null && status.Trim().ToLowerInvariant() != "open") continue;
                    text = text.Trim();
                    owner = owner?.Trim();
                    if (text.Length == 0 || Encoding.UTF8.GetByteCount(text) > 1200 || Encoding.UTF8.GetByteCount(owner ?? "") > 300) continue;
                    if (existing is int index && (index < 0 || index >= existingTasks.Count || !referenced.Add(index))) continue;
                    tasks.Add(new(text, string.IsNullOrEmpty(owner) ? null : owner, existing is int reference ? existingTasks[reference].Id : null));
                }
            }
            string? title = null;
            if (LossyString(root, "title") is { } candidate)
                try { title = ValidateTitle(candidate); } catch (AiError) { }
            return new(notes, title, tasks);
        }
    }

    /// <summary>A <c>Lossy&lt;String&gt;</c>: the value when it is a JSON string, otherwise null.</summary>
    private static string? LossyString(JsonElement root, string name) =>
        root.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    /// <summary>Decodes one <c>Item</c>; any wrongly typed field drops just this task.</summary>
    private static bool TryItem(JsonElement element, out string text, out string? owner, out string? status, out int? existing)
    {
        text = ""; owner = null; status = null; existing = null;
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty("text", out var textValue) || textValue.ValueKind != JsonValueKind.String) return false;
        text = textValue.GetString() ?? "";
        if (!OptionalString(element, "owner", out owner) || !OptionalString(element, "status", out status)) return false;
        if (element.TryGetProperty("existing", out var index) && index.ValueKind != JsonValueKind.Null)
        {
            if (index.ValueKind != JsonValueKind.Number) return false;
            if (index.TryGetInt32(out var value)) existing = value;
            else if (index.TryGetDouble(out var number) && number == Math.Floor(number) && Math.Abs(number) <= int.MaxValue) existing = (int)number;
            else return false;
        }
        return true;
    }

    private static bool OptionalString(JsonElement element, string name, out string? value)
    {
        value = null;
        if (!element.TryGetProperty(name, out var property) || property.ValueKind == JsonValueKind.Null) return true;
        if (property.ValueKind != JsonValueKind.String) return false;
        value = property.GetString();
        return true;
    }

    /// <summary>One line, at most 80 characters, no Markdown heading or fence, not a placeholder; wrapping quotes are removed.</summary>
    public static string ValidateTitle(string text)
    {
        var title = text.Trim().Trim('"', '“', '”', '„').Trim();
        if (title.Length == 0 || new StringInfo(title).LengthInTextElements > 80
            || title.IndexOfAny(['\n', '\r', '\u000B', '\u000C', '\u0085', (char)0x2028, (char)0x2029]) >= 0
            || !title.EnumerateRunes().Any(rune => TaskTextKey.IsAlphanumeric(Rune.GetUnicodeCategory(rune)))
            || title.StartsWith('#') || title.StartsWith("```", StringComparison.Ordinal) || IsPlaceholderTitle(title))
            throw AiError.Transient(AiMessages.InvalidTitle);
        return title;
    }

    /// <summary>Trimmed model text; empty output is an error so nothing empty is ever saved.</summary>
    public static string ValidateOutput(string text)
    {
        var cleaned = (text ?? "").Trim();
        return cleaned.Length == 0 ? throw AiError.Transient(AiMessages.EmptyNotes) : cleaned;
    }
}
