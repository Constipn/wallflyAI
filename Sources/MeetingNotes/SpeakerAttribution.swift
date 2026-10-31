import Foundation

enum SpeakerAttribution: String, Codable { case teamsAX = "teams-ax", manual }

struct MeetingParticipant: Identifiable, Codable, Equatable {
    var id = UUID()
    var name: String
    var sourceKey: String?
    var sessionID: UUID?
    var colorIndex: Int = 0
}

/// Times are seconds relative to TranscriptSegment.offset, including silence in that audio interval.
struct TranscriptWord: Codable, Equatable {
    var text: String
    var start: TimeInterval
    var end: TimeInterval
    var participantID: UUID?
    var attribution: SpeakerAttribution?
    /// The raw per-word Teams UI decision, kept apart from the utterance-level `participantID`.
    /// Optional so transcripts saved before it existed decode unchanged; not used for display.
    var uiParticipantID: UUID?
    /// Cluster ID from the offline acoustic diarization (`Meeting.acousticSpeakers`), nil when the word
    /// lies outside every diarized segment or diarization has not run. Optional, so older files decode.
    var acousticSpeaker: Int?
    /// The Teams utterance label (Phase 1) before acoustic fusion replaced `participantID`. Fusion reads it, so
    /// re-running it gives the same result. Optional: older words get it from `participantID` on their first fusion.
    var utteranceParticipantID: UUID?
}

/// One cluster of the offline acoustic diarization and how it is bound to a meeting participant.
struct AcousticSpeaker: Codable, Equatable, Sendable, Identifiable {
    /// Diarizer cluster index, stable within one `DiarizationRun`.
    let id: Int
    var speechSeconds: TimeInterval
    var wordCount: Int
    var participantID: UUID?
    var bindingSource: AcousticBindingSource?
    /// Too little or too fragmented speech to show as an own speaker.
    var suppressed: Bool
}

enum AcousticBindingSource: String, Codable, Sendable {
    /// Bound by Teams UI votes of the cluster's words.
    case vote
    /// Bound by the user (renaming a "Speaker k").
    case manual
}

enum DiarizationStatus: String, Codable, Sendable {
    case running, completed, failed, skipped
}

/// Metadata of the last acoustic diarization run of a meeting. `segments` are meeting offsets.
struct DiarizationRun: Codable, Equatable, Sendable {
    var engine: String
    var modelVersion: String?
    var threshold: Double?
    var status: DiarizationStatus
    var startedAt: Date?
    var finishedAt: Date?
    var message: String?
    var segments: [AcousticSegment]?
}

/// One interval of the persisted Teams evidence timeline, in meeting offsets (seconds, 3 decimals).
/// `speaker` intervals (including healthy gaps owned by that speaker) carry `participantID`,
/// `barrier` intervals the failure reason; `gap` is a healthy no-speaker stretch without owner;
/// `context` marks a Teams process/context boundary (`start == end`). Missing time = expired evidence.
struct TeamsTimelineEntry: Codable, Equatable, Sendable {
    enum Kind: String, Codable, Sendable { case speaker, gap, barrier, context }
    var kind: Kind
    var start: TimeInterval
    var end: TimeInterval
    var participantID: UUID?
    var barrier: String?
}

struct TranscriptTurnID: Hashable {
    var segmentID: UUID
    var wordIndex: Int?
}

struct TranscriptReference: Equatable {
    var segmentID: UUID
    var wordRange: Range<Int>?
}

/// One displayed word (or untimed chunk) with its audio extent, used only to order the transcript view.
private struct TranscriptDisplayItem {
    var turn: TranscriptTurn
    var end: TimeInterval
    var words: Int
    /// Only timed words join neighbours into an utterance block; an untimed chunk is a block of its own.
    var timed: Bool
    /// Start of the item's utterance block, or the later position a deferred backchannel is shown at.
    var sortKey: TimeInterval
    /// Keeps the words of one block together when two blocks share a sort key.
    var block = 0
    var start: TimeInterval { turn.offset }

    init(turn: TranscriptTurn, end: TimeInterval, words: Int, timed: Bool) {
        self.turn = turn; self.end = end; self.words = words; self.timed = timed; sortKey = turn.offset
    }
}

extension Meeting {
    func speakerName(_ source: Speaker, participantID: UUID?) -> String {
        guard source == .others, let id = participantID,
              let participant = participants?.first(where: { $0.id == id }) else { return source.rawValue }
        return participant.name
    }

