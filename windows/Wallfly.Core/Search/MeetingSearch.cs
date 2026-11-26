using System.Globalization;
using System.Text;

namespace Wallfly.Core.Search;

public enum MeetingSearchSource { Title, Notes, Enhanced, Transcript }

/// <summary>A half-open character range in the original passage text.</summary>
public readonly record struct SearchRange(int Start, int Length)
{
    public int End => Start + Length;
}

/// <summary>One searchable unit: the title, a note line or a transcript turn.</summary>
public sealed record SearchPassage(string Id, MeetingSearchSource Source, string Text, double? Offset = null, Guid? SegmentId = null)
{
    public string Label => Source switch
    {
        MeetingSearchSource.Title => "Title",
        MeetingSearchSource.Notes => "My notes",
        MeetingSearchSource.Enhanced => "Enhanced notes",
        _ => "Transcript"
    } + (Offset is double offset ? " · " + Meeting.Timestamp(offset) : "");
}

public sealed record MeetingSearchHit(SearchPassage Passage, IReadOnlyList<SearchRange> Ranges)
{
    /// <summary>45 characters before the first match and 110 after it, with an ellipsis where cut.</summary>
    public string Snippet => MeetingSearch.Snippet(Passage.Text, Ranges);
}

public sealed record MeetingSearchResult(Guid Id, IReadOnlyList<MeetingSearchHit> Hits, DateTimeOffset Date)
{
    public bool TitleMatch => Hits.Any(hit => hit.Passage.Source == MeetingSearchSource.Title);
}

/// <summary>
/// Library search of spec 8: terms are split at whitespace and compared case- and accent-insensitively;
/// <b>all</b> terms must occur in the <b>same</b> source (title, notes, enhanced notes or one transcript
/// turn). Hits carry coordinates of the current text, never of persisted data.
/// </summary>
public static class MeetingSearch
{
    public const int SnippetBefore = 45;
    public const int SnippetAfter = 110;

