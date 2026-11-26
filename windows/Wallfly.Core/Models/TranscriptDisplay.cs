namespace Wallfly.Core;

/// <summary>Half-open word index range <c>[Start, End)</c> of one segment (Swift <c>Range&lt;Int&gt;</c>).</summary>
public readonly record struct WordRange(int Start, int End)
{
    public int Count => End - Start;
    public bool IsEmpty => End <= Start;
    public override string ToString() => $"{Start}..<{End}";
}

/// <summary>Points to a whole segment (<see cref="Words"/> null) or to some of its words.</summary>
public readonly record struct TranscriptReference(Guid SegmentID, WordRange? Words = null);

/// <summary>Identity of a displayed turn: its first segment and word (null for the first word or an untimed segment).</summary>
public readonly record struct TranscriptTurnId(Guid SegmentID, int? WordIndex = null);

/// <summary>A presentation of consecutive recognition chunks, preserving their source data (Swift <c>TranscriptTurn</c>).</summary>
public sealed record TranscriptTurn
{
    public required TranscriptTurnId Id { get; init; }
    public required Speaker Speaker { get; init; }
    public required double Offset { get; init; }
    public required string Text { get; set; }
    public required bool IsFinal { get; set; }
    /// <summary>Null for the Me channel unless the microphone pass (or a manual assignment) named another voice on it.</summary>
    public Guid? ParticipantID { get; init; }
    public required List<TranscriptReference> References { get; init; }
}

/// <summary>The display constants of <see cref="TranscriptDisplay"/> (macOS <c>TeamsAttributionPolicy</c> defaults).</summary>
/// <param name="UnitGap">A word gap at least this long ends an utterance block.</param>
/// <param name="BackchannelDuration">A contribution up to this long inside the other channel's block is a backchannel.</param>
/// <param name="BackchannelWords">A backchannel has at most this many words.</param>
public sealed record TranscriptDisplayOptions(double UnitGap = 0.7, double BackchannelDuration = 1.2, int BackchannelWords = 3)
{
    public static TranscriptDisplayOptions Default { get; } = new();
}

/// <summary>
/// The one transcript grouping of the app (spec 4.4; port of <c>Meeting.transcriptRuns</c>, <c>orderByUtterance</c>,
/// <c>conversation</c>, <c>transcriptText</c> and <c>conversationText</c> from SpeakerAttribution.swift / Models.swift). The
/// chat view, the speaker editor (its <see cref="TranscriptReference"/>s) and <c>Wallfly.Speakers.TranscriptTurns</c> all
/// use it. The stored segments stay intact; turns are built from their words. Both channels sort by utterance block, and
/// a short backchannel strictly inside the other channel's block is shown right after that block.
/// </summary>
public static class TranscriptDisplay
{
    /// <summary>One displayed word (or untimed chunk) with its audio extent, used only to order the view.</summary>
    private sealed class DisplayItem(TranscriptTurn turn, double end, int words, bool timed)
    {
        public TranscriptTurn Turn { get; } = turn;
        public double End { get; } = end;
        public int Words { get; } = words;
        /// <summary>Only timed words join neighbours into an utterance block; an untimed chunk is a block of its own.</summary>
        public bool Timed { get; } = timed;
        /// <summary>Start of the item's utterance block, or the later position a deferred backchannel is shown at.</summary>
        public double SortKey { get; set; } = turn.Offset;
        /// <summary>Keeps the words of one block together when two blocks share a sort key.</summary>
        public int Block { get; set; }
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

    /// <summary>
    /// First meeting-wide acoustic identity of the microphone pass (Windows, AP5): cluster <c>k</c> of the Me channel is
    /// <c>TranscriptWord.AcousticSpeaker</c> <c>1000 + k</c>.
    /// </summary>
    public const int MicrophoneSpeakerBase = 1000;

    /// <summary>
    /// <c>SourceKey</c> prefix of a synthetic "Speaker k" participant that stands for an unnamed acoustic identity
    /// (<c>AcousticSpeakerLabels</c>): an unnamed voice, never a name.
    /// </summary>
    public const string AcousticSourceKeyPrefix = "acoustic/";

    /// <summary>
    /// True when a Me word's participant is shown: the microphone pass named the word's voice (a microphone identity) or the
    /// user assigned it manually.
    /// </summary>
    public static bool NamesMeWord(TranscriptWord word) =>
        word.AcousticSpeaker >= MicrophoneSpeakerBase || word.Attribution == WordAttribution.Manual;

