using System.Text.Json;
using Wallfly.Audio;

namespace Wallfly.Transcription.Deepgram;

public readonly record struct DeepgramWord(string Word, string? PunctuatedWord, double Start, double End)
{
    public string DisplayText => PunctuatedWord ?? Word;
}

public sealed record DeepgramAlternative(string Transcript, IReadOnlyList<DeepgramWord>? Words);

/// <summary>
/// One server message (port of the macOS <c>DeepgramResult</c>). Only the fields the pipeline reads
/// are kept; other fields (<c>channel_index</c>, <c>metadata</c>, <c>confidence</c>, <c>from_finalize</c>,
/// <c>request_id</c> …) are ignored. <see cref="SpeechFinal"/> is parsed but never used: finality is
/// <see cref="IsFinal"/> alone.
/// </summary>
public sealed record DeepgramResult(
    string Type,
    double? Start = null,
    double? Duration = null,
    bool? IsFinal = null,
    bool? SpeechFinal = null,
    IReadOnlyList<DeepgramAlternative>? Alternatives = null)
{
    public DeepgramAlternative? FirstAlternative => Alternatives is { Count: > 0 } list ? list[0] : null;
}

/// <summary>
/// Decodes server messages with the strictness of the Swift <c>Decodable</c>: <c>type</c> is required,
/// a present field of the wrong JSON type is an error, <c>null</c> counts as absent for optional fields,
/// and a present <c>channel</c> needs <c>alternatives</c> with a <c>transcript</c> each.
/// </summary>
public static class DeepgramResultParser
{
    public static bool TryParse(ReadOnlySpan<byte> utf8, out DeepgramResult? result)
    {
        result = null;
        try
        {
            var reader = new Utf8JsonReader(utf8);
            using var document = JsonDocument.ParseValue(ref reader);
            result = Parse(document.RootElement);
            return true;
        }
        catch (Exception error) when (error is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            result = null;
            return false;
        }
    }

    public static bool TryParse(string json, out DeepgramResult? result) => TryParse(System.Text.Encoding.UTF8.GetBytes(json), out result);

    private static DeepgramResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new FormatException("A message must be an object.");
        var type = RequiredString(root, "type");
        List<DeepgramAlternative>? alternatives = null;
        if (Present(root, "channel", out var channel))
        {
            if (channel.ValueKind != JsonValueKind.Object) throw new FormatException("channel");
            if (!channel.TryGetProperty("alternatives", out var list) || list.ValueKind != JsonValueKind.Array) throw new FormatException("alternatives");
            alternatives = [];
            foreach (var alternative in list.EnumerateArray())
            {
                if (alternative.ValueKind != JsonValueKind.Object) throw new FormatException("alternative");
                List<DeepgramWord>? words = null;
                if (Present(alternative, "words", out var wordList))
                {
                    if (wordList.ValueKind != JsonValueKind.Array) throw new FormatException("words");
                    words = [];
                    foreach (var word in wordList.EnumerateArray())
                    {
                        if (word.ValueKind != JsonValueKind.Object) throw new FormatException("word");
                        words.Add(new(RequiredString(word, "word"), OptionalString(word, "punctuated_word"),
                            RequiredNumber(word, "start"), RequiredNumber(word, "end")));
                    }
                }
                alternatives.Add(new(RequiredString(alternative, "transcript"), words));
            }
        }
        return new(type, OptionalNumber(root, "start"), OptionalNumber(root, "duration"),
            OptionalBool(root, "is_final"), OptionalBool(root, "speech_final"), alternatives);
    }

    private static bool Present(JsonElement element, string name, out JsonElement value) =>
        element.TryGetProperty(name, out value) && value.ValueKind != JsonValueKind.Null;

    private static string RequiredString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString()! : throw new FormatException(name);

    private static string? OptionalString(JsonElement element, string name) =>
        !Present(element, name, out var value) ? null : value.ValueKind == JsonValueKind.String ? value.GetString() : throw new FormatException(name);

    private static double RequiredNumber(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.Number ? value.GetDouble() : throw new FormatException(name);

    private static double? OptionalNumber(JsonElement element, string name) =>
        !Present(element, name, out var value) ? null : value.ValueKind == JsonValueKind.Number ? value.GetDouble() : throw new FormatException(name);

    private static bool? OptionalBool(JsonElement element, string name) =>
        !Present(element, name, out var value) ? null
        : value.ValueKind == JsonValueKind.True ? true
        : value.ValueKind == JsonValueKind.False ? false
        : throw new FormatException(name);
}

/// <summary>
/// Turns the results of one connection into segments (port of the macOS <c>DeepgramResultAssembler</c>).
/// Only this connection's provisional tail is replaceable. A final is identified by its audio interval
/// in samples, never by its text, so identical utterances on independent sources survive; later results
/// inside an already final interval are dropped, and a final can confirm only the prefix of the preview.
/// </summary>
public sealed class DeepgramResultAssembler(AudioSource source, Guid generation, double anchor, Guid? sessionId = null)
{
    private readonly List<(long Lower, long Upper)> finalized = [];
    private TranscriptionSegment? preview;

