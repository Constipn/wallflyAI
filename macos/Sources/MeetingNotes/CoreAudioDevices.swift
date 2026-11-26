import AVFoundation
import CoreAudio
import SafeAudio

struct MicrophoneDevice: Identifiable, Equatable {
    let id: String // stable UID
    let objectID: AudioDeviceID
    let name: String
    var transport: UInt32 = 0
    var isVirtualOrAggregate: Bool { transport == kAudioDeviceTransportTypeVirtual || transport == kAudioDeviceTransportTypeAggregate }
}

struct CoreAudioCaptureError: LocalizedError {
    let status: OSStatus
    let operation: String
    var errorDescription: String? { "\(operation) (Core Audio \(status))." }
}

enum CoreAudioDevices {
    static func address(_ selector: AudioObjectPropertySelector, scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal) -> AudioObjectPropertyAddress {
        AudioObjectPropertyAddress(mSelector: selector, mScope: scope, mElement: kAudioObjectPropertyElementMain)
    }
    static func check(_ status: OSStatus, _ operation: String) throws {
        guard status == noErr else { throw CoreAudioCaptureError(status: status, operation: operation) }
    }
    static func value<T: BitwiseCopyable>(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector, initial: T) throws -> T {
        var property = address(selector), value = initial, size = UInt32(MemoryLayout<T>.size)
        try check(AudioObjectGetPropertyData(object, &property, 0, nil, &size, &value), "Could not read audio device")
        return value
    }
    static func string(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector) -> String? {
        var property = address(selector), value: Unmanaged<CFString>?
        var size = UInt32(MemoryLayout<Unmanaged<CFString>?>.size)
        guard AudioObjectGetPropertyData(object, &property, 0, nil, &size, &value) == noErr else { return nil }
        return value?.takeRetainedValue() as String?
    }
    static func inputStreamCount(_ device: AudioObjectID) -> Int {
        var input = address(kAudioDevicePropertyStreams, scope: kAudioDevicePropertyScopeInput), bytes: UInt32 = 0
        guard AudioObjectGetPropertyDataSize(device, &input, 0, nil, &bytes) == noErr else { return 0 }
        return Int(bytes) / MemoryLayout<AudioStreamID>.size
    }
    static func describe(_ device: AudioDeviceID) -> MicrophoneDevice {
        MicrophoneDevice(id: string(device, kAudioDevicePropertyDeviceUID) ?? "", objectID: device,
                         name: string(device, kAudioObjectPropertyName) ?? "System microphone",
                         transport: (try? value(device, kAudioDevicePropertyTransportType, initial: UInt32(0))) ?? 0)
    }
    /// Every input device, including virtual and aggregate ones (usable only when preferred).
    static func inventory() -> MicrophoneInventory {
        var property = address(kAudioHardwarePropertyDevices), size: UInt32 = 0
        let system = AudioObjectID(kAudioObjectSystemObject)
        var devices: [AudioDeviceID] = []
        if AudioObjectGetPropertyDataSize(system, &property, 0, nil, &size) == noErr {
            devices = [AudioDeviceID](repeating: 0, count: Int(size) / MemoryLayout<AudioDeviceID>.size)
            if AudioObjectGetPropertyData(system, &property, 0, nil, &size, &devices) != noErr { devices = [] }
        }
        let inputs = devices.filter { inputStreamCount($0) > 0 }.map(describe).filter { !$0.id.isEmpty }
            .sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
        let defaultID = (try? value(system, kAudioHardwarePropertyDefaultInputDevice, initial: AudioDeviceID(0))) ?? 0
        var defaultUID = inputs.first(where: { $0.objectID == defaultID })?.id ?? ""
        if defaultUID.isEmpty, defaultID != 0 { defaultUID = string(defaultID, kAudioDevicePropertyDeviceUID) ?? "" }
        return MicrophoneInventory(devices: inputs, defaultUID: defaultUID)
    }
    /// Devices offered in the picker.
    static func microphones() -> [MicrophoneDevice] { inventory().devices.filter { !$0.isVirtualOrAggregate } }
    /// First candidate of `MicrophoneCandidates.order` (pinned, else default); used by the legacy path.
    static func resolve(uid: String) throws -> (device: MicrophoneDevice, fallback: Bool) {
        guard let first = MicrophoneCandidates.order(inventory(), selectedUID: uid).first else { throw MeetingError.message("No microphone available.") }
        return (first.device, first.fallback)
    }
}

