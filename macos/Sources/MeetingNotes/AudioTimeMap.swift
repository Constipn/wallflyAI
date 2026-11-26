import Foundation

/// Pure mapping between sample positions in one saved 16 kHz CAF file and meeting offsets.
/// The archive omits capture gaps, so time advances linearly only between anchors.
struct AudioTimeMap: Equatable {
    static let sampleRate = 16_000.0
    /// Sorted by frame; at most one anchor per frame (the last one reported wins).
    let anchors: [AudioAnchor]
    /// Optional file length; when set, offsets past the end of the file map to nil.
    let frameCount: Int?

    init(anchors: [AudioAnchor], frameCount: Int? = nil) {
        var sorted: [AudioAnchor] = []
        for anchor in anchors.enumerated().sorted(by: { ($0.element.frame, $0.offset) < ($1.element.frame, $1.offset) }).map(\.element) {
            if sorted.last?.frame == anchor.frame { sorted[sorted.count - 1] = anchor } else { sorted.append(anchor) }
        }
        self.anchors = sorted
        self.frameCount = frameCount
    }

    /// Anchors of one file out of `Meeting.audioAnchors`.
    init(anchors: [AudioAnchor], file: String, frameCount: Int? = nil) {
        self.init(anchors: anchors.filter { $0.file == file }, frameCount: frameCount)
    }

    /// Fallback for meetings saved before anchors existed: the file starts at `fileStart`
    /// (0 for the first recording attempt) and every capture gap of `source` shifts later
    /// frames by its duration. Overlapping gaps count once, as in `recordingGapDuration`.
    init(recordingGaps: [RecordingGap], source: CaptureSource, file: String = "",
         fileStart: TimeInterval = 0, frameCount: Int? = nil) {
        var anchors = [AudioAnchor(file: file, frame: 0, offset: fileStart)]
        var frame = 0, offset = fileStart
        for gap in recordingGaps.filter({ $0.source == source && $0.duration > 0 }).sorted(by: { $0.start < $1.start }) {
            let end = gap.start + gap.duration
            guard end > offset else { continue }
            let start = max(gap.start, offset)
            frame += Int(((start - offset) * Self.sampleRate).rounded())
            offset = end
            if anchors.last?.frame == frame { anchors.removeLast() }
            anchors.append(AudioAnchor(file: file, frame: frame, offset: offset))
        }
        self.init(anchors: anchors, frameCount: frameCount)
    }

    /// Meeting offset of `frame`. Frames before the first anchor extrapolate backwards from it;
    /// without anchors the file is assumed to start at offset 0.
    func offset(forFrame frame: Int) -> TimeInterval {
        guard let first = anchors.first else { return Double(frame) / Self.sampleRate }
        let anchor = anchors.last(where: { $0.frame <= frame }) ?? first
        return anchor.offset + Double(frame - anchor.frame) / Self.sampleRate
    }

    /// Frame recorded at meeting `offset`, or nil when no audio was written for that time
    /// (before the file, inside a gap, or past `frameCount`). If a backwards timestamp jump
    /// recorded the same offset twice, the earliest frame in the file is returned.
    func frame(forOffset offset: TimeInterval) -> Int? {
        guard offset.isFinite else { return nil }
        let anchors = self.anchors.isEmpty ? [AudioAnchor(file: "", frame: 0, offset: 0)] : self.anchors
        for (index, anchor) in anchors.enumerated() {
            let limit = index + 1 < anchors.count ? anchors[index + 1].frame : frameCount
            guard offset >= anchor.offset - 1e-9,
                  let relative = Self.frame((offset - anchor.offset) * Self.sampleRate, limit: limit.map { $0 - anchor.frame })
            else { continue }
            return anchor.frame + relative
        }
        return nil
    }

    /// Floor with a small tolerance so `frame(forOffset: offset(forFrame: f)) == f` despite rounding.
    private static func frame(_ samples: Double, limit: Int?) -> Int? {
        let value = Int(max(0, (samples + 1e-4).rounded(.down)))
        if let limit, value >= limit { return nil }
        return value
    }
}
