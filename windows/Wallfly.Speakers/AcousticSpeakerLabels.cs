using System.Globalization;
using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>
/// Applies the acoustic fusion (<see cref="SpeakerFusion"/>) to a meeting's stored transcript and keeps the "Speaker k"
/// participants and manual acoustic bindings in sync (port of AcousticSpeakerLabels.swift). Pure data logic, no I/O.
/// The meeting is changed in place; pass a copy (<c>WallflyJson.Copy</c>) when the change must be discardable.
/// </summary>
public static class AcousticSpeakerLabels
{
    /// <summary><c>SourceKey</c> prefix of the synthetic participant that stands for an unbound acoustic identity.</summary>
    public const string AcousticSourceKeyPrefix = TranscriptDisplay.AcousticSourceKeyPrefix;

    /// <summary>
    /// Meeting-wide ids of microphone identities: cluster <c>k</c> of the Me pass is identity <c>1000 + k</c> in
    /// <c>AcousticSpeakers</c>, in <c>TranscriptWord.AcousticSpeaker</c> of Me words and in the "Speaker k" source key, so it
    /// never collides with an Others cluster (the same voice on both channels is not merged).
    /// </summary>
    public const int MicrophoneSpeakerBase = TranscriptDisplay.MicrophoneSpeakerBase;

    /// <summary>Microphone rule: one voice with less overlapped speech than this share stays the user.</summary>
    public const double MicrophoneOverlapShare = 0.01;

    /// <summary>True for a meeting-wide identity id of the microphone channel.</summary>
    public static bool IsMicrophoneSpeaker(int id) => id >= MicrophoneSpeakerBase;

    /// <summary>The acoustic identity a synthetic "Speaker k" participant stands for, null for every other participant.</summary>
    public static int? AcousticSpeakerId(MeetingParticipant participant)
    {
        if (participant.SourceKey is not { } key || !key.StartsWith(AcousticSourceKeyPrefix, StringComparison.Ordinal)) return null;
        return int.TryParse(key.AsSpan(AcousticSourceKeyPrefix.Length), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var id) ? id : null;
    }

    /// <summary>True when the transcript has timed remote words (the only words fusion can label).</summary>
    public static bool HasTimedRemoteWords(this Meeting meeting) => meeting.HasTimedWords(Speaker.Others);

    /// <summary>True when the transcript has timed words on <paramref name="channel"/>.</summary>
    public static bool HasTimedWords(this Meeting meeting, Speaker channel) =>
        meeting.Transcript.Any(segment => segment.Speaker == channel
            && (segment.Words ?? []).Any(word => double.IsFinite(word.Start) && double.IsFinite(word.End)));

