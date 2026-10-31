import AVFoundation
import WebRTCAEC
import os

/// WebRTC noise suppressor target levels: 6, 12, 18 and 21 dB.
enum NoiseSuppressionLevel: Int32 { case off, low, moderate, high, veryHigh }

final class EchoCanceller {
    private let handle: OpaquePointer
    private(set) var noiseSuppression = NoiseSuppressionLevel.off
    init(noiseSuppression: NoiseSuppressionLevel = .off) throws {
        guard let handle = mn_aec_create() else { throw MeetingError.message("Could not create echo filter.") }
        self.handle = handle
        if noiseSuppression != .off { try setNoiseSuppression(noiseSuppression) }
    }
    deinit { mn_aec_destroy(handle) }
    /// AEC3 framing + overlap-add: 128 samples (8ms) at 16kHz; the noise suppressor's
    /// 256-point analysis window adds a 96-sample overlap. Both verified by correlation tests and
    /// independent of the estimated acoustic speaker-to-microphone delay.
    var latencySamples: Int { 128 + (noiseSuppression == .off ? 0 : 96)}
    /// AEC3's own full-band ERLE estimate (dB) and render-to-capture delay (ms, 4-ms steps).
    func metrics() -> (erle: Double, delayMS: Int)? {
        var erle = 0.0, delay: Int32 = 0
        guard mn_aec_metrics(handle, &erle, &delay) == 1 else { return nil }
        return (erle, Int(delay))
    }
    /// Recreates only the noise suppressor; the converged echo filter is kept.
    func setNoiseSuppression(_ level: NoiseSuppressionLevel) throws {
        guard mn_aec_set_ns(handle, level.rawValue) == 1 else { throw MeetingError.message("Could not configure noise suppression.") }
        noiseSuppression = level
    }
    func reset() throws {
        guard mn_aec_reset(handle) == 1 else { throw MeetingError.message("Could not reset echo filter.") }
    }
    func render(_ samples: [Float]) throws {
        guard samples.count == 160, mn_aec_render(handle, samples) == 1 else { throw MeetingError.message("Invalid echo reference.") }
    }
    func capture(_ samples: [Float]) throws -> [Float] {
        var output = [Float](repeating: 0, count: 160)
        guard samples.count == 160, mn_aec_capture(handle, samples, &output) == 1 else { throw MeetingError.message("Echo cancellation failed.") }
        return output
    }
}

struct CapturePacket {
    let source: CaptureSource
    let generation: UInt64
    let buffer: AVAudioPCMBuffer
    let timestamp: Double
    var duration: Double { Double(buffer.frameLength) / buffer.format.sampleRate }
}

/// Hardware callbacks only copy PCM and place it here. No unbounded dispatch closures.
/// 240ms waiting + 240ms being drained + 490ms normalized + a 10ms fragment
/// stay below one second per source, including a concurrent callback batch.
final class CaptureInbox: @unchecked Sendable {
    private let lock = NSLock()
    private var packets: [CaptureSource: [CapturePacket]] = [:]
    private var generations: [CaptureSource: UInt64] = [:]
    private var arrivals: [CaptureSource: Double] = [:]
    private var ends: [CaptureSource: Double] = [:]
    private var losses: [CaptureSource: RecordingGap] = [:]
    func activate(_ source: CaptureSource, generation: UInt64) {
        lock.lock(); defer { lock.unlock() }
        generations[source] = generation; packets[source] = []; ends[source] = nil; arrivals[source] = ProcessInfo.processInfo.systemUptime
    }
    func isActive(_ source: CaptureSource, generation: UInt64) -> Bool {
        lock.lock(); defer { lock.unlock() }; return generations[source] == generation
    }
    func invalidate(_ source: CaptureSource) {
        lock.lock(); defer { lock.unlock() }
        generations[source] = nil
    }
    func offer(_ packet: CapturePacket) {
        lock.lock(); defer { lock.unlock() }
        guard generations[packet.source] == packet.generation,
              packet.buffer.frameLength > 0, packet.timestamp.isFinite,
              packet.duration.isFinite, packet.duration > 0 else { return }
        arrivals[packet.source] = ProcessInfo.processInfo.systemUptime
        ends[packet.source] = max(ends[packet.source] ?? packet.timestamp, packet.timestamp + packet.duration)
        var pending = packets[packet.source] ?? []
        pending.append(packet)
        var duration = pending.reduce(0) { $0 + $1.duration }
        while duration > 0.24, !pending.isEmpty {
            let lost = pending.removeFirst(); duration -= lost.duration
            if var gap = losses[packet.source] {
                gap.duration = max(gap.start + gap.duration, lost.timestamp + lost.duration) - gap.start
                losses[packet.source] = gap
            } else { losses[packet.source] = RecordingGap(start: lost.timestamp, duration: lost.duration, source: packet.source, reason: "Input buffer overflowed") }
        }
        packets[packet.source] = pending
    }
    func drain() -> ([CapturePacket], [RecordingGap]) {
        lock.lock(); defer { lock.unlock() }
        let result = packets.values.flatMap { $0 }.sorted { $0.timestamp < $1.timestamp }
        packets.removeAll(keepingCapacity: true)
        let gaps = Array(losses.values); losses.removeAll(keepingCapacity: true)
        return (result, gaps)
    }
    func lastAudioEnd(_ source: CaptureSource) -> Double? {
        lock.lock(); defer { lock.unlock() }; return ends[source] ?? arrivals[source]
    }
    func lastArrival(_ source: CaptureSource) -> Double? {
        lock.lock(); defer { lock.unlock() }; return arrivals[source]
    }
    func hasAudio(_ source: CaptureSource, generation: UInt64) -> Bool {
        lock.lock(); defer { lock.unlock() }
        return generations[source] == generation && ends[source] != nil
    }
}

