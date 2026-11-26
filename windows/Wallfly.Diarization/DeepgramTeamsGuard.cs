using System.Globalization;
using Wallfly.Core;
using Wallfly.Speakers;

namespace Wallfly.Diarization;

/// <summary>Thresholds of <see cref="DeepgramTeamsGuard"/> (plan step 4; start values).</summary>
public sealed record DeepgramTeamsGuardOptions
{
    /// <summary>A tile counts as present in a speaker when it has at least this many Teams-named words there (the fusion's <see cref="FusionPolicy.MinVotes"/>).</summary>
    public int MinVotesPerTile { get; init; } = 3;
    /// <summary>
    /// The second tile must hold at least this share of the speaker's Teams-named words for the speaker to count as two people.
    /// At 25 % a binding to the leader would overwrite every fourth named word; below it the fusion's turn override
    /// (<see cref="FusionPolicy.OverrideShare"/>) handles the rest.
    /// </summary>
    public double MinSecondTileShare { get; init; } = 0.25;
    /// <summary>
    /// A piece of a mixed speaker without named words takes the tile of the nearest named piece of the same speaker within this
    /// gap (the flow-of-speech gap of <see cref="TeamsNamingOptions.FlowGap"/>); farther pieces keep the original speaker.
    /// </summary>
    public double FlowGap { get; init; } = 2.0;
}

/// <summary>What the guard changed (numbers only).</summary>
/// <param name="Speakers">Speakers of the input.</param>
/// <param name="MixedSpeakers">Speakers Teams shows as two or more tiles, relabelled stretch by stretch.</param>
/// <param name="Stretches">Tile stretches those speakers were cut into.</param>
/// <param name="MergedSpeakers">Speakers joined into another speaker bound to the same tile.</param>
public readonly record struct DeepgramGuardSummary(int Speakers, int MixedSpeakers, int Stretches, int MergedSpeakers)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"speakers={Speakers} mixed={MixedSpeakers} stretches={Stretches} mergedSameTile={MergedSpeakers}");
}

/// <summary>
/// Plan step 4: the Teams check for results without a voice merge (the cloud path, where <see cref="TeamsVoiceGuard.Apply"/>
/// returns at once). Tiles are identities (one tile = one speaker with the tile's name, even with several people behind it):
/// <list type="number">
/// <item>A speaker whose Teams-named words show two or more tiles, each with ≥ 3 votes and the second with ≥ 25 % of its named
/// words, must not be bound to one name. Its segments are cut at the Teams changes (the middle between the last word named A
/// and the first word named B) and each stretch becomes a speaker of its own per tile, which the fusion then binds to that tile.
/// Pieces without named words join the nearest named stretch of the same speaker within <see cref="DeepgramTeamsGuardOptions.FlowGap"/>,
/// otherwise they keep the original speaker.</item>
/// <item>Speakers bound to the same tile (the fusion's binding rule; tiles compared by name) collapse into one speaker,
/// the one with most speech.</item>
/// </list>
/// Votes use the fusion's Teams evidence (<see cref="TeamsVoiceGuard.Words"/>) and word-to-speaker rule; the stretches follow
/// the Phase-1 (utterance-voted) labels, which are smoothed per turn. Pure; reads the meeting only. Call it right before the
/// fusion, where the local path calls <see cref="TeamsVoiceGuard.Apply"/>.
/// </summary>
public static class DeepgramTeamsGuard
{
    private const double Epsilon = 1e-9;

    /// <summary>A part of an archive segment with a linear time map: frames [From, To) start at meeting offset <see cref="Start"/>.</summary>
    private sealed record Piece(int Speaker, int File, long From, long To, double Start)
    {
        public double End => Start + (To - From) / DiarizationTimeline.SampleRate;
        public long FrameAt(double offset) => Math.Clamp(From + (long)Math.Round((offset - Start) * DiarizationTimeline.SampleRate, MidpointRounding.AwayFromZero), From, To);
    }

