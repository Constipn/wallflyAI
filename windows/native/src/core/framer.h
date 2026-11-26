// Framer: port of EchoFrameAssembler (EchoAudioProcessor.swift:227-247).
//
// Index n = p / 160 with p = round(t * 16000) on the session-wide grid; sample p lands at
// local offset p % 160 of frame n. Samples at negative positions are dropped (Swift:
// "guard position >= 0"). A frame is emitted when the index changes or when local 159 is
// written, so the head and the tail of a stream come out as PARTIAL frames carrying
// [valid_lo, valid_hi) instead of being trimmed to the grid - the Mac loses no audio at a
// stream boundary and neither do we.
//
// The Mac assembler records NO gaps: the timeline discontinuity of a packet is reported
// exactly once, by the layer that owns the packet timestamps (EchoAudioProcessor.append on
// the Mac, CaptureNormalizer here). The GapLog stays in the signature so the session layer
// keeps compiling; the framer only counts.
#pragma once
#include <cstdint>
#include <vector>

#include "core/types.h"

namespace wf {

class Framer {
public:
    struct Stats {
        uint64_t frames = 0;
        uint64_t trimmed_samples = 0;   // samples at negative positions, dropped
        uint64_t late_samples = 0;      // kept for the session layer; the Mac never drops here
        uint64_t skipped_frames = 0;    // frame indices never produced (gaps)
        uint64_t partial_frames = 0;    // frames emitted with valid_hi - valid_lo < 160
    };

    void configure(SourceKind source, int64_t origin_100ns, GapLog* gaps);
    void reset();

    // `pos` is the absolute 16 kHz timeline position of data[0]. Completed frames are
    // appended to `out`; the caller owns and reuses that vector.
    void push(int64_t pos, const float* data, size_t n, std::vector<Frame160>& out);
    // EchoFrameAssembler.finish(): hands out the trailing partial frame, once.
    void finish(std::vector<Frame160>& out);

    int64_t next_position() const { return next_pos_; }
    bool has_partial() const { return have_partial_; }
    const Stats& stats() const { return stats_; }

private:
    int64_t host_time_of(int64_t position) const {
        return origin_100ns_ + position * k::kTicksPerSample;
    }
    void start_partial(int64_t index);
    void emit(std::vector<Frame160>& out);

    SourceKind source_ = SourceKind::Me;
    int64_t origin_100ns_ = 0;
    GapLog* gaps_ = nullptr;
    int64_t next_pos_ = 0;         // position just after the last pushed sample
    int64_t partial_index_ = 0;    // frame currently being assembled
    int64_t last_index_ = 0;       // last emitted frame index
    bool    have_partial_ = false;
    bool    have_last_ = false;
    int     valid_lo_ = k::kFrameSamples;   // Swift: valid = 160..<160 (empty)
    int     valid_hi_ = 0;
    uint32_t pending_flags_ = 0;
    float   buf_[k::kFrameSamples] = {};
    Stats   stats_;
};

}  // namespace wf
