import Foundation

/// Applies the acoustic fusion (`SpeakerFusion`) to a meeting's stored transcript and keeps the
/// "Speaker k" participants and manual acoustic bindings in sync. Pure data logic, no I/O.
extension Meeting {
    /// `sourceKey` prefix of the synthetic participant that stands for an unbound acoustic identity.
    static let acousticSourceKeyPrefix = "acoustic/"

    /// The acoustic identity a synthetic "Speaker k" participant stands for, nil for every other participant.
    static func acousticSpeakerID(of participant: MeetingParticipant) -> Int? {
        guard let key = participant.sourceKey, key.hasPrefix(acousticSourceKeyPrefix) else { return nil }
        return Int(key.dropFirst(acousticSourceKeyPrefix.count))
    }

    /// True when the transcript has timed remote words (the only words fusion can label).
    var hasTimedRemoteWords: Bool {
        transcript.contains { $0.speaker == .others && ($0.words ?? []).contains { $0.start.isFinite && $0.end.isFinite } }
    }

    /// Relabels every timed remote word from the acoustic `segments` (meeting offsets) and the Teams evidence.
    ///
    /// Inputs per word: the raw Teams label (`uiParticipantID`) and the Phase-1 label (`utteranceParticipantID`).
    /// Meetings recorded before raw labels were stored (no word has `uiParticipantID`) use the stored pre-fusion
    /// label as the Teams proxy. Manual words keep their label and vote with it; manual bindings in
    /// `acousticSpeakers` are never overridden. Deterministic: applying it twice with the same segments
    /// yields the same meeting.
    mutating func applyAcousticFusion(segments: [AcousticSegment], policy: FusionPolicy = .init()) {
        var locations: [(segment: Int, word: Int)] = []
        for (segmentIndex, segment) in transcript.enumerated() where segment.speaker == .others {
            for (wordIndex, word) in (segment.words ?? []).enumerated() where word.start.isFinite && word.end.isFinite {
                locations.append((segmentIndex, wordIndex))
            }
        }
        // Record the Phase-1 label of words fusion has never touched (older meetings and words of later sessions).
        for (s, w) in locations {
            guard let word = transcript[s].words?[w], word.attribution != .manual,
                  word.utteranceParticipantID == nil, word.acousticSpeaker == nil else { continue }
            transcript[s].words?[w].utteranceParticipantID = word.participantID
        }
        let hasRawUILabels = locations.contains { transcript[$0.segment].words?[$0.word].uiParticipantID != nil }
        let knownParticipants = Set((participants ?? []).map(\.id))

        let words = locations.enumerated().map { id, location -> FusionWord in
            let segment = transcript[location.segment], word = segment.words![location.word]
            let manual = word.attribution == .manual
            return FusionWord(id: id, start: segment.offset + word.start, end: segment.offset + max(word.start, word.end),
                              uiParticipantID: hasRawUILabels ? word.uiParticipantID : word.utteranceParticipantID,
                              phase1ParticipantID: manual ? word.participantID : word.utteranceParticipantID,
                              isManual: manual)
        }
        var manualBindings: [Int: UUID] = [:]
        for speaker in acousticSpeakers ?? [] where speaker.bindingSource == .manual {
            if let participant = speaker.participantID, knownParticipants.contains(participant) { manualBindings[speaker.id] = participant }
        }
        let fusion = SpeakerFusion.fuse(words: words,
            segments: segments.map { FusionAcousticSegment(speaker: $0.speaker, start: $0.start, end: $0.end) },
            manualBindings: manualBindings, policy: policy)

        // "Speaker k" only for identities that label at least one word, numbered by first appearance.
        let unlabelled = Set(fusion.words.filter { $0.source == .unlabelled }.compactMap(\.acousticSpeaker))
        let ordinals = fusion.speakers.filter { unlabelled.contains($0.id) && $0.displayOrdinal != nil }
            .sorted { $0.displayOrdinal! < $1.displayOrdinal! }.map(\.id)
        var people = participants ?? []
        var synthetic: [Int: UUID] = [:]
        for (index, id) in ordinals.enumerated() {
            let key = Self.acousticSourceKeyPrefix + String(id), name = "Speaker \(index + 1)"
            if let existing = people.firstIndex(where: { $0.sourceKey == key }) {
                people[existing].name = name
                synthetic[id] = people[existing].id
            } else {
                let participant = MeetingParticipant(name: name, sourceKey: key, colorIndex: people.count)
                people.append(participant)
                synthetic[id] = participant.id
            }
        }

        for (label, location) in zip(fusion.words, locations) {
            guard var word = transcript[location.segment].words?[location.word] else { continue }
            word.acousticSpeaker = label.acousticSpeaker
            if word.attribution != .manual {
                let participant = label.source == .unlabelled ? label.acousticSpeaker.flatMap { synthetic[$0] } : label.participantID
                word.participantID = participant
                word.attribution = participant == nil ? nil : .teamsAX
            }
            transcript[location.segment].words?[location.word] = word
        }

        // Drop "Speaker k" records a run no longer needs, unless a word (e.g. a manual one) still points to them.
        let referenced = Set(transcript.flatMap { segment in [segment.participantID] + (segment.words ?? []).map(\.participantID) }.compactMap { $0 })
        people.removeAll { Self.acousticSpeakerID(of: $0) != nil && !synthetic.values.contains($0.id) && !referenced.contains($0.id) }
        participants = people.isEmpty && participants == nil ? nil : people

        var speakers = fusion.speakers.map { speaker in
            AcousticSpeaker(id: speaker.id, speechSeconds: (speaker.speechSeconds * 1000).rounded() / 1000, wordCount: speaker.wordCount,
                            participantID: speaker.boundParticipantID,
                            bindingSource: speaker.boundParticipantID == nil ? nil : (speaker.bindingIsManual ? .manual : .vote),
                            suppressed: speaker.suppressed)
        }
        // A manual binding outlives a run in which its identity does not occur.
        for speaker in acousticSpeakers ?? [] where speaker.bindingSource == .manual && !speakers.contains(where: { $0.id == speaker.id }) {
            speakers.append(AcousticSpeaker(id: speaker.id, speechSeconds: 0, wordCount: 0, participantID: speaker.participantID,
                                            bindingSource: .manual, suppressed: false))
        }
        acousticSpeakers = speakers.sorted { $0.id < $1.id }
    }