    /// The detailed source chunks stay intact; runs are a view over their word assignments.
    var transcriptRuns: [TranscriptTurn] {
        var items: [TranscriptDisplayItem] = []
        for segment in orderedTranscript {
            if let words = segment.words, !words.isEmpty {
                for index in words.indices {
                    let word = words[index]
                    let text = word.text.trimmingCharacters(in: .whitespacesAndNewlines)
                    if !text.isEmpty {
                        let start = segment.offset + word.start
                        items.append(TranscriptDisplayItem(turn: TranscriptTurn(id: TranscriptTurnID(segmentID: segment.id, wordIndex: index == 0 ? nil : index),
                            speaker: segment.speaker, offset: start, text: text,
                            isFinal: segment.isFinal, participantID: segment.speaker == .others ? word.participantID : nil,
                            references: [TranscriptReference(segmentID: segment.id, wordRange: index..<(index + 1))]),
                            end: max(start, segment.offset + word.end), words: 1, timed: true))
                    }
                }
            } else {
                let text = segment.text.trimmingCharacters(in: .whitespacesAndNewlines)
                guard !text.isEmpty else { continue }
                items.append(TranscriptDisplayItem(turn: TranscriptTurn(id: TranscriptTurnID(segmentID: segment.id), speaker: segment.speaker,
                    offset: segment.offset, text: text, isFinal: segment.isFinal,
                    participantID: segment.speaker == .others ? segment.participantID : nil,
                    references: [TranscriptReference(segmentID: segment.id)]),
                    end: segment.offset + max(0, segment.duration), words: text.split(whereSeparator: \.isWhitespace).count, timed: false))
            }
        }
        Self.orderByUtterance(&items)
        // Source chunks can overlap. Words sort by their utterance block and then in audio order, so
        // simultaneous speech on both channels shows as whole turns, while a pause of `unitGap` on one side
        // still lets the other side's contribution split it.
        items.sort {
            if $0.sortKey != $1.sortKey { return $0.sortKey < $1.sortKey }
            if $0.block != $1.block { return $0.block < $1.block }
            if $0.turn.offset != $1.turn.offset { return $0.turn.offset < $1.turn.offset }
            if $0.turn.id.segmentID != $1.turn.id.segmentID { return $0.turn.id.segmentID.uuidString < $1.turn.id.segmentID.uuidString }
            return ($0.turn.id.wordIndex ?? -1) < ($1.turn.id.wordIndex ?? -1)
        }
        var grouped: [TranscriptTurn] = []
        for run in items.map(\.turn) {
            if let last = grouped.indices.last, grouped[last].id.segmentID == run.id.segmentID,
               grouped[last].participantID == run.participantID, grouped[last].speaker == run.speaker,
               let previousRange = grouped[last].references.last?.wordRange,
               let range = run.references.first?.wordRange, previousRange.upperBound == range.lowerBound {
                grouped[last].text += " " + run.text
                grouped[last].references[grouped[last].references.count - 1].wordRange = previousRange.lowerBound..<range.upperBound
            } else { grouped.append(run) }
        }
        return grouped
    }

    /// Orders the view by utterance block: consecutive timed words of one channel and participant with gaps
    /// below `unitGap` sort at the block's start. A short contribution (e.g. "Ja.") that lies strictly inside
    /// a block of the other channel is shown right after that block instead of cutting the sentence into pieces.
    private static func orderByUtterance(_ items: inout [TranscriptDisplayItem]) {
        let policy = TeamsAttributionPolicy()
        struct Block { var participantID: UUID?; var start: TimeInterval; var end: TimeInterval; var words: Int; var timed: Bool; var members: [Int] }
        func blocks(of speaker: Speaker) -> [Block] {
            var result: [Block] = []
            let members = items.indices.filter { items[$0].turn.speaker == speaker }.sorted { items[$0].start < items[$1].start }
            for index in members {
                let item = items[index]
                if let last = result.last, last.timed, item.timed, last.participantID == item.turn.participantID,
                   item.start - last.end < policy.unitGap {
                    result[result.count - 1].end = max(last.end, item.end)
                    result[result.count - 1].words += item.words
                    result[result.count - 1].members.append(index)
                } else {
                    result.append(Block(participantID: item.turn.participantID, start: item.start, end: item.end,
                                        words: item.words, timed: item.timed, members: [index]))
                }
            }
            return result
        }
        let me = blocks(of: .me), others = blocks(of: .others)
        for (number, block) in (me + others).enumerated() {
            for index in block.members { items[index].block = number }
        }
        for (guests, hosts) in [(me, others), (others, me)] {
            // Walk backwards so the next block's own (possibly deferred) position is known: a deferred
            // backchannel never moves past the next block of its own channel.
            var nextKey = TimeInterval.infinity
            for guest in guests.reversed() {
                var key = guest.start
                if guest.end - guest.start <= policy.backchannelDuration + 1e-9 && guest.words <= policy.backchannelWords {
                    // Hosts are ordered by start, so only the last host starting before the guest can contain it.
                    var low = 0, high = hosts.count
                    while low < high { let mid = (low + high) / 2; if hosts[mid].start < guest.start { low = mid + 1 } else { high = mid } }
                    if low > 0, hosts[low - 1].end > guest.end { key = max(guest.start, min(hosts[low - 1].end, nextKey)) }
                }
                for index in guest.members { items[index].sortKey = key }
                nextKey = key
            }
        }
    }

    mutating func assign(_ references: [TranscriptReference], to participantID: UUID?) {
        guard participantID == nil || participants?.contains(where: { $0.id == participantID }) == true else { return }
        for reference in references {
            guard let index = transcript.firstIndex(where: { $0.id == reference.segmentID }), transcript[index].speaker == .others else { continue }
            if let range = reference.wordRange, var words = transcript[index].words {
                guard range.lowerBound >= 0, range.upperBound <= words.count else { continue }
                for i in range { words[i].participantID = participantID; words[i].attribution = .manual }
                transcript[index].words = words
            } else {
                transcript[index].participantID = participantID
                transcript[index].attribution = .manual
            }
        }
    }
}

