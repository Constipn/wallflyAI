import AppKit
import ApplicationServices

/// Chromium debounces AXEnhancedUserInterface for two seconds. Repeating the
/// enable request every poll can keep postponing publication of its web tree.
struct TeamsWebActivation {
    struct Attempt {
        var result: AXError
        var time: TimeInterval
        func isInitializing(at now: TimeInterval) -> Bool { result == .success && now - time < 3 }
    }
    private var attempts: [Int32: Attempt] = [:]

    mutating func retain(_ pids: Set<Int32>) { attempts = attempts.filter { pids.contains($0.key) } }

    mutating func request(pid: Int32, at now: TimeInterval, enable: () -> AXError) -> Attempt {
        if let previous = attempts[pid], previous.result != .cannotComplete || now - previous.time < 30 {
            return previous
        }
        let attempt = Attempt(result: enable(), time: now)
        attempts[pid] = attempt
        return attempt
    }
}

enum TeamsWebProcess {
    static let bundleID = "com.microsoft.teams2.helper"

    /// The Teams bundle ships its own WebView host. Do not enable accessibility
    /// in other Chromium applications, renderers, or a similarly named process.
    static func belongsToTeams(bundleID: String?, url: URL?, owner: URL?) -> Bool {
        guard bundleID == Self.bundleID, let url, let owner else { return false }
        let expected = owner.appendingPathComponent("Contents/Helpers/Microsoft Teams WebView.app")
        return url.resolvingSymlinksInPath().standardizedFileURL == expected.resolvingSymlinksInPath().standardizedFileURL
    }
}