/// A converter persists across callbacks. Output time advances by produced samples, never
/// by processing time. Clock discontinuities and format changes are explicit boundaries.
final class CaptureNormalizer {
    static let format = AVAudioFormat(commonFormat: .pcmFormatFloat32, sampleRate: 16_000, channels: 1, interleaved: false)!
    private var converter: AVAudioConverter?
    private var inputFormat: AVAudioFormat?
    private var expectedInput: Double?
    private var inputTimeline: Double?
    private var nextOutput: Double = 0
    private var expectedSamples: Double = 0
    private var producedSamples = 0
    /// Samples inserted or dropped to follow the host clock without a DSP boundary.
    private(set) var correctedSamples = 0
    func convert(_ buffer: AVAudioPCMBuffer, at timestamp: Double) throws -> (samples: [Float], time: Double, discontinuity: Bool, formatChanged: Bool) {
        let changed = inputFormat != nil && inputFormat != buffer.format
        // Compare capture clocks against consumed INPUT samples. AVAudioConverter
        // can retain more than 3ms internally (e.g. 4800-frame microphone callbacks);
        // produced output is therefore not a clock reference. Comparing it caused
        // a reset on every callback, discarded speech and prevented AEC convergence.
        // Only a callback step >3ms or accumulated drift >20ms rebuilds the converter;
        // smaller device-clock drift is re-anchored sample by sample below.
        let step = expectedInput.map { timestamp - $0 } ?? 0
        let drift = inputTimeline.map { timestamp - $0 } ?? 0
        let jumped = abs(step) > 0.003 || abs(drift) > 0.02
        let rebuilt = inputFormat != buffer.format || jumped
        if rebuilt {
            inputFormat = buffer.format
            converter = buffer.format == Self.format ? nil : AVAudioConverter(from: buffer.format, to: Self.format)
            // Normal priming reads ahead but preserves sample-zero timing. .none
            // inserts rate-dependent leading latency (notably at 8/24kHz), which
            // misaligns microphone/reference and shifts saved word timestamps.
            converter?.primeMethod = .normal
            // Downmix every channel, not just the first microphone channel.
            converter?.downmix = true
            nextOutput = timestamp
            inputTimeline = timestamp
            expectedSamples = 0; producedSamples = 0
        }
        if expectedInput == nil { nextOutput = timestamp; inputTimeline = timestamp }
        expectedSamples += Double(buffer.frameLength) * 16_000 / buffer.format.sampleRate
        expectedInput = timestamp + Double(buffer.frameLength) / buffer.format.sampleRate
        inputTimeline = (inputTimeline ?? timestamp) + Double(buffer.frameLength) / buffer.format.sampleRate
        let output: AVAudioPCMBuffer
        if buffer.format == Self.format { output = buffer }
        else {
            guard let converter else { throw MeetingError.message("Recording format is not supported.") }
            output = AVAudioPCMBuffer(pcmFormat: Self.format, frameCapacity: AVAudioFrameCount(ceil(Double(buffer.frameLength) * 16_000 / buffer.format.sampleRate)) + 64)!
            var consumed = false
            var error: NSError?
            let status = converter.convert(to: output, error: &error) { _, state in
                if consumed { state.pointee = .noDataNow; return nil }
                consumed = true; state.pointee = .haveData; return buffer
            }
            if let error { throw error }
            guard status != .error else { throw MeetingError.message("Audio conversion failed.") }
        }
        let count = Int(output.frameLength)
        producedSamples += count
        var samples = Array(UnsafeBufferPointer(start: output.floatChannelData![0], count: count)).map { $0.isFinite ? $0 : 0 }
        if !rebuilt, abs(drift) > 0.001, count > 0 {
            // Only the drift beyond a 1-ms jitter band is corrected, at most one sample per 10ms.
            // Steady clock drift thus moves each stream by single samples, which AEC3 tracks;
            // re-anchoring the whole millisecond at once made the echo path jump by up to 16 samples.
            // Output stays contiguous, so 10-ms AEC frames remain covered.
            let limit = max(1, count / 160), excess = (abs(drift) - 0.001) * 16_000 * (drift > 0 ? 1 : -1)
            samples = Self.stretch(samples, by: min(limit, max(-limit, Int(excess.rounded()))))
            let applied = samples.count - count
            inputTimeline! += Double(applied) / 16_000
            expectedSamples += Double(applied); producedSamples += applied; correctedSamples += abs(applied)
        }
        let time = nextOutput
        nextOutput += Double(samples.count) / 16_000
        return (samples, time, changed || jumped, changed)
    }
    /// Inserts (k > 0) interpolated or drops (k < 0) neighbour-averaged samples, spread evenly.
    static func stretch(_ samples: [Float], by k: Int) -> [Float] {
        let n = abs(k)
        guard n > 0, !samples.isEmpty else { return samples }
        var result: [Float] = []; result.reserveCapacity(samples.count + k)
        var next = 1
        for (i, sample) in samples.enumerated() {
            if next <= n && i == next * samples.count / (n + 1) {
                next += 1
                if k > 0 { result.append(((result.last ?? sample) + sample) / 2) }
                else { if let last = result.popLast() { result.append((last + sample) / 2) }; continue }
            }
            result.append(sample)
        }
        return result
    }
    func finish() throws -> (samples: [Float], time: Double) {
        let time = nextOutput
        guard let converter else { return ([], time) }
        let remaining = max(0, Int(expectedSamples.rounded()) - producedSamples)
        guard remaining > 0 else { self.converter = nil; return ([], time) }
        let output = AVAudioPCMBuffer(pcmFormat: Self.format, frameCapacity: AVAudioFrameCount(remaining + 64))!
        var error: NSError?
        let status = converter.convert(to: output, error: &error) { _, state in state.pointee = .endOfStream; return nil }
        self.converter = nil
        if let error { throw error }
        guard status != .error else { throw MeetingError.message("Audio converter could not finish.") }
        let count = min(remaining, Int(output.frameLength))
        producedSamples += count; nextOutput += Double(count)/16_000
        return (Array(UnsafeBufferPointer(start: output.floatChannelData![0], count: count)), time)
    }
}