    public static IReadOnlyList<string> Terms(string? query) =>
        (query ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(term => term.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    /// <summary>NFD, drop combining marks, lowercase — with a map back to the original indices.</summary>
    public static (string Text, int[] Map) Fold(string text)
    {
        var builder = new StringBuilder(text.Length);
        var map = new List<int>(text.Length);
        for (var index = 0; index < text.Length;)
        {
            var length = char.IsHighSurrogate(text[index]) && index + 1 < text.Length && char.IsLowSurrogate(text[index + 1]) ? 2 : 1;
            // A lone surrogate cannot be normalized; it is kept as it is so the index map stays exact.
            if (length == 1 && char.IsSurrogate(text[index]))
            {
                builder.Append(text[index]); map.Add(index); index++;
                continue;
            }
            foreach (var character in text.Substring(index, length).Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark) continue;
                builder.Append(char.ToLowerInvariant(character));
                map.Add(index);
            }
            index += length;
        }
        return (builder.ToString(), [.. map]);
    }

    /// <summary>Merged match ranges of every term, in coordinates of <paramref name="text"/>.</summary>
    public static IReadOnlyList<SearchRange> Ranges(string text, IReadOnlyList<string> terms)
    {
        var (folded, map) = Fold(text);
        var found = new List<SearchRange>();
        foreach (var term in terms)
        {
            var needle = Fold(term).Text;
            if (needle.Length == 0) continue;
            var start = 0;
            while (start < folded.Length)
            {
                var position = folded.IndexOf(needle, start, StringComparison.Ordinal);
                if (position < 0) break;
                var originalStart = map[position];
                var originalEnd = position + needle.Length < map.Length ? map[position + needle.Length] : text.Length;
                found.Add(new(originalStart, Math.Max(1, originalEnd - originalStart)));
                start = position + needle.Length;
            }
        }
        var merged = new List<SearchRange>();
        foreach (var range in found.OrderBy(range => range.Start).ThenBy(range => range.Length))
        {
            if (merged.Count > 0 && merged[^1].End >= range.Start)
            {
                var last = merged[^1];
                merged[^1] = new(last.Start, Math.Max(last.End, range.End) - last.Start);
            }
            else merged.Add(range);
        }
        return merged;
    }

    public static string Snippet(string text, IReadOnlyList<SearchRange> ranges)
    {
        if (ranges.Count == 0) return text;
        var first = ranges[0];
        var start = Math.Max(0, first.Start - SnippetBefore);
        var end = Math.Min(text.Length, first.End + SnippetAfter);
        while (start > 0 && IsContinuation(text, start)) start--;
        while (end < text.Length && IsContinuation(text, end)) end++;
        var body = text[start..end].Replace('\n', ' ').Replace('\r', ' ');
        return (start > 0 ? "…" : "") + body + (end < text.Length ? "…" : "");
    }

    private static bool IsContinuation(string text, int index) =>
        index > 0 && index < text.Length
        && (char.IsLowSurrogate(text[index])
            || CharUnicodeInfo.GetUnicodeCategory(text, index) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            || text[index] == '‍' || text[index - 1] == '‍' || text[index] == '️');

    public static IReadOnlyList<SearchPassage> Passages(Meeting meeting)
    {
        var passages = new List<SearchPassage> { new("title", MeetingSearchSource.Title, meeting.Title) };
        AddLines(passages, MeetingSearchSource.Notes, meeting.Notes);
        AddLines(passages, MeetingSearchSource.Enhanced, meeting.DisplayedEnhancedNotes);
        var index = 0;
        foreach (var turn in Turns(meeting))
            passages.Add(new($"transcript-{index++}", MeetingSearchSource.Transcript, turn.Text, turn.Offset, turn.SegmentId));
        return passages;
    }

    private static void AddLines(List<SearchPassage> passages, MeetingSearchSource source, string text)
    {
        var lines = (text ?? "").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line.Trim().Length == 0) continue;
            passages.Add(new($"{source.ToString().ToLowerInvariant()}-{index}", source, line));
        }
    }

    /// <summary>Consecutive passages of the same channel and participant, as the transcript shows them.</summary>
    private static List<(double Offset, Guid SegmentId, string Text)> Turns(Meeting meeting)
    {
        var turns = new List<(double Offset, Guid SegmentId, string Text)>();
        Speaker? speaker = null;
        Guid? participant = null;
        foreach (var segment in meeting.OrderedTranscript)
        {
            if (turns.Count > 0 && speaker == segment.Speaker && participant == segment.ParticipantID)
                turns[^1] = turns[^1] with { Text = turns[^1].Text + " " + segment.Text };
            else turns.Add((segment.Offset, segment.Id, segment.Text));
            speaker = segment.Speaker; participant = segment.ParticipantID;
        }
        return turns;
    }

    public static bool Matches(Meeting meeting, string? query)
    {
        var terms = Terms(query);
        return terms.Count == 0 || Hits(meeting, terms).Count > 0;
    }

    private static List<MeetingSearchHit> Hits(Meeting meeting, IReadOnlyList<string> terms)
    {
        var passages = Passages(meeting);
        var hits = new List<MeetingSearchHit>();
        foreach (var source in Enum.GetValues<MeetingSearchSource>())
        {
            var parts = passages.Where(passage => passage.Source == source).ToList();
            if (parts.Count == 0) continue;
            var text = string.Join('\n', parts.Select(passage => passage.Text));
            if (!terms.All(term => Ranges(text, [term]).Count > 0)) continue;
            foreach (var part in parts)
            {
                var matches = Ranges(part.Text, terms);
                if (matches.Count > 0) hits.Add(new(part, matches));
            }
        }
        return hits;
    }

    public static IReadOnlyList<MeetingSearchResult> Search(IEnumerable<Meeting> meetings, string? query)
    {
        var terms = Terms(query);
        if (terms.Count == 0) return [];
        var results = new List<MeetingSearchResult>();
        foreach (var meeting in meetings)
        {
            var hits = Hits(meeting, terms);
            if (hits.Count > 0) results.Add(new(meeting.Id, hits, meeting.StartedAt ?? meeting.CreatedAt));
        }
        return results
            .OrderByDescending(result => result.TitleMatch)
            .ThenByDescending(result => result.Date)
            .ThenBy(result => result.Id.ToString("D").ToUpperInvariant(), StringComparer.Ordinal)
            .ToList();
    }
}
