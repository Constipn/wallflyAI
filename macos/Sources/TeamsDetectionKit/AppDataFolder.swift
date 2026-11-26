import Foundation

/// The app's data folder in Application Support, shared by the app and the login helper.
///
/// Earlier versions used `MeetingNotes`. Both processes pick the folder with the same rule
/// (`resolve`), so they agree on the Detection directory before, during and after the one-time
/// rename. Only the main app renames (`migrateLegacyFolder`); the helper never does.
public enum AppDataFolder {
    public static let name = "Wallfly"
    public static let legacyName = "MeetingNotes"

    public static var applicationSupport: URL {
        FileManager.default.urls(for: .applicationSupportDirectory, in: .userDomainMask)[0]
    }

    /// `Wallfly` if it exists, otherwise an existing legacy `MeetingNotes` folder, otherwise `Wallfly`.
    /// When both exist, `Wallfly` wins and the legacy folder is left untouched.
    public static func resolve(in base: URL = applicationSupport, fileManager: FileManager = .default) -> URL {
        let current = base.appendingPathComponent(name, isDirectory: true)
        let legacy = base.appendingPathComponent(legacyName, isDirectory: true)
        if isDirectory(current, fileManager) { return current }
        if isDirectory(legacy, fileManager) { return legacy }
        return current
    }

    public enum MigrationOutcome: Equatable, Sendable {
        /// Neither folder exists; the app creates `Wallfly` on first write.
        case freshInstall
        /// Only `Wallfly` exists.
        case alreadyMigrated
        /// `MeetingNotes` was renamed to `Wallfly`.
        case migrated
        /// Both exist. Nothing is moved, merged or deleted; `Wallfly` is used.
        case bothExist
        /// The rename was not attempted (`beforeMove` declined); the legacy folder stays in use.
        case skipped
        /// The rename failed; the legacy folder stays in use.
        case failed(String)
    }

    /// Renames `<base>/MeetingNotes` to `<base>/Wallfly` once, atomically and without replacing anything.
    /// `beforeMove` runs only when a rename is about to happen; returning `false` skips it.
    public static func migrateLegacyFolder(in base: URL = applicationSupport, fileManager: FileManager = .default,
                                           beforeMove: () -> Bool = { true },
                                           move: (URL, URL) throws -> Void = renameExclusive) -> MigrationOutcome {
        let current = base.appendingPathComponent(name, isDirectory: true)
        let legacy = base.appendingPathComponent(legacyName, isDirectory: true)
        let hasCurrent = isDirectory(current, fileManager)
        let hasLegacy = isDirectory(legacy, fileManager)
        switch (hasLegacy, hasCurrent) {
        case (false, false): return .freshInstall
        case (false, true): return .alreadyMigrated
        case (true, true): return .bothExist
        case (true, false):
            guard beforeMove() else { return .skipped }
            do { try move(legacy, current) } catch { return .failed(error.localizedDescription) }
            return .migrated
        }
    }

    /// Same-volume atomic rename that fails instead of replacing an existing destination,
    /// including an empty directory (which plain rename(2) would replace).
    public static func renameExclusive(_ source: URL, _ destination: URL) throws {
        guard renamex_np(source.path, destination.path, UInt32(RENAME_EXCL)) == 0 else {
            let code = errno
            throw NSError(domain: NSPOSIXErrorDomain, code: Int(code),
                          userInfo: [NSLocalizedDescriptionKey: "Renaming \(source.path) to \(destination.path) failed: \(String(cString: strerror(code)))"])
        }
    }

    private static func isDirectory(_ url: URL, _ fileManager: FileManager) -> Bool {
        var directory: ObjCBool = false
        return fileManager.fileExists(atPath: url.path, isDirectory: &directory) && directory.boolValue
    }
}
