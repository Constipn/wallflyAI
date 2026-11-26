// Supervisor: the control plane of a session.
//
// Everything that can block, fail or hang happens here, never on the DSP path:
//   * One message queue fed by the capture threads (StreamChannel::on_event), by
//     IMMNotificationClient and by the API thread. The notification callbacks only
//     post messages - they never touch COM or a device (spec 3.1 "Windows").
//   * One state machine per role (mic / Others / reference) with every constant of
//     spec 3.1 and 3.2; see the kSup* values below.
//   * Every driver call ("step") runs on a disposable worker thread with a 5 s limit.
//     A step that blows the limit is orphaned: the supervisor drops its reference and
//     the worker keeps sole ownership of the stream, the channel and the plant, so it
//     can finish and clean up whenever the driver finally returns. Two orphans mean
//     "Audio driver blocked".
//
// poll(now) does all the work and never blocks: the real session calls it from the
// supervisor thread every 10 ms, the fake session calls it from wf_fake_tick, which is
// why every robustness scenario runs in virtual time.
#pragma once
#include <atomic>
#include <cstdint>
#include <memory>

#include "api/wallfly_audio.h"
#include "core/constants.h"
#include "core/ring.h"
#include "core/types.h"
#include "wasapi/capture_types.h"

namespace wf {

enum class Role : uint32_t { Mic = 0, Others = 1, Reference = 2 };
inline constexpr int kRoleCount = 3;
const char* role_name(Role r);

// ---------------------------------------------------------------------------
// Spec 3.1 "Robustheit" and 3.2 "Fehler", in 100 ns ticks.
// ---------------------------------------------------------------------------
namespace sup {
// "Geraet gilt erst nach dem ersten Puffer als erworben | Frist 2,5 s"
inline constexpr int64_t kFirstBufferTimeout = 25 * k::kTicksPerSecond / 10;      // 2.5 s
// "bei Stopp davor bis zu 3 Neustarts nach 0,25/0,5/0,75 s"
inline constexpr int     kFirstBufferRestarts = 3;
inline constexpr int64_t kFirstBufferDelays[kFirstBufferRestarts] = {
    250 * k::kTicksPerMs, 500 * k::kTicksPerMs, 750 * k::kTicksPerMs};
// "Zeitlimit je Treiber-Schritt | 5 s ... nach 2 verwaisten heisst es Audio driver blocked"
inline constexpr int64_t kStepTimeout = 5 * k::kTicksPerSecond;
inline constexpr int     kOrphanLimit = 2;
// "Versuchszyklus | 3 Versuche im Abstand von 0,5 s; danach degraded"
inline constexpr int     kOpenAttempts   = 3;
inline constexpr int64_t kAttemptSpacing = 500 * k::kTicksPerMs;
// "Hintergrund-Retries | 5 Stueck nach 1, 2, 4, 8, 16 s (Deckel 30 s)"
inline constexpr int     kBackgroundRetries = 5;
inline constexpr int64_t kBackgroundDelays[kBackgroundRetries] = {
    1 * k::kTicksPerSecond, 2 * k::kTicksPerSecond, 4 * k::kTicksPerSecond,
    8 * k::kTicksPerSecond, 16 * k::kTicksPerSecond};
inline constexpr int64_t kBackgroundCap = 30 * k::kTicksPerSecond;
// "Geraetewechsel | Ereignisse auf 200 ms entprellt"
inline constexpr int64_t kDeviceDebounce = 200 * k::kTicksPerMs;
// "Formatwechsel im Geraet | 50 ms zusammenfassen, dann Einheit neu aufbauen"
inline constexpr int64_t kFormatCoalesce = 50 * k::kTicksPerMs;
// "Watchdog | prueft alle 100 ms; 0,5 s ohne Callback fuehrt zur Wiederherstellung"
inline constexpr int64_t kWatchdogInterval = 100 * k::kTicksPerMs;
inline constexpr int64_t kWatchdogTimeout  = 500 * k::kTicksPerMs;
// Spec 3.2: "Faellt der Others-Kanal aus, gibt es Neustarts nach 0,5/1/2 s"
inline constexpr int     kOthersRestarts = 3;
inline constexpr int64_t kOthersDelays[kOthersRestarts] = {
    500 * k::kTicksPerMs, 1 * k::kTicksPerSecond, 2 * k::kTicksPerSecond};
// Peak below which a render endpoint counts as silent (-60 dBFS), used by the
// reference watchdog gate of spec 3.3.
inline constexpr float kSilencePeak = 0.001f;
// AP3 (2026-11-23): a keep-alive that fails to start or whose thread ends on its own is
// reported (WF_EVENT_WARNING) and retried after 0.5 / 1 / 2 s; after the third failure in a
// row the session goes on without it until the render endpoint changes. An exit after this
// much healthy run time starts a new series.
inline constexpr int     kKeepAliveRetries = 3;
inline constexpr int64_t kKeepAliveDelays[kKeepAliveRetries] = {
    500 * k::kTicksPerMs, 1 * k::kTicksPerSecond, 2 * k::kTicksPerSecond};
inline constexpr int64_t kKeepAliveHealthy = 10 * k::kTicksPerSecond;
}  // namespace sup

// IPlant::mic_default_role(): which default the current (or last) mic stream was opened as.
inline constexpr uint32_t kMicRoleExplicit       = 0;   // configured endpoint, or none yet
inline constexpr uint32_t kMicRoleCommunications = 1;   // default communications capture
inline constexpr uint32_t kMicRoleConsole        = 2;   // console default (no comms default)

// Copies `prefix` + `text` into out[cap] (NUL terminated, truncated). Used for the event
// details "mic:<name>", "render:<name>", "fallback:<reason>".
void compose_detail(wchar_t* out, size_t cap, const wchar_t* prefix, const wchar_t* text);
// Appends "|" + `field` to the NUL terminated out[cap] (truncated), or copies `field` when
// out is empty. The detail grammar is '|'-separated fields.
void append_detail(wchar_t* out, size_t cap, const wchar_t* field);

// ---------------------------------------------------------------------------
// Control messages
// ---------------------------------------------------------------------------
struct SupMsg {
    enum class Kind : uint32_t {
        None = 0,
        Start,                 // wf_session_start
        Stop,                  // wf_session_stop
        SetMicDevice,          // wf_session_set_mic_device (device_id)
        StreamStarted,         // IAudioClient::Start succeeded on the capture thread
        StreamFirstPacket,     // first buffer of this attempt arrived
        StreamDeviceLost,      // AUDCLNT_E_DEVICE_INVALIDATED
        StreamError,           // any other fatal stream error (hr)
        StreamStopped,         // capture thread left its loop
        DefaultDeviceChanged,  // IMMNotificationClient, debounced here
        FormatChanged,         // PKEY_AudioEngine_DeviceFormat/OEMFormat of the mic endpoint
                               // changed (Session filters); coalesced here, then confirmed
                               // by IPlant::mic_format_changed
        RenderDefaultChanged,  // AP3: default render endpoint changed. hr 0: the console role
                               // (device_id = the new default), hr 1: the multimedia role of
                               // the same pair (only extends a pending window). Debounced
                               // 200 ms; ignored while render_device_id is pinned
        RenderEndpointLost,    // AP3: the pinned render endpoint (device_id) was removed or
                               // left DEVICE_STATE_ACTIVE (hr = new state); debounced 200 ms,
                               // then the render side falls back to the default
    };
    Kind     kind = Kind::None;
    uint32_t role = 0;            // Role
    uint32_t attempt = 0;         // attempt id of the reporting channel
    int32_t  hr = 0;
    int64_t  at_100ns = 0;
    wchar_t  device_id[256] = {};
};

// Bounded multi-producer / single-consumer message ring. Producers are capture threads,
// the MMDevice notification thread and the API thread; the consumer is poll(). Guarded
// by an SRWLOCK because it is the control plane - the DSP path never touches it.
class MsgQueue {
public:
    MsgQueue();
    ~MsgQueue();
    MsgQueue(const MsgQueue&) = delete;
    MsgQueue& operator=(const MsgQueue&) = delete;