    /// <summary>
    /// Relabels every timed remote word from the acoustic <paramref name="segments"/> (meeting offsets) and the Teams evidence.
    /// Inputs per word: the raw Teams label (<c>UiParticipantID</c>) and the Phase-1 label (<c>UtteranceParticipantID</c>).
    /// Meetings recorded before raw labels were stored (no word has <c>UiParticipantID</c>) use the stored pre-fusion label
    /// as the Teams proxy. Manual words keep their label and vote with it; manual bindings in <c>AcousticSpeakers</c> are
    /// never overridden. Deterministic and idempotent: applying it twice with the same segments yields the same meeting.
    /// </summary>
    public static void ApplyAcousticFusion(this Meeting meeting, IReadOnlyList<AcousticSegment> segments, FusionPolicy? policy = null)
    {
        var transcript = meeting.Transcript;
        var locations = new List<(int Segment, int Word)>();
        for (var segmentIndex = 0; segmentIndex < transcript.Count; segmentIndex++)
        {
            var segment = transcript[segmentIndex];
            if (segment.Speaker != Speaker.Others || segment.Words is null) continue;
            for (var wordIndex = 0; wordIndex < segment.Words.Count; wordIndex++)
            {
                var word = segment.Words[wordIndex];
                if (double.IsFinite(word.Start) && double.IsFinite(word.End)) locations.Add((segmentIndex, wordIndex));
            }
        }
        // Record the Phase-1 label of words fusion has never touched (older meetings and words of later sessions).
        foreach (var (s, w) in locations)
        {
            var word = transcript[s].Words![w];
            if (word.Attribution == WordAttribution.Manual || word.UtteranceParticipantID is not null || word.AcousticSpeaker is not null) continue;
            word.UtteranceParticipantID = word.ParticipantID;
        }
        var hasRawUILabels = locations.Any(location => transcript[location.Segment].Words![location.Word].UiParticipantID is not null);
        var knownParticipants = (meeting.Participants ?? []).Select(person => person.Id).ToHashSet();

        var words = locations.Select((location, id) =>
        {
            var segment = transcript[location.Segment];
            var word = segment.Words![location.Word];
            var manual = word.Attribution == WordAttribution.Manual;
            return new FusionWord(id, segment.Offset + word.Start, segment.Offset + Math.Max(word.Start, word.End),
                hasRawUILabels ? word.UiParticipantID : word.UtteranceParticipantID,
                manual ? word.ParticipantID : word.UtteranceParticipantID, manual);
        }).ToList();
        var manualBindings = new Dictionary<int, Guid>();
        foreach (var speaker in meeting.AcousticSpeakers ?? [])
        {
            if (speaker.BindingSource == AcousticBindingSource.Manual && speaker.ParticipantID is Guid participant && knownParticipants.Contains(participant))
                manualBindings[speaker.Id] = participant;
        }
        var fusion = SpeakerFusion.Fuse(words, segments.Select(segment => new FusionAcousticSegment(segment.Speaker, segment.Start, segment.End)).ToList(),
            manualBindings, policy);

        // "Speaker k" only for identities that label at least one word, numbered by first appearance.
        var unlabelled = fusion.Words.Where(label => label.Source == FusionLabelSource.Unlabelled && label.AcousticSpeaker is not null)
            .Select(label => label.AcousticSpeaker!.Value).ToHashSet();
        var ordinals = fusion.Speakers.Where(speaker => unlabelled.Contains(speaker.Id) && speaker.DisplayOrdinal is not null)
            .OrderBy(speaker => speaker.DisplayOrdinal).Select(speaker => speaker.Id).ToList();
        var people = meeting.Participants is null ? new List<MeetingParticipant>() : [.. meeting.Participants];
        var synthetic = new Dictionary<int, Guid>();
        for (var index = 0; index < ordinals.Count; index++)
        {
            var id = ordinals[index];
            var key = AcousticSourceKeyPrefix + id.ToString(CultureInfo.InvariantCulture);
            var name = $"Speaker {index + 1}";
            var existing = people.FirstOrDefault(person => person.SourceKey == key);
            if (existing is not null)
            {
                existing.Name = name;
                synthetic[id] = existing.Id;
            }
            else
            {
                var participant = new MeetingParticipant { Name = name, SourceKey = key, ColorIndex = people.Count };
                people.Add(participant);
                synthetic[id] = participant.Id;
            }
        }

        for (var index = 0; index < locations.Count; index++)
        {
            var label = fusion.Words[index];
            var word = transcript[locations[index].Segment].Words![locations[index].Word];
            word.AcousticSpeaker = label.AcousticSpeaker;
            if (word.Attribution == WordAttribution.Manual) continue;
            Guid? participant = label.ParticipantID;
            if (label.Source == FusionLabelSource.Unlabelled)
                participant = label.AcousticSpeaker is int acoustic && synthetic.TryGetValue(acoustic, out var syntheticId) ? syntheticId : null;
            word.ParticipantID = participant;
            word.Attribution = participant is null ? null : WordAttribution.TeamsAccessibility;
        }

        // Drop "Speaker k" records a run no longer needs, unless a word (e.g. a manual one) still points to them. The
        // microphone pass keeps its own records.
        var referenced = Referenced(transcript);
        var keep = synthetic.Values.ToHashSet();
        people.RemoveAll(person => AcousticSpeakerId(person) is int acoustic && !IsMicrophoneSpeaker(acoustic) && !keep.Contains(person.Id)
            && !referenced.Contains(person.Id));
        meeting.Participants = people.Count == 0 && meeting.Participants is null ? null : people;

        var speakers = fusion.Speakers.Select(speaker => new AcousticSpeaker
        {
            Id = speaker.Id, SpeechSeconds = SwiftMath.RoundedMillis(speaker.SpeechSeconds), WordCount = speaker.WordCount,
            ParticipantID = speaker.BoundParticipantID,
            BindingSource = speaker.BoundParticipantID is null ? null : speaker.BindingIsManual ? AcousticBindingSource.Manual : AcousticBindingSource.Vote,
            Suppressed = speaker.Suppressed
        }).ToList();
        // A manual binding outlives a run in which its identity does not occur; the microphone identities stay as they are.
        foreach (var speaker in meeting.AcousticSpeakers ?? [])
        {
            if (IsMicrophoneSpeaker(speaker.Id)) speakers.Add(speaker);
            else if (speaker.BindingSource == AcousticBindingSource.Manual && !speakers.Any(known => known.Id == speaker.Id))
                speakers.Add(new AcousticSpeaker { Id = speaker.Id, ParticipantID = speaker.ParticipantID, BindingSource = AcousticBindingSource.Manual });
        }
        meeting.AcousticSpeakers = speakers.OrderBy(speaker => speaker.Id).ToList();
        // The microphone's "Speaker k" numbers follow the Others ones: renumber them with the stored microphone segments.
        if (meeting.Diarization?.Channels?.Me?.Segments is { } microphone) meeting.ApplyMicrophoneLabels(microphone, policy);
    }