    /// <summary>Runs: per segment, consecutive words with the same participant (Swift <c>transcriptRuns</c>).</summary>
    public static List<TranscriptTurn> Runs(Meeting meeting, TranscriptDisplayOptions? options = null)
    {
        // A Me word shows under a participant only when the microphone pass gave it another voice ("Speaker k") or it was
        // assigned manually; any other participant on a Me word (older data) is ignored, as on macOS.
        var known = (meeting.Participants ?? []).Select(person => person.Id).ToHashSet();
        Guid? MeWord(TranscriptWord word) => NamesMeWord(word) && word.ParticipantID is Guid id && known.Contains(id) ? id : null;
        var items = new List<DisplayItem>();
        foreach (var segment in Ordered(meeting.Transcript))
        {
            if (segment.Words is { Count: > 0 } words)
            {
                for (var index = 0; index < words.Count; index++)
                {
                    var word = words[index];
                    var text = word.Text.Trim();
                    if (text.Length == 0) continue;
                    var start = segment.Offset + word.Start;
                    items.Add(new DisplayItem(new TranscriptTurn
                    {
                        Id = new(segment.Id, index == 0 ? null : index), Speaker = segment.Speaker, Offset = start, Text = text,
                        IsFinal = segment.IsFinal, ParticipantID = segment.Speaker == Speaker.Others ? word.ParticipantID : MeWord(word),
                        References = [new(segment.Id, new WordRange(index, index + 1))]
                    }, Math.Max(start, segment.Offset + word.End), 1, true));
                }
            }
            else
            {
                var text = segment.Text.Trim();
                if (text.Length == 0) continue;
                items.Add(new DisplayItem(new TranscriptTurn
                {
                    Id = new(segment.Id), Speaker = segment.Speaker, Offset = segment.Offset, Text = text, IsFinal = segment.IsFinal,
                    ParticipantID = segment.Speaker == Speaker.Others ? segment.ParticipantID : null,
                    References = [new(segment.Id)]
                }, segment.Offset + Math.Max(0, segment.Duration), text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length, false));
            }
        }
        OrderByUtterance(items, options ?? TranscriptDisplayOptions.Default);
        // Source chunks can overlap. Words sort by their utterance block and then in audio order, so simultaneous speech
        // on both channels shows as whole turns, while a pause of UnitGap on one side still lets the other side split it.
        items.Sort((a, b) =>
        {
            if (a.SortKey != b.SortKey) return a.SortKey.CompareTo(b.SortKey);
            if (a.Block != b.Block) return a.Block.CompareTo(b.Block);
            if (a.Turn.Offset != b.Turn.Offset) return a.Turn.Offset.CompareTo(b.Turn.Offset);
            if (a.Turn.Id.SegmentID != b.Turn.Id.SegmentID)
                return string.CompareOrdinal(a.Turn.Id.SegmentID.ToString("D"), b.Turn.Id.SegmentID.ToString("D"));
            return (a.Turn.Id.WordIndex ?? -1).CompareTo(b.Turn.Id.WordIndex ?? -1);
        });
        var grouped = new List<TranscriptTurn>();
        foreach (var run in items.Select(item => item.Turn))
        {
            if (grouped.Count > 0)
            {
                var last = grouped[^1];
                if (last.Id.SegmentID == run.Id.SegmentID && last.ParticipantID == run.ParticipantID && last.Speaker == run.Speaker
                    && last.References[^1].Words is { } previous && run.References[0].Words is { } range && previous.End == range.Start)
                {
                    last.Text += " " + run.Text;
                    last.References[^1] = last.References[^1] with { Words = new WordRange(previous.Start, range.End) };
                    continue;
                }
            }
            grouped.Add(run);
        }
        return grouped;
    }

    /// <summary>
    /// Consecutive runs of the same channel and participant, joined into turns (Swift <c>conversation</c>): the chat view.
    /// Each turn carries the <see cref="TranscriptReference"/>s that "Rename speaker…" / "Assign this segment" act on.
    /// </summary>
    public static List<TranscriptTurn> Conversation(Meeting meeting, TranscriptDisplayOptions? options = null)
    {
        var turns = new List<TranscriptTurn>();
        foreach (var run in Runs(meeting, options))
        {
            if (turns.Count > 0 && turns[^1].Speaker == run.Speaker && turns[^1].ParticipantID == run.ParticipantID)
            {
                var last = turns[^1];
                last.Text += " " + run.Text;
                last.IsFinal = last.IsFinal && run.IsFinal;
                last.References.AddRange(run.References);
            }
            else turns.Add(run);
        }
        return turns;
    }

