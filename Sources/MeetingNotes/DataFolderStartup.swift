import AppKit
import OSLog
import TeamsDetectionKit

/// One-time rename of `Application Support/MeetingNotes` to `Application Support/Wallfly`,
/// run before anything reads or writes the library. A failure leaves the legacy folder in use.
enum DataFolderStartup {
    private static let logger = Logger(subsystem: "io.github.constipn.wallfly", category: "data-folder")
    static let detectorBundleID = "io.github.constipn.wallfly.detector"
    /// Helper bundle ID from before the rename; that build has the legacy Detection folder hard-coded.
    static let legacyDetectorBundleID = "local.meetingnotes.detector"

    /// Evaluated once per process on first access.
    private static let outcome: AppDataFolder.MigrationOutcome = migrate()

    @discardableResult static func prepare() -> AppDataFolder.MigrationOutcome { outcome }

    /// Default library root when no custom location is chosen.
    static var defaultLibraryRoot: URL {
        prepare()
        return AppDataFolder.resolve()
    }

    /// The pre-rename folder whose saved audio paths are read as paths under `root`, if any.
    static func legacyRoot(for root: URL) -> URL? {
        let legacy = AppDataFolder.applicationSupport.appendingPathComponent(AppDataFolder.legacyName, isDirectory: true)
        return root.standardizedFileURL == legacy.standardizedFileURL ? nil : legacy
    }

    private static func migrate() -> AppDataFolder.MigrationOutcome {
        let base = AppDataFolder.applicationSupport
        let legacy = base.appendingPathComponent(AppDataFolder.legacyName, isDirectory: true)
        let result = AppDataFolder.migrateLegacyFolder(in: base, beforeMove: {
            // Never rename a folder the user chose as the meeting library.
            if let selected = LibraryLocation.selected, isSameOrInside(selected, legacy) {
                logger.notice("Data folder rename skipped: the chosen library lives in the legacy folder")
                return false
            }
            return stopRunningDetector()
        })
        switch result {
        case .freshInstall: logger.info("Data folder: new install")
        case .alreadyMigrated: break
        case .migrated: logger.notice("Data folder renamed from \(AppDataFolder.legacyName, privacy: .public) to \(AppDataFolder.name, privacy: .public)")
        case .bothExist:
            logger.warning("Data folders \(AppDataFolder.legacyName, privacy: .public) and \(AppDataFolder.name, privacy: .public) both exist; using \(AppDataFolder.name, privacy: .public), the legacy folder is left untouched")
        case .skipped: logger.notice("Data folder rename skipped; still using \(AppDataFolder.legacyName, privacy: .public)")
        case .failed(let message):
            logger.error("Data folder rename failed; still using \(AppDataFolder.legacyName, privacy: .public): \(message, privacy: .public)")
        }
        return result
    }

    static func isSameOrInside(_ url: URL, _ folder: URL) -> Bool {
        let path = url.resolvingSymlinksInPath().standardizedFileURL.path
        let folderPath = folder.resolvingSymlinksInPath().standardizedFileURL.path
        return path == folderPath || path.hasPrefix(folderPath + "/")
    }

    /// A running helper (possibly an older build with a fixed legacy path) would recreate the
    /// legacy Detection folder after the rename. Stop it first; the app relaunches it later.
    /// Returns `false` if it is still running after the timeout, which skips the rename.
    private static func stopRunningDetector(timeout: TimeInterval = 3) -> Bool {
        let running = [detectorBundleID, legacyDetectorBundleID]
            .flatMap { NSRunningApplication.runningApplications(withBundleIdentifier: $0) }
        guard !running.isEmpty else { return true }
        let pids = running.map(\.processIdentifier)
        running.forEach { $0.terminate() }
        func alive(_ pid: pid_t) -> Bool { kill(pid, 0) == 0 || errno == EPERM }
        let deadline = ProcessInfo.processInfo.systemUptime + timeout
        while pids.contains(where: alive), ProcessInfo.processInfo.systemUptime < deadline { usleep(50_000) }
        guard !pids.contains(where: alive) else {
            logger.error("Background helper did not quit in time; data folder rename postponed")
            return false
        }
        return true
    }
}
