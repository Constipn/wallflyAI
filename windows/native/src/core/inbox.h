// CaptureInbox (spec 3.5 stage 2): port of CaptureInbox (EchoAudioProcessor.swift:56-110),
// per source, bounded to 240 ms at the source rate. On overflow the oldest packets are
// dropped and an InboxOverflow gap {start, duration} is recorded. Storage is allocated once
// in configure(); push/pop never allocate.
//
// One loss, one report: before 2026-11-23 every overflow was reported twice - here as
// InboxOverflow, and again by CaptureNormalizer as a TimestampDiscontinuity when the first
// surviving packet arrived with a timestamp jump over exactly the dropped interval. The
// dropped duration now travels with that packet (InputPacket::hole_reported_100ns).
//
// Mac parity note: the Swift inbox merges every loss between two drain() calls into ONE
// RecordingGap, and the app merges further in CaptureEventInbox.append / appendRecordingGap
// (same source + reason, starting within duration + 11 ms). We record one gap per overflow
// event; the equivalent coalescing belongs to the event layer in src/session, not here.
// Packet generations are a session-layer concept on Windows (StreamChannel::attempt_id).
#pragma once
#include <cstdint>
#include <vector>

#include "core/types.h"

namespace wf {

// A packet as handed over by the capture thread (one WASAPI buffer, already mono).
struct InputPacket {
    const float* data = nullptr;
    uint32_t     frames = 0;
    uint32_t     rate = 0;
    int64_t      qpc_100ns = 0;
    uint64_t     device_position = 0;
    uint32_t     flags = 0;      // AUDCLNT_BUFFERFLAGS_*
    // Length of the hole directly BEFORE this packet that has already been reported as a
    // gap upstream (a capture-ring loss, or packets this inbox dropped). The normalizer
    // subtracts it from the discontinuity it would otherwise log for the same hole, so one
    // lost interval is reported exactly once; only an excess beyond it (> 3 ms) is still
    // logged as an independent discontinuity. 0 = nothing reported upstream.
    int64_t      hole_reported_100ns = 0;
};

class CaptureInbox {
public:
    struct Stats {
        uint64_t pushed = 0;
        uint64_t dropped_packets = 0;
        uint64_t dropped_frames = 0;
        uint64_t overflow_events = 0;
    };

    // rate = source sample rate; capacity becomes ceil(0.240 * rate) frames.
    void configure(SourceKind source, uint32_t rate, GapLog* gaps);
    void reset();

    // Returns false when the packet caused older audio to be dropped.
    bool push(const InputPacket& p);
    // Hands out the oldest packet; the view stays valid until the next push/pop.
    bool pop(InputPacket& out);

    bool empty() const { return packets_.empty(); }
    size_t queued_frames() const { return queued_; }
    size_t capacity_frames() const { return capacity_; }
    uint32_t rate() const { return rate_; }
    const Stats& stats() const { return stats_; }

private:
    struct Entry {
        size_t   offset = 0;    // index into store_
        uint32_t frames = 0;
        int64_t  qpc_100ns = 0;
        uint64_t device_position = 0;
        uint32_t flags = 0;
        int64_t  hole_100ns = 0;   // InputPacket::hole_reported_100ns as pushed
    };
    void drop_oldest(size_t needed);
    int64_t ticks_of(size_t frames) const;

    SourceKind source_ = SourceKind::Me;
    uint32_t rate_ = 0;
    size_t capacity_ = 0;           // accounted limit (240 ms)
    std::vector<float> store_;      // ring, sized 2 * capacity_ so packets never wrap mid-packet
    std::vector<float> scratch_;
    std::vector<Entry> packets_;    // FIFO, head_ is the first live entry
    size_t head_ = 0;
    size_t write_ = 0;              // next free index in store_
    size_t queued_ = 0;
    // Audio dropped (and reported as InboxOverflow) since the last pop, plus the holes the
    // dropped packets themselves carried: it sits directly before the next packet popped,
    // which hands it on as hole_reported_100ns so the normalizer does not report it again.
    int64_t pending_hole_100ns_ = 0;
    GapLog* gaps_ = nullptr;
    Stats stats_;
};

}  // namespace wf
