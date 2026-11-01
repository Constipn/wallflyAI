import Foundation
import CoreFoundation
import CryptoKit

public struct MeetingTools: Sendable {
    let library: ReadLibrary
    public init(library: ReadLibrary) { self.library = library }
    public func call(_ tool: String, arguments: Data) throws -> Data {
        guard let a = try JSONSerialization.jsonObject(with: arguments) as? [String: Any] else { throw ReadFailure.invalidArguments }
        let allowed: Set<String>
        switch tool {
        case "search_meetings": allowed = ["query", "from", "to", "participant", "limit", "cursor"]
        case "get_meeting": allowed = ["meeting_id"]
        case "get_transcript": allowed = ["meeting_id", "start", "end", "limit", "cursor"]
        default: throw ReadFailure.invalidArguments
        }
        guard Set(a.keys).isSubset(of: allowed) else { throw ReadFailure.invalidArguments }
        func string(_ key: String) throws -> String? {
            guard let value = a[key] else { return nil }; guard let s = value as? String else { throw ReadFailure.invalidArguments }; return s
        }
        func number(_ key: String) throws -> Double? {
            guard let value = a[key] else { return nil }
            guard let n = value as? NSNumber, CFGetTypeID(n) != CFBooleanGetTypeID(), n.doubleValue.isFinite else { throw ReadFailure.invalidArguments }; return n.doubleValue
        }
        let maximum = tool == "get_transcript" ? 500 : 100
        let rawLimit = try number("limit") ?? (tool == "get_transcript" ? 100 : 20)
        guard rawLimit >= 1, rawLimit <= Double(maximum), rawLimit.rounded() == rawLimit else { throw ReadFailure.invalidArguments }
        let limit = Int(rawLimit)
        let snapshot = try library.snapshot()
        var query = a; query.removeValue(forKey: "cursor")
        let queryData = try JSONSerialization.data(withJSONObject: query, options: [.sortedKeys])
        let signature = SHA256.hash(data: Data(tool.utf8) + queryData + Data(snapshot.revision.utf8)).map { String(format: "%02x", $0) }.joined()
        var offset = 0
        if let cursor = try string("cursor") {
            guard let data = Data(base64Encoded: cursor), let decoded = String(data: data, encoding: .utf8) else { throw ReadFailure.invalidArguments }
            let parts = decoded.split(separator: ":")
            guard parts.count == 2, parts[0] == signature, let value = Int(parts[1]), value >= 0 else { throw ReadFailure.staleCursor }; offset = value
        }
        func encoded<T: Encodable>(_ value: T) throws -> Any { try JSONSerialization.jsonObject(with: JSONEncoder().encode(value)) }
        func page(_ values: [[String: Any]]) throws -> Data {
            guard offset <= values.count else { throw ReadFailure.staleCursor }
            let end = min(values.count, offset + limit)
            let cursor: Any = end < values.count ? Data("\(signature):\(end)".utf8).base64EncodedString() : NSNull()
            return try JSONSerialization.data(withJSONObject: ["results": Array(values[offset..<end]), "next_cursor": cursor, "incomplete": snapshot.skipped > 0, "skipped_meetings": snapshot.skipped], options: [.sortedKeys])
        }
        let iso = ISO8601DateFormatter()
        if tool == "search_meetings" {
            func date(_ key: String) throws -> Date? { guard let s = try string(key) else { return nil }; guard let d = iso.date(from: s) else { throw ReadFailure.invalidArguments }; return d }
            let from = try date("from"), to = try date("to")
            if let from, let to, from > to { throw ReadFailure.invalidArguments }
            let participant = try string("participant"), terms = TextSearch.terms(try string("query") ?? "")
            var results: [(Bool, Date, String, [String: Any])] = []
            for m in snapshot.meetings {
                if let from, m.date < from { continue }; if let to, m.date > to { continue }
                if let participant, !(m.participants ?? []).contains(where: { !TextSearch.ranges(in: $0.name, terms: [participant]).isEmpty }) { continue }
                var hits: [[String: Any]] = []
                let sources: [(String, [(String, Double?, String?, String?)])] = [
                    ("title", [(m.title, nil, nil, nil)]), ("notes", [(m.notes, nil, nil, nil)]),
                    ("enhanced", [(m.cleanEnhancedNotes, nil, nil, nil)]),
                    ("transcript", m.runs.map { ($0.text, $0.offset, $0.speaker, $0.segmentID.uuidString) })]
                for (source, passages) in sources where !terms.isEmpty {
                    let joined = passages.map { $0.0 }.joined(separator: "\n")
                    guard terms.allSatisfy({ !TextSearch.ranges(in: joined, terms: [$0]).isEmpty }) else { continue }
                    for (text, time, speaker, segment) in passages {
                        guard let match = TextSearch.ranges(in: text, terms: terms).first else { continue }
                        let ns = text as NSString
                        let start = max(0, match.location - 45), end = min(ns.length, NSMaxRange(match) + 110)
                        let range = ns.rangeOfComposedCharacterSequences(for: NSRange(location: start, length: end - start))
                        var hit: [String: Any] = ["source": source, "text": ns.substring(with: range)]
                        if let time { hit["offset"] = time }; if let speaker { hit["speaker"] = speaker }; if let segment { hit["segment_id"] = segment }
                        hits.append(hit)
                    }
                }
                if !terms.isEmpty && hits.isEmpty { continue }
                results.append((hits.contains { $0["source"] as? String == "title" }, m.date, m.id.uuidString,
                    ["meeting_id": m.id.uuidString, "title": m.title, "date": iso.string(from: m.date), "hits": Array(hits.prefix(3))]))
            }
            results.sort { if $0.0 != $1.0 { return $0.0 }; if $0.1 != $1.1 { return $0.1 > $1.1 }; return $0.2 < $1.2 }
            return try page(results.map { $0.3 })
        }
        guard let raw = try string("meeting_id"), let id = UUID(uuidString: raw) else { throw ReadFailure.invalidArguments }
        guard let m = snapshot.meetings.first(where: { $0.id == id }) else {
            let root = URL(fileURLWithPath: library.override ?? ReadLibrary.selectedPath)
            if FileManager.default.fileExists(atPath: root.appendingPathComponent(id.uuidString + ".json").path) { throw ReadFailure.damagedMeeting }
            throw ReadFailure.notFound
        }
        if tool == "get_meeting" {
            return try JSONSerialization.data(withJSONObject: ["meeting_id": m.id.uuidString, "title": m.title, "date": iso.string(from: m.date), "duration": m.duration,
                "notes": m.notes, "enhanced_notes": m.cleanEnhancedNotes, "participants": try encoded(m.participants ?? []), "tasks": try encoded(m.tasks ?? [])], options: [.sortedKeys])
        }
        let start = try number("start") ?? 0, end = try number("end") ?? Double.greatestFiniteMagnitude
        guard start >= 0, end >= start else { throw ReadFailure.invalidArguments }
        let runs = m.runs.filter { $0.offset >= start && $0.offset < end }
        let data = try page(try runs.map { try encoded($0) as! [String: Any] })
        var result = try JSONSerialization.jsonObject(with: data) as! [String: Any]
        result["meeting_id"] = m.id.uuidString
        result["transcription_gaps"] = try encoded((m.transcriptionGaps ?? []).filter { $0.start + $0.duration >= start && $0.start < end })
        return try JSONSerialization.data(withJSONObject: result, options: [.sortedKeys])
    }
}