    /// <summary>What the microphone labelling decided (statistics only, no text).</summary>
    /// <param name="Clusters">Clusters with segments.</param>
    /// <param name="Retained">Clusters left after the 10-s and phantom filters.</param>
    /// <param name="SpeechSeconds">Union of the microphone speech.</param>
    /// <param name="OverlapShare">Share of that speech during which two or more clusters speak.</param>
    /// <param name="Relabelled">True when the Me words carry acoustic identities (several voices or overlap ≥ 1 %).</param>
    /// <param name="DominantSpeaker">The cluster treated as the user (most speech among the retained ones); null unless relabelled.</param>
    /// <param name="LabelledWords">Me words that now show another speaker than "Me".</param>
    public readonly record struct MicrophoneLabelling(int Clusters, int Retained, double SpeechSeconds, double OverlapShare, bool Relabelled,
        int? DominantSpeaker, int LabelledWords, int SuppressedIdentities);

    /// <summary>
    /// Labels the timed Me words from the microphone pass's <paramref name="segments"/> (meeting offsets, 0-based clusters),
    /// following the microphone rule: with exactly one retained voice and less than 1 % overlapped speech everything
    /// stays the user (no acoustic identity on Me words). Otherwise every Me word gets its identity
    /// (<see cref="MicrophoneSpeakerBase"/> + cluster); the dominant retained cluster (most speech) stays "Me" (no
    /// participant), every other retained cluster becomes a synthetic "Speaker k" numbered after the Others ones, and weak or
    /// phantom clusters stay "Me". Me words keep <see cref="Speaker.Me"/> as their channel. Manual words and manual bindings
    /// (renamed microphone speakers) win, as on the Others channel. Deterministic and idempotent.
    /// </summary>
    public static MicrophoneLabelling ApplyMicrophoneLabels(this Meeting meeting, IReadOnlyList<AcousticSegment> segments, FusionPolicy? policy = null)
    {
        var transcript = meeting.Transcript;
        var locations = new List<(int Segment, int Word)>();
        for (var segmentIndex = 0; segmentIndex < transcript.Count; segmentIndex++)
        {
            var segment = transcript[segmentIndex];
            if (segment.Speaker != Speaker.Me || segment.Words is null) continue;
            for (var wordIndex = 0; wordIndex < segment.Words.Count; wordIndex++)
            {
                var word = segment.Words[wordIndex];
                if (double.IsFinite(word.Start) && double.IsFinite(word.End)) locations.Add((segmentIndex, wordIndex));
            }
        }
        var knownParticipants = (meeting.Participants ?? []).Select(person => person.Id).ToHashSet();
        var words = locations.Select((location, id) =>
        {
            var segment = transcript[location.Segment];
            var word = segment.Words![location.Word];
            var manual = word.Attribution == WordAttribution.Manual;
            return new FusionWord(id, segment.Offset + word.Start, segment.Offset + Math.Max(word.Start, word.End), null, manual ? word.ParticipantID : null, manual);
        }).ToList();
        var manualBindings = new Dictionary<int, Guid>();
        foreach (var speaker in meeting.AcousticSpeakers ?? [])
        {
            if (IsMicrophoneSpeaker(speaker.Id) && speaker.BindingSource == AcousticBindingSource.Manual && speaker.ParticipantID is Guid participant
                && knownParticipants.Contains(participant))
                manualBindings[speaker.Id - MicrophoneSpeakerBase] = participant;
        }
        var fusion = SpeakerFusion.Fuse(words, segments.Select(segment => new FusionAcousticSegment(segment.Speaker, segment.Start, segment.End)).ToList(),
            manualBindings, policy);
        var retained = fusion.Speakers.Where(speaker => !speaker.Suppressed).ToList();
        var overlap = OverlapShare(segments, out var speech);
        var relabelled = retained.Count > 1 || (retained.Count == 1 && overlap >= MicrophoneOverlapShare);
        int? dominant = retained.Count == 0 ? null
            : retained.OrderByDescending(speaker => speaker.SpeechSeconds).ThenBy(speaker => speaker.Id).First().Id;
        var suppressed = fusion.Speakers.Where(speaker => speaker.Suppressed).Select(speaker => speaker.Id).ToHashSet();
        var bound = fusion.Speakers.Where(speaker => speaker.BoundParticipantID is not null).ToDictionary(speaker => speaker.Id, speaker => speaker.BoundParticipantID!.Value);

        // "Speaker k" for the other retained, unbound clusters that label a word, by first appearance, after the Others ones.
        var people = meeting.Participants is null ? new List<MeetingParticipant>() : [.. meeting.Participants];
        var othersCount = people.Count(person => AcousticSpeakerId(person) is int acoustic && !IsMicrophoneSpeaker(acoustic));
        var synthetic = new Dictionary<int, Guid>();
        if (relabelled)
        {
            var order = new List<int>();
            foreach (var label in fusion.Words.OrderBy(label => words[label.Id].Start).ThenBy(label => label.Id))
            {
                if (label.AcousticSpeaker is int cluster && cluster != dominant && !suppressed.Contains(cluster) && !bound.ContainsKey(cluster) && !order.Contains(cluster))
                    order.Add(cluster);
            }
            for (var index = 0; index < order.Count; index++)
            {
                var key = AcousticSourceKeyPrefix + (MicrophoneSpeakerBase + order[index]).ToString(CultureInfo.InvariantCulture);
                var name = $"Speaker {othersCount + index + 1}";
                var existing = people.FirstOrDefault(person => person.SourceKey == key);
                if (existing is not null)
                {
                    existing.Name = name;
                    synthetic[order[index]] = existing.Id;
                }
                else
                {
                    var participant = new MeetingParticipant { Name = name, SourceKey = key, ColorIndex = ParticipantPalette.NextColorIndex(people) };
                    people.Add(participant);
                    synthetic[order[index]] = participant.Id;
                }
            }
        }

        var labelledWords = 0;
        for (var index = 0; index < locations.Count; index++)
        {
            var label = fusion.Words[index];
            var word = transcript[locations[index].Segment].Words![locations[index].Word];
            word.AcousticSpeaker = relabelled && label.AcousticSpeaker is int cluster ? MicrophoneSpeakerBase + cluster : null;
            if (word.Attribution == WordAttribution.Manual) continue;
            Guid? participant = null;
            if (relabelled && label.AcousticSpeaker is int identity && !suppressed.Contains(identity))
                participant = bound.TryGetValue(identity, out var person) ? person : synthetic.TryGetValue(identity, out var speaker) ? speaker : null;
            word.ParticipantID = participant;
            word.Attribution = participant is null ? null : WordAttribution.TeamsAccessibility;
            if (participant is not null) labelledWords++;
        }

        var referenced = Referenced(transcript);
        var keep = synthetic.Values.ToHashSet();
        people.RemoveAll(person => AcousticSpeakerId(person) is int acoustic && IsMicrophoneSpeaker(acoustic) && !keep.Contains(person.Id)
            && !referenced.Contains(person.Id));
        meeting.Participants = people.Count == 0 && meeting.Participants is null ? null : people;

        var speakers = (meeting.AcousticSpeakers ?? []).Where(speaker => !IsMicrophoneSpeaker(speaker.Id)).ToList();
        if (relabelled)
        {
            speakers.AddRange(fusion.Speakers.Select(speaker => new AcousticSpeaker
            {
                Id = MicrophoneSpeakerBase + speaker.Id, SpeechSeconds = SwiftMath.RoundedMillis(speaker.SpeechSeconds), WordCount = speaker.WordCount,
                ParticipantID = speaker.BoundParticipantID, BindingSource = speaker.BoundParticipantID is null ? null : AcousticBindingSource.Manual,
                Suppressed = speaker.Suppressed
            }));
        }
        // A manual binding outlives a run in which its identity does not occur (or the microphone has one voice).
        foreach (var speaker in meeting.AcousticSpeakers ?? [])
        {
            if (IsMicrophoneSpeaker(speaker.Id) && speaker.BindingSource == AcousticBindingSource.Manual && !speakers.Any(known => known.Id == speaker.Id))
                speakers.Add(new AcousticSpeaker { Id = speaker.Id, ParticipantID = speaker.ParticipantID, BindingSource = AcousticBindingSource.Manual });
        }
        meeting.AcousticSpeakers = speakers.Count == 0 && meeting.AcousticSpeakers is null ? null : speakers.OrderBy(speaker => speaker.Id).ToList();
        return new MicrophoneLabelling(fusion.Speakers.Count, retained.Count, speech, overlap, relabelled, relabelled ? dominant : null, labelledWords,
            suppressed.Count);
    }

