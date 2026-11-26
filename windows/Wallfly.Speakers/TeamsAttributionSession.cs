using Wallfly.Core;

namespace Wallfly.Speakers;

/// <summary>A segment of the transcript that a reconciliation relabelled: <see cref="Index"/> into the input list.</summary>
public readonly record struct SegmentRevision(int Index, TranscriptSegment Segment);

/// <summary>
/// Pure, session-scoped alignment of the Teams speaking indicator with remote word times (1:1 port of
/// <c>TeamsAttributionSession</c>, SpeakerAttribution.swift). No voice, roster-order or text inference.
/// Not thread-safe: one owner ingests and annotates.
/// </summary>
/// <remarks>
/// Pipeline: <see cref="Ingest"/> turns each UI read into one evidence sample (speaker, healthy gap or barrier,
/// backdated by <see cref="TeamsAttributionPolicy.DetectionLag"/>); <see cref="Decision"/> is the raw per-word label
/// (one participant covers at least 75 % of the word); <c>Annotate</c> splits remote words into utterances, keeps only
/// established runs of the raw label as turns and votes one owner per turn; <see cref="Reconcile"/> applies it to the
/// live revision window; <see cref="ExportTimeline"/> compresses the evidence for <c>Meeting.SpeakerTimeline</c>.
/// </remarks>
public sealed class TeamsAttributionSession
{
    private readonly List<TeamsEvidenceSample> samples = [];
    private readonly Dictionary<string, Guid> participantIDs = new(StringComparer.Ordinal);
    private readonly Dictionary<Guid, string> observedNames = [];
    private readonly Dictionary<Guid, string> stableParticipantKeys = [];
    private readonly List<double> contextBoundaries = [];
    private string? contextID;

    public TeamsAttributionSession(Guid id, double origin, double baseDuration, TeamsAttributionPolicy? policy = null)
    {
        Id = id; Origin = origin; BaseDuration = baseDuration; Policy = policy ?? new TeamsAttributionPolicy();
    }

    /// <summary>The recording session (<c>TranscriptSegment.SessionID</c>) whose remote words this session labels.</summary>
    public Guid Id { get; }
    /// <summary>Clock value (seconds) of the session start on the observation clock.</summary>
    public double Origin { get; }
    /// <summary>Meeting offset of <see cref="Origin"/>: the duration of earlier sessions of the same meeting.</summary>
    public double BaseDuration { get; }
    public TeamsAttributionPolicy Policy { get; }
    public IReadOnlyList<TeamsEvidenceSample> Samples => samples;
    public IReadOnlyList<double> ContextBoundaries => contextBoundaries;
    public TeamsSpeakerObservation? LastObservation { get; private set; }
    /// <summary>Meeting offset of the latest ingested observation (not lag-corrected).</summary>
    public double LatestOffset { get; private set; }

    /// <summary>
    /// Rebuilds a session from a persisted timeline for offline replay (Swift <c>init(id:timeline:policy:)</c>). Samples
    /// are placed so that each interval is covered exactly as before (speaker intervals with samples at most
    /// <see cref="TeamsAttributionPolicy.EvidenceLifetime"/> apart, the last one ending at the interval end); name and
    /// identity state is not restored, so the result cannot ingest meaningfully.
    /// </summary>
    public static TeamsAttributionSession FromTimeline(Guid id, IEnumerable<SpeakerTimelineEntry> timeline, TeamsAttributionPolicy? policy = null)
    {
        var entries = timeline.OrderBy(entry => entry.Start).ToList();
        var session = new TeamsAttributionSession(id, 0, entries.Count > 0 ? entries[0].Start : 0, policy);
        var lifetime = Math.Max(session.Policy.EvidenceLifetime, 1e-3);
        foreach (var entry in entries)
        {
            switch (entry.Kind)
            {
                case SpeakerTimelineKind.Context:
                    session.contextBoundaries.Add(entry.Start);
                    break;
                case SpeakerTimelineKind.Gap:
                    session.samples.Add(new(entry.Start, TeamsEvidence.HealthyGap));
                    break;
                case SpeakerTimelineKind.Barrier:
                    var reason = TeamsAttributionFailureRaw.TryParse(entry.Barrier, out var parsed) ? parsed : TeamsAttributionFailure.Unavailable;
                    session.samples.Add(new(entry.Start, TeamsEvidence.Barrier(reason)));
                    break;
                case SpeakerTimelineKind.Speaker:
                    if (entry.ParticipantID is not Guid person) continue;
                    var last = entry.End - lifetime;
                    if (last <= entry.Start + 1e-9)
                    {
                        session.samples.Add(new(entry.Start, TeamsEvidence.Speaker(person)));
                    }
                    else
                    {
                        var steps = (int)Math.Ceiling((last - entry.Start) / lifetime - 1e-9);
                        for (var step = 0; step <= steps; step++)
                        {
                            session.samples.Add(new(entry.Start + (last - entry.Start) * step / steps, TeamsEvidence.Speaker(person)));
                        }
                    }
                    break;
            }
        }
        session.LatestOffset = entries.Count > 0 ? entries.Max(entry => entry.End) : 0;
        return session;
    }

