using Wallfly.Core;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>
/// Checks the voice merge of the Others pass against the Teams names before the fusion. The fusion labels every automatic
/// word of a bound identity's turn with the binding unless a strong Teams majority overrides it (<see cref="FusionPolicy.OverrideShare"/>),
/// and a turn is a run of consecutive words of one identity. So when two remote people end up in one voice, the words of
/// one of them that carry a correct Teams name take the other's name (or "Speaker k" when the vote of the mixed voice fails).
/// The guard undoes exactly those merges: a voice whose clusters Teams clearly attributes to different people is split
/// again; clusters attributed to the same person stay one voice, clusters without a clear Teams attribution return to
/// their own cluster, as without the merge. Pure; numbers only.
/// </summary>
public static class TeamsVoiceGuard
{
    /// <summary>A timed remote word in meeting time with the Teams (or manual) name it carries before the fusion.</summary>
    public readonly record struct NamedWord(double Start, double End, Guid? Participant);

    /// <summary>
    /// The participant Teams clearly names for each cluster, with the fusion's binding rule: the winner among the cluster's
    /// named words has at least <see cref="FusionPolicy.MinVotes"/> votes and <see cref="FusionPolicy.MinVoteShare"/> of the
    /// named words, and the named words cover at least <see cref="FusionPolicy.MinVoteCoverage"/> of the cluster's words.
    /// A word belongs to the cluster whose segment holds its midpoint (largest overlap, ties to the lower id), else to the
    /// nearest segment within <see cref="FusionPolicy.NearestSegmentTolerance"/>, as in the fusion.
    /// </summary>
    public static Dictionary<int, Guid> Attribution(IReadOnlyList<NamedWord> words, IReadOnlyList<AcousticSegment> segments, FusionPolicy? policy = null)
    {
        policy ??= new FusionPolicy();
        var totals = new Dictionary<int, int>();
        var votes = new Dictionary<int, Dictionary<Guid, int>>();
        foreach (var word in words)
        {
            if (Cluster(word, segments, policy.NearestSegmentTolerance) is not int cluster) continue;
            totals[cluster] = totals.GetValueOrDefault(cluster) + 1;
            if (word.Participant is not Guid person) continue;
            var tally = votes.TryGetValue(cluster, out var known) ? known : votes[cluster] = [];
            tally[person] = tally.GetValueOrDefault(person) + 1;
        }
        var result = new Dictionary<int, Guid>();
        foreach (var (cluster, tally) in votes)
        {
            var named = tally.Values.Sum();
            var (winner, count) = tally.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).Select(pair => (pair.Key, pair.Value)).First();
            if (count >= policy.MinVotes && count / (double)named >= policy.MinVoteShare - 1e-9 && named / (double)totals[cluster] >= policy.MinVoteCoverage - 1e-9)
                result[cluster] = winner;
        }
        return result;
    }

    /// <summary>
    /// The voice map without merges that contradict <paramref name="attribution"/>, and how many voices that restored. A voice
    /// with clusters of two or more attributed people is split: per person one voice (keeping the voice's id when it is among
    /// them, else the cluster with most speech), every cluster without attribution on its own. Other voices stay as they are.
    /// </summary>
    public static (Dictionary<int, int> Voices, int Separated) Guard(IReadOnlyDictionary<int, int> voices, IReadOnlyDictionary<int, Guid> attribution,
        IReadOnlyDictionary<int, double> speech)
    {
        var result = voices.ToDictionary(pair => pair.Key, pair => pair.Value);
        var separated = 0;
        foreach (var group in voices.GroupBy(pair => pair.Value, pair => pair.Key))
        {
            var members = group.Order().ToList();
            var people = members.Where(attribution.ContainsKey).GroupBy(cluster => attribution[cluster]).ToList();
            if (people.Count < 2) continue;
            foreach (var person in people)
            {
                var own = person.ToList();
                var id = own.Contains(group.Key) ? group.Key
                    : own.OrderByDescending(cluster => speech.GetValueOrDefault(cluster)).ThenBy(cluster => cluster).First();
                foreach (var cluster in own) result[cluster] = id;
            }
            foreach (var cluster in members.Where(cluster => !attribution.ContainsKey(cluster))) result[cluster] = cluster;
            separated += members.Select(cluster => result[cluster]).Distinct().Count() - 1;
        }
        return (result, separated);
    }

    /// <summary>
    /// The Others result with merges that contradict the meeting's Teams names undone (unchanged without a voice merge or
    /// without a contradiction). Reads the meeting, never changes it; call it before the fusion.
    /// </summary>
    public static DiarizationResult Apply(Meeting meeting, DiarizationResult result, FusionPolicy? policy, out int separated)
    {
        separated = 0;
        if (result.Unmerged is not { } unmerged || result.Voices is not { } voices || voices.Values.Distinct().Count() == voices.Count) return result;
        var segments = DiarizationTimeline.MapToMeeting(unmerged, result.Files, DiarizationTimeline.TimeMaps(meeting, result.Files));
        var attribution = Attribution(Words(meeting), segments, policy);
        var speech = unmerged.GroupBy(segment => segment.Speaker).ToDictionary(group => group.Key, group => group.Sum(segment => (double)(segment.EndFrame - segment.StartFrame)));
        var (guarded, count) = Guard(voices, attribution, speech);
        if (count == 0) return result;
        separated = count;
        return result with { Segments = Relabel(unmerged, guarded), Voices = guarded };
    }

    /// <summary>
    /// The timed Others words with the name the fusion takes as Teams evidence: a manual word its manual label, otherwise the
    /// raw Teams label when the meeting has raw labels, else the stored pre-fusion label (as <see cref="AcousticSpeakerLabels.ApplyAcousticFusion"/>).
    /// </summary>
    public static List<NamedWord> Words(Meeting meeting)
    {
        var timed = meeting.Transcript.Where(segment => segment.Speaker == Speaker.Others && segment.Words is not null)
            .SelectMany(segment => segment.Words!.Where(word => double.IsFinite(word.Start) && double.IsFinite(word.End)).Select(word => (segment, word))).ToList();
        var raw = timed.Any(pair => pair.word.UiParticipantID is not null);
        return timed.Select(pair =>
        {
            var word = pair.word;
            var name = word.Attribution == WordAttribution.Manual ? word.ParticipantID
                : raw ? word.UiParticipantID
                : word.UtteranceParticipantID ?? (word.AcousticSpeaker is null ? word.ParticipantID : null);
            return new NamedWord(pair.segment.Offset + word.Start, pair.segment.Offset + Math.Max(word.Start, word.End), name);
        }).ToList();
    }

    /// <summary>Relabels archive segments with their voice; overlapping or touching segments of one voice in one file become one.</summary>
    public static List<ArchiveSegment> Relabel(IReadOnlyList<ArchiveSegment> segments, IReadOnlyDictionary<int, int> voices)
    {
        var result = new List<ArchiveSegment>();
        foreach (var own in segments.Select(segment => segment with { Speaker = voices.GetValueOrDefault(segment.Speaker, segment.Speaker) })
                     .GroupBy(segment => (segment.File, segment.Speaker)))
        {
            ArchiveSegment? open = null;
            foreach (var segment in own.OrderBy(segment => segment.StartFrame).ThenBy(segment => segment.EndFrame))
            {
                if (open is { } current && segment.StartFrame <= current.EndFrame) { open = current with { EndFrame = Math.Max(current.EndFrame, segment.EndFrame) }; continue; }
                if (open is { } done) result.Add(done);
                open = segment;
            }
            if (open is { } last) result.Add(last);
        }
        return result.OrderBy(segment => segment.File).ThenBy(segment => segment.StartFrame).ThenBy(segment => segment.Speaker).ToList();
    }

    /// <summary>The cluster a word belongs to with the fusion's rule (also used by <see cref="DeepgramTeamsGuard"/>).</summary>
    internal static int? Cluster(NamedWord word, IReadOnlyList<AcousticSegment> segments, double tolerance)
    {
        var middle = (word.Start + word.End) / 2;
        var overlap = new Dictionary<int, double>();
        (double Distance, int Speaker)? nearest = null;
        foreach (var segment in segments)
        {
            if (segment.Start <= middle && middle <= segment.End)
                overlap[segment.Speaker] = overlap.GetValueOrDefault(segment.Speaker) + Math.Max(0, Math.Min(word.End, segment.End) - Math.Max(word.Start, segment.Start));
            var distance = Math.Max(0, Math.Max(segment.Start - middle, middle - segment.End));
            if (nearest is not { } current || distance < current.Distance || (distance == current.Distance && segment.Speaker < current.Speaker))
                nearest = (distance, segment.Speaker);
        }
        if (overlap.Count > 0) return overlap.OrderByDescending(pair => pair.Value).ThenBy(pair => pair.Key).First().Key;
        return nearest is { } found && found.Distance <= tolerance + 1e-9 ? found.Speaker : null;
    }
}
