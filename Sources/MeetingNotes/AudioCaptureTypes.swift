import Foundation

enum CaptureSource: String, Codable, CaseIterable { case microphone, reference, meeting }
extension CaptureSource {
    var displayName: String {
        switch self { case .microphone: "Microphone"; case .reference: "System audio"; case .meeting: "Meeting audio" }
    }
}
/// `degraded`: a source stays unavailable but the recording continues (microphone or the Teams-mode
/// echo reference); only the "others" track can make the whole capture `failed`.
enum CaptureState: String, Equatable { case starting, running, recovering, degraded, stopping, failed }
struct RecordingGap: Codable, Equatable {
    var start: TimeInterval
    var duration: TimeInterval
    var source: CaptureSource
    var reason: String
}
/// Maps a sample position in a saved per-channel CAF file to a meeting offset. Capture gaps
/// are not written as silence, so a new anchor starts whenever the written audio jumps in time;
/// between anchors `offset(frame) = anchor.offset + (frame - anchor.frame) / 16_000`.
struct AudioAnchor: Codable, Equatable, Sendable {
    /// `lastPathComponent` of the CAF file.
    let file: String
    /// Sample index in that file at which the anchor applies.
    let frame: Int
    /// Meeting offset in seconds for that frame.
    let offset: TimeInterval
}
enum CaptureEvent: Sendable {
    case state(CaptureState)
    case source(CaptureSource, CaptureState)
    case microphone(name: String, fallback: Bool, reason: String? = nil)
    case gap(RecordingGap)
}

/// Echo cancellation is the normal recording path. The unfiltered path requires an
/// explicit diagnostic override and is never selected automatically after a failure.
enum CapturePath {
    static var improved: Bool { usesEchoCancellation(environment: ProcessInfo.processInfo.environment) }
    static func usesEchoCancellation(environment: [String: String]) -> Bool {
        environment["MEETINGNOTES_AUDIO_PATH"] != "legacy"
    }
}

struct CaptureRecoveryPolicy {
    static let delays: [Double] = [0.5, 1, 2]
    static let timeout: Double = 5
    static let debounce: Double = 0.2
    static let callbackTimeout: Double = 0.5
    /// First buffer after a microphone start; afterwards the watchdog's `callbackTimeout` applies.
    static let firstBufferTimeout: Double = 2.5
    /// Attempts whose driver call never returned; beyond this no new attempt is started.
    static let maxOrphanedAttempts = 2
}

final class CaptureEventInbox: @unchecked Sendable {
    private let lock = NSLock()
    private var events: [CaptureEvent] = []
    @discardableResult
    func append(_ event: CaptureEvent) -> Bool {
        lock.lock(); defer { lock.unlock() }
        let needsDelivery = events.isEmpty
        if case .gap(let gap) = event,
           let index = events.lastIndex(where: {
               if case .gap(let previous) = $0 {
                   return previous.source == gap.source && previous.reason == gap.reason &&
                       gap.start >= previous.start && gap.start <= previous.start + previous.duration + 0.011
               }
               return false
           }), case .gap(var previous) = events[index] {
            previous.duration = max(previous.start + previous.duration, gap.start + gap.duration) - previous.start
            events[index] = .gap(previous)
        } else { events.append(event) }
        return needsDelivery
    }
    func drain() -> [CaptureEvent] { lock.lock(); defer { lock.unlock() }; let result = events; events.removeAll(keepingCapacity: true); return result }
}

extension Meeting {
    /// Overlapping reasons for the same source describe one missing interval.
    var recordingGapDuration: Double {
        CaptureSource.allCases.reduce(0) { total, source in
            var end = 0.0, duration = 0.0
            for gap in (recordingGaps ?? []).filter({ $0.source == source }).sorted(by: { $0.start < $1.start }) {
                duration += max(0, gap.start + gap.duration - max(end, gap.start))
                end = max(end, gap.start + gap.duration)
            }
            return total + duration
        }
    }
    mutating func appendRecordingGap(_ gap: RecordingGap) {
        var gaps = recordingGaps ?? []
        if let index = gaps.lastIndex(where: { $0.source == gap.source && $0.reason == gap.reason }),
           gap.start >= gaps[index].start, gap.start <= gaps[index].start + gaps[index].duration + 0.011 {
            gaps[index].duration = max(gaps[index].start + gaps[index].duration, gap.start + gap.duration) - gaps[index].start
        } else { gaps.append(gap) }
        recordingGaps = gaps
    }
}

/// Injectable hardware boundary for deterministic lifecycle tests; production uses the
/// Core Audio/AVAudioEngine/ScreenCaptureKit implementation in AudioCapture.
@MainActor
struct CaptureSourceOperations {
    var start: (CaptureSource, UInt64, CaptureInbox) async throws -> Void
    var stop: (CaptureSource) -> Void
    // Lifecycle-only substitutes need no PCM. Readiness tests opt in to the same
    // first-callback requirement that always applies to production Core Audio.
    var requiresInitialAudio = false
    // Production restarts a microphone engine stopped by a startup configuration change.
    var resume: ((CaptureSource) async throws -> Void)? = nil
    // Optional microphone substitutes: a device inventory and a per-device start. Without them the
    // microphone is one pseudo device started through `start`.
    var microphones: (() -> MicrophoneInventory)? = nil
    var startMicrophone: ((MicrophoneDevice, UInt64, CaptureInbox) async throws -> Void)? = nil
}
