import Foundation

/// One remote word in absolute meeting time with the labels known before fusion.
struct FusionWord: Equatable, Sendable {
    /// Caller-defined index, returned unchanged.
    var id: Int
    var start: TimeInterval
    var end: TimeInterval
    /// Raw per-word Teams decision (may be nil).
    var uiParticipantID: UUID?
    /// Utterance-voted Teams result (Phase 1); for a manual word, its manual label.
    var phase1ParticipantID: UUID?
    /// A manual label is never changed, but counts as evidence.
    var isManual: Bool
}

/// One speech segment of the acoustic diarizer in absolute meeting time.
struct FusionAcousticSegment: Equatable, Sendable {
    var speaker: Int
    var start: TimeInterval
    var end: TimeInterval
}

/// Thresholds of the fusion (chosen defaults).
struct FusionPolicy: Equatable, Sendable {
    /// A word overlapping no segment takes the nearest segment within this distance.
    var nearestSegmentTolerance: TimeInterval = 0.3
    /// A sandwiched acoustic run shorter than this is diarizer jitter.
    var shortRunDuration: TimeInterval = 0.5
    /// A sandwiched acoustic run with at most this many words is diarizer jitter.
    var shortRunWords = 2
    /// An acoustic identity with less segment speech than this is suppressed.
    var minIdentitySpeech: TimeInterval = 10
    /// Identities below this speech time are checked for a jittery pattern.
    var jitterSpeechCeiling: TimeInterval = 30
    /// A jittery identity appears in at least this many separate runs.
    var jitterMinRuns = 20
    /// A jittery identity never holds a run longer than this.
    var jitterMaxLongestRun: TimeInterval = 3
    /// Words starting this soon after an acoustic turn starts do not vote for a binding (Teams indicator lag).
    var turnLeadIn: TimeInterval = 0.6
    /// A binding needs at least this many votes for the winner.
    var minVotes = 3
    /// The winner needs at least this share of the named votes (binding and unbound turn votes).
    var minVoteShare: Double = 0.6
    /// Named votes must cover at least this share of the identity's words for a binding.
    var minVoteCoverage: Double = 0.25
    /// A Teams majority overrides a bound name only with at least this duration share of the named words.
    var overrideShare: Double = 0.8
    /// A Teams majority overrides a bound name only if named words cover at least this share of the turn.
    var overrideCoverage: Double = 0.5
    /// A Teams majority overrides a bound name only in turns at least this long.
    var overrideMinDuration: TimeInterval = 2
    /// An unbound turn takes the Teams majority only if it has at least this many words.
    var unboundMinVotes = 2
}

/// Why a word carries its final label.
enum FusionLabelSource: Equatable, Sendable {
    /// The user's manual label, unchanged.
    case manual
    /// The participant bound to the word's acoustic identity.
    case acousticBound
    /// The Teams majority over the word's acoustic turn.
    case uiVote
    /// The Phase 1 result (no or suppressed acoustic identity); its participant may be nil.
    case phase1
    /// An unbound, unsuppressed acoustic identity without a clear Teams vote ("Speaker k").
    case unlabelled
}

struct FusionWordLabel: Equatable, Sendable {
    var id: Int
    var acousticSpeaker: Int?
    var participantID: UUID?
    var source: FusionLabelSource
}

struct FusionSpeaker: Equatable, Sendable {
    var id: Int
    /// Union duration of the identity's acoustic segments.
    var speechSeconds: TimeInterval
    var wordCount: Int
    /// Manual or voted binding; always nil for a suppressed identity.
    var boundParticipantID: UUID?
    var bindingIsManual: Bool
    var suppressed: Bool
    /// 1-based "Speaker k" for unbound, unsuppressed identities by first appearance; nil otherwise or without words.
    var displayOrdinal: Int?
}