    /// <summary>
    /// Compresses the ingested samples into intervals for persistence (port of <c>exportTimeline(until:)</c>).
    /// Consecutive samples with the same state merge; a speaker sample covers up to
    /// <see cref="TeamsAttributionPolicy.EvidenceLifetime"/>, the final gap or barrier runs to <paramref name="until"/>
    /// (default: the latest observation). Times are rounded to milliseconds. <see cref="FromTimeline"/> on the result
    /// yields the same decisions.
    /// </summary>
    public List<SpeakerTimelineEntry> ExportTimeline(double? until = null)
    {
        var stop = Math.Max(LatestOffset, until ?? LatestOffset);
        var result = new List<SpeakerTimelineEntry>();
        void Append(SpeakerTimelineKind kind, double start, double end, Guid? participant = null, string? barrier = null)
        {
            if (!(end > start + 1e-9)) return;
            if (result.Count > 0)
            {
                var last = result[^1];
                if (last.Kind == kind && last.ParticipantID == participant && last.Barrier == barrier && start <= last.End + 1e-6)
                {
                    last.End = Math.Max(last.End, end);
                    return;
                }
            }
            result.Add(new() { Kind = kind, Start = start, End = end, ParticipantID = participant, Barrier = barrier });
        }
        for (var index = 0; index < samples.Count; index++)
        {
            var sample = samples[index];
            var hasNext = index + 1 < samples.Count;
            var next = hasNext ? samples[index + 1].Offset : Math.Max(stop, sample.Offset);
            switch (sample.Evidence.Kind)
            {
                case TeamsEvidenceKind.Speaker:
                    var limit = sample.Offset + Policy.EvidenceLifetime;
                    Append(SpeakerTimelineKind.Speaker, sample.Offset, hasNext ? Math.Min(next, limit) : limit, sample.Evidence.Participant);
                    break;
                case TeamsEvidenceKind.HealthyGap:
                    if (GapOwner(index) is Guid owner) Append(SpeakerTimelineKind.Speaker, sample.Offset, next, owner);
                    else Append(SpeakerTimelineKind.Gap, sample.Offset, next);
                    break;
                case TeamsEvidenceKind.Barrier:
                    Append(SpeakerTimelineKind.Barrier, sample.Offset, next, barrier: sample.Evidence.Reason.RawValue());
                    break;
            }
        }
        foreach (var boundary in contextBoundaries)
        {
            var position = result.FindIndex(entry => entry.Start >= boundary);
            result.Insert(position < 0 ? result.Count : position, new() { Kind = SpeakerTimelineKind.Context, Start = boundary, End = boundary });
        }
        foreach (var entry in result)
        {
            entry.Start = SwiftMath.RoundedMillis(entry.Start);
            entry.End = SwiftMath.RoundedMillis(entry.End);
        }
        return result;
    }

    /// <summary>
    /// Adds one UI read as an evidence sample and creates or renames this session's participants in
    /// <paramref name="participants"/> (the meeting's list, changed in place). Reads older than the last one, before
    /// <see cref="Origin"/> or with a non-finite timestamp are ignored.
    /// </summary>
    public void Ingest(TeamsSpeakerObservation observation, List<MeetingParticipant> participants)
    {
        ArgumentNullException.ThrowIfNull(observation);
        ArgumentNullException.ThrowIfNull(participants);
        if (!double.IsFinite(observation.Timestamp) || observation.Timestamp < Origin
            || (LastObservation is not null && observation.Timestamp < LastObservation.Timestamp)) return;
        var rawOffset = BaseDuration + observation.Timestamp - Origin;
        var newContext = observation.ContextID ?? observation.SurfaceID;
        var changedContext = contextID is not null && newContext is not null && contextID != newContext;
        if (changedContext)
        {
            participantIDs.Clear(); observedNames.Clear(); stableParticipantKeys.Clear();
            contextBoundaries.Add(rawOffset);
        }
        if (newContext is not null) contextID = newContext;
        LastObservation = observation; LatestOffset = rawOffset;

        var remote = observation.Participants.Where(person => !person.IsSelf && person.Name.Trim().Length > 0).ToList();
        var stableCounts = remote.Where(person => person.StableKey is not null).GroupBy(person => person.StableKey!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.Count(), StringComparer.Ordinal);
        string? UniqueStable(TeamsObservedParticipant person) =>
            person.StableKey is not null && stableCounts.GetValueOrDefault(person.StableKey) == 1 ? person.StableKey : null;
        var candidates = new List<Guid>();
        var unresolvedActive = false;
        // Elements with a stable key first: a name-only element of the same person (a second tile) then joins it.
        foreach (var observed in remote.OrderBy(person => UniqueStable(person) is null ? 1 : 0))
        {
            var stableKey = UniqueStable(observed);
            var name = Normalized(observed.Name);
            if ((stableKey is not null ? ResolveStable(stableKey, name, observed, participants) : ResolveByName(name, observed, participants))
                is not Guid participantID)
            {
                // Two people with this name and different stable keys: a name alone cannot tell which one speaks.
                if (observed.Active) unresolvedActive = true;
                continue;
            }
            if (stableKey is not null && observedNames.TryGetValue(participantID, out var previousName) && previousName != name)
            {
                // A stable identity whose display name changed (e.g. a guest that signed in): the participant follows,
                // unless the user renamed it.
                var existing = participants.FirstOrDefault(person => person.Id == participantID);
                if (existing is not null && Normalized(existing.Name) == previousName) existing.Name = observed.Name;
            }
            observedNames[participantID] = name;
            if (stableKey is not null) stableParticipantKeys[participantID] = stableKey;
            if (observed.Active && !candidates.Contains(participantID)) candidates.Add(participantID);
        }

        TeamsEvidence evidence;
        if (observation.Participants.Any(person => person.IsSelf && person.Active) || observation.Status == TeamsSpeakerStatus.SelfSpeaking
            || observation.Failure == TeamsObservationFailure.LocalUserSpeaking)
            evidence = TeamsEvidence.Barrier(TeamsAttributionFailure.LocalUserSpeaking);
        else if (observation.Status == TeamsSpeakerStatus.Ambiguous || candidates.Count > 1 || unresolvedActive
                 || observation.Failure == TeamsObservationFailure.UnidentifiedSpeaker || observation.Failure == TeamsObservationFailure.ConflictingViews)
            evidence = TeamsEvidence.Barrier(TeamsAttributionFailure.Ambiguous);
        else if (observation.Failure is not null
                 || (observation.Status != TeamsSpeakerStatus.Active && observation.Status != TeamsSpeakerStatus.WaitingForSpeaker))
            evidence = TeamsEvidence.Barrier(TeamsAttributionFailure.Unavailable);
        else if (candidates.Count > 0)
            evidence = TeamsEvidence.Speaker(candidates[0]);
        else
            evidence = TeamsEvidence.HealthyGap;

        // A recovery never backdates across an explicit failure or process boundary.
        var recovering = samples.Count > 0 && samples[^1].Evidence.Kind == TeamsEvidenceKind.Barrier;
        var isBarrier = evidence.Kind == TeamsEvidenceKind.Barrier;
        var corrected = rawOffset - (recovering || isBarrier || changedContext ? 0 : Math.Max(0, Policy.DetectionLag));
        var offset = Math.Max(BaseDuration, Math.Max(samples.Count > 0 ? samples[^1].Offset : BaseDuration, corrected));
        samples.Add(new(offset, evidence));
    }