    /// <summary>
    /// A manual binding moves to a new run's identity only when both runs clearly heard the same voice: the new identity
    /// covers at least this share of the bound identity's earlier speech, and that speech is at least this share of the new
    /// identity's own (mutual majority).
    /// </summary>
    public const double BindingCarryShare = 0.5;

    /// <summary>What <see cref="PreserveManualBindings"/> did (counts only).</summary>
    /// <param name="Pinned">Words that showed a bound name and now carry it as a manual label.</param>
    /// <param name="Carried">Bindings moved to an identity of the new run.</param>
    /// <param name="Dropped">Bindings without a clear counterpart in the new run (their words stay pinned).</param>
    public readonly record struct BindingCarry(int Pinned, int Carried, int Dropped);

    /// <summary>
    /// Before the Others words are relabelled from a new run: manual bindings (a renamed "Speaker k") are keyed by the old run's
    /// cluster ids, which mean nothing in a new run (Deepgram numbers speakers per request; a local → cloud switch changes every
    /// id; the Teams-only path has no ids at all). Manual wins: every timed Others word that shows a bound name gets that name
    /// as its own manual label (protected from every later pass), so the user's naming can never move to another voice or be
    /// lost. The binding itself moves to the identity of <paramref name="next"/> that matches it by time overlap
    /// (<see cref="BindingCarryShare"/>, largest overlap wins), so new words of that voice are named too; without such a
    /// match (or without <paramref name="next"/>) it is dropped. Microphone identities are left alone. Changes the meeting in place.
    /// </summary>
    public static BindingCarry PreserveManualBindings(this Meeting meeting, IReadOnlyList<AcousticSegment>? previous, IReadOnlyList<AcousticSegment>? next)
    {
        var known = (meeting.Participants ?? []).Select(person => person.Id).ToHashSet();
        var bindings = (meeting.AcousticSpeakers ?? []).Where(speaker => !IsMicrophoneSpeaker(speaker.Id) && speaker.BindingSource == AcousticBindingSource.Manual
            && speaker.ParticipantID is Guid participant && known.Contains(participant)).ToDictionary(speaker => speaker.Id, speaker => speaker.ParticipantID!.Value);
        if (bindings.Count == 0) return default;
        var pinned = PinBoundWords(meeting, Speaker.Others, bindings);
        var carried = new Dictionary<int, (Guid Participant, double Overlap)>();
        if (previous is { Count: > 0 } before && next is { Count: > 0 } after)
        {
            foreach (var (id, participant) in bindings)
            {
                var own = before.Where(segment => segment.Speaker == id).ToList();
                var length = own.Sum(segment => Math.Max(0, segment.End - segment.Start));
                if (length <= 0) continue;
                var best = after.GroupBy(segment => segment.Speaker)
                    .Select(group => (Id: group.Key, Overlap: own.Sum(old => group.Sum(segment => Math.Max(0, Math.Min(old.End, segment.End) - Math.Max(old.Start, segment.Start)))),
                        Length: group.Sum(segment => Math.Max(0, segment.End - segment.Start))))
                    .OrderByDescending(candidate => candidate.Overlap).ThenBy(candidate => candidate.Id).FirstOrDefault();
                if (best.Overlap <= 0 || best.Overlap < BindingCarryShare * length - 1e-9 || best.Overlap < BindingCarryShare * best.Length - 1e-9) continue;
                // Two bindings on one new identity: the larger overlap wins.
                if (carried.TryGetValue(best.Id, out var other) && other.Overlap >= best.Overlap) continue;
                carried[best.Id] = (participant, best.Overlap);
            }
        }
        var speakers = (meeting.AcousticSpeakers ?? []).Where(speaker => IsMicrophoneSpeaker(speaker.Id) || speaker.BindingSource != AcousticBindingSource.Manual)
            .Where(speaker => !carried.ContainsKey(speaker.Id)).ToList();
        speakers.AddRange(carried.Select(pair => new AcousticSpeaker { Id = pair.Key, ParticipantID = pair.Value.Participant, BindingSource = AcousticBindingSource.Manual }));
        meeting.AcousticSpeakers = speakers.Count == 0 ? null : speakers.OrderBy(speaker => speaker.Id).ToList();
        return new BindingCarry(pinned, carried.Count, bindings.Count - carried.Count);
    }