    bool post(const SupMsg& m);          // false when the ring is full (counted)
    bool take(SupMsg& out);              // false when empty
    HANDLE wake_handle() const { return wake_; }
    uint64_t dropped() const { return dropped_.load(std::memory_order_relaxed); }

private:
    static constexpr size_t kCapacity = 256;
    SRWLOCK lock_ = SRWLOCK_INIT;
    SupMsg  buf_[kCapacity];
    size_t  head_ = 0;
    size_t  count_ = 0;
    HANDLE  wake_ = nullptr;
    std::atomic<uint64_t> dropped_{0};
};

// ---------------------------------------------------------------------------
// Stream channel: one attempt of one role
// ---------------------------------------------------------------------------

// Owns the platform stream object of an attempt. Its destructor stops the stream, which
// is what makes an orphaned step safe: whoever holds the last reference cleans up.
class StreamHolder {
public:
    virtual ~StreamHolder() = default;
    // Spec 3.3: with a keep-alive render stream in use, a position that stops advancing
    // tells a stalled audio engine from genuine silence. Default: "no keep-alive here".
    virtual bool keepalive_clock_stalled(int64_t now_100ns) {
        (void)now_100ns;
        return false;
    }
};

struct StreamChannel final : public IPacketSink {
    StreamChannel(Role r, uint32_t attempt, bool app_src, std::shared_ptr<MsgQueue> q)
        : role(r), attempt_id(attempt), app_source(app_src), queue(std::move(q)) {}
    ~StreamChannel() override = default;