    /// <summary>The raw per-word participant, or null.</summary>
    public Guid? Participant(double start, double end) => Decision(start, end).ParticipantID;

    /// <summary>
    /// The strict per-word decision: a participant shown as speaking for at least
    /// <see cref="TeamsAttributionPolicy.MinimumWordCoverage"/> of [start, end) (meeting offsets) without a barrier,
    /// stale evidence or context change in between.
    /// </summary>
    public TeamsWordDecision Decision(double start, double end)
    {
        if (!double.IsFinite(start) || !double.IsFinite(end) || !(end > start) || samples.Count == 0)
            return TeamsWordDecision.Unknown(TeamsAttributionFailure.NoObservation);
        if (contextBoundaries.Any(boundary => start < boundary && boundary < end))
            return TeamsWordDecision.Unknown(TeamsAttributionFailure.ContextChanged);
        int low = 0, high = samples.Count;
        while (low < high) { var mid = (low + high) / 2; if (samples[mid].Offset <= start) low = mid + 1; else high = mid; }
        if (low == 0) return TeamsWordDecision.Unknown(TeamsAttributionFailure.NoObservation);
        var index = low - 1;
        var cursor = start;
        var coverage = new Dictionary<Guid, double>();
        var order = new List<Guid>();
        void Add(Guid person, double amount)
        {
            if (coverage.TryGetValue(person, out var value)) coverage[person] = value + amount;
            else { coverage[person] = amount; order.Add(person); }
        }
        while (cursor < end)
        {
            var sample = samples[index];
            var next = index + 1 < samples.Count ? samples[index + 1].Offset : double.PositiveInfinity;
            var stop = Math.Min(end, next);
            if (stop <= cursor) { index++; continue; }
            switch (sample.Evidence.Kind)
            {
                case TeamsEvidenceKind.Barrier:
                    return TeamsWordDecision.Unknown(sample.Evidence.Reason);
                case TeamsEvidenceKind.Speaker:
                    if (!(stop <= sample.Offset + Policy.EvidenceLifetime + 1e-9)) return TeamsWordDecision.Unknown(TeamsAttributionFailure.Stale);
                    Add(sample.Evidence.Participant, stop - cursor);
                    break;
                case TeamsEvidenceKind.HealthyGap:
                    if (GapOwner(index) is Guid owner) Add(owner, stop - cursor);
                    break;
            }
            cursor = stop;
            if (cursor < end) index++;
        }
        // Swift max(by:) keeps the first of equal maxima.
        Guid? best = null;
        var bestValue = double.NegativeInfinity;
        foreach (var person in order) if (coverage[person] > bestValue) { best = person; bestValue = coverage[person]; }
        if (best is null || !(bestValue / (end - start) + 1e-9 >= Policy.MinimumWordCoverage))
            return TeamsWordDecision.Unknown(TeamsAttributionFailure.InsufficientCoverage);
        return new(best);
    }

