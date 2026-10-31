import Foundation

/// Detects the end of a Teams call during a recording. Never ends a recording that did not
/// first see an active call, so in-person meetings are left alone.
struct MeetingEndPolicy: Equatable {
    static let requiredActiveSamples = 2
    static let requiredAbsence: TimeInterval = 10

    private(set) var isArmed = false
    private(set) var hasEnded = false
    private var activeSamples = 0
    private var absentSince: TimeInterval?

    /// Returns true exactly once, when the call has been gone long enough.
    mutating func observe(_ sample: TeamsAudioDetection, at time: TimeInterval) -> Bool {
        guard !hasEnded else { return false }
        switch sample {
        case .active:
            absentSince = nil
            activeSamples = min(Self.requiredActiveSamples, activeSamples + 1)
            if activeSamples >= Self.requiredActiveSamples { isArmed = true }
        case .absent, .notRunning:
            if !isArmed { activeSamples = 0; return false }
            if absentSince == nil { absentSince = time }
            if time - (absentSince ?? time) >= Self.requiredAbsence { hasEnded = true; return true }
        case .unknown, .needsAccessibility:
            absentSince = nil
            if !isArmed { activeSamples = 0 }
        }
        return false
    }
}

/// Sampling hooks of the end-of-meeting watch; replaced in tests.
struct MeetingEndWatch {
    var sample: () async -> TeamsAudioDetection
    var pause: () async throws -> Void = { try await Task.sleep(for: .seconds(2)) }
    var now: () -> TimeInterval = { ProcessInfo.processInfo.systemUptime }
}