/// Retains listener blocks and removes exactly those blocks at teardown.
final class AudioDeviceObservation {
    private let queue: DispatchQueue
    private var entries: [(AudioObjectID, AudioObjectPropertyAddress, AudioObjectPropertyListenerBlock)] = []
    init(queue: DispatchQueue) { self.queue = queue }
    func observe(_ object: AudioObjectID, _ selector: AudioObjectPropertySelector,
                 scope: AudioObjectPropertyScope = kAudioObjectPropertyScopeGlobal, changed: @escaping () -> Void) {
        var address = CoreAudioDevices.address(selector, scope: scope)
        let block: AudioObjectPropertyListenerBlock = { _, _ in changed() }
        if AudioObjectAddPropertyListenerBlock(object, &address, queue, block) == noErr { entries.append((object, address, block)) }
    }
    deinit {
        for (object, var address, block) in entries { AudioObjectRemovePropertyListenerBlock(object, &address, queue, block) }
    }
}

/// A private, unmuted, mono global tap. PCM exists only in the bounded capture inbox.
// start/stop are confined to the reference hardware queue; the main actor only
// reads objectID after start completes, before it relinquishes ownership to stop.
final class PlaybackReferenceTap: @unchecked Sendable {
    private var tap: AudioObjectID = 0
    private var aggregate: AudioObjectID = 0
    private var ioProc: AudioDeviceIOProcID?
    private(set) var format: AVAudioFormat?
    var objectID: AudioObjectID { tap }
    func start(inbox: CaptureInbox, generation: UInt64) throws {
        do {
            var pid = getpid(), process: AudioObjectID = 0
            var property = CoreAudioDevices.address(kAudioHardwarePropertyTranslatePIDToProcessObject)
            var size = UInt32(MemoryLayout<AudioObjectID>.size)
            try CoreAudioDevices.check(AudioObjectGetPropertyData(AudioObjectID(kAudioObjectSystemObject), &property,
                UInt32(MemoryLayout<pid_t>.size), &pid, &size, &process), "Could not exclude own audio process")
            guard process != 0 else { throw MeetingError.message("Own audio process unavailable.") }
            let description = CATapDescription(monoGlobalTapButExcludeProcesses: [process])
            description.name = "Wallfly Echo Reference"
            description.isPrivate = true
            description.muteBehavior = .unmuted
            try CoreAudioDevices.check(AudioHardwareCreateProcessTap(description, &tap), "Could not start echo reference. Check system audio access")
            var asbd = try CoreAudioDevices.value(tap, kAudioTapPropertyFormat, initial: AudioStreamBasicDescription())
            guard let format = AVAudioFormat(streamDescription: &asbd), format.sampleRate > 0 else { throw MeetingError.message("Invalid echo reference format.") }
            self.format = format
            let composition: [String: Any] = [
                kAudioAggregateDeviceNameKey: "Wallfly Private Reference",
                kAudioAggregateDeviceUIDKey: UUID().uuidString,
                kAudioAggregateDeviceIsPrivateKey: true,
                // true defers aggregate start until the first tapped playback. We
                // need hardware-timed zero PCM while output is idle so microphone
                // AEC/readiness can run before the other participants speak.
                kAudioAggregateDeviceTapAutoStartKey: false,
                kAudioAggregateDeviceTapListKey: [[kAudioSubTapUIDKey: description.uuid.uuidString, kAudioSubTapDriftCompensationKey: true]]
            ]
            try CoreAudioDevices.check(AudioHardwareCreateAggregateDevice(composition as CFDictionary, &aggregate), "Could not create reference device")
            try CoreAudioDevices.check(AudioDeviceCreateIOProcIDWithBlock(&ioProc, aggregate, nil) { _, input, inputTime, _, _ in
                let list = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: input))
                guard let first = list.first, asbd.mBytesPerFrame > 0 else { return }
                let count = first.mDataByteSize / asbd.mBytesPerFrame
                guard count > 0, let copy = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: count) else { return }
                copy.frameLength = count
                let target = UnsafeMutableAudioBufferListPointer(copy.mutableAudioBufferList)
                for (src, dst) in zip(list, target) {
                    guard let destination = dst.mData else { continue }
                    if let data = src.mData { memcpy(destination, data, min(Int(src.mDataByteSize), Int(dst.mDataByteSize))) }
                    else { memset(destination, 0, Int(dst.mDataByteSize)) } // Real digital silence, not missing callbacks.
                }
                let stamp = inputTime.pointee
                let time = stamp.mFlags.contains(.hostTimeValid) ? AVAudioTime.seconds(forHostTime: stamp.mHostTime) : ProcessInfo.processInfo.systemUptime
                inbox.offer(CapturePacket(source: .reference, generation: generation, buffer: copy, timestamp: time))
            }, "Could not set up reference callback")
            try CoreAudioDevices.check(AudioDeviceStart(aggregate, ioProc), "Could not activate echo reference")
        } catch { stop(); throw error }
    }
    func stop() {
        if aggregate != 0 {
            if let ioProc { AudioDeviceStop(aggregate, ioProc); AudioDeviceDestroyIOProcID(aggregate, ioProc) }
            AudioHardwareDestroyAggregateDevice(aggregate)
        }
        ioProc = nil; aggregate = 0
        if tap != 0 { AudioHardwareDestroyProcessTap(tap); tap = 0 }
    }
    deinit { stop() }
}