    /// <summary>The speaker on both sides of a short healthy gap, or null.</summary>
    private Guid? GapOwner(int index)
    {
        int left = index, right = index;
        while (left > 0 && samples[left - 1].Evidence.Kind == TeamsEvidenceKind.HealthyGap)
        {
            left--;
            if (samples[index].Offset - samples[left].Offset > Policy.MaximumHealthyGap) return null;
        }
        while (right + 1 < samples.Count && samples[right + 1].Evidence.Kind == TeamsEvidenceKind.HealthyGap)
        {
            right++;
            if (samples[right].Offset - samples[left].Offset > Policy.MaximumHealthyGap) return null;
        }
        if (left == 0 || right + 1 >= samples.Count) return null;
        var before = samples[left - 1].Evidence;
        var after = samples[right + 1].Evidence;
        if (before.Kind != TeamsEvidenceKind.Speaker || after.Kind != TeamsEvidenceKind.Speaker || before.Participant != after.Participant) return null;
        if (!(samples[right + 1].Offset - samples[left].Offset <= Policy.MaximumHealthyGap + 1e-9)) return null;
        if (!(samples[left].Offset <= samples[left - 1].Offset + Policy.EvidenceLifetime)) return null;
        return before.Participant;
    }

    private static string Normalized(string name) => name.Trim().ToLowerInvariant();

    // ---- participant identity (Windows) ------------------------------------------------------------------------------
    //
    // A remote participant is one person per normalized display name, unless two DIFFERENT stable keys carry that name
    // (genuinely two people). UIA runtime ids of tiles are transient (Teams/Chromium re-create a tile on every layout
    // change, and one person may show as two tiles at once), so an element key never creates a participant: before
    // 2026-11-24 a 1:1 call produced 14 entries for one colleague. The stored SourceKey is "<context>/stable/<key>" or
    // "<context>/name/<normalized name>".

    /// <summary>The participant of an element with a stable key unique in its observation.</summary>
    private Guid ResolveStable(string stableKey, string name, TeamsObservedParticipant observed, List<MeetingParticipant> participants)
    {
        var sourceKey = $"{contextID ?? "unknown"}/stable/{stableKey}";
        if (participantIDs.TryGetValue(sourceKey, out var known)) return known;
        // The same name without a stable key yet (or with this one): the same person; a different stable key is someone else.
        var byName = observedNames.Where(pair => pair.Value == name
                && (!stableParticipantKeys.TryGetValue(pair.Key, out var stable) || stable == stableKey))
            .Select(pair => pair.Key).ToList();
        var id = Earliest(byName, participants) ?? Same(participants, sourceKey) ?? Earlier(participants, name, stableKey) ?? Create(sourceKey, observed, participants);
        participantIDs[sourceKey] = id;
        return id;
    }

    /// <summary>This session's participant with exactly this key (a context that comes back).</summary>
    private Guid? Same(List<MeetingParticipant> participants, string sourceKey) =>
        participants.FirstOrDefault(person => person.SourceKey == sourceKey && person.SessionID == Id)?.Id;

    /// <summary>The participant of an element without a unique stable key: its normalized name; null when two stable people share it.</summary>
    private Guid? ResolveByName(string name, TeamsObservedParticipant observed, List<MeetingParticipant> participants)
    {
        var byName = observedNames.Where(pair => pair.Value == name).Select(pair => pair.Key).ToList();
        if (byName.Select(person => stableParticipantKeys.GetValueOrDefault(person)).Where(stable => stable is not null).Distinct(StringComparer.Ordinal).Count() > 1)
            return null;
        var sourceKey = $"{contextID ?? "unknown"}/name/{name}";
        if (participantIDs.TryGetValue(sourceKey, out var known)) return known;
        var id = Earliest(byName, participants) ?? Same(participants, sourceKey) ?? Earlier(participants, name, null) ?? Create(sourceKey, observed, participants);
        participantIDs[sourceKey] = id;
        return id;
    }

    /// <summary>The first of <paramref name="ids"/> in the meeting's participant order.</summary>
    private static Guid? Earliest(List<Guid> ids, List<MeetingParticipant> participants)
    {
        if (ids.Count == 0) return null;
        foreach (var person in participants) if (ids.Contains(person.Id)) return person.Id;
        return ids[0];
    }

    /// <summary>
    /// A Teams participant of this meeting from an EARLIER recording session (a resumed recording) with this name (its
    /// observed name in the source key, or its current name), whose stable key does not contradict
    /// <paramref name="stableKey"/>; the earliest wins. Participants of this session's earlier contexts (another call or
    /// window) are not reused (port of the macOS rule "window changes do not mix participants").
    /// </summary>
    private MeetingParticipant? EarlierParticipant(List<MeetingParticipant> participants, string name, string? stableKey) =>
        participants.FirstOrDefault(person => TeamsParticipantKeys.IsTeams(person) && person.SessionID != Id
            && (TeamsParticipantKeys.ObservedName(person.SourceKey) == name || Normalized(person.Name) == name)
            && !(stableParticipantKeys.TryGetValue(person.Id, out var claimed) && claimed != stableKey && stableKey is not null)
            && !(stableKey is not null && TeamsParticipantKeys.StableKey(person.SourceKey) is { } stored && stored != stableKey));

    private Guid? Earlier(List<MeetingParticipant> participants, string name, string? stableKey) => EarlierParticipant(participants, name, stableKey)?.Id;

