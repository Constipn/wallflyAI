import Foundation

struct TranscriptionGap: Codable, Equatable {
    var start: TimeInterval
    var duration: TimeInterval
    var speaker: Speaker
    var reason: String
}

enum TranscriptionEvent: Sendable {
    case state(Speaker, String)
    case gap(TranscriptionGap)
    /// Pipeline-relative offset; the store adds the session base like for gaps and segments.
    case anchor(AudioAnchor)
}

/// Coalesce notifications before crossing to the main actor, including during stop.
final class TranscriptionEventInbox: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [TranscriptionEvent] = []
    @discardableResult func append(_ event: TranscriptionEvent) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let notify = events.isEmpty
        switch event {
        case .state(let source, _):
            events.removeAll { if case .state(let old, _) = $0 { return source == old }; return false }
        case .gap(let gap):
            if let index = events.lastIndex(where: {
                if case .gap(let old) = $0 {
                    return old.speaker == gap.speaker && old.reason == gap.reason &&
                        gap.start >= old.start && gap.start <= old.start + old.duration + 0.001
                }
                return false
            }), case .gap(var old) = events[index] {
                old.duration = max(old.start + old.duration, gap.start + gap.duration) - old.start
                events[index] = .gap(old)
                return notify
            }
        case .anchor: break
        }
        events.append(event)
        return notify
    }
    func drain() -> [TranscriptionEvent] {
        lock.lock(); defer { lock.unlock() }
        let result = events; events.removeAll(keepingCapacity: true); return result
    }
}

extension Meeting {
    mutating func appendTranscriptionGap(_ gap: TranscriptionGap) {
        var gaps = transcriptionGaps ?? []
        if let index = gaps.lastIndex(where: {
            $0.speaker == gap.speaker && $0.reason == gap.reason && gap.start >= $0.start && gap.start <= $0.start + $0.duration + 0.001
        }) {
            gaps[index].duration = max(gaps[index].start + gaps[index].duration, gap.start + gap.duration) - gaps[index].start
        } else { gaps.append(gap) }
        transcriptionGaps = gaps
    }
    mutating func appendAudioAnchor(_ anchor: AudioAnchor) {
        audioAnchors = (audioAnchors ?? []) + [anchor]
    }
}

struct DeepgramResult: Decodable {
    struct Channel: Decodable { var alternatives: [Alternative] }
    struct Alternative: Decodable {
        var transcript: String
        var words: [Word]?
    }
    struct Word: Decodable {
        var word: String
        var punctuated_word: String?
        var start: Double
        var end: Double
    }
    var type: String
    var start: Double?
    var duration: Double?
    var is_final: Bool?
    var speech_final: Bool?
    var channel: Channel?
}

/// Only this connection's provisional tail is replaceable. Final identities are audio intervals,
/// never text, so concurrent identical utterances on independent sources survive.
struct DeepgramResultAssembler {
    let speaker: Speaker
    let generation: UUID
    let anchor: Double
    private(set) var confirmedThrough: Double = 0
    private var finalized: [Range<Int>] = []
    private var preview: TranscriptSegment?

    init(speaker: Speaker, generation: UUID, anchor: Double) {
        self.speaker = speaker; self.generation = generation; self.anchor = anchor
    }

    mutating func clearPreview() -> [TranscriptSegment] {
        guard var old = preview else { return [] }
        preview = nil
        old.text = ""; old.words = nil; old.isFinal = true
        return [old]
    }

    mutating func accept(_ result: DeepgramResult, sentDuration: Double) -> [TranscriptSegment] {
        guard result.type == "Results", let start = result.start, let duration = result.duration,
              start.isFinite, duration.isFinite, start >= 0, duration >= 0,
              start + duration <= sentDuration + 0.02,
              let alternative = result.channel?.alternatives.first else { return [] }
        let end = start + duration
        let range = Int((start * 16_000).rounded())..<Int((end * 16_000).rounded())
        let final = result.is_final == true
        if finalized.contains(where: { $0.lowerBound <= range.lowerBound && $0.upperBound >= range.upperBound }) { return [] }
        let words = (alternative.words ?? []).filter { word in
            word.start.isFinite && word.end.isFinite && word.start >= start - 0.02 && word.end >= word.start &&
            word.end <= end + 0.02 && !finalized.contains { interval in
                interval.contains(Int(((word.start + word.end) * 8_000).rounded()))
            }
        }
        // Do not allow a text-only late preview to cover a previously finalized interval.
        if words.isEmpty && !alternative.transcript.isEmpty && finalized.contains(where: { $0.overlaps(range) }) { return [] }
        let filtered = words.count != (alternative.words ?? []).count
        let segmentStart = filtered ? (words.first?.start ?? start) : start
        let text = filtered ? words.map { $0.punctuated_word ?? $0.word }.joined(separator: " ") : alternative.transcript
        let oldPreview = preview
        var emitted = clearPreview()
        let segment = TranscriptSegment(
            id: (!final && oldPreview?.offset == anchor + segmentStart) ? oldPreview!.id : UUID(),
            speaker: speaker, offset: anchor + segmentStart, text: text, duration: max(0, end - segmentStart),
            isFinal: final, words: words.isEmpty ? nil : words.map {
                TranscriptWord(text: $0.punctuated_word ?? $0.word, start: max(0, $0.start - segmentStart), end: max(0, $0.end - segmentStart))
            }, connectionID: generation)
        if final {
            finalized.append(range)
            confirmedThrough = max(confirmedThrough, end)
            if !text.isEmpty { emitted.append(segment) }
            // A final can confirm only the prefix of the currently displayed preview.
            if var tail = oldPreview, let previousWords = tail.words {
                let remaining = previousWords.filter { tail.offset + $0.start >= anchor + end - 0.0001 }
                if !remaining.isEmpty {
                    let shift = remaining[0].start
                    tail.id = UUID()
                    tail.offset += shift
                    tail.duration = max(0, tail.duration - shift)
                    tail.words = remaining.map { word in
                        var word = word; word.start -= shift; word.end -= shift; return word
                    }
                    tail.text = remaining.map(\.text).joined(separator: " ")
                    preview = tail
                    emitted.append(tail)
                }
            }
        } else if !text.isEmpty {
            preview = segment
            emitted.append(segment)
        }
        return emitted
    }
}
