import Foundation
import CoreGraphics
import ScreenCaptureKit

/// One explicit OS request per app run; Start/Refresh never create a prompt loop.
final class ScreenCaptureAuthorization: @unchecked Sendable {
    static let shared = ScreenCaptureAuthorization()
    private let lock = NSLock()
    private var requested = false

    func ensureAccess(preflight: () -> Bool = { CGPreflightScreenCaptureAccess() },
                      request: () -> Bool = { CGRequestScreenCaptureAccess() }) throws {
        if preflight() { return }
        lock.lock()
        let shouldRequest = !requested
        requested = true
        lock.unlock()
        guard shouldRequest else { throw ScreenCaptureAccessError.notAuthorized }
        let accepted = request()
        if preflight() { return }
        if accepted { throw ScreenCaptureAccessError.relaunchRequired }
        throw ScreenCaptureAccessError.notAuthorized
    }
}

enum ScreenCaptureAccessError: LocalizedError {
    case notAuthorized
    case relaunchRequired
    var errorDescription: String? {
        switch self {
        case .notAuthorized:
            return "macOS has not yet allowed this version of Wallfly to access screen/system audio. A switch that is already on may belong to an older version. Open System Settings → Privacy & Security → Screen & System Audio Recording, remove every “Wallfly” entry with “−”, add Wallfly from Applications with “+” and turn it on. Then quit and reopen Wallfly. Your meetings are kept."
        case .relaunchRequired:
            return "macOS has allowed access to screen/system audio. Quit Wallfly completely and reopen it for the permission to take effect."
        }
    }
}

enum CaptureFailure {
    static func describe(_ error: Error, operation: String) -> MeetingError {
        // Our own errors already contain actionable user-facing explanations.
        // Wrapping them again adds a meaningless Swift enum domain/code.
        if let error = error as? MeetingError { return error }
        if let error = error as? MeetingAudioSourceUnavailable { return .message(error.localizedDescription) }
        if let error = error as? ScreenCaptureAccessError { return .message(error.localizedDescription) }
        let native = error as NSError
        if native.domain == SCStreamErrorDomain && native.code == SCStreamError.Code.userDeclined.rawValue {
            return .message(ScreenCaptureAccessError.notAuthorized.localizedDescription)
        }
        // Connection, display and internal errors are not permission denials.
        return .message("\(operation): \(native.localizedDescription) [\(native.domain) \(native.code)]")
    }
}