    private Guid Create(string sourceKey, TeamsObservedParticipant observed, List<MeetingParticipant> participants)
    {
        var participant = new MeetingParticipant { Name = observed.Name, SourceKey = sourceKey, SessionID = Id, ColorIndex = participants.Count };
        participants.Add(participant);
        return participant.Id;
    }

    /// <summary>Counters for the status line over <paramref name="segments"/>; <paramref name="now"/> is on the observation clock.</summary>
    public TeamsSpeakerDiagnostics Diagnostics(IEnumerable<TranscriptSegment> segments, double now)
    {
        var result = new TeamsSpeakerDiagnostics();
        if (LastObservation is { } observation)
        {
            result.ParticipantCount = observation.Participants.Count(person => !person.IsSelf);
            result.ObservedAt = observation.Timestamp;
            result.ObservationAge = Math.Max(0, now - observation.Timestamp);
            result.ReadDuration = observation.ReadDuration;
            result.ObservationFailure = observation.Failure;
        }
        var recent = segments.Where(segment => segment.SessionID == Id && segment.Speaker == Speaker.Others && segment.IsFinal && segment.Text.Length > 0).ToList();
        TranscriptSegment? latest = null;
        foreach (var segment in recent) if (latest is null || segment.Offset > latest.Offset) latest = segment;
        result.LatestRemoteHasWordTimes = latest is null ? null : (latest.Words?.Count ?? 0) > 0;
        foreach (var segment in recent)
        {
            if (segment.Words is not { Count: > 0 } words)
            {
                result.UntimedChunks++;
                Count(result, TeamsAttributionFailure.MissingWordTimes);
                continue;
            }
            foreach (var word in words)
            {
                result.TimedWords++;
                if (word.ParticipantID is not null) result.NamedWords++;
                else if (word.Attribution != WordAttribution.Manual)
                    Count(result, Decision(segment.Offset + word.Start, segment.Offset + word.End).Failure ?? TeamsAttributionFailure.InsufficientCoverage);
            }
        }
        return result;

        static void Count(TeamsSpeakerDiagnostics diagnostics, TeamsAttributionFailure reason) =>
            diagnostics.UnknownReasons[reason] = diagnostics.UnknownReasons.GetValueOrDefault(reason) + 1;
    }

    /// <summary>Labels the remote words of one segment (see the list overload).</summary>
    public TranscriptSegment Annotate(TranscriptSegment original) => Annotate([original])[0];

    /// <summary>
    /// Labels remote words per utterance instead of per word. Utterances span recognition results (finals and previews
    /// alike) and end only at a word gap of <see cref="TeamsAttributionPolicy.UnitGap"/>, a context change or a manual word.
    /// Inside an utterance only established runs of the raw per-word decision start a new turn; each turn then gets one
    /// owner from an overlap-weighted vote of the UI intervals, or none. Manual words are never relabelled. With
    /// <paramref name="revisableAfter"/>, utterances ending before it stay as they are, unless they contain a word of a
    /// segment in <paramref name="alwaysRevising"/> (a late recognition result). <paramref name="finalPass"/> additionally
    /// drops identities that only appear through indicator flicker.
    /// </summary>
    /// <returns>
    /// A list parallel to <paramref name="segments"/>. A segment whose words do not change is returned as the same
    /// instance; a changed one is a copy. The input is never modified.
    /// </returns>
    public List<TranscriptSegment> Annotate(IReadOnlyList<TranscriptSegment> segments, double? revisableAfter = null,
        IReadOnlySet<Guid>? alwaysRevising = null, bool finalPass = false)
    {
        var utterances = SplitUtterances(segments);
        var result = new List<TranscriptSegment>(segments);
        if (utterances.Count == 0) return result;
        var low = utterances[0][0].Start;
        var high = utterances.SelectMany(utterance => utterance).Max(word => word.End);
        var intervals = UiIntervals(low - Policy.VoteLead, high + Policy.VoteTrail);
        var turns = new List<AttributedTurn>();
        foreach (var utterance in utterances)
        {
            var utteranceEnd = utterance.Max(word => word.End);
            var revisable = (revisableAfter is not double cutoff || utteranceEnd >= cutoff)
                || (alwaysRevising is not null && utterance.Any(word => alwaysRevising.Contains(segments[word.Segment].Id)));
            foreach (var run in SpeakerRuns(utterance))
            {
                (Guid? Owner, double End)? previous = turns.Count > 0 ? (turns[^1].Owner, turns[^1].End) : null;
                var (decision, share) = Vote(run.Start, run.End, previous, intervals);
                turns.Add(new AttributedTurn(run.Words, run.Start, run.End, revisable) { Owner = decision.ParticipantID, Share = share });
            }
        }
        if (finalPass) SuppressFlickerIdentities(turns);
        var copied = new bool[segments.Count];
        foreach (var turn in turns)
        {
            if (!turn.Revisable) continue;
            var attribution = turn.Owner is null ? (WordAttribution?)null : WordAttribution.TeamsAccessibility;
            foreach (var word in turn.Words)
            {
                var current = result[word.Segment].Words![word.Index];
                if (current.ParticipantID == turn.Owner && current.Attribution == attribution
                    && current.UiParticipantID == word.Raw && current.UtteranceParticipantID == turn.Owner) continue;
                if (!copied[word.Segment]) { result[word.Segment] = SpeakerModelCopy.Clone(result[word.Segment]); copied[word.Segment] = true; }
                var target = result[word.Segment].Words![word.Index];
                target.ParticipantID = turn.Owner;
                target.Attribution = attribution;
                target.UiParticipantID = word.Raw;
                target.UtteranceParticipantID = turn.Owner;
            }
        }
        return result;
    }

