// Framer implementation, a literal port of EchoFrameAssembler.append/finish. Alignment to
// the shared 160-sample grid is what lets the mic and the reference stream address the same
// frame index n, which the echo processor relies on.
#include "core/framer.h"

#include <cstring>

namespace wf {

void Framer::configure(SourceKind source, int64_t origin_100ns, GapLog* gaps) {
    source_ = source;
    origin_100ns_ = origin_100ns;
    gaps_ = gaps;
    reset();
    stats_ = Stats{};
}

void Framer::reset() {
    next_pos_ = 0;
    partial_index_ = 0;
    last_index_ = 0;
    have_partial_ = false;
    have_last_ = false;
    valid_lo_ = k::kFrameSamples;
    valid_hi_ = 0;
    pending_flags_ = 0;
}

void Framer::start_partial(int64_t index) {
    std::memset(buf_, 0, sizeof(buf_));
    partial_index_ = index;
    have_partial_ = true;
    valid_lo_ = k::kFrameSamples;
    valid_hi_ = 0;
    pending_flags_ = 0;
    if (have_last_ && index != last_index_ + 1) {
        pending_flags_ |= kFrameFirstAfterGap;
        if (index > last_index_ + 1) {
            stats_.skipped_frames += static_cast<uint64_t>(index - last_index_ - 1);
        }
    }
}

void Framer::emit(std::vector<Frame160>& out) {
    have_partial_ = false;
    if (valid_hi_ <= valid_lo_) return;          // nothing was written into this frame
    Frame160 f;
    f.index = partial_index_;
    f.host_100ns = host_time_of(partial_index_ * k::kFrameSamples);
    f.flags = pending_flags_;
    f.valid_lo = static_cast<uint16_t>(valid_lo_);
    f.valid_hi = static_cast<uint16_t>(valid_hi_);
    std::memcpy(f.s, buf_, sizeof(buf_));
    out.push_back(f);
    last_index_ = partial_index_;
    have_last_ = true;
    ++stats_.frames;
    if (valid_hi_ - valid_lo_ < k::kFrameSamples) ++stats_.partial_frames;
}

void Framer::push(int64_t pos, const float* data, size_t n, std::vector<Frame160>& out) {
    if (!data || n == 0) return;
    for (size_t i = 0; i < n; ++i) {
        const int64_t position = pos + static_cast<int64_t>(i);
        if (position < 0) {                       // Swift: guard position >= 0 else continue
            ++stats_.trimmed_samples;
            continue;
        }
        const int64_t index = position / k::kFrameSamples;
        const int local = static_cast<int>(position % k::kFrameSamples);
        if (!have_partial_ || partial_index_ != index) {
            if (have_partial_) emit(out);
            start_partial(index);
        }
        buf_[local] = data[i];
        if (local < valid_lo_) valid_lo_ = local;
        valid_hi_ = local + 1;
        if (local == k::kFrameSamples - 1) emit(out);
    }
    next_pos_ = pos + static_cast<int64_t>(n);
}

void Framer::finish(std::vector<Frame160>& out) {
    if (have_partial_) emit(out);
}

}  // namespace wf
