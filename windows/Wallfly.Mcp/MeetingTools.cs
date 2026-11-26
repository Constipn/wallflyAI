using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Wallfly.Core;
using Wallfly.Core.Json;
using Wallfly.Core.Search;
using Wallfly.Core.Text;

namespace Wallfly.Mcp;

/// <summary>
/// The three read-only tools, ported from <c>MeetingTools.swift</c> (WallflyReadCore, macOS 1.13.0): same tool
/// names, arguments, validation, ordering, pagination and result keys (sorted). Every value is an explicit
/// projection, so unknown persisted fields, audio paths and settings never reach a response.
/// <para>Windows extensions (additive; a client written for the Mac never sends or needs them):
/// <c>search_meetings.folder</c> filters by Wallfly folder (name or ID) and every meeting result carries its
/// <c>folder</c> name; <c>get_transcript.format = "text"</c> returns the page as <c>[mm:ss] Name: Text</c>
/// lines in <c>text</c> instead of the structured <c>results</c>.</para>
/// </summary>
public sealed partial class MeetingTools(LibraryReader library)
{
    public const string SearchMeetings = "search_meetings";
    public const string GetMeeting = "get_meeting";
    public const string GetTranscript = "get_transcript";
    public static readonly string[] Names = [SearchMeetings, GetMeeting, GetTranscript];

    private const int SnippetBefore = 45;
    private const int SnippetAfter = 110;
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    public static readonly JsonSerializerOptions Output = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    [GeneratedRegex(@"^\d{4}-\d{2}-\d{2}T\d{2}:\d{2}:\d{2}(\.\d{1,7})?(Z|[+-]\d{2}:\d{2})$", RegexOptions.CultureInvariant)]
    private static partial Regex InternetDateTime();

    public LibraryReader Library { get; } = library;

    public JsonObject Call(string tool, IReadOnlyDictionary<string, JsonElement>? arguments)
    {
        // JSON null counts as an omitted argument (several clients send null for unset optional parameters).
        var a = (arguments ?? new Dictionary<string, JsonElement>())
            .Where(pair => pair.Value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        string[] allowed = tool switch
        {
            SearchMeetings => ["query", "from", "to", "participant", "limit", "cursor", "folder"],
            GetMeeting => ["meeting_id"],
            GetTranscript => ["meeting_id", "start", "end", "limit", "cursor", "format"],
            _ => throw Invalid()
        };
        if (a.Keys.Any(key => !allowed.Contains(key, StringComparer.Ordinal))) throw Invalid();

        string? Text(string key)
        {
            if (!a.TryGetValue(key, out var value)) return null;
            return value.ValueKind == JsonValueKind.String ? value.GetString() : throw Invalid();
        }
        double? Number(string key)
        {
            if (!a.TryGetValue(key, out var value)) return null;
            return value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number) && double.IsFinite(number) ? number : throw Invalid();
        }

        var maximum = tool == GetTranscript ? 500 : 100;
        var rawLimit = Number("limit") ?? (tool == GetTranscript ? 100 : 20);
        if (rawLimit < 1 || rawLimit > maximum || Math.Round(rawLimit) != rawLimit) throw Invalid();
        var limit = (int)rawLimit;
        var snapshot = Library.Snapshot();
        var signature = Signature(tool, a, snapshot.Revision);
        var offset = 0;
        if (Text("cursor") is { } cursor)
        {
            string decoded;
            try { decoded = StrictUtf8.GetString(Convert.FromBase64String(cursor)); }
            catch (Exception error) when (error is FormatException or ArgumentException) { throw Invalid(); }
            var parts = decoded.Split(':');
            if (parts.Length != 2 || parts[0] != signature
                || !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out offset))
                throw new ReadException(ReadFailure.StaleCursor);
        }

