// Meeting timeline and audio archive anchors (spec 3.6). Meeting time = packet time -
// origin + baseDuration; gaps are never written as silence, instead the archive carries
// anchors {frame, offset} and a new one is created once the prediction is off by > 1.5 samples.
#pragma once
#include <algorithm>
#include <cmath>
#include <cstdint>
#include <vector>

#include "core/constants.h"
#include "core/types.h"

namespace wf {

class Timeline {
public:
    Timeline() = default;
    Timeline(int64_t origin_100ns, double base_duration_sec)
        : origin_100ns_(origin_100ns), base_duration_sec_(base_duration_sec) {}

    void set_origin(int64_t origin_100ns) { origin_100ns_ = origin_100ns; }
    void set_base_duration(double sec) { base_duration_sec_ = sec; }
    int64_t origin_100ns() const { return origin_100ns_; }
    double base_duration_sec() const { return base_duration_sec_; }

    // Meeting offset of a host timestamp.
    double offset_of(int64_t host_100ns) const {
        return static_cast<double>(host_100ns - origin_100ns_) / k::kTicksPerSecond +
               base_duration_sec_;
    }
    // Meeting offset of a 16 kHz sample position on a stream anchored at stream_origin.
    double offset_of_position(int64_t position, int64_t stream_origin_100ns) const {
        return static_cast<double>(stream_origin_100ns - origin_100ns_) / k::kTicksPerSecond +
               static_cast<double>(position) / k::kSampleRate + base_duration_sec_;
    }
    double offset_of_frame(int64_t frame_index, int64_t stream_origin_100ns) const {
        return offset_of_position(frame_index * k::kFrameSamples, stream_origin_100ns);
    }

private:
    int64_t origin_100ns_ = 0;
    double  base_duration_sec_ = 0.0;
};

// Spec 3.6: "Offset(f) = Anker.Offset + (f - Anker.Frame) / 16000". Mirrors AudioAnchor
// of AudioCaptureTypes.swift; `file` tags the archive file the anchor belongs to.
struct AudioAnchor {
    int64_t frame = 0;    // sample frame index inside the archive file
    double  offset = 0.0; // meeting offset in seconds
    int     file = 0;     // archive file tag (Mac: the CAF lastPathComponent)
};

inline double predict_offset(const AudioAnchor& a, int64_t frame) {
    return a.offset + static_cast<double>(frame - a.frame) / k::kSampleRate;
}

// Mirrors what the .NET AudioTimeMap writes: a new anchor whenever the predicted offset
// deviates from the real one by more than 1.5 samples.
class AnchorTracker {
public:
    void reset() { have_ = false; }
    bool has_anchor() const { return have_; }
    const AudioAnchor& anchor() const { return anchor_; }

    // Returns true (and fills `out`) when this (frame, offset) pair needs a new anchor.
    bool note(int64_t frame, double offset, AudioAnchor* out = nullptr) {
        if (have_ && std::fabs(offset - predict_offset(anchor_, frame)) <= k::kAnchorThresholdSeconds) {
            return false;
        }
        anchor_ = AudioAnchor{frame, offset};
        have_ = true;
        if (out) *out = anchor_;
        return true;
    }

private:
    AudioAnchor anchor_;
    bool have_ = false;
};

// Port of AudioTimeMap.swift: the pure mapping between sample positions in ONE saved
// 16 kHz archive file and meeting offsets. The archive omits capture gaps, so time advances
// linearly only between anchors.
class AudioTimeMap {
public:
    AudioTimeMap() = default;
    // Anchors are sorted by frame; for the same frame the LAST one in input order wins.
    // frame_count < 0 means "unknown", so offsets past the end of the file still map.
    explicit AudioTimeMap(std::vector<AudioAnchor> anchors, int64_t frame_count = -1)
        : frame_count_(frame_count) {
        std::stable_sort(anchors.begin(), anchors.end(),
                         [](const AudioAnchor& a, const AudioAnchor& b) { return a.frame < b.frame; });
        for (const AudioAnchor& a : anchors) {
            if (!anchors_.empty() && anchors_.back().frame == a.frame) anchors_.back() = a;
            else anchors_.push_back(a);
        }
    }
    // Anchors of one file out of the meeting's whole anchor list.
    static AudioTimeMap for_file(const std::vector<AudioAnchor>& all, int file,
                                 int64_t frame_count = -1) {
        std::vector<AudioAnchor> mine;
        for (const AudioAnchor& a : all) {
            if (a.file == file) mine.push_back(a);
        }
        return AudioTimeMap(std::move(mine), frame_count);
    }

