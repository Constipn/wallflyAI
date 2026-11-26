import AVFoundation
import Foundation

/// Independent microphone/system-audio recognition. Chunk timestamps refer to capture time,
/// rather than upload time; the two sources never share a transcription request.
final class ChunkedTranscriptionPipeline: AudioTranscribing, @unchecked Sendable {
    private static let sampleRate = 16_000
    private static let maximumSamples = sampleRate * 4
    private static let previewSamples = sampleRate * 2
    private static let minimumSamples = sampleRate / 2
    private static let silenceSamples = sampleRate * 35 / 100
    private static let maximumBacklog = 10
    private static let speechThreshold: Float = 0.001778 // -55 dBFS RMS

    private let localServer: LocalWhisperServer?
    private let wordTimestamps: Bool
    private let sampleAccurateTimestamps: Bool
    private let previewTimeout: TimeInterval
    private let speaker: Speaker
    private let language: String?
    private let apiKey: String
    private let model: String
    private let recordingURL: URL?
    private let format: AVAudioFormat
    private let queue: DispatchQueue
    private let session: URLSession
    private let onSegment: @Sendable (TranscriptSegment) -> Void
    private let onError: @Sendable (String) -> Void
    private let onEvent: (@Sendable (TranscriptionEvent) -> Void)?

    // Everything below is confined to queue, including converter and archival writes.
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?
    private var audioFile: AVAudioFile?
    private var writtenFrames = 0
    private var archiveExpectedOffset: TimeInterval?
    private var stopped = false
    private var cancelled = false
    private var failed = false
    private var samples: [Float] = []
    private var chunkOffset: TimeInterval = 0
    private var expectedOffset: TimeInterval?
    private var lastAudioArrival: TimeInterval = 0
    private var idleTimer: DispatchSourceTimer?
    private var segmentID = UUID()
    private var lastPreviewSampleCount = 0
    private var containsSpeech = false
    private var trailingSilence = 0
    private var tasks: [UUID: Task<Void, Never>] = [:]
    private var tail: Task<Void, Never>?

