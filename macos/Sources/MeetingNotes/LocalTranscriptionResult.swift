import Foundation

struct TranscriptionResult {
    var text: String
    var words: [TranscriptWord]?

    /// OpenAI uses a top-level lexical word array; local Whisper uses segment tokens.
    static func decodeOpenAI(_ data: Data, duration: TimeInterval, includeWords: Bool) throws -> TranscriptionResult {
        guard var root = try JSONSerialization.jsonObject(with: data) as? [String: Any], root["text"] is String else {
            throw MeetingError.message("OpenAI returned an unreadable transcript.")
        }
        let words = root["words"] as? [[String: Any]]
        root["segments"] = words.map { [["words": $0]] } ?? []
        return try decode(JSONSerialization.data(withJSONObject: root), duration: duration, includeWords: includeWords)
    }

    /// whisper.cpp verbose_json carries token text (including spaces) in segments[].words.
    /// Reject unusable timings without losing or inventing any recognized text.
    static func decode(_ data: Data, duration: TimeInterval, includeWords: Bool) throws -> TranscriptionResult {
        guard let root = try JSONSerialization.jsonObject(with: data) as? [String: Any], let text = root["text"] as? String else {
            throw MeetingError.message("The local Whisper model returned an unreadable transcript.")
        }
        let result = TranscriptionResult(text: text.trimmingCharacters(in: .whitespacesAndNewlines))
        guard includeWords, let segments = root["segments"] as? [[String: Any]], !segments.isEmpty else { return result }
        var words: [TranscriptWord] = []
        for segment in segments {
            guard let tokens = segment["words"] as? [[String: Any]] else { return result }
            for token in tokens {
                guard let word = token["word"] as? String, let start = token["start"] as? Double,
                      let end = token["end"] as? Double, start.isFinite, end.isFinite,
                      start >= 0, end >= start, end <= duration + 0.05,
                      words.last.map({ start >= $0.start }) ?? true else { return result }
                guard !word.isEmpty else { continue }
                if start == end, !words.isEmpty, !word.unicodeScalars.contains(where: { CharacterSet.alphanumerics.contains($0) }) {
                    words[words.count - 1].text += word
                } else { words.append(TranscriptWord(text: word, start: start, end: end)) }
            }
        }
        func normalized(_ value: String) -> String { value.split(whereSeparator: \.isWhitespace).joined(separator: " ") }
        guard !words.isEmpty else { return result }
        if normalized(words.map(\.text).joined()) != normalized(result.text) {
            // Also accept true word arrays whose entries do not contain leading spaces.
            guard normalized(words.map(\.text).joined(separator: " ")) == normalized(result.text) else { return result }
            for index in words.indices where index > 0 { words[index].text = " " + words[index].text }
        }
        // whisper.cpp calls these "words", but some builds return BPE token fragments.
        // Keep a lexical word together so a speaker boundary cannot split "Wiederbeschaffung".
        var lexicalWords: [TranscriptWord] = []
        for word in words {
            if !lexicalWords.isEmpty, word.text.first?.isWhitespace != true,
               lexicalWords.last?.text.last?.isWhitespace != true {
                lexicalWords[lexicalWords.count - 1].text += word.text
                lexicalWords[lexicalWords.count - 1].end = max(lexicalWords[lexicalWords.count - 1].end, word.end)
            } else { lexicalWords.append(word) }
        }
        return TranscriptionResult(text: result.text, words: lexicalWords)
    }
}

// Existing local-server callers retain their source-specific API.
typealias LocalTranscriptionResult = TranscriptionResult
