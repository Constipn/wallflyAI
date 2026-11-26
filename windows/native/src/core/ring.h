// Lock-free single-producer/single-consumer ring between the real-time capture
// thread and the DSP thread (spec 3.5 stage 1, re-implementation of MNAudioRing.c).
// Fixed 1024 slots x 480 mono floats, acquire/release atomics, epoch against stale slots.
//
// Deviations from SafeAudio/MNAudioRing.c, all forced by the WASAPI side:
//  * MNAudioRing is multi channel with a separate frame capacity and block capacity and
//    checks both before a write; a WASAPI capture period is bounded, so one slot holds one
//    period of one already-downmixed mono channel and the frame bound is implicit.
//  * MNAudioRingWrite takes the epoch as a PARAMETER (the producer knows its own
//    generation) and MNAudioRingRead only reports it - the consumer filters. Here the
//    producer stamps the ring's current epoch and pop() skips stale slots itself, counting
//    them in discarded(). Each stream owns its ring and a restart swaps the whole channel,
//    so a stale producer cannot write into a bumped ring in the first place.
//  * a full ring drops the NEWEST block (the producer never blocks) and remembers the loss;
//    the next block that fits carries it (RingSlot::lost_frames / lost_qpc_100ns), so the
//    consumer reports one gap per lost interval at its true position.
//  * slots carry pu64DevicePosition and the AUDCLNT_BUFFERFLAGS_* word, which have no
//    CoreAudio counterpart; MNAudioRingEntry carries only the host time.
#pragma once
#include <atomic>
#include <cstdint>
#include <memory>

#include "core/constants.h"

namespace wf {

struct RingSlot {
    int64_t  qpc_100ns = 0;        // pu64QPCPosition of the slot's first sample
    uint64_t device_position = 0;  // pu64DevicePosition
    uint32_t flags = 0;            // AUDCLNT_BUFFERFLAGS_*
    uint32_t epoch = 0;
    uint32_t frames = 0;           // valid samples in data[]
    // Blocks the producer had to drop (ring full) directly before this slot: their summed
    // frame count and the timestamp of the first one. 0 = no loss in front of this slot.
    uint64_t lost_frames = 0;
    int64_t  lost_qpc_100ns = 0;
    float    data[k::kRingSlotFrames] = {};
};

class AudioRing {
public:
    AudioRing();
    AudioRing(const AudioRing&) = delete;
    AudioRing& operator=(const AudioRing&) = delete;

    static constexpr uint64_t kSlots = static_cast<uint64_t>(k::kRingSlots);
    static constexpr uint32_t kSlotFrames = static_cast<uint32_t>(k::kRingSlotFrames);

    // Producer side (real-time thread): never blocks, never allocates. A full ring or
    // an oversized block is dropped and counted instead.
    bool push(const float* samples, uint32_t frames, int64_t qpc_100ns,
              uint64_t device_position, uint32_t flags);

    // Consumer side: skips slots written before the last bump_epoch().
    bool pop(RingSlot& out);
    // Consumer side: describes the slot the next pop() returns without consuming it (stale
    // slots in front of it are skipped and counted, exactly as pop() would). False if empty.
    bool peek(int64_t* qpc_100ns, uint32_t* frames, uint32_t* flags);
    // Consumer side, for the stop flush only: a loss that no later slot has carried yet
    // (the stream stopped delivering while the ring was full). Racy against a producer that
    // is still pushing; it can under-report that last loss, never report it twice.
    bool pending_loss(int64_t* qpc_100ns, uint64_t* frames) const;

    // Called by the consumer after a stream rebuild so leftovers are discarded.
    void bump_epoch();

    uint32_t epoch() const { return epoch_.load(std::memory_order_relaxed); }
    uint64_t overflows() const { return overflows_.load(std::memory_order_relaxed); }
    uint64_t discarded() const { return discarded_.load(std::memory_order_relaxed); }
    uint64_t size() const {
        return tail_.load(std::memory_order_acquire) - head_.load(std::memory_order_acquire);
    }

private:
    void note_loss(int64_t qpc_100ns, uint32_t frames);   // producer side

    std::unique_ptr<RingSlot[]> slots_;
    alignas(64) std::atomic<uint64_t> head_{0};   // consumer
    alignas(64) std::atomic<uint64_t> tail_{0};   // producer
    alignas(64) std::atomic<uint32_t> epoch_{1};
    std::atomic<uint64_t> overflows_{0};
    std::atomic<uint64_t> discarded_{0};
    // Producer-owned loss bookkeeping (see RingSlot::lost_frames); atomic only so that
    // pending_loss() may look at it.
    std::atomic<uint64_t> loss_frames_{0};
    std::atomic<int64_t>  loss_qpc_100ns_{0};
};

}  // namespace wf
