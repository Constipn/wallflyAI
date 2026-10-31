import Foundation

struct TranscriptMirrorDestination: Codable, Equatable {
    var path: String
    var bookmark: Data?

    init(url: URL) {
        path = url.standardizedFileURL.path
        bookmark = try? url.bookmarkData(options: .withSecurityScope, includingResourceValuesForKeys: nil, relativeTo: nil)
    }

    func resolve() throws -> URL {
        guard let bookmark else { return URL(fileURLWithPath: path, isDirectory: true) }
        var stale = false
        return try URL(resolvingBookmarkData: bookmark, options: [.withSecurityScope, .withoutUI],
                       relativeTo: nil, bookmarkDataIsStale: &stale)
    }
}

/// A transcript mirror deliberately contains neither notes nor audio paths.
struct TranscriptMirrorDocument: Codable, Equatable {
    var format = "meetingnotes.transcript.v1"
    var meetingID: UUID
    var title: String
    var createdAt: Date
    var startedAt: Date?
    var duration: TimeInterval
    var transcript: [TranscriptSegment]
    var participants: [MeetingParticipant]?
    var transcriptionGaps: [TranscriptionGap]?

    init(_ meeting: Meeting) {
        meetingID = meeting.id; title = meeting.title; createdAt = meeting.createdAt
        startedAt = meeting.startedAt; duration = meeting.duration; transcript = meeting.transcript
        participants = meeting.participants; transcriptionGaps = meeting.transcriptionGaps
    }
}

/// Runs on the existing serial meeting writer, after the internal save has succeeded.
/// Its failures are reported independently and can never fail that internal save.
final class TranscriptMirror: @unchecked Sendable {
    private let lock = NSLock()
    private var enabled = false
    private var report: (@MainActor @Sendable (UUID, String?) -> Void)?

    func configure(enabled: Bool, report: @escaping @MainActor @Sendable (UUID, String?) -> Void) {
        lock.withLock { self.enabled = enabled; self.report = report }
    }

    func save(_ meeting: Meeting) {
        let (enabled, report) = lock.withLock { (self.enabled, self.report) }
        guard enabled, let destination = meeting.transcriptMirror, !meeting.transcript.isEmpty else { return }
        let message: String?
        do { try Self.write(meeting, to: destination); message = nil }
        catch { message = "The meeting is saved internally. Mirroring the transcript failed: \(error.localizedDescription)" }
        if let report { Task { @MainActor in report(meeting.id, message) } }
    }

    static func write(_ meeting: Meeting, to destination: TranscriptMirrorDestination) throws {
        let directory = try destination.resolve()
        let access = directory.startAccessingSecurityScopedResource()
        defer { if access { directory.stopAccessingSecurityScopedResource() } }
        let values = try directory.resourceValues(forKeys: [.isDirectoryKey])
        guard values.isDirectory == true else { throw MeetingError.message("The transcript folder is not available.") }
        let url = directory.appendingPathComponent("\(meeting.id.uuidString).transcript.json")
        let document = TranscriptMirrorDocument(meeting)
        // Do not replace unrelated files or follow user-created symbolic links.
        if (try? FileManager.default.attributesOfItem(atPath: url.path)) != nil {
            let properties = try url.resourceValues(forKeys: [.isRegularFileKey, .isSymbolicLinkKey])
            guard properties.isRegularFile == true, properties.isSymbolicLink != true,
                  let previous = try? JSONDecoder().decode(TranscriptMirrorDocument.self, from: Data(contentsOf: url)),
                  previous.format == document.format, previous.meetingID == meeting.id else {
                throw MeetingError.message("The destination contains a different file with the same name. It was not changed.")
            }
        }
        let encoder = JSONEncoder()
        encoder.outputFormatting = [.prettyPrinted, .sortedKeys]
        try encoder.encode(document).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}