    /// <summary>
    /// The microphone track is always the user ("Me", never split; decision of 25.11.2026): removes what a microphone pass of
    /// an older version left behind (the stored microphone segments, the microphone identities ≥ <see cref="MicrophoneSpeakerBase"/>,
    /// their "Speaker k" participants) and returns every non-manual Me word to the user. Manual wins: a manual word label stays,
    /// and a word that showed the name of a manually bound microphone voice keeps that name as its own manual label. Without
    /// this, every re-apply (<see cref="ApplyAcousticFusion"/>, <see cref="ReapplyAcousticLabels"/>) would bring the old split
    /// back. Changes the meeting in place; true when anything changed.
    /// </summary>
    public static bool ClearMicrophoneLabels(this Meeting meeting)
    {
        var changed = false;
        var known = (meeting.Participants ?? []).Select(person => person.Id).ToHashSet();
        var bindings = (meeting.AcousticSpeakers ?? []).Where(speaker => IsMicrophoneSpeaker(speaker.Id) && speaker.BindingSource == AcousticBindingSource.Manual
            && speaker.ParticipantID is Guid participant && known.Contains(participant)).ToDictionary(speaker => speaker.Id, speaker => speaker.ParticipantID!.Value);
        changed |= PinBoundWords(meeting, Speaker.Me, bindings) > 0;
        foreach (var segment in meeting.Transcript.Where(segment => segment.Speaker == Speaker.Me))
        {
            foreach (var word in segment.Words ?? [])
            {
                if (word.AcousticSpeaker is not null) { word.AcousticSpeaker = null; changed = true; }
                if (word.Attribution == WordAttribution.Manual || word.ParticipantID is null && word.Attribution is null) continue;
                word.ParticipantID = null;
                word.Attribution = null;
                changed = true;
            }
        }
        if (meeting.AcousticSpeakers is { } speakers && speakers.Any(speaker => IsMicrophoneSpeaker(speaker.Id)))
        {
            var kept = speakers.Where(speaker => !IsMicrophoneSpeaker(speaker.Id)).ToList();
            meeting.AcousticSpeakers = kept.Count == 0 ? null : kept;
            changed = true;
        }
        if (meeting.Participants is { } people)
        {
            var referenced = Referenced(meeting.Transcript);
            var remaining = people.Where(person => !(AcousticSpeakerId(person) is int id && IsMicrophoneSpeaker(id) && !referenced.Contains(person.Id))).ToList();
            if (remaining.Count != people.Count) { meeting.Participants = remaining; changed = true; }
        }
        if (meeting.Diarization?.Channels?.Me is { Segments: not null } me)
        {
            me.Segments = null;
            // The record no longer describes labels the meeting shows (the status line would still report detected voices).
            if (me.Status == DiarizationStatus.Completed)
            {
                me.Status = DiarizationStatus.Skipped;
                me.Message = DiarizationRunStates.MicrophonePassOffMessage;
                me.Retained = null;
            }
            changed = true;
        }
        return changed;
    }