    /// <summary>The owner an utterance from <paramref name="start"/> to <paramref name="end"/> gets from the UI evidence around it.</summary>
    public TeamsWordDecision UtteranceDecision(double start, double end, Guid? previousOwner = null, double previousEnd = double.NegativeInfinity)
    {
        var intervals = UiIntervals(start - Policy.VoteLead, end + Policy.VoteTrail);
        return Vote(start, end, (previousOwner, previousEnd), intervals).Decision;
    }

    /// <summary>
    /// The live revision window (port of <c>MeetingStore.reconcileTeamsTranscript</c>). Reads this session's remote
    /// segments that end at most <see cref="TeamsAttributionPolicy.UnitLookback"/> before the window
    /// (<see cref="LatestOffset"/> minus <see cref="TeamsAttributionPolicy.RevisionWindow"/>), plus those around a just
    /// <paramref name="arrived"/> segment, and relabels utterances ending inside the window or containing the arrived
    /// segment. <paramref name="all"/> is the final pass at stop: every segment, flicker suppression on.
    /// </summary>
    /// <returns>Only the segments whose labels changed (copies), with their index in <paramref name="transcript"/>.</returns>
    public List<SegmentRevision> Reconcile(IReadOnlyList<TranscriptSegment> transcript, bool all = false, Guid? arrived = null)
    {
        var cutoff = LatestOffset - Policy.RevisionWindow;
        var lookback = Policy.UnitLookback;
        var arrivedSegment = arrived is null ? null : transcript.FirstOrDefault(segment => segment.Id == arrived && segment.Speaker == Speaker.Others);
        (double Lower, double Upper)? late = arrivedSegment is null ? null
            : (arrivedSegment.Offset - lookback, arrivedSegment.Offset + arrivedSegment.Duration + lookback);
        var batch = new List<int>();
        for (var index = 0; index < transcript.Count; index++)
        {
            var segment = transcript[index];
            var end = segment.Offset + segment.Duration;
            if (segment.Speaker != Speaker.Others || segment.SessionID != Id) continue;
            if (all || end >= cutoff - lookback || (late is { } window && end >= window.Lower && segment.Offset <= window.Upper)) batch.Add(index);
        }
        var annotated = Annotate(batch.Select(index => transcript[index]).ToList(), all ? null : cutoff,
            arrived is Guid id ? new HashSet<Guid> { id } : null, all);
        var changes = new List<SegmentRevision>();
        for (var position = 0; position < batch.Count; position++)
        {
            if (!ReferenceEquals(annotated[position], transcript[batch[position]])) changes.Add(new(batch[position], annotated[position]));
        }
        return changes;
    }

    /// <summary>A timed, automatically labelled remote word in absolute meeting time; <see cref="Raw"/> is its per-word decision.</summary>
    private record struct WordRef(int Segment, int Index, double Start, double End)
    {
        public Guid? Raw { get; set; }
    }

    /// <summary>A merged stretch in which Teams showed exactly one remote participant as speaking.</summary>
    private readonly record struct UIInterval(Guid ParticipantID, double Start, double End);

    /// <summary>Consecutive words of one utterance that share a label.</summary>
    private sealed class Run(Guid? label, List<WordRef> words)
    {
        public Guid? Label { get; } = label;
        public List<WordRef> Words { get; set; } = words;
        public double Start => Words.Count > 0 ? Words[0].Start : 0;
        public double End => Words.Count > 0 ? Words.Max(word => word.End) : 0;
        public double Span => End - Start;
    }

    /// <summary>A turn with its voted owner; <see cref="Revisable"/> is false for frozen utterances, whose labels are kept.</summary>
    private sealed record AttributedTurn(List<WordRef> Words, double Start, double End, bool Revisable)
    {
        public Guid? Owner { get; set; }
        public double Share { get; init; }
    }

