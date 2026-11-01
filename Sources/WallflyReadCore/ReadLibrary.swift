import Foundation
import CryptoKit

public enum ReadFailure: String, Error { case unavailable, invalidArguments, notFound, damagedMeeting, staleCursor }

/// Explicit projection: unknown persisted fields (including audio paths) never enter a response.
public struct ReadMeeting: Codable, Sendable {
    public let id: UUID
    public let title: String
    public let createdAt: Date
    public let startedAt: Date?
    public let duration: Double
    public let notes: String
    public let enhancedNotes: String
    public let participants: [Participant]?
    public let tasks: [Action]?
    public let transcript: [Segment]
    public let transcriptionGaps: [Gap]?
    public struct Participant: Codable, Sendable { public let id: UUID; public let name: String }
    public struct Action: Codable, Sendable { public let id: UUID; public let text: String; public let owner: String?; public let completedAt: Date?; public let review: String? }
    public struct Gap: Codable, Sendable { public let start: Double; public let duration: Double; public let reason: String; public let speaker: String }
    public struct Word: Codable, Sendable { let text: String; let start: Double; let end: Double; let participantID: UUID? }
    public struct Segment: Codable, Sendable {
        let id: UUID; let speaker: String; let offset: Double; let text: String
        let duration: Double?; let isFinal: Bool; let participantID: UUID?; let words: [Word]?
    }
    public struct Run: Codable, Sendable {
        public let segmentID: UUID; public var wordStart: Int?; public var wordEnd: Int?
        public let source: String; public let participantID: UUID?; public let speaker: String; public let offset: Double; public var text: String; public let isFinal: Bool
    }
    public var date: Date { startedAt ?? createdAt }
    public var cleanEnhancedNotes: String { TextSearch.cleanNotes(enhancedNotes) }
    private struct DisplayItem {
        var turn: Run
        var end: Double
        var words: Int
        var timed: Bool
        var block = 0
        var sortKey: Double
        var start: Double { turn.offset }
    }
    public var runs: [Run] {
        var items: [DisplayItem] = []
        for s in transcript.sorted(by: { $0.offset == $1.offset ? $0.id.uuidString < $1.id.uuidString : $0.offset < $1.offset }) {
            func name(_ id: UUID?) -> String { s.speaker == "Me" ? "Me" : participants?.first(where: { $0.id == id })?.name ?? "Others" }
            if let words = s.words, !words.isEmpty {
                for (i, w) in words.enumerated() where !w.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                    let run = Run(segmentID: s.id, wordStart: i, wordEnd: i + 1, source: s.speaker, participantID: s.speaker == "Me" ? nil : w.participantID, speaker: name(w.participantID), offset: s.offset + w.start, text: w.text.trimmingCharacters(in: .whitespacesAndNewlines), isFinal: s.isFinal)
                    items.append(DisplayItem(turn: run, end: max(run.offset, s.offset + w.end), words: 1, timed: true, sortKey: run.offset))
                }
            } else if !s.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty {
                let run = Run(segmentID: s.id, source: s.speaker, participantID: s.speaker == "Me" ? nil : s.participantID, speaker: name(s.participantID), offset: s.offset, text: s.text.trimmingCharacters(in: .whitespacesAndNewlines), isFinal: s.isFinal)
                items.append(DisplayItem(turn: run, end: s.offset + max(0, s.duration ?? 0), words: run.text.split(whereSeparator: \.isWhitespace).count, timed: false, sortKey: s.offset))
            }
        }
        orderByUtterance(&items)
        items.sort {
            if $0.sortKey != $1.sortKey { return $0.sortKey < $1.sortKey }
            if $0.block != $1.block { return $0.block < $1.block }
            if $0.start != $1.start { return $0.start < $1.start }
            if $0.turn.segmentID != $1.turn.segmentID { return $0.turn.segmentID.uuidString < $1.turn.segmentID.uuidString }
            return ($0.turn.wordStart ?? -1) < ($1.turn.wordStart ?? -1)
        }
        var output: [Run] = []
        for item in items {
            let run = item.turn
            if let last = output.indices.last, output[last].segmentID == run.segmentID, output[last].source == run.source, output[last].participantID == run.participantID,
               let start = run.wordStart, output[last].wordEnd == start {
                output[last].text += " " + run.text; output[last].wordEnd = run.wordEnd
            } else { output.append(run) }
        }
        return output
    }

    private func orderByUtterance(_ items: inout [DisplayItem]) {
        struct Block { var participantID: UUID?; var start: TimeInterval; var end: TimeInterval; var words: Int; var timed: Bool; var members: [Int] }
        func blocks(of speaker: String) -> [Block] {
            var result: [Block] = []
            let members = items.indices.filter { (speaker == "Me" ? items[$0].turn.source == "Me" : items[$0].turn.source != "Me") }.sorted { items[$0].start < items[$1].start }
            for index in members {
                let item = items[index]
                if let last = result.last, last.timed, item.timed, last.participantID == item.turn.participantID,
                   item.start - last.end < 0.7 {
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
        let me = blocks(of: "Me"), others = blocks(of: "Others")
        for (number, block) in (me + others).enumerated() {
            for index in block.members { items[index].block = number }
        }
        for (guests, hosts) in [(me, others), (others, me)] {
            // Walk backwards so the next block's own (possibly deferred) position is known: a deferred
            // backchannel never moves past the next block of its own channel.
            var nextKey = TimeInterval.infinity
            for guest in guests.reversed() {
                var key = guest.start
                if guest.end - guest.start <= 1.2 + 1e-9 && guest.words <= 3 {
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

}

public enum TextSearch {
    public static func terms(_ query: String) -> [String] { Array(Set(query.split(whereSeparator: \.isWhitespace).map { String($0).lowercased() })).sorted() }
    public static func ranges(in text: String, terms: [String]) -> [NSRange] {
        let value = text as NSString
        var found: [NSRange] = []
        for term in terms where !term.isEmpty {
            var start = 0
            while start < value.length {
                let range = value.range(of: term, options: [.caseInsensitive, .diacriticInsensitive], range: NSRange(location: start, length: value.length - start))
                if range.location == NSNotFound { break }
                found.append(range); start = NSMaxRange(range)
            }
        }
        var merged: [NSRange] = []
        for range in found.sorted(by: { $0.location < $1.location }) {
            if let last = merged.last, NSMaxRange(last) >= range.location { merged[merged.count - 1] = NSUnionRange(last, range) }
            else { merged.append(range) }
        }
        return merged
    }
    private static let timestampToken = #"\d{1,3}:\d{2}(?::\d{2})?"#
    private static let timestampCitation = #"\[\#(timestampToken)(?:[ \t]*(?:[-–—,;/]|to|bis)[ \t]*\#(timestampToken))*\](?!\()"#
    private static let timestampRun = #"(?:\#(timestampCitation)[ \t]*)*\#(timestampCitation)"#
    private static let leadingTimestamps = try! NSRegularExpression(
        pattern: #"(?m)^([ \t]*(?:[-*][ \t]+)?)\#(timestampRun)[ \t]*"#)
    private static let inlineTimestamps = try! NSRegularExpression(pattern: #"[ \t]*\#(timestampRun)"#)

    /// Removes transcript timestamp citations such as [03:14] or [03:14–05:20]. Never removes newlines,
    /// so the line count (used by search to map hits) is preserved. Markdown links are left alone.
    public static func cleanNotes(_ text: String) -> String {
        guard text.contains("[") else { return text }
        var result = leadingTimestamps.stringByReplacingMatches(
            in: text, range: NSRange(text.startIndex..., in: text), withTemplate: "$1")
        result = inlineTimestamps.stringByReplacingMatches(
            in: result, range: NSRange(result.startIndex..., in: result), withTemplate: "")
        return result
    }
}

public struct ReadLibrary: Sendable {
    public let override: String?
    public init(path: String? = nil) { override = path }
    public static var selectedPath: String {
        UserDefaults(suiteName: "io.github.constipn.wallfly")?.string(forKey: "meetingLibraryPath")
            ?? FileManager.default.homeDirectoryForCurrentUser.appendingPathComponent("Library/Application Support/Wallfly").path
    }
    public func snapshot() throws -> (meetings: [ReadMeeting], skipped: Int, revision: String) {
        let root = URL(fileURLWithPath: override ?? Self.selectedPath, isDirectory: true)
        guard let urls = try? FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: [.isRegularFileKey, .isSymbolicLinkKey]) else { throw ReadFailure.unavailable }
        var meetings: [ReadMeeting] = [], skipped = 0, hash = SHA256()
        hash.update(data: Data(root.path.utf8))
        for url in urls.sorted(by: { $0.lastPathComponent < $1.lastPathComponent }) where url.pathExtension == "json" && UUID(uuidString: url.deletingPathExtension().lastPathComponent) != nil {
            hash.update(data: Data(url.lastPathComponent.utf8))
            guard let props = try? url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey]), props.isRegularFile == true, props.isSymbolicLink != true, let data = try? Data(contentsOf: url) else { skipped += 1; hash.update(data: Data("unreadable".utf8)); continue }
            hash.update(data: data)
            if let m = try? JSONDecoder().decode(ReadMeeting.self, from: data), m.id == UUID(uuidString: url.deletingPathExtension().lastPathComponent) { meetings.append(m) } else { skipped += 1 }
        }
        return (meetings, skipped, hash.finalize().map { String(format: "%02x", $0) }.joined())
    }
}
