import Foundation

/// The location pointer lives outside the movable library. Credentials remain in Keychain;
/// the login helper's Detection directory stays at its fixed Application Support location.
enum LibraryLocation {
    static let key = "meetingLibraryPath"
    static var selected: URL? {
        UserDefaults.standard.string(forKey: key).map { URL(fileURLWithPath: $0, isDirectory: true) }
    }
    static func remember(_ url: URL) throws {
        let defaults = UserDefaults.standard
        let previous = defaults.string(forKey: key)
        defaults.set(url.path, forKey: key)
        // Commit the pointer before deleting any original data, including across a sudden exit.
        guard defaults.synchronize() else {
            defaults.set(previous, forKey: key)
            _ = defaults.synchronize()
            throw MeetingError.message("The new library path could not be saved. The original library is kept.")
        }
    }
}

struct LibraryMigration {
    let source: MeetingRepository
    let destination: MeetingRepository
    let movedNames: [String]
    let meetings: [Meeting]

    static func prepare(source: MeetingRepository, destinationURL: URL) throws -> Self {
        let fm = FileManager.default
        let old = source.root.resolvingSymlinksInPath().standardizedFileURL
        let target = destinationURL.resolvingSymlinksInPath().standardizedFileURL
        guard target != old, !target.path.hasPrefix(old.path + "/"),
              !old.path.hasPrefix(target.path + "/"), !fm.fileExists(atPath: target.path) else {
            throw MeetingError.message("Choose a different folder without an existing MeetingNotes library. Existing files are not overwritten.")
        }
        let names = try fm.contentsOfDirectory(atPath: old.path).filter { $0 != "Detection" }
        let destination = MeetingRepository(root: target)
        try destination.prepare()
        do {
            for name in names {
                try fm.copyItem(at: old.appendingPathComponent(name), to: target.appendingPathComponent(name))
            }
            var meetings = try destination.load()
            for index in meetings.indices {
                func rebase(_ path: String) -> String {
                    let url = URL(fileURLWithPath: path).standardizedFileURL
                    let prefix = source.root.standardizedFileURL.path + "/Audio/"
                    guard url.path.hasPrefix(prefix) else { return path }
                    return target.appendingPathComponent("Audio/" + url.path.dropFirst(prefix.count)).path
                }
                meetings[index].audioFiles = meetings[index].audioFiles.map(rebase)
                meetings[index].audioDeletionPending = meetings[index].audioDeletionPending?.map(rebase)
                try destination.save(meetings[index])
            }
            _ = try destination.loadSettings()
            _ = try destination.loadFolderCatalog()
            return Self(source: source, destination: destination, movedNames: names, meetings: meetings)
        } catch {
            // Only our newly created destination is removed; the original library is untouched.
            try? fm.removeItem(at: target)
            throw error
        }
    }

    /// Call only after the app has switched its persistent location pointer and writer.
    func removeOriginals() throws {
        for name in movedNames { try FileManager.default.removeItem(at: source.root.appendingPathComponent(name)) }
    }
}