    public static DiarizationResult Apply(Meeting meeting, DiarizationResult result, FusionPolicy? policy, out DeepgramGuardSummary summary,
        DeepgramTeamsGuardOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(meeting);
        ArgumentNullException.ThrowIfNull(result);
        policy ??= new FusionPolicy();
        options ??= new DeepgramTeamsGuardOptions();
        var speakers = result.Segments.Select(segment => segment.Speaker).Distinct().Count();
        summary = new DeepgramGuardSummary(speakers, 0, 0, 0);
        if (result.Segments.Count == 0) return result;

        var tileOf = TileKeys(meeting);
        var votes = TeamsVoiceGuard.Words(meeting);
        var pieces = Pieces(result.Segments, result.Files, DiarizationTimeline.TimeMaps(meeting, result.Files));
        var tallies = Tallies(votes, pieces, tileOf, policy);
        var mixed = tallies.Where(pair => IsMixed(pair.Value, options)).Select(pair => pair.Key).ToHashSet();

        var stretches = 0;
        var tileOfSpeaker = new Dictionary<int, string>();
        if (mixed.Count > 0)
        {
            var phaseOne = PhaseOneWords(meeting, tileOf);
            var nextId = pieces.Max(piece => piece.Speaker) + 1;
            var ids = new Dictionary<(int Speaker, string Tile), int>();
            int IdFor(int speaker, string tile)
            {
                if (!ids.TryGetValue((speaker, tile), out var id)) { ids[(speaker, tile)] = id = nextId++; tileOfSpeaker[id] = tile; }
                return id;
            }
            var segments = pieces.Select(piece => new AcousticSegment { Speaker = piece.Speaker, Start = piece.Start, End = piece.End }).ToList();
            var output = new List<Piece>();
            var pending = new List<Piece>();
            foreach (var piece in pieces)
            {
                if (!mixed.Contains(piece.Speaker)) { output.Add(piece); continue; }
                var named = phaseOne.Where(word => (word.Start + word.End) / 2 is var middle && middle >= piece.Start - Epsilon && middle <= piece.End + Epsilon
                        && TeamsVoiceGuard.Cluster(new TeamsVoiceGuard.NamedWord(word.Start, word.End, null), segments, policy.NearestSegmentTolerance) == piece.Speaker)
                    .OrderBy(word => word.Start).ToList();
                if (named.Count == 0) { pending.Add(piece); continue; }
                // Runs of one tile (unnamed words do not break a run); cut in the middle between two runs.
                var runs = new List<(string Tile, double FirstStart, double LastEnd)>();
                foreach (var word in named)
                {
                    if (runs.Count > 0 && runs[^1].Tile == word.Tile) runs[^1] = runs[^1] with { LastEnd = Math.Max(runs[^1].LastEnd, word.End) };
                    else runs.Add((word.Tile, word.Start, word.End));
                }
                var from = piece.From;
                for (var index = 0; index < runs.Count; index++)
                {
                    var to = index + 1 < runs.Count ? piece.FrameAt((runs[index].LastEnd + runs[index + 1].FirstStart) / 2) : piece.To;
                    if (to > from) output.Add(piece with { Speaker = IdFor(piece.Speaker, runs[index].Tile), From = from, To = to, Start = piece.Start + (from - piece.From) / DiarizationTimeline.SampleRate });
                    from = to;
                }
                stretches += runs.Count;
            }
            foreach (var piece in pending)
            {
                var near = output.Where(other => tileOfSpeaker.ContainsKey(other.Speaker) && ids.Any(pair => pair.Value == other.Speaker && pair.Key.Speaker == piece.Speaker))
                    .Select(other => (other, gap: Math.Max(0, Math.Max(other.Start - piece.End, piece.Start - other.End))))
                    .Where(pair => pair.gap <= options.FlowGap + Epsilon)
                    .OrderBy(pair => pair.gap).ThenBy(pair => pair.other.Start).Select(pair => pair.other).FirstOrDefault();
                output.Add(near is null ? piece : piece with { Speaker = near.Speaker });
            }
            pieces = output.OrderBy(piece => piece.Start).ThenBy(piece => piece.Speaker).ToList();
        }

        // Speakers bound to the same tile: one speaker. Stretch speakers are bound by construction.
        var bound = new Dictionary<int, string>(tileOfSpeaker);
        var attribution = TeamsVoiceGuard.Attribution(votes, pieces.Select(piece => new AcousticSegment { Speaker = piece.Speaker, Start = piece.Start, End = piece.End }).ToList(), policy);
        foreach (var (speaker, person) in attribution) bound.TryAdd(speaker, Tile(person, tileOf));
        var speech = pieces.GroupBy(piece => piece.Speaker).ToDictionary(group => group.Key, group => group.Sum(piece => (double)(piece.To - piece.From)));
        var merge = new Dictionary<int, int>();
        foreach (var group in bound.GroupBy(pair => pair.Value, pair => pair.Key).Where(group => group.Count() > 1))
        {
            var target = group.OrderByDescending(speaker => speech.GetValueOrDefault(speaker)).ThenBy(speaker => speaker).First();
            foreach (var speaker in group.Where(speaker => speaker != target)) merge[speaker] = target;
        }
        summary = new DeepgramGuardSummary(speakers, mixed.Count, stretches, merge.Count);
        if (mixed.Count == 0 && merge.Count == 0) return result;
        var relabelled = TeamsVoiceGuard.Relabel(pieces.Select(piece => new ArchiveSegment(piece.Speaker, piece.File, piece.From, piece.To)).ToList(), merge);
        return result with { Segments = relabelled };
    }

