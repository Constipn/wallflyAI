import AVFoundation
import ScreenCaptureKit
import CoreAudio
import SafeAudio
import os

/// One microphone start with its own hardware queue, so a driver call that never returns
/// blocks only this attempt (an orphan) and never the next one.
private final class MicrophoneAttempt {
    enum Hardware {
        case none, hal(HALMicrophoneSource), engine(PreparedMicrophone)
        func stop() {
            switch self { case .none: break; case .hal(let source): source.stop(); case .engine(let prepared): prepared.stop() }
        }
        func restartIfStopped() throws -> Bool {
            switch self { case .none: false; case .hal(let source): try source.restartIfStopped(); case .engine(let prepared): try prepared.restartIfStopped() }
        }
    }
    let generation: UInt64
    let device: MicrophoneDevice
    let queue: DispatchQueue
    var hardware = Hardware.none
    var observer: NSObjectProtocol?
    /// Hardware failure reported while the attempt still waits for its first buffer.
    var failure: Error?
    var released = false
    init(generation: UInt64, device: MicrophoneDevice) {
        self.generation = generation; self.device = device
        queue = DispatchQueue(label: "meetingnotes.microphone-attempt-\(generation)", qos: .userInitiated)
    }
}

private enum MicrophoneOutcome { case acquired, denied, blocked, failed }

/// Hardware lifecycle is main-actor owned. All DSP and downstream audio delivery run on
/// one serial queue. Each source has its own generation and cancellable recovery task.
/// Only the "others" track can fail a recording; the microphone (and the Teams-mode echo
/// reference) degrade instead: the recording keeps the system audio.
@MainActor
final class AudioCapture {
    private let onAudio: @Sendable (Speaker, AVAudioPCMBuffer, TimeInterval) -> Void
    private let onMeter: @Sendable (Speaker, Double) -> Void
    private let onError: @Sendable (String) -> Void
    private let onEvent: @Sendable (CaptureEvent) -> Void
    private let improved: Bool
    private let sourceOperations: CaptureSourceOperations?
    private let checkMeetingSource: ((String) async throws -> Void)?
    private let retryDelays: [Double]
    private let retryTimeout: Double
    private let microphonePolicy: MicrophoneRecoveryPolicy
    private let backend = MicrophoneBackend.current
    private var legacy: LegacyAudioCapture?
    private let inbox = CaptureInbox()
    private let referenceHardware = DispatchQueue(label: "meetingnotes.reference-hardware", qos: .userInitiated)
    private let dsp = DispatchQueue(label: "meetingnotes.echo-processing", qos: .userInitiated)
    private var processor: EchoAudioProcessor?
    private var pump: DispatchSourceTimer?
    private var watchdog: Task<Void, Never>?
    private var microphone: MicrophoneAttempt?
    private var attemptQueues: [UInt64: DispatchQueue] = [:]
    private var orphanedAttempts = 0
    /// Queues of orphaned attempts: abandoned by design, so `stop` does not wait for them.
    private var orphanedGenerations = Set<UInt64>()
    private var deviceChange: Task<Void, Never>?
    /// The initial attempts of an acquire cycle are running; a device change does not restart them.
    private var microphoneAttempting = false
    private var microphoneDenied = false
    private var microphoneConfigurationCheck: Task<Void, Never>?
    private var deviceObservation: AudioDeviceObservation?
    private var reference: PlaybackReferenceTap?
    private var stream: SCStream?
    private var streamStop: Task<Void, Never>?
    private var streamSink: MeetingStreamSink?
    private var initialMeetingFilter: SCContentFilter?
    private var generations: [CaptureSource: UInt64] = [:]
    private var lastGeneration: UInt64 = 0
    private var recoveries: [CaptureSource: Task<Void, Never>] = [:]
    private var recoveryStarts: [CaptureSource: Double] = [:]
    private var recoveryReasons: [CaptureSource: String] = [:]
    private var pendingChanges = Set<CaptureSource>()
    private var debounce: Task<Void, Never>?
    private(set) var state: CaptureState = .starting
    private var sourceStates: [CaptureSource: CaptureState] = [:]
    private(set) var degradedReasons: [CaptureSource: String] = [:]
    private var stopped = false
    private var started = false
    private var launching = false
    private var initialFailure: String?
    private var bundleID = ""
    private var selectedUID = ""
    private var origin: Double = 0
    private var deviceChanges = 0, recoveryAttempts = 0
    private static let logger = Logger(subsystem: "io.github.constipn.wallfly", category: "audio-lifecycle")

    init(onAudio: @escaping @Sendable (Speaker, AVAudioPCMBuffer, TimeInterval) -> Void,
         onMeter: @escaping @Sendable (Speaker, Double) -> Void,
         onError: @escaping @Sendable (String) -> Void,
         onEvent: @escaping @Sendable (CaptureEvent) -> Void = { _ in }, improved: Bool = CapturePath.improved,
         sourceOperations: CaptureSourceOperations? = nil,
         checkMeetingSource: ((String) async throws -> Void)? = nil,
         retryDelays: [Double] = CaptureRecoveryPolicy.delays,
         retryTimeout: Double = CaptureRecoveryPolicy.timeout,
         microphonePolicy: MicrophoneRecoveryPolicy = .standard) {
        self.onAudio = onAudio; self.onMeter = onMeter; self.onError = onError; self.onEvent = onEvent; self.improved = improved
        self.sourceOperations = sourceOperations; self.retryDelays = retryDelays; self.retryTimeout = retryTimeout
        self.checkMeetingSource = checkMeetingSource; self.microphonePolicy = microphonePolicy
        if !improved { legacy = LegacyAudioCapture(onAudio: onAudio, onMeter: onMeter, onError: onError) }
    }
    static func requestMicrophone() async throws { try await LegacyAudioCapture.requestMicrophone() }
    static func authorizeSystemAudio() throws { try LegacyAudioCapture.authorizeSystemAudio() }
    static func sources() async throws -> [AudioSource] { try await LegacyAudioCapture.sources() }
    nonisolated static func copy(_ buffer: AVAudioPCMBuffer) -> AVAudioPCMBuffer? { LegacyAudioCapture.copy(buffer) }
    nonisolated static func level(_ buffer: AVAudioPCMBuffer) -> Double { LegacyAudioCapture.level(buffer) }
    func state(of source: CaptureSource) -> CaptureState? { sourceStates[source] }
    private var now: Double { ProcessInfo.processInfo.systemUptime }