    const Role     role;
    const uint32_t attempt_id;
    // Spec 3.2: an include-mode app source may legitimately fall silent, so it is exempt
    // from the 0.5 s watchdog and from the discontinuity gaps.
    const bool     app_source;
    std::shared_ptr<MsgQueue> queue;
    std::unique_ptr<StreamHolder> holder;   // set by the plant's start step

    AudioRing ring;
    std::atomic<int64_t>  last_packet_100ns{0};   // arrival time on the session clock
    // Longest time between two consecutive packet arrivals of this attempt (diagnostics,
    // WfDiagnostics.capture_gap_max_ms_*). Written by the capture thread only.
    std::atomic<int64_t>  arrival_gap_max_100ns{0};
    std::atomic<uint64_t> packets{0};
    std::atomic<uint32_t> rate{0};
    std::atomic<uint32_t> raw_accepted{0};
    std::atomic<uint32_t> live{0};                // 1 between Started and Stopped
    // Spec 3.1 microphone fallback, written by the plant's start step before it completes
    // (ctx->done release) and read by the supervisor after it (acquire): the configured
    // endpoint failed and the system default was opened instead. Empty detail = no fallback.
    wchar_t  fallback_detail[128] = {};           // "fallback:<reason>", the DEVICE_ACQUIRED detail
    int32_t  fallback_hr = 0;                     // the configured endpoint's failure
    // AP3, same protocol (written by the start step, read after done): the endpoint the
    // stream runs on (empty for process loopback) and its detail label "mic:<name>" /
    // "render:<name>" for DEVICE_ACQUIRED / DEVICE_LOST.
    wchar_t  endpoint_id[256] = {};
    wchar_t  endpoint_label[128] = {};
    // AP3, set by the supervisor before the channel is published: the WF_GAP_* reason the DSP
    // gives the hole in front of this attempt's first packet (the normalizer's re-anchor
    // gap, which carries the true position and length) instead of
    // WF_GAP_TIMESTAMP_DISCONTINUITY. WF_GAP_NONE = no planned hand-over.
    uint32_t handover_gap = WF_GAP_NONE;

    // Shared entry point for the real capture thread and for wf_fake_push.
    void deliver(const WfPacket& p, int64_t arrival_100ns);
    void post(SupMsg::Kind kind, int32_t hr, int64_t at_100ns);

