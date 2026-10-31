import AVFoundation
import Foundation
import OSLog

/// All mutable recognition state is confined to queue. Socket calls return immediately;
/// archival writes therefore never wait for the network. Admission is bounded before dispatch.
final class DeepgramTranscriptionPipeline: AudioTranscribing, @unchecked Sendable {
    private static let logger = Logger(subsystem: "io.github.constipn.wallfly", category: "deepgram-streaming")
    struct Policy {
        var bufferSeconds = 10.0
        var operationTimeout = 10.0
        var finishTimeout = 20.0
        var retryDelays = [0.5, 1.0, 2.0]
        var recoveryInterval = 30.0
        var keepAliveInterval = 3.0
    }
    private struct Packet {
        var offset: Double
        var data: Data
        var duration: Double { Double(data.count) / 32_000 }
    }
    private enum State { case idle, connecting, streaming, recovering, paused, closing, ended }
    private let queue = DispatchQueue(label: "meetingnotes.deepgram", qos: .userInitiated)
    private let admission = NSLock()
    private var admittedSeconds = 0.0
    private var accepting = true
    private var ingressFailed = false
    private let speaker: Speaker
    private let locale: Locale
    private var key: String
    private let policy: Policy
    private let now: @Sendable () -> Double
    private let makeTransport: @Sendable (URLRequest) -> any DeepgramTransport
    private let onSegment: @Sendable (TranscriptSegment) -> Void
    private let onEvent: @Sendable (TranscriptionEvent) -> Void
    private let onError: @Sendable (String) -> Void
    private let format = AVAudioFormat(standardFormatWithSampleRate: 16_000, channels: 1)!
    private let sampleAccurateTimestamps: Bool
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?
    private var audioFile: AVAudioFile?
    private let audioFileName: String?
    private var writtenFrames = 0
    private var timer: DispatchSourceTimer?
    private var state: State = .idle
    private var transport: (any DeepgramTransport)?
    private var generation = UUID()
    private var assembler: DeepgramResultAssembler?
    private var anchor: Double?
    private var sentEnd: Double = 0
    private var pending: [Packet] = []
    private var pendingBytes = 0
    private var partial = Data()
    private var partialOffset: Double = 0
    private var expectedOffset: Double?
    private var lastAudioArrival = 0.0
    private var inFlight = false
    private var operationDeadline: Double?
    private var nextSend = 0.0
    private var lastSend = 0.0
    private var nextRetry = 0.0
    private var failures = 0
    private var finishing = false
    private var finishDeadline: Double?
    private var finishWaiters: [CheckedContinuation<Void, Never>] = []
    // Numeric diagnostics only: no audio, recognized text, API credentials or request URLs.
    private var diagnosticsStarted: Double?
    private var diagnosticSamples = 0
    private var diagnosticEnergy = 0.0
    private var diagnosticResults = 0
    private var diagnosticNonemptyResults = 0
    private var diagnosticFinalResults = 0
    private var diagnosticPublishedFinals = 0
    private var diagnosticRejectedNonempty = 0
    private var diagnosticLastResultEnd = 0.0