    /// <summary>
    /// The label of a displayed run or turn (or one word): the participant <paramref name="participantID"/> names, else "Me"
    /// / "Others". On the Me channel a run carries a participant only when the microphone pass named the voice ("Speaker k")
    /// or the user assigned it (<see cref="Runs"/> filters with <see cref="NamesMeWord"/>), so this is the word-level view;
    /// the segment-level label is <see cref="Meeting.SpeakerName(Speaker, Guid?)"/>.
    /// </summary>
    public static string SpeakerName(Meeting meeting, Speaker speaker, Guid? participantID) =>
        (participantID is null ? null : meeting.Participants?.FirstOrDefault(person => person.Id == participantID)?.Name)
        ?? (speaker == Speaker.Me ? "Me" : "Others");

    /// <summary><c>[mm:ss] Name: text</c> per run, joined by newlines (Swift <c>transcriptText</c>).</summary>
    public static string TranscriptText(Meeting meeting, TranscriptDisplayOptions? options = null) =>
        string.Join('\n', Runs(meeting, options).Select(turn => $"[{Meeting.Timestamp(turn.Offset)}] {SpeakerName(meeting, turn.Speaker, turn.ParticipantID)}: {turn.Text}"));

    /// <summary><c>[mm:ss] Name: text</c> per turn, joined by blank lines (Swift <c>conversationText</c>).</summary>
    public static string ConversationText(Meeting meeting, TranscriptDisplayOptions? options = null) =>
        string.Join("\n\n", Conversation(meeting, options).Select(turn => $"[{Meeting.Timestamp(turn.Offset)}] {SpeakerName(meeting, turn.Speaker, turn.ParticipantID)}: {turn.Text}"));

    private static IEnumerable<TranscriptSegment> Ordered(IEnumerable<TranscriptSegment> transcript) =>
        transcript.OrderBy(segment => segment.Offset).ThenBy(segment => segment.Id.ToString("D"), StringComparer.Ordinal);

    /// <summary>
    /// Orders the view by utterance block: consecutive timed words of one channel and participant with gaps below
    /// <see cref="TranscriptDisplayOptions.UnitGap"/> sort at the block's start. A short contribution (e.g. "Ja.") that lies
    /// strictly inside a block of the other channel is shown right after that block instead of cutting the sentence.
    /// </summary>
    private static void OrderByUtterance(List<DisplayItem> items, TranscriptDisplayOptions options)
    {
        List<Block> Blocks(Speaker speaker)
        {
            var result = new List<Block>();
            var members = Enumerable.Range(0, items.Count).Where(index => items[index].Turn.Speaker == speaker)
                .OrderBy(index => items[index].Start).ToList();
            foreach (var index in members)
            {
                var item = items[index];
                if (result.Count > 0)
                {
                    var last = result[^1];
                    if (last.Timed && item.Timed && last.ParticipantID == item.Turn.ParticipantID && item.Start - last.End < options.UnitGap)
                    {
                        last.End = Math.Max(last.End, item.End);
                        last.Words += item.Words;
                        last.Members.Add(index);
                        continue;
                    }
                }
                result.Add(new Block(item.Turn.ParticipantID, item.Start, item.End, item.Words, item.Timed, index));
            }
            return result;
        }
        var me = Blocks(Speaker.Me);
        var others = Blocks(Speaker.Others);
        var number = 0;
        foreach (var block in me.Concat(others))
        {
            foreach (var index in block.Members) items[index].Block = number;
            number++;
        }
        foreach (var (guests, hosts) in new[] { (me, others), (others, me) })
        {
            // Walk backwards so the next block's own (possibly deferred) position is known: a deferred backchannel
            // never moves past the next block of its own channel.
            var nextKey = double.PositiveInfinity;
            for (var position = guests.Count - 1; position >= 0; position--)
            {
                var guest = guests[position];
                var key = guest.Start;
                if (guest.End - guest.Start <= options.BackchannelDuration + 1e-9 && guest.Words <= options.BackchannelWords)
                {
                    // Hosts are ordered by start, so only the last host starting before the guest can contain it.
                    int low = 0, high = hosts.Count;
                    while (low < high) { var mid = (low + high) / 2; if (hosts[mid].Start < guest.Start) low = mid + 1; else high = mid; }
                    if (low > 0 && hosts[low - 1].End > guest.End) key = Math.Max(guest.Start, Math.Min(hosts[low - 1].End, nextKey));
                }
                foreach (var index in guest.Members) items[index].SortKey = key;
                nextKey = key;
            }
        }
    }
}