struct EchoFrame {
    var index: Int
    var samples = [Float](repeating: 0, count: 160)
    var valid = 160..<160
    var time: Double { Double(index * 160 + valid.lowerBound) / 16_000 }
    var duration: Double { Double(valid.count) / 16_000 }
}

struct EchoFrameAssembler {
    private(set) var partial: EchoFrame?
    mutating func append(_ samples: [Float], at time: Double) -> [EchoFrame] {
        var result: [EchoFrame] = []
        let first = Int((time * 16_000).rounded())
        for (n, sample) in samples.enumerated() {
            let position = first + n
            guard position >= 0 else { continue }
            let frameIndex = position / 160, local = position % 160
            if partial?.index != frameIndex {
                if let partial { result.append(partial) }
                partial = EchoFrame(index: frameIndex)
            }
            partial!.samples[local] = sample
            partial!.valid = min(partial!.valid.lowerBound, local)..<(local + 1)
            if local == 159 { result.append(partial!); partial = nil }
        }
        return result
    }
    mutating func finish() -> EchoFrame? { defer { partial = nil }; return partial }
}

/// All methods run on the owning capture DSP queue; no hardware or file I/O here.
final class EchoAudioProcessor: @unchecked Sendable {
    private let aec: EchoCanceller
    private let globalIsMeeting: Bool
    private let onAudio: (Speaker, AVAudioPCMBuffer, Double) -> Void
    private let onGap: (RecordingGap) -> Void
    private var normalizers: [CaptureSource: CaptureNormalizer] = [:]
    private var assemblers: [CaptureSource: EchoFrameAssembler] = [:]
    private var frames: [CaptureSource: [Int: EchoFrame]] = [:]
    private var lastPacketEnd: [CaptureSource: Double] = [:]
    private var lastMicrophone = -1
    private var lastRender = -1
    private var finished = false
    // Timeline frame of the last fed capture frames. Output sample k of fed frame n is fed
    // sample n*160 + k - latency, so microphone holes never shift the cleaned audio.
    private var fedFrames: [Int] = []
    private var fedCount = 0
    private var pendingMicrophone: [Range<Int>] = []
    private var diagnostics = AudioDiagnostics()
    /// Microphone frames passed through the canceller without playback reference (not persisted).
    private(set) var unfilteredFrames = 0
    private(set) var aecResets = 0
    var latencySamples: Int { aec.latencySamples }
    init(globalIsMeeting: Bool, noiseSuppression: NoiseSuppressionLevel = .moderate, onAudio: @escaping (Speaker, AVAudioPCMBuffer, Double) -> Void,
         onGap: @escaping (RecordingGap) -> Void) throws {
        aec = try EchoCanceller(noiseSuppression: noiseSuppression); self.globalIsMeeting = globalIsMeeting
        self.onAudio = onAudio; self.onGap = onGap
    }
    func append(_ packet: CapturePacket, origin: Double, now: Double, processImmediately: Bool = true) throws {
        guard !finished else { return }
        let offset = packet.timestamp - origin
        if let expected = lastPacketEnd[packet.source] {
            if offset < expected - 0.003 {
                onGap(RecordingGap(start: max(0, offset), duration: packet.duration, source: packet.source, reason: "Late audio packet discarded"))
                return
            }
            if offset > expected + 0.003, packet.source != .meeting {
                onGap(RecordingGap(start: max(0, expected), duration: offset - expected, source: packet.source, reason: "Audio timestamps interrupted"))
            }
        }
        lastPacketEnd[packet.source] = offset + packet.duration
        let normalizer = normalizers[packet.source] ?? CaptureNormalizer()
        normalizers[packet.source] = normalizer
        let corrected = normalizer.correctedSamples
        let converted = try normalizer.convert(packet.buffer, at: packet.timestamp - origin)
        diagnostics.correctedSamples += normalizer.correctedSamples - corrected
        // A new clock anchor neither discards audio nor the converged echo path. Only another
        // microphone format (usually another device/acoustic path) restarts the canceller.
        if converted.discontinuity { diagnostics.timestampJumps += 1 }
        if converted.formatChanged && packet.source == .microphone {
            try flushEchoTail(); discardDelayedMicrophone(reason: "Audio format changed"); try resetEchoCanceller()
        }
        let ready = assemblers[packet.source, default: EchoFrameAssembler()].append(converted.samples, at: converted.time)
        for frame in ready { insert(frame, source: packet.source) }
        if processImmediately { try process(now: now - origin) }
    }
    private func insert(_ frame: EchoFrame, source: CaptureSource) {
        if source == .meeting || (source == .reference && globalIsMeeting) { emit(frame.samples, range: frame.valid, at: frame.time, speaker: .others) }
        guard source != .meeting else { return }
        frames[source, default: [:]][frame.index] = frame
        while frames[source, default: [:]].count > 49 {
            let oldest = frames[source]!.keys.min()!
            let discarded = frames[source]!.removeValue(forKey: oldest)!
            gap(discarded, source: source, reason: "Processing buffer overflowed")
        }
        diagnostics.maximumBufferedFrames = max(diagnostics.maximumBufferedFrames, frames[source, default: [:]].count)
    }
    func accountInputLoss(_ gap: RecordingGap) {
        diagnostics.discardedSamples += max(0, Int((gap.duration * 16_000).rounded()))
        // Already reported by the inbox; the next packet must not report the same hole again.
        let end = gap.start + gap.duration
        lastPacketEnd[gap.source] = max(lastPacketEnd[gap.source] ?? end, end)
    }
    func process(now: Double, draining: Bool = false) throws {
        for index in frames[.microphone, default: [:]].keys.sorted() {
            guard let mic = frames[.microphone]?[index] else { continue }
            if index <= lastMicrophone { frames[.microphone]?[index] = nil; gap(mic, source: .microphone, reason: "Late microphone packet"); continue }
            let reference = frames[.reference]?[index]
            let covered = reference.map { $0.valid.lowerBound <= mic.valid.lowerBound && $0.valid.upperBound >= mic.valid.upperBound } ?? false
            if !covered && !draining && now < mic.time + mic.duration + 0.1 { break }
            frames[.microphone]?[index] = nil
            // A microphone hole (gap recorded on arrival) keeps the converged filter. Skipping the
            // matching playback keeps render and capture in step.
            if lastMicrophone >= 0 && index != lastMicrophone + 1 { lastRender = max(lastRender, index - 1) }
            // Catch up render history before capture; never feed future playback.
            var played = [Float](repeating: 0, count: 160)
            for key in frames[.reference, default: [:]].keys.sorted() where key <= index {
                let render = frames[.reference]!.removeValue(forKey: key)!
                if key > lastRender { try aec.render(render.samples); lastRender = key; if key == index { played = render.samples } }
            }
            // Reference still (partly) missing after the wait: the microphone passes through the
            // canceller with silence as playback instead of becoming a gap or resetting the filter.
            if !covered { unfilteredFrames += 1; diagnostics.unfilteredFrames += 1 }
            if lastRender < index { try aec.render(played); lastRender = index }
            pendingMicrophone.append((index * 160 + mic.valid.lowerBound)..<(index * 160 + mic.valid.upperBound))
            let output = try feed(mic.samples, index: index)
            diagnostics.observe(mic.samples, output, played)
            lastMicrophone = index
        }
        // Stale reference data can never be replayed after a microphone outage.
        let cutoff = Int((now - 0.5) * 100)
        frames[.reference] = frames[.reference, default: [:]].filter { $0.key >= cutoff }
        // Once per second: a lagging (non-causal) or jumping reference shows up as delay range.
        if now - diagnostics.lastMetrics >= 1, let metrics = aec.metrics() { diagnostics.observe(metrics, at: now) }
        diagnostics.reportIfDue(now: now)
    }
    /// A playback-tap restart keeps the converged filter (the acoustic path is unchanged);
    /// only a microphone change resets the canceller.
    func reset(_ source: CaptureSource, at time: Double, reason: String) throws {
        if let partial = assemblers[source]?.finish() { gap(partial, source: source, reason: reason) }
        for frame in frames[source, default: [:]].values { gap(frame, source: source, reason: reason) }
        frames[source] = [:]
        normalizers[source] = nil
        guard source == .microphone else { return }
        frames[.reference] = [:]
        discardDelayedMicrophone(reason: reason)
        try resetEchoCanceller(); lastRender = -1
    }
    func finish(now: Double) throws {
        guard !finished else { return }
        for source in CaptureSource.allCases {
            if let tail = try normalizers[source]?.finish() {
                let ready = assemblers[source, default: EchoFrameAssembler()].append(tail.samples, at: tail.time)
                for frame in ready { insert(frame, source: source) }
            }
            if let frame = assemblers[source]?.finish() { insert(frame, source: source) }
        }
        try process(now: now, draining: true)
        try flushEchoTail()
        finished = true
        frames.removeAll(); normalizers.removeAll()
    }
    private func resetEchoCanceller() throws {
        try aec.reset(); aecResets += 1; diagnostics.aecResets += 1
        fedFrames.removeAll(); fedCount = 0
    }
    /// Feeds digital silence until every processed microphone sample has passed the DSP latency.
    private func flushEchoTail() throws {
        guard !pendingMicrophone.isEmpty else { return }
        let zero = [Float](repeating: 0, count: 160)
        for n in 1...(latencySamples + 159) / 160 { try aec.render(zero); _ = try feed(zero, index: lastMicrophone + n) }
    }
    private func feed(_ samples: [Float], index: Int) throws -> [Float] {
        let output = try aec.capture(samples)
        let latency = latencySamples, span = (latency + 159) / 160
        fedFrames.append(index); if fedFrames.count > span + 1 { fedFrames.removeFirst(fedFrames.count - span - 1) }
        let start = fedCount * 160 - latency
        for fed in max(0, fedCount - span)...fedCount {
            let lower = max(fed * 160, start), upper = min(fed * 160 + 160, start + 160)
            guard lower < upper else { continue }
            let frame = fedFrames[fedFrames.count - 1 - (fedCount - fed)]
            emitClean(Array(output[(lower - start)..<(upper - start)]), firstSample: frame * 160 + lower - fed * 160)
        }
        fedCount += 1
        return output
    }
    private func emitClean(_ output: [Float], firstSample: Int) {
        let end = firstSample + output.count
        var pending: [Range<Int>] = []
        for range in pendingMicrophone {
            let lower = max(range.lowerBound, firstSample), upper = min(range.upperBound, end)
            if lower < upper { emit(output, range: (lower - firstSample)..<(upper - firstSample), at: Double(lower)/16_000, speaker: .me) }
            if range.upperBound > end { pending.append(max(range.lowerBound, end)..<range.upperBound) }
        }
        pendingMicrophone = pending
    }
    private func discardDelayedMicrophone(reason: String) {
        for range in pendingMicrophone {
            onGap(RecordingGap(start: Double(range.lowerBound)/16_000, duration: Double(range.count)/16_000, source: .microphone, reason: reason))
        }
        pendingMicrophone.removeAll()
    }
    private func emit(_ samples: [Float], range: Range<Int>, at time: Double, speaker: Speaker) {
        guard !range.isEmpty else { return }
        let buffer = AVAudioPCMBuffer(pcmFormat: CaptureNormalizer.format, frameCapacity: AVAudioFrameCount(range.count))!
        buffer.frameLength = buffer.frameCapacity
        for (n, i) in range.enumerated() { buffer.floatChannelData![0][n] = samples[i] }
        onAudio(speaker, buffer, max(0, time))
    }
    private func gap(_ frame: EchoFrame, source: CaptureSource, reason: String) {
        diagnostics.discardedSamples += frame.valid.count
        onGap(RecordingGap(start: frame.time, duration: frame.duration, source: source, reason: reason))
    }
}

