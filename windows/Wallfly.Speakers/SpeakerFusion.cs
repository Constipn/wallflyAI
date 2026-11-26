namespace Wallfly.Speakers;

/// <summary>One remote word in absolute meeting time with the labels known before fusion.</summary>
/// <param name="Id">Caller-defined index, returned unchanged.</param>
/// <param name="UiParticipantID">Raw per-word Teams decision (may be null).</param>
/// <param name="Phase1ParticipantID">Utterance-voted Teams result (Phase 1); for a manual word, its manual label.</param>
/// <param name="IsManual">A manual label is never changed, but counts as evidence.</param>
public readonly record struct FusionWord(int Id, double Start, double End, Guid? UiParticipantID, Guid? Phase1ParticipantID, bool IsManual);

/// <summary>One speech segment of the acoustic diarizer in absolute meeting time.</summary>
public readonly record struct FusionAcousticSegment(int Speaker, double Start, double End);

/// <summary>Thresholds of the fusion (port of <c>FusionPolicy</c>, SpeakerFusion.swift; chosen defaults).</summary>
public sealed record FusionPolicy
{
    /// <summary>
    /// A word whose midpoint lies in no segment takes the segment nearest to its midpoint within this distance (the reference
    /// value is 0.5 s; the macOS port used 0.3 s from the word's edges).
    /// </summary>
    public double NearestSegmentTolerance { get; init; } = 0.5;
    /// <summary>A sandwiched acoustic run shorter than this is diarizer jitter.</summary>
    public double ShortRunDuration { get; init; } = 0.5;
    /// <summary>A sandwiched acoustic run with at most this many words is diarizer jitter.</summary>
    public int ShortRunWords { get; init; } = 2;
    /// <summary>
    /// An acoustic identity with less segment speech than this is suppressed, as long as another identity of the same
    /// fusion (one channel: the fusion runs per source) reaches it (weak-identity rule).
    /// </summary>
    public double MinIdentitySpeech { get; init; } = 10;
    /// <summary>
    /// Phantom rule (10–30 s of speech, ≥ 20 runs, longest run ≤ 3 s and average speaker confidence &lt; 0.4):
    /// identities below this speech time are checked for the jittery pattern. The acoustic pass gives no per-word
    /// confidence, so the duration, run-count and longest-run conditions alone decide.
    /// </summary>
    public double JitterSpeechCeiling { get; init; } = 30;
    /// <summary>A phantom (jittery) identity appears in at least this many separate runs.</summary>
    public int JitterMinRuns { get; init; } = 20;
    /// <summary>A phantom (jittery) identity never holds a run longer than this.</summary>
    public double JitterMaxLongestRun { get; init; } = 3;
    /// <summary>Words starting this soon after an acoustic turn starts do not vote for a binding (Teams indicator lag).</summary>
    public double TurnLeadIn { get; init; } = 0.6;
    /// <summary>A binding needs at least this many votes for the winner.</summary>
    public int MinVotes { get; init; } = 3;
    /// <summary>The winner needs at least this share of the named votes (binding and unbound turn votes).</summary>
    public double MinVoteShare { get; init; } = 0.6;
    /// <summary>Named votes must cover at least this share of the identity's words for a binding.</summary>
    public double MinVoteCoverage { get; init; } = 0.25;
    /// <summary>A Teams majority overrides a bound name only with at least this duration share of the named words.</summary>
    public double OverrideShare { get; init; } = 0.8;
    /// <summary>A Teams majority overrides a bound name only if named words cover at least this duration share of the turn.</summary>
    public double OverrideCoverage { get; init; } = 0.5;
    /// <summary>A Teams majority overrides a bound name only in turns at least this long.</summary>
    public double OverrideMinDuration { get; init; } = 2;
    /// <summary>An unbound turn takes the Teams majority only if it has at least this many words.</summary>
    public int UnboundMinVotes { get; init; } = 2;
}

