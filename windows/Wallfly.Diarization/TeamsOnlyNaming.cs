using System.Globalization;
using Wallfly.Core;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Thresholds of <see cref="TeamsOnlyNaming"/>: the plan's start values (step 5), to be checked in Phase B.</summary>
public sealed record TeamsNamingOptions
{
    /// <summary>
    /// At least this share of the timed Others words must carry an unambiguous tile name (the Phase-1 Teams label or a manual
    /// one). The first real Teams call (24.11.2026) had 91 %.
    /// </summary>
    public double MinNamedShare { get; init; } = 0.85;
    /// <summary>
    /// Others speech (word time) inside "ambiguous" Teams barriers (two tiles lit, or a name Teams could not resolve) must stay
    /// below this share: there the indicator cannot tell who spoke.
    /// </summary>
    public double MaxAmbiguousShare { get; init; } = 0.02;
    /// <summary>
    /// No stretch longer than this in which Teams was unreadable (screen sharing, window gone, no reads, stale reads) may
    /// contain Others words: inheriting a name across such a stretch could hand a whole contribution to the wrong person.
    /// </summary>
    public double MaxUnreadableSeconds { get; init; } = 30;
    /// <summary>
    /// "Flow of speech": consecutive Others words with gaps up to this long. An unnamed word inherits only from named words
    /// of its own flow. 2 s is the gap the cloud path joins words of one speaker across (<see cref="DeepgramPrerecordedOptions.SegmentJoinGap"/>);
    /// a longer pause is a new contribution that may come from anyone.
    /// </summary>
    public double FlowGap { get; init; } = 2.0;
    /// <summary>
    /// Words in flows without any named word stay unnamed; more than this share of them and Teams alone is not enough (the
    /// plan's conditions say nothing about them; same limit as the ambiguity).
    /// </summary>
    public double MaxUnresolvedShare { get; init; } = 0.02;
}

/// <summary>Why Teams naming alone is (or is not) enough.</summary>
public enum TeamsNamingReason
{
    /// <summary>All conditions hold.</summary>
    Sufficient,
    /// <summary>Exactly one remote tile in the whole meeting: every Others word gets its name.</summary>
    SingleTile,
    /// <summary>No timed Others words: nothing to decide (the run's skip rules apply).</summary>
    NoOthersWords,
    /// <summary>No Teams tile or no stored Teams evidence (<c>Meeting.SpeakerTimeline</c>).</summary>
    NoTeamsEvidence,
    /// <summary>Named share below <see cref="TeamsNamingOptions.MinNamedShare"/>.</summary>
    TooFewNamedWords,
    /// <summary>Ambiguous share at or above <see cref="TeamsNamingOptions.MaxAmbiguousShare"/>.</summary>
    TooMuchAmbiguity,
    /// <summary>An unreadable stretch longer than <see cref="TeamsNamingOptions.MaxUnreadableSeconds"/> contains Others words.</summary>
    TeamsUnreadable,
    /// <summary>Too many words without a named word in their flow (<see cref="TeamsNamingOptions.MaxUnresolvedShare"/>).</summary>
    UnresolvedWords
}

/// <summary>Where a word's final name comes from.</summary>
public enum TeamsNameSource
{
    /// <summary>The user's manual label (never changed).</summary>
    Manual,
    /// <summary>The word's own Phase-1 Teams label.</summary>
    Teams,
    /// <summary>The nearest named word of the same flow of speech.</summary>
    Inherited,
    /// <summary>The only remote tile of the meeting.</summary>
    SingleTile,
    /// <summary>No named word in its flow: stays unnamed.</summary>
    Unresolved
}

/// <summary>The final name of one timed Others word: <see cref="Segment"/>/<see cref="Word"/> index into <c>Meeting.Transcript</c>.</summary>
public readonly record struct TeamsWordName(int Segment, int Word, Guid? ParticipantID, TeamsNameSource Source);

/// <summary>The decision plus everything measured for it (shares of the timed Others words; numbers only, loggable).</summary>
public sealed record TeamsNamingDecision(bool Sufficient, TeamsNamingReason Reason, int OthersWords, int NamedWords, double NamedShare, double AmbiguousShare,
    double LongestUnreadableSeconds, int UnresolvedWords, double UnresolvedShare, int RemoteTiles, IReadOnlyList<TeamsWordName> Assignment)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"teamsOnly={(Sufficient ? "yes" : "no")} reason={Reason} words={OthersWords} named={NamedShare:0.000} ambiguous={AmbiguousShare:0.000} longestUnreadable={LongestUnreadableSeconds:0.0}s unresolved={UnresolvedShare:0.000} tiles={RemoteTiles}");
}

