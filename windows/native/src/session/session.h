// Session: the object behind the opaque handle of the C ABI.
//
// It owns everything a recording needs and wires the pieces together:
//
//   config -> plant (which streams exist)        -> Supervisor (when they run)
//          -> StreamChannel rings                -> DspThread (what comes out)
//          -> FrameOutQueue + EventQueue + HANDLE (what the caller reads)
//
// Stream layout per WfSessionConfig.others_mode:
//   0 system exclude  mic + one process-loopback exclude stream that is Others *and*
//                     the echo reference.
//   1 process include mic + a process-loopback include stream on target_pid (Others)
//                     + a separate exclude stream used only as the echo reference; its
//                     frames are never published (spec 3.2).
//   2 device loopback mic + classic loopback on a render endpoint, which is Others and
//                     the reference (it delivers nothing while the endpoint is idle).
// Every mode also runs the silent keep-alive render stream, whatever keepalive_mode says:
// an idle render endpoint that wakes up stalls process loopback for 0.43-0.52 s
// (measurement 2026-11-01, audioprobe onset). keepalive_mode only selects the spec 3.3
// watchdog gate (forced on in mode 2). Since AP3 (2026-11-23) the keep-alive belongs to the
// plant and has its own driver steps: in mode 2 it runs on exactly the endpoint the
// loopback opened, in modes 0/1 on the pinned or default render endpoint, and it follows
// render switches without touching a capture stream.
//
// Device changes (AP3): an empty render_device_id follows the default console render
// endpoint (debounced 200 ms, see Supervisor::handle_render_timer); a pinned one ignores
// default changes and falls back to the default when it is lost. An unconfigured mic opens
// and follows the default communications capture endpoint (console when there is none).
//
// Threads: supervisor (control plane, MTA), DSP (10 ms timer), one capture thread per
// stream inside the WASAPI primitives, plus disposable driver-step workers. A fake
// session starts none of them - wf_fake_tick drives supervisor and DSP in order.
//
// Stop order (wf_session_stop): 1. the DSP thread runs the stop flush itself and queues
// Stopped last (bounded wait, kStopWaitMs); 2. only then the supervisor is told to stop the
// streams, which it does in the background. destroy() waits for the supervisor and the
// driver workers and abandons (leaks) the session when one of its threads never returns,
// instead of freeing memory under a running thread.
#pragma once
#include <atomic>
#include <memory>

#include "api/wallfly_audio.h"
#include "core/timeline.h"
#include "session/dsp_thread.h"
#include "session/event_queue.h"
#include "session/fake_capture.h"
#include "session/power_request.h"
#include "session/supervisor.h"
#include "wasapi/notification_client.h"

namespace wf {

class Session final : public ISupervisorHost, public IDspHost {
public:
    // Validates `cfg` (including cfg.size) and builds the session. On failure returns
    // null and writes a WF_E_* code to `err`.
    static Session* create(const WfSessionConfig& cfg, bool fake, int32_t* err);
    // wf_session_destroy: stops if needed, joins every thread and deletes `s`. Returns false
    // when a thread did not return in time; `s` is then abandoned (leaked) on purpose.
    static bool destroy(Session* s);
    ~Session() override;

    // Upper bound of wf_session_stop's wait for the DSP thread's flush (3 s quit budget).
    static constexpr uint32_t kStopWaitMs = 2500;

    Session(const Session&) = delete;
    Session& operator=(const Session&) = delete;

    int32_t start();
    int32_t set_mic_device(const wchar_t* device_id);
    int32_t stop();
    HANDLE  event_handle() const { return event_; }

    uint32_t read_frames(WfFrame* out, uint32_t cap);
    uint32_t read_events(WfEvent* out, uint32_t cap);
    void get_levels(WfLevels* out);
    void get_metrics(WfMetrics* out);
    void get_diagnostics(WfDiagnostics* out);   // `out` zeroed by the caller
    int32_t get_origin(int64_t* origin_100ns, double* base_duration_sec) const;

    bool is_fake() const { return fake_; }
    int32_t fake_push(uint32_t source, const float* samples, uint32_t frames, uint32_t rate,
                      int64_t qpc_100ns, uint64_t device_position, uint32_t flags);
    int32_t fake_set_now(int64_t qpc_100ns);
    int32_t fake_tick();
    int32_t fake_signal(uint32_t signal, uint32_t source, int32_t hresult);
    int32_t fake_query(uint32_t what, wchar_t* text, uint32_t text_chars, uint32_t* value);