    /// <summary>Splits this session's timed remote words into utterances, in time order.</summary>
    private List<List<WordRef>> SplitUtterances(IReadOnlyList<TranscriptSegment> segments)
    {
        var words = new List<(WordRef Ref, bool Manual)>();
        for (var segmentIndex = 0; segmentIndex < segments.Count; segmentIndex++)
        {
            var segment = segments[segmentIndex];
            if (segment.Speaker != Speaker.Others || segment.SessionID != Id || segment.Words is null) continue;
            for (var index = 0; index < segment.Words.Count; index++)
            {
                var word = segment.Words[index];
                if (!double.IsFinite(word.Start) || !double.IsFinite(word.End)) continue;
                words.Add((new WordRef(segmentIndex, index, segment.Offset + word.Start, segment.Offset + word.End), word.Attribution == WordAttribution.Manual));
            }
        }
        words.Sort((a, b) =>
        {
            var order = a.Ref.Start.CompareTo(b.Ref.Start);
            if (order != 0) return order;
            order = a.Ref.Segment.CompareTo(b.Ref.Segment);
            return order != 0 ? order : a.Ref.Index.CompareTo(b.Ref.Index);
        });
        var result = new List<List<WordRef>>();
        var current = new List<WordRef>();
        double currentEnd = 0;
        foreach (var (word, manual) in words)
        {
            if (current.Count > 0)
            {
                var previous = current[^1];
                if (manual || word.Start - currentEnd >= Policy.UnitGap - 1e-6
                    || contextBoundaries.Any(boundary => previous.Start < boundary && boundary <= word.Start))
                {
                    result.Add(current); current = [];
                }
            }
            if (manual) continue;
            currentEnd = current.Count == 0 ? word.End : Math.Max(currentEnd, word.End);
            current.Add(word);
        }
        if (current.Count > 0) result.Add(current);
        return result;
    }

    /// <summary>Runs of the raw word decision; runs too short to be a real change of speaker are merged into a neighbour.</summary>
    private List<Run> SpeakerRuns(List<WordRef> utterance)
    {
        var runs = new List<Run>();
        foreach (var original in utterance)
        {
            var word = original;
            var label = Decision(word.Start, word.End).ParticipantID;
            word.Raw = label;
            if (runs.Count > 0 && runs[^1].Label == label) runs[^1].Words.Add(word);
            else runs.Add(new Run(label, [word]));
        }
        while (ShortestMergeableRun(runs) is { } merge)
        {
            var (index, target) = merge;
            if (index - 1 >= 0 && index + 1 < runs.Count && runs[index - 1].Label == runs[index + 1].Label)
            {
                // The same speaker on both sides: the short run was an interruption of one turn.
                runs[index - 1].Words.AddRange(runs[index].Words);
                runs[index - 1].Words.AddRange(runs[index + 1].Words);
                runs.RemoveRange(index, 2);
            }
            else if (target < index)
            {
                runs[target].Words.AddRange(runs[index].Words);
                runs.RemoveAt(index);
            }
            else
            {
                runs[target].Words = [.. runs[index].Words, .. runs[target].Words];
                runs.RemoveAt(index);
            }
        }
        return runs;
    }

    /// <summary>
    /// The shortest run that is not an established turn and has a neighbour it may join (the longer one).
    /// A named run never joins an unnamed one.
    /// </summary>
    private (int Index, int Target)? ShortestMergeableRun(List<Run> runs)
    {
        (int Index, int Target)? best = null;
        for (var index = 0; index < runs.Count; index++)
        {
            if (IsEstablished(runs[index])) continue;
            int? target = null;
            foreach (var neighbour in new[] { index - 1, index + 1 })
            {
                if (neighbour < 0 || neighbour >= runs.Count) continue;
                if (runs[index].Label is not null && runs[neighbour].Label is null) continue;
                // Swift max(by:) keeps the first of equal maxima (the left neighbour).
                if (target is null || Less(runs[target.Value], runs[neighbour])) target = neighbour;
            }
            if (target is null) continue;
            if (best is { } current && !Less(runs[index], runs[current.Index])) continue;
            best = (index, target.Value);
        }
        return best;

        static bool Less(Run left, Run right) =>
            left.Span < right.Span || (left.Span == right.Span && left.Words.Count < right.Words.Count);
    }

    private bool IsEstablished(Run run) =>
        run.Words.Count >= Policy.MinTurnWords && run.Span + 1e-9 >= (run.Label is null ? Policy.MinUnknownRun : Policy.MinTurnDuration);

    /// <summary>Overlap-weighted vote of UI intervals for one turn; abstains rather than guessing.</summary>
    private (TeamsWordDecision Decision, double Share) Vote(double start, double end, (Guid? Owner, double End)? previous, List<UIInterval> intervals)
    {
        static double Overlap(UIInterval interval, double low, double high) => Math.Max(0, Math.Min(interval.End, high) - Math.Max(interval.Start, low));
        var inside = new Dictionary<Guid, double>();
        var score = new Dictionary<Guid, double>();
        var startDistance = new Dictionary<Guid, double>();
        foreach (var interval in intervals)
        {
            if (!(interval.End > start - Policy.VoteLead && interval.Start < end + Policy.VoteTrail)) continue;
            var person = interval.ParticipantID;
            var within = Overlap(interval, start, end);
            var around = Overlap(interval, start - Policy.VoteLead, start) + Overlap(interval, end, end + Policy.VoteTrail);
            inside[person] = inside.GetValueOrDefault(person) + within;
            score[person] = score.GetValueOrDefault(person) + within + Policy.OutsideWeight * around;
            startDistance[person] = Math.Min(startDistance.GetValueOrDefault(person, double.PositiveInfinity), Math.Abs(interval.Start - start));
        }
        var ranked = score.Where(pair => pair.Value > 0).ToList();
        ranked.Sort((a, b) => a.Value != b.Value ? b.Value.CompareTo(a.Value) : UuidOrder.Compare(a.Key, b.Key));
        if (ranked.Count == 0) return (TeamsWordDecision.Unknown(TeamsAttributionFailure.NoObservation), 0);
        var winner = ranked[0];
        if (!(inside.GetValueOrDefault(winner.Key) / Math.Max(end - start, 1e-9) + 1e-9 >= Policy.MinUnitCoverage))
            return (TeamsWordDecision.Unknown(TeamsAttributionFailure.InsufficientCoverage), 0);
        var total = ranked.Aggregate(0.0, (sum, pair) => sum + pair.Value);
        var share = winner.Value / total;
        if (ranked.Count < 2 || !(ranked[1].Value >= Policy.CloseVoteRatio * winner.Value)) return (new(winner.Key), share);
        var runnerUp = ranked[1];
        if (end - start <= Policy.ShortUnitDuration + 1e-9)
        {
            // A short utterance belongs to whoever started speaking around its start.
            var nearer = startDistance.GetValueOrDefault(runnerUp.Key, double.PositiveInfinity) < startDistance.GetValueOrDefault(winner.Key, double.PositiveInfinity)
                ? runnerUp : winner;
            return (new(nearer.Key), nearer.Value / total);
        }
        if (previous is { Owner: Guid owner } before && (owner == winner.Key || owner == runnerUp.Key) && start - before.End <= Policy.StickyGap + 1e-9)
            return (new(owner), score.GetValueOrDefault(owner) / total);
        return (TeamsWordDecision.Unknown(TeamsAttributionFailure.Ambiguous), 0);
    }