    func start(bundleID: String, microphoneUID: String = "", onClockStart: (@MainActor @Sendable (Double) -> Void)? = nil) async throws {
        guard !started, !stopped else { throw CancellationError() }
        started = true; self.bundleID = bundleID; selectedUID = microphoneUID
        if let legacy {
            try await legacy.start(bundleID: bundleID, microphoneUID: microphoneUID, onClockStart: onClockStart)
            if let resolved = try? CoreAudioDevices.resolve(uid: microphoneUID) { onEvent(.microphone(name: resolved.device.name, fallback: resolved.fallback)) }
            return
        }
        if sourceOperations == nil { try Self.authorizeSystemAudio() }
        if !bundleID.isEmpty, sourceOperations == nil || checkMeetingSource != nil {
            // Resolve app availability before the clock or any audio input starts.
            // Only this failure can trigger automatic routing's system fallback.
            var sourceError: Error?
            let completed = await BoundedWork.run(for: .seconds(retryTimeout)) {
                do {
                    if let checkMeetingSource = self.checkMeetingSource { try await checkMeetingSource(bundleID) }
                    else {
                        try MeetingAudioSource.requireRunning(bundleID: bundleID)
                        let filter = try await self.meetingFilter(bundleID: bundleID)
                        try Task.checkCancellation()
                        guard !self.stopped else { throw CancellationError() }
                        self.initialMeetingFilter = filter
                    }
                } catch { sourceError = error }
            }
            guard completed else { throw MeetingError.message("Timed out while checking the meeting audio source.") }
            if let sourceError { throw sourceError }
            try Task.checkCancellation()
            guard !stopped else { throw CancellationError() }
        }
        setState(.starting); launching = true
        origin = now
        onClockStart?(origin) // Exactly once, including across device changes/recovery.
        let audio = onAudio, meter = onMeter, event = onEvent
        var lastMeter: [Speaker: Double] = [:]
        do {
            processor = try EchoAudioProcessor(globalIsMeeting: bundleID.isEmpty, onAudio: { speaker, buffer, offset in
                audio(speaker, buffer, offset)
                let now = ProcessInfo.processInfo.systemUptime
                if now - (lastMeter[speaker] ?? 0) >= 0.08 { lastMeter[speaker] = now; meter(speaker, Self.level(buffer)) }
            }, onGap: { event(.gap($0)) })
            startPump()
            for source in requiredSources {
                if source == .microphone { await launchMicrophone(); guard !stopped else { throw CancellationError() }; continue }
                let generation = nextGeneration(source)
                var startupError: Error?
                let completed = await BoundedWork.run(for: .seconds(retryTimeout)) {
                    do { try await self.startReadySource(source, generation: generation) } catch { startupError = error }
                }
                guard !stopped else { throw CancellationError() }
                if let initialFailure { throw MeetingError.message(initialFailure) }
                if let failure = completed ? startupError : MeetingError.message("Timed out while starting the audio source (\(source.displayName)).") {
                    // Only the others track fails the recording. The Teams-mode echo reference
                    // degrades: the microphone then passes through the canceller unfiltered.
                    guard source != othersSource, !(failure is CancellationError) else { throw failure }
                    Self.logger.notice("Audio source \(source.rawValue, privacy: .public) unavailable at start: \(failure.localizedDescription, privacy: .public)")
                    stopSource(source)
                    superviseDegraded(source, reason: "Echo reference unavailable", gapStart: 0, probe: !MicrophonePolicy.permissionDenied(failure))
                    continue
                }
                guard valid(source, generation) else { throw CancellationError() }
                sourceStates[source] = .running; onEvent(.source(source, .running))
            }
            guard !stopped else { throw CancellationError() }
            launching = false
            observeDevices(); startWatchdog(); updateState()
        } catch {
            launching = false
            if !stopped { setState(.failed) }
            await stop()
            throw CaptureFailure.describe(error, operation: "Could not start audio recording")
        }
    }
    // Establish our Core Audio process with microphone I/O before resolving its process
    // object for exclusion from the tap. DSP still waits for and analyzes render first.
    private var requiredSources: [CaptureSource] { bundleID.isEmpty ? [.microphone, .reference] : [.microphone, .reference, .meeting] }
    /// The track whose loss ends the recording.
    private var othersSource: CaptureSource { bundleID.isEmpty ? .reference : .meeting }
    private func allocateGeneration() -> UInt64 { lastGeneration += 1; return lastGeneration }
    private func nextGeneration(_ source: CaptureSource) -> UInt64 {
        let next = allocateGeneration()
        generations[source] = next; inbox.activate(source, generation: next); return next
    }
    private func valid(_ source: CaptureSource, _ generation: UInt64) -> Bool { !stopped && generations[source] == generation }
    private func setState(_ state: CaptureState) { self.state = state; onEvent(.state(state)) }
    /// recovering > degraded > running; `failed` and `stopping` are final.
    private func updateState() {
        guard !stopped, !launching, state != .failed else { return }
        let states = requiredSources.compactMap { sourceStates[$0] }
        let next: CaptureState = states.contains(.recovering) || !pendingChanges.isEmpty ? .recovering
            : states.contains(.degraded) ? .degraded : .running
        if next != state { setState(next) }
    }
    private func markDegraded(_ source: CaptureSource, reason: String) {
        degradedReasons[source] = reason
        guard sourceStates[source] != .degraded else { return }
        Self.logger.notice("Audio source \(source.rawValue, privacy: .public) degraded: \(reason, privacy: .public)")
        sourceStates[source] = .degraded; onEvent(.source(source, .degraded)); updateState()
    }
    private func sourceRecovered(_ source: CaptureSource, gapStart: Double, reason: String) {
        sourceStates[source] = .running; degradedReasons[source] = nil; onEvent(.source(source, .running))
        onEvent(.gap(RecordingGap(start: gapStart, duration: max(0, now - origin - gapStart), source: source, reason: reason)))
        recoveries[source] = nil; recoveryStarts[source] = nil; recoveryReasons[source] = nil
        observeDevices(); updateState()
    }