enum TeamsSpeakerStatus: String {
    case automatic = "Teams audio and speaker names are selected automatically at recording start"
    case disabled = "Teams speaker names are off"
    case ready = "Ready for Teams · start recording to detect speakers"
    case wrongAudioSource = "Select Microsoft Teams audio to enable speaker names"
    case waitingForSpeaker = "Teams names found · waiting for a speaker signal"
    case needsAccessibility = "Accessibility access needed · recording still works"
    case teamsNotRunning = "Microsoft Teams is not running"
    case meetingNotFound = "No Teams meeting detected"
    case active = "Teams speaker detection active"
    case unavailable = "Teams speaker signal unavailable · using Others"
    case ambiguous = "Multiple or unclear active speakers · using Others"
    case selfSpeaking = "You are speaking · remote attribution paused"
    case missingWordTimes = "Teams speakers found · word timings unavailable · using Others"
    var displayName: String {
        switch self {
        case .automatic: return "Teams audio and speaker names are chosen automatically when recording starts"
        case .disabled: return "Teams speaker names are off"
        case .ready: return "Ready for Teams · Start recording to detect speakers"
        case .wrongAudioSource: return "Choose Microsoft Teams as the audio source for speaker names"
        case .waitingForSpeaker: return "Teams names found · waiting for a speaker signal"
        case .needsAccessibility: return "Accessibility access missing · Recording still works"
        case .teamsNotRunning: return "Microsoft Teams is not running"
        case .meetingNotFound: return "No Teams meeting detected"
        case .active: return "Teams speaker detection active"
        case .unavailable: return "Teams speaker signal missing · Assigned as Others"
        case .ambiguous: return "Multiple or unclear speakers · Assigned as Others"
        case .selfSpeaking: return "You are speaking · Assigning other speakers paused"
        case .missingWordTimes: return "Teams speaker found · Word timings missing · Assigned as Others"
        }
    }

}

struct TeamsObservedParticipant: Equatable {
    var key: String
    var name: String
    var active: Bool
    var isSelf: Bool = false
    var stableKey: String?
}

enum TeamsObservationFailure: String {
    case accessibility, notRunning, meetingNotFound, unreadable, truncated, conflictingViews, unidentifiedSpeaker, localUserSpeaking
}

struct TeamsSpeakerObservation: Equatable {
    var timestamp: TimeInterval // ProcessInfo.systemUptime, the same clock used by audio capture
    var surfaceID: String?
    var participants: [TeamsObservedParticipant] = []
    var status: TeamsSpeakerStatus
    /// Process generation, independent of gallery/compact window identity.
    var contextID: String?
    var readDuration: TimeInterval = 0
    var failure: TeamsObservationFailure?
}

struct TeamsAttributionPolicy {
    var detectionLag: TimeInterval = 0.5
    var evidenceLifetime: TimeInterval = 1
    var minimumWordCoverage: Double = 0.75
    var maximumHealthyGap: TimeInterval = 0.5
    var revisionWindow: TimeInterval = 15
    /// A word gap at least this long ends an utterance.
    var unitGap: TimeInterval = 0.7
    /// UI evidence this long before an utterance still counts for its vote (at `outsideWeight`).
    var voteLead: TimeInterval = 0.5
    /// UI evidence this long after an utterance still counts for its vote (at `outsideWeight`).
    var voteTrail: TimeInterval = 0.3
    /// Weight of UI evidence just outside an utterance relative to evidence inside it.
    var outsideWeight: Double = 0.25
    /// Shorter UI speaking intervals are indicator noise and never vote.
    var minUIInterval: TimeInterval = 0.2
    /// The winner must be shown as speaking for at least this share of the utterance.
    var minUnitCoverage: Double = 0.4
    /// A runner-up with at least this share of the winner's score makes the vote close.
    var closeVoteRatio: Double = 0.7
    /// Close votes on utterances up to this long go to the interval that started nearest the utterance.
    var shortUnitDuration: TimeInterval = 1.2
    /// A named run must last this long to split an utterance into a new turn.
    var minTurnDuration: TimeInterval = 1.5
    /// A run must contain this many words to split an utterance into a new turn.
    var minTurnWords = 3
    /// An unnamed run must last this long to split an utterance (shorter UI gaps are bridged).
    var minUnknownRun: TimeInterval = 2.5
    /// A close vote keeps the previous owner if the utterance starts within this time after them.
    var stickyGap: TimeInterval = 2
    /// How far before the revision window a reconciliation reads words and UI evidence.
    var unitLookback: TimeInterval = 30
    /// Final pass: an identity with less automatic speech than this is treated as indicator flicker.
    var minIdentitySpeech: TimeInterval = 10
    /// Final pass: identities below this speech time are checked for a jittery pattern.
    var jitterSpeechCeiling: TimeInterval = 30
    /// Final pass: a jittery identity appears in at least this many separate runs.
    var jitterMinRuns = 20
    /// Final pass: a jittery identity never holds a run longer than this.
    var jitterMaxLongestRun: TimeInterval = 3
    /// Final pass: a jittery identity wins its votes with less than this mean share.
    var jitterMaxMeanShare: Double = 0.6
    /// Display: a contribution up to this long inside the other channel's block is a backchannel.
    var backchannelDuration: TimeInterval = 1.2
    /// Display: a backchannel has at most this many words.
    var backchannelWords = 3
}

enum TeamsAttributionFailure: String, Hashable {
    case missingWordTimes, noObservation, stale, ambiguous, localUserSpeaking, unavailable, contextChanged, insufficientCoverage
}

struct TeamsWordDecision {
    var participantID: UUID?
    var failure: TeamsAttributionFailure?
}