    /// <summary>
    /// UI speaking intervals derived from the samples: a speaker sample lasts until the next sample (at most
    /// <see cref="TeamsAttributionPolicy.EvidenceLifetime"/>), a healthy gap belongs to the speaker on both sides;
    /// barriers yield nothing. Intervals shorter than <see cref="TeamsAttributionPolicy.MinUIInterval"/> are dropped.
    /// </summary>
    private List<UIInterval> UiIntervals(double low, double high)
    {
        // Start one lookback earlier so an interval running into the window keeps its true start.
        var origin = low - Policy.UnitLookback;
        int lower = 0, upper = samples.Count;
        while (lower < upper) { var mid = (lower + upper) / 2; if (samples[mid].Offset <= origin) lower = mid + 1; else upper = mid; }
        var result = new List<UIInterval>();
        var index = Math.Max(0, lower - 1);
        while (index < samples.Count && samples[index].Offset < high)
        {
            var sample = samples[index];
            var next = index + 1 < samples.Count ? samples[index + 1].Offset : double.PositiveInfinity;
            UIInterval? piece = sample.Evidence.Kind switch
            {
                TeamsEvidenceKind.Speaker => new UIInterval(sample.Evidence.Participant, sample.Offset, Math.Min(next, sample.Offset + Policy.EvidenceLifetime)),
                TeamsEvidenceKind.HealthyGap => GapOwner(index) is Guid owner ? new UIInterval(owner, sample.Offset, next) : null,
                _ => null
            };
            index++;
            if (piece is not { } value || !(value.End > value.Start)) continue;
            if (result.Count > 0 && result[^1].ParticipantID == value.ParticipantID && value.Start <= result[^1].End + 1e-9)
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, value.End) };
            else result.Add(value);
        }
        return result.Where(interval => interval.End - interval.Start + 1e-9 >= Policy.MinUIInterval).ToList();
    }

    /// <summary>
    /// Final pass: identities with too little speech, or with only short, weakly won runs, lose their automatic turns.
    /// Applies only while another identity is clearly established, so a short call keeps its names.
    /// </summary>
    private void SuppressFlickerIdentities(List<AttributedTurn> turns)
    {
        var stats = new Dictionary<Guid, IdentityStats>();
        var runStart = 0;
        for (var index = 0; index < turns.Count; index++)
        {
            if (turns[index].Owner is not Guid owner) { runStart = index + 1; continue; }
            if (!stats.TryGetValue(owner, out var stat)) stats[owner] = stat = new IdentityStats();
            stat.Speech += turns[index].End - turns[index].Start;
            stat.Shares.Add(turns[index].Share);
            if (index + 1 == turns.Count || turns[index + 1].Owner != owner)
            {
                stat.Runs++;
                stat.LongestRun = Math.Max(stat.LongestRun, turns[index].End - turns[runStart].Start);
                runStart = index + 1;
            }
        }
        var suppressed = stats.Where(pair =>
        {
            var (person, value) = (pair.Key, pair.Value);
            if (!stats.Any(other => other.Key != person && other.Value.Speech >= Policy.MinIdentitySpeech)) return false;
            var meanShare = value.Shares.Sum() / Math.Max(1, value.Shares.Count);
            return value.Speech < Policy.MinIdentitySpeech
                || (value.Speech < Policy.JitterSpeechCeiling && value.Runs >= Policy.JitterMinRuns
                    && value.LongestRun <= Policy.JitterMaxLongestRun && meanShare < Policy.JitterMaxMeanShare);
        }).Select(pair => pair.Key).ToHashSet();
        foreach (var turn in turns) if (turn.Owner is Guid owner && suppressed.Contains(owner)) turn.Owner = null;
    }

    private sealed class IdentityStats
    {
        public double Speech { get; set; }
        public List<double> Shares { get; } = [];
        public int Runs { get; set; }
        public double LongestRun { get; set; }
    }
}
