using System.Globalization;
using System.Text;

namespace Wallfly.Ai;

/// <summary>A task the model proposes. <see cref="ExistingTaskId"/> marks a reference to an already saved task.</summary>
public sealed record TaskDraft(string Text, string? Owner = null, Guid? ExistingTaskId = null);

/// <summary>A task already stored with the meeting, reduced to what the prompts need.</summary>
public sealed record ExistingTaskReference(Guid Id, string Text, string? Owner);

/// <summary>
/// Port of <c>MeetingTask.key</c>: case- and diacritic-insensitive, alphanumeric runs (letters, marks and numbers)
/// joined by single spaces.
/// </summary>
public static class TaskTextKey
{
    public static string Normalize(string? text)
    {
        var builder = new StringBuilder();
        foreach (var rune in (text ?? "").Normalize(NormalizationForm.FormD).EnumerateRunes())
        {
            var category = Rune.GetUnicodeCategory(rune);
            if (category == UnicodeCategory.NonSpacingMark) continue;
            if (!IsAlphanumeric(category)) { builder.Append(' '); continue; }
            var lower = Rune.ToLowerInvariant(rune);
            // Full case folding, as Foundation's caseInsensitive folding does.
            if (lower.Value is 'ß' or 'ẞ') builder.Append("ss"); else builder.Append(lower.ToString());
        }
        return string.Join(' ', builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries));
    }

    /// <summary><c>CharacterSet.alphanumerics</c>: Unicode categories L*, M* and N*.</summary>
    internal static bool IsAlphanumeric(UnicodeCategory category) => category <= UnicodeCategory.OtherNumber;

    /// <summary><c>CharacterSet.letters</c>: Unicode categories L* and M*.</summary>
    internal static bool IsLetter(UnicodeCategory category) => category <= UnicodeCategory.EnclosingMark;
}

/// <summary>Port of <c>PersonalTasks</c>: only the app user's own commitments become to-do suggestions.</summary>
public static class PersonalTasks
{
    /// <summary>"Me", "Ich", "myself" or exactly the configured name, ignoring case and accents.</summary>
    public static bool IsMine(string? owner, string? name)
    {
        if (owner == null) return false;
        var key = TaskTextKey.Normalize(owner);
        var configured = TaskTextKey.Normalize(name);
        return key is "me" or "ich" or "myself" || configured.Length > 0 && key == configured;
    }

    /// <summary>The personal-task rule appended to the system instructions; the name is quoted as untrusted data.</summary>
    public static string Instructions(string? name) => Prompts.Fill("PersonalTasks", ("NAME", SwiftText.DebugDescription(name ?? "")));
}

/// <summary>Text encodings that keep prompts byte-identical to the macOS app.</summary>
internal static class SwiftText
{
    /// <summary><c>String(reflecting:)</c>: double-quoted with Swift escapes for quotes, backslashes and control characters.</summary>
    public static string DebugDescription(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var rune in value.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\': builder.Append("\\\\"); break;
                case '"': builder.Append("\\\""); break;
                case '\'': builder.Append("\\'"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case 0: builder.Append("\\0"); break;
                default:
                    var category = Rune.GetUnicodeCategory(rune);
                    if (category is UnicodeCategory.Control or UnicodeCategory.Format or UnicodeCategory.LineSeparator
                        or UnicodeCategory.ParagraphSeparator or UnicodeCategory.Surrogate or UnicodeCategory.OtherNotAssigned)
                        builder.Append("\\u{").Append(rune.Value.ToString("x", CultureInfo.InvariantCulture)).Append('}');
                    else builder.Append(rune.ToString());
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    /// <summary>A JSON string as <c>JSONSerialization</c> writes it: non-ASCII kept, <c>/</c> escaped.</summary>
    public static string JsonString(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (var character in value)
        {
            switch (character)
            {
                case '"': builder.Append("\\\""); break;
                case '\\': builder.Append("\\\\"); break;
                case '/': builder.Append("\\/"); break;
                case '\n': builder.Append("\\n"); break;
                case '\r': builder.Append("\\r"); break;
                case '\t': builder.Append("\\t"); break;
                case '\b': builder.Append("\\b"); break;
                case '\f': builder.Append("\\f"); break;
                default:
                    if (character < 0x20) builder.Append("\\u").Append(((int)character).ToString("x4", CultureInfo.InvariantCulture));
                    else builder.Append(character);
                    break;
            }
        }
        return builder.Append('"').ToString();
    }

    public static string JsonOptionalString(string? value) => value == null ? "null" : JsonString(value);

    /// <summary><c>{"index":i,"owner":…,"text":…}</c> with sorted keys, the existing-task entry of both prompts.</summary>
    public static string TaskEntry(int index, string text, string? owner) =>
        "{\"index\":" + index.ToString(CultureInfo.InvariantCulture) + ",\"owner\":" + JsonOptionalString(owner) + ",\"text\":" + JsonString(text) + "}";
}
