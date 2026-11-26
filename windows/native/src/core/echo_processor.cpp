// EchoProcessor implementation, following EchoAudioProcessor.process/feed/emitClean.
// Render always runs before the matching capture, future reference is never fed, and
// mn_aec_reset is reached only through the two explicit device/format entry points.
#include "core/echo_processor.h"

#include <algorithm>
#include <chrono>
#include <cmath>
#include <cstring>

namespace wf {

namespace {
const float kZeroFrame[k::kFrameSamples] = {};

// The bridge's input rule (AECBridge.cpp): NaN and Inf become 0, everything else is
// clamped to [-1, 1]. The passthrough keeps that guarantee for the Me channel.
inline float scrub(float v) { return std::isfinite(v) ? std::clamp(v, -1.0f, 1.0f) : 0.0f; }

inline int64_t steady_now_100ns() {
    using namespace std::chrono;
    return duration_cast<duration<int64_t, std::ratio<1, 10000000>>>(
               steady_clock::now().time_since_epoch())
        .count();
}

}  // namespace

// ---- FrameStore --------------------------------------------------------------
void FrameStore::init(size_t capacity) {
    v_.clear();
    v_.reserve(capacity ? capacity : 1);
}

const Frame160* FrameStore::find(int64_t index) const {
    for (const Frame160& f : v_) {
        if (f.index == index) return &f;
        if (f.index > index) break;
    }
    return nullptr;
}

void FrameStore::insert(const Frame160& f) {
    size_t i = v_.size();
    while (i > 0 && v_[i - 1].index > f.index) --i;
    if (i > 0 && v_[i - 1].index == f.index) {
        v_[i - 1] = f;                       // Swift: frames[source][index] = frame
        return;
    }
    v_.insert(v_.begin() + static_cast<ptrdiff_t>(i), f);
}

void FrameStore::erase_front() {
    if (!v_.empty()) v_.erase(v_.begin());
}

// ---- EchoProcessor -----------------------------------------------------------
EchoProcessor::EchoProcessor() = default;

EchoProcessor::~EchoProcessor() {
    if (aec_) mn_aec_destroy(aec_);
}

void EchoProcessor::configure(const Config& cfg, GapLog* gaps, int64_t origin_100ns) {
    cfg_ = cfg;
    gaps_ = gaps;
    origin_100ns_ = origin_100ns;
    if (cfg_.enable_aec) {
        if (!aec_) aec_ = mn_aec_create();
        mn_aec_set_ns(aec_, cfg_.ns_level);
        // EchoCanceller.latencySamples: the noise suppressor's 256-point window adds its 96
        // sample overlap only while it runs, so turning NS off shortens the compensation.
        latency_samples_ =
            k::kAec3DelaySamples + (cfg_.ns_level == 0 ? 0 : k::kNsDelaySamples);
    } else {
        // Passthrough: no bridge at all, hence nothing to compensate and nothing to flush.
        if (aec_) mn_aec_destroy(aec_);
        aec_ = nullptr;
        latency_samples_ = 0;
    }
    flush_frames_ = (latency_samples_ + k::kFrameSamples - 1) / k::kFrameSamples;
    // One slot above the cap: the Mac inserts first and only then trims to 49.
    ref_.init(k::kMaxBufferedFramesPerSource + 1);
    cap_.init(k::kMaxBufferedFramesPerSource + 1);
    fed_.clear();
    fed_.reserve(static_cast<size_t>(flush_frames_) + 2);
    fifo_.clear();
    fifo_.reserve(static_cast<size_t>(flush_frames_ + 2) * k::kFrameSamples);
    fed_count_ = 0;
    fifo_fed_pos_ = 0;
    fifo_live_ = false;
    last_mic_ = -1;
    last_render_ = -1;
    finished_ = false;
    stats_ = Stats{};
}

int64_t EchoProcessor::now() const {
    return now_fn_ ? now_fn_(now_ctx_) : steady_now_100ns();
}

void EchoProcessor::metrics(double* erle_db, int* delay_ms) const {
    if (aec_) {
        mn_aec_metrics(aec_, erle_db, delay_ms);
        return;
    }
    if (erle_db) *erle_db = 0.0;     // passthrough: nothing is cancelled, nothing estimated
    if (delay_ms) *delay_ms = 0;
}

void EchoProcessor::gap(const Frame160& f, SourceKind source, GapReason reason) {
    if (!gaps_ || valid_count(f) <= 0) return;
    gaps_->add(source, reason, valid_start_100ns(f), valid_seconds(f));
}

// ---- intake ------------------------------------------------------------------
void EchoProcessor::push_reference(const Frame160& f, std::vector<Frame160>* others_out) {
    if (finished_) return;            // Swift append(): "guard !finished else { return }"
    if (others_out) {                 // Others passes through unchanged, own timeline
        others_out->push_back(f);
        ++stats_.others_frames;
    }
    if (!cfg_.enable_aec) return;     // passthrough: nothing consumes a buffered reference
    ref_.insert(f);
    // Swift insert(): "while frames[source].count > 49 { remove the lowest index }".
    while (ref_.size() > static_cast<size_t>(k::kMaxBufferedFramesPerSource)) {
        gap(ref_.front(), SourceKind::Others, GapReason::InboxOverflow);
        ref_.erase_front();
        ++stats_.ref_overflow;
    }
}

void EchoProcessor::push_capture(const Frame160& f) {
    if (finished_) return;
    cap_.insert(f);
    while (cap_.size() > static_cast<size_t>(k::kMaxBufferedFramesPerSource)) {
        gap(cap_.front(), SourceKind::Me, GapReason::InboxOverflow);
        cap_.erase_front();
        ++stats_.cap_overflow;
    }
}

void EchoProcessor::process(std::vector<Frame160>& me_out) { run(me_out, false); }

// ---- the main loop -----------------------------------------------------------
void EchoProcessor::run(std::vector<Frame160>& me_out, bool draining) {
    if (finished_) return;
    if (!cfg_.enable_aec) {
        run_passthrough(me_out);
        return;
    }
    while (!cap_.empty()) {
        const Frame160 mic = cap_.front();
        const int64_t n = mic.index;

        // Swift: "if index <= lastMicrophone { gap('Late microphone packet') }".
        if (n <= last_mic_) {
            gap(mic, SourceKind::Me, GapReason::LateFrame);
            cap_.erase_front();
            ++stats_.late_dropped;
            continue;
        }

        const Frame160* reference = ref_.find(n);
        const bool covered = reference != nullptr && reference->valid_lo <= mic.valid_lo &&
                             reference->valid_hi >= mic.valid_hi;
        if (!covered && !draining) {
            // mic.time + mic.duration + 0.1: the end of the VALID part, plus 100 ms.
            const int64_t deadline = mic.host_100ns +
                                     static_cast<int64_t>(mic.valid_hi) * k::kTicksPerSample +
                                     k::kReferenceWaitTicks;
            if (now() < deadline) break;                 // keep waiting, later frames too
        }
        cap_.erase_front();

        // A microphone hole keeps the converged filter. Skipping the matching playback
        // keeps render and capture in step.
        if (last_mic_ >= 0 && n != last_mic_ + 1 && last_render_ < n - 1) last_render_ = n - 1;

        // Catch up render history before capture; never feed future playback.
        while (!ref_.empty() && ref_.front().index <= n) {
            const Frame160 r = ref_.front();
            ref_.erase_front();
            if (r.index > last_render_) {
                mn_aec_render(aec_, r.s);
                ++stats_.rendered;
                last_render_ = r.index;
            }
        }

        // Reference still (partly) missing after the wait: the microphone passes through
        // the canceller with silence as playback instead of becoming a gap or resetting.
        uint32_t flags = mic.flags;
        if (!covered) {
            ++stats_.unfiltered_frames;
            flags |= kFrameUnfiltered;
        }
        if (last_render_ < n) {
            mn_aec_render(aec_, kZeroFrame);
            ++stats_.rendered;
            last_render_ = n;
        }

        mn_aec_capture(aec_, mic.s, out_);
        ++stats_.captured;
        last_mic_ = n;
        feed_output(n, mic.valid_lo, mic.valid_hi, flags, me_out);
    }
    age_reference();
}

// enable_aec == false: every buffered mic frame leaves at once, on its own timeline index.
// The late-frame rule is kept, so the Me timeline stays strictly increasing either way.
void EchoProcessor::run_passthrough(std::vector<Frame160>& me_out) {
    while (!cap_.empty()) {
        Frame160 mic = cap_.front();
        cap_.erase_front();
        if (mic.index <= last_mic_) {
            gap(mic, SourceKind::Me, GapReason::LateFrame);
            ++stats_.late_dropped;
            continue;
        }
        last_mic_ = mic.index;
        mic.host_100ns = origin_100ns_ + mic.index * k::kTicksPerFrame;   // as drain_fed()
        for (int i = 0; i < k::kFrameSamples; ++i) {
            mic.s[i] = (i >= mic.valid_lo && i < mic.valid_hi) ? scrub(mic.s[i]) : 0.0f;
        }
        if (mic.valid_hi > mic.valid_lo) {
            me_out.push_back(mic);
            ++stats_.me_frames;
        }
    }
}

// Swift: "let cutoff = Int((now - 0.5) * 100); frames[.reference] = filter { key >= cutoff }".
// Stale reference data can never be replayed after a microphone outage. The cutoff follows
// the wall clock, not the newest mic frame, and is applied after the loop above.
void EchoProcessor::age_reference() {
    if (ref_.empty()) return;
    const double now_sec = static_cast<double>(now() - origin_100ns_) / k::kTicksPerSecond;
    const int64_t cutoff =
        static_cast<int64_t>((now_sec - k::kReferenceMaxAgeSeconds) * 100.0);   // truncates
    while (!ref_.empty() && ref_.front().index < cutoff) {
        ref_.erase_front();
        ++stats_.ref_discarded;
    }
}

// ---- latency compensation (fed space) ----------------------------------------
void EchoProcessor::feed_output(int64_t index, int valid_lo, int valid_hi, uint32_t flags,
                                std::vector<Frame160>& me_out) {
    if (!fifo_live_) {
        fifo_.clear();
        fifo_fed_pos_ = fed_count_ * k::kFrameSamples - latency_samples_;
        fifo_live_ = true;
    }
    fifo_.insert(fifo_.end(), out_, out_ + k::kFrameSamples);

    Fed fe;
    fe.fed_index = fed_count_;
    fe.index = index;
    fe.valid_lo = valid_lo;
    fe.valid_hi = valid_hi;
    fe.flags = flags;
    fed_.push_back(fe);
    ++fed_count_;
    drain_fed(me_out);
}

// Fed frame m owns fed positions [m*160, m*160+160); its timeline index says where that
// block belongs. Only the mic frame's own valid range is emitted (Swift emitClean masks
// against pendingMicrophone), so a partial head or tail stays partial.
void EchoProcessor::drain_fed(std::vector<Frame160>& me_out) {
    while (!fed_.empty()) {
        const Fed fe = fed_.front();
        const int64_t lo = fe.fed_index * k::kFrameSamples;
        const int64_t available = fifo_fed_pos_ + static_cast<int64_t>(fifo_.size());
        if (available < lo + k::kFrameSamples) break;
        const size_t base = static_cast<size_t>(lo - fifo_fed_pos_);
        if (fe.valid_hi > fe.valid_lo) {
            Frame160 f;
            f.index = fe.index;
            f.host_100ns = origin_100ns_ + fe.index * k::kTicksPerFrame;
            f.flags = fe.flags;
            f.valid_lo = static_cast<uint16_t>(fe.valid_lo);
            f.valid_hi = static_cast<uint16_t>(fe.valid_hi);
            for (int i = fe.valid_lo; i < fe.valid_hi; ++i) f.s[i] = fifo_[base + static_cast<size_t>(i)];
            me_out.push_back(f);
            ++stats_.me_frames;
        }
        const size_t drop = base + k::kFrameSamples;
        fifo_.erase(fifo_.begin(), fifo_.begin() + static_cast<ptrdiff_t>(drop));
        fifo_fed_pos_ += static_cast<int64_t>(drop);
        fed_.erase(fed_.begin());
    }
}

// Feeds digital silence until every processed microphone sample has passed the DSP latency.
// The flush frames carry an empty valid range, so they only push earlier audio out.
void EchoProcessor::flush_tail(std::vector<Frame160>& me_out) {
    if (!aec_) return;                        // passthrough: nothing is delayed
    if (fed_.empty()) return;                 // Swift: guard !pendingMicrophone.isEmpty
    for (int i = 0; i < flush_frames_; ++i) {
        mn_aec_render(aec_, kZeroFrame);
        ++stats_.rendered;
        mn_aec_capture(aec_, kZeroFrame, out_);
        ++stats_.captured;
        feed_output(last_mic_ + 1 + i, 0, 0, kFrameSyntheticSilence, me_out);
    }
}

// Swift discardDelayedMicrophone: what is still inside the canceller becomes a gap.
void EchoProcessor::discard_pending(GapReason reason) {
    if (gaps_) {
        for (const Fed& fe : fed_) {
            if (fe.valid_hi <= fe.valid_lo) continue;
            const int64_t start = origin_100ns_ +
                                  (fe.index * k::kFrameSamples + fe.valid_lo) * k::kTicksPerSample;
            gaps_->add(SourceKind::Me, reason, start,
                       static_cast<double>(fe.valid_hi - fe.valid_lo) / k::kSampleRate);
        }
    }
    fed_.clear();
}

void EchoProcessor::reset_canceller() {
    if (aec_) {                               // WfMetrics.aec_resets counts real resets only
        mn_aec_reset(aec_);
        ++stats_.resets;
    }
    fed_.clear();
    fifo_.clear();
    fifo_live_ = false;
    fed_count_ = 0;
}

// ---- lifecycle ---------------------------------------------------------------
void EchoProcessor::stop(std::vector<Frame160>& me_out) {
    if (finished_) return;
    run(me_out, true);                       // no more waiting for a missing reference
    flush_tail(me_out);
    finished_ = true;
    cap_.clear();
    ref_.clear();
    fed_.clear();
    fifo_.clear();
    fifo_live_ = false;
}

void EchoProcessor::on_mic_device_changed() {
    // Swift reset(.microphone): buffered frames and the delayed tail become gaps, the
    // reference buffer is dropped and the render history restarts. No flush: the device
    // that produced that audio is gone.
    while (!cap_.empty()) {
        gap(cap_.front(), SourceKind::Me, GapReason::Restart);
        cap_.erase_front();
    }
    ref_.clear();
    discard_pending(GapReason::Restart);
    reset_canceller();
    last_render_ = -1;
}

void EchoProcessor::on_mic_format_changed(std::vector<Frame160>* me_out) {
    // Swift append(): "try flushEchoTail(); discardDelayedMicrophone('Audio format
    // changed'); try resetEchoCanceller()" - the tail is emitted, not dropped.
    if (me_out) flush_tail(*me_out);
    discard_pending(GapReason::Restart);
    reset_canceller();
}

void EchoProcessor::on_render_changed(std::vector<Frame160>* me_out) {
    if (finished_) return;
    if (me_out) flush_tail(*me_out);
    discard_pending(GapReason::Restart);
    ref_.clear();                              // no stale reference of the old endpoint
    if (aec_) ++stats_.render_resets;
    reset_canceller();
}

}  // namespace wf
