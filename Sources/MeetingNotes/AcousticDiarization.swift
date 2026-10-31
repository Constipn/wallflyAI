import AVFoundation
import Foundation
import FluidAudio

/// One diarized stretch of remote audio, in meeting-offset seconds.
/// `speaker` is the cluster index of one diarization run (0-based, numbered by first appearance).
struct AcousticSegment: Codable, Equatable, Sendable {
    let speaker: Int
    let start: TimeInterval
    let end: TimeInterval
}

enum AcousticDiarizationError: Error, LocalizedError, Equatable {
    /// Models could not be downloaded or loaded (offline, proxy, corrupt cache). Callers skip quietly.
    case modelsUnavailable(String)
    /// None of the given recordings contains audio.
    case noAudio
    /// A recording could not be read or is not 16 kHz mono.
    case unreadableAudio(file: String, reason: String)
    case processingFailed(String)
    /// Remote audio longer than `AcousticDiarization.maxAudioSeconds`; callers skip with a message.
    case audioTooLong(seconds: Int, limit: Int)

    var errorDescription: String? {
        switch self {
        case .modelsUnavailable(let reason): return "Speaker models unavailable: \(reason)"
        case .noAudio: return "No audio recording available for speaker detection."
        case .unreadableAudio(let file, let reason): return "Audio file \(file) unreadable: \(reason)"
        case .processingFailed(let reason): return "Speaker detection failed: \(reason)"
        case .audioTooLong(let seconds, let limit):
            return "Recording too long for speaker detection (\(seconds / 60) min, limit \(limit / 60) min)."
        }
    }
}

/// Offline speaker diarization of a meeting's remote channel.
///
/// The shared `diarize(audioFiles:…)` lays the recording attempts out on one 16 kHz mono timeline (with silence
/// between them, so a single clustering run keeps identities consistent across attempts), diarizes it once and
/// maps the result back to meeting offsets. Implementations provide `diarize(samples:)` (whole timeline in memory,
/// fine for tests) and may override `diarize(audio:)` to read the timeline lazily (`FluidAudioDiarizer` does).
protocol AcousticDiarizer: Sendable {
    var engine: String { get }
    var modelVersion: String? { get }
    var threshold: Double? { get }
    /// Times in the result are seconds into `samples`; speaker IDs may be arbitrary integers.
    func diarize(samples: [Float], progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment]
    /// Same contract on a lazily read timeline. Default: reads everything and calls `diarize(samples:)`.
    func diarize(audio: ConcatenatedAudio, progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment]
}

extension AcousticDiarizer {
    func diarize(audio: ConcatenatedAudio, progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment] {
        try await diarize(samples: audio.readAll(), progress: progress)
    }

    /// Diarizes the given recordings (in attempt order) once and returns meeting-offset segments sorted by start.
    /// `timeMap` maps a file to its meeting time; nil means the file starts at offset 0 without gaps.
    /// Segments (or parts of them) in the inserted silence are dropped. Throws `audioTooLong` above `maxSeconds`.
    func diarize(audioFiles: [URL], timeMap: (URL) -> AudioTimeMap?,
                 progress: (@Sendable (Double) -> Void)? = nil,
                 maxSeconds: TimeInterval = AcousticDiarization.maxAudioSeconds) async throws -> [AcousticSegment] {
        let audio = try ConcatenatedAudio(files: audioFiles)
        guard audio.parts.contains(where: { $0.frameCount > 0 }) else { throw AcousticDiarizationError.noAudio }
        let seconds = Double(audio.sampleCount) / AudioTimeMap.sampleRate
        guard seconds <= maxSeconds else {
            throw AcousticDiarizationError.audioTooLong(seconds: Int(seconds.rounded()), limit: Int(maxSeconds))
        }
        let maps = audio.parts.map { timeMap($0.file) ?? AudioTimeMap(anchors: [], frameCount: $0.frameCount) }
        try Task.checkCancellation()
        let raw = try await diarize(audio: audio, progress: progress)
        return AcousticDiarization.renumbered(audio.meetingSegments(raw, timeMaps: maps))
    }
}

enum AcousticDiarization {
    static let sampleRate = AudioTimeMap.sampleRate
    /// Audio is streamed from disk, but FluidAudio keeps per-chunk segmentation/embedding state in memory:
    /// peak RSS of the live test was ≈ 250 MB for 7 min and ≈ 300 MB for 30 min (M5 Pro), i.e. roughly
    /// +130 MB per hour. 4 h (≈ 0.75 GB estimated) bounds forgotten recordings.
    static let maxAudioSeconds: TimeInterval = 4 * 3600

