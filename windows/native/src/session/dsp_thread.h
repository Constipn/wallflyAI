// DspThread: the serial 10 ms DSP queue of spec 3.5 stage 3.
//
// One tick does, in this order:
//   ring -> CaptureInbox -> CaptureNormalizer -> Framer -> EchoProcessor -> output queue
// plus the level meters, the gap log drain and the metric counters.
//
// Backlog (since 2026-11-23): the capture ring (1024 slots, ~10 s of 10 ms packets) is the
// cross-thread buffer on Windows, not the 240 ms CaptureInbox. A tick therefore never moves a
// whole ring into the inbox (that discarded everything a DSP stall > 240 ms had left in the
// ring). It works through the rings in time-aligned SLICES instead: window = earliest head
// timestamp over all sources + 100 ms; every source (Reference, Others, Mic) takes the slots
// that start inside the window, at most 100 ms of its own audio; then the pipeline, the echo
// canceller and the emit run; repeat until every ring is empty. While a backlog remains the
// canceller's clock is the window end, not the wall clock, so mic frames wait for their
// reference exactly as they would have in real time (no spurious "unfiltered" frames, no
// 49-frame store overflow, no aged-out reference). Real losses (ring overflow) are reported
// once per interval and source as WF_GAP_INBOX_OVERFLOW. Every event of a tick
// is staged in the session's EventCoalescer and committed once at the end of the tick, the
// Windows equivalent of the Mac pump handing its events to CaptureEventInbox. Frames carry
// the Frame160 valid range into WfFrame.valid_lo/valid_hi. Nothing on this path
// allocates after start(): every vector is reserved in configure(), the inboxes own their
// storage and the frame queues are fixed-size rings.
//
// The thread itself waits on a CREATE_WAITABLE_TIMER_HIGH_RESOLUTION with a 10 ms period.
// A fake session never starts it and calls tick() from wf_fake_tick instead, which is why
// the whole pipeline is testable without hardware and without wall-clock time.
//
// Stop (wf_session_stop): the DSP thread is the ONLY producer of frames and events for the
// whole life of the session, the stop flush included. finish_thread() posts a request; the
// thread runs flush_stop() itself - last drain, trailing partial frames, AEC tail, then
// IDspHost::dsp_stopped() so Stopped is the last event - and exits. The API thread only
// waits, with a bound. A small atomic producer token makes the hand-over explicit: the
// thread holds it (Busy) for every tick and for the flush, and a stopping thread whose wait
// ran out may take it over (Taken) only while the DSP thread is between ticks - after that
// the DSP thread never touches a queue again. Nothing on the tick path blocks on it.
#pragma once
#include <atomic>
#include <cstdint>
#include <memory>
#include <vector>

#include "api/wallfly_audio.h"
#include "core/echo_processor.h"
#include "core/framer.h"
#include "core/inbox.h"
#include "core/levels.h"
#include "core/normalizer.h"
#include "core/ring.h"
#include "core/timeline.h"
#include "core/types.h"
#include "session/supervisor.h"

namespace wf {

// Everything wf_session_get_metrics needs that the DSP owns. Written by the DSP thread
// at the end of each tick, read by the API thread - hence the atomics.
struct DspMetrics {
    std::atomic<double>   erle_db{0.0};
    std::atomic<double>   drift_ppm_me{0.0};
    std::atomic<double>   drift_ppm_others{0.0};
    std::atomic<double>   jitter_p95_ms_me{0.0};
    std::atomic<double>   jitter_p95_ms_others{0.0};
    std::atomic<int32_t>  delay_ms{0};
    std::atomic<uint64_t> unfiltered_frames{0};
    std::atomic<uint64_t> late_dropped{0};
    std::atomic<uint64_t> ref_discarded{0};
    std::atomic<uint64_t> inbox_overflows{0};
    std::atomic<uint64_t> timestamp_errors{0};
    std::atomic<uint64_t> discontinuities{0};
    std::atomic<uint64_t> frames_me{0};
    std::atomic<uint64_t> frames_others{0};
    std::atomic<uint64_t> aec_resets{0};
    std::atomic<uint64_t> aec_render_resets{0};              // AP3, WfDiagnostics
    std::atomic<uint64_t> ring_overflows{0};
    // WfDiagnostics (wf_session_get_diagnostics). Per session, never reset by reading.
    std::atomic<int64_t>  tick_gap_max_100ns{0};             // longest pause between ticks
    std::atomic<uint32_t> ring_high_water[kRoleCount] = {};  // most slots queued, per Role
    std::atomic<int64_t>  capture_gap_max_100ns[kRoleCount] = {};   // longest arrival pause
    std::atomic<uint32_t> slices_max{0};                     // most slices one tick needed
    std::atomic<uint64_t> ring_loss_gaps{0};                 // ring losses reported
};

// Rolling p95 of |timestamp - prediction| per source, used for WfMetrics and for the
// ClockJitterWarning of the plan ("p95 > 3 ms").
class JitterTracker {
public:
    void reset();
    void add(double ms);
    double p95() const { return p95_.load(std::memory_order_relaxed); }
    bool take_warning();   // true exactly once, when p95 first exceeds 3 ms

private:
    static constexpr size_t kWindow = 256;   // ~2.6 s at the 10 ms packet cadence
    double buf_[kWindow] = {};
    double scratch_[kWindow] = {};
    size_t n_ = 0;
    std::atomic<double> p95_{0.0};
    bool warned_ = false;
    bool warn_pending_ = false;
};

// What the DSP needs from the session.
class IDspHost {
public:
    virtual ~IDspHost() = default;
    virtual std::shared_ptr<StreamChannel> dsp_channel(Role r) = 0;
    // Events are staged (gaps coalesced, see EventCoalescer) and reach the public event
    // queue only through dsp_commit_events(), which every tick calls once at its end.
    virtual void dsp_publish(const WfEvent& e) = 0;
    virtual bool dsp_push_frame(const WfFrame& f) = 0;     // into the public frame queue
    virtual void dsp_forward_supervisor_events() = 0;      // supervisor ring -> stage
    virtual size_t dsp_commit_events() = 0;                // stage -> event queue; count
    virtual void dsp_notify() = 0;                         // SetEvent on the session handle
    // The stop flush is complete: stage WF_EVENT_STOPPED. Called exactly once, at the very
    // end of flush_stop() and on the producer thread; the commit right after it makes
    // Stopped the last event, queued after the last frame.
    virtual void dsp_stopped(int64_t now) = 0;
};

class DspThread {
public:
    using NowFn = int64_t (*)(void* ctx);