    // IPacketSink - called on the capture thread.
    void on_packet(const WfPacket& p) override;
    void on_event(const StreamEvent& e) override;
};

// ---------------------------------------------------------------------------
// Plant: the thing that actually opens and closes streams
// ---------------------------------------------------------------------------
struct PlantParams {
    Role     role = Role::Mic;
    uint32_t others_mode = WF_OTHERS_SYSTEM_EXCLUDE;
    uint32_t target_pid = 0;
    uint32_t prefer_raw = 1;
    uint32_t keepalive_mode = WF_KEEPALIVE_NONE;
    wchar_t  mic_device_id[256] = {};
    wchar_t  render_device_id[256] = {};
};

class IPlant;

// One driver step. Heap allocated and reference counted so an orphaned worker keeps
// everything it touches alive; the supervisor simply drops its own reference.
struct StepCtx {
    // KeepAliveStart: start the silent render stream on params.render_device_id (empty = the
    // default console render endpoint; a pinned id that fails falls back to the default)
    // and, once it runs, stop the previous one (make before break). KeepAliveStop: stop it.
    // Neither has a channel.
    enum class Kind : uint32_t { Start, Stop, KeepAliveStart, KeepAliveStop };
    Kind        kind = Kind::Start;
    PlantParams params;
    std::shared_ptr<IPlant> plant;
    std::shared_ptr<StreamChannel> channel;
    std::atomic<uint32_t> done{0};
    std::atomic<int32_t>  hr{0};
    // Results of a keep-alive step (written before `done`): the endpoint it resolved to,
    // its "render:<name>" label, and "fallback:<reason>" + hr when a pinned id failed.
    wchar_t endpoint_id[256] = {};
    wchar_t endpoint_label[128] = {};
    wchar_t fallback_detail[128] = {};
    int32_t fallback_hr = 0;
    // KeepAliveStart only: a failing explicit id may fall back to the default (modes 0/1 with
    // a pinned render_device_id). False in mode 2, where the keep-alive must run on exactly
    // the endpoint the loopback runs on.
    bool    allow_fallback = false;
};

class IPlant {
public:
    virtual ~IPlant() = default;
    // Executes the step - inline (fake) or on a disposable worker thread (real).
    virtual void launch(const std::shared_ptr<StepCtx>& ctx) = 0;
    // Performed by launch(); split out so both plants share the completion protocol.
    virtual int32_t do_start(StepCtx& ctx) = 0;
    virtual void    do_stop(StepCtx& ctx) = 0;
    virtual std::shared_ptr<StreamChannel> make_channel(Role r, uint32_t attempt, bool app_source) = 0;
    // Spec 3.3: with a keep-alive in use the reference watchdog may only fire when the
    // keep-alive clock has stalled or a render endpoint actually carries signal.
    virtual bool reference_watchdog_armed(int64_t now_100ns) = 0;
    // Confirms that the mic endpoint really changed format before the supervisor rebuilds
    // the unit (spec 3.1 "Formatwechsel im Geraet"): true when the format a stream opened
    // like the current mic stream (same endpoint, same RAW option and category) would get
    // NOW differs from the format the current stream was opened with, compared by sample
    // rate, channel count and sample format. The open-time format is recorded by every mic
    // open and never overwritten by this check, so an unchanged endpoint answers false no
    // matter how often it is asked. False while no mic stream is open (the next open
    // reads the current format anyway). `device_id` is the configured id, informational.
    virtual bool mic_format_changed(const wchar_t* device_id) = 0;
    // The endpoint id the current mic stream was opened on (the resolved default endpoint
    // when no id is configured), copied into out[cap]. Called on the MMDevice notification
    // thread, so implementations only lock and copy - no COM. False while no mic is open.
    virtual bool mic_endpoint(wchar_t* out, size_t cap) = 0;
    // kMicRole* of the current (or last) mic open. Notification thread: lock and copy only.
    virtual uint32_t mic_default_role() = 0;
    // AP3: performs a KeepAliveStart / KeepAliveStop step (see StepCtx). The keep-alive is
    // owned by the plant, not by a stream: it follows the render endpoint on its own.
    virtual int32_t do_keepalive(StepCtx& ctx) = 0;
    // Polled by the supervisor: true once when the running keep-alive's thread ended on its
    // own (it is then forgotten); `hr` receives why.
    virtual bool keepalive_exited(int32_t* hr) = 0;
    // Friendly name of endpoint `id` of the given flow for event details; an empty id names
    // the default the plant would open now (render: console; capture: communications, else
    // console). "unknown endpoint" when it cannot be read. Supervisor thread (MTA) only.
    virtual void endpoint_name(bool render, const wchar_t* id, wchar_t* out, size_t cap) = 0;
    // Waits for outstanding driver workers at shutdown; the rest are abandoned.
    virtual void wait_workers(uint32_t timeout_ms) { (void)timeout_ms; }
};

// Runs ctx->kind and publishes the result. Used by both plants.
void run_step(const std::shared_ptr<StepCtx>& ctx);

// Spec 3.1 "Schlaegt die gespeicherte fehl, wird im selben Versuch der Standard probiert":
// every failure of the configured endpoint allows the fallback except E_ACCESSDENIED (a
// permission error stops every retry, so it must not be hidden by another device).
bool mic_fallback_allowed(int32_t hr);
// Marks `ch` as running on the system default instead of the configured endpoint; `reason`
// ("selected microphone missing", "<name> unavailable") becomes the DEVICE_ACQUIRED detail
// "fallback:<reason>", truncated to WfEvent.detail.
void set_mic_fallback(StreamChannel& ch, int32_t configured_hr, const wchar_t* reason);

// ---------------------------------------------------------------------------
// Host callbacks
// ---------------------------------------------------------------------------
class ISupervisorHost {
public:
    virtual ~ISupervisorHost() = default;
    // Queue one event (the supervisor's own SPSC ring; the DSP tick forwards it).
    virtual void sup_publish(const WfEvent& e) = 0;
    // A role's stream was replaced; the DSP thread must drain this channel from now on.
    // `ch` may be null, which means "this role has no stream at the moment".
    virtual void sup_set_channel(Role r, std::shared_ptr<StreamChannel> ch) = 0;
    // Spec 3.3 step 5: the only two reasons for an AEC filter reset.
    virtual void sup_mic_reset(bool format_change) = 0;
    // AP3: the third one - the echo path moved to another render endpoint.
    virtual void sup_render_reset() = 0;
    // Meeting offset of a host timestamp (spec 3.6).
    virtual double sup_offset(int64_t host_100ns) = 0;
};

// ---------------------------------------------------------------------------
// Supervisor
// ---------------------------------------------------------------------------
class Supervisor {
public:
    struct Counters {
        uint64_t restarts = 0;
        uint64_t watchdog_recoveries = 0;
        uint32_t orphaned_steps = 0;
        uint32_t mic_attempt = 0;
        uint32_t others_attempt = 0;
        uint32_t mic_raw = 0;
        uint32_t ref_mode_actual = WF_REF_MODE_NONE;
        // AP3 (WfDiagnostics)
        uint32_t render_switches = 0;
        uint32_t keepalive_failures = 0;
        uint32_t keepalive_starts = 0;
    };

