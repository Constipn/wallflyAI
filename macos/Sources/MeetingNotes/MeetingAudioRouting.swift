import AppKit
import ApplicationServices
import TeamsDetectionKit

enum MeetingAudioChoice: Hashable {
    case automatic
    case manual(String)
}

struct RecordingAudioSelection {
    /// A session copy: never persist the resolved source over the user's choice.
    var settings: AppSettings
    var explanation = ""

    var displayName: String {
        let id = settings.audioSourceBundleID
        if id.isEmpty { return "All system audio" }
        if id == TeamsAccessibility.bundleID { return "Microsoft Teams" }
        return MeetingAudioSource.runningApplications().first { $0.id == id }?.name ?? id
    }

    func fallingBackToSystem() -> Self {
        var fallback = self
        fallback.settings.audioSourceBundleID = ""
        fallback.explanation = "Teams audio is no longer available · Using system audio"
        return fallback
    }
}

enum TeamsAudioDetection: Equatable {
    case active, absent, notRunning, needsAccessibility, unknown
}

/// Owns the stateful AX reader on one serial queue; no traversal on the UI thread.
final class TeamsAudioProbe: @unchecked Sendable {
    private let queue = DispatchQueue(label: "io.github.constipn.wallfly.audio-selection", qos: .userInitiated)
    private let reader = TeamsCallReader()

    func sample() async -> TeamsAudioDetection {
        await withCheckedContinuation { continuation in
            queue.async { [self] in
                let result: TeamsAudioDetection
                if NSRunningApplication.runningApplications(withBundleIdentifier: TeamsStructure.bundleID).isEmpty {
                    result = .notRunning
                } else if !AXIsProcessTrusted() {
                    result = .needsAccessibility
                } else {
                    switch reader.sample().sample {
                    case .active: result = .active
                    case .absent: result = .absent
                    case .notRunning: result = .notRunning
                    case .unknown: result = .unknown
                    }
                }
                continuation.resume(returning: result)
            }
        }
    }
}

@MainActor
final class MeetingAudioRouting {
    private let sample: () async -> TeamsAudioDetection
    private let now: () -> TimeInterval
    private let pause: () async throws -> Void
    private let timeout: TimeInterval

    init(sample: (() async -> TeamsAudioDetection)? = nil,
         timeout: TimeInterval = 2,
         now: @escaping () -> TimeInterval = { ProcessInfo.processInfo.systemUptime },
         pause: @escaping () async throws -> Void = { try await Task.sleep(for: .milliseconds(150)) }) {
        let probe = TeamsAudioProbe()
        self.sample = sample ?? { await probe.sample() }
        self.timeout = timeout; self.now = now; self.pause = pause
    }

    func resolve(settings: AppSettings) async throws -> RecordingAudioSelection {
        try Task.checkCancellation()
        guard settings.automaticMeetingAudioSource else { return .init(settings: settings) }
        var session = settings
        session.automaticMeetingAudioSource = false
        session.audioSourceBundleID = ""
        let deadline = now() + timeout
        while true {
            let detection = await sample()
            try Task.checkCancellation()
            switch detection {
            case .active:
                session.audioSourceBundleID = TeamsAccessibility.bundleID
                return .init(settings: session)
            case .absent, .notRunning:
                return .init(settings: session)
            case .needsAccessibility:
                return .init(settings: session, explanation: "Teams detection needs Accessibility access · Using system audio")
            case .unknown:
                if now() >= deadline {
                    return .init(settings: session, explanation: "Teams meeting cannot be reliably detected · Using system audio")
                }
                try await pause()
            }
        }
    }

    /// Retry only a missing app source before its capture clock has started.
    /// The caller tears down the unstarted attempt before throwing this error.
    func start(settings: AppSettings, attempt: (RecordingAudioSelection) async throws -> Void) async throws {
        let selection = try await resolve(settings: settings)
        do { try await attempt(selection) }
        catch let error as MeetingAudioSourceUnavailable {
            try Task.checkCancellation()
            guard settings.automaticMeetingAudioSource,
                  selection.settings.audioSourceBundleID == TeamsAccessibility.bundleID else { throw error }
            try await attempt(selection.fallingBackToSystem())
        }
    }
}