    public AudioSource Source { get; } = source;
    public Guid Generation { get; } = generation;
    /// <summary>Session offset of the first audio sample sent on this connection.</summary>
    public double Anchor { get; } = anchor;
    /// <summary>Connection-relative end of the latest final result.</summary>
    public double ConfirmedThrough { get; private set; }

    /// <summary>Withdraws the preview with an empty final of the same id; the merger then removes it.</summary>
    public List<TranscriptionSegment> ClearPreview()
    {
        if (preview is not { } old) return [];
        preview = null;
        return [Copy(old, text: "", words: null, isFinal: true)];
    }

    public List<TranscriptionSegment> Accept(DeepgramResult result, double sentDuration)
    {
        if (result.Type != "Results" || result.Start is not double start || result.Duration is not double duration
            || !double.IsFinite(start) || !double.IsFinite(duration) || start < 0 || duration < 0
            || start + duration > sentDuration + .02 || result.FirstAlternative is not { } alternative) return [];
        var end = start + duration;
        var range = (Lower: Samples(start * 16_000), Upper: Samples(end * 16_000));
        var final = result.IsFinal == true;
        if (finalized.Any(interval => interval.Lower <= range.Lower && interval.Upper >= range.Upper)) return [];
        var allWords = alternative.Words ?? [];
        var words = allWords.Where(word =>
            double.IsFinite(word.Start) && double.IsFinite(word.End) && word.Start >= start - .02 && word.End >= word.Start &&
            word.End <= end + .02 && !finalized.Any(interval => Contains(interval, Samples((word.Start + word.End) * 8_000)))).ToList();
        // Do not allow a text-only late preview to cover a previously finalized interval.
        if (words.Count == 0 && alternative.Transcript.Length > 0 && finalized.Any(interval => Overlaps(interval, range))) return [];
        var filtered = words.Count != allWords.Count;
        var segmentStart = filtered ? (words.Count > 0 ? words[0].Start : start) : start;
        var text = filtered ? string.Join(' ', words.Select(word => word.DisplayText)) : alternative.Transcript;
        var oldPreview = preview;
        var emitted = ClearPreview();
        var segment = new TranscriptionSegment
        {
            Id = !final && oldPreview != null && oldPreview.Offset == Anchor + segmentStart ? oldPreview.Id : Guid.NewGuid(),
            Source = Source,
            Offset = Anchor + segmentStart,
            Text = text,
            Duration = Math.Max(0, end - segmentStart),
            IsFinal = final,
            Words = words.Count == 0 ? null : words.Select(word =>
                new TranscriptionWord(word.DisplayText, Math.Max(0, word.Start - segmentStart), Math.Max(0, word.End - segmentStart))).ToList(),
            ConnectionId = Generation,
            SessionId = sessionId
        };
        if (final)
        {
            finalized.Add(range);
            ConfirmedThrough = Math.Max(ConfirmedThrough, end);
            if (text.Length > 0) emitted.Add(segment);
            // A final can confirm only the prefix of the currently displayed preview.
            if (oldPreview is { Words: { } previousWords })
            {
                var remaining = previousWords.Where(word => oldPreview.Offset + word.Start >= Anchor + end - .0001).ToList();
                if (remaining.Count > 0)
                {
                    var shift = remaining[0].Start;
                    var tail = new TranscriptionSegment
                    {
                        Id = Guid.NewGuid(),
                        Source = oldPreview.Source,
                        Offset = oldPreview.Offset + shift,
                        Duration = Math.Max(0, oldPreview.Duration - shift),
                        Words = remaining.Select(word => word with { Start = word.Start - shift, End = word.End - shift }).ToList(),
                        Text = string.Join(' ', remaining.Select(word => word.Text)),
                        IsFinal = oldPreview.IsFinal,
                        ConnectionId = oldPreview.ConnectionId,
                        SessionId = oldPreview.SessionId
                    };
                    preview = tail;
                    emitted.Add(tail);
                }
            }
        }
        else if (text.Length > 0)
        {
            preview = segment;
            emitted.Add(segment);
        }
        return emitted;
    }

    /// <summary>Swift <c>Int(x.rounded())</c>: round half away from zero.</summary>
    private static long Samples(double value) => (long)Math.Round(value, MidpointRounding.AwayFromZero);

    private static bool Contains((long Lower, long Upper) interval, long value) => interval.Lower <= value && value < interval.Upper;

    /// <summary>Swift <c>Range.overlaps</c>: empty ranges overlap nothing.</summary>
    private static bool Overlaps((long Lower, long Upper) a, (long Lower, long Upper) b) =>
        !(b.Upper <= a.Lower || a.Upper <= b.Lower || a.Lower == a.Upper || b.Lower == b.Upper);

    private static TranscriptionSegment Copy(TranscriptionSegment old, string text, IReadOnlyList<TranscriptionWord>? words, bool isFinal) => new()
    {
        Id = old.Id, Source = old.Source, Offset = old.Offset, Duration = old.Duration, Text = text,
        IsFinal = isFinal, Words = words, ConnectionId = old.ConnectionId, SessionId = old.SessionId
    };
}