/// In-memory counters only. Coverage is not an estimate of recognition accuracy.
struct TeamsSpeakerDiagnostics {
    var participantCount = 0
    var observedAt: TimeInterval?
    var readDuration: TimeInterval = 0
    var observationAge: TimeInterval = 0
    var observationFailure: TeamsObservationFailure?
    var timedWords = 0
    var namedWords = 0
    var untimedChunks = 0
    var latestRemoteHasWordTimes: Bool?
    var unknownReasons: [TeamsAttributionFailure: Int] = [:]
}

/// Pure, session-scoped UI/time alignment. No voice, roster-order or text inference.
struct TeamsAttributionSession {
    enum Evidence: Equatable { case speaker(UUID), healthyGap, barrier(TeamsAttributionFailure) }
    struct Sample { var offset: TimeInterval; var evidence: Evidence }
    let id: UUID
    let origin: TimeInterval
    let baseDuration: TimeInterval
    let policy: TeamsAttributionPolicy
    private(set) var samples: [Sample] = []
    private(set) var lastObservation: TeamsSpeakerObservation?
    private(set) var latestOffset: TimeInterval = 0
    private var participantIDs: [String: UUID] = [:]
    private var observedNames: [UUID: String] = [:]
    private var stableParticipantKeys: [UUID: String] = [:]
    private var ambiguousNames = Set<String>()
    private var contextID: String?
    private var contextBoundaries: [TimeInterval] = []

    init(id: UUID, origin: TimeInterval, baseDuration: TimeInterval, policy: TeamsAttributionPolicy = .init()) {
        self.id = id; self.origin = origin; self.baseDuration = baseDuration; self.policy = policy
    }

    /// Rebuilds a session from a persisted timeline for offline replay. Samples are placed so that each
    /// interval is covered exactly as before (speaker intervals with samples at most `evidenceLifetime` apart,
    /// the last one ending at the interval end); name/identity state is not restored, so it cannot ingest.
    init(id: UUID, timeline: [TeamsTimelineEntry], policy: TeamsAttributionPolicy = .init()) {
        let entries = timeline.sorted { $0.start < $1.start }
        self.init(id: id, origin: 0, baseDuration: entries.first?.start ?? 0, policy: policy)
        let lifetime = max(policy.evidenceLifetime, 1e-3)
        for entry in entries {
            switch entry.kind {
            case .context: contextBoundaries.append(entry.start)
            case .gap: samples.append(Sample(offset: entry.start, evidence: .healthyGap))
            case .barrier:
                let reason = entry.barrier.flatMap(TeamsAttributionFailure.init(rawValue:)) ?? .unavailable
                samples.append(Sample(offset: entry.start, evidence: .barrier(reason)))
            case .speaker:
                guard let person = entry.participantID else { continue }
                let last = entry.end - lifetime
                if last <= entry.start + 1e-9 {
                    samples.append(Sample(offset: entry.start, evidence: .speaker(person)))
                } else {
                    let steps = Int(((last - entry.start) / lifetime - 1e-9).rounded(.up))
                    for step in 0...steps {
                        samples.append(Sample(offset: entry.start + (last - entry.start) * Double(step) / Double(steps), evidence: .speaker(person)))
                    }
                }
            }
        }
        latestOffset = entries.map(\.end).max() ?? 0
    }

    /// Compresses the ingested samples into intervals for persistence. Consecutive samples with the same
    /// state merge; a speaker sample covers up to `evidenceLifetime`, the final gap/barrier runs to `end`
    /// (default: the latest observation). Rebuilding with `init(id:timeline:)` yields the same decisions.
    func exportTimeline(until end: TimeInterval? = nil) -> [TeamsTimelineEntry] {
        let stop = max(latestOffset, end ?? latestOffset)
        var result: [TeamsTimelineEntry] = []
        func append(_ kind: TeamsTimelineEntry.Kind, _ start: TimeInterval, _ end: TimeInterval,
                    participant: UUID? = nil, barrier: String? = nil) {
            guard end > start + 1e-9 else { return }
            if let last = result.indices.last, result[last].kind == kind, result[last].participantID == participant,
               result[last].barrier == barrier, start <= result[last].end + 1e-6 {
                result[last].end = max(result[last].end, end)
            } else {
                result.append(TeamsTimelineEntry(kind: kind, start: start, end: end, participantID: participant, barrier: barrier))
            }
        }
        for index in samples.indices {
            let sample = samples[index]
            let next = index + 1 < samples.count ? samples[index + 1].offset : max(stop, sample.offset)
            switch sample.evidence {
            case .speaker(let person):
                let limit = sample.offset + policy.evidenceLifetime
                append(.speaker, sample.offset, index + 1 < samples.count ? min(next, limit) : limit, participant: person)
            case .healthyGap:
                if let person = gapOwner(at: index) { append(.speaker, sample.offset, next, participant: person) }
                else { append(.gap, sample.offset, next) }
            case .barrier(let reason): append(.barrier, sample.offset, next, barrier: reason.rawValue)
            }
        }
        for boundary in contextBoundaries {
            let position = result.firstIndex { $0.start >= boundary } ?? result.endIndex
            result.insert(TeamsTimelineEntry(kind: .context, start: boundary, end: boundary), at: position)
        }
        func rounded(_ value: TimeInterval) -> TimeInterval { (value * 1000).rounded() / 1000 }
        return result.map { var entry = $0; entry.start = rounded(entry.start); entry.end = rounded(entry.end); return entry }
    }