    init(speaker: Speaker, locale: Locale, recordingURL: URL?, apiKey: String, model: String,
         onSegment: @escaping @Sendable (TranscriptSegment) -> Void,
         onError: @escaping @Sendable (String) -> Void,
         onEvent: (@Sendable (TranscriptionEvent) -> Void)? = nil,
         sessionConfiguration: URLSessionConfiguration? = nil, localServer: LocalWhisperServer? = nil,
         wordTimestamps: Bool = false, sampleAccurateTimestamps: Bool = false,
         previewTimeout: TimeInterval = 4) async throws {
        guard localServer != nil || !apiKey.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty else {
            throw MeetingError.message("Add an OpenAI API key before recording with cloud transcription.")
        }
        guard let format = AVAudioFormat(commonFormat: .pcmFormatFloat32,
                                         sampleRate: Double(Self.sampleRate), channels: 1, interleaved: false) else {
            throw MeetingError.message("Could not create the audio format for transcription.")
        }
        self.localServer = localServer
        self.wordTimestamps = wordTimestamps
        self.sampleAccurateTimestamps = sampleAccurateTimestamps
        self.previewTimeout = previewTimeout
        self.speaker = speaker
        let languageCode = locale.language.languageCode?.identifier
        self.language = languageCode == "und" ? nil : languageCode
        self.apiKey = apiKey
        self.model = model.isEmpty ? "gpt-transcribe" : model
        self.recordingURL = recordingURL
        self.format = format
        self.onSegment = onSegment
        self.onError = onError
        self.onEvent = onEvent
        self.queue = DispatchQueue(label: "meetingnotes.openai-audio.\(speaker.rawValue)", qos: .userInitiated)
        let configuration = sessionConfiguration ?? URLSessionConfiguration.ephemeral
        configuration.timeoutIntervalForRequest = 90
        configuration.timeoutIntervalForResource = 180
        configuration.httpShouldSetCookies = false
        configuration.urlCache = nil
        self.session = URLSession(configuration: configuration, delegate: TranscriptionRedirectBlocker(), delegateQueue: nil)
        if let recordingURL {
            self.audioFile = try AVAudioFile(forWriting: recordingURL, settings: format.settings)
        }
        samples.reserveCapacity(Self.maximumSamples)
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 0.4, repeating: 0.15)
        timer.setEventHandler { [weak self] in
            guard let self, !self.stopped, !self.samples.isEmpty, self.containsSpeech,
                  ProcessInfo.processInfo.systemUptime - self.lastAudioArrival > 0.4 else { return }
            self.flushChunk()
        }
        self.idleTimer = timer
        timer.resume()
    }

    /// Capture owns the buffer until this queued operation finishes. No network or encoding
    /// work runs on the audio callback thread.
    func append(_ buffer: AVAudioPCMBuffer, at offset: TimeInterval) {
        queue.async { [self] in
            guard !stopped && !cancelled else { return }
            do {
                let converted = try convert(buffer)
                guard converted.frameLength > 0 else { return }
                let firstFrame = writtenFrames
                try audioFile?.write(from: converted)
                if audioFile != nil, let file = recordingURL?.lastPathComponent {
                    // Chunk offsets below use the raw capture offset. Compare it with the position
                    // projected from the last anchor (not the previous buffer), so jitter within the
                    // gap tolerance cannot accumulate into drift between anchors and segments.
                    let tolerance = sampleAccurateTimestamps ? 1.5 / Double(Self.sampleRate) : 0.3
                    var projected = archiveExpectedOffset ?? offset
                    if archiveExpectedOffset.map({ abs(offset - $0) > tolerance }) ?? true {
                        onEvent?(.anchor(AudioAnchor(file: file, frame: firstFrame, offset: offset)))
                        projected = offset
                    }
                    archiveExpectedOffset = projected + Double(converted.frameLength) / Double(Self.sampleRate)
                    writtenFrames += Int(converted.frameLength)
                }
                guard !failed, let channel = converted.floatChannelData?[0] else { return }
                let count = Int(converted.frameLength)
                lastAudioArrival = ProcessInfo.processInfo.systemUptime
                // The shared processor emits sample-exact timestamps. Preserve even short
                // explicit gaps for Whisper word alignment; legacy hardware callbacks still
                // have conversion latency/jitter and retain their prior tolerance.
                // A hardware device can itself emit mono 16 kHz, so the PCM format
                // cannot establish which clock policy the caller supplied.
                let gapTolerance = sampleAccurateTimestamps ? 1.5 / Double(Self.sampleRate) : 0.3
                if let expectedOffset, abs(offset - expectedOffset) > gapTolerance, !samples.isEmpty { flushChunk() }
                expectedOffset = offset + Double(count) / Double(Self.sampleRate)
                var index = 0
                // A 20ms RMS window avoids letting a long, mostly silent buffer hide speech.
                while index < count && !failed {
                    let end = min(index + 320, count)
                    if samples.isEmpty { chunkOffset = max(0, offset + Double(index) / Double(Self.sampleRate)) }
                    var energy: Float = 0
                    for i in index..<end {
                        let value = channel[i].isFinite ? channel[i] : 0
                        energy += value * value
                        samples.append(value)
                    }
                    let isSpeech = sqrt(energy / Float(end - index)) >= Self.speechThreshold
                    if isSpeech { containsSpeech = true; trailingSilence = 0 }
                    else { trailingSilence += end - index }
                    index = end
                    if samples.count >= Self.maximumSamples ||
                        (samples.count >= Self.minimumSamples && trailingSilence >= Self.silenceSamples) {
                        flushChunk()
                    } else if containsSpeech && samples.count - lastPreviewSampleCount >= Self.previewSamples && tasks.count < 2 {
                        enqueueChunk(isFinal: false)
                        lastPreviewSampleCount = samples.count
                    }
                }
            } catch {
                fail("Audio processing failed.")
            }
        }
    }

    private func convert(_ buffer: AVAudioPCMBuffer) throws -> AVAudioPCMBuffer {
        if buffer.format == format { return buffer }
        if inputFormat != buffer.format {
            converter = AVAudioConverter(from: buffer.format, to: format)
            converter?.primeMethod = .none
            inputFormat = buffer.format
        }
        guard let converter else { throw MeetingError.message("The recording format is not supported.") }
        let capacity = AVAudioFrameCount(ceil(Double(buffer.frameLength) * format.sampleRate / buffer.format.sampleRate)) + 64
        guard let output = AVAudioPCMBuffer(pcmFormat: format, frameCapacity: capacity) else {
            throw MeetingError.message("Could not allocate memory for converted audio.")
        }
        var consumed = false
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, state in
            if consumed { state.pointee = .noDataNow; return nil }
            consumed = true
            state.pointee = .haveData
            return buffer
        }
        if let error { throw error }
        if status == .error { throw MeetingError.message("Audio conversion failed.") }
        return output
    }

    /// Two-second previews reuse one segment ID; four-second blocks or a pause finalize it.
    private func flushChunk() {
        defer {
            samples.removeAll(keepingCapacity: true)
            segmentID = UUID()
            lastPreviewSampleCount = 0
            containsSpeech = false
            trailingSilence = 0
        }
        enqueueChunk(isFinal: true)
    }

    private func enqueueChunk(isFinal: Bool) {
        guard containsSpeech, !samples.isEmpty, !failed, !cancelled else { return }
        guard tasks.count < Self.maximumBacklog else {
            fail("Transcription can no longer keep up with the meeting. The recording was stopped.")
            return
        }
        let currentSegmentID = segmentID
        let wav = Self.encodeWAV(samples: samples)
        let offset = chunkOffset
        let duration = Double(samples.count) / Double(Self.sampleRate)
        let previous = tail
        let id = UUID()
        let task = Task { [self] in
            await previous?.value
            let allowed = await mayUpload()
            if !Task.isCancelled && allowed {
                do {
                    let result = try await recognize(wav, duration: duration, isFinal: isFinal)
                    if !Task.isCancelled && (isFinal || !result.text.trimmingCharacters(in: .whitespacesAndNewlines).isEmpty) {
                        onSegment(TranscriptSegment(id: currentSegmentID, speaker: speaker, offset: offset, text: result.text,
                                                    duration: duration, isFinal: isFinal, words: result.words))
                    }
                } catch is CancellationError {
                    // Cancellation is a deliberate teardown, never a transcript result.
                } catch {
                    // Previews are replaceable guesses. Their failure must not discard
                    // the retained audio or prevent the authoritative final request.
                    if isFinal && !Task.isCancelled {
                        await reportUploadFailure(error)
                    }
                }
            }
            await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
                queue.async { [self] in tasks.removeValue(forKey: id); done.resume() }
            }
        }
        tasks[id] = task
        tail = task
    }

    private func mayUpload() async -> Bool {
        await withCheckedContinuation { done in
            queue.async { [self] in done.resume(returning: !failed && !cancelled) }
        }
    }

    private func reportUploadFailure(_ error: Error) async {
        await withCheckedContinuation { (done: CheckedContinuation<Void, Never>) in
            queue.async { [self] in
                let explanation: String
                if localServer != nil {
                    explanation = "Local Whisper transcription failed: \(error.localizedDescription)"
                } else if let failure = error as? TranscriptionHTTPError {
                    switch failure.status {
                    case 401: explanation = "OpenAI rejects the API key. Check the key in Settings."
                    case 403: explanation = "OpenAI does not allow audio transcription with this key."
                    case 429: explanation = "OpenAI’s usage limit is preventing transcription."
                    case 400, 404: explanation = "OpenAI rejects the transcription model or the audio request. Check the model in Settings."
                    default: explanation = "OpenAI transcription failed (HTTP \(failure.status))."
                    }
                } else if error is URLError {
                    explanation = "The connection to OpenAI dropped during transcription."
                } else {
                    explanation = "OpenAI returned an unreadable transcription."
                }
                fail(explanation)
                done.resume()
            }
        }
    }

    private func fail(_ text: String) {
        guard !failed && !cancelled else { return }
        failed = true
        let recovery = recordingURL == nil ? " This audio was not saved." : " The saved audio stays on this Mac for recovery."
        onError("\(speaker == .me ? "Microphone" : "Meeting audio"): \(text)\(recovery)")
    }

    func finish() async {
        let pending = await withCheckedContinuation { (done: CheckedContinuation<Task<Void, Never>?, Never>) in
            queue.async { [self] in
                stopped = true
                idleTimer?.cancel(); idleTimer = nil
                flushChunk()
                audioFile = nil
                done.resume(returning: tail)
            }
        }
        await pending?.value
        session.finishTasksAndInvalidate()
    }

    func cancel() async {
        let pending = await withCheckedContinuation { (done: CheckedContinuation<[Task<Void, Never>], Never>) in
            queue.async { [self] in
                stopped = true
                idleTimer?.cancel(); idleTimer = nil
                cancelled = true
                samples.removeAll()
                audioFile = nil
                let pending = Array(tasks.values)
                pending.forEach { $0.cancel() }
                done.resume(returning: pending)
            }
        }
        session.invalidateAndCancel()
        for task in pending { await task.value }
    }

    private struct TranscriptionHTTPError: Error { let status: Int }

    private func recognize(_ wav: Data, duration: TimeInterval, isFinal: Bool) async throws -> TranscriptionResult {
        let request: @Sendable () async throws -> TranscriptionResult = { [self] in
            if let localServer {
                return try await localServer.transcribeResult(wav, language: language, duration: duration,
                    includeWords: wordTimestamps && isFinal)
            }
            return try await transcribe(wav, duration: duration, includeWords: wordTimestamps && isFinal, isFinal: isFinal)
        }
        if isFinal { return try await request() }
        // URLSession's request timeout is an inactivity timeout. An absolute deadline
        // also bounds a slowly trickling preview, so it cannot starve final chunks.
        return try await withThrowingTaskGroup(of: TranscriptionResult.self) { group in
            group.addTask { try await request() }
            group.addTask { [previewTimeout] in
                try await Task.sleep(for: .seconds(previewTimeout))
                throw URLError(.timedOut)
            }
            defer { group.cancelAll() }
            return try await group.next()!
        }
    }

    private func transcribe(_ wav: Data, duration: TimeInterval, includeWords: Bool, isFinal: Bool) async throws -> TranscriptionResult {
        let boundary = "MeetingNotes-\(UUID().uuidString)"
        var body = Data()
        func addField(_ name: String, _ value: String) {
            body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"\(name)\"\r\n\r\n\(value)\r\n".utf8))
        }
        addField("model", model)
        addField("response_format", includeWords ? "verbose_json" : "json")
        if includeWords { addField("timestamp_granularities[]", "word") }
        if let language { addField(model == AppSettings.openAITranscriptionModel ? "languages[]" : "language", language) }
        body.append(Data("--\(boundary)\r\nContent-Disposition: form-data; name=\"file\"; filename=\"meeting.wav\"\r\nContent-Type: audio/wav\r\n\r\n".utf8))
        body.append(wav)
        body.append(Data("\r\n--\(boundary)--\r\n".utf8))
        var request = URLRequest(url: URL(string: "https://api.openai.com/v1/audio/transcriptions")!)
        request.httpMethod = "POST"
        request.setValue("Bearer \(apiKey)", forHTTPHeaderField: "Authorization")
        request.setValue("multipart/form-data; boundary=\(boundary)", forHTTPHeaderField: "Content-Type")
        request.httpBody = body
        let retries = isFinal ? 2 : 0
        for attempt in 0...retries {
            try Task.checkCancellation()
            let (data, response) = try await session.data(for: request)
            guard let response = response as? HTTPURLResponse else { throw URLError(.badServerResponse) }
            if (200..<300).contains(response.statusCode) {
                return try TranscriptionResult.decodeOpenAI(data, duration: duration, includeWords: includeWords)
            }
            if attempt < retries && (response.statusCode == 429 || (500..<600).contains(response.statusCode)) {
                let serverDelay = response.value(forHTTPHeaderField: "Retry-After").flatMap(Double.init)
                let delay = min(10, max(1, serverDelay ?? pow(2, Double(attempt))))
                try await Task.sleep(for: .seconds(delay))
                continue
            }
            // Never include an API error body: it can contain request data or credentials.
            throw TranscriptionHTTPError(status: response.statusCode)
        }
        throw URLError(.badServerResponse)
    }

    /// Standard little-endian, mono PCM16 WAV. Exposed internally for deterministic tests.
    static func encodeWAV(samples: [Float], sampleRate: Int = 16_000) -> Data {
        var data = Data(capacity: 44 + samples.count * 2)
        func ascii(_ string: String) { data.append(contentsOf: string.utf8) }
        func u16(_ value: UInt16) { var little = value.littleEndian; withUnsafeBytes(of: &little) { data.append(contentsOf: $0) } }
        func u32(_ value: UInt32) { var little = value.littleEndian; withUnsafeBytes(of: &little) { data.append(contentsOf: $0) } }
        ascii("RIFF"); u32(UInt32(36 + samples.count * 2)); ascii("WAVE")
        ascii("fmt "); u32(16); u16(1); u16(1); u32(UInt32(sampleRate)); u32(UInt32(sampleRate * 2)); u16(2); u16(16)
        ascii("data"); u32(UInt32(samples.count * 2))
        for sample in samples {
            let finite = sample.isFinite ? sample : 0
            let clamped = min(1, max(-1, finite))
            let pcm = Int16((clamped * (clamped < 0 ? 32768 : 32767)).rounded())
            u16(UInt16(bitPattern: pcm))
        }
        return data
    }
}

/// Do not forward the Authorization header or recorded audio through HTTP redirects.
private final class TranscriptionRedirectBlocker: NSObject, URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(_ session: URLSession, task: URLSessionTask,
                    willPerformHTTPRedirection response: HTTPURLResponse,
                    newRequest request: URLRequest,
                    completionHandler: @escaping (URLRequest?) -> Void) {
        completionHandler(nil)
    }
}