    struct Config {
        uint32_t others_mode = WF_OTHERS_SYSTEM_EXCLUDE;
        uint32_t ns_level = static_cast<uint32_t>(k::kNsLevel);
        uint32_t timestamp_mode = WF_TIMESTAMP_RAW_QPC;
        bool     enable_aec = true;
        double   base_duration_sec = 0.0;
        int64_t  session_origin_100ns = 0;
    };

    DspThread() = default;
    ~DspThread();
    DspThread(const DspThread&) = delete;
    DspThread& operator=(const DspThread&) = delete;

    void configure(IDspHost* host, const Config& cfg, NowFn now_fn, void* now_ctx);

    // Real sessions only. The fake session drives tick() directly.
    bool start_thread();
    bool thread_running() const { return thread_ != nullptr; }
    // Asks the thread to run flush_stop() itself and exit, and waits at most `timeout_ms`.
    // True once the flush is complete (Stopped is queued), even if the thread is still on
    // its way out. Requires a running thread.
    bool finish_thread(uint32_t timeout_ms);
    // After finish_thread() returned false: takes the producer role over if the thread is
    // between ticks. True means the DSP thread will never touch the queues again, so the
    // caller may publish (an Error event). False: it is stuck inside a tick or the flush.
    bool take_producer();
    // Makes the thread leave at its next wait (no flush) and waits at most `timeout_ms`.
    // True when there is no thread any more and every handle is closed; false when it is
    // still running - its owner must then be abandoned, not freed.
    bool join_thread(uint32_t timeout_ms);

    // One 10 ms step at host time `now` (100 ns units).
    void tick(int64_t now);
    // Mac EchoAudioProcessor.finish: a last tick, every stream's trailing partial frame
    // (Framer::finish), then EchoProcessor::stop - drain without waiting and flush the
    // latency tail (spec 3.3 step 6). Only real captured audio is emitted.
    void flush_stop(int64_t now);

    // Posted by the supervisor thread; consumed by the next tick so mn_aec_reset is
    // always called from the DSP thread (spec 3.3 step 5).
    void request_mic_reset(bool format_change);
    // AP3: the echo path moved to another render endpoint (EchoProcessor::on_render_changed),
    // consumed by the next tick like the mic resets.
    void request_render_reset();

    const DspMetrics& metrics() const { return metrics_; }
    void levels(WfLevels* out, int64_t now) const;

private:
    struct SourceState {
        bool       active = false;
        bool       is_reference = false;   // feeds the echo canceller
        bool       emits_others = false;   // its frames are published as WF_SOURCE_OTHERS
        SourceKind kind = SourceKind::Others;
        uint32_t   wf_source = WF_SOURCE_OTHERS;
        CaptureInbox inbox;
        CaptureNormalizer norm;
        Framer   framer;
        GapLog   gaps;
        JitterTracker jitter;
        std::shared_ptr<StreamChannel> channel;
        uint32_t attempt = 0;
        uint32_t rate = 0;
        // Spec 3.3 step 1: every source is cut on ONE shared 16 kHz grid anchored at the
        // session origin, which is what lets the mic and the reference address the same
        // frame index n. This is the stream's own anchor expressed on that grid.
        int64_t  pos_offset = 0;
        bool     framer_ready = false;
        bool     have_prev_ts = false;
        int64_t  prev_end_100ns = 0;
        int64_t  smoothed_100ns = 0;
        uint64_t ring_overflows = 0;
        int64_t  capture_gap_prev = 0;   // arrival_gap_max_100ns of earlier attempts
        // AP3: WF_GAP_* reason for the first re-anchor gap of the current channel (the hole of
        // a planned hand-over), WF_GAP_NONE when the channel was an ordinary restart.
        uint32_t handover_gap = WF_GAP_NONE;
    };

