import Foundation

/// Search coordinates belong to the current source text, never to persisted meeting data.
enum MeetingSearchSource: String, CaseIterable, Sendable {
    case title, notes, enhanced, transcript
    var label: String {
        switch self {
        case .title: return "Title"
        case .notes: return "My notes"
        case .enhanced: return "Enhanced notes"
        case .transcript: return "Transcript"
        }
    }
}

struct SearchPassage: Identifiable, Equatable, Sendable {
    let id: String
    let source: MeetingSearchSource
    let text: String
    var offset: TimeInterval? = nil
    var segmentID: UUID? = nil
    var label: String { source.label + (offset.map { " · " + Meeting.timestamp($0) } ?? "") }
}

struct MeetingSearchHit: Identifiable, Equatable, Sendable {
    let passage: SearchPassage
    let ranges: [NSRange]
    var id: String { passage.id }
    var snippet: String {
        let value = passage.text as NSString
        guard let first = ranges.first else { return passage.text }
        // NSString boundaries are adjusted to whole composed characters for emoji and accents.
        let start = max(0, first.location - 45)
        let end = min(value.length, first.location + first.length + 110)
        let range = value.rangeOfComposedCharacterSequences(for: NSRange(location: start, length: end - start))
        return (range.location > 0 ? "…" : "") + value.substring(with: range).replacingOccurrences(of: "\n", with: " ") + (NSMaxRange(range) < value.length ? "…" : "")
    }
}

struct MeetingSearchResult: Identifiable, Equatable, Sendable {
    let id: UUID
    let hits: [MeetingSearchHit]
    let date: Date
    var titleMatch: Bool { hits.contains { $0.passage.source == .title } }
}

enum MeetingSearch {
    static func terms(_ query: String) -> [String] {
        Array(Set(query.split(whereSeparator: \.isWhitespace).map { String($0).lowercased() })).sorted()
    }

    static func ranges(in text: String, terms: [String]) -> [NSRange] {
        let value = text as NSString
        var found: [NSRange] = []
        for term in terms where !term.isEmpty {
            var start = 0
            while start < value.length {
                let range = value.range(of: term, options: [.caseInsensitive, .diacriticInsensitive], range: NSRange(location: start, length: value.length - start))
                if range.location == NSNotFound { break }
                found.append(range)
                start = NSMaxRange(range)
            }
        }
        var merged: [NSRange] = []
        for range in found.sorted(by: { $0.location < $1.location }) {
            if let last = merged.last, NSMaxRange(last) >= range.location {
                merged[merged.count - 1] = NSUnionRange(last, range)
            } else { merged.append(range) }
        }
        return merged
    }

    static func passages(_ meeting: Meeting) -> [SearchPassage] {
        MeetingSearchSource.allCases.flatMap { passages(meeting, source: $0) }
    }

    static func passages(_ meeting: Meeting, source: MeetingSearchSource) -> [SearchPassage] {
        switch source {
        case .title:
            return [SearchPassage(id: "title", source: .title, text: meeting.title)]
        case .notes, .enhanced:
            return (source == .notes ? meeting.notes : meeting.enhancedNotes).components(separatedBy: "\n").enumerated().compactMap { index, line in
                guard !line.trimmingCharacters(in: .whitespaces).isEmpty else { return nil }
                return SearchPassage(id: "\(source.rawValue)-\(index)", source: source, text: line)
            }
        case .transcript:
            // Runs preserve the original timestamp and source reference, including provisional replacement.
            return meeting.transcriptRuns.enumerated().map { index, run in
                SearchPassage(id: "transcript-\(index)", source: .transcript, text: run.text, offset: run.offset, segmentID: run.id.segmentID)
            }
        }
    }

    static func search(_ meetings: [Meeting], query: String) -> [MeetingSearchResult] {
        let words = terms(query)
        guard !words.isEmpty else { return [] }
        var results: [MeetingSearchResult] = []
        for meeting in meetings {
            if Task.isCancelled { return [] }
            let passages = passages(meeting)
            var hits: [MeetingSearchHit] = []
            for source in MeetingSearchSource.allCases {
                let parts = passages.filter { $0.source == source }
                let text = parts.map(\.text).joined(separator: "\n")
                guard words.allSatisfy({ !ranges(in: text, terms: [$0]).isEmpty }) else { continue }
                hits += parts.compactMap { part in
                    let matches = ranges(in: part.text, terms: words)
                    return matches.isEmpty ? nil : MeetingSearchHit(passage: part, ranges: matches)
                }
            }
            if !hits.isEmpty { results.append(.init(id: meeting.id, hits: hits, date: meeting.startedAt ?? meeting.createdAt)) }
        }
        return results.sorted {
            if $0.titleMatch != $1.titleMatch { return $0.titleMatch }
            if $0.date != $1.date { return $0.date > $1.date }
            return $0.id.uuidString < $1.id.uuidString
        }
    }
}