    /// Remote-channel recordings of a meeting in attempt order (`Meeting.audioFiles` keeps that order).
    static func othersFiles(of meeting: Meeting) -> [URL] {
        meeting.audioFiles.filter { $0.hasSuffix("-others.caf") }.map { URL(fileURLWithPath: $0) }
    }

    /// Time maps of the given remote-channel files: the saved anchors when a file has some, otherwise the
    /// `recordingGaps` fallback. Fallback caveats: the last file (also a single one) is placed so that it ends at
    /// `meeting.duration` (the stop time), other files start at 0 (first) or where the previous one ends; the
    /// capture source whose gaps are applied is guessed by `fallbackSource`. On the two reference meetings the
    /// end placement matched the word timestamps better than a start at 0 (capture starts ~0.27 s late).
    static func timeMaps(for meeting: Meeting, files: [URL]) throws -> [URL: AudioTimeMap] {
        let lengths = try files.map { try ConcatenatedAudio.frameCount(of: $0) }
        return timeMaps(for: meeting, files: Array(zip(files, lengths)))
    }

    static func timeMaps(for meeting: Meeting, files: [(url: URL, frameCount: Int)]) -> [URL: AudioTimeMap] {
        let anchors = meeting.audioAnchors ?? []
        let gaps = meeting.recordingGaps ?? []
        let totalSeconds = files.reduce(0) { $0 + Double($1.frameCount) / sampleRate }
        let source = fallbackSource(gaps: gaps, missingSeconds: max(0, meeting.duration - totalSeconds))
        var result: [URL: AudioTimeMap] = [:]
        var previousEnd: TimeInterval = 0
        for (index, file) in files.enumerated() {
            let name = file.url.lastPathComponent
            var map = AudioTimeMap(anchors: anchors, file: name, frameCount: file.frameCount)
            if map.anchors.isEmpty {
                var start = index == 0 ? 0 : previousEnd
                if index == files.count - 1 {
                    let seconds = Double(file.frameCount) / sampleRate
                    let lost = mergedGapDuration(gaps.filter { $0.source == source }, from: previousEnd, to: meeting.duration)
                    start = max(previousEnd, meeting.duration - seconds - lost)
                }
                map = AudioTimeMap(recordingGaps: gaps, source: source, file: name, fileStart: start, frameCount: file.frameCount)
            }
            result[file.url] = map
            previousEnd = max(previousEnd, map.offset(forFrame: file.frameCount))
        }
        return result
    }

    /// The remote channel comes from the app-specific `.meeting` stream when a meeting app was selected and from
    /// the global `.reference` tap otherwise; old meetings do not record which. `.meeting` gaps prove the former;
    /// `.reference` gaps longer than the time missing from the files cannot have been cut out of them.
    static func fallbackSource(gaps: [RecordingGap], missingSeconds: TimeInterval) -> CaptureSource {
        if gaps.contains(where: { $0.source == .meeting && $0.duration > 0 }) { return .meeting }
        let reference = mergedGapDuration(gaps.filter { $0.source == .reference }, from: 0, to: .infinity)
        return reference > missingSeconds + 0.5 ? .meeting : .reference
    }

    static func mergedGapDuration(_ gaps: [RecordingGap], from lower: TimeInterval, to upper: TimeInterval) -> TimeInterval {
        var end = lower, total = 0.0
        for gap in gaps.sorted(by: { $0.start < $1.start }) {
            let start = max(gap.start, end), stop = min(gap.start + gap.duration, upper)
            if stop > start { total += stop - start }
            end = max(end, min(gap.start + gap.duration, upper))
        }
        return total
    }

    /// Sorts by start and renumbers speakers 0, 1, … by first appearance so IDs do not depend on engine internals.
    static func renumbered(_ segments: [AcousticSegment]) -> [AcousticSegment] {
        let sorted = segments.enumerated().sorted { ($0.element.start, $0.offset) < ($1.element.start, $1.offset) }.map(\.element)
        var ids: [Int: Int] = [:]
        return sorted.map { segment in
            let id = ids[segment.speaker] ?? ids.count
            ids[segment.speaker] = id
            return AcousticSegment(speaker: id, start: segment.start, end: segment.end)
        }
    }
}

/// Several 16 kHz mono recordings on one sample timeline, separated by silence, read lazily.
///
/// Only the file headers are opened up front; `copySamples` reads the requested window from the CAF files
/// (and synthesizes the separators), so memory does not grow with meeting length. Thread-safe: FluidAudio's
/// segmentation and embedding workers read concurrently, so reads are serialized by a lock.
final class ConcatenatedAudio: AudioSampleSource, @unchecked Sendable {
    static let separatorSeconds = 1.0
    struct Part: Equatable {
        let file: URL
        /// First sample of this file on the timeline.
        let start: Int
        let frameCount: Int
    }
    let parts: [Part]
    let sampleCount: Int
    let separatorFrames: Int
    private let files: [AVAudioFile]
    private let lock = NSLock()
    private var scratch: AVAudioPCMBuffer?