    /// Renaming a "Speaker k" binds its acoustic identity to a person manually. An existing participant with
    /// the same name (ignoring case and surrounding spaces) takes over the identity and the synthetic record is
    /// removed; otherwise the record itself becomes an ordinary, user-named participant. Returns false when
    /// `participantID` is not a "Speaker k".
    mutating func bindAcousticSpeaker(participantID: UUID, name: String) -> Bool {
        let name = name.trimmingCharacters(in: .whitespacesAndNewlines)
        guard var people = participants, let index = people.firstIndex(where: { $0.id == participantID }),
              let acousticID = Self.acousticSpeakerID(of: people[index]), !name.isEmpty else { return false }
        let normalized = name.lowercased()
        let target: UUID
        if let existing = people.first(where: { $0.id != participantID && $0.name.trimmingCharacters(in: .whitespacesAndNewlines).lowercased() == normalized }) {
            target = existing.id
            people.remove(at: index)
            for s in transcript.indices {
                if transcript[s].participantID == participantID { transcript[s].participantID = target }
                guard let words = transcript[s].words else { continue }
                for w in words.indices where words[w].participantID == participantID { transcript[s].words?[w].participantID = target }
            }
        } else {
            target = participantID
            people[index].name = name
            people[index].sourceKey = nil
        }
        participants = people
        var speakers = acousticSpeakers ?? []
        if let existing = speakers.firstIndex(where: { $0.id == acousticID }) {
            speakers[existing].participantID = target
            speakers[existing].bindingSource = .manual
            speakers[existing].suppressed = false
        } else {
            speakers.append(AcousticSpeaker(id: acousticID, speechSeconds: 0, wordCount: 0, participantID: target, bindingSource: .manual, suppressed: false))
        }
        acousticSpeakers = speakers.sorted { $0.id < $1.id }
        // Relabel with the stored segments so the binding applies exactly as a re-run would apply it.
        if let segments = diarization?.segments, !segments.isEmpty { applyAcousticFusion(segments: segments) }
        return true
    }
}