/// <summary>Why a word carries its final label.</summary>
public enum FusionLabelSource
{
    /// <summary>The user's manual label, unchanged.</summary>
    Manual,
    /// <summary>The participant bound to the word's acoustic identity.</summary>
    AcousticBound,
    /// <summary>The Teams majority over the word's acoustic turn.</summary>
    UiVote,
    /// <summary>The Phase 1 result (no or suppressed acoustic identity); its participant may be null.</summary>
    Phase1,
    /// <summary>An unbound, unsuppressed acoustic identity without a clear Teams vote ("Speaker k").</summary>
    Unlabelled
}

public readonly record struct FusionWordLabel(int Id, int? AcousticSpeaker, Guid? ParticipantID, FusionLabelSource Source);

/// <param name="SpeechSeconds">Union duration of the identity's acoustic segments.</param>
/// <param name="BoundParticipantID">Manual or voted binding; always null for a suppressed identity.</param>
/// <param name="DisplayOrdinal">1-based "Speaker k" for unbound, unsuppressed identities by first appearance; null otherwise or without words.</param>
public readonly record struct FusionSpeaker(int Id, double SpeechSeconds, int WordCount, Guid? BoundParticipantID, bool BindingIsManual,
    bool Suppressed, int? DisplayOrdinal);

/// <summary>One label per input word (input order) and every acoustic identity of the segments (sorted by id).</summary>
public sealed class FusionResult(IReadOnlyList<FusionWordLabel> words, IReadOnlyList<FusionSpeaker> speakers) : IEquatable<FusionResult>
{
    public IReadOnlyList<FusionWordLabel> Words { get; } = words;
    public IReadOnlyList<FusionSpeaker> Speakers { get; } = speakers;

    public bool Equals(FusionResult? other) =>
        other is not null && Words.SequenceEqual(other.Words) && Speakers.SequenceEqual(other.Speakers);
    public override bool Equals(object? obj) => Equals(obj as FusionResult);
    public override int GetHashCode() => HashCode.Combine(Words.Count, Speakers.Count);
}

/// <summary>
/// Fuses Teams UI evidence with acoustic diarization (1:1 port of <c>SpeakerFusion</c>, plan Phase 3). Priority:
/// manual &gt; Teams UI &gt; voice, but a bound acoustic identity holds its name against UI flicker within a turn.
/// Deterministic and independent of input order.
/// </summary>
/// <remarks>
/// Steps: (1) word → acoustic identity (the segment containing the word's midpoint, else the segment nearest to the
/// midpoint within 0.5 s, as words are assigned to turns); (2) smoothing of brief
/// uncorroborated runs between two runs of the same identity; (3) suppression of flicker identities; (4) binding by
/// Teams votes (manual bindings win); (5) one label per acoustic turn; (6) the caller writes the labels back
/// (<see cref="AcousticSpeakerLabels.ApplyAcousticFusion"/>).
/// </remarks>
public static class SpeakerFusion
{
    private const double Epsilon = 1e-9;

    /// <summary>Consecutive words (in time order) with the same acoustic identity.</summary>
    private sealed class Turn(int? speaker, int member, double start, double end)
    {
        public int? Speaker { get; } = speaker;
        /// <summary>Indices into the input words, in time order.</summary>
        public List<int> Members { get; } = [member];
        public double Start { get; private set; } = start;
        public double End { get; private set; } = end;
        public double Duration => End - Start;

        public void Absorb(Turn other)
        {
            Members.AddRange(other.Members);
            Start = Math.Min(Start, other.Start);
            End = Math.Max(End, other.End);
        }
    }

    private sealed class IdentityStats
    {
        public double Speech { get; set; }
        public int Words { get; set; }
        public int Runs { get; set; }
        public double LongestRun { get; set; }
        public bool HasManualWord { get; set; }
    }

    /// <summary>Duration-weighted Teams majority of one turn.</summary>
    private readonly record struct TurnVote(Guid ParticipantID, double Share, double Coverage, int Words);

