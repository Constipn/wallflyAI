// CaptureNormalizer implementation. The error that drives the drift correction is the
// difference between a packet's host timestamp and the timeline time the normalizer is
// about to assign to it, so every inserted/removed sample feeds straight back into it.
#include "core/normalizer.h"

#include <algorithm>
#include <cmath>
#include <cstdlib>

namespace wf {

namespace {
inline int64_t frames_to_ticks(uint64_t frames, uint32_t rate) {
    if (rate == 0) return 0;
    return static_cast<int64_t>(static_cast<double>(frames) * k::kTicksPerSecond / rate + 0.5);
}
}  // namespace

void CaptureNormalizer::configure(const Config& cfg, GapLog* gaps, ResamplerFactory factory) {
    cfg_ = cfg;
    gaps_ = gaps;
    factory_ = factory ? factory : default_resampler_factory();
    resampler_.reset();
    rate_ = 0;
    have_origin_ = false;
    anchored_ = false;
    origin_100ns_ = 0;
    next_pos_ = 0;
    have_prev_ = false;
    have_device_pos_ = false;
    seg_open_ = false;
    seg_first_ts_100ns_ = 0;
    seg_nominal_ticks_ = 0.0;
    closed_elapsed_ticks_ = 0.0;
    closed_nominal_ticks_ = 0.0;
    stretch_buf_.reserve(static_cast<size_t>(k::kSampleRate));
    stats_ = Stats{};
}

void CaptureNormalizer::reset() {
    anchored_ = false;
    have_prev_ = false;
    have_device_pos_ = false;
    if (resampler_) resampler_->reset();
}

// Literal port of CaptureNormalizer.stretch (EchoAudioProcessor.swift:187-201).
void CaptureNormalizer::stretch(const std::vector<float>& in, int k, std::vector<float>& out) {
    const int n = k < 0 ? -k : k;
    out.clear();
    if (n <= 0 || in.empty()) {
        out.assign(in.begin(), in.end());
        return;
    }
    const int count = static_cast<int>(in.size());
    out.reserve(in.size() + (k > 0 ? static_cast<size_t>(k) : 0u));
    int next = 1;
    for (int i = 0; i < count; ++i) {
        const float sample = in[static_cast<size_t>(i)];
        if (next <= n && i == static_cast<int>(static_cast<int64_t>(next) * count / (n + 1))) {
            ++next;
            if (k > 0) {
                const float last = out.empty() ? sample : out.back();
                out.push_back(0.5f * (last + sample));
            } else {
                if (!out.empty()) out.back() = 0.5f * (out.back() + sample);
                continue;                      // this sample is dropped
            }
        }
        out.push_back(sample);
    }
}

void CaptureNormalizer::rebuild(double in_rate) {
    resampler_ = factory_(in_rate, static_cast<double>(k::kSampleRate));
}

// content_ts = host time of the first sample this packet will produce, already corrected
// for the resampler's algorithmic delay.
void CaptureNormalizer::anchor_to(int64_t content_ts, bool emit_gap, int64_t covered_100ns) {
    if (!have_origin_) {
        origin_100ns_ = content_ts;   // position 0 is the first sample ever produced
        next_pos_ = 0;
        have_origin_ = true;
    } else {
        const int64_t old_pos = next_pos_;
        next_pos_ = static_cast<int64_t>(
            std::llround(static_cast<double>(content_ts - origin_100ns_) / k::kTicksPerSample));
        const int64_t skipped = next_pos_ - old_pos;
        if (emit_gap && gaps_ && !cfg_.app_source) {
            const int64_t covered = covered_100ns > 0
                                        ? static_cast<int64_t>(std::llround(
                                              static_cast<double>(covered_100ns) / k::kTicksPerSample))
                                        : 0;
            if (covered <= 0) {
                gaps_->add(cfg_.source, GapReason::TimestampDiscontinuity, host_time_of(old_pos),
                           skipped > 0 ? static_cast<double>(skipped) / k::kSampleRate : 0.0);
            } else {
                // The hole (or `covered` of it) is already reported upstream as a loss. Only
                // an excess beyond the 3 ms re-anchor tolerance is an independent
                // discontinuity; it is placed right after the reported part.
                const int64_t rest = skipped - covered;
                if (rest * k::kTicksPerSample > k::kReanchorJumpTicks) {
                    gaps_->add(cfg_.source, GapReason::TimestampDiscontinuity,
                               host_time_of(old_pos + covered),
                               static_cast<double>(rest) / k::kSampleRate);
                }
            }
        }
        ++stats_.reanchors;
    }
    anchored_ = true;
}

int64_t CaptureNormalizer::process(const InputPacket& p, std::vector<float>& out,
                                   bool* discontinuity) {
    out.clear();
    if (discontinuity) *discontinuity = false;
    if (p.frames == 0) return next_pos_;

    // --- 1. effective host timestamp -----------------------------------------
    // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR: the QPC value is unusable, derive the time
    // from the end of the previous packet instead (spec 3.3 "Zeitstempel").
    int64_t ts = p.qpc_100ns;
    if (p.flags & kBufTimestampError) {
        ++stats_.timestamp_errors;
        if (have_prev_) ts = prev_end_100ns_;
    }

    // --- 1b. "Late audio packet discarded" (EchoAudioProcessor.append:281-284) ------
    // A packet that starts more than 3 ms before the predicted continuation is dropped
    // whole; it must not drag the timeline backwards. The Mac records this gap for every
    // source, the app-source exemption applies only to the forward "timestamps
    // interrupted" case.
    if (anchored_ && have_prev_ && (ts - prev_end_100ns_) < -k::kReanchorJumpTicks) {
        ++stats_.late_packets;
        if (gaps_ && p.rate != 0) {
            gaps_->add(cfg_.source, GapReason::LateFrame, ts,
                       static_cast<double>(p.frames) / p.rate);
        }
        return next_pos_;
    }

    const bool rate_changed = (p.rate != 0 && p.rate != rate_);
    if (rate_changed) {
        rate_ = p.rate;
        rebuild(static_cast<double>(rate_));
        anchored_ = false;
        have_prev_ = false;
        have_device_pos_ = false;
    }
    if (!resampler_) rebuild(static_cast<double>(rate_ ? rate_ : k::kSampleRate));

    const int64_t delay_ticks =
        static_cast<int64_t>(std::llround(resampler_->algorithmic_delay_seconds() * k::kTicksPerSecond));

    bool reanchor = false;
    bool gap_from_discontinuity = false;
    int64_t err = 0;
    // Mac: discontinuity = changed || jumped, and `jumped` is false while expectedInput is
    // nil. The FIRST anchor of a stream is therefore never a discontinuity - the Mac drops
    // the whole normalizer on a restart, so its first packet starts clean as well.
    const bool was_anchored = anchored_;

    if (!anchored_) {
        reanchor = true;
    } else {
        // --- 2. AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY ------------------------
        // The gap length comes from the device position delta; > 3 ms re-anchors.
        if (p.flags & kBufDataDiscontinuity) {
            ++stats_.discontinuities;
            double gap_sec = 0.0;
            if (have_device_pos_ && p.device_position > prev_device_end_ && rate_ > 0) {
                gap_sec = static_cast<double>(p.device_position - prev_device_end_) / rate_;
            } else {
                gap_sec = static_cast<double>(ts - prev_end_100ns_) / k::kTicksPerSecond;
            }
            if (gap_sec < 0.0) gap_sec = 0.0;
            if (gaps_ && !cfg_.app_source) {
                if (p.hole_reported_100ns <= 0) {
                    gaps_->add(cfg_.source, GapReason::TimestampDiscontinuity, prev_end_100ns_,
                               gap_sec);
                } else {
                    // Same rule as anchor_to(): the reported loss is not logged twice.
                    const double rest =
                        gap_sec - static_cast<double>(p.hole_reported_100ns) / k::kTicksPerSecond;
                    if (rest * k::kTicksPerSecond > k::kReanchorJumpTicks) {
                        gaps_->add(cfg_.source, GapReason::TimestampDiscontinuity,
                                   prev_end_100ns_ + p.hole_reported_100ns, rest);
                    }
                }
            }
            if (gap_sec * k::kTicksPerSecond > k::kReanchorJumpTicks) {
                reanchor = true;
                gap_from_discontinuity = true;   // the gap above already covers it
            }
        }
        // --- 3. jump against the predicted continuity (> 3 ms) ----------------
        const int64_t jump = ts - prev_end_100ns_;
        if (std::llabs(jump) > k::kReanchorJumpTicks) reanchor = true;
        // --- 4. accumulated drift (> 20 ms) -----------------------------------
        err = (ts - delay_ticks) - host_time_of(next_pos_);
        stats_.last_error_sec = static_cast<double>(err) / k::kTicksPerSecond;
        if (std::llabs(err) > k::kReanchorDriftTicks) reanchor = true;
    }

    if (reanchor) {
        resampler_->reset();
        if (anchored_ || have_origin_) rebuild(static_cast<double>(rate_ ? rate_ : k::kSampleRate));
        anchor_to(ts - delay_ticks, !gap_from_discontinuity, p.hole_reported_100ns);
        if (discontinuity) *discontinuity = was_anchored;
        // A new anchored segment: whatever lies between the previous packet's end and this
        // packet is a hole, never part of the drift measurement.
        if (seg_open_) {
            closed_elapsed_ticks_ += static_cast<double>(prev_end_100ns_ - seg_first_ts_100ns_);
            closed_nominal_ticks_ += seg_nominal_ticks_;
        }
        seg_open_ = true;
        seg_first_ts_100ns_ = ts;
        seg_nominal_ticks_ = 0.0;
    }

    // --- 5. convert -----------------------------------------------------------
    const int64_t start = next_pos_;
    resampler_->process(p.data, p.frames, out);

    // --- 6. drift correction (CaptureNormalizer.convert:171-181) --------------
    // Only the drift beyond the 1 ms jitter band is corrected, at most one sample per
    // 10 ms of audio in this packet. Output stays contiguous, so 10 ms AEC frames remain
    // covered, and the applied samples feed back into `err` on the next packet.
    if (!reanchor && !out.empty() && std::llabs(err) > k::kDriftIgnoreTicks) {
        const int limit =
            std::max<int>(1, static_cast<int>(out.size() / k::kFrameSamples));
        const double err_sec = static_cast<double>(err) / k::kTicksPerSecond;
        const double excess =
            (std::fabs(err_sec) - 0.001) * k::kSampleRate * (err_sec > 0 ? 1.0 : -1.0);
        int correction = static_cast<int>(std::lround(excess));
        if (correction > limit) correction = limit;
        if (correction < -limit) correction = -limit;
        if (correction != 0) {
            stretch(out, correction, stretch_buf_);
            const int applied = static_cast<int>(stretch_buf_.size()) -
                                static_cast<int>(out.size());
            out.swap(stretch_buf_);
            if (applied > 0) stats_.inserted_samples += static_cast<uint64_t>(applied);
            else if (applied < 0) stats_.removed_samples += static_cast<uint64_t>(-applied);
        }
    }

    next_pos_ += static_cast<int64_t>(out.size());

    // --- 7. bookkeeping -------------------------------------------------------
    ++stats_.packets;
    stats_.consumed_frames += p.frames;
    prev_end_100ns_ = ts + frames_to_ticks(p.frames, rate_);
    prev_device_end_ = p.device_position + p.frames;
    have_prev_ = true;
    have_device_pos_ = (p.device_position != 0) || have_device_pos_;

    if (rate_ > 0) {
        seg_nominal_ticks_ += static_cast<double>(p.frames) * k::kTicksPerSecond / rate_;
        const double elapsed = closed_elapsed_ticks_ +
                               static_cast<double>(prev_end_100ns_ - seg_first_ts_100ns_);
        const double nominal = closed_nominal_ticks_ + seg_nominal_ticks_;
        if (elapsed > 0.0) stats_.drift_ppm = (elapsed - nominal) / elapsed * 1e6;
    }
    return start;
}

}  // namespace wf