/// Potentially blocking driver work must never occupy the main actor. A caller may
/// abandon this continuation at a deadline; generation checks reject its late result.
func performAudioHardwareWork<T>(on queue: DispatchQueue, _ work: @escaping () throws -> T) async throws -> T {
    try await withCheckedThrowingContinuation { continuation in
        queue.async { continuation.resume(with: Result { try work() }) }
    }
}

/// Selectable fallback: `MEETINGNOTES_MIC_BACKEND=avengine` restores the AVAudioEngine input node.
enum MicrophoneBackend {
    case hal, avengine
    static var current: MicrophoneBackend { ProcessInfo.processInfo.environment["MEETINGNOTES_MIC_BACKEND"] == "avengine" ? .avengine : .hal }
}

/// Input-only AUHAL (no AVAudioEngine default-device aggregate, no Bluetooth format negotiation).
/// All methods run on the attempt queue passed to `start`.
struct HALMicrophoneSource: @unchecked Sendable {
    let capture: MNHALMicrophone
    let device: MicrophoneDevice
    func stop() { capture.stop() }
    @discardableResult func restartIfStopped() throws -> Bool {
        var restarted: ObjCBool = false
        try capture.restartIfStopped(&restarted); return restarted.boolValue
    }
    static func start(device: MicrophoneDevice, queue: DispatchQueue, inbox: CaptureInbox, generation: UInt64,
                      failed: @escaping @Sendable (Error) -> Void) throws -> HALMicrophoneSource {
        guard inbox.isActive(.microphone, generation: generation) else { throw CancellationError() }
        let capture = MNHALMicrophone(device: device.objectID)
        capture.failureHandler = { failed($0) }
        try capture.start(with: queue) { buffer, seconds in
            inbox.offer(CapturePacket(source: .microphone, generation: generation, buffer: buffer, timestamp: seconds))
        }
        return HALMicrophoneSource(capture: capture, device: device)
    }
}

struct PreparedMicrophone: @unchecked Sendable {
    let capture: MNSafeMicrophone
    var engine: AVAudioEngine { capture.engine }
    let device: MicrophoneDevice
    let fallback: Bool

    func stop() { capture.stop() }
    /// Hardware queue only. Returns whether a stopped engine was restarted.
    @discardableResult func restartIfStopped() throws -> Bool {
        var restarted: ObjCBool = false
        try capture.restartIfStopped(&restarted); return restarted.boolValue
    }

    static func start(uid: String, onAudio: @escaping AVAudioNodeTapBlock) throws -> PreparedMicrophone {
        let resolved = try CoreAudioDevices.resolve(uid: uid)
        return try start(device: resolved.device, fallback: resolved.fallback, onAudio: onAudio)
    }
    static func start(device: MicrophoneDevice, fallback: Bool, onAudio: @escaping AVAudioNodeTapBlock) throws -> PreparedMicrophone {
        let capture = MNSafeMicrophone(engine: AVAudioEngine())
        try capture.start(withDevice: device.objectID, block: onAudio)
        return PreparedMicrophone(capture: capture, device: device, fallback: fallback)
    }

    static func start(device: MicrophoneDevice, inbox: CaptureInbox, generation: UInt64) throws -> PreparedMicrophone {
        guard inbox.isActive(.microphone, generation: generation) else { throw CancellationError() }
        return try start(device: device, fallback: false) { buffer, time in
            guard let owned = AudioCapture.copy(buffer) else { return }
            let timestamp = time.isHostTimeValid ? AVAudioTime.seconds(forHostTime: time.hostTime) : ProcessInfo.processInfo.systemUptime
            inbox.offer(CapturePacket(source: .microphone, generation: generation, buffer: owned, timestamp: timestamp))
        }
    }
}