    mutating func ingest(_ observation: TeamsSpeakerObservation, participants: inout [MeetingParticipant]) {
        guard observation.timestamp.isFinite, observation.timestamp >= origin,
              lastObservation.map({ observation.timestamp >= $0.timestamp }) ?? true else { return }
        let rawOffset = baseDuration + observation.timestamp - origin
        let newContext = observation.contextID ?? observation.surfaceID
        let changedContext = contextID != nil && newContext != nil && contextID != newContext
        if changedContext {
            participantIDs.removeAll(); observedNames.removeAll(); stableParticipantKeys.removeAll(); ambiguousNames.removeAll()
            contextBoundaries.append(rawOffset)
        }
        if let newContext { contextID = newContext }
        lastObservation = observation; latestOffset = rawOffset
        let remote = observation.participants.filter { !$0.isSelf && !$0.name.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty }
        let nameCounts = Dictionary(grouping: remote, by: { Self.normalized($0.name) })
        for (name, matches) in nameCounts where matches.count > 1 { ambiguousNames.insert(name) }
        let stableCounts = Dictionary(grouping: remote.compactMap(\.stableKey), by: { $0 })
        var candidates: [UUID] = []
        var used = Set<UUID>()
        for observed in remote {
            let stableKey = observed.stableKey.flatMap { stableCounts[$0]?.count == 1 ? $0 : nil }
            let key = stableKey.map { "stable/\($0)" } ?? "element/\(observed.key)"
            let sourceKey = "\(contextID ?? "unknown")/\(key)"
            let participantID: UUID
            if let existing = participantIDs[sourceKey] { participantID = existing }
            else {
                let name = Self.normalized(observed.name)
                let matches = observedNames.filter {
                    guard $0.value == name, !used.contains($0.key) else { return false }
                    // A name can reconnect a transient AX element after a layout change.
                    // It cannot override evidence that two stable identities differ.
                    return stableKey == nil || stableParticipantKeys[$0.key] == nil || stableParticipantKeys[$0.key] == stableKey
                }.map(\.key)
                if !ambiguousNames.contains(name), nameCounts[name]?.count == 1, matches.count == 1 {
                    participantID = matches[0]
                } else {
                    let participant = participants.first { $0.sourceKey == sourceKey && $0.sessionID == id } ??
                        MeetingParticipant(name: observed.name, sourceKey: sourceKey, sessionID: id, colorIndex: participants.count)
                    if !participants.contains(where: { $0.id == participant.id }) { participants.append(participant) }
                    participantID = participant.id
                }
                participantIDs[sourceKey] = participantID
            }
            if let previousName = observedNames[participantID], let index = participants.firstIndex(where: { $0.id == participantID }),
               Self.normalized(participants[index].name) == previousName {
                participants[index].name = observed.name
            }
            observedNames[participantID] = Self.normalized(observed.name)
            if let stableKey { stableParticipantKeys[participantID] = stableKey }
            used.insert(participantID)
            if observed.active { candidates.append(participantID) }
        }
        let evidence: Evidence
        if observation.participants.contains(where: { $0.isSelf && $0.active }) || observation.status == .selfSpeaking || observation.failure == .localUserSpeaking {
            evidence = .barrier(.localUserSpeaking)
        } else if observation.status == .ambiguous || candidates.count > 1 || observation.failure == .unidentifiedSpeaker || observation.failure == .conflictingViews {
            evidence = .barrier(.ambiguous)
        } else if observation.failure != nil || (observation.status != .active && observation.status != .waitingForSpeaker) {
            evidence = .barrier(.unavailable)
        } else if let candidate = candidates.first { evidence = .speaker(candidate) }
        else { evidence = .healthyGap }

        // A recovery never backdates across an explicit failure or process boundary.
        let recovering: Bool
        if case .barrier = samples.last?.evidence { recovering = true } else { recovering = false }
        let isBarrier: Bool
        if case .barrier = evidence { isBarrier = true } else { isBarrier = false }
        let corrected = rawOffset - ((recovering || isBarrier || changedContext) ? 0 : max(0, policy.detectionLag))
        let offset = max(baseDuration, max(samples.last?.offset ?? baseDuration, corrected))
        samples.append(Sample(offset: offset, evidence: evidence))
    }

    func participant(start: TimeInterval, end: TimeInterval) -> UUID? {
        decision(start: start, end: end).participantID
    }

    func decision(start: TimeInterval, end: TimeInterval) -> TeamsWordDecision {
        func unknown(_ reason: TeamsAttributionFailure) -> TeamsWordDecision { .init(failure: reason) }
        guard start.isFinite, end.isFinite, end > start, !samples.isEmpty else { return unknown(.noObservation) }
        if contextBoundaries.contains(where: { start < $0 && $0 < end }) { return unknown(.contextChanged) }
        var low = 0, high = samples.count
        while low < high { let mid = (low + high) / 2; if samples[mid].offset <= start { low = mid + 1 } else { high = mid } }
        guard low > 0 else { return unknown(.noObservation) }
        var index = low - 1
        var cursor = start
        var coverage: [UUID: TimeInterval] = [:]
        while cursor < end {
            let sample = samples[index]
            let next = index + 1 < samples.count ? samples[index + 1].offset : .infinity
            let stop = min(end, next)
            if stop <= cursor { index += 1; continue }
            switch sample.evidence {
            case .barrier(let reason): return unknown(reason)
            case .speaker(let person):
                guard stop <= sample.offset + policy.evidenceLifetime + 1e-9 else { return unknown(.stale) }
                coverage[person, default: 0] += stop - cursor
            case .healthyGap:
                if let person = gapOwner(at: index) { coverage[person, default: 0] += stop - cursor }
            }
            cursor = stop
            if cursor < end { index += 1 }
        }
        guard let best = coverage.max(by: { $0.value < $1.value }),
              best.value / (end - start) + 1e-9 >= policy.minimumWordCoverage else { return unknown(.insufficientCoverage) }
        return .init(participantID: best.key)
    }