    public static FusionResult Fuse(IReadOnlyList<FusionWord> words, IReadOnlyList<FusionAcousticSegment> segments,
        IReadOnlyDictionary<int, Guid> manualBindings, FusionPolicy? policy = null)
    {
        policy ??= new FusionPolicy();
        var order = Enumerable.Range(0, words.Count)
            .OrderBy(index => words[index].Start).ThenBy(index => words[index].End).ThenBy(index => words[index].Id).ThenBy(index => index).ToList();
        var turns = MakeTurns(order, order.Select(index => AcousticSpeaker(words[index], segments, policy)).ToList(), words);
        Smooth(turns, words, policy);

        var ids = segments.Select(segment => segment.Speaker).Distinct().Order().ToList();
        var stats = Statistics(ids, turns, segments, words);
        var suppressed = SuppressedIDs(ids, stats, manualBindings, policy);
        var bindings = new Dictionary<int, Guid>();
        foreach (var id in ids)
        {
            if (suppressed.Contains(id)) continue;
            var bound = manualBindings.TryGetValue(id, out var manual) ? manual
                : VotedBinding(id, turns, words, stats.TryGetValue(id, out var stat) ? stat.Words : 0, policy);
            if (bound is Guid participant) bindings[id] = participant;
        }

        var labels = words.Select(word => new FusionWordLabel(word.Id, null, word.Phase1ParticipantID, FusionLabelSource.Phase1)).ToArray();
        var ordinals = new Dictionary<int, int>();
        foreach (var turn in turns)
        {
            (Guid?, FusionLabelSource)? decided = turn.Speaker is int speaker && !suppressed.Contains(speaker)
                ? Label(turn, bindings.TryGetValue(speaker, out var bound) ? bound : null, manualBindings.ContainsKey(speaker), words, policy)
                : null;
            if (turn.Speaker is int unbound && !suppressed.Contains(unbound) && !bindings.ContainsKey(unbound) && !ordinals.ContainsKey(unbound))
                ordinals[unbound] = ordinals.Count + 1;
            foreach (var index in turn.Members)
            {
                labels[index] = labels[index] with { AcousticSpeaker = turn.Speaker };
                if (words[index].IsManual) labels[index] = labels[index] with { Source = FusionLabelSource.Manual };
                else if (decided is { } label) labels[index] = labels[index] with { ParticipantID = label.Item1, Source = label.Item2 };
            }
        }

        var speakers = ids.Select(id => new FusionSpeaker(id, stats.TryGetValue(id, out var stat) ? stat.Speech : 0, stats.TryGetValue(id, out var count) ? count.Words : 0,
            bindings.TryGetValue(id, out var bound) ? bound : null, manualBindings.ContainsKey(id), suppressed.Contains(id),
            ordinals.TryGetValue(id, out var ordinal) ? ordinal : null)).ToList();
        return new FusionResult(labels, speakers);
    }

    /// <summary>
    /// The speaker whose segment contains the word's midpoint (several overlapping speakers: the largest total overlap with
    /// the word), else the segment nearest to the midpoint within tolerance; ties go to the lower id.
    /// </summary>
    private static int? AcousticSpeaker(FusionWord word, IReadOnlyList<FusionAcousticSegment> segments, FusionPolicy policy)
    {
        var middle = (word.Start + word.End) / 2;
        var overlap = new Dictionary<int, double>();
        (double Distance, int Speaker)? nearest = null;
        foreach (var segment in segments)
        {
            var distance = Math.Max(0, Math.Max(segment.Start - middle, middle - segment.End));
            if (segment.Start <= middle && middle <= segment.End)
                overlap[segment.Speaker] = overlap.GetValueOrDefault(segment.Speaker) + Math.Max(0, Math.Min(word.End, segment.End) - Math.Max(word.Start, segment.Start));
            if (nearest is not { } current || distance < current.Distance || (distance == current.Distance && segment.Speaker < current.Speaker))
                nearest = (distance, segment.Speaker);
        }
        if (overlap.Count > 0)
        {
            var best = overlap.First();
            foreach (var pair in overlap)
                if (pair.Value > best.Value || (pair.Value == best.Value && pair.Key < best.Key)) best = pair;
            return best.Key;
        }
        if (nearest is not { } found || !(found.Distance <= policy.NearestSegmentTolerance + Epsilon)) return null;
        return found.Speaker;
    }