    // One recording gap, in meeting time (Mac RecordingGap{start, duration, source}).
    struct GapSpan {
        double     start = 0.0;
        double     duration = 0.0;
        SourceKind source = SourceKind::Me;
    };
    // Fallback for archives saved without anchors: the file starts at `file_start` and
    // every capture gap of `source` shifts later frames by its duration. Overlapping gaps
    // count once.
    static AudioTimeMap from_gaps(std::vector<GapSpan> gaps, SourceKind source, int file = 0,
                                  double file_start = 0.0, int64_t frame_count = -1) {
        std::stable_sort(gaps.begin(), gaps.end(),
                         [](const GapSpan& a, const GapSpan& b) { return a.start < b.start; });
        std::vector<AudioAnchor> anchors;
        anchors.push_back(AudioAnchor{0, file_start, file});
        int64_t frame = 0;
        double offset = file_start;
        for (const GapSpan& g : gaps) {
            if (g.source != source || !(g.duration > 0.0)) continue;
            const double end = g.start + g.duration;
            if (!(end > offset)) continue;
            const double start = g.start > offset ? g.start : offset;
            frame += static_cast<int64_t>(std::llround((start - offset) * k::kSampleRate));
            offset = end;
            if (!anchors.empty() && anchors.back().frame == frame) anchors.pop_back();
            anchors.push_back(AudioAnchor{frame, offset, file});
        }
        return AudioTimeMap(std::move(anchors), frame_count);
    }

    const std::vector<AudioAnchor>& anchors() const { return anchors_; }
    int64_t frame_count() const { return frame_count_; }

    // Meeting offset of `frame`. Frames before the first anchor extrapolate backwards from
    // it; without anchors the file is assumed to start at offset 0.
    double offset_for_frame(int64_t frame) const {
        if (anchors_.empty()) return static_cast<double>(frame) / k::kSampleRate;
        const AudioAnchor* best = &anchors_.front();
        for (const AudioAnchor& a : anchors_) {
            if (a.frame > frame) break;
            best = &a;
        }
        return best->offset + static_cast<double>(frame - best->frame) / k::kSampleRate;
    }

    // Frame recorded at meeting `offset`, or false when no audio was written for that time
    // (before the file, inside a gap, or past frame_count). If a backwards timestamp jump
    // recorded the same offset twice, the earliest frame in the file is returned.
    bool frame_for_offset(double offset, int64_t* out) const {
        if (!std::isfinite(offset)) return false;
        const AudioAnchor zero{};
        const size_t n = anchors_.empty() ? 1u : anchors_.size();
        for (size_t i = 0; i < n; ++i) {
            const AudioAnchor& a = anchors_.empty() ? zero : anchors_[i];
            bool has_limit = false;
            int64_t limit = 0;
            if (!anchors_.empty() && i + 1 < anchors_.size()) {
                limit = anchors_[i + 1].frame - a.frame;
                has_limit = true;
            } else if (frame_count_ >= 0) {
                limit = frame_count_ - a.frame;
                has_limit = true;
            }
            if (offset < a.offset - 1e-9) continue;
            // Floor with a small tolerance so frame(offset(f)) == f despite rounding.
            double value = std::floor((offset - a.offset) * k::kSampleRate + 1e-4);
            if (value < 0.0) value = 0.0;
            const int64_t relative = static_cast<int64_t>(value);
            if (has_limit && relative >= limit) continue;
            if (out) *out = a.frame + relative;
            return true;
        }
        return false;
    }

private:
    std::vector<AudioAnchor> anchors_;
    int64_t frame_count_ = -1;   // < 0: unknown
};

}  // namespace wf
