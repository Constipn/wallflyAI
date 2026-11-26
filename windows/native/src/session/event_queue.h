// Output queues of a session: the frame queue the .NET drain thread empties and the
// event queue next to it. Both are single-producer / single-consumer rings with their
// storage allocated once in init(), so nothing on the 10 ms DSP path allocates or locks.
//
// Producers:
//   * frames      - the DSP thread only.
//   * events      - the DSP thread only. The supervisor runs on its own thread, so it
//                   writes into its own SPSC ring (EventQueue as well) which the DSP
//                   tick drains into the public one. That keeps both rings strictly
//                   SPSC and gives every event a single total order.
// Consumer: wf_session_read_frames / wf_session_read_events, i.e. the caller's thread.
//
// A full ring drops the newest item and counts it; WfMetrics.frames_dropped and
// WfMetrics.events_dropped report that. WfEvent.sequence is assigned before the
// capacity check, so a reader that keeps draining sees the numbering jump over
// everything that was thrown away while it was behind.
//
// Between the producers and the public event queue sits an EventCoalescer (Mac
// CaptureEventInbox): everything one DSP tick publishes is staged there, adjacent gaps of
// the same source and reason are merged, and the tick commits the result in one go.
#pragma once
#include <atomic>
#include <cstddef>
#include <cstdint>
#include <cstring>
#include <memory>

#include "api/wallfly_audio.h"

namespace wf {

// Capacities. 2048 frames are 20.48 s of one source or 10.24 s of both - well past the
// ">= 2 s" the plan asks for, and still only 1.3 MB.
inline constexpr size_t kFrameQueueCapacity = 2048;
inline constexpr size_t kEventQueueCapacity = 512;
// The supervisor -> DSP hand-off ring. 128 control events per 10 ms tick is far more
// than the state machine can produce.
inline constexpr size_t kPendingEventCapacity = 256;

template <class T>
class SpscQueue {
public:
    SpscQueue() = default;
    SpscQueue(const SpscQueue&) = delete;
    SpscQueue& operator=(const SpscQueue&) = delete;

    // Allocates `capacity` slots. Call once, before any producer or consumer runs.
    void init(size_t capacity) {
        cap_ = capacity ? capacity : 1;
        buf_.reset(new T[cap_]);
        head_.store(0, std::memory_order_relaxed);
        tail_.store(0, std::memory_order_relaxed);
    }

    size_t capacity() const { return cap_; }

    size_t size() const {
        const uint64_t t = tail_.load(std::memory_order_acquire);
        const uint64_t h = head_.load(std::memory_order_acquire);
        return static_cast<size_t>(t - h);
    }

    // Producer side. Returns false when the ring is full; the item is not stored.
    bool push(const T& v) {
        const uint64_t t = tail_.load(std::memory_order_relaxed);
        const uint64_t h = head_.load(std::memory_order_acquire);
        if (t - h >= cap_) return false;
        buf_[static_cast<size_t>(t % cap_)] = v;
        tail_.store(t + 1, std::memory_order_release);
        return true;
    }

    // Consumer side. Copies at most `max` items and returns how many were copied.
    size_t pop(T* out, size_t max) {
        uint64_t h = head_.load(std::memory_order_relaxed);
        const uint64_t t = tail_.load(std::memory_order_acquire);
        size_t n = static_cast<size_t>(t - h);
        if (n > max) n = max;
        for (size_t i = 0; i < n; ++i) out[i] = buf_[static_cast<size_t>((h + i) % cap_)];
        head_.store(h + n, std::memory_order_release);
        return n;
    }

private:
    std::unique_ptr<T[]> buf_;
    size_t cap_ = 1;
    alignas(64) std::atomic<uint64_t> head_{0};  // consumer cursor
    alignas(64) std::atomic<uint64_t> tail_{0};  // producer cursor
};

// Frame output queue: >= 2 s of WfFrame as required by the plan.
class FrameOutQueue {
public:
    void init(size_t capacity = kFrameQueueCapacity) { q_.init(capacity); }
    // Returns false and counts the drop when the queue is full.
    bool push(const WfFrame& f) {
        if (q_.push(f)) return true;
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    size_t pop(WfFrame* out, size_t max) { return q_.pop(out, max); }
    size_t size() const { return q_.size(); }
    size_t capacity() const { return q_.capacity(); }
    uint64_t dropped() const { return dropped_.load(std::memory_order_relaxed); }

private:
    SpscQueue<WfFrame> q_;
    std::atomic<uint64_t> dropped_{0};
};

// Event queue. Stamps WfEvent.sequence so a reader can detect dropped events even
// without looking at the metrics.
class EventQueue {
public:
    void init(size_t capacity = kEventQueueCapacity) { q_.init(capacity); }