    private static List<Turn> MakeTurns(List<int> order, List<int?> speakers, IReadOnlyList<FusionWord> words)
    {
        var turns = new List<Turn>();
        for (var position = 0; position < order.Count; position++)
        {
            var index = order[position];
            var single = new Turn(speakers[position], index, words[index].Start, words[index].End);
            if (turns.Count > 0 && turns[^1].Speaker == speakers[position]) turns[^1].Absorb(single);
            else turns.Add(single);
        }
        return turns;
    }

    /// <summary>
    /// Merges a brief run of B between two runs of A into A (diarizer jitter). Kept are runs without an acoustic identity,
    /// runs containing a manual word, and runs whose own Teams evidence names someone other than the host (a corroborated
    /// interjection).
    /// </summary>
    private static void Smooth(List<Turn> turns, IReadOnlyList<FusionWord> words, FusionPolicy policy)
    {
        var index = 1;
        while (index + 1 < turns.Count)
        {
            var candidate = turns[index];
            var isBrief = candidate.Duration < policy.ShortRunDuration - Epsilon || candidate.Members.Count <= policy.ShortRunWords;
            if (isBrief && candidate.Speaker is not null && turns[index - 1].Speaker is not null && turns[index - 1].Speaker == turns[index + 1].Speaker
                && !candidate.Members.Any(member => words[member].IsManual)
                && !IsCorroborated(candidate, [.. turns[index - 1].Members, .. turns[index + 1].Members], words))
            {
                turns[index - 1].Absorb(candidate);
                turns[index - 1].Absorb(turns[index + 1]);
                turns.RemoveRange(index, 2);
            }
            else
            {
                index++;
            }
        }
    }

    /// <summary>True if a word of the run carries a Teams label other than the host's most frequent one.</summary>
    private static bool IsCorroborated(Turn run, List<int> host, IReadOnlyList<FusionWord> words)
    {
        var hostTally = new Dictionary<Guid, double>();
        foreach (var index in host) if (words[index].UiParticipantID is Guid id) hostTally[id] = hostTally.GetValueOrDefault(id) + 1;
        var hostLabel = Leader(hostTally)?.ParticipantID;
        return run.Members.Any(index => words[index].UiParticipantID is Guid id && id != hostLabel);
    }

    private static Dictionary<int, IdentityStats> Statistics(List<int> ids, List<Turn> turns, IReadOnlyList<FusionAcousticSegment> segments, IReadOnlyList<FusionWord> words)
    {
        var stats = ids.ToDictionary(id => id, _ => new IdentityStats());
        foreach (var own in segments.GroupBy(segment => segment.Speaker))
        {
            var coveredUntil = double.NegativeInfinity;
            foreach (var segment in own.OrderBy(segment => segment.Start).ThenBy(segment => segment.End))
            {
                var from = Math.Max(segment.Start, coveredUntil);
                if (segment.End > from) stats[own.Key].Speech += segment.End - from;
                coveredUntil = Math.Max(coveredUntil, segment.End);
            }
        }
        foreach (var turn in turns)
        {
            if (turn.Speaker is not int speaker || !stats.TryGetValue(speaker, out var stat)) continue;
            stat.Words += turn.Members.Count;
            stat.Runs++;
            stat.LongestRun = Math.Max(stat.LongestRun, turn.Duration);
            if (turn.Members.Any(index => words[index].IsManual)) stat.HasManualWord = true;
        }
        return stats;
    }

    /// <summary>
    /// Weak identities (less than <see cref="FusionPolicy.MinIdentitySpeech"/>) and phantoms (the jitter pattern), unless
    /// manually confirmed. Nothing is suppressed when every identity would be, so a source whose identities are all short
    /// keeps them (weak identities are dropped only once one identity of the source has 10 s).
    /// </summary>
    private static HashSet<int> SuppressedIDs(List<int> ids, Dictionary<int, IdentityStats> stats, IReadOnlyDictionary<int, Guid> manualBindings, FusionPolicy policy)
    {
        var candidates = ids.Where(id =>
        {
            if (!stats.TryGetValue(id, out var stat) || manualBindings.ContainsKey(id) || stat.HasManualWord) return false;
            return stat.Speech < policy.MinIdentitySpeech - Epsilon
                || (stat.Speech < policy.JitterSpeechCeiling - Epsilon && stat.Runs >= policy.JitterMinRuns
                    && stat.LongestRun <= policy.JitterMaxLongestRun + Epsilon);
        }).ToList();
        return candidates.Count < ids.Count ? candidates.ToHashSet() : [];
    }