    // Producer token states (see the header comment).
    static constexpr uint32_t kProducerIdle    = 0;   // between ticks
    static constexpr uint32_t kProducerBusy    = 1;   // inside a tick or the stop flush
    static constexpr uint32_t kProducerFlushed = 2;   // the stop flush is complete
    static constexpr uint32_t kProducerTaken   = 3;   // a stopping thread took over

    static DWORD WINAPI thread_main(void* self);
    void run();
    bool enter_producer();                            // Idle -> Busy
    void close_thread_handles();
    int64_t now() const { return now_fn_ ? now_fn_(now_ctx_) : 0; }

    // At most this much audio per source and slice (see the header comment).
    static constexpr int64_t kSliceTicks = 100 * k::kTicksPerMs;
    // Safety bound: every slice takes at least one slot, so 3 full rings fit easily.
    static constexpr uint32_t kMaxSlicesPerTick = 4 * k::kRingSlots;

    void pull_channel(Role r, int64_t now);
    void prepare_ring(SourceState& s, int role_index);   // rate change, high-water mark
    // Host time a ring slot will be framed at (the predicted continuation for a slot whose
    // QPC value is flagged unusable).
    int64_t head_time(const SourceState& s, int64_t qpc, uint32_t flags) const;
    bool slice_window(int64_t* window_end);   // false when every ring is empty
    bool rings_pending();
    // Moves the slots of one slice (start < window_end, <= kSliceTicks of audio, at least one
    // slot when the head is inside the window) from the ring into the inbox.
    void drain_ring(SourceState& s, int64_t window_end);
    size_t process_backlog(int64_t now);      // the slice loop; returns frames handed out
    static int64_t echo_clock(void* self);    // the canceller's clock (see echo_now_)
    void run_pipeline(SourceState& s);
    void route_frames(const SourceState& s);
    size_t emit_frames(int64_t now);   // returns the number of frames handed out
    void emit_frame(const Frame160& f, uint32_t wf_source, uint32_t attempt, LevelMeter& meter,
                    std::atomic<uint64_t>& counter, int64_t now);
    void publish_gap(const Gap& g, uint32_t wf_source, uint32_t reason, uint32_t attempt);
    void drain_gaps(SourceState& s);
    void drain_echo_gaps();
    // The stream that feeds the canceller: Reference in include mode, Others otherwise.
    const SourceState& reference_state() const;
    void publish_metrics();

    IDspHost* host_ = nullptr;
    Config    cfg_;
    Timeline  timeline_;
    NowFn     now_fn_ = nullptr;
    void*     now_ctx_ = nullptr;

    SourceState src_[kRoleCount];
    EchoProcessor echo_;
    GapLog        echo_gaps_;
    LevelMeter    meter_me_;
    LevelMeter    meter_others_;

    // Scratch, reserved once in configure().
    RingSlot slot_;
    std::vector<float> norm_out_;
    std::vector<Frame160> frames_;
    std::vector<Frame160> me_frames_;
    std::vector<Frame160> others_frames_;

    // Non-zero while a slice of a backlog is processed: the time the canceller sees
    // instead of the wall clock (the slice's window end). DSP thread only.
    int64_t echo_now_ = 0;
    int64_t last_tick_ = 0;        // `now` of the previous tick, for tick_gap_max_100ns
    uint64_t ring_loss_gaps_ = 0;

    std::atomic<uint32_t> mic_device_resets_{0};
    std::atomic<uint32_t> mic_format_resets_{0};
    std::atomic<uint32_t> render_resets_{0};
    uint32_t consumed_device_resets_ = 0;
    uint32_t consumed_format_resets_ = 0;
    uint32_t consumed_render_resets_ = 0;

    std::atomic<float> level_me_{0.0f};
    std::atomic<float> level_others_{0.0f};
    std::atomic<uint32_t> me_active_{0};
    std::atomic<uint32_t> others_active_{0};

    DspMetrics metrics_;
    std::atomic<uint32_t> producer_{kProducerIdle};
    HANDLE thread_ = nullptr;
    HANDLE exit_ = nullptr;     // leave at the next wait, no flush (destroy fallback)
    HANDLE finish_ = nullptr;   // run the stop flush, then leave
    HANDLE timer_ = nullptr;
};

}  // namespace wf