/// <summary>
/// Plan step 5: decides from the stored Teams evidence whether the Others channel needs a speaker-separation pass at all.
/// The tile is the identity (one tile = one speaker with the tile's name), so voices are only needed where Teams shows no
/// clear speaker. Sufficient when (1) at least 85 % of the timed Others words carry an unambiguous tile name, (2) less than
/// 2 % of the Others word time falls into ambiguous Teams stretches, (3) no unreadable stretch over 30 s contains Others
/// words, and (4) after inheritance at most 2 % of the words remain unnamed; or when the meeting has exactly one remote tile.
/// </summary>
/// <remarks>
/// A word's tile name is its Phase-1 label (the utterance-voted Teams owner, <c>UtteranceParticipantID</c>, or, for a word
/// no fusion touched yet, <c>ParticipantID</c>); manual labels count as named and are never changed. Inheritance: the Others
/// words in time order form flows (gaps up to <see cref="TeamsNamingOptions.FlowGap"/>); an unnamed word takes the name of
/// the nearest named word of its flow (distance between the words' edges, ties to the earlier word: the speaker who was
/// talking continues). Unreadable means: no evidence entry at all, or a barrier other than "ambiguous" and
/// "localUserSpeaking" (both mean Teams was readable). Pure; reads the meeting only.
/// </remarks>
public static class TeamsOnlyNaming
{
    private const double Epsilon = 1e-9;

    private readonly record struct Item(int Segment, int Word, double Start, double End, Guid? Label, bool Manual);

    public static TeamsNamingDecision Decide(Meeting meeting, TeamsNamingOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        options ??= new TeamsNamingOptions();
        var people = meeting.Participants ?? [];
        var tiles = people.Where(TeamsParticipantKeys.IsTeams).ToList();
        var tileName = tiles.GroupBy(person => person.Id).ToDictionary(group => group.Key, group => TeamsParticipantKeys.Normalized(group.First().Name));
        var remoteTiles = tileName.Values.Distinct(StringComparer.Ordinal).Count();
        var items = Items(meeting, tileName.Keys.ToHashSet());
        var timeline = meeting.SpeakerTimeline ?? [];
        if (items.Count == 0) return Result(false, TeamsNamingReason.NoOthersWords, items, 0, 0, 0, remoteTiles, []);

        var named = items.Count(item => item.Label is not null);
        var ambiguous = AmbiguousShare(items, timeline);
        var unreadable = LongestUnreadable(items, timeline);

        if (remoteTiles == 1)
        {
            // One tile = one speaker: the earliest entry of that name stands for it (one person may have several entries
            // when the Teams context changed).
            var only = tiles[0].Id;
            var assignment = items.Select(item => item.Manual
                ? new TeamsWordName(item.Segment, item.Word, item.Label, TeamsNameSource.Manual)
                : new TeamsWordName(item.Segment, item.Word, only, TeamsNameSource.SingleTile)).ToList();
            return Result(true, TeamsNamingReason.SingleTile, items, named, ambiguous, unreadable, remoteTiles, assignment);
        }
        var inherited = Inherit(items, options.FlowGap);
        if (remoteTiles == 0 || timeline.Count == 0)
            return Result(false, TeamsNamingReason.NoTeamsEvidence, items, named, ambiguous, unreadable, remoteTiles, inherited);
        var reason = (double)named / items.Count < options.MinNamedShare - Epsilon ? TeamsNamingReason.TooFewNamedWords
            : ambiguous >= options.MaxAmbiguousShare - Epsilon ? TeamsNamingReason.TooMuchAmbiguity
            : unreadable > options.MaxUnreadableSeconds + Epsilon ? TeamsNamingReason.TeamsUnreadable
            : (double)inherited.Count(name => name.Source == TeamsNameSource.Unresolved) / items.Count > options.MaxUnresolvedShare + Epsilon
                ? TeamsNamingReason.UnresolvedWords
                : TeamsNamingReason.Sufficient;
        return Result(reason == TeamsNamingReason.Sufficient, reason, items, named, ambiguous, unreadable, remoteTiles, inherited);
    }