struct FusionResult: Equatable, Sendable {
    /// One label per input word, in input order.
    var words: [FusionWordLabel]
    /// Every acoustic identity of the segments, sorted by id.
    var speakers: [FusionSpeaker]
}

/// Fuses Teams UI evidence with acoustic diarization (plan Phase 3). Priority: manual > UI > acoustic,
/// but a bound acoustic identity holds its name against UI flicker within a turn.
enum SpeakerFusion {
    private static let epsilon = 1e-9

    /// Consecutive words (in time order) with the same acoustic identity.
    private struct Turn {
        var speaker: Int?
        /// Indices into the input words, in time order.
        var members: [Int]
        var start: TimeInterval
        var end: TimeInterval
        var duration: TimeInterval { end - start }

        mutating func absorb(_ other: Turn) {
            members += other.members
            start = min(start, other.start)
            end = max(end, other.end)
        }
    }

    private struct IdentityStats {
        var speech: TimeInterval = 0
        var words = 0
        var runs = 0
        var longestRun: TimeInterval = 0
        var hasManualWord = false
    }

    /// Duration-weighted Teams majority of one turn.
    private struct TurnVote {
        var participantID: UUID
        /// The winner's share of the named duration.
        var share: Double
        /// Named duration relative to the duration of all words.
        var coverage: Double
        var words: Int
    }

    static func fuse(words: [FusionWord], segments: [FusionAcousticSegment], manualBindings: [Int: UUID],
                     policy: FusionPolicy = .init()) -> FusionResult {
        let order = words.indices.sorted { (words[$0].start, words[$0].end, words[$0].id, $0) < (words[$1].start, words[$1].end, words[$1].id, $1) }
        var turns = makeTurns(order: order, speakers: order.map { acousticSpeaker(for: words[$0], in: segments, policy: policy) }, words: words)
        smooth(&turns, words: words, policy: policy)

        let ids = Set(segments.map(\.speaker)).sorted()
        let stats = statistics(ids: ids, turns: turns, segments: segments, words: words)
        let suppressed = suppressedIDs(ids: ids, stats: stats, manualBindings: manualBindings, policy: policy)
        var bindings: [Int: UUID] = [:]
        for id in ids where !suppressed.contains(id) {
            bindings[id] = manualBindings[id] ?? votedBinding(for: id, turns: turns, words: words, wordCount: stats[id]?.words ?? 0, policy: policy)
        }

        var labels = words.map { FusionWordLabel(id: $0.id, acousticSpeaker: nil, participantID: $0.phase1ParticipantID, source: .phase1) }
        var ordinals: [Int: Int] = [:]
        for turn in turns {
            let decided = turn.speaker.flatMap { suppressed.contains($0) ? nil : label(for: turn, boundTo: bindings[$0], manually: manualBindings[$0] != nil, words: words, policy: policy) }
            if let speaker = turn.speaker, !suppressed.contains(speaker), bindings[speaker] == nil, ordinals[speaker] == nil {
                ordinals[speaker] = ordinals.count + 1
            }
            for index in turn.members {
                labels[index].acousticSpeaker = turn.speaker
                if words[index].isManual { labels[index].source = .manual }
                else if let decided { (labels[index].participantID, labels[index].source) = decided }
            }
        }

        let speakers = ids.map { id in
            FusionSpeaker(id: id, speechSeconds: stats[id]?.speech ?? 0, wordCount: stats[id]?.words ?? 0,
                          boundParticipantID: bindings[id], bindingIsManual: manualBindings[id] != nil,
                          suppressed: suppressed.contains(id), displayOrdinal: ordinals[id])
        }
        return FusionResult(words: labels, speakers: speakers)
    }

    // MARK: Acoustic identity per word