    void configure(ISupervisorHost* host, std::shared_ptr<IPlant> plant,
                   std::shared_ptr<MsgQueue> queue, const PlantParams& params,
                   bool reference_separate);

    // Drains the message queue, advances every timer and launches whatever is due.
    void poll(int64_t now_100ns);

    void request_start(int64_t now_100ns);
    void request_stop(int64_t now_100ns);

    bool stop_complete() const { return stop_requested_ && all_idle(); }
    bool started() const { return started_; }
    uint32_t state() const;
    const Counters& counters() const { return counters_; }
    uint32_t attempt_of(Role r) const { return roles_[static_cast<int>(r)].attempt_id; }
    bool running(Role r) const {
        return roles_[static_cast<int>(r)].phase == Phase::Running;
    }
    bool active(Role r) const { return role_active_[static_cast<int>(r)]; }

private:
    enum class Phase : uint32_t {
        Idle = 0,     // no stream, nothing scheduled
        Pending,      // a start or stop step is in flight
        AwaitFirst,   // started; waiting for the first buffer (2.5 s)
        Running,
        Wait,         // waiting for retry_at before the next start
        Degraded,     // mic gave up its attempt cycle; background retries may follow
        Failed,       // permission denied, driver blocked or retries exhausted
        Stopped,
    };
    enum class After : uint32_t { None, Retry, StartNow, GiveUp, Final };

    struct RoleState {
        Phase    phase = Phase::Idle;
        After    after = After::None;
        uint32_t attempt_id = 0;
        int      open_attempts = 0;
        int      first_buffer_restarts = 0;
        int      background_retries = 0;
        int      others_restarts = 0;
        int64_t  retry_at = 0;
        int64_t  first_buffer_deadline = 0;
        int64_t  step_deadline = 0;
        bool     reference_missing = false;
        // AP3: a planned rebuild (render switch) arrived while a start step was in flight;
        // the stream that step opens is torn down and reopened once it completes.
        bool     rebuild_pending = false;
        std::shared_ptr<StepCtx>       step;
        std::shared_ptr<StreamChannel> channel;
    };