    private func gapOwner(at index: Int) -> UUID? {
        var left = index, right = index
        while left > 0, samples[left - 1].evidence == .healthyGap {
            left -= 1
            if samples[index].offset - samples[left].offset > policy.maximumHealthyGap { return nil }
        }
        while right + 1 < samples.count, samples[right + 1].evidence == .healthyGap {
            right += 1
            if samples[right].offset - samples[left].offset > policy.maximumHealthyGap { return nil }
        }
        guard left > 0, right + 1 < samples.count,
              case .speaker(let before) = samples[left - 1].evidence,
              case .speaker(let after) = samples[right + 1].evidence, before == after,
              samples[right + 1].offset - samples[left].offset <= policy.maximumHealthyGap + 1e-9,
              samples[left].offset <= samples[left - 1].offset + policy.evidenceLifetime else { return nil }
        return before
    }

    private static func normalized(_ name: String) -> String { name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() }

    func diagnostics(for segments: [TranscriptSegment], now: TimeInterval) -> TeamsSpeakerDiagnostics {
        var result = TeamsSpeakerDiagnostics()
        if let observation = lastObservation {
            result.participantCount = observation.participants.filter { !$0.isSelf }.count
            result.observedAt = observation.timestamp
            result.observationAge = max(0, now - observation.timestamp)
            result.readDuration = observation.readDuration
            result.observationFailure = observation.failure
        }
        let recent = segments.filter { $0.sessionID == id && $0.speaker == .others && $0.isFinal && !$0.text.isEmpty }
        result.latestRemoteHasWordTimes = recent.max(by: { $0.offset < $1.offset }).map { !($0.words ?? []).isEmpty }
        for segment in recent {
            guard let words = segment.words, !words.isEmpty else {
                result.untimedChunks += 1
                result.unknownReasons[.missingWordTimes, default: 0] += 1
                continue
            }
            for word in words {
                result.timedWords += 1
                if word.participantID != nil { result.namedWords += 1 }
                else if word.attribution != .manual {
                    let reason = decision(start: segment.offset + word.start, end: segment.offset + word.end).failure ?? .insufficientCoverage
                    result.unknownReasons[reason, default: 0] += 1
                }
            }
        }
        return result
    }

    func annotate(_ original: TranscriptSegment) -> TranscriptSegment {
        annotate([original])[0]
    }

    /// Labels remote words per utterance instead of per word. Utterances span recognition results
    /// (finals and previews alike) and end only at a word gap of `unitGap`, a context change or a
    /// manual word. Inside an utterance only established runs of the raw per-word decision start a new
    /// turn; each turn then gets one owner from an overlap-weighted vote of the UI intervals, or none.
    /// Manual words are never relabeled. With `revisableAfter`, utterances ending before it stay as they are,
    /// unless they contain a word of a segment in `alwaysRevising` (a late recognition result).
    /// `finalPass` additionally drops identities that only appear through indicator flicker.
    func annotate(_ segments: [TranscriptSegment], revisableAfter cutoff: TimeInterval? = nil,
                  alwaysRevising forcedIDs: Set<UUID> = [], finalPass: Bool = false) -> [TranscriptSegment] {
        let utterances = splitUtterances(segments)
        guard let low = utterances.first?.first?.start, let high = utterances.joined().map(\.end).max() else { return segments }
        let intervals = uiIntervals(from: low - policy.voteLead, to: high + policy.voteTrail)
        var turns: [AttributedTurn] = []
        for utterance in utterances {
            let utteranceEnd = utterance.map(\.end).max() ?? 0
            let revisable = (cutoff.map { utteranceEnd >= $0 } ?? true) || utterance.contains { forcedIDs.contains(segments[$0.segment].id) }
            for run in speakerRuns(in: utterance) {
                let previous = turns.last.map { (owner: $0.owner, end: $0.end) }
                let ballot = vote(start: run.start, end: run.end, previous: previous, intervals: intervals)
                turns.append(AttributedTurn(words: run.words, start: run.start, end: run.end, revisable: revisable,
                                            owner: ballot.decision.participantID, share: ballot.share))
            }
        }
        if finalPass { suppressFlickerIdentities(in: &turns) }
        var result = segments
        for turn in turns where turn.revisable {
            for word in turn.words {
                result[word.segment].words?[word.index].participantID = turn.owner
                result[word.segment].words?[word.index].attribution = turn.owner == nil ? nil : .teamsAX
                result[word.segment].words?[word.index].uiParticipantID = word.raw
                result[word.segment].words?[word.index].utteranceParticipantID = turn.owner
            }
        }
        return result
    }