    /// The speaker with the largest total overlap, else the nearest segment within tolerance; ties go to the lower id.
    private static func acousticSpeaker(for word: FusionWord, in segments: [FusionAcousticSegment], policy: FusionPolicy) -> Int? {
        var overlap: [Int: TimeInterval] = [:]
        var nearest: (distance: TimeInterval, speaker: Int)?
        for segment in segments {
            let shared = min(word.end, segment.end) - max(word.start, segment.start)
            if shared > 0 { overlap[segment.speaker, default: 0] += shared }
            let distance = max(0, -shared)
            if nearest.map({ (distance, segment.speaker) < ($0.distance, $0.speaker) }) ?? true { nearest = (distance, segment.speaker) }
        }
        if let best = overlap.max(by: { ($0.value, $1.key) < ($1.value, $0.key) }) { return best.key }
        guard let nearest, nearest.distance <= policy.nearestSegmentTolerance + epsilon else { return nil }
        return nearest.speaker
    }

    private static func makeTurns(order: [Int], speakers: [Int?], words: [FusionWord]) -> [Turn] {
        var turns: [Turn] = []
        for (index, speaker) in zip(order, speakers) {
            let single = Turn(speaker: speaker, members: [index], start: words[index].start, end: words[index].end)
            if let last = turns.indices.last, turns[last].speaker == speaker { turns[last].absorb(single) }
            else { turns.append(single) }
        }
        return turns
    }

    /// Merges a brief run of B between two runs of A into A (diarizer jitter). Kept are runs without an
    /// acoustic identity, runs containing a manual word, and runs whose own Teams evidence names someone
    /// other than the host (a corroborated interjection).
    private static func smooth(_ turns: inout [Turn], words: [FusionWord], policy: FusionPolicy) {
        var index = 1
        while index + 1 < turns.count {
            let candidate = turns[index]
            let isBrief = candidate.duration < policy.shortRunDuration - epsilon || candidate.members.count <= policy.shortRunWords
            if isBrief, candidate.speaker != nil, turns[index - 1].speaker != nil, turns[index - 1].speaker == turns[index + 1].speaker,
               !candidate.members.contains(where: { words[$0].isManual }),
               !isCorroborated(candidate, host: turns[index - 1].members + turns[index + 1].members, words: words) {
                turns[index - 1].absorb(candidate)
                turns[index - 1].absorb(turns[index + 1])
                turns.removeSubrange(index...(index + 1))
            } else {
                index += 1
            }
        }
    }

    /// True if a word of the run carries a Teams label other than the host's most frequent one.
    private static func isCorroborated(_ run: Turn, host: [Int], words: [FusionWord]) -> Bool {
        var hostTally: [UUID: Double] = [:]
        for index in host { if let id = words[index].uiParticipantID { hostTally[id, default: 0] += 1 } }
        let hostLabel = leader(of: hostTally)?.participantID
        return run.members.contains { words[$0].uiParticipantID.map { $0 != hostLabel } ?? false }
    }

    // MARK: Identities

    private static func statistics(ids: [Int], turns: [Turn], segments: [FusionAcousticSegment], words: [FusionWord]) -> [Int: IdentityStats] {
        var stats = Dictionary(uniqueKeysWithValues: ids.map { ($0, IdentityStats()) })
        for (speaker, own) in Dictionary(grouping: segments, by: \.speaker) {
            var coveredUntil = -TimeInterval.infinity
            for segment in own.sorted(by: { ($0.start, $0.end) < ($1.start, $1.end) }) {
                let from = max(segment.start, coveredUntil)
                if segment.end > from { stats[speaker]?.speech += segment.end - from }
                coveredUntil = max(coveredUntil, segment.end)
            }
        }
        for turn in turns {
            guard let speaker = turn.speaker, var stat = stats[speaker] else { continue }
            stat.words += turn.members.count
            stat.runs += 1
            stat.longestRun = max(stat.longestRun, turn.duration)
            if turn.members.contains(where: { words[$0].isManual }) { stat.hasManualWord = true }
            stats[speaker] = stat
        }
        return stats
    }

