import Foundation
import Security

struct MeetingRepository {
    let root: URL
    private let requiresExistingRoot: Bool
    init(root: URL? = nil, requiresExistingRoot: Bool = false) {
        self.requiresExistingRoot = requiresExistingRoot || (root == nil && LibraryLocation.selected != nil)
        self.root = root ?? LibraryLocation.selected ?? FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
            .appendingPathComponent("MeetingNotes", isDirectory: true)
    }
    func prepare() throws {
        if requiresExistingRoot && !FileManager.default.fileExists(atPath: root.path) {
            throw MeetingError.message("The selected meeting library is not available. Connect the drive and reopen the app: \(root.path)")
        }
        try FileManager.default.createDirectory(at: root, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
    }
    func load() throws -> [Meeting] {
        try prepare()
        return try FileManager.default.contentsOfDirectory(at: root, includingPropertiesForKeys: nil)
            .filter { $0.pathExtension == "json" && $0.lastPathComponent != "settings.json" }
            .map { try JSONDecoder().decode(Meeting.self, from: Data(contentsOf: $0)) }
            .sorted { $0.createdAt > $1.createdAt }
    }
    func save(_ meeting: Meeting) throws {
        try prepare()
        let data = try JSONEncoder().encode(meeting)
        let url = root.appendingPathComponent("\(meeting.id.uuidString).json")
        try data.write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    func audioDirectory(for id: UUID) throws -> URL {
        try prepare()
        let url = root.appendingPathComponent("Audio/\(id.uuidString)", isDirectory: true)
        try FileManager.default.createDirectory(at: url, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        return url
    }
    func removeAudioFile(_ path: String, meetingID: UUID) throws {
        let directory = root.appendingPathComponent("Audio/\(meetingID.uuidString)").standardizedFileURL
        let url = URL(fileURLWithPath: path).standardizedFileURL
        // Never follow stored paths or symbolic directory links outside this meeting's archive.
        guard url.deletingLastPathComponent() == directory,
              directory.resolvingSymlinksInPath() == root.resolvingSymlinksInPath().appendingPathComponent("Audio/\(meetingID.uuidString)").standardizedFileURL,
              url.pathExtension == "caf" else {
            throw MeetingError.message("Audio is outside the meeting archive and was not deleted.")
        }
        if FileManager.default.fileExists(atPath: url.path) { try FileManager.default.removeItem(at: url) }
    }

    func delete(_ meeting: Meeting) throws {
        let json = root.appendingPathComponent("\(meeting.id.uuidString).json")
        // Removing recordings first keeps the meeting entry available if removal fails.
        let audio = root.appendingPathComponent("Audio/\(meeting.id.uuidString)")
        if FileManager.default.fileExists(atPath: audio.path) { try FileManager.default.removeItem(at: audio) }
        if FileManager.default.fileExists(atPath: json.path) { try FileManager.default.removeItem(at: json) }
    }
    func loadSettings() throws -> AppSettings {
        let path = root.appendingPathComponent("settings.json")
        guard FileManager.default.fileExists(atPath: path.path) else { return AppSettings() }
        return try JSONDecoder().decode(AppSettings.self, from: Data(contentsOf: path))
    }
    func saveSettings(_ settings: AppSettings) throws {
        try prepare()
        try JSONEncoder().encode(settings).write(to: root.appendingPathComponent("settings.json"), options: .atomic)
    }
    func loadFolderCatalog() throws -> FolderCatalog {
        let url = root.appendingPathComponent("Library/folders.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return FolderCatalog() }
        let catalog = try JSONDecoder().decode(FolderCatalog.self, from: Data(contentsOf: url))
        guard Set(catalog.folders.map(\.id)).count == catalog.folders.count else {
            throw MeetingError.message("The folder file contains duplicate folder IDs.")
        }
        return catalog
    }
    func saveFolderCatalog(_ catalog: FolderCatalog) throws {
        try prepare()
        let directory = root.appendingPathComponent("Library", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let url = directory.appendingPathComponent("folders.json")
        try JSONEncoder().encode(catalog).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
    func loadPersonCatalog() throws -> PersonCatalog {
        let url = root.appendingPathComponent("Library/people.json")
        guard FileManager.default.fileExists(atPath: url.path) else { return PersonCatalog() }
        return try JSONDecoder().decode(PersonCatalog.self, from: Data(contentsOf: url))
    }
    func savePersonCatalog(_ catalog: PersonCatalog) throws {
        try prepare()
        let directory = root.appendingPathComponent("Library", isDirectory: true)
        try FileManager.default.createDirectory(at: directory, withIntermediateDirectories: true, attributes: [.posixPermissions: 0o700])
        let url = directory.appendingPathComponent("people.json")
        try JSONEncoder().encode(catalog).write(to: url, options: .atomic)
        try FileManager.default.setAttributes([.posixPermissions: 0o600], ofItemAtPath: url.path)
    }
}

/// Serial background writer for meeting files: `enqueue` coalesces to the latest snapshot per meeting,
/// `write` and `delete` run synchronously but ordered after everything already queued.
final class MeetingWriter: @unchecked Sendable {
    private let queue = DispatchQueue(label: "meetingnotes.writer", qos: .utility)
    private let lock = NSLock()
    private var pending: [UUID: (meeting: Meeting, revision: Int, completion: @MainActor (Result<Void, Error>) -> Void)] = [:]
    private let saveMeeting: @Sendable (Meeting) throws -> Void
    private let deleteMeeting: @Sendable (Meeting) throws -> Void
    private let mirror: TranscriptMirror?

    init(save: @escaping @Sendable (Meeting) throws -> Void, delete: @escaping @Sendable (Meeting) throws -> Void, mirror: TranscriptMirror? = nil) {
        saveMeeting = save; deleteMeeting = delete; self.mirror = mirror
    }
    convenience init(repository: MeetingRepository, mirror: TranscriptMirror? = nil) {
        self.init(save: { try repository.save($0) }, delete: { try repository.delete($0) }, mirror: mirror)
    }

    func enqueue(_ meeting: Meeting, revision: Int, completion: @escaping @MainActor (Result<Void, Error>) -> Void) {
        let id = meeting.id
        lock.lock(); pending[id] = (meeting, revision, completion); lock.unlock()
        queue.async { [self] in
            lock.lock(); let job = pending.removeValue(forKey: id); lock.unlock()
            guard let job else { return }
            let result = Result { try saveMeeting(job.meeting); mirror?.save(job.meeting) }
            Task { @MainActor in job.completion(result) }
        }
    }
    /// A newer synchronous write supersedes a snapshot that has not started yet.
    func write(_ meeting: Meeting) throws {
        lock.lock(); pending.removeValue(forKey: meeting.id); lock.unlock()
        try queue.sync { try saveMeeting(meeting); mirror?.save(meeting) }
    }
    func delete(_ meeting: Meeting) throws {
        lock.lock(); pending.removeValue(forKey: meeting.id); lock.unlock()
        try queue.sync { try deleteMeeting(meeting) }
    }
}

struct SecureCredential {
    // Keychain item name from before the bundle-ID rename; kept so saved API keys are still found.
    private static let service = "local.meetingnotes.api"
    static func load(account: String = "enhancement") -> String {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account,
            kSecReturnData as String: true, kSecMatchLimit as String: kSecMatchLimitOne]
        var result: CFTypeRef?
        guard SecItemCopyMatching(query as CFDictionary, &result) == errSecSuccess,
              let data = result as? Data else { return "" }
        return String(data: data, encoding: .utf8) ?? ""
    }
    static func save(_ value: String, account: String = "enhancement") throws {
        let query: [String: Any] = [kSecClass as String: kSecClassGenericPassword,
            kSecAttrService as String: service, kSecAttrAccount as String: account]
        if value.isEmpty {
            let status = SecItemDelete(query as CFDictionary)
            guard status == errSecSuccess || status == errSecItemNotFound else {
                throw MeetingError.message("The keychain could not remove the API key (\(status)).")
            }
            return
        }
        let attributes = [kSecValueData as String: Data(value.utf8)]
        var status = SecItemUpdate(query as CFDictionary, attributes as CFDictionary)
        if status == errSecItemNotFound {
            var item = query
            item[kSecValueData as String] = Data(value.utf8)
            item[kSecAttrAccessible as String] = kSecAttrAccessibleWhenUnlockedThisDeviceOnly
            status = SecItemAdd(item as CFDictionary, nil)
        }
        guard status == errSecSuccess else { throw MeetingError.message("The keychain could not save the API key (\(status)).") }
    }
}
