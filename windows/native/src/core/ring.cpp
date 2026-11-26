// AudioRing: the 1024-slot SPSC ring. All storage is allocated once in the
// constructor; push/pop touch nothing but the slot array and three atomics.
#include "core/ring.h"

#include <cstring>

namespace wf {

AudioRing::AudioRing() : slots_(new RingSlot[kSlots]()) {}

void AudioRing::note_loss(int64_t qpc_100ns, uint32_t frames) {
    overflows_.fetch_add(1, std::memory_order_relaxed);
    const uint64_t lost = loss_frames_.load(std::memory_order_relaxed);
    if (lost == 0) loss_qpc_100ns_.store(qpc_100ns, std::memory_order_relaxed);
    loss_frames_.store(lost + frames, std::memory_order_release);
}

bool AudioRing::push(const float* samples, uint32_t frames, int64_t qpc_100ns,
                     uint64_t device_position, uint32_t flags) {
    if (frames == 0) {
        overflows_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    if (frames > kSlotFrames) {                  // cannot be stored: lost audio as well
        note_loss(qpc_100ns, frames);
        return false;
    }
    const uint64_t t = tail_.load(std::memory_order_relaxed);
    const uint64_t h = head_.load(std::memory_order_acquire);
    if (t - h >= kSlots) {                       // full: producer never blocks
        note_loss(qpc_100ns, frames);
        return false;
    }
    RingSlot& s = slots_[t & (kSlots - 1)];
    s.qpc_100ns = qpc_100ns;
    s.device_position = device_position;
    s.flags = flags;
    s.frames = frames;
    s.lost_frames = loss_frames_.load(std::memory_order_relaxed);
    s.lost_qpc_100ns = s.lost_frames ? loss_qpc_100ns_.load(std::memory_order_relaxed) : 0;
    // Handed to this slot: cleared before the slot becomes visible, so pending_loss() can
    // never see the same loss once more after the consumer popped it.
    if (s.lost_frames) loss_frames_.store(0, std::memory_order_release);
    s.epoch = epoch_.load(std::memory_order_acquire);
    if (samples) {
        std::memcpy(s.data, samples, static_cast<size_t>(frames) * sizeof(float));
    } else {
        std::memset(s.data, 0, static_cast<size_t>(frames) * sizeof(float));
    }
    tail_.store(t + 1, std::memory_order_release);
    return true;
}

bool AudioRing::pop(RingSlot& out) {
    const uint32_t current = epoch_.load(std::memory_order_relaxed);
    for (;;) {
        const uint64_t h = head_.load(std::memory_order_relaxed);
        const uint64_t t = tail_.load(std::memory_order_acquire);
        if (h == t) return false;
        const RingSlot& s = slots_[h & (kSlots - 1)];
        const bool stale = (s.epoch != current);
        if (!stale) {
            out.qpc_100ns = s.qpc_100ns;
            out.device_position = s.device_position;
            out.flags = s.flags;
            out.epoch = s.epoch;
            out.frames = s.frames;
            out.lost_frames = s.lost_frames;
            out.lost_qpc_100ns = s.lost_qpc_100ns;
            std::memcpy(out.data, s.data, static_cast<size_t>(s.frames) * sizeof(float));
        }
        head_.store(h + 1, std::memory_order_release);
        if (!stale) return true;
        discarded_.fetch_add(1, std::memory_order_relaxed);
    }
}

bool AudioRing::peek(int64_t* qpc_100ns, uint32_t* frames, uint32_t* flags) {
    const uint32_t current = epoch_.load(std::memory_order_relaxed);
    for (;;) {
        const uint64_t h = head_.load(std::memory_order_relaxed);
        const uint64_t t = tail_.load(std::memory_order_acquire);
        if (h == t) return false;
        const RingSlot& s = slots_[h & (kSlots - 1)];
        if (s.epoch == current) {
            if (qpc_100ns) *qpc_100ns = s.qpc_100ns;
            if (frames) *frames = s.frames;
            if (flags) *flags = s.flags;
            return true;
        }
        head_.store(h + 1, std::memory_order_release);   // stale: skipped, as pop() does
        discarded_.fetch_add(1, std::memory_order_relaxed);
    }
}

bool AudioRing::pending_loss(int64_t* qpc_100ns, uint64_t* frames) const {
    const uint64_t lost = loss_frames_.load(std::memory_order_acquire);
    if (lost == 0) return false;
    if (qpc_100ns) *qpc_100ns = loss_qpc_100ns_.load(std::memory_order_relaxed);
    if (frames) *frames = lost;
    return true;
}

void AudioRing::bump_epoch() {
    epoch_.fetch_add(1, std::memory_order_release);
}

}  // namespace wf
