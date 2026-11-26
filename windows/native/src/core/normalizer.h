// CaptureNormalizer: port of CaptureNormalizer (EchoAudioProcessor.swift:114-217) plus the
// packet-level checks of EchoAudioProcessor.append (lines 277-289). Packets at any rate in,
// a continuous 16 kHz sample timeline anchored to the host clock out.
//
// Mac parity notes:
//  * drift beyond the 1 ms jitter band is corrected by round((|err| - 1 ms) * 16000)
//    samples, clamped to max(1, produced/160) per packet - "at most one sample per 10 ms
//    OF AUDIO", not per 10 ms of wall time. The applied samples feed straight back into the
//    error, which is what rate-limits a steady clock offset.
//  * a packet starting more than 3 ms BEFORE the predicted continuation is discarded whole
//    (Swift: "Late audio packet discarded"); it never reaches the resampler.
//  * the Mac measures the drift against consumed INPUT samples because AVAudioConverter can
//    retain several thousand frames. Our IResampler is push based and returns everything it
//    produced, so the produced-sample timeline differs from the consumed-input timeline by
//    less than one sample; the resampler's own algorithmic delay is compensated explicitly.
//  * AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR / _DATA_DISCONTINUITY have no macOS counterpart;
//    they are Windows-only inputs to the same re-anchor decision.
#pragma once
#include <cstdint>
#include <memory>
#include <vector>

#include "core/inbox.h"
#include "core/resampler.h"
#include "core/types.h"

namespace wf {

class CaptureNormalizer {
public:
    struct Config {
        SourceKind source = SourceKind::Me;
        // Spec 3.2: an app-specific reference may fall silent, so it is exempt from the
        // discontinuity gaps and from the watchdog expectation.
        bool app_source = false;
    };

    struct Stats {
        uint64_t packets = 0;
        uint64_t inserted_samples = 0;
        uint64_t removed_samples = 0;
        uint64_t reanchors = 0;
        uint64_t timestamp_errors = 0;
        uint64_t discontinuities = 0;
        uint64_t late_packets = 0;      // discarded whole, > 3 ms before the predicted end
        uint64_t consumed_frames = 0;   // input frames since the first anchor
        double   last_error_sec = 0.0;  // host time minus assigned timeline time
        // Measured device clock vs. host clock: (host time spanned - nominal duration of the
        // consumed frames) / host time spanned, summed over the anchored segments only. A
        // hole between segments (re-anchor after a stall or jump, restart, inbox overflow)
        // is lost audio, not clock drift; counting it from the first packet, as before
        // 2026-11-01, turned a 0.1 s hole in a 5 s run into "20000 ppm".
        double   drift_ppm = 0.0;
    };

    void configure(const Config& cfg, GapLog* gaps, ResamplerFactory factory = {});
    void reset();   // forget the anchor; the next packet re-anchors (keeps the origin)

    // Appends the normalized samples for one packet to `out` (cleared first) and returns
    // the absolute timeline position of out[0]. `discontinuity` reports a re-anchor.
    // The caller owns and reuses `out`; reserve it once so this path never allocates.
    int64_t process(const InputPacket& p, std::vector<float>& out, bool* discontinuity = nullptr);

    bool anchored() const { return anchored_; }
    bool has_origin() const { return have_origin_; }
    int64_t origin_100ns() const { return origin_100ns_; }     // host time of position 0
    int64_t next_position() const { return next_pos_; }
    int64_t host_time_of(int64_t position) const {
        return origin_100ns_ + position * k::kTicksPerSample;
    }
    const Stats& stats() const { return stats_; }

    // CaptureNormalizer.stretch: inserts (k > 0) interpolated or drops (k < 0)
    // neighbour-averaged samples, spread evenly over the block. Exposed for the tests.
    static void stretch(const std::vector<float>& in, int k, std::vector<float>& out);

private:
    void rebuild(double in_rate);
    // `covered_100ns`: InputPacket::hole_reported_100ns of the packet that re-anchors.
    void anchor_to(int64_t content_ts, bool emit_gap, int64_t covered_100ns = 0);

    Config cfg_;
    GapLog* gaps_ = nullptr;
    ResamplerFactory factory_;
    std::unique_ptr<IResampler> resampler_;

    uint32_t rate_ = 0;
    bool  have_origin_ = false;
    bool  anchored_ = false;
    int64_t origin_100ns_ = 0;      // host time of timeline position 0
    int64_t next_pos_ = 0;          // position of the next sample to be produced
    int64_t prev_end_100ns_ = 0;    // host time just after the previous packet
    uint64_t prev_device_end_ = 0;  // expected device position of the next packet
    bool  have_prev_ = false;
    bool  have_device_pos_ = false;
    // drift_ppm bookkeeping: the open segment (since the last anchor) plus the closed ones.
    bool    seg_open_ = false;
    int64_t seg_first_ts_100ns_ = 0;
    double  seg_nominal_ticks_ = 0.0;
    double  closed_elapsed_ticks_ = 0.0;
    double  closed_nominal_ticks_ = 0.0;
    std::vector<float> stretch_buf_;   // reserved once, so a correction never allocates
    Stats stats_;
};

}  // namespace wf