    init(speaker: Speaker, locale: Locale, recordingURL: URL?, apiKey: String,
         onSegment: @escaping @Sendable (TranscriptSegment) -> Void,
         onEvent: @escaping @Sendable (TranscriptionEvent) -> Void,
         onError: @escaping @Sendable (String) -> Void,
         sampleAccurateTimestamps: Bool = true, policy: Policy = Policy(),
         now: @escaping @Sendable () -> Double = { ProcessInfo.processInfo.systemUptime },
         automaticTimer: Bool = true,
         makeTransport: @escaping @Sendable (URLRequest) -> any DeepgramTransport = { DeepgramWebSocket(request: $0) }) throws {
        guard !apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MeetingError.message("No Deepgram API key found. Set DEEPGRAM_API_KEY or enter it in Settings.")
        }
        self.speaker = speaker; self.locale = locale; self.key = apiKey
        self.onSegment = onSegment; self.onEvent = onEvent; self.onError = onError
        self.sampleAccurateTimestamps = sampleAccurateTimestamps
        self.policy = policy; self.now = now; self.makeTransport = makeTransport
        self.audioFileName = recordingURL?.lastPathComponent
        if let recordingURL { audioFile = try AVAudioFile(forWriting: recordingURL, settings: format.settings) }
        if automaticTimer {
            let timer = DispatchSource.makeTimerSource(queue: queue)
            timer.schedule(deadline: .now(), repeating: 0.02, leeway: .milliseconds(5))
            timer.setEventHandler { [weak self] in self?.tick() }
            self.timer = timer
            timer.resume()
        }
    }

    deinit { timer?.cancel(); transport?.close() }

    func append(_ buffer: AVAudioPCMBuffer, at offset: TimeInterval) {
        let seconds = Double(buffer.frameLength) / buffer.format.sampleRate
        admission.lock()
        guard accepting else { admission.unlock(); return }
        guard seconds.isFinite, seconds >= 0, admittedSeconds + seconds <= policy.bufferSeconds else {
            let report = !ingressFailed
            ingressFailed = true
            admission.unlock()
            // This is a local processing/archival overload, not a network outage.
            if report { onError("Audio processing overloaded; the recording is being stopped to avoid further unnoticed audio loss.") }
            return
        }
        admittedSeconds += seconds
        // Enqueue under the admission lock so finish cannot overtake an accepted buffer.
        queue.async { [self] in
            defer { admission.lock(); admittedSeconds -= seconds; admission.unlock() }
            guard state != .ended else { return }
            do {
                let normalized = try convert(buffer)
                guard normalized.frameLength > 0, offset.isFinite, offset >= 0,
                      let channel = normalized.floatChannelData?[0] else { return }
                diagnosticsStarted = diagnosticsStarted ?? now()
                for i in 0..<Int(normalized.frameLength) {
                    let sample = channel[i].isFinite ? Double(channel[i]) : 0
                    diagnosticEnergy += sample * sample
                }
                diagnosticSamples += Int(normalized.frameLength)
                // Archive every captured sample even when recognition is paused or recovering.
                let firstFrame = writtenFrames
                try audioFile?.write(from: normalized)
                let duration = Double(normalized.frameLength) / 16_000
                var offset = offset
                var discontinuous = expectedOffset == nil
                if let expectedOffset {
                    let tolerance = sampleAccurateTimestamps ? 1.5 / 16_000 : 0.3
                    if abs(expectedOffset - offset) <= tolerance { offset = expectedOffset }
                    else { flushPartial(); discontinuous = true }
                }
                expectedOffset = offset + duration
                if audioFile != nil, let audioFileName {
                    // Same snapped offset that recognition packets (and so segment offsets) use.
                    if discontinuous { onEvent(.anchor(AudioAnchor(file: audioFileName, frame: firstFrame, offset: offset))) }
                    writtenFrames += Int(normalized.frameLength)
                }
                lastAudioArrival = now()
                if state == .paused {
                    gap(offset, duration, "Transcription paused: check access or configuration")
                    return
                }
                appendPCM(Self.encodePCM(UnsafeBufferPointer(start: channel, count: Int(normalized.frameLength))), at: offset)
                tick()
            } catch {
                onError("Audio could not be processed or archived. The recording is being stopped.")
            }
        }
        admission.unlock()
    }

    static func encodePCM(_ samples: UnsafeBufferPointer<Float>) -> Data {
        var result = Data(capacity: samples.count * 2)
        for sample in samples {
            let value = sample.isFinite ? min(1, max(-1, sample)) : 0
            let integer = Int16(clamping: Int((Double(value) * 32768).rounded()))
            let bits = UInt16(bitPattern: integer)
            result.append(UInt8(truncatingIfNeeded: bits))
            result.append(UInt8(truncatingIfNeeded: bits >> 8))
        }
        return result
    }

    private func convert(_ buffer: AVAudioPCMBuffer) throws -> AVAudioPCMBuffer {
        if buffer.format == format { return buffer }
        if inputFormat != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: format)
            converter?.primeMethod = .none
            inputFormat = buffer.format
        }
        guard let converter, let output = AVAudioPCMBuffer(pcmFormat: format,
            frameCapacity: AVAudioFrameCount(ceil(Double(buffer.frameLength) * 16_000 / buffer.format.sampleRate)) + 64) else {
            throw MeetingError.message("Unsupported audio format")
        }
        var consumed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, state in
            if consumed { state.pointee = .noDataNow; return nil }
            consumed = true; state.pointee = .haveData; return buffer
        }
        if let error { throw error }
        if status == .error { throw MeetingError.message("Audio conversion failed") }
        return output
    }

    private func appendPCM(_ data: Data, at offset: Double) {
        if partial.isEmpty { partialOffset = offset }
        partial.append(data)
        while partial.count >= 3200 {
            enqueue(Packet(offset: partialOffset, data: Data(partial.prefix(3200))))
            partial.removeFirst(3200)
            partialOffset += 0.1
        }
    }

    private func flushPartial() {
        guard !partial.isEmpty else { return }
        enqueue(Packet(offset: partialOffset, data: partial))
        partial = Data()
    }

    private func enqueue(_ packet: Packet) {
        pending.append(packet); pendingBytes += packet.data.count
        var droppedStart: Double?, droppedEnd = 0.0
        while pendingBytes > Int(policy.bufferSeconds * 32_000), !pending.isEmpty {
            let dropped = pending.removeFirst(); pendingBytes -= dropped.data.count
            droppedStart = droppedStart ?? dropped.offset
            droppedEnd = dropped.offset + dropped.duration
        }
        if let start = droppedStart {
            gap(start, droppedEnd - start, "Audio buffer full: not transmitted")
            if anchor != nil { disconnect(reason: "Audio buffer full", retry: false) }
        }
    }

    private func tick() {
        guard state != .ended else { return }
        if let diagnosticsStarted, now() - diagnosticsStarted >= 30 { reportDiagnostics() }
        // Selected-app capture can cease callbacks during silence. Do not strand the last
        // sub-100ms packet until another speaker starts or the user stops recording.
        if !partial.isEmpty, now() - lastAudioArrival >= 0.1 { flushPartial() }
        if let finishDeadline, now() >= finishDeadline {
            discardPending(reason: "Transcript finalization exceeded 20 seconds")
            invalidate(markUnconfirmed: true)
            end()
            return
        }
        if let operationDeadline, now() >= operationDeadline {
            disconnect(reason: "Deepgram connection not responding", retry: true)
        }
        if finishing && pending.isEmpty && partial.isEmpty && (state == .idle || state == .recovering || state == .paused) {
            end(); return
        }
        if (state == .idle || (state == .recovering && now() >= nextRetry)), !pending.isEmpty { connect() }
        guard state == .streaming, !inFlight else { return }
        if let packet = pending.first, now() >= nextSend {
            if let anchor, abs(packet.offset - (anchor + sentEnd)) > 1.5 / 16_000 {
                // Never splice capture gaps out of Deepgram's sample clock.
                closeStream()
                return
            }
            if anchor == nil {
                anchor = packet.offset
                assembler = DeepgramResultAssembler(speaker: speaker, generation: generation, anchor: packet.offset)
            }
            pending.removeFirst(); pendingBytes -= packet.data.count
            sentEnd += packet.duration // includes an ambiguous in-flight send if transport subsequently fails
            nextSend = now() + packet.duration / 1.25
            send(.data(packet.data))
        } else if pending.isEmpty && finishing { closeStream() }
        else if now() - lastSend >= policy.keepAliveInterval { send(.string("{\"type\":\"KeepAlive\"}")) }
    }

    private func connect() {
        generation = UUID(); anchor = nil; sentEnd = 0; assembler = nil
        state = .connecting
        onEvent(.state(speaker, "Connecting…"))
        operationDeadline = now() + policy.operationTimeout
        let current = generation
        let transport = makeTransport(DeepgramConfiguration.request(key: key, locale: locale))
        self.transport = transport
        transport.open { [weak self] result in
            guard let self else { return }
            self.queue.async {
                guard self.generation == current, self.state == .connecting else { return }
                switch result {
                case .success:
                    self.operationDeadline = nil; self.state = .streaming
                    self.lastSend = self.now(); self.nextSend = self.now()
                    self.onEvent(.state(self.speaker, "Live · Nova-3"))
                    self.receive(current)
                    self.tick()
                case .failure(let error): self.transportFailed(error)
                }
            }
        }
    }

    private func receive(_ current: UUID) {
        transport?.receive { [weak self] result in
            guard let self else { return }
            self.queue.async {
                guard self.generation == current, self.state != .ended else { return }
                switch result {
                case .success(let message):
                    let data: Data
                    switch message {
                    case .data(let bytes): data = bytes
                    case .string(let string): data = Data(string.utf8)
                    @unknown default: self.disconnect(reason: "Unreadable Deepgram response", retry: true); return
                    }
                    do {
                        let result = try JSONDecoder().decode(DeepgramResult.self, from: data)
                        if result.type == "Error" {
                            // No raw server text in logs/UI (may contain request information).
                            self.disconnect(reason: "Deepgram reported a streaming error", retry: true)
                            return
                        }
                        if result.type == "Results", var assembler = self.assembler {
                            let nonempty = !(result.channel?.alternatives.first?.transcript.isEmpty ?? true)
                            self.diagnosticResults += 1
                            if nonempty { self.diagnosticNonemptyResults += 1 }
                            if result.is_final == true { self.diagnosticFinalResults += 1 }
                            self.diagnosticLastResultEnd = (result.start ?? 0) + (result.duration ?? 0)
                            let segments = assembler.accept(result, sentDuration: self.sentEnd)
                            self.diagnosticPublishedFinals += segments.filter { $0.isFinal && !$0.text.isEmpty }.count
                            if nonempty && !segments.contains(where: { !$0.text.isEmpty }) { self.diagnosticRejectedNonempty += 1 }
                            self.assembler = assembler
                            segments.forEach(self.onSegment)
                            if result.is_final == true { self.failures = 0 }
                        }
                        if result.type == "Metadata", self.state == .closing {
                            self.completeClose(confirmed: true); return
                        }
                    } catch { self.disconnect(reason: "Unreadable Deepgram response", retry: true); return }
                    self.receive(current)
                case .failure(let error):
                    if self.state == .closing { self.completeClose(confirmed: false) }
                    else { self.transportFailed(error) }
                }
            }
        }
    }

    private func send(_ message: URLSessionWebSocketTask.Message) {
        inFlight = true
        operationDeadline = now() + policy.operationTimeout
        lastSend = now()
        let current = generation
        transport?.send(message) { [weak self] error in
            guard let self else { return }
            self.queue.async {
                guard self.generation == current, self.state != .ended else { return }
                self.inFlight = false
                if let error { self.transportFailed(error); return }
                // CloseStream keeps its deadline until the terminal receive.
                if self.state != .closing { self.operationDeadline = nil }
                self.tick()
            }
        }
    }

    private func closeStream() {
        state = .closing
        onEvent(.state(speaker, "Finalizing transcript…"))
        send(.string("{\"type\":\"CloseStream\"}"))
    }

    private func completeClose(confirmed: Bool) {
        // Metadata follows final results on a successful CloseStream. A transport failure
        // alone is not proof of processing and must preserve an uncertainty marker.
        invalidate(markUnconfirmed: !confirmed)
        state = .idle
        if finishing && pending.isEmpty { end() }
        else { tick() }
    }

    private func transportFailed(_ error: Error) {
        if let failure = error as? DeepgramTransportError,
           [400, 401, 402, 403, 404, 422].contains(failure.status) {
            invalidate(markUnconfirmed: true)
            state = .paused
            discardPending(reason: "Deepgram access or configuration invalid")
            onEvent(.state(speaker, "Paused · check Deepgram access and reconnect"))
            if finishing { end() }
        } else { disconnect(reason: "Deepgram connection interrupted", retry: true) }
    }

    private func disconnect(reason: String, retry: Bool) {
        invalidate(markUnconfirmed: true)
        state = .recovering
        let delay = retry ? (failures < policy.retryDelays.count ? policy.retryDelays[failures] : policy.recoveryInterval) : 0
        if retry { failures += 1 }
        nextRetry = now() + delay
        onEvent(.state(speaker, "\(reason) · retrying in \(Int(ceil(delay))) s"))
    }

    private func invalidate(markUnconfirmed: Bool) {
        reportDiagnostics()
        if markUnconfirmed, let anchor {
            let confirmed = assembler?.confirmedThrough ?? 0
            gap(anchor + confirmed, max(0, sentEnd - confirmed), "Sent but not finally confirmed; possibly incomplete")
        }
        if var assembler { assembler.clearPreview().forEach(onSegment) }
        generation = UUID()
        transport?.close(); transport = nil; assembler = nil; anchor = nil; sentEnd = 0
        inFlight = false; operationDeadline = nil
    }

    private func gap(_ start: Double, _ duration: Double, _ reason: String) {
        guard duration > 0.0001 else { return }
        onEvent(.gap(TranscriptionGap(start: start, duration: duration, speaker: speaker, reason: reason)))
    }

    private func discardPending(reason: String) {
        flushPartial()
        for packet in pending { gap(packet.offset, packet.duration, reason) }
        pending.removeAll(); pendingBytes = 0
    }

    func restart(apiKey: String) {
        queue.async { [self] in
            guard state != .ended, !finishing else { return }
            key = apiKey
            disconnect(reason: "Reconnecting transcription", retry: false)
            failures = 0
            tick()
        }
    }

    func finish() async {
        await withCheckedContinuation { done in
            admission.lock(); accepting = false
            queue.async { [self] in
                guard state != .ended else { done.resume(); return }
                finishWaiters.append(done)
                if !finishing {
                    finishing = true; finishDeadline = now() + policy.finishTimeout
                    flushPartial()
                    audioFile = nil
                }
                tick()
            }
            admission.unlock()
        }
    }

    func cancel() async {
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            admission.lock(); accepting = false
            queue.async { [self] in
                invalidate(markUnconfirmed: true)
                discardPending(reason: "Transcription cancelled")
                end(); done.resume()
            }
            admission.unlock()
        }
    }

    private func end() {
        guard state != .ended else { return }
        reportDiagnostics()
        state = .ended
        timer?.cancel(); timer = nil; transport?.close(); transport = nil; audioFile = nil
        onEvent(.state(speaker, "Ended"))
        let waiters = finishWaiters; finishWaiters = []
        waiters.forEach { $0.resume() }
    }

    private func reportDiagnostics() {
        guard diagnosticSamples > 0 || diagnosticResults > 0 else { return }
        let rms = sqrt(diagnosticEnergy / Double(max(1, diagnosticSamples)))
        Self.logger.info("Deepgram source=\(self.speaker.rawValue, privacy: .public) inputSamples=\(self.diagnosticSamples) rms=\(rms) sentSeconds=\(self.sentEnd) queuedBytes=\(self.pendingBytes) results=\(self.diagnosticResults) nonempty=\(self.diagnosticNonemptyResults) finalResults=\(self.diagnosticFinalResults) publishedFinals=\(self.diagnosticPublishedFinals) ignoredNonempty=\(self.diagnosticRejectedNonempty) lastResultEnd=\(self.diagnosticLastResultEnd)")
        diagnosticSamples = 0; diagnosticEnergy = 0; diagnosticResults = 0
        diagnosticNonemptyResults = 0; diagnosticFinalResults = 0; diagnosticPublishedFinals = 0
        diagnosticRejectedNonempty = 0; diagnosticLastResultEnd = 0; diagnosticsStarted = now()
    }

    /// Deterministic scheduler boundary: tests advance the injected clock, then execute a tick.
    func synchronize() async {
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            queue.async { [self] in tick(); done.resume() }
        }
    }
}