    /// <summary>
    /// Writes a sufficient decision's names into the meeting's words (the same meeting <see cref="Decide"/> read): participant,
    /// Teams attribution (none for an unresolved word), no acoustic identity. The Phase-1 label of a word no fusion touched yet is
    /// kept in <c>UtteranceParticipantID</c>, as the fusion does. Manual words stay as they are. Returns the words changed.
    /// </summary>
    /// <remarks>
    /// Words only: the caller records the run (engine "teams") and decides what happens to an earlier acoustic result
    /// (<c>Diarization.Segments</c>, <c>AcousticSpeakers</c>, "Speaker k" participants), which a later re-apply would otherwise use again.
    /// </remarks>
    public static int Apply(Meeting meeting, TeamsNamingDecision decision)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(decision);
        if (!decision.Sufficient) throw new InvalidOperationException("Teams naming is not sufficient for this meeting.");
        var changed = 0;
        foreach (var name in decision.Assignment)
        {
            if (name.Source == TeamsNameSource.Manual || name.Segment < 0 || name.Segment >= meeting.Transcript.Count) continue;
            var words = meeting.Transcript[name.Segment].Words;
            if (words is null || name.Word < 0 || name.Word >= words.Count) continue;
            var word = words[name.Word];
            if (word.Attribution == WordAttribution.Manual) continue;
            if (word.UtteranceParticipantID is null && word.AcousticSpeaker is null) word.UtteranceParticipantID = word.ParticipantID;
            var attribution = name.ParticipantID is null ? (WordAttribution?)null : WordAttribution.TeamsAccessibility;
            if (word.ParticipantID == name.ParticipantID && word.Attribution == attribution && word.AcousticSpeaker is null) continue;
            word.ParticipantID = name.ParticipantID;
            word.Attribution = attribution;
            word.AcousticSpeaker = null;
            changed++;
        }
        return changed;
    }

    /// <summary>The timed Others words in time order with their tile name (Teams participants only) or manual label.</summary>
    private static List<Item> Items(Meeting meeting, HashSet<Guid> tiles)
    {
        var items = new List<Item>();
        for (var segmentIndex = 0; segmentIndex < meeting.Transcript.Count; segmentIndex++)
        {
            var segment = meeting.Transcript[segmentIndex];
            if (segment.Speaker != Speaker.Others || segment.Words is null) continue;
            for (var wordIndex = 0; wordIndex < segment.Words.Count; wordIndex++)
            {
                var word = segment.Words[wordIndex];
                if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
                var manual = word.Attribution == WordAttribution.Manual;
                var label = manual ? word.ParticipantID : word.UtteranceParticipantID ?? (word.AcousticSpeaker is null ? word.ParticipantID : null);
                if (!manual && label is Guid id && !tiles.Contains(id)) label = null;
                items.Add(new Item(segmentIndex, wordIndex, segment.Offset + word.Start, segment.Offset + Math.Max(word.Start, word.End), label, manual));
            }
        }
        return items.OrderBy(item => item.Start).ThenBy(item => item.End).ThenBy(item => item.Segment).ThenBy(item => item.Word).ToList();
    }

    /// <summary>Share of the Others word time inside ambiguous barriers (word count when all words have zero length).</summary>
    private static double AmbiguousShare(List<Item> items, List<SpeakerTimelineEntry> timeline)
    {
        var raw = TeamsAttributionFailure.Ambiguous.RawValue();
        var barriers = Merge(timeline.Where(entry => entry.Kind == SpeakerTimelineKind.Barrier && entry.Barrier == raw).Select(entry => (entry.Start, entry.End)));
        var total = items.Sum(item => item.End - item.Start);
        if (!(total > 0))
            return (double)items.Count(item => barriers.Any(barrier => barrier.Start <= item.Start && item.Start < barrier.End)) / items.Count;
        var inside = items.Sum(item => barriers.Sum(barrier => Math.Max(0, Math.Min(item.End, barrier.End) - Math.Max(item.Start, barrier.Start))));
        return inside / total;
    }

    /// <summary>The longest stretch without readable Teams evidence (within the Others words' span) that contains an Others word's midpoint.</summary>
    private static double LongestUnreadable(List<Item> items, List<SpeakerTimelineEntry> timeline)
    {
        var readableBarriers = new[] { TeamsAttributionFailure.Ambiguous.RawValue(), TeamsAttributionFailure.LocalUserSpeaking.RawValue() };
        var readable = Merge(timeline.Where(entry => entry.Kind is SpeakerTimelineKind.Speaker or SpeakerTimelineKind.Gap
                || (entry.Kind == SpeakerTimelineKind.Barrier && readableBarriers.Contains(entry.Barrier)))
            .Select(entry => (entry.Start, entry.End)));
        var from = items.Min(item => item.Start);
        var to = items.Max(item => item.End);
        var stretches = new List<(double Start, double End)>();
        var cursor = from;
        foreach (var (start, end) in readable)
        {
            if (end <= cursor) continue;
            if (start >= to) break;
            if (start > cursor) stretches.Add((cursor, start));
            cursor = Math.Max(cursor, end);
        }
        if (cursor < to) stretches.Add((cursor, to));
        var longest = 0.0;
        foreach (var (start, end) in stretches)
        {
            if (!items.Any(item => (item.Start + item.End) / 2 is var middle && middle >= start && middle <= end)) continue;
            longest = Math.Max(longest, end - start);
        }
        return longest;
    }

    /// <summary>Sorted, overlapping or touching intervals joined.</summary>
    private static List<(double Start, double End)> Merge(IEnumerable<(double Start, double End)> intervals)
    {
        var result = new List<(double Start, double End)>();
        foreach (var (start, end) in intervals.Where(interval => interval.End > interval.Start).OrderBy(interval => interval.Start))
        {
            if (result.Count > 0 && start <= result[^1].End + 1e-6) result[^1] = (result[^1].Start, Math.Max(result[^1].End, end));
            else result.Add((start, end));
        }
        return result;
    }

    /// <summary>Every word's final name: manual and named words keep theirs, the others inherit within their flow.</summary>
    private static List<TeamsWordName> Inherit(List<Item> items, double flowGap)
    {
        var result = new TeamsWordName[items.Count];
        var flowStart = 0;
        var flowEnd = double.NegativeInfinity;
        for (var index = 0; index <= items.Count; index++)
        {
            if (index < items.Count && (index == flowStart || items[index].Start - flowEnd <= flowGap + Epsilon))
            {
                flowEnd = Math.Max(flowEnd, items[index].End);
                continue;
            }
            ResolveFlow(items, flowStart, index, result);
            if (index == items.Count) break;
            flowStart = index;
            flowEnd = items[index].End;
        }
        return [.. result];
    }

    private static void ResolveFlow(List<Item> items, int from, int to, TeamsWordName[] result)
    {
        // Nearest named word before and after each position (named = carries a label, manual or Teams).
        var before = new int?[to - from];
        var after = new int?[to - from];
        int? last = null;
        for (var index = from; index < to; index++) { if (items[index].Label is not null) last = index; before[index - from] = last; }
        last = null;
        for (var index = to - 1; index >= from; index--) { if (items[index].Label is not null) last = index; after[index - from] = last; }
        for (var index = from; index < to; index++)
        {
            var item = items[index];
            if (item.Manual) { result[index] = new(item.Segment, item.Word, item.Label, TeamsNameSource.Manual); continue; }
            if (item.Label is not null) { result[index] = new(item.Segment, item.Word, item.Label, TeamsNameSource.Teams); continue; }
            int? source = (before[index - from], after[index - from]) switch
            {
                (int left, int right) => Math.Max(0, item.Start - items[left].End) <= Math.Max(0, items[right].Start - item.End) + Epsilon ? left : right,
                (int left, null) => left,
                (null, int right) => right,
                _ => null
            };
            result[index] = source is int named
                ? new(item.Segment, item.Word, items[named].Label, TeamsNameSource.Inherited)
                : new(item.Segment, item.Word, null, TeamsNameSource.Unresolved);
        }
    }

    private static TeamsNamingDecision Result(bool sufficient, TeamsNamingReason reason, List<Item> items, int named, double ambiguous, double unreadable, int tiles,
        IReadOnlyList<TeamsWordName> assignment)
    {
        var unresolved = assignment.Count(name => name.Source == TeamsNameSource.Unresolved);
        var count = Math.Max(1, items.Count);
        return new TeamsNamingDecision(sufficient, reason, items.Count, named, items.Count == 0 ? 0 : (double)named / count, ambiguous, unreadable,
            unresolved, items.Count == 0 ? 0 : (double)unresolved / count, tiles, sufficient ? assignment : []);
    }
}