    /// The owner an utterance from `start` to `end` gets from the UI evidence around it.
    func utteranceDecision(start: TimeInterval, end: TimeInterval, previousOwner: UUID? = nil,
                           previousEnd: TimeInterval = -.infinity) -> TeamsWordDecision {
        let intervals = uiIntervals(from: start - policy.voteLead, to: end + policy.voteTrail)
        return vote(start: start, end: end, previous: (previousOwner, previousEnd), intervals: intervals).decision
    }

    /// A timed, automatically labelled remote word in absolute meeting time.
    private struct WordRef {
        var segment: Int; var index: Int; var start: TimeInterval; var end: TimeInterval
        /// The strict per-word decision (`decision(start:end:)`), set while building runs.
        var raw: UUID? = nil
    }
    /// A merged stretch in which Teams showed exactly one remote participant as speaking.
    private struct UIInterval { var participantID: UUID; var start: TimeInterval; var end: TimeInterval }
    /// Consecutive words of one utterance that share a label.
    private struct Run {
        var label: UUID?
        var words: [WordRef]
        var start: TimeInterval { words.first?.start ?? 0 }
        var end: TimeInterval { words.map(\.end).max() ?? 0 }
        var span: TimeInterval { end - start }
    }
    /// A turn with its voted owner; `revisable` is false for frozen utterances, whose labels are kept.
    private struct AttributedTurn {
        var words: [WordRef]; var start: TimeInterval; var end: TimeInterval; var revisable: Bool
        var owner: UUID?; var share: Double
    }

    /// Splits this session's timed remote words into utterances, in time order.
    private func splitUtterances(_ segments: [TranscriptSegment]) -> [[WordRef]] {
        var words: [(ref: WordRef, manual: Bool)] = []
        for (segmentIndex, segment) in segments.enumerated() where segment.speaker == .others && segment.sessionID == id {
            for (index, word) in (segment.words ?? []).enumerated() where word.start.isFinite && word.end.isFinite {
                words.append((WordRef(segment: segmentIndex, index: index, start: segment.offset + word.start,
                                      end: segment.offset + word.end), word.attribution == .manual))
            }
        }
        words.sort { ($0.ref.start, $0.ref.segment, $0.ref.index) < ($1.ref.start, $1.ref.segment, $1.ref.index) }
        var result: [[WordRef]] = []
        var current: [WordRef] = []
        var currentEnd: TimeInterval = 0
        for (word, manual) in words {
            if let previous = current.last, manual || word.start - currentEnd >= policy.unitGap - 1e-6 ||
                contextBoundaries.contains(where: { previous.start < $0 && $0 <= word.start }) {
                result.append(current); current = []
            }
            if manual { continue }
            currentEnd = current.isEmpty ? word.end : max(currentEnd, word.end)
            current.append(word)
        }
        if !current.isEmpty { result.append(current) }
        return result
    }

    /// Runs of the raw word decision; runs too short to be a real change of speaker are merged into a neighbour.
    private func speakerRuns(in utterance: [WordRef]) -> [Run] {
        var runs: [Run] = []
        for var word in utterance {
            let label = decision(start: word.start, end: word.end).participantID
            word.raw = label
            if let last = runs.indices.last, runs[last].label == label { runs[last].words.append(word) }
            else { runs.append(Run(label: label, words: [word])) }
        }
        while let (index, target) = shortestMergeableRun(in: runs) {
            let neighbours = [index - 1, index + 1].filter { runs.indices.contains($0) }
            if neighbours.count == 2, runs[index - 1].label == runs[index + 1].label {
                // The same speaker on both sides: the short run was an interruption of one turn.
                runs[index - 1].words += runs[index].words + runs[index + 1].words
                runs.removeSubrange(index...(index + 1))
            } else if target < index {
                runs[target].words += runs[index].words
                runs.remove(at: index)
            } else {
                runs[target].words = runs[index].words + runs[target].words
                runs.remove(at: index)
            }
        }
        return runs
    }

    /// The shortest run that is not an established turn and has a neighbour it may join (the longer one).
    /// A named run never joins an unnamed one.
    private func shortestMergeableRun(in runs: [Run]) -> (index: Int, target: Int)? {
        var best: (index: Int, target: Int)?
        for index in runs.indices where !isEstablished(runs[index]) {
            let targets = [index - 1, index + 1].filter {
                runs.indices.contains($0) && (runs[index].label == nil || runs[$0].label != nil)
            }
            guard let target = targets.max(by: { (runs[$0].span, runs[$0].words.count) < (runs[$1].span, runs[$1].words.count) })
            else { continue }
            if let current = best, (runs[current.index].span, runs[current.index].words.count) <= (runs[index].span, runs[index].words.count) { continue }
            best = (index, target)
        }
        return best
    }

    private func isEstablished(_ run: Run) -> Bool {
        run.words.count >= policy.minTurnWords &&
            run.span + 1e-9 >= (run.label == nil ? policy.minUnknownRun : policy.minTurnDuration)
    }