        JsonObject Page(List<JsonNode> values, string key = "results", Func<List<JsonNode>, JsonNode>? render = null)
        {
            if (offset > values.Count) throw new ReadException(ReadFailure.StaleCursor);
            var end = Math.Min(values.Count, offset + limit);
            var page = values.GetRange(offset, end - offset);
            var next = end < values.Count ? Convert.ToBase64String(Encoding.UTF8.GetBytes($"{signature}:{end}")) : null;
            return Sorted(
                (key, render is null ? new JsonArray([.. page]) : render(page)),
                ("next_cursor", next),
                ("incomplete", snapshot.Skipped > 0),
                ("skipped_meetings", snapshot.Skipped));
        }

        if (tool == SearchMeetings)
        {
            DateTimeOffset? Date(string key)
            {
                if (Text(key) is not { } value) return null;
                return InternetDateTime().IsMatch(value)
                    && DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.None, out var date) ? date : throw Invalid();
            }
            var from = Date("from");
            var to = Date("to");
            if (from > to) throw Invalid();
            var participant = Text("participant");
            var terms = MeetingSearch.Terms(Text("query") ?? "");
            var folder = Text("folder") is { } folderArgument ? ResolveFolder(snapshot, folderArgument) : (Guid?)null;
            var results = new List<(bool Title, DateTimeOffset Date, string Id, JsonNode Value)>();
            foreach (var meeting in snapshot.Meetings)
            {
                var m = meeting.Meeting;
                if (from is { } lower && meeting.Date < lower) continue;
                if (to is { } upper && meeting.Date > upper) continue;
                if (participant is not null && !(m.Participants ?? []).Any(person => person is not null
                        && MeetingSearch.Ranges(person.Name ?? "", [participant]).Count > 0)) continue;
                if (folder is { } folderID && snapshot.Folders?.FolderID(m.Id) != folderID) continue;
                var hits = terms.Count == 0 ? [] : Hits(meeting, terms);
                if (terms.Count > 0 && hits.Count == 0) continue;
                var id = Upper(m.Id);
                results.Add((hits.Any(hit => hit["source"]!.GetValue<string>() == "title"), meeting.Date, id, Sorted(
                    ("meeting_id", id),
                    ("title", m.Title),
                    ("date", Iso(meeting.Date)),
                    ("hits", new JsonArray([.. hits.Take(3)])),
                    ("folder", snapshot.FolderName(m.Id)))));
            }
            return Page(results
                .OrderByDescending(result => result.Title)
                .ThenByDescending(result => result.Date)
                .ThenBy(result => result.Id, StringComparer.Ordinal)
                .Select(result => result.Value).ToList());
        }

        if (Text("meeting_id") is not { } raw || !Guid.TryParse(raw, out var meetingID)) throw Invalid();
        var found = snapshot.Meetings.FirstOrDefault(meeting => meeting.Id == meetingID);
        if (found is null)
            throw new ReadException(snapshot.Damaged.Contains(meetingID) ? ReadFailure.DamagedMeeting : ReadFailure.NotFound);
        var details = found.Meeting;
        if (tool == GetMeeting)
        {
            return Sorted(
                ("meeting_id", Upper(details.Id)),
                ("title", details.Title),
                ("date", Iso(found.Date)),
                ("duration", details.Duration),
                ("notes", details.Notes),
                ("enhanced_notes", TimestampCitations.Strip(details.EnhancedNotes)),
                ("participants", new JsonArray([.. (details.Participants ?? []).Where(person => person is not null)
                    .Select(person => (JsonNode)Sorted(("id", Upper(person.Id)), ("name", person.Name ?? "")))])),
                ("tasks", new JsonArray([.. (details.Tasks ?? []).Where(task => task is not null).Select(Task)])),
                ("folder", snapshot.FolderName(details.Id)));
        }

        var start = Number("start") ?? 0;
        var finish = Number("end") ?? double.MaxValue;
        if (start < 0 || finish < start) throw Invalid();
        var format = Text("format") ?? "segments";
        if (format is not ("segments" or "text")) throw Invalid();
        var runs = found.Runs.Where(run => run.Offset >= start && run.Offset < finish).ToList();
        var result = format == "text"
            ? Page(runs.Select(run => (JsonNode)RunNode(run)).ToList(), "text", page => Lines(page))
            : Page(runs.Select(run => (JsonNode)RunNode(run)).ToList());
        result["meeting_id"] = Upper(details.Id);
        result["transcription_gaps"] = new JsonArray([.. (details.TranscriptionGaps ?? [])
            .Where(gap => gap is not null && gap.Start + gap.Duration >= start && gap.Start < finish)
            .Select(gap => (JsonNode)Sorted(("start", gap.Start), ("duration", gap.Duration), ("reason", gap.Reason ?? ""),
                ("speaker", RawValueEnumConverter<Speaker>.RawValue(gap.Speaker))))]);
        return SortedCopy(result);
    }

    private static ReadException Invalid() => new(ReadFailure.InvalidArguments);

    private static Guid ResolveFolder(LibrarySnapshot snapshot, string argument)
    {
        var folders = snapshot.Folders?.Folders ?? [];
        var name = argument.Trim();
        var match = Guid.TryParse(name, out var id)
            ? folders.FirstOrDefault(folder => folder.Id == id)
            : folders.FirstOrDefault(folder => string.Equals(folder.Name?.Trim(), name, StringComparison.CurrentCultureIgnoreCase));
        return match?.Id ?? throw new ReadException(ReadFailure.FolderNotFound);
    }

    /// <summary>
    /// Hits of macOS <c>search_meetings</c>: every term must occur in the same source (title, notes, enhanced notes
    /// with citations removed, or the transcript); each matching passage yields its first match with 45
    /// characters before and 110 after, widened to whole grapheme clusters.
    /// </summary>
    private static List<JsonNode> Hits(ReadMeeting meeting, IReadOnlyList<string> terms)
    {
        // Folded once per call; the sources' folded text is cached per file version. Same test as
        // MeetingSearch.Ranges(joined, [term]).Count > 0, which also never matches an empty folded term.
        var needles = terms.Select(term => MeetingSearch.Fold(term).Text).ToList();
        var hits = new List<JsonNode>();
        foreach (var source in meeting.Sources)
        {
            if (!needles.All(needle => needle.Length > 0 && source.Folded.Contains(needle, StringComparison.Ordinal))) continue;
            foreach (var (text, run) in source.Passages)
            {
                var ranges = MeetingSearch.Ranges(text, terms);
                if (ranges.Count == 0) continue;
                var pairs = new List<(string, JsonNode?)> { ("source", source.Name), ("text", Excerpt(text, ranges[0])) };
                if (run is not null)
                {
                    pairs.Add(("offset", run.Offset));
                    pairs.Add(("speaker", run.Speaker));
                    pairs.Add(("segment_id", Upper(run.SegmentID)));
                }
                hits.Add(Sorted([.. pairs]));
            }
        }
        return hits;
    }

    /// <summary>The macOS excerpt: no ellipsis, line breaks kept, never splitting a grapheme cluster.</summary>
    internal static string Excerpt(string text, SearchRange match)
    {
        var start = Math.Max(0, match.Start - SnippetBefore);
        var end = Math.Min(text.Length, match.End + SnippetAfter);
        int position = 0, from = 0, to = text.Length;
        while (position < text.Length)
        {
            if (position <= start) from = position;
            if (position >= end) { to = position; break; }
            position += Math.Max(1, StringInfo.GetNextTextElementLength(text.AsSpan(position)));
        }
        return text[from..to];
    }

    private static JsonNode Task(MeetingTask task)
    {
        var pairs = new List<(string, JsonNode?)> { ("id", Upper(task.Id)), ("text", task.Text ?? "") };
        if (task.Owner is not null) pairs.Add(("owner", task.Owner));
        // Same encoding as the macOS helper (Swift JSONEncoder default): seconds since 2001-01-01 UTC.
        if (task.CompletedAt is { } completed) pairs.Add(("completedAt", (completed - AppleDateConverter.Epoch).TotalSeconds));
        if (task.Review is { } review) pairs.Add(("review", RawValueEnumConverter<TaskReview>.RawValue(review)));
        return Sorted([.. pairs]);
    }

    private static JsonObject RunNode(TranscriptRun run)
    {
        var pairs = new List<(string, JsonNode?)>
        {
            ("segmentID", Upper(run.SegmentID)),
            ("source", RawValueEnumConverter<Speaker>.RawValue(run.Source)),
            ("speaker", run.Speaker),
            ("offset", run.Offset),
            ("text", run.Text),
            ("isFinal", run.IsFinal)
        };
        if (run.WordStart is { } wordStart) pairs.Add(("wordStart", wordStart));
        if (run.WordEnd is { } wordEnd) pairs.Add(("wordEnd", wordEnd));
        if (run.ParticipantID is { } participant) pairs.Add(("participantID", Upper(participant)));
        return Sorted([.. pairs]);
    }

    /// <summary>
    /// <c>format = "text"</c>: one <c>[mm:ss] Name: Text</c> line per speaker turn of the page (consecutive runs of the
    /// same channel and participant are joined), the transcript shape of the Markdown export.
    /// </summary>
    private static JsonNode Lines(List<JsonNode> page)
    {
        var lines = new List<(string Source, string? Participant, string Speaker, double Offset, StringBuilder Text)>();
        foreach (var node in page)
        {
            var source = node["source"]!.GetValue<string>();
            var participant = node["participantID"]?.GetValue<string>();
            var speaker = node["speaker"]!.GetValue<string>();
            var text = node["text"]!.GetValue<string>();
            if (lines.Count > 0 && lines[^1].Source == source && lines[^1].Participant == participant && lines[^1].Speaker == speaker)
                lines[^1].Text.Append(' ').Append(text);
            else lines.Add((source, participant, speaker, node["offset"]!.GetValue<double>(), new StringBuilder(text)));
        }
        return JsonValue.Create(string.Join('\n', lines.Select(line => $"[{Meeting.Timestamp(line.Offset)}] {line.Speaker}: {line.Text}")));
    }

    /// <summary>Hash of tool, arguments without the cursor (sorted keys) and library revision; binds a cursor to its query.</summary>
    private static string Signature(string tool, Dictionary<string, JsonElement> arguments, string revision)
    {
        var query = new StringBuilder("{");
        foreach (var pair in arguments.Where(pair => pair.Key != "cursor").OrderBy(pair => pair.Key, StringComparer.Ordinal))
            query.Append(JsonSerializer.Serialize(pair.Key)).Append(':').Append(pair.Value.GetRawText()).Append(',');
        query.Append('}');
        var data = Encoding.UTF8.GetBytes(tool + query + revision);
        return Convert.ToHexStringLower(SHA256.HashData(data));
    }

    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    /// <summary>ISO 8601 in UTC without fractional seconds, like Foundation's default <c>ISO8601DateFormatter</c>.</summary>
    internal static string Iso(DateTimeOffset date) =>
        date.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);

    /// <summary>A JSON object with keys in ordinal order (the macOS helper writes <c>.sortedKeys</c>).</summary>
    internal static JsonObject Sorted(params (string Key, JsonNode? Value)[] pairs)
    {
        var result = new JsonObject();
        foreach (var (key, value) in pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal)) result[key] = value;
        return result;
    }

    private static JsonObject SortedCopy(JsonObject source)
    {
        var pairs = source.Select(pair => (pair.Key, pair.Value)).ToList();
        source.Clear();
        return Sorted([.. pairs]);
    }
}