    init(files urls: [URL], separatorSeconds: Double = ConcatenatedAudio.separatorSeconds) throws {
        separatorFrames = Int((separatorSeconds * AudioTimeMap.sampleRate).rounded())
        files = try urls.map { try Self.open($0) }
        var parts: [Part] = [], cursor = 0
        for (url, file) in zip(urls, files) {
            let frames = Int(file.length)
            if frames > 0, parts.contains(where: { $0.frameCount > 0 }) { cursor += separatorFrames }
            parts.append(Part(file: url, start: cursor, frameCount: frames))
            cursor += frames
        }
        self.parts = parts
        sampleCount = cursor
    }

    static func frameCount(of url: URL) throws -> Int { Int(try open(url).length) }

    private static func open(_ url: URL) throws -> AVAudioFile {
        let file: AVAudioFile
        do { file = try AVAudioFile(forReading: url, commonFormat: .pcmFormatFloat32, interleaved: false) }
        catch { throw AcousticDiarizationError.unreadableAudio(file: url.lastPathComponent, reason: error.localizedDescription) }
        let format = file.fileFormat
        guard format.sampleRate == AudioTimeMap.sampleRate, format.channelCount == 1 else {
            throw AcousticDiarizationError.unreadableAudio(file: url.lastPathComponent,
                reason: "\(Int(format.sampleRate)) Hz, \(format.channelCount) channels instead of 16 kHz mono")
        }
        return file
    }

    /// Writes timeline samples `offset ..< offset + count` to `destination`; anything outside the files
    /// (separators, before 0, past the end) is zero.
    func copySamples(into destination: UnsafeMutablePointer<Float>, offset: Int, count: Int) throws {
        guard count > 0 else { return }
        destination.initialize(repeating: 0, count: count)
        lock.lock(); defer { lock.unlock() }
        for (part, file) in zip(parts, files) where part.frameCount > 0 {
            let lower = max(offset, part.start), upper = min(offset + count, part.start + part.frameCount)
            guard upper > lower else { continue }
            try read(file, name: part.file.lastPathComponent, from: lower - part.start, count: upper - lower,
                     into: destination.advanced(by: lower - offset))
        }
    }

    private func read(_ file: AVAudioFile, name: String, from frame: Int, count: Int, into destination: UnsafeMutablePointer<Float>) throws {
        let chunk = 1 << 18
        if scratch == nil || Int(scratch!.frameCapacity) < min(count, chunk) {
            scratch = AVAudioPCMBuffer(pcmFormat: file.processingFormat, frameCapacity: AVAudioFrameCount(min(count, chunk)))
        }
        guard let buffer = scratch else {
            throw AcousticDiarizationError.unreadableAudio(file: name, reason: "Could not allocate buffer")
        }
        var done = 0
        do {
            file.framePosition = AVAudioFramePosition(frame)
            while done < count {
                try file.read(into: buffer, frameCount: AVAudioFrameCount(min(count - done, Int(buffer.frameCapacity))))
                guard buffer.frameLength > 0, let channel = buffer.floatChannelData?[0] else { break }
                destination.advanced(by: done).update(from: channel, count: Int(buffer.frameLength))
                done += Int(buffer.frameLength)
            }
        } catch {
            throw AcousticDiarizationError.unreadableAudio(file: name, reason: error.localizedDescription)
        }
    }

    /// The whole timeline in memory (≈ 230 MB per hour); only for small inputs such as tests.
    func readAll() throws -> [Float] {
        var samples = [Float](repeating: 0, count: sampleCount)
        try samples.withUnsafeMutableBufferPointer { pointer in
            guard let base = pointer.baseAddress else { return }
            try copySamples(into: base, offset: 0, count: sampleCount)
        }
        return samples
    }

    /// Maps timeline segments to meeting offsets: clipped to each file, split where the file's time map jumps
    /// (capture gaps), and dropped where they only cover inserted silence.
    func meetingSegments(_ segments: [AcousticSegment], timeMaps: [AudioTimeMap]) -> [AcousticSegment] {
        let rate = AudioTimeMap.sampleRate
        var result: [AcousticSegment] = []
        for segment in segments where segment.end > segment.start {
            let lower = Int((segment.start * rate).rounded()), upper = Int((segment.end * rate).rounded())
            for (part, map) in zip(parts, timeMaps) where part.frameCount > 0 {
                let first = max(lower, part.start) - part.start, last = min(upper, part.start + part.frameCount) - part.start
                guard last > first else { continue }
                let cuts = [first] + map.anchors.map(\.frame).filter { $0 > first && $0 < last } + [last]
                for (from, to) in zip(cuts, cuts.dropFirst()) where to > from {
                    let start = map.offset(forFrame: from)
                    result.append(AcousticSegment(speaker: segment.speaker, start: start, end: start + Double(to - from) / rate))
                }
            }
        }
        return result
    }
}

