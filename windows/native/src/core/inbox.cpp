// CaptureInbox implementation: linear sample store with compaction, so a popped
// packet is always contiguous and no allocation happens after configure().
#include "core/inbox.h"

#include <cmath>
#include <cstring>

namespace wf {

void CaptureInbox::configure(SourceKind source, uint32_t rate, GapLog* gaps) {
    source_ = source;
    rate_ = rate;
    gaps_ = gaps;
    capacity_ = static_cast<size_t>(k::kInboxSeconds * rate + 0.5);
    if (capacity_ < 1) capacity_ = 1;
    store_.assign(capacity_ * 2, 0.0f);
    scratch_.assign(capacity_, 0.0f);
    packets_.clear();
    packets_.reserve(256);
    reset();
}

void CaptureInbox::reset() {
    packets_.clear();
    head_ = 0;
    write_ = 0;
    queued_ = 0;
    pending_hole_100ns_ = 0;
}

int64_t CaptureInbox::ticks_of(size_t frames) const {
    if (rate_ == 0) return 0;
    return static_cast<int64_t>(static_cast<double>(frames) * k::kTicksPerSecond / rate_ + 0.5);
}

void CaptureInbox::drop_oldest(size_t needed) {
    if (queued_ + needed <= capacity_) return;
    int64_t start = 0;
    size_t dropped_frames = 0;
    size_t dropped_packets = 0;
    while (head_ < packets_.size() && queued_ + needed > capacity_) {
        const Entry& e = packets_[head_];
        if (dropped_packets == 0) start = e.qpc_100ns;
        dropped_frames += e.frames;
        // The dropped packet and whatever hole it carried both lie before the next survivor.
        pending_hole_100ns_ += e.hole_100ns + ticks_of(e.frames);
        queued_ -= e.frames;
        ++dropped_packets;
        ++head_;
    }
    if (head_ >= packets_.size()) {   // inbox drained: restart the store
        packets_.clear();
        head_ = 0;
        write_ = 0;
    }
    if (dropped_packets > 0) {
        stats_.dropped_packets += dropped_packets;
        stats_.dropped_frames += dropped_frames;
        ++stats_.overflow_events;
        if (gaps_ && rate_ > 0) {
            gaps_->add(source_, GapReason::InboxOverflow, start,
                       static_cast<double>(dropped_frames) / rate_);
        }
    }
}

bool CaptureInbox::push(const InputPacket& p) {
    if (p.frames == 0 || capacity_ == 0) return true;
    if (p.rate != 0 && p.rate != rate_) configure(source_, p.rate, gaps_);
    const size_t before = stats_.overflow_events;

    const float* src = p.data;
    size_t frames = p.frames;
    int64_t qpc = p.qpc_100ns;
    uint64_t devpos = p.device_position;
    int64_t hole_extra = 0;

    // A single packet larger than the whole inbox: keep its tail, report the head as a gap.
    if (frames > capacity_) {
        const size_t cut = frames - capacity_;
        if (gaps_) {
            gaps_->add(source_, GapReason::InboxOverflow, qpc,
                       static_cast<double>(cut) / rate_);
        }
        stats_.dropped_frames += cut;
        ++stats_.overflow_events;
        hole_extra = ticks_of(cut);   // the cut head sits directly before the kept tail
        if (src) src += cut;
        qpc += static_cast<int64_t>(static_cast<double>(cut) * k::kTicksPerSecond / rate_ + 0.5);
        devpos += cut;
        frames = capacity_;
    }

    drop_oldest(frames);

    if (write_ + frames > store_.size()) {   // compact the live region to the front
        const size_t live_off = (head_ < packets_.size()) ? packets_[head_].offset : write_;
        const size_t live = write_ - live_off;
        if (live > 0) std::memmove(store_.data(), store_.data() + live_off, live * sizeof(float));
        for (size_t i = head_; i < packets_.size(); ++i) packets_[i].offset -= live_off;
        if (head_ > 0) {
            packets_.erase(packets_.begin(), packets_.begin() + static_cast<ptrdiff_t>(head_));
            head_ = 0;
        }
        write_ = live;
    }

    Entry e;
    e.offset = write_;
    e.frames = static_cast<uint32_t>(frames);
    e.qpc_100ns = qpc;
    e.device_position = devpos;
    e.flags = p.flags;
    e.hole_100ns = p.hole_reported_100ns + hole_extra;
    if (src) {
        std::memcpy(store_.data() + write_, src, frames * sizeof(float));
    } else {
        std::memset(store_.data() + write_, 0, frames * sizeof(float));
    }
    write_ += frames;
    queued_ += frames;
    packets_.push_back(e);
    ++stats_.pushed;
    return stats_.overflow_events == before;
}

bool CaptureInbox::pop(InputPacket& out) {
    if (head_ >= packets_.size()) return false;
    const Entry e = packets_[head_++];
    queued_ -= e.frames;
    out.data = store_.data() + e.offset;
    out.frames = e.frames;
    out.rate = rate_;
    out.qpc_100ns = e.qpc_100ns;
    out.device_position = e.device_position;
    out.flags = e.flags;
    out.hole_reported_100ns = e.hole_100ns + pending_hole_100ns_;
    pending_hole_100ns_ = 0;
    if (head_ >= packets_.size()) {   // fully drained: reclaim the store
        packets_.clear();
        head_ = 0;
        // write_ stays: the returned view must survive until the next push.
    }
    return true;
}

}  // namespace wf