    /// <summary>Timed words of <paramref name="channel"/> that show the participant their identity is manually bound to become manual words.</summary>
    private static int PinBoundWords(Meeting meeting, Speaker channel, IReadOnlyDictionary<int, Guid> bindings)
    {
        if (bindings.Count == 0) return 0;
        var pinned = 0;
        foreach (var segment in meeting.Transcript.Where(segment => segment.Speaker == channel))
        {
            foreach (var word in segment.Words ?? [])
            {
                if (word.Attribution == WordAttribution.Manual || word.AcousticSpeaker is not int id || !bindings.TryGetValue(id, out var participant)
                    || word.ParticipantID != participant) continue;
                word.Attribution = WordAttribution.Manual;
                pinned++;
            }
        }
        return pinned;
    }

    /// <summary>
    /// Applies the stored results of both channels again (after a manual binding): the Others segments (which renumber the
    /// microphone's "Speaker k" as well), else the microphone segments alone.
    /// </summary>
    public static void ReapplyAcousticLabels(this Meeting meeting, FusionPolicy? policy = null)
    {
        if (meeting.Diarization?.Segments is { Count: > 0 } others) meeting.ApplyAcousticFusion(others, policy);
        else if (meeting.Diarization?.Channels?.Me?.Segments is { } microphone) meeting.ApplyMicrophoneLabels(microphone, policy);
    }