    // MARK: Microphone
    // The pinned device, otherwise the system default (a failing pinned device falls
    // back to the default); up to 3 attempts 500ms apart; then degraded with up to 5 background retries
    // 1s, 2s, 4s … (≤30s). A device change starts a new cycle; switching is break-before-make.

    private func microphoneInventory() -> MicrophoneInventory {
        if let microphones = sourceOperations?.microphones { return microphones() }
        if sourceOperations != nil {
            let device = MicrophoneDevice(id: selectedUID.isEmpty ? "default" : selectedUID, objectID: 0, name: "Microphone")
            return MicrophoneInventory(devices: [device], defaultUID: device.id)
        }
        return CoreAudioDevices.inventory()
    }
    private func launchMicrophone() async {
        sourceStates[.microphone] = .starting
        let outcome = await attemptMicrophone()
        guard !stopped else { return }
        switch outcome {
        case .acquired: sourceStates[.microphone] = .running; onEvent(.source(.microphone, .running))
        case .denied:
            microphoneDenied = true
            recoveryStarts[.microphone] = 0; recoveryReasons[.microphone] = "No microphone access"
            markDegraded(.microphone, reason: "No microphone access")
        case .blocked, .failed:
            // Attempts 2 and 3 run alongside the system audio start; the gap starts at zero.
            sourceStates[.microphone] = .recovering; onEvent(.source(.microphone, .recovering))
            superviseMicrophone(reason: "Microphone unavailable", gapStart: 0, attemptsDone: 1)
        }
    }
    /// One attempt: the pinned device and, if it fails, the system default.
    private func attemptMicrophone() async -> MicrophoneOutcome {
        var firstReason: String?
        for candidate in MicrophoneCandidates.order(microphoneInventory(), selectedUID: selectedUID) {
            guard !stopped, !Task.isCancelled else { return .failed }
            guard orphanedAttempts < microphonePolicy.maxOrphanedAttempts else { return .blocked }
            let device = candidate.device
            do {
                let attempt = try await startMicrophone(device, generation: nextGeneration(.microphone))
                let reason = candidate.reason ?? firstReason
                Self.logger.notice("Microphone \(device.name, privacy: .public) transport=\(device.transport) generation=\(attempt.generation) fallback=\(candidate.fallback)")
                onEvent(.microphone(name: device.name, fallback: candidate.fallback, reason: candidate.fallback ? reason : nil))
                return .acquired
            } catch is CancellationError { return .failed } catch {
                Self.logger.notice("Microphone \(device.name, privacy: .public) transport=\(device.transport) failed \(MicrophonePolicy.describe(error), privacy: .public)")
                if MicrophonePolicy.permissionDenied(error) { return .denied }
                if firstReason == nil { firstReason = MicrophonePolicy.reason(error, device: device) }
            }
        }
        return .failed
    }
    /// The rest of an acquire cycle, then the bounded background re-acquire. The gap opened at
    /// `gapStart` stays open until a microphone delivers audio again.
    private func superviseMicrophone(reason: String, gapStart: Double, attemptsDone: Int = 0) {
        recoveries.removeValue(forKey: .microphone)?.cancel()
        recoveryStarts[.microphone] = gapStart; recoveryReasons[.microphone] = reason
        microphoneAttempting = true
        recoveries[.microphone] = Task { [weak self] in
            guard let self else { return }
            let policy = self.microphonePolicy
            var outcome = MicrophoneOutcome.failed
            for attempt in attemptsDone..<max(attemptsDone, policy.attempts) {
                if attempt > 0 { do { try await Task.sleep(for: .seconds(policy.attemptDelay)) } catch { return } }
                guard !self.stopped, !Task.isCancelled else { return }
                self.recoveryAttempts += 1
                outcome = await self.attemptMicrophone()
                guard !self.stopped, !Task.isCancelled else { return }
                if outcome != .failed { break }
            }
            self.microphoneAttempting = false
            var retry = 0
            while true {
                switch outcome {
                case .acquired: self.sourceRecovered(.microphone, gapStart: gapStart, reason: reason); return
                case .denied:
                    self.microphoneDenied = true; self.recoveries[.microphone] = nil
                    self.markDegraded(.microphone, reason: "No microphone access"); return
                case .blocked: self.markDegraded(.microphone, reason: "Audio driver blocked")
                case .failed: self.markDegraded(.microphone, reason: reason)
                }
                // Bounded by the background retry count; afterwards only a device change retries.
                guard retry < policy.backgroundRetries else { self.recoveries[.microphone] = nil; return }
                do { try await Task.sleep(for: .seconds(policy.backgroundRetryDelay(retry))) } catch { return }
                retry += 1
                guard !self.stopped, !Task.isCancelled else { return }
                self.recoveryAttempts += 1
                outcome = await self.attemptMicrophone()
                guard !self.stopped, !Task.isCancelled else { return }
            }
        }
    }
    /// Break-before-make: stops the current microphone (if any) and starts a fresh acquire cycle.
    private func restartMicrophoneCycle(reason: String) {
        guard !stopped, !launching, !microphoneDenied else { return }
        if sourceStates[.microphone] == .running { recover(.microphone, reason: reason); return }
        let gapStart = recoveryStarts[.microphone] ?? max(0, now - origin)
        let gapReason = recoveryReasons[.microphone] ?? reason
        recoveries.removeValue(forKey: .microphone)?.cancel()
        // A pending start may still return the previously selected device.
        // Invalidate it now so the new cycle owns the next attempt.
        stopSource(.microphone)
        superviseMicrophone(reason: gapReason, gapStart: gapStart)
    }
    private func startMicrophone(_ device: MicrophoneDevice, generation: UInt64) async throws -> MicrophoneAttempt {
        guard orphanedAttempts < microphonePolicy.maxOrphanedAttempts else { throw MicrophoneDriverBlockedError() }
        if let microphone { releaseAttempt(microphone) }
        let attempt = MicrophoneAttempt(generation: generation, device: device)
        microphone = attempt
        attemptQueues[generation] = attempt.queue
        do {
            try await launch(attempt)
            try await waitForFirstAudio(attempt)
            return attempt
        } catch { releaseAttempt(attempt); throw error }
    }
    private func attemptValid(_ attempt: MicrophoneAttempt) -> Bool { !stopped && !attempt.released && microphone === attempt }
    private func launch(_ attempt: MicrophoneAttempt) async throws {
        let generation = attempt.generation, device = attempt.device, inbox = inbox
        if let sourceOperations {
            let start = sourceOperations.startMicrophone
            _ = try await boundedAttemptWork(attempt, timeout: retryTimeout, work: { () async throws -> MicrophoneAttempt.Hardware in
                if let start { try await start(device, generation, inbox) } else { try await sourceOperations.start(.microphone, generation, inbox) }
                return .none
            }, discard: { _ in sourceOperations.stop(.microphone) })
            guard attemptValid(attempt) else { throw CancellationError() }
            return
        }
        let queue = attempt.queue, backend = backend
        let failed: @Sendable (Error) -> Void = { [weak self] error in
            Task { @MainActor in self?.microphoneHardwareFailed(generation: generation, error: error) }
        }
        let hardware = try await boundedAttemptWork(attempt, timeout: retryTimeout, work: {
            try await performAudioHardwareWork(on: queue) { () throws -> MicrophoneAttempt.Hardware in
                guard inbox.isActive(.microphone, generation: generation) else { throw CancellationError() }
                switch backend {
                case .hal: return .hal(try HALMicrophoneSource.start(device: device, queue: queue, inbox: inbox, generation: generation, failed: failed))
                case .avengine: return .engine(try PreparedMicrophone.start(device: device, inbox: inbox, generation: generation))
                }
            }
        }, discard: { hardware in queue.async { hardware.stop() } })
        guard attemptValid(attempt) else { queue.async { hardware.stop() }; throw CancellationError() }
        attempt.hardware = hardware
        if case .engine(let prepared) = hardware {
            let engine = prepared.engine
            attempt.observer = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange, object: engine, queue: nil) { [weak self] _ in
                Task { @MainActor in
                    guard let self, self.valid(.microphone, generation) else { return }
                    self.checkMicrophoneConfiguration(engine: engine, generation: generation)
                }
            }
        }
    }
    /// A step that misses its deadline stays counted as orphaned until the driver returns;
    /// its late result is then discarded on the attempt's own queue.
    private func boundedAttemptWork<T>(_ attempt: MicrophoneAttempt, timeout: Double, work: @escaping @MainActor () async throws -> T,
                                       discard: @escaping (T) -> Void) async throws -> T {
        var outcome: Result<T, Error>?
        var orphaned = false
        let completed = await BoundedWork.run(for: .seconds(timeout)) {
            let result: Result<T, Error>
            do { result = .success(try await work()) } catch { result = .failure(error) }
            if orphaned {
                self.orphanedAttempts -= 1; self.orphanedGenerations.remove(attempt.generation)
                Self.logger.notice("Orphaned microphone attempt returned generation=\(attempt.generation) orphans=\(self.orphanedAttempts)")
                if case .success(let value) = result { discard(value) }
            } else { outcome = result }
        }
        guard completed, let outcome else {
            if case .success(let value)? = outcome { discard(value) } // Returned right after the deadline.
            else if outcome == nil {
                orphaned = true; orphanedAttempts += 1; orphanedGenerations.insert(attempt.generation)
                Self.logger.notice("Microphone attempt orphaned generation=\(attempt.generation) orphans=\(self.orphanedAttempts)")
            }
            throw MicrophoneStartTimeoutError()
        }
        return try outcome.get()
    }
    private func waitForFirstAudio(_ attempt: MicrophoneAttempt) async throws {
        // Engine/AUHAL start success does not prove that a driver delivers audio.
        guard sourceOperations?.requiresInitialAudio ?? true else { return }
        let begin = now, generation = attempt.generation
        var resumes = 0
        while !inbox.hasAudio(.microphone, generation: generation) {
            try Task.checkCancellation()
            guard attemptValid(attempt) else { throw CancellationError() }
            if let failure = attempt.failure { throw failure }
            if now - begin >= microphonePolicy.firstBufferTimeout { throw MicrophoneNoAudioError() }
            try await Task.sleep(for: .milliseconds(10))
            // A configuration change can stop the device before its first buffer.
            if resumes < 3, now - begin >= Double(resumes + 1) * 0.25 {
                resumes += 1
                // A failed restart must not abort the attempt; later restarts and the deadline still apply.
                do { try await resumeStalled(attempt) }
                catch is CancellationError { throw CancellationError() }
                catch { Self.logger.notice("Restart of stalled microphone failed attempt=\(resumes): \(error.localizedDescription, privacy: .public)") }
            }
        }
    }
    private func resumeStalled(_ attempt: MicrophoneAttempt) async throws {
        guard attemptValid(attempt) else { throw CancellationError() }
        if let sourceOperations { try await sourceOperations.resume?(.microphone); return }
        let hardware = attempt.hardware, queue = attempt.queue
        let restarted = try await boundedAttemptWork(attempt, timeout: retryTimeout, work: {
            try await performAudioHardwareWork(on: queue) { try hardware.restartIfStopped() }
        }, discard: { _ in })
        guard attemptValid(attempt) else { throw CancellationError() }
        if restarted { Self.logger.notice("Microphone stopped before first audio; restarted generation=\(attempt.generation)") }
    }
    private func releaseAttempt(_ attempt: MicrophoneAttempt) {
        guard !attempt.released else { return }
        attempt.released = true
        if microphone === attempt {
            microphone = nil
            if generations[.microphone] == attempt.generation { inbox.invalidate(.microphone) }
        }
        if let observer = attempt.observer { NotificationCenter.default.removeObserver(observer); attempt.observer = nil }
        let generation = attempt.generation
        if let sourceOperations { sourceOperations.stop(.microphone); attemptQueues[generation] = nil; return }
        let hardware = attempt.hardware
        attempt.hardware = .none
        attempt.queue.async { [weak self] in
            hardware.stop()
            Task { @MainActor in self?.attemptQueues[generation] = nil }
        }
    }
    private func microphoneHardwareFailed(generation: UInt64, error: Error) {
        guard !stopped else { return }
        Self.logger.notice("Microphone hardware failure generation=\(generation) \(MicrophonePolicy.describe(error), privacy: .public)")
        guard let microphone, microphone.generation == generation else { return }
        if sourceStates[.microphone] == .running && !launching { recover(.microphone, reason: "Microphone failed") }
        else { microphone.failure = error }
    }
    /// Device list, default input or device liveness changed (debounced): a new acquire cycle,
    /// unless the microphone runs fine on the device that would be chosen now.
    func microphoneDevicesChanged() {
        guard !stopped, !launching, legacy == nil, !microphoneDenied else { return }
        deviceChange?.cancel()
        deviceChange = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(CaptureRecoveryPolicy.debounce)) } catch { return }
            guard let self, !self.stopped, !Task.isCancelled else { return }
            self.deviceChange = nil
            self.deviceChanges += 1
            if self.sourceStates[.microphone] == .running, let current = self.microphone {
                let inventory = self.microphoneInventory()
                let target = MicrophoneCandidates.order(inventory, selectedUID: self.selectedUID).first
                if target?.device.id == current.device.id, inventory.devices.contains(where: { $0.id == current.device.id }) { return }
                self.restartMicrophoneCycle(reason: "Audio device changed")
            } else if !self.microphoneAttempting {
                self.restartMicrophoneCycle(reason: "Microphone unavailable")
            }
        }
    }
    func selectMicrophone(uid: String) {
        selectedUID = uid
        if let legacy {
            legacy.selectMicrophone(uid: uid)
            if let resolved = try? CoreAudioDevices.resolve(uid: uid) { onEvent(.microphone(name: resolved.device.name, fallback: resolved.fallback)) }
            return
        }
        guard !stopped, !launching else { return } // A launch in progress reads the new selection.
        if sourceStates[.microphone] == .running, let current = microphone,
           let target = MicrophoneCandidates.order(microphoneInventory(), selectedUID: uid).first, target.device.id == current.device.id {
            onEvent(.microphone(name: current.device.name, fallback: target.fallback, reason: target.reason)); return
        }
        restartMicrophoneCycle(reason: "Microphone changed")
    }
    func checkMicrophoneConfiguration(engine: AVAudioEngine, generation: UInt64) {
        microphoneConfigurationCheck?.cancel()
        let queue = microphone?.queue ?? DispatchQueue.global(qos: .userInitiated)
        microphoneConfigurationCheck = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(CaptureRecoveryPolicy.debounce)) } catch { return }
            guard let self, self.valid(.microphone, generation), !Task.isCancelled else { return }
            let running = try? await performAudioHardwareWork(on: queue) { engine.isRunning }
            guard self.valid(.microphone, generation), !Task.isCancelled else { return }
            // A configuration notification by itself is normal during device startup.
            // Changed PCM formats are handled by the normalizer; a running engine's
            // missing callbacks are handled by readiness/the watchdog. Restart only
            // when the engine actually stopped, avoiding a repeated notification loop.
            if running == false { self.scheduleChange(.microphone) }
        }
    }

    // MARK: Reference and meeting stream

    private func startReadySource(_ source: CaptureSource, generation: UInt64) async throws {
        try await startSource(source, generation: generation)
        // AudioDeviceStart success does not prove that a driver delivers audio. Keep the
        // per-attempt deadline active until its first PCM callback, otherwise the watchdog can
        // recycle successful-but-silent starts forever. ScreenCaptureKit can legitimately omit
        // packets during silence.
        guard source != .meeting, sourceOperations?.requiresInitialAudio ?? true else { return }
        let begin = now
        var resumes = 0
        while !inbox.hasAudio(source, generation: generation) {
            try Task.checkCancellation()
            guard valid(source, generation) else { throw CancellationError() }
            try await Task.sleep(for: .milliseconds(10))
            if resumes < 3, now - begin >= Double(resumes + 1) * 0.25, let resume = sourceOperations?.resume {
                resumes += 1
                do { try await resume(source) }
                catch { Self.logger.notice("Restart of stalled \(source.rawValue, privacy: .public) failed attempt=\(resumes): \(error.localizedDescription, privacy: .public)") }
            }
        }
    }
    private func startSource(_ source: CaptureSource, generation: UInt64) async throws {
        guard valid(source, generation) else { throw CancellationError() }
        if let sourceOperations { try await sourceOperations.start(source, generation, inbox); return }
        switch source {
        case .microphone: throw CancellationError() // Started through acquireMicrophone.
        case .reference:
            let inbox = inbox
            let tap = try await performAudioHardwareWork(on: referenceHardware) {
                guard inbox.isActive(.reference, generation: generation) else { throw CancellationError() }
                let tap = PlaybackReferenceTap()
                try tap.start(inbox: inbox, generation: generation)
                return tap
            }
            guard valid(source, generation) else {
                referenceHardware.async { tap.stop() }
                throw CancellationError()
            }
            reference = tap
        case .meeting:
            if let streamStop { await streamStop.value; self.streamStop = nil }
            guard valid(source, generation) else { throw CancellationError() }
            let filter: SCContentFilter
            if let prepared = initialMeetingFilter {
                filter = prepared
                initialMeetingFilter = nil
            } else {
                filter = try await meetingFilter(bundleID: bundleID)
            }
            guard valid(source, generation) else { throw CancellationError() }
            let configuration = SCStreamConfiguration()
            configuration.capturesAudio = true; configuration.excludesCurrentProcessAudio = true
            configuration.sampleRate = 48_000; configuration.channelCount = 1
            configuration.width = 2; configuration.height = 2; configuration.showsCursor = false
            configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
            let sink = MeetingStreamSink(inbox: inbox, generation: generation) { [weak self] in
                Task { @MainActor in
                    guard let self, self.valid(.meeting, generation) else { return }
                    if self.state == .starting { self.initialFailure = "Meeting audio stream interrupted at start." }
                    else { self.recover(.meeting, reason: "Meeting audio stream interrupted") }
                }
            }
            let stream = SCStream(filter: filter, configuration: configuration, delegate: sink)
            try stream.addStreamOutput(sink, type: .audio, sampleHandlerQueue: DispatchQueue(label: "meetingnotes.meeting-input"))
            self.stream = stream; streamSink = sink
            try await stream.startCapture()
            guard valid(source, generation) else { try? await stream.stopCapture(); throw CancellationError() }
        }
    }

    private func meetingFilter(bundleID: String) async throws -> SCContentFilter {
        let content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: false)
        guard let display = content.displays.first else { throw MeetingError.message("No display available for meeting audio.") }
        let apps = content.applications.filter { $0.bundleIdentifier == bundleID }
        guard !apps.isEmpty else { throw MeetingAudioSource.unavailable(bundleID: bundleID) }
        return SCContentFilter(display: display, including: apps, exceptingWindows: [])
    }
    private func stopSource(_ source: CaptureSource, drain: Bool = false) {
        if !drain || source != .meeting { inbox.invalidate(source) }
        generations[source] = allocateGeneration()
        if source == .microphone {
            microphoneConfigurationCheck?.cancel(); microphoneConfigurationCheck = nil
            if let microphone { releaseAttempt(microphone) }
            return
        }
        if let sourceOperations { sourceOperations.stop(source); return }
        switch source {
        case .microphone: break
        case .reference:
            if let old = reference { referenceHardware.async { old.stop() } }
            reference = nil
        case .meeting:
            let old = stream; stream = nil; streamSink = nil
            if let old {
                let previous = streamStop, inbox = inbox
                streamStop = Task {
                    await previous?.value
                    try? await old.stopCapture()
                    if drain { inbox.invalidate(.meeting) }
                }
            }
        }
    }
    private func observeDevices() {
        guard sourceOperations == nil else { return }
        let observation = AudioDeviceObservation(queue: .main)
        deviceObservation = observation
        let system = AudioObjectID(kAudioObjectSystemObject)
        // No default-output observer: the global tap aggregate has no main sub-device, so the
        // echo reference is output-independent. Only its own format change rebuilds it.
        for selector in [kAudioHardwarePropertyDevices, kAudioHardwarePropertyDefaultInputDevice] {
            observation.observe(system, selector) { [weak self] in Task { @MainActor in self?.microphoneDevicesChanged() } }
        }
        // The AUHAL backend repairs format changes in place and reports device loss itself.
        if backend == .avengine, let mic = microphone?.device {
            for selector in [kAudioDevicePropertyNominalSampleRate, kAudioDevicePropertyDeviceIsAlive] {
                observation.observe(mic.objectID, selector) { [weak self] in Task { @MainActor in self?.scheduleChange(.microphone) } }
            }
        }
        if let reference {
            observation.observe(reference.objectID, kAudioTapPropertyFormat) { [weak self] in Task { @MainActor in self?.scheduleChange(.reference) } }
        }
    }
    func scheduleChange(_ source: CaptureSource) {
        guard !stopped, !launching, state == .running || state == .recovering || state == .degraded, recoveries[source] == nil else { return }
        if pendingChanges.insert(source).inserted {
            // A format-change notification invalidates the old interpretation immediately;
            // debounce only the expensive rebuild, never consume 200ms with a stale format.
            inbox.invalidate(source)
            if recoveryStarts[source] == nil {
                recoveryStarts[source] = max(0, (inbox.lastAudioEnd(source) ?? now) - origin)
            }
            sourceStates[source] = .recovering; onEvent(.source(source, .recovering)); updateState()
        }
        debounce?.cancel()
        debounce = Task { [weak self] in
            do { try await Task.sleep(for: .seconds(CaptureRecoveryPolicy.debounce)) } catch { return }
            guard let self, !self.stopped else { return }
            let changes = self.pendingChanges; self.pendingChanges.removeAll()
            self.deviceChanges += changes.count
            for source in changes { self.recover(source, reason: "Audio device or format changed") }
            self.updateState()
        }
    }
    func recover(_ source: CaptureSource, reason: String) {
        guard !stopped, !launching, state != .starting, recoveries[source] == nil else { return }
        let gapStart = recoveryStarts[source] ?? max(0, (inbox.lastAudioEnd(source) ?? now) - origin)
        recoveryStarts[source] = gapStart; recoveryReasons[source] = reason
        sourceStates[source] = .recovering; onEvent(.source(source, .recovering)); updateState()
        stopSource(source)
        resetDSP(source, at: gapStart, reason: reason)
        if source == .microphone { superviseMicrophone(reason: reason, gapStart: gapStart); return }
        recoveries[source] = Task { [weak self] in
            guard let self else { return }
            var failure: Error?
            for delay in self.retryDelays {
                do { try await Task.sleep(for: .seconds(delay)) } catch { return }
                guard !self.stopped, !Task.isCancelled else { return }
                failure = await self.restartSource(source)
                guard !self.stopped, !Task.isCancelled else { return }
                if failure == nil { self.sourceRecovered(source, gapStart: gapStart, reason: reason); return }
                // Permission denial is not a transient device fault.
                if let failure, MicrophonePolicy.permissionDenied(failure) { break }
            }
            guard !self.stopped else { return }
            if source != self.othersSource {
                self.recoveries[source] = nil
                self.superviseDegraded(source, reason: reason, gapStart: gapStart, probe: !(failure.map(MicrophonePolicy.permissionDenied) ?? false))
                return
            }
            self.onEvent(.gap(RecordingGap(start: gapStart, duration: max(0, self.now - self.origin - gapStart), source: source, reason: "Recovery failed")))
            self.recoveryStarts[source] = nil
            self.setState(.failed)
            self.onError("The audio source could not be recovered. Audio already received is being saved.")
            self.stopInput(discardPending: false)
        }
    }
    /// One bounded start of a reference/meeting source; nil on success.
    private func restartSource(_ source: CaptureSource) async -> Error? {
        recoveryAttempts += 1
        let generation = nextGeneration(source)
        var failure: Error?
        let completed = await BoundedWork.run(for: .seconds(retryTimeout)) {
            do { try await self.startReadySource(source, generation: generation) } catch { failure = error }
        }
        guard !stopped, !Task.isCancelled else { return CancellationError() }
        if completed && failure == nil && valid(source, generation) { return nil }
        stopSource(source)
        return failure ?? MeetingError.message("Timed out while starting the audio source (\(source.displayName)).")
    }
    /// Re-acquires a degraded non-microphone source (Teams-mode echo reference) on the microphone's
    /// background schedule (1s, 2s, 4s …, bounded); afterwards it stays degraded.
    private func superviseDegraded(_ source: CaptureSource, reason: String, gapStart: Double, probe: Bool) {
        recoveryStarts[source] = gapStart; recoveryReasons[source] = reason
        markDegraded(source, reason: reason)
        guard probe else { return }
        recoveries[source] = Task { [weak self] in
            guard let self else { return }
            for retry in 0..<self.microphonePolicy.backgroundRetries {
                do { try await Task.sleep(for: .seconds(self.microphonePolicy.backgroundRetryDelay(retry))) } catch { return }
                guard !self.stopped, !Task.isCancelled else { return }
                let failure = await self.restartSource(source)
                guard !self.stopped, !Task.isCancelled else { return }
                if failure == nil { self.sourceRecovered(source, gapStart: gapStart, reason: reason); return }
            }
            self.recoveries[source] = nil
        }
    }
    private func resetDSP(_ source: CaptureSource, at time: Double, reason: String) {
        let processor = processor, reportError = onError
        dsp.async {
            do { try processor?.reset(source, at: time, reason: reason) }
            catch { reportError("Audio cleanup could not be reset.") }
        }
    }
    private func startPump() {
        let timer = DispatchSource.makeTimerSource(queue: dsp)
        let processor = processor, inbox = inbox, origin = origin, event = onEvent, reportError = onError
        timer.schedule(deadline: .now(), repeating: .milliseconds(10))
        timer.setEventHandler {
            do {
                let (packets, gaps) = inbox.drain()
                for var gap in gaps {
                    gap.start = max(0, gap.start - origin); processor?.accountInputLoss(gap); event(.gap(gap))
                    // Lost microphone/reference input is a hole in the frame timeline; the echo filter
                    // and its render/capture alignment survive it (see EchoAudioProcessor.process).
                    if gap.source == .meeting { try processor?.reset(gap.source, at: gap.start, reason: gap.reason) }
                }
                let now = ProcessInfo.processInfo.systemUptime
                for packet in packets { try processor?.append(packet, origin: origin, now: now, processImmediately: false) }
                try processor?.process(now: now - origin)
            } catch { reportError("Echo cancellation failed. The recording is being stopped.") }
        }
        pump = timer; timer.resume()
    }
    func checkSourceHealth(now: Double) {
        for source in [CaptureSource.microphone, .reference] where sourceStates[source] == .running {
            if let arrival = inbox.lastArrival(source), now - arrival > CaptureRecoveryPolicy.callbackTimeout {
                recover(source, reason: "Audio callbacks stopped")
            }
        }
    }
    private func startWatchdog() {
        guard sourceOperations == nil else { return }
        watchdog = Task { [weak self] in
            var lastReport = ProcessInfo.processInfo.systemUptime
            while !Task.isCancelled {
                do { try await Task.sleep(for: .milliseconds(100)) } catch { return }
                guard let self, !self.stopped else { return }
                let now = ProcessInfo.processInfo.systemUptime
                self.checkSourceHealth(now: now)
                if now - lastReport >= 30 {
                    var hal = ""
                    if case .hal(let source)? = self.microphone?.hardware {
                        hal = " renderErrors=\(source.capture.renderErrors) overruns=\(source.capture.overruns)"
                    }
                    Self.logger.info("Audio 30s deviceChanges=\(self.deviceChanges) recoveryAttempts=\(self.recoveryAttempts) orphans=\(self.orphanedAttempts) microphone=\(self.sourceStates[.microphone]?.rawValue ?? "-", privacy: .public)\(hal, privacy: .public)")
                    self.deviceChanges = 0; self.recoveryAttempts = 0; lastReport = now
                }
            }
        }
    }
    func stopInput(discardPending: Bool = true) {
        legacy?.stopInput(discardPending: discardPending)
        guard !stopped else {
            if discardPending {
                for source in requiredSources { inbox.invalidate(source) }
                pump?.cancel(); pump = nil; _ = inbox.drain()
            }
            return
        }
        stopped = true
        if state != .failed { setState(.stopping) }
        debounce?.cancel(); debounce = nil; pendingChanges.removeAll()
        deviceChange?.cancel(); deviceChange = nil
        watchdog?.cancel(); watchdog = nil
        recoveries.values.forEach { $0.cancel() }; recoveries.removeAll()
        let end = max(0, now - origin)
        for (source, start) in recoveryStarts {
            onEvent(.gap(RecordingGap(start: start, duration: max(0, end - start), source: source, reason: "Recording ended during recovery")))
        }
        recoveryStarts.removeAll()
        deviceObservation = nil
        for source in requiredSources { stopSource(source, drain: !discardPending) }
        if discardPending { pump?.cancel(); pump = nil; _ = inbox.drain() }
    }
    func stop() async {
        if let legacy { await legacy.stop(); return }
        stopInput(discardPending: false)
        // Also bound cleanup after a failed initial start, which has no recording
        // finalization deadline yet. Driver operations can finish later, but generation
        // invalidation prevents them from publishing audio or reviving the capture.
        let streamStop = streamStop; self.streamStop = nil
        let queues = attemptQueues.filter { !orphanedGenerations.contains($0.key) }.map(\.value) + [referenceHardware]
        let stoppedHardware = await BoundedWork.run(for: .seconds(retryTimeout)) {
            for queue in queues { _ = try? await performAudioHardwareWork(on: queue) { () } }
            await streamStop?.value
        }
        if !stoppedHardware { onError("The audio driver is not responding while stopping. Audio already received is being saved.") }
        pump?.cancel(); pump = nil
        let processor = processor, inbox = inbox, origin = origin, reportError = onError, event = onEvent
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            dsp.async {
                defer { done.resume() }
                do {
                    let now = ProcessInfo.processInfo.systemUptime
                    let (packets, gaps) = inbox.drain()
                    for var gap in gaps { gap.start = max(0, gap.start - origin); processor?.accountInputLoss(gap); event(.gap(gap)) }
                    for packet in packets { try processor?.append(packet, origin: origin, now: now, processImmediately: false) }
                    try processor?.finish(now: now - origin)
                } catch { reportError("The last audio blocks could not be fully processed.") }
            }
        }
        self.processor = nil
    }
}

private final class MeetingStreamSink: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    let inbox: CaptureInbox
    let generation: UInt64
    let failed: @Sendable () -> Void
    init(inbox: CaptureInbox, generation: UInt64, failed: @escaping @Sendable () -> Void) {
        self.inbox = inbox; self.generation = generation; self.failed = failed
    }
    func stream(_ stream: SCStream, didStopWithError error: Error) { failed() }
    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        guard outputType == .audio, sampleBuffer.isValid, let description = sampleBuffer.formatDescription else { return }
        let format = AVAudioFormat(cmAudioFormatDescription: description)
        guard sampleBuffer.numSamples > 0, let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(sampleBuffer.numSamples)) else { return }
        buffer.frameLength = buffer.frameCapacity
        guard CMSampleBufferCopyPCMDataIntoAudioBufferList(sampleBuffer, at: 0, frameCount: Int32(sampleBuffer.numSamples), into: buffer.mutableAudioBufferList) == noErr else { failed(); return }
        let timestamp = sampleBuffer.presentationTimeStamp.seconds
        guard timestamp.isFinite else { failed(); return }
        inbox.offer(CapturePacket(source: .meeting, generation: generation, buffer: buffer, timestamp: timestamp))
    }
}