    /// <summary>Word-count vote of Teams labels (outside each turn's lead-in) and manual labels for one identity.</summary>
    private static Guid? VotedBinding(int id, List<Turn> turns, IReadOnlyList<FusionWord> words, int wordCount, FusionPolicy policy)
    {
        var votes = new Dictionary<Guid, double>();
        foreach (var turn in turns)
        {
            if (turn.Speaker != id) continue;
            foreach (var index in turn.Members)
            {
                var word = words[index];
                var voter = word.IsManual ? word.Phase1ParticipantID
                    : word.Start >= turn.Start + policy.TurnLeadIn - Epsilon ? word.UiParticipantID : null;
                if (voter is Guid person) votes[person] = votes.GetValueOrDefault(person) + 1;
            }
        }
        var named = votes.Values.Sum();
        if (Leader(votes) is not { } winner || !(winner.Value >= policy.MinVotes) || !(winner.Value / named >= policy.MinVoteShare - Epsilon)
            || !(named / Math.Max(wordCount, 1) >= policy.MinVoteCoverage - Epsilon)) return null;
        return winner.ParticipantID;
    }

    /// <summary>The label of an unsuppressed acoustic turn's automatic words. A clear, long Teams majority overrides a voted binding, never a manual one.</summary>
    private static (Guid?, FusionLabelSource) Label(Turn turn, Guid? bound, bool manually, IReadOnlyList<FusionWord> words, FusionPolicy policy)
    {
        var vote = TeamsMajority(turn, words);
        if (bound is Guid binding)
        {
            if (!manually && vote is { } majority && majority.ParticipantID != binding && majority.Share >= policy.OverrideShare - Epsilon
                && majority.Coverage >= policy.OverrideCoverage - Epsilon && turn.Duration >= policy.OverrideMinDuration - Epsilon)
                return (majority.ParticipantID, FusionLabelSource.UiVote);
            return (binding, FusionLabelSource.AcousticBound);
        }
        if (vote is { } result && result.Words >= policy.UnboundMinVotes && result.Share >= policy.MinVoteShare - Epsilon)
            return (result.ParticipantID, FusionLabelSource.UiVote);
        return (null, FusionLabelSource.Unlabelled);
    }

    /// <summary>Duration-weighted vote of the turn's Teams labels (manual words vote with their manual label).</summary>
    private static TurnVote? TeamsMajority(Turn turn, IReadOnlyList<FusionWord> words)
    {
        var weight = new Dictionary<Guid, double>();
        var count = new Dictionary<Guid, int>();
        double total = 0;
        foreach (var index in turn.Members)
        {
            var word = words[index];
            var duration = Math.Max(0, word.End - word.Start);
            total += duration;
            if ((word.IsManual ? word.Phase1ParticipantID : word.UiParticipantID) is not Guid voter) continue;
            weight[voter] = weight.GetValueOrDefault(voter) + duration;
            count[voter] = count.GetValueOrDefault(voter) + 1;
        }
        var named = weight.Values.Sum();
        if (!(named > 0) || Leader(weight) is not { } winner) return null;
        return new TurnVote(winner.ParticipantID, winner.Value / named, named / total, count.GetValueOrDefault(winner.ParticipantID));
    }

    /// <summary>The highest tally; ties go to the lexicographically smaller UUID for determinism.</summary>
    private static (Guid ParticipantID, double Value)? Leader(Dictionary<Guid, double> tally)
    {
        (Guid ParticipantID, double Value)? best = null;
        foreach (var (participant, value) in tally)
        {
            if (best is not { } current || value > current.Value || (value == current.Value && UuidOrder.Compare(participant, current.ParticipantID) < 0))
                best = (participant, value);
        }
        return best;
    }
}
