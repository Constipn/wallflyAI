import AppKit

struct MeetingAudioSourceUnavailable: LocalizedError {
    var message: String
    var errorDescription: String? { message }
}

/// App liveness and ScreenCaptureKit availability are separate checks. An app
/// can be running without appearing in the current shareable-content snapshot.
enum MeetingAudioSource {
    static func runningApplications() -> [AudioSource] {
        NSWorkspace.shared.runningApplications.compactMap { app in
            guard let id = app.bundleIdentifier, !app.isTerminated else { return nil }
            return AudioSource(id: id, name: app.localizedName ?? id)
        }
    }

    static func requireRunning(bundleID: String,
                               applications: () -> [AudioSource] = runningApplications) throws {
        // The global source does not require a particular meeting app.
        guard !bundleID.isEmpty else { return }
        let running = applications()
        guard running.contains(where: { $0.id == bundleID }) else {
            throw unavailable(bundleID: bundleID, applications: { running })
        }
    }

    static func unavailable(bundleID: String,
                            applications: () -> [AudioSource] = runningApplications) -> MeetingAudioSourceUnavailable {
        let app = applications().first { $0.id == bundleID }
        let name = app?.name ?? (bundleID == TeamsAccessibility.bundleID ? "Microsoft Teams" : bundleID)
        if app != nil {
            return .init(message: "“\(name)” is running, but macOS does not offer it as an audio source yet. Open a window of this app and try recording again. Under Settings → Recording → Meeting audio you can choose a different source.")
        }
        return .init(message: "“\(name)” is selected as meeting audio, but the app is not open. Open it and start recording again. To use a different audio source, change Settings → Recording → Meeting audio.")
    }
}
