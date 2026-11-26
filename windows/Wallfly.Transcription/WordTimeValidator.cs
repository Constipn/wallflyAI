using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Wallfly.Transcription;

/// <summary>
/// 1:1 port of the macOS <c>TranscriptionResult.decode</c> / <c>decodeOpenAI</c> (LocalTranscriptionResult.swift).
/// Unusable timings are rejected without losing or inventing recognized text: any token with a non-finite
/// time, an end before its start, a start below zero, an end more than <see cref="EndToleranceSeconds"/>
/// past the passage, or a start before the previous word drops <em>all</em> words of the passage (the text
/// stays). Zero-length punctuation tokens are appended to the previous word, the joined words must equal the
/// text (with or without separating spaces), and BPE fragments are merged into lexical words.
/// </summary>
public static class WordTimeValidator
{
    public const double EndToleranceSeconds = .05;

    /// <summary>whisper.cpp JSON: <c>text</c> plus, for verbose_json, <c>segments[].words[]</c> token entries.</summary>
    /// <exception cref="FormatException">The payload is not a JSON object with a string <c>text</c>.</exception>
    public static ChunkTranscription Decode(string json, double duration, bool includeWords)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var text = RequireText(root);
        List<JsonElement>? segments = null;
        if (root.TryGetProperty("segments", out var raw)) segments = ObjectArray(raw);
        return Decode(text, segments, duration, includeWords);
    }

    /// <summary>
    /// OpenAI JSON: <c>text</c> plus, for verbose_json with word granularity, a top-level lexical <c>words</c>
    /// array. As on macOS, the words become a single segment and any OpenAI <c>segments</c> field is ignored.
    /// </summary>
    /// <exception cref="FormatException">The payload is not a JSON object with a string <c>text</c>.</exception>
    public static ChunkTranscription DecodeOpenAi(string json, double duration, bool includeWords)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        var text = RequireText(root);
        List<JsonElement> segments = [];
        if (root.TryGetProperty("words", out var words) && ObjectArray(words) != null)
        {
            // Swift: root["segments"] = [["words": words]]
            using var wrapped = JsonDocument.Parse("{\"words\":" + words.GetRawText() + "}");
            segments.Add(wrapped.RootElement.Clone());
        }
        return Decode(text, segments, duration, includeWords);
    }

    private static ChunkTranscription Decode(string rawText, List<JsonElement>? segments, double duration, bool includeWords)
    {
        var text = rawText.Trim();
        var result = new ChunkTranscription(text);
        if (!includeWords || segments == null || segments.Count == 0) return result;
        var words = new List<MutableWord>();
        foreach (var segment in segments)
        {
            if (!segment.TryGetProperty("words", out var tokenArray) || ObjectArray(tokenArray) is not { } tokens) return result;
            foreach (var token in tokens)
            {
                if (!token.TryGetProperty("word", out var wordElement) || wordElement.ValueKind != JsonValueKind.String) return result;
                if (!TryNumber(token, "start", out var start) || !TryNumber(token, "end", out var end)) return result;
                if (!(start >= 0) || !(end >= start) || !(end <= duration + EndToleranceSeconds)) return result;
                if (words.Count > 0 && !(start >= words[^1].Start)) return result;
                var word = wordElement.GetString()!;
                if (word.Length == 0) continue;
                if (start == end && words.Count > 0 && !ContainsAlphanumeric(word)) words[^1].Text += word;
                else words.Add(new(word, start, end));
            }
        }
        if (words.Count == 0) return result;
        if (Normalized(string.Concat(words.Select(word => word.Text))) != Normalized(text))
        {
            // Also accept true word arrays whose entries do not contain leading spaces.
            if (Normalized(string.Join(' ', words.Select(word => word.Text))) != Normalized(text)) return result;
            for (var index = 1; index < words.Count; index++) words[index].Text = " " + words[index].Text;
        }
        // whisper.cpp calls these "words", but some builds return BPE token fragments. Keep a lexical word
        // together so a speaker boundary cannot split "Wiederbeschaffung".
        var lexical = new List<MutableWord>();
        foreach (var word in words)
        {
            if (lexical.Count > 0 && !char.IsWhiteSpace(word.Text[0]) && !char.IsWhiteSpace(lexical[^1].Text[^1]))
            {
                lexical[^1].Text += word.Text;
                lexical[^1].End = Math.Max(lexical[^1].End, word.End);
            }
            else lexical.Add(word);
        }
        return new(text, lexical.Select(word => new TranscriptionWord(word.Text, word.Start, word.End)).ToList());
    }

    /// <summary>Swift <c>split(whereSeparator: \.isWhitespace).joined(separator: " ")</c>.</summary>
    internal static string Normalized(string value) =>
        string.Join(' ', value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary><c>CharacterSet.alphanumerics</c>: Unicode categories L*, M* and N*.</summary>
    internal static bool ContainsAlphanumeric(string value)
    {
        foreach (var rune in value.EnumerateRunes())
        {
            switch (Rune.GetUnicodeCategory(rune))
            {
                case UnicodeCategory.UppercaseLetter or UnicodeCategory.LowercaseLetter or UnicodeCategory.TitlecaseLetter
                    or UnicodeCategory.ModifierLetter or UnicodeCategory.OtherLetter
                    or UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark
                    or UnicodeCategory.DecimalDigitNumber or UnicodeCategory.LetterNumber or UnicodeCategory.OtherNumber:
                    return true;
            }
        }
        return false;
    }

    private static JsonDocument Parse(string json)
    {
        try { return JsonDocument.Parse(json); }
        catch (JsonException error) { throw new FormatException("The transcript is not valid JSON.", error); }
    }

    private static string RequireText(JsonElement root) =>
        root.ValueKind == JsonValueKind.Object && root.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String
            ? text.GetString()!
            : throw new FormatException("The transcript has no text.");

    /// <summary>Swift <c>as? [[String: Any]]</c>: an array whose elements are all objects, otherwise null.</summary>
    private static List<JsonElement>? ObjectArray(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Array) return null;
        var items = new List<JsonElement>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) return null;
            items.Add(item);
        }
        return items;
    }

    private static bool TryNumber(JsonElement token, string name, out double value)
    {
        value = 0;
        return token.TryGetProperty(name, out var element) && element.ValueKind == JsonValueKind.Number
            && element.TryGetDouble(out value) && double.IsFinite(value);
    }

    private sealed class MutableWord(string text, double start, double end)
    {
        public string Text { get; set; } = text;
        public double Start { get; } = start;
        public double End { get; set; } = end;
    }
}