    /// Overlap-weighted vote of UI intervals for one turn; abstains rather than guessing.
    private func vote(start: TimeInterval, end: TimeInterval, previous: (owner: UUID?, end: TimeInterval)?,
                      intervals: [UIInterval]) -> (decision: TeamsWordDecision, share: Double) {
        func overlap(_ interval: UIInterval, _ low: TimeInterval, _ high: TimeInterval) -> TimeInterval {
            max(0, min(interval.end, high) - max(interval.start, low))
        }
        var inside: [UUID: TimeInterval] = [:], score: [UUID: Double] = [:], startDistance: [UUID: TimeInterval] = [:]
        for interval in intervals where interval.end > start - policy.voteLead && interval.start < end + policy.voteTrail {
            let person = interval.participantID
            let within = overlap(interval, start, end)
            let around = overlap(interval, start - policy.voteLead, start) + overlap(interval, end, end + policy.voteTrail)
            inside[person, default: 0] += within
            score[person, default: 0] += within + policy.outsideWeight * around
            startDistance[person] = min(startDistance[person] ?? .infinity, abs(interval.start - start))
        }
        let ranked = score.filter { $0.value > 0 }
            .sorted { $0.value != $1.value ? $0.value > $1.value : $0.key.uuidString < $1.key.uuidString }
        guard let winner = ranked.first else { return (.init(failure: .noObservation), 0) }
        guard (inside[winner.key] ?? 0) / max(end - start, 1e-9) + 1e-9 >= policy.minUnitCoverage else {
            return (.init(failure: .insufficientCoverage), 0)
        }
        let share = winner.value / ranked.reduce(0) { $0 + $1.value }
        guard ranked.count > 1, ranked[1].value >= policy.closeVoteRatio * winner.value else {
            return (.init(participantID: winner.key), share)
        }
        let runnerUp = ranked[1]
        if end - start <= policy.shortUnitDuration + 1e-9 {
            // A short utterance belongs to whoever started speaking around its start.
            let nearer = (startDistance[runnerUp.key] ?? .infinity) < (startDistance[winner.key] ?? .infinity) ? runnerUp : winner
            return (.init(participantID: nearer.key), nearer.value / ranked.reduce(0) { $0 + $1.value })
        }
        if let previous, let owner = previous.owner, owner == winner.key || owner == runnerUp.key,
           start - previous.end <= policy.stickyGap + 1e-9 {
            return (.init(participantID: owner), (score[owner] ?? 0) / ranked.reduce(0) { $0 + $1.value })
        }
        return (.init(failure: .ambiguous), 0)
    }

    /// UI speaking intervals derived from the samples: a speaker sample lasts until the next sample (at most
    /// `evidenceLifetime`), a healthy gap belongs to the speaker on both sides; barriers yield nothing.
    private func uiIntervals(from low: TimeInterval, to high: TimeInterval) -> [UIInterval] {
        // Start one lookback earlier so an interval running into the window keeps its true start.
        let origin = low - policy.unitLookback
        var lower = 0, upper = samples.count
        while lower < upper { let mid = (lower + upper) / 2; if samples[mid].offset <= origin { lower = mid + 1 } else { upper = mid } }
        var result: [UIInterval] = []
        var index = max(0, lower - 1)
        while index < samples.count, samples[index].offset < high {
            let sample = samples[index]
            let next = index + 1 < samples.count ? samples[index + 1].offset : .infinity
            var piece: UIInterval?
            switch sample.evidence {
            case .speaker(let person): piece = UIInterval(participantID: person, start: sample.offset, end: min(next, sample.offset + policy.evidenceLifetime))
            case .healthyGap: piece = gapOwner(at: index).map { UIInterval(participantID: $0, start: sample.offset, end: next) }
            case .barrier: piece = nil
            }
            index += 1
            guard let piece, piece.end > piece.start else { continue }
            if let last = result.indices.last, result[last].participantID == piece.participantID, piece.start <= result[last].end + 1e-9 {
                result[last].end = max(result[last].end, piece.end)
            } else { result.append(piece) }
        }
        return result.filter { $0.end - $0.start + 1e-9 >= policy.minUIInterval }
    }

    /// Final pass: identities with too little speech, or with only short, weakly won runs, lose their
    /// automatic turns. Applies only while another identity is clearly established, so a short call keeps its names.
    private func suppressFlickerIdentities(in turns: inout [AttributedTurn]) {
        struct Stats { var speech: TimeInterval = 0; var shares: [Double] = []; var runs = 0; var longestRun: TimeInterval = 0 }
        var stats: [UUID: Stats] = [:]
        var runStart = 0
        for index in turns.indices {
            guard let owner = turns[index].owner else { runStart = index + 1; continue }
            stats[owner, default: Stats()].speech += turns[index].end - turns[index].start
            stats[owner, default: Stats()].shares.append(turns[index].share)
            if index + 1 == turns.count || turns[index + 1].owner != owner {
                stats[owner, default: Stats()].runs += 1
                stats[owner, default: Stats()].longestRun = max(stats[owner]?.longestRun ?? 0, turns[index].end - turns[runStart].start)
                runStart = index + 1
            }
        }
        let suppressed = Set(stats.filter { person, value in
            guard stats.contains(where: { $0.key != person && $0.value.speech >= policy.minIdentitySpeech }) else { return false }
            let meanShare = value.shares.reduce(0, +) / Double(max(1, value.shares.count))
            return value.speech < policy.minIdentitySpeech ||
                (value.speech < policy.jitterSpeechCeiling && value.runs >= policy.jitterMinRuns &&
                 value.longestRun <= policy.jitterMaxLongestRun && meanShare < policy.jitterMaxMeanShare)
        }.keys)
        for index in turns.indices where turns[index].owner.map(suppressed.contains) == true { turns[index].owner = nil }
    }
}
