using Wallfly.Core;

namespace Wallfly.Mcp;

/// <summary>
/// One displayed transcript run: consecutive words of one segment with the same participant, or a whole
/// segment without word times. Property names are the JSON keys of the macOS <c>ReadMeeting.Run</c>.
/// </summary>
public sealed record TranscriptRun(
    Guid SegmentID, int? WordStart, int? WordEnd, Speaker Source, Guid? ParticipantID,
    string Speaker, double Offset, string Text, bool IsFinal);

/// <summary>
/// Port of <c>ReadMeeting.runs</c> / <c>orderByUtterance</c> (WallflyReadCore, macOS 1.13.0), the same view the
/// app's transcript shows: words carry their own participant, timed blocks of one channel and participant are
/// joined across gaps under 0.7 s, and a short backchannel (at most 1.2 s and 3 words) inside the other
/// channel's block is shown right after that block. Self-contained like WallflyReadCore, so the read-only
/// helper depends only on the persisted shape, not on app services.
/// </summary>
public static class TranscriptRuns
{
    private const double JoinGap = 0.7;
    private const double BackchannelDuration = 1.2;
    private const int BackchannelWords = 3;

    private sealed class DisplayItem(TranscriptRun turn, double end, int words, bool timed, double sortKey)
    {
        public TranscriptRun Turn { get; } = turn;
        public double End { get; } = end;
        public int Words { get; } = words;
        public bool Timed { get; } = timed;
        public int Block { get; set; }
        public double SortKey { get; set; } = sortKey;
        public double Start => Turn.Offset;
    }

    private sealed class Block(Guid? participantID, double start, double end, int words, bool timed, int member)
    {
        public Guid? ParticipantID { get; } = participantID;
        public double Start { get; } = start;
        public double End { get; set; } = end;
        public int Words { get; set; } = words;
        public bool Timed { get; } = timed;
        public List<int> Members { get; } = [member];
    }

    private static string Upper(Guid id) => id.ToString("D").ToUpperInvariant();

    public static IReadOnlyList<TranscriptRun> Build(Meeting meeting)
    {
        var participants = (meeting.Participants ?? []).Where(person => person is not null).ToList();
        var items = new List<DisplayItem>();
        var segments = (meeting.Transcript ?? []).Where(segment => segment is not null)
            .OrderBy(segment => segment.Offset).ThenBy(segment => Upper(segment.Id), StringComparer.Ordinal);
        foreach (var segment in segments)
        {
            var isMe = segment.Speaker == Speaker.Me;
            string Name(Guid? id) => isMe ? "Me" : participants.FirstOrDefault(person => person.Id == id)?.Name ?? "Others";
            // Windows only: a Me word named by the microphone pass ("Speaker k") or manually shows under that participant.
            MeetingParticipant? MeWordParticipant(TranscriptWord word) => isMe && TranscriptDisplay.NamesMeWord(word) && word.ParticipantID is Guid known
                ? participants.FirstOrDefault(person => person.Id == known) : null;
            var words = segment.Words;
            if (words is { Count: > 0 })
            {
                for (var index = 0; index < words.Count; index++)
                {
                    var word = words[index];
                    var text = word?.Text?.Trim() ?? "";
                    if (word is null || text.Length == 0) continue;
                    var named = MeWordParticipant(word);
                    var run = new TranscriptRun(segment.Id, index, index + 1, segment.Speaker, isMe ? named?.Id : word.ParticipantID,
                        named?.Name ?? Name(word.ParticipantID), segment.Offset + word.Start, text, segment.IsFinal);
                    items.Add(new DisplayItem(run, Math.Max(run.Offset, segment.Offset + word.End), 1, true, run.Offset));
                }
            }
            else
            {
                var text = segment.Text?.Trim() ?? "";
                if (text.Length == 0) continue;
                var run = new TranscriptRun(segment.Id, null, null, segment.Speaker, isMe ? null : segment.ParticipantID,
                    Name(segment.ParticipantID), segment.Offset, text, segment.IsFinal);
                items.Add(new DisplayItem(run, segment.Offset + Math.Max(0, segment.Duration),
                    text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, false, segment.Offset));
            }
        }
        OrderByUtterance(items);
        var ordered = items
            .OrderBy(item => item.SortKey)
            .ThenBy(item => item.Block)
            .ThenBy(item => item.Start)
            .ThenBy(item => Upper(item.Turn.SegmentID), StringComparer.Ordinal)
            .ThenBy(item => item.Turn.WordStart ?? -1);
        var output = new List<TranscriptRun>();
        foreach (var item in ordered)
        {
            var run = item.Turn;
            if (output.Count > 0 && output[^1] is var last && last.SegmentID == run.SegmentID && last.Source == run.Source
                && last.ParticipantID == run.ParticipantID && run.WordStart is int start && last.WordEnd == start)
                output[^1] = last with { Text = last.Text + " " + run.Text, WordEnd = run.WordEnd };
            else output.Add(run);
        }
        return output;
    }

    private static void OrderByUtterance(List<DisplayItem> items)
    {
        List<Block> Blocks(bool me)
        {
            var result = new List<Block>();
            var members = Enumerable.Range(0, items.Count)
                .Where(index => (items[index].Turn.Source == Speaker.Me) == me)
                .OrderBy(index => items[index].Start);
            foreach (var index in members)
            {
                var item = items[index];
                if (result.Count > 0 && result[^1] is var last && last.Timed && item.Timed
                    && last.ParticipantID == item.Turn.ParticipantID && item.Start - last.End < JoinGap)
                {
                    last.End = Math.Max(last.End, item.End);
                    last.Words += item.Words;
                    last.Members.Add(index);
                }
                else result.Add(new Block(item.Turn.ParticipantID, item.Start, item.End, item.Words, item.Timed, index));
            }
            return result;
        }

        var me = Blocks(true);
        var others = Blocks(false);
        var number = 0;
        foreach (var block in me.Concat(others))
        {
            foreach (var index in block.Members) items[index].Block = number;
            number++;
        }
        foreach (var (guests, hosts) in new[] { (me, others), (others, me) })
        {
            // Walk backwards so the next block's own (possibly deferred) position is known: a deferred
            // backchannel never moves past the next block of its own channel.
            var nextKey = double.PositiveInfinity;
            for (var position = guests.Count - 1; position >= 0; position--)
            {
                var guest = guests[position];
                var key = guest.Start;
                if (guest.End - guest.Start <= BackchannelDuration + 1e-9 && guest.Words <= BackchannelWords)
                {
                    // Hosts are ordered by start, so only the last host starting before the guest can contain it.
                    int low = 0, high = hosts.Count;
                    while (low < high)
                    {
                        var mid = (low + high) / 2;
                        if (hosts[mid].Start < guest.Start) low = mid + 1; else high = mid;
                    }
                    if (low > 0 && hosts[low - 1].End > guest.End) key = Math.Max(guest.Start, Math.Min(hosts[low - 1].End, nextKey));
                }
                foreach (var index in guest.Members) items[index].SortKey = key;
                nextKey = key;
            }
        }
    }
}