    // ---- ISupervisorHost -------------------------------------------------
    void sup_publish(const WfEvent& e) override;
    void sup_set_channel(Role r, std::shared_ptr<StreamChannel> ch) override;
    void sup_mic_reset(bool format_change) override;
    void sup_render_reset() override;
    double sup_offset(int64_t host_100ns) override;

    // ---- IDspHost --------------------------------------------------------
    std::shared_ptr<StreamChannel> dsp_channel(Role r) override;
    void dsp_publish(const WfEvent& e) override;
    bool dsp_push_frame(const WfFrame& f) override;
    void dsp_forward_supervisor_events() override;
    size_t dsp_commit_events() override;
    void dsp_notify() override;
    void dsp_stopped(int64_t now) override;

private:
    Session() = default;
    int64_t now() const;
    static int64_t clock_fn(void* ctx) { return static_cast<const Session*>(ctx)->now(); }
    static DWORD WINAPI supervisor_thread_main(void* self);
    void supervisor_run();
    static void notification_fn(void* ctx, const DeviceNotification& n);
    // AP3: delivers one fake endpoint notification through notification_fn.
    void fake_notify_default(EDataFlow flow, ERole role, const std::wstring& id);
    int32_t fake_endpoint_signal(uint32_t signal, int32_t hresult);
    // Stages and commits one event from the calling thread. Only legal while that thread
    // is the sole producer: a fake session, or after DspThread::take_producer().
    void publish_direct(const WfEvent& e);
    // Everything destroy() does before the delete; false when a thread is still running.
    bool shutdown();
    // Mirrors the supervisor's plain counters into atomics. Called on the supervisor
    // thread (real) or inside wf_fake_tick (fake), never from the API thread.
    void capture_supervisor_state();

    // cfg_.mic_device_id is the only field that changes after create(): the API thread
    // writes it in set_mic_device, the MMDevice notification thread reads it.
    mutable SRWLOCK cfg_lock_ = SRWLOCK_INIT;
    WfSessionConfig cfg_{};
    bool fake_ = false;
    bool fallback_to_device_loopback_ = false;
    std::atomic<uint32_t> started_{0};
    std::atomic<uint32_t> stopped_{0};      // wf_session_stop was called
    std::atomic<uint32_t> finished_{0};     // Stopped is queued (set by the producer)
    // Result of the first wf_session_stop. WF_E_FAIL marks the session as failed: the DSP
    // thread did not finish the stop flush in time (destroy() may have to abandon it).
    std::atomic<int32_t>  stop_rc_{WF_OK};
    std::atomic<int64_t> fake_now_{0};

    Timeline timeline_;
    int64_t origin_100ns_ = 0;

    std::shared_ptr<MsgQueue> queue_;
    std::shared_ptr<IPlant> plant_;
    FakePlant* fake_plant_ = nullptr;   // non-owning view of plant_ for the wf_fake_* path

    Supervisor supervisor_;
    DspThread dsp_;

    FrameOutQueue frames_;
    EventQueue events_;      // public queue, produced by the DSP thread only
    EventQueue sup_events_;  // supervisor -> DSP hand-off, forwarded once per tick
    // Everything one DSP tick publishes, gaps coalesced; committed into events_ at the end
    // of the tick. Same single producer as events_.
    EventCoalescer staged_events_;

    mutable SRWLOCK channels_lock_ = SRWLOCK_INIT;
    std::shared_ptr<StreamChannel> channels_[kRoleCount];

    // Supervisor snapshot for wf_session_get_metrics (written by capture_supervisor_state).
    std::atomic<uint32_t> state_{WF_STATE_IDLE};
    std::atomic<uint64_t> restarts_{0};
    std::atomic<uint64_t> watchdog_recoveries_{0};
    std::atomic<uint32_t> orphaned_steps_{0};
    std::atomic<uint32_t> mic_attempt_{0};
    std::atomic<uint32_t> mic_raw_{0};
    std::atomic<uint32_t> ref_mode_actual_{WF_REF_MODE_NONE};
    std::atomic<uint32_t> render_switches_{0};      // AP3 (WfDiagnostics)
    std::atomic<uint32_t> keepalive_failures_{0};
    std::atomic<uint32_t> keepalive_starts_{0};

    // Held from start() until stop() (also released by a failed start and by destroy).
    std::unique_ptr<PowerRequest> power_;
    bool fake_fail_start_ = false;   // WF_FAKE_SIGNAL_SESSION_START_FAILS

    HANDLE event_ = nullptr;
    HANDLE sup_thread_ = nullptr;
    HANDLE sup_stop_ = nullptr;
    NotificationClient notifications_;
    bool notifications_registered_ = false;
};

}  // namespace wf