/// pyannote community-1 offline pipeline (segmentation, WeSpeaker embeddings, PLDA + VBx clustering) via FluidAudio.
/// Models come from `FluidInference/speaker-diarization-coreml` at the revision FluidAudio pins
/// and are cached in `~/Library/Application Support/FluidAudio/Models/`.
struct FluidAudioDiarizer: AcousticDiarizer {
    /// Clustering distance threshold in [0, 2]; larger merges more and yields fewer speakers. nil = FluidAudio default (0.6).
    var clusteringThreshold: Double?
    /// Custom model cache directory (tests); nil = FluidAudio's default.
    var modelDirectory: URL?

    init(clusteringThreshold: Double? = nil, modelDirectory: URL? = nil) {
        self.clusteringThreshold = clusteringThreshold
        self.modelDirectory = modelDirectory
    }

    /// FluidAudio 0.17.7 offline pipeline with the pyannote community-1 models.
    var engine: String { "fluidaudio-community-1" }
    var modelVersion: String? { "\(Repo.diarizer.rawValue)@\(Repo.diarizer.revision)" }
    var threshold: Double? { clusteringThreshold ?? OfflineDiarizerConfig.default.clusteringThreshold }

    /// Downloads (first use) and compiles the models without diarizing; FluidAudio keeps them in its cache.
    static func prepareModels(directory: URL? = nil) async throws {
        try await prepare(OfflineDiarizerManager(), directory: directory)
    }

    private static func prepare(_ manager: OfflineDiarizerManager, directory: URL?) async throws {
        do { try await manager.prepareModels(directory: directory) }
        catch is CancellationError { throw CancellationError() }
        catch { throw AcousticDiarizationError.modelsUnavailable(error.localizedDescription) }
    }

    func diarize(samples: [Float], progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment] {
        try await diarize(source: ArrayAudioSampleSource(samples: samples), progress: progress)
    }

    /// Streams the timeline from the CAF files instead of materializing it (≈ 230 MB per hour of audio).
    func diarize(audio: ConcatenatedAudio, progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment] {
        try await diarize(source: audio, progress: progress)
    }

    private func diarize(source: AudioSampleSource, progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment] {
        var config = OfflineDiarizerConfig.default
        if let clusteringThreshold { config.clusteringThreshold = clusteringThreshold }
        let manager = OfflineDiarizerManager(config: config)
        try await Self.prepare(manager, directory: modelDirectory)
        try Task.checkCancellation()
        let result: DiarizationResult
        do {
            var callback: (@Sendable (Int, Int) -> Void)?
            if let progress {
                callback = { done, total in progress(total > 0 ? Double(done) / Double(total) : 0) }
            }
            result = try await manager.process(audioSource: source, audioLoadingSeconds: 0, progressCallback: callback)
        } catch OfflineDiarizationError.noSpeechDetected {
            return []
        } catch is CancellationError {
            throw CancellationError()
        } catch {
            throw AcousticDiarizationError.processingFailed(error.localizedDescription)
        }
        return result.segments.map {
            AcousticSegment(speaker: Self.clusterIndex($0.speakerId), start: Double($0.startTimeSeconds), end: Double($0.endTimeSeconds))
        }
    }

    /// FluidAudio names clusters "S1", "S2", …; the digits are kept (renumbered later), anything else becomes -1.
    static func clusterIndex(_ id: String) -> Int {
        Int(id.filter(\.isNumber)) ?? -1
    }
}

/// Deterministic stand-in for tests: every maximal run of non-zero samples is one segment, and its speaker is
/// the run's absolute amplitude rounded to tenths (0.1 -> 1, 0.2 -> 2, …).
struct FakeDiarizer: AcousticDiarizer {
    var engine: String { "fake" }
    var modelVersion: String? { nil }
    var threshold: Double? { nil }

    func diarize(samples: [Float], progress: (@Sendable (Double) -> Void)?) async throws -> [AcousticSegment] {
        var result: [AcousticSegment] = []
        var index = 0
        while index < samples.count {
            guard samples[index] != 0 else { index += 1; continue }
            let speaker = Int((abs(samples[index]) * 10).rounded())
            let start = index
            while index < samples.count, samples[index] != 0, Int((abs(samples[index]) * 10).rounded()) == speaker { index += 1 }
            result.append(AcousticSegment(speaker: speaker, start: Double(start) / AudioTimeMap.sampleRate,
                                          end: Double(index) / AudioTimeMap.sampleRate))
        }
        progress?(1)
        return result
    }
}