    // AP3: the silent keep-alive render stream, owned by the plant and run by its own steps
    // so it can follow the render endpoint without touching any capture stream.
    struct KeepAlive {
        bool     want = false;          // the session is running (not stopping)
        bool     running = false;       // a keep-alive runs (on `on`)
        bool     dirty = false;         // (re)start on `target` is due
        bool     gave_up = false;       // kKeepAliveRetries failures in a row
        int      failures = 0;          // consecutive
        int64_t  retry_at = 0;          // 0 = now
        int64_t  started_at = 0;
        int64_t  step_deadline = 0;
        wchar_t  target[256] = {};      // "" = the plant's default rule
        wchar_t  on[256] = {};          // endpoint it runs on
        wchar_t  label[128] = {};       // "render:<name>" of `on` (or of the failed target)
        std::shared_ptr<StepCtx> step;
    };

    RoleState& rs(Role r) { return roles_[static_cast<int>(r)]; }
    const RoleState& rs(Role r) const { return roles_[static_cast<int>(r)]; }
    bool all_idle() const;
    bool device_mode() const { return params_.others_mode == WF_OTHERS_DEVICE_LOOPBACK; }
    bool render_pinned() const { return params_.render_device_id[0] != 0; }

    // AP3 render side.
    void handle_render_timer(int64_t now);
    // Moves the render side to `target` (empty = the default): device mode rebuilds the
    // Others loopback (the keep-alive follows its endpoint), modes 0/1 move the keep-alive.
    void move_render(const wchar_t* target, bool lost, int64_t now);
    void rebuild(Role r, int64_t now);
    void keepalive_retarget(const wchar_t* target, int64_t now, bool force = false);
    void launch_keepalive(StepCtx::Kind kind, int64_t now);
    bool service_keepalive_progress(int64_t now);   // true when anything happened
    void keepalive_failed(int32_t hr, bool exited, const wchar_t* label, int64_t now);
    void on_render_channel_started(int64_t now, bool another_open_follows);
    void note_render_endpoint(const wchar_t* id);
    void abandon_pinned_render(const wchar_t* fallback_detail, int32_t hr, int64_t now,
                               bool announce);

    void handle(const SupMsg& m, int64_t now);
    void begin_start(Role r, int64_t now);
    void begin_stop(Role r, After after, int64_t now);
    void on_step_finished(Role r, int32_t hr, int64_t now);
    void on_start_failed(Role r, int32_t hr, int64_t now);
    void schedule_after_failure(Role r, int64_t now);
    void on_first_packet(Role r, uint32_t attempt, int64_t now);
    void check_timers(int64_t now);
    bool check_timers_progress(int64_t now);   // check_timers + "did anything fire"
    void check_watchdog(int64_t now);
    void give_up(Role r, int64_t now);
    void publish(uint32_t kind, Role r, uint32_t attempt, int64_t now, int32_t hr = 0,
                 const wchar_t* detail = nullptr);
    void publish_gap(Role r, uint32_t reason, int64_t now, double duration_sec);
    void note_orphan(Role r, int64_t now);

    ISupervisorHost* host_ = nullptr;
    std::shared_ptr<IPlant> plant_;
    std::shared_ptr<MsgQueue> queue_;
    PlantParams params_;
    RoleState roles_[kRoleCount];
    bool role_active_[kRoleCount] = {false, false, false};

    bool started_ = false;
    bool stop_requested_ = false;
    bool driver_blocked_ = false;
    bool others_failed_ = false;
    int64_t next_watchdog_ = 0;
    int64_t device_change_at_ = 0;     // 0 = nothing pending
    int64_t format_change_at_ = 0;
    wchar_t pending_device_[256] = {};
    Counters counters_;

    // AP3 render endpoint following.
    int64_t render_change_at_ = 0;     // debounce deadline, 0 = nothing pending
    bool    render_pending_lost_ = false;
    wchar_t render_pending_id_[256] = {};   // the newest console default ("" = unknown)
    wchar_t render_current_[256] = {};      // endpoint the render side runs on now
    bool    render_reset_requested_ = false;  // a planned move already reset the canceller
    // WF_GAP_* reason for the hole in front of the next attempt of each role that delivers
    // (WF_GAP_NONE = ordinary restart, the DSP's TimestampDiscontinuity).
    uint32_t next_handover_[kRoleCount] = {WF_GAP_NONE, WF_GAP_NONE, WF_GAP_NONE};
    KeepAlive ka_;
};

}  // namespace wf