    /// <summary>Share of the union speech time during which at least two different clusters speak; <paramref name="speech"/> is that union.</summary>
    public static double OverlapShare(IReadOnlyList<AcousticSegment> segments, out double speech)
    {
        var events = segments.Where(segment => segment.End > segment.Start)
            .SelectMany(segment => new[] { (Time: segment.Start, segment.Speaker, Delta: 1), (Time: segment.End, segment.Speaker, Delta: -1) })
            .OrderBy(change => change.Time).ThenBy(change => change.Delta).ToList();
        var active = new Dictionary<int, int>();
        double overlapped = 0, last = 0;
        speech = 0;
        foreach (var change in events)
        {
            var speaking = active.Count(pair => pair.Value > 0);
            if (speaking >= 1) speech += change.Time - last;
            if (speaking >= 2) overlapped += change.Time - last;
            active[change.Speaker] = active.GetValueOrDefault(change.Speaker) + change.Delta;
            last = change.Time;
        }
        return speech > 0 ? overlapped / speech : 0;
    }

    private static HashSet<Guid> Referenced(List<TranscriptSegment> transcript) =>
        transcript.SelectMany(segment => (segment.Words ?? []).Select(word => word.ParticipantID).Append(segment.ParticipantID))
            .Where(id => id is not null).Select(id => id!.Value).ToHashSet();

    /// <summary>
    /// Renaming a "Speaker k" binds its acoustic identity to a person manually. An existing participant with the same
    /// name (ignoring case and surrounding spaces) takes over the identity and the synthetic record is removed; otherwise
    /// the record itself becomes an ordinary, user-named participant. Re-applies the fusion with the stored segments.
    /// Returns false (and changes nothing) when <paramref name="participantID"/> is not a "Speaker k" or the name is blank.
    /// </summary>
    public static bool BindAcousticSpeaker(this Meeting meeting, Guid participantID, string name)
    {
        name = name.Trim();
        if (meeting.Participants is not { } existingPeople) return false;
        var index = existingPeople.FindIndex(person => person.Id == participantID);
        if (index < 0 || AcousticSpeakerId(existingPeople[index]) is not int acousticID || name.Length == 0) return false;
        var people = new List<MeetingParticipant>(existingPeople);
        var normalized = name.ToLowerInvariant();
        Guid target;
        var namesake = people.FirstOrDefault(person => person.Id != participantID && person.Name.Trim().ToLowerInvariant() == normalized);
        if (namesake is not null)
        {
            target = namesake.Id;
            people.RemoveAt(index);
            foreach (var segment in meeting.Transcript)
            {
                if (segment.ParticipantID == participantID) segment.ParticipantID = target;
                foreach (var word in segment.Words ?? []) if (word.ParticipantID == participantID) word.ParticipantID = target;
            }
        }
        else
        {
            target = participantID;
            people[index].Name = name;
            people[index].SourceKey = null;
        }
        meeting.Participants = people;
        var speakers = meeting.AcousticSpeakers is null ? new List<AcousticSpeaker>() : [.. meeting.AcousticSpeakers];
        var known = speakers.FirstOrDefault(speaker => speaker.Id == acousticID);
        if (known is not null)
        {
            known.ParticipantID = target;
            known.BindingSource = AcousticBindingSource.Manual;
            known.Suppressed = false;
        }
        else
        {
            speakers.Add(new AcousticSpeaker { Id = acousticID, ParticipantID = target, BindingSource = AcousticBindingSource.Manual });
        }
        meeting.AcousticSpeakers = speakers.OrderBy(speaker => speaker.Id).ToList();
        // Relabel with the stored segments so the binding applies exactly as a re-run would apply it.
        meeting.ReapplyAcousticLabels();
        return true;
    }
}