    /// <summary>Two or more tiles with ≥ <see cref="DeepgramTeamsGuardOptions.MinVotesPerTile"/> votes each, the second with ≥ the minimum share of the named words.</summary>
    public static bool IsMixed(IReadOnlyDictionary<string, int> tally, DeepgramTeamsGuardOptions? options = null)
    {
        options ??= new DeepgramTeamsGuardOptions();
        var named = tally.Values.Sum();
        var ranked = tally.Values.OrderDescending().ToList();
        return ranked.Count >= 2 && ranked[1] >= options.MinVotesPerTile && ranked[0] >= options.MinVotesPerTile
            && ranked[1] / (double)named >= options.MinSecondTileShare - Epsilon;
    }

    /// <summary>Teams-named words per speaker and tile, with the fusion's word-to-speaker rule.</summary>
    private static Dictionary<int, Dictionary<string, int>> Tallies(List<TeamsVoiceGuard.NamedWord> words, List<Piece> pieces, Dictionary<Guid, string> tileOf,
        FusionPolicy policy)
    {
        var segments = pieces.Select(piece => new AcousticSegment { Speaker = piece.Speaker, Start = piece.Start, End = piece.End }).ToList();
        var result = new Dictionary<int, Dictionary<string, int>>();
        foreach (var word in words)
        {
            if (word.Participant is not Guid person || TeamsVoiceGuard.Cluster(word, segments, policy.NearestSegmentTolerance) is not int speaker) continue;
            var tally = result.TryGetValue(speaker, out var known) ? known : result[speaker] = new Dictionary<string, int>(StringComparer.Ordinal);
            var tile = Tile(person, tileOf);
            tally[tile] = tally.GetValueOrDefault(tile) + 1;
        }
        return result;
    }

    /// <summary>Tile key of a participant: its normalised name for Teams participants (one person may have several entries), else its id.</summary>
    private static Dictionary<Guid, string> TileKeys(Meeting meeting) =>
        (meeting.Participants ?? []).GroupBy(person => person.Id).ToDictionary(group => group.Key, group => group.First() is var person && TeamsParticipantKeys.IsTeams(person)
            ? "name:" + TeamsParticipantKeys.Normalized(person.Name) : "id:" + group.Key.ToString("D"));

    private static string Tile(Guid person, Dictionary<Guid, string> tileOf) => tileOf.TryGetValue(person, out var tile) ? tile : "id:" + person.ToString("D");

    /// <summary>The Phase-1 (utterance-voted) or manual label of each timed Others word, in meeting time; unnamed words are left out.</summary>
    private static List<(double Start, double End, string Tile)> PhaseOneWords(Meeting meeting, Dictionary<Guid, string> tileOf) =>
        meeting.Transcript.Where(segment => segment.Speaker == Speaker.Others && segment.Words is not null)
            .SelectMany(segment => segment.Words!.Where(word => double.IsFinite(word.Start) && double.IsFinite(word.End)).Select(word =>
            {
                var label = word.Attribution == WordAttribution.Manual ? word.ParticipantID
                    : word.UtteranceParticipantID ?? (word.AcousticSpeaker is null ? word.ParticipantID : null);
                return (Start: segment.Offset + word.Start, End: segment.Offset + Math.Max(word.Start, word.End), Label: label);
            }))
            .Where(word => word.Label is Guid id && !(meeting.Participants ?? []).Any(person => person.Id == id && AcousticSpeakerLabels.AcousticSpeakerId(person) is not null))
            .Select(word => (word.Start, word.End, Tile(word.Label!.Value, tileOf))).ToList();

    /// <summary>Archive segments split where their file's time map jumps (as <see cref="DiarizationTimeline.MapToMeeting"/>), keeping the frames.</summary>
    private static List<Piece> Pieces(IReadOnlyList<ArchiveSegment> segments, IReadOnlyList<ArchiveFile> files, IReadOnlyList<AudioTimeMap> maps)
    {
        var result = new List<Piece>();
        foreach (var segment in segments)
        {
            if (segment.File < 0 || segment.File >= files.Count || segment.File >= maps.Count) continue;
            var map = maps[segment.File];
            var first = Math.Max(segment.StartFrame, 0);
            var last = Math.Min(segment.EndFrame, files[segment.File].FrameCount);
            if (!(last > first)) continue;
            var cuts = new List<long> { first };
            cuts.AddRange(map.Anchors.Select(anchor => anchor.Frame).Where(frame => frame > first && frame < last));
            cuts.Add(last);
            for (var index = 0; index + 1 < cuts.Count; index++)
                if (cuts[index + 1] > cuts[index]) result.Add(new Piece(segment.Speaker, segment.File, cuts[index], cuts[index + 1], map.OffsetForFrame(cuts[index])));
        }
        return result.OrderBy(piece => piece.Start).ThenBy(piece => piece.Speaker).ToList();
    }
}
