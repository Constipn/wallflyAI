import AVFoundation
import ScreenCaptureKit
import CoreMedia
import CoreAudio

final class LegacyAudioCapture: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    // Microphone and observer are confined to hardware; audio state belongs to queue.
    private let hardware = DispatchQueue(label: "meetingnotes.legacy-microphone", qos: .userInitiated)
    private var microphone: PreparedMicrophone?
    private var engineObserver: NSObjectProtocol?
    @MainActor private var stream: SCStream?
    private var microphoneGeneration = 0 // confined to queue
    private var lastMicrophoneArrival: Double = 0
    private var watchdog: DispatchSourceTimer?
    private var reportedMicrophoneFailure = false
    private var microphoneInputStarted = false
    private var selectedMicrophoneUID = ""
    private var microphoneReady = false
    private var microphoneBuffers = 0
    private var recoveringMicrophone = false
    private var recoveryAttempt = 0
    private var configurationRevision = 0
    private var configurationCheck: DispatchWorkItem?
    private var recoveryWork: DispatchWorkItem?
    private var recoveryDeadline: DispatchWorkItem?
    private let microphoneStart: (String, @escaping AVAudioNodeTapBlock) throws -> PreparedMicrophone
    private let microphoneTimeout: Double
    private let microphoneNow: @Sendable () -> Double
    private let configurationDelay: Double
    private let recoveryDelays: [Double]
    private let queue = DispatchQueue(label: "meetingnotes.capture", qos: .userInitiated)
    private let onAudio: @Sendable (Speaker, AVAudioPCMBuffer, TimeInterval) -> Void
    private let onMeter: @Sendable (Speaker, Double) -> Void
    private let onError: @Sendable (String) -> Void
    private var origin: Double = 0
    private var lastMeter: [Speaker: Double] = [:]
    private var accepting = false
    private var stopRequested = false // confined to queue

    init(onAudio: @escaping @Sendable (Speaker, AVAudioPCMBuffer, TimeInterval) -> Void,
         onMeter: @escaping @Sendable (Speaker, Double) -> Void,
         onError: @escaping @Sendable (String) -> Void,
         microphoneTimeout: Double = 5,
         microphoneNow: @escaping @Sendable () -> Double = { ProcessInfo.processInfo.systemUptime },
         configurationDelay: Double = 0.2,
         recoveryDelays: [Double] = [0.5, 1, 2],
         microphoneStart: @escaping (String, @escaping AVAudioNodeTapBlock) throws -> PreparedMicrophone = { uid, callback in
             try PreparedMicrophone.start(uid: uid, onAudio: callback)
         }) {
        self.onAudio = onAudio
        self.onMeter = onMeter
        self.onError = onError
        self.microphoneStart = microphoneStart
        self.microphoneTimeout = microphoneTimeout
        self.microphoneNow = microphoneNow
        self.configurationDelay = configurationDelay
        self.recoveryDelays = recoveryDelays
    }

    static func requestMicrophone() async throws {
        let allowed = await AVCaptureDevice.requestAccess(for: .audio)
        guard allowed else { throw MeetingError.message("Allow Wallfly to use the microphone under System Settings → Privacy & Security → Microphone.") }
    }

    static func authorizeSystemAudio() throws { try ScreenCaptureAuthorization.shared.ensureAccess() }

    static func sources() async throws -> [AudioSource] {
        try authorizeSystemAudio()
        let content: SCShareableContent
        do { content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: false) }
        catch { throw CaptureFailure.describe(error, operation: "Could not list audio sources") }
        var seen = Set<String>()
        return content.applications.compactMap { app in
            guard app.bundleIdentifier != Bundle.main.bundleIdentifier,
                  !app.bundleIdentifier.isEmpty,
                  seen.insert(app.bundleIdentifier).inserted else { return nil }
            return AudioSource(id: app.bundleIdentifier, name: app.applicationName)
        }.sorted { $0.name.localizedCaseInsensitiveCompare($1.name) == .orderedAscending }
    }

    @MainActor func start(bundleID: String, microphoneUID: String = "", onClockStart: (@MainActor @Sendable (TimeInterval) -> Void)? = nil) async throws {
        try Self.authorizeSystemAudio()
        let content: SCShareableContent
        do { content = try await SCShareableContent.excludingDesktopWindows(true, onScreenWindowsOnly: false) }
        catch {
            throw CaptureFailure.describe(error, operation: "Could not determine system audio sources")
        }
        guard queue.sync(execute: { !stopRequested }) else { throw CancellationError() }
        guard let display = content.displays.first else { throw MeetingError.message("No display is available for system audio recording.") }
        let filter: SCContentFilter
        if bundleID.isEmpty {
            let own = content.applications.filter { $0.bundleIdentifier == Bundle.main.bundleIdentifier }
            filter = SCContentFilter(display: display, excludingApplications: own, exceptingWindows: [])
        } else {
            let apps = content.applications.filter { $0.bundleIdentifier == bundleID }
            guard !apps.isEmpty else { throw MeetingAudioSource.unavailable(bundleID: bundleID) }
            filter = SCContentFilter(display: display, including: apps, exceptingWindows: [])
        }
        let configuration = SCStreamConfiguration()
        configuration.capturesAudio = true
        configuration.excludesCurrentProcessAudio = true
        configuration.sampleRate = 48000
        configuration.channelCount = 1
        configuration.width = 2
        configuration.height = 2
        configuration.minimumFrameInterval = CMTime(value: 1, timescale: 1)
        configuration.showsCursor = false
        let stream = SCStream(filter: filter, configuration: configuration, delegate: self)
        // Register audio only: no screen frames are consumed or saved.
        try stream.addStreamOutput(self, type: .audio, sampleHandlerQueue: queue)
        self.stream = stream
        let origin = ProcessInfo.processInfo.systemUptime
        queue.sync { self.origin = origin }
        onClockStart?(origin)
        do {
            guard queue.sync(execute: { !stopRequested }) else { throw CancellationError() }
            try await stream.startCapture()
            guard queue.sync(execute: { !stopRequested }) else {
                try? await stream.stopCapture()
                throw CancellationError()
            }
            try await startMicrophoneInput(uid: microphoneUID)
            guard queue.sync(execute: { !stopRequested }) else { throw CancellationError() }
        } catch {
            await stop()
            throw CaptureFailure.describe(error, operation: "Could not start audio recording")
        }
    }

    // Independent from ScreenCaptureKit so microphone lifecycle can be verified without TCC.
    func startMicrophoneInput(uid: String) async throws {
        let generation = try queue.sync { () throws -> Int in
            guard !microphoneInputStarted, !stopRequested else { throw CancellationError() }
            microphoneInputStarted = true
            selectedMicrophoneUID = uid
            accepting = true
            return nextMicrophoneGeneration()
        }
        try await performAudioHardwareWork(on: hardware) { [self] in
            try startMicrophone(uid: uid, generation: generation)
        }
    }

    // Called only on hardware, including when replacing a device. A fresh engine avoids
    // reusing a graph that a device switch or failed tap may have left inconsistent.
    private func startMicrophone(uid: String, generation: Int) throws {
        guard queue.sync(execute: { isCurrentMicrophone(generation) }) else { throw CancellationError() }
        disposeMicrophone()
        guard queue.sync(execute: { isCurrentMicrophone(generation) }) else { throw CancellationError() }
        let prepared = try microphoneStart(uid) { [weak self] buffer, time in
            guard let self, buffer.frameLength > 0, let owned = Self.copy(buffer) else { return }
            let timestamp = time.isHostTimeValid ? AVAudioTime.seconds(forHostTime: time.hostTime) : ProcessInfo.processInfo.systemUptime
            self.queue.async {
                guard self.microphoneGeneration == generation else { return }
                self.lastMicrophoneArrival = self.microphoneNow()
                self.microphoneBuffers += 1
                self.receive(owned, speaker: .me, timestamp: timestamp)
                if !self.stopRequested, self.microphoneReady, self.recoveringMicrophone {
                    self.microphoneRecovered(generation: generation)
                }
            }
        }
        guard queue.sync(execute: { isCurrentMicrophone(generation) }) else { prepared.stop(); throw CancellationError() }
        microphone = prepared
        engineObserver = NotificationCenter.default.addObserver(forName: .AVAudioEngineConfigurationChange,
            object: prepared.engine, queue: nil) { [weak self] _ in
            guard let self else { return }
            self.queue.async {
                self.microphoneConfigurationChanged(generation: generation)
            }
        }
        queue.async { [self] in
            guard isCurrentMicrophone(generation) else { return }
            microphoneReady = true
            if recoveringMicrophone {
                if microphoneBuffers > 0 { microphoneRecovered(generation: generation) }
            } else {
                if microphoneBuffers == 0 { lastMicrophoneArrival = microphoneNow() }
                startMicrophoneWatchdog(generation: generation)
            }
        }
    }

    // The following lifecycle helpers run on queue. Driver work remains on hardware.
    private func isCurrentMicrophone(_ generation: Int) -> Bool {
        !stopRequested && !reportedMicrophoneFailure && microphoneGeneration == generation
    }

    private func cancelMicrophoneWork() {
        configurationRevision += 1
        configurationCheck?.cancel(); configurationCheck = nil
        recoveryWork?.cancel(); recoveryWork = nil
        recoveryDeadline?.cancel(); recoveryDeadline = nil
        watchdog?.cancel(); watchdog = nil
    }

    private func nextMicrophoneGeneration() -> Int {
        cancelMicrophoneWork()
        microphoneGeneration += 1
        microphoneReady = false
        microphoneBuffers = 0
        lastMicrophoneArrival = microphoneNow()
        return microphoneGeneration
    }

    private func startMicrophoneWatchdog(generation: Int) {
        watchdog?.cancel()
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + microphoneTimeout, repeating: min(1, microphoneTimeout))
        timer.setEventHandler { [weak self] in
            guard let self, self.isCurrentMicrophone(generation), !self.recoveringMicrophone,
                  self.microphoneNow() - self.lastMicrophoneArrival >= self.microphoneTimeout else { return }
            self.beginMicrophoneRecovery()
        }
        watchdog = timer
        timer.resume()
    }

    private func microphoneConfigurationChanged(generation: Int) {
        guard isCurrentMicrophone(generation), !recoveringMicrophone else { return }
        configurationCheck?.cancel()
        configurationRevision += 1
        let revision = configurationRevision
        let buffers = microphoneBuffers
        let check = DispatchWorkItem { [weak self] in
            guard let self, self.isCurrentMicrophone(generation), self.configurationRevision == revision else { return }
            self.hardware.async { [weak self] in
                guard let self, self.queue.sync(execute: {
                    self.isCurrentMicrophone(generation) && self.configurationRevision == revision
                }) else { return }
                let running = self.microphone?.engine.isRunning == true
                self.queue.async {
                    guard self.isCurrentMicrophone(generation), self.configurationRevision == revision else { return }
                    self.configurationCheck = nil
                    // A notification alone is not a failure. Allow a running engine its
                    // normal startup window; the watchdog also detects a later stalled tap.
                    if running && (self.microphoneBuffers > buffers ||
                        self.microphoneNow() - self.lastMicrophoneArrival < self.microphoneTimeout) { return }
                    self.beginMicrophoneRecovery()
                }
            }
        }
        configurationCheck = check
        queue.asyncAfter(deadline: .now() + configurationDelay, execute: check)
    }

    private func beginMicrophoneRecovery() {
        guard !stopRequested, !reportedMicrophoneFailure, !recoveringMicrophone else { return }
        recoveringMicrophone = true
        recoveryAttempt = 0
        scheduleMicrophoneRecovery()
    }

    private func scheduleMicrophoneRecovery() {
        guard recoveryAttempt < recoveryDelays.count else {
            failMicrophone("The microphone still delivers no audio after an automatic restart. Check the selected device and start the recording again.")
            return
        }
        let generation = nextMicrophoneGeneration()
        let delay = recoveryDelays[recoveryAttempt]
        recoveryAttempt += 1
        let uid = selectedMicrophoneUID
        let work = DispatchWorkItem { [weak self] in
            guard let self, self.isCurrentMicrophone(generation), self.recoveringMicrophone else { return }
            self.recoveryWork = nil
            // Includes driver startup and the first nonempty audio callback. A driver
            // that returns late cannot revive an expired attempt or a stopped recording.
            let deadline = DispatchWorkItem { [weak self] in
                guard let self, self.isCurrentMicrophone(generation), self.recoveringMicrophone else { return }
                self.scheduleMicrophoneRecovery()
            }
            self.recoveryDeadline = deadline
            self.queue.asyncAfter(deadline: .now() + self.microphoneTimeout, execute: deadline)
            self.hardware.async { [weak self] in
                guard let self else { return }
                do { try self.startMicrophone(uid: uid, generation: generation) }
                catch {
                    self.queue.async {
                        guard self.isCurrentMicrophone(generation), self.recoveringMicrophone else { return }
                        self.scheduleMicrophoneRecovery()
                    }
                }
            }
        }
        recoveryWork = work
        queue.asyncAfter(deadline: .now() + delay, execute: work)
    }

    private func microphoneRecovered(generation: Int) {
        recoveringMicrophone = false
        recoveryAttempt = 0
        recoveryDeadline?.cancel(); recoveryDeadline = nil
        startMicrophoneWatchdog(generation: generation)
    }

    private func failMicrophone(_ message: String) {
        guard !stopRequested, !reportedMicrophoneFailure else { return }
        let generation = nextMicrophoneGeneration()
        reportedMicrophoneFailure = true
        recoveringMicrophone = false
        hardware.async { [self] in
            if queue.sync(execute: { microphoneGeneration == generation }) { disposeMicrophone() }
        }
        onError(message)
    }

    private func disposeMicrophone() {
        if let engineObserver { NotificationCenter.default.removeObserver(engineObserver); self.engineObserver = nil }
        microphone?.stop()
        microphone = nil
    }

    func selectMicrophone(uid: String) {
        let generation = queue.sync { () -> Int? in
            guard microphoneInputStarted, !stopRequested else { return nil }
            selectedMicrophoneUID = uid
            reportedMicrophoneFailure = false
            recoveringMicrophone = false
            recoveryAttempt = 0
            return nextMicrophoneGeneration()
        }
        guard let generation else { return }
        hardware.async { [self] in
            do { try startMicrophone(uid: uid, generation: generation) }
            catch is CancellationError { }
            catch {
                queue.async {
                    guard self.isCurrentMicrophone(generation) else { return }
                    self.failMicrophone("Microphone switch failed: \(error.localizedDescription)")
                }
            }
        }
    }

    /// Invalidate callbacks immediately; driver teardown never blocks the main actor.
    func stopInput(discardPending: Bool = true) {
        queue.sync {
            stopRequested = true
            recoveringMicrophone = false
            if discardPending { microphoneGeneration += 1 }
            cancelMicrophoneWork()
            if discardPending { accepting = false }
        }
        hardware.async { self.disposeMicrophone() }
    }

    @MainActor func stop() async {
        stopInput(discardPending: false)
        let finishingStream = stream
        stream = nil
        if let finishingStream { try? await finishingStream.stopCapture() }
        _ = try? await performAudioHardwareWork(on: hardware) { }
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            queue.async { self.accepting = false; done.resume() }
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async { [self] in
            guard !stopRequested else { return }
            onError("System audio recording interrupted: \(error.localizedDescription)")
        }
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of outputType: SCStreamOutputType) {
        guard outputType == .audio, sampleBuffer.isValid,
              let description = sampleBuffer.formatDescription else { return }
        let format = AVAudioFormat(cmAudioFormatDescription: description)
        guard let buffer = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: AVAudioFrameCount(sampleBuffer.numSamples)) else { return }
        buffer.frameLength = buffer.frameCapacity
        let result = CMSampleBufferCopyPCMDataIntoAudioBufferList(sampleBuffer, at: 0,
            frameCount: Int32(sampleBuffer.numSamples), into: buffer.mutableAudioBufferList)
        guard result == noErr else { onError("Could not read system audio (\(result))."); return }
        let pts = sampleBuffer.presentationTimeStamp.seconds
        receive(buffer, speaker: .others, timestamp: pts.isFinite ? pts : ProcessInfo.processInfo.systemUptime)
    }

    private func receive(_ buffer: AVAudioPCMBuffer, speaker: Speaker, timestamp: Double) {
        guard accepting else { return }
        let now = ProcessInfo.processInfo.systemUptime
        // Both capture APIs timestamp in host-clock seconds. Fall back to arrival if a driver doesn't.
        let saneTimestamp = abs(timestamp - now) < 60 ? timestamp : now - Double(buffer.frameLength) / buffer.format.sampleRate
        let offset = max(0, saneTimestamp - origin)
        onAudio(speaker, buffer, offset)
        if now - (lastMeter[speaker] ?? 0) > 0.08 {
            lastMeter[speaker] = now
            onMeter(speaker, Self.level(buffer))
        }
    }

    static func copy(_ source: AVAudioPCMBuffer) -> AVAudioPCMBuffer? {
        guard let target = AVAudioPCMBuffer(pcmFormat: source.format, frameCapacity: source.frameLength) else { return nil }
        target.frameLength = source.frameLength
        let sourceList = UnsafeMutableAudioBufferListPointer(UnsafeMutablePointer(mutating: source.audioBufferList))
        let targetList = UnsafeMutableAudioBufferListPointer(target.mutableAudioBufferList)
        for (src, dst) in zip(sourceList, targetList) {
            if let srcData = src.mData, let dstData = dst.mData { memcpy(dstData, srcData, Int(src.mDataByteSize)) }
        }
        return target
    }

    static func level(_ buffer: AVAudioPCMBuffer) -> Double {
        let count = Int(buffer.frameLength)
        guard count > 0 else { return 0 }
        var energy: Double = 0
        if let channels = buffer.floatChannelData {
            for index in 0..<count { let x = Double(channels[0][index]); energy += x * x }
        } else if let channels = buffer.int16ChannelData {
            for index in 0..<count { let x = Double(channels[0][index]) / 32768; energy += x * x }
        }
        let rms = sqrt(energy / Double(count))
        return max(0, min(1, (20 * log10(max(rms, 0.00001)) + 60) / 60))
    }
}
