// Shared value types of the portable DSP core: sources, 160-sample frames, WASAPI
// buffer flags and the gap log that every stage writes its RecordingGap entries into.
// These stay C++-internal; the C ABI in src/api mirrors them later.
#pragma once
#include <cstdint>
#include <vector>

#include "core/constants.h"

namespace wf {

enum class SourceKind : uint8_t { Me = 0, Others = 1 };

// Spec 3.6 "RecordingGap {start, duration, source, reason}" plus the reasons listed
// in the implementation plan (Event-Arten / Gap).
enum class GapReason : uint8_t {
    InboxOverflow = 0,          // "Input buffer overflowed" (spec 3.5)
    TimestampDiscontinuity = 1, // host clock jump / AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY
    LateFrame = 2,              // late or duplicate frame dropped (spec 3.3 step 4)
    DeviceLost = 3,
    Restart = 4,
    WatchdogRecovery = 5,
};

struct Gap {
    SourceKind source = SourceKind::Me;
    GapReason  reason = GapReason::InboxOverflow;
    int64_t    start_100ns = 0;   // host clock, same domain as the packet timestamps
    double     duration_sec = 0.0;
};

// Collector for gaps. Reserved once at setup so the per-frame path never allocates.
class GapLog {
public:
    void reserve(size_t n) { items_.reserve(n); }
    void add(SourceKind s, GapReason r, int64_t start_100ns, double duration_sec) {
        items_.push_back(Gap{s, r, start_100ns, duration_sec < 0.0 ? 0.0 : duration_sec});
    }
    const std::vector<Gap>& items() const { return items_; }
    std::vector<Gap>& items() { return items_; }
    size_t size() const { return items_.size(); }
    void clear() { items_.clear(); }
private:
    std::vector<Gap> items_;
};

// Frame flags, mirroring WfFrame.flags of the planned C ABI.
inline constexpr uint32_t kFrameUnfiltered        = 0x1u;  // processed with a zero reference
inline constexpr uint32_t kFrameFirstAfterGap     = 0x2u;
inline constexpr uint32_t kFrameSyntheticSilence  = 0x4u;  // generated, not captured

// One 10 ms frame on the common 16 kHz timeline (spec 3.3 step 1).
//
// [valid_lo, valid_hi) mirrors EchoFrame.valid of EchoAudioProcessor.swift: a stream that
// starts, resumes or ends in the middle of a frame produces a PARTIAL frame whose samples
// outside the valid range are zero and must not be archived. The Mac code never trims a
// stream to the 160 grid, so the first and the last fragment of every stream survive.
// Defaults describe a complete frame, so every consumer that ignores the range keeps
// working - it then writes at most 159 zero samples at a stream boundary.
struct Frame160 {
    int64_t  index = 0;        // absolute frame index; sample position = index * 160
    int64_t  host_100ns = 0;   // host time of the frame's first sample (not of valid_lo)
    uint32_t flags = 0;
    uint16_t valid_lo = 0;                        // first carrying sample
    uint16_t valid_hi = k::kFrameSamples;         // one past the last carrying sample
    float    s[k::kFrameSamples] = {};
};

// Mac EchoFrame.valid.count / .time / .duration.
inline int valid_count(const Frame160& f) {
    return f.valid_hi > f.valid_lo ? static_cast<int>(f.valid_hi) - static_cast<int>(f.valid_lo) : 0;
}
inline double valid_seconds(const Frame160& f) {
    return static_cast<double>(valid_count(f)) / k::kSampleRate;
}
// Host time of the frame's first CARRYING sample - what the Mac records as gap start.
inline int64_t valid_start_100ns(const Frame160& f) {
    return f.host_100ns + static_cast<int64_t>(f.valid_lo) * k::kTicksPerSample;
}

// AUDCLNT_BUFFERFLAGS_* as passed through from the capture thread (winapi values).
inline constexpr uint32_t kBufDataDiscontinuity = 0x1u;
inline constexpr uint32_t kBufSilent            = 0x2u;
inline constexpr uint32_t kBufTimestampError    = 0x4u;

}  // namespace wf