private struct AudioDiagnostics {
    private static let logger = Logger(subsystem: "io.github.constipn.wallfly", category: "audio-summary")
    var timestampJumps = 0, discardedSamples = 0, maximumBufferedFrames = 0
    var unfilteredFrames = 0, correctedSamples = 0, aecResets = 0
    private(set) var lastMetrics = 0.0
    private var erle = Double.nan, delay = 0, minimumDelay = Int.max, maximumDelay = Int.min
    private var before = 0.0, after = 0.0, reference = 0.0
    private var count = 0, clipping = 0
    private var lastReport = 0.0
    mutating func observe(_ mic: [Float], _ clean: [Float], _ render: [Float]) {
        for i in mic.indices {
            before += Double(mic[i] * mic[i]); after += Double(clean[i] * clean[i]); reference += Double(render[i] * render[i])
            if abs(mic[i]) >= 0.999 { clipping += 1 }
        }
        count += mic.count
    }
    mutating func observe(_ metrics: (erle: Double, delayMS: Int), at now: Double) {
        erle = metrics.erle; delay = metrics.delayMS; lastMetrics = now
        minimumDelay = min(minimumDelay, delay); maximumDelay = max(maximumDelay, delay)
    }
    mutating func reportIfDue(now: Double) {
        guard now - lastReport >= 30 else { return }
        let n = Double(max(1, count))
        let snapshot = self
        Self.logger.info("AEC 30s rmsIn=\(sqrt(snapshot.before/n)) rmsOut=\(sqrt(snapshot.after/n)) rmsRef=\(sqrt(snapshot.reference/n)) clipping=\(snapshot.clipping) maxFrames=\(snapshot.maximumBufferedFrames) discarded=\(snapshot.discardedSamples) timestampJumps=\(snapshot.timestampJumps) unfilteredFrames=\(snapshot.unfilteredFrames) correctedSamples=\(snapshot.correctedSamples) aecResets=\(snapshot.aecResets) aecERLE=\(snapshot.erle) delayMs=\(snapshot.delay) delayRangeMs=\(snapshot.minimumDelay == Int.max ? 0 : snapshot.minimumDelay)...\(snapshot.maximumDelay == Int.min ? 0 : snapshot.maximumDelay)")
        self = AudioDiagnostics(); lastReport = now; lastMetrics = snapshot.lastMetrics
    }
}