    /// Flicker identities (too little speech, or many short runs), unless manually confirmed.
    /// Nothing is suppressed when every identity would be.
    private static func suppressedIDs(ids: [Int], stats: [Int: IdentityStats], manualBindings: [Int: UUID], policy: FusionPolicy) -> Set<Int> {
        let candidates = ids.filter { id in
            guard let stat = stats[id], manualBindings[id] == nil, !stat.hasManualWord else { return false }
            return stat.speech < policy.minIdentitySpeech - epsilon ||
                (stat.speech < policy.jitterSpeechCeiling - epsilon && stat.runs >= policy.jitterMinRuns &&
                    stat.longestRun <= policy.jitterMaxLongestRun + epsilon)
        }
        return candidates.count < ids.count ? Set(candidates) : []
    }

    /// Word-count vote of Teams labels (outside each turn's lead-in) and manual labels for one identity.
    private static func votedBinding(for id: Int, turns: [Turn], words: [FusionWord], wordCount: Int, policy: FusionPolicy) -> UUID? {
        var votes: [UUID: Double] = [:]
        for turn in turns where turn.speaker == id {
            for index in turn.members {
                let word = words[index]
                let voter = word.isManual ? word.phase1ParticipantID
                    : (word.start >= turn.start + policy.turnLeadIn - epsilon ? word.uiParticipantID : nil)
                if let voter { votes[voter, default: 0] += 1 }
            }
        }
        let named = votes.values.reduce(0, +)
        guard let winner = leader(of: votes), winner.value >= Double(policy.minVotes),
              winner.value / named >= policy.minVoteShare - epsilon,
              named / Double(max(wordCount, 1)) >= policy.minVoteCoverage - epsilon else { return nil }
        return winner.participantID
    }

    // MARK: Turn labels

    /// The label of an unsuppressed acoustic turn's automatic words. A clear, long Teams majority
    /// overrides a voted binding, never a manual one.
    private static func label(for turn: Turn, boundTo bound: UUID?, manually: Bool, words: [FusionWord],
                              policy: FusionPolicy) -> (UUID?, FusionLabelSource) {
        let vote = teamsMajority(of: turn, words: words)
        if let bound {
            if !manually, let vote, vote.participantID != bound, vote.share >= policy.overrideShare - epsilon,
               vote.coverage >= policy.overrideCoverage - epsilon, turn.duration >= policy.overrideMinDuration - epsilon {
                return (vote.participantID, .uiVote)
            }
            return (bound, .acousticBound)
        }
        if let vote, vote.words >= policy.unboundMinVotes, vote.share >= policy.minVoteShare - epsilon {
            return (vote.participantID, .uiVote)
        }
        return (nil, .unlabelled)
    }

    /// Duration-weighted vote of the turn's Teams labels (manual words vote with their manual label).
    private static func teamsMajority(of turn: Turn, words: [FusionWord]) -> TurnVote? {
        var weight: [UUID: Double] = [:], count: [UUID: Int] = [:]
        var total: TimeInterval = 0
        for index in turn.members {
            let word = words[index]
            let duration = max(0, word.end - word.start)
            total += duration
            guard let voter = word.isManual ? word.phase1ParticipantID : word.uiParticipantID else { continue }
            weight[voter, default: 0] += duration
            count[voter, default: 0] += 1
        }
        let named = weight.values.reduce(0, +)
        guard named > 0, let winner = leader(of: weight) else { return nil }
        return TurnVote(participantID: winner.participantID, share: winner.value / named, coverage: named / total,
                        words: count[winner.participantID] ?? 0)
    }

    /// The highest tally; ties go to the lexicographically smaller UUID for determinism.
    private static func leader(of tally: [UUID: Double]) -> (participantID: UUID, value: Double)? {
        tally.max { ($0.value, $1.key.uuidString) < ($1.value, $0.key.uuidString) }.map { ($0.key, $0.value) }
    }
}