    // Producer side; `e.sequence` is overwritten with the running counter.
    bool push(WfEvent e) {
        e.sequence = next_sequence_++;
        if (q_.push(e)) return true;
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    // Forwards an event that already carries a sequence number (supervisor -> DSP).
    bool forward(const WfEvent& e) {
        if (q_.push(e)) return true;
        dropped_.fetch_add(1, std::memory_order_relaxed);
        return false;
    }
    size_t pop(WfEvent* out, size_t max) { return q_.pop(out, max); }
    size_t size() const { return q_.size(); }
    size_t capacity() const { return q_.capacity(); }
    uint64_t dropped() const { return dropped_.load(std::memory_order_relaxed); }

private:
    SpscQueue<WfEvent> q_;
    std::atomic<uint64_t> dropped_{0};
    uint32_t next_sequence_ = 1;   // touched by the producer only
};

// Staging area in front of the public EventQueue, a port of CaptureEventInbox.append
// (AudioCaptureTypes.swift:56-77). Events keep their order. A gap merges into the LAST
// staged gap with the same source and reason when it starts inside
//   [previous.start, previous.start + previous.duration + 11 ms]
// and the previous gap is then extended to max(both ends) - previous.start. Like the Mac
// rule, the search looks past non-gap events and gaps of other sources or reasons.
//
// commit() is the Mac drain(): the DSP thread calls it once at the end of every tick, so
// merging never crosses a tick. The persistent merge of the Mac (Meeting.appendRecordingGap,
// same rule against the stored gap list) belongs to the consumer of the event queue.
// Merged gaps never reach the queue, so they consume no WfEvent.sequence number.
//
// Single producer (the DSP thread, or the stopping thread once the DSP thread is gone).
// Storage is fixed, nothing allocates.
class EventCoalescer {
public:
    static constexpr size_t kCapacity = 64;
    static constexpr double kMergeSlackSec = 0.011;   // Mac: "+ 0.011"

    // Stages `e`. A full stage is committed first, so the order is kept even then.
    void stage(const WfEvent& e, EventQueue& q) {
        if (e.kind == WF_EVENT_GAP && merge(e)) return;
        if (n_ == kCapacity) commit(q);
        buf_[n_++] = e;
    }

    // Hands everything staged to `q` (which assigns the sequence numbers) and returns the
    // number of events handed over.
    size_t commit(EventQueue& q) {
        const size_t n = n_;
        for (size_t i = 0; i < n; ++i) q.push(buf_[i]);
        n_ = 0;
        return n;
    }

    size_t staged() const { return n_; }
    uint64_t merged() const { return merged_; }

private:
    bool merge(const WfEvent& e) {
        for (size_t i = n_; i-- > 0;) {
            WfEvent& p = buf_[i];
            if (p.kind != WF_EVENT_GAP || p.source != e.source || p.reason != e.reason) continue;
            if (e.at_offset_sec < p.at_offset_sec ||
                e.at_offset_sec > p.at_offset_sec + p.duration_sec + kMergeSlackSec) {
                continue;
            }
            const double end_p = p.at_offset_sec + p.duration_sec;
            const double end_e = e.at_offset_sec + e.duration_sec;
            p.duration_sec = (end_e > end_p ? end_e : end_p) - p.at_offset_sec;
            ++merged_;
            return true;
        }
        return false;
    }

    WfEvent buf_[kCapacity];
    size_t n_ = 0;
    uint64_t merged_ = 0;
};

// Builds a zeroed event with the common fields filled in. reason starts as WF_GAP_NONE, so
// only a WF_EVENT_GAP that sets its own reason ever carries a WF_GAP_* value (0 included).
inline WfEvent make_event(uint32_t kind, uint32_t source, uint32_t attempt, double at_offset_sec) {
    WfEvent e;
    std::memset(&e, 0, sizeof(e));
    e.kind = kind;
    e.source = source;
    e.attempt_id = attempt;
    e.reason = WF_GAP_NONE;
    e.at_offset_sec = at_offset_sec;
    return e;
}

// Copies a NUL terminated detail string into WfEvent.detail, truncating if needed.
inline void set_detail(WfEvent& e, const wchar_t* text) {
    if (!text) return;
    size_t i = 0;
    const size_t max = sizeof(e.detail) / sizeof(e.detail[0]) - 1;
    while (text[i] && i < max) { e.detail[i] = text[i]; ++i; }
    e.detail[i] = 0;
}

}  // namespace wf
