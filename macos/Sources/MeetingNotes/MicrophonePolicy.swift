import CoreAudio
import Foundation
import ScreenCaptureKit

/// A started device that delivered no PCM within the first-buffer deadline.
struct MicrophoneNoAudioError: LocalizedError {
    var errorDescription: String? { "Microphone delivers no audio." }
}
/// The driver did not return from the start call within the deadline (the attempt is orphaned).
struct MicrophoneStartTimeoutError: LocalizedError {
    var errorDescription: String? { "Timed out while starting the microphone." }
}
/// Too many earlier attempts are still blocked inside the audio driver.
struct MicrophoneDriverBlockedError: LocalizedError {
    var errorDescription: String? { "Audio driver blocked." }
}

enum MicrophonePolicy {
    static func fourCC(_ code: String) -> OSStatus {
        OSStatus(bitPattern: code.utf8.reduce(UInt32(0)) { $0 << 8 | UInt32($1) })
    }
    static func status(of error: Error) -> OSStatus? {
        if let core = error as? CoreAudioCaptureError { return core.status }
        let native = error as NSError
        guard native.domain == NSOSStatusErrorDomain || native.domain == "com.apple.coreaudio.avfaudio" else { return nil }
        return OSStatus(truncatingIfNeeded: native.code)
    }
    /// The only failure that stops all retries.
    static func permissionDenied(_ error: Error) -> Bool {
        if error is ScreenCaptureAccessError { return true }
        if status(of: error) == kAudioDevicePermissionsError { return true }
        let native = error as NSError
        return native.domain == SCStreamErrorDomain && native.code == SCStreamError.Code.userDeclined.rawValue
    }
    /// Short reason for the fallback hint, e.g. "USB microphone unavailable".
    static func reason(_ error: Error, device: MicrophoneDevice) -> String {
        error is MicrophoneNoAudioError ? "\(device.name) delivers no audio" : "\(device.name) unavailable"
    }
    static func describe(_ error: Error) -> String {
        let native = error as NSError
        let phase = native.userInfo["MNAudioPhase"] as? String ?? "-"
        return "code=\(status(of: error).map(String.init) ?? "\(native.domain) \(native.code)") phase=\(phase)"
    }
}

/// Microphone acquire: 3 attempts 500ms apart, then background re-acquire 1s, 2s, 4s …
/// capped at 30s; 5 background retries is our chosen default.
struct MicrophoneRecoveryPolicy {
    var attempts = 3
    var attemptDelay = 0.5
    var backgroundRetryBase = 1.0, backgroundRetryCap = 30.0
    var backgroundRetries = 5
    /// Deadline for the first buffer of a started device (the watchdog afterwards uses 0.5s).
    var firstBufferTimeout = CaptureRecoveryPolicy.firstBufferTimeout
    var maxOrphanedAttempts = CaptureRecoveryPolicy.maxOrphanedAttempts
    func backgroundRetryDelay(_ retry: Int) -> Double { min(backgroundRetryCap, backgroundRetryBase * pow(2, Double(retry))) }
    static let standard = MicrophoneRecoveryPolicy()
}

struct MicrophoneInventory: Equatable {
    var devices: [MicrophoneDevice]
    var defaultUID: String
}

struct MicrophoneCandidate: Equatable {
    let device: MicrophoneDevice
    /// The system default replacing a pinned device.
    let fallback: Bool
    let reason: String?
}

enum MicrophoneCandidates {
    /// The pinned device if present, then the system default. No other devices are ever used.
    static func order(_ inventory: MicrophoneInventory, selectedUID: String) -> [MicrophoneCandidate] {
        let pinned = selectedUID.isEmpty ? nil : inventory.devices.first { $0.id == selectedUID }
        let systemDefault = inventory.devices.first { $0.id == inventory.defaultUID }
        var result: [MicrophoneCandidate] = []
        if let pinned { result.append(MicrophoneCandidate(device: pinned, fallback: false, reason: nil)) }
        if let systemDefault, systemDefault.id != pinned?.id {
            let missing = !selectedUID.isEmpty && pinned == nil
            result.append(MicrophoneCandidate(device: systemDefault, fallback: !selectedUID.isEmpty,
                                              reason: missing ? "selected microphone missing" : nil))
        }
        return result
    }
}
