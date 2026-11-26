// Supervisor implementation. Every transition is driven from poll(now); nothing here
// sleeps, so the fake session can replay hours of robustness behaviour in virtual time.
#include "session/supervisor.h"

#include <cstring>
#include <cwchar>

#include "session/event_queue.h"
#include "wasapi/clock.h"

namespace wf {

namespace {

constexpr int32_t kAccessDenied = static_cast<int32_t>(0x80070005);  // E_ACCESSDENIED
constexpr int32_t kStepOrphaned = static_cast<int32_t>(0x80004004);  // E_ABORT

inline uint32_t source_of(Role r) {
    switch (r) {
        case Role::Mic: return WF_SOURCE_ME;
        case Role::Others: return WF_SOURCE_OTHERS;
        case Role::Reference: return WF_SOURCE_REFERENCE;
    }
    return WF_SOURCE_SESSION;
}

inline bool failed(int32_t hr) { return hr < 0; }

constexpr int32_t kDeviceInvalidated = static_cast<int32_t>(0x88890004);  // AUDCLNT_E_DEVICE_INVALIDATED

template <size_t N>
void copy_w(wchar_t (&dst)[N], const wchar_t* src) {
    size_t i = 0;
    for (; src && src[i] && i + 1 < N; ++i) dst[i] = src[i];
    dst[i] = 0;
}

// Endpoint ids are compared the way MMDevice treats them: case-insensitive.
bool same_endpoint_id(const wchar_t* a, const wchar_t* b) {
    if (!a || !b || !a[0] || !b[0]) return false;
    return CompareStringOrdinal(a, -1, b, -1, TRUE) == CSTR_EQUAL;
}

}  // namespace

void compose_detail(wchar_t* out, size_t cap, const wchar_t* prefix, const wchar_t* text) {
    if (!out || cap == 0) return;
    size_t i = 0;
    for (size_t j = 0; prefix && prefix[j] && i + 1 < cap; ++j, ++i) out[i] = prefix[j];
    for (size_t j = 0; text && text[j] && i + 1 < cap; ++j, ++i) out[i] = text[j];
    out[i] = 0;
}

void append_detail(wchar_t* out, size_t cap, const wchar_t* field) {
    if (!out || cap == 0 || !field || !field[0]) return;
    size_t i = 0;
    while (i + 1 < cap && out[i]) ++i;
    if (i > 0 && i + 1 < cap) out[i++] = L'|';
    for (size_t j = 0; field[j] && i + 1 < cap; ++j, ++i) out[i] = field[j];
    out[i] = 0;
}

const char* role_name(Role r) {
    switch (r) {
        case Role::Mic: return "mic";
        case Role::Others: return "others";
        case Role::Reference: return "reference";
    }
    return "?";
}

// ---------------------------------------------------------------------------
// MsgQueue
// ---------------------------------------------------------------------------
MsgQueue::MsgQueue() { wake_ = CreateEventW(nullptr, FALSE, FALSE, nullptr); }

MsgQueue::~MsgQueue() {
    if (wake_) CloseHandle(wake_);
}

bool MsgQueue::post(const SupMsg& m) {
    bool ok = false;
    AcquireSRWLockExclusive(&lock_);
    if (count_ < kCapacity) {
        buf_[(head_ + count_) % kCapacity] = m;
        ++count_;
        ok = true;
    }
    ReleaseSRWLockExclusive(&lock_);
    if (!ok) {
        dropped_.fetch_add(1, std::memory_order_relaxed);
    } else if (wake_) {
        SetEvent(wake_);
    }
    return ok;
}

bool MsgQueue::take(SupMsg& out) {
    bool ok = false;
    AcquireSRWLockExclusive(&lock_);
    if (count_ > 0) {
        out = buf_[head_];
        head_ = (head_ + 1) % kCapacity;
        --count_;
        ok = true;
    }
    ReleaseSRWLockExclusive(&lock_);
    return ok;
}

// ---------------------------------------------------------------------------
// StreamChannel
// ---------------------------------------------------------------------------
void StreamChannel::post(SupMsg::Kind kind, int32_t hr, int64_t at_100ns) {
    if (!queue) return;
    SupMsg m;
    m.kind = kind;
    m.role = static_cast<uint32_t>(role);
    m.attempt = attempt_id;
    m.hr = hr;
    m.at_100ns = at_100ns;
    queue->post(m);
}

void StreamChannel::deliver(const WfPacket& p, int64_t arrival_100ns) {
    if (p.frames == 0) return;
    rate.store(p.sample_rate, std::memory_order_relaxed);
    // A WASAPI packet can be larger than one ring slot (480 frames), so split it and
    // advance the timestamp and the device position per chunk. Only the first chunk
    // keeps the DATA_DISCONTINUITY bit so the normalizer logs one gap, not several.
    const uint32_t slot = AudioRing::kSlotFrames;
    for (uint32_t off = 0; off < p.frames; off += slot) {
        const uint32_t n = (p.frames - off) < slot ? (p.frames - off) : slot;
        int64_t ts = p.qpc_100ns;
        uint32_t flags = p.flags;
        if (off != 0) {
            flags &= ~kBufDataDiscontinuity;
            if (p.sample_rate != 0) {
                ts += static_cast<int64_t>(static_cast<double>(off) * k::kTicksPerSecond /
                                               p.sample_rate +
                                           0.5);
            }
        }
        ring.push(p.mono ? p.mono + off : nullptr, n, ts, p.device_position + off, flags);
    }
    // One writer (this capture thread), so the max needs no compare-exchange.
    const int64_t prev_arrival = last_packet_100ns.load(std::memory_order_relaxed);
    if (prev_arrival != 0 && arrival_100ns - prev_arrival >
                                 arrival_gap_max_100ns.load(std::memory_order_relaxed)) {
        arrival_gap_max_100ns.store(arrival_100ns - prev_arrival, std::memory_order_relaxed);
    }
    last_packet_100ns.store(arrival_100ns, std::memory_order_release);
    const uint64_t before = packets.fetch_add(1, std::memory_order_acq_rel);
    if (before == 0) post(SupMsg::Kind::StreamFirstPacket, 0, arrival_100ns);
}

void StreamChannel::on_packet(const WfPacket& p) { deliver(p, qpc_now_100ns()); }

void StreamChannel::on_event(const StreamEvent& e) {
    const int64_t now = qpc_now_100ns();
    switch (e.kind) {
        case StreamEvent::Started:
            live.store(1, std::memory_order_relaxed);
            post(SupMsg::Kind::StreamStarted, static_cast<int32_t>(e.hr), now);
            break;
        case StreamEvent::FirstPacket:
            break;  // deliver() posts it, so the fake path behaves identically
        case StreamEvent::DeviceInvalidated:
            post(SupMsg::Kind::StreamDeviceLost, static_cast<int32_t>(e.hr), now);
            break;
        case StreamEvent::Error:
            post(SupMsg::Kind::StreamError, static_cast<int32_t>(e.hr), now);
            break;
        case StreamEvent::Stopped:
            live.store(0, std::memory_order_relaxed);
            post(SupMsg::Kind::StreamStopped, static_cast<int32_t>(e.hr), now);
            break;
    }
}

// ---------------------------------------------------------------------------
// Step execution
// ---------------------------------------------------------------------------
void run_step(const std::shared_ptr<StepCtx>& ctx) {
    int32_t hr = 0;
    switch (ctx->kind) {
        case StepCtx::Kind::Start: hr = ctx->plant->do_start(*ctx); break;
        case StepCtx::Kind::Stop: ctx->plant->do_stop(*ctx); break;
        case StepCtx::Kind::KeepAliveStart:
        case StepCtx::Kind::KeepAliveStop: hr = ctx->plant->do_keepalive(*ctx); break;
    }
    ctx->hr.store(hr, std::memory_order_relaxed);
    ctx->done.store(1, std::memory_order_release);
}

bool mic_fallback_allowed(int32_t hr) { return failed(hr) && hr != kAccessDenied; }

void set_mic_fallback(StreamChannel& ch, int32_t configured_hr, const wchar_t* reason) {
    static const wchar_t kPrefix[] = L"fallback:";
    const size_t cap = sizeof(ch.fallback_detail) / sizeof(ch.fallback_detail[0]);
    size_t i = 0;
    for (; kPrefix[i] && i + 1 < cap; ++i) ch.fallback_detail[i] = kPrefix[i];
    for (size_t j = 0; reason && reason[j] && i + 1 < cap; ++j, ++i) ch.fallback_detail[i] = reason[j];
    ch.fallback_detail[i] = 0;
    ch.fallback_hr = configured_hr;
}

// ---------------------------------------------------------------------------
// Supervisor
// ---------------------------------------------------------------------------
void Supervisor::configure(ISupervisorHost* host, std::shared_ptr<IPlant> plant,
                           std::shared_ptr<MsgQueue> queue, const PlantParams& params,
                           bool reference_separate) {
    host_ = host;
    plant_ = std::move(plant);
    queue_ = std::move(queue);
    params_ = params;
    role_active_[static_cast<int>(Role::Mic)] = true;
    role_active_[static_cast<int>(Role::Others)] = true;
    role_active_[static_cast<int>(Role::Reference)] = reference_separate;
    counters_.ref_mode_actual = params_.others_mode == WF_OTHERS_DEVICE_LOOPBACK
                                    ? WF_REF_MODE_DEVICE_LOOPBACK
                                    : WF_REF_MODE_PROCESS_EXCLUDE;
}

void Supervisor::request_start(int64_t now) {
    SupMsg m;
    m.kind = SupMsg::Kind::Start;
    m.at_100ns = now;
    queue_->post(m);
}

void Supervisor::request_stop(int64_t now) {
    SupMsg m;
    m.kind = SupMsg::Kind::Stop;
    m.at_100ns = now;
    queue_->post(m);
}

bool Supervisor::all_idle() const {
    if (ka_.step || ka_.running) return false;
    for (int i = 0; i < kRoleCount; ++i) {
        if (!role_active_[i]) continue;
        const RoleState& s = roles_[i];
        if (s.step) return false;
        if (s.phase != Phase::Stopped && s.phase != Phase::Idle) return false;
    }
    return true;
}

uint32_t Supervisor::state() const {
    if (stop_requested_) return all_idle() ? WF_STATE_STOPPED : WF_STATE_STOPPING;
    if (!started_) return WF_STATE_IDLE;
    if (driver_blocked_) return WF_STATE_DRIVER_BLOCKED;
    if (others_failed_) return WF_STATE_OTHERS_FAILED;
    const RoleState& mic = roles_[static_cast<int>(Role::Mic)];
    if (mic.phase == Phase::Degraded || mic.phase == Phase::Failed) return WF_STATE_DEGRADED;
    for (int i = 0; i < kRoleCount; ++i) {
        if (!role_active_[i]) continue;
        if (roles_[i].phase != Phase::Running) return WF_STATE_STARTING;
    }
    return WF_STATE_RUNNING;
}

void Supervisor::publish(uint32_t kind, Role r, uint32_t attempt, int64_t now, int32_t hr,
                         const wchar_t* detail) {
    if (!host_) return;
    WfEvent e = make_event(kind, source_of(r), attempt, host_->sup_offset(now));
    e.hresult = hr;
    set_detail(e, detail);
    host_->sup_publish(e);
}

void Supervisor::publish_gap(Role r, uint32_t reason, int64_t now, double duration_sec) {
    if (!host_) return;
    WfEvent e = make_event(WF_EVENT_GAP, source_of(r), rs(r).attempt_id, host_->sup_offset(now));
    e.reason = reason;
    e.duration_sec = duration_sec < 0.0 ? 0.0 : duration_sec;
    host_->sup_publish(e);
}

void Supervisor::note_orphan(Role r, int64_t now) {
    ++counters_.orphaned_steps;
    if (counters_.orphaned_steps >= static_cast<uint32_t>(sup::kOrphanLimit) && !driver_blocked_) {
        driver_blocked_ = true;
        publish(WF_EVENT_DRIVER_BLOCKED, r, rs(r).attempt_id, now, 0, L"audio driver blocked");
    }
}

void Supervisor::begin_start(Role r, int64_t now) {
    RoleState& s = rs(r);
    ++s.attempt_id;
    if (r == Role::Mic) counters_.mic_attempt = s.attempt_id;
    if (r == Role::Others) counters_.others_attempt = s.attempt_id;
    if (s.attempt_id > 1) {
        ++counters_.restarts;
        publish(WF_EVENT_RESTARTING, r, s.attempt_id, now);
    }
    const bool app_source = (r == Role::Others && params_.others_mode == WF_OTHERS_PROCESS_INCLUDE);
    s.channel = plant_->make_channel(r, s.attempt_id, app_source);
    // AP3: a planned hand-over (render switch, lost render endpoint) is reported as ONE gap
    // with this reason: the DSP's re-anchor gap in front of the first packet that arrives.
    if (s.channel) s.channel->handover_gap = next_handover_[static_cast<int>(r)];

    auto ctx = std::make_shared<StepCtx>();
    ctx->kind = StepCtx::Kind::Start;
    ctx->params = params_;
    ctx->params.role = r;
    ctx->plant = plant_;
    ctx->channel = s.channel;

    s.step = ctx;
    s.step_deadline = now + sup::kStepTimeout;
    s.phase = Phase::Pending;
    s.after = After::None;
    plant_->launch(ctx);
}

void Supervisor::begin_stop(Role r, After after, int64_t now) {
    RoleState& s = rs(r);
    s.after = after;
    if (!s.channel) {
        // Nothing to tear down - apply the follow-up action straight away.
        on_step_finished(r, 0, now);
        return;
    }
    auto ctx = std::make_shared<StepCtx>();
    ctx->kind = StepCtx::Kind::Stop;
    ctx->params = params_;
    ctx->params.role = r;
    ctx->plant = plant_;
    ctx->channel = s.channel;

    s.step = ctx;
    s.step_deadline = now + sup::kStepTimeout;
    s.phase = Phase::Pending;
    if (host_) host_->sup_set_channel(r, nullptr);   // the DSP stops draining immediately
    plant_->launch(ctx);
}

// Entry point for both "the step finished" and "there was no step to run".
void Supervisor::on_step_finished(Role r, int32_t hr, int64_t now) {
    (void)hr;   // a stop step reports no useful status; failures surface as stream events
    RoleState& s = rs(r);
    const After after = s.after;
    s.after = After::None;
    s.channel.reset();
    if (host_) host_->sup_set_channel(r, nullptr);

    switch (after) {
        case After::StartNow:
            begin_start(r, now);
            return;
        case After::Retry:
            s.phase = (r == Role::Mic && s.background_retries > 0) ? Phase::Degraded : Phase::Wait;
            return;
        case After::GiveUp:
            give_up(r, now);
            return;
        case After::Final:
            s.phase = Phase::Stopped;
            return;
        case After::None:
        default:
            s.phase = (r == Role::Mic) ? Phase::Degraded : Phase::Failed;
            s.retry_at = 0;
            return;
    }
}

void Supervisor::on_start_failed(Role r, int32_t hr, int64_t now) {
    RoleState& s = rs(r);
    s.channel.reset();
    if (host_) host_->sup_set_channel(r, nullptr);

    // Spec 3.1: "Berechtigungsfehler stoppt alle Retries." Only E_ACCESSDENIED counts;
    // AUDCLNT_E_DEVICE_IN_USE is an exclusive-mode owner, not a permission problem, and
    // is retried like any other failure.
    if (hr == kAccessDenied) {
        publish(WF_EVENT_PERMISSION_DENIED, r, s.attempt_id, now, hr, L"microphone access denied");
        s.phase = Phase::Failed;
        s.retry_at = 0;
        return;
    }
    s.after = After::None;
    schedule_after_failure(r, now);
}

// Decides the next step after a failure, sets retry_at and applies the resulting phase.
void Supervisor::schedule_after_failure(Role r, int64_t now) {
    RoleState& s = rs(r);
    if (driver_blocked_) {
        give_up(r, now);
        return;
    }
    if (r == Role::Mic) {
        if (s.background_retries > 0) {                     // background ladder
            if (s.background_retries < sup::kBackgroundRetries) {
                int64_t d = sup::kBackgroundDelays[s.background_retries];
                if (d > sup::kBackgroundCap) d = sup::kBackgroundCap;
                s.retry_at = now + d;
                ++s.background_retries;
                s.phase = Phase::Degraded;
            } else {
                s.retry_at = 0;
                s.phase = Phase::Degraded;                  // no retries left
            }
            return;
        }
        ++s.open_attempts;
        if (s.open_attempts < sup::kOpenAttempts) {
            s.retry_at = now + sup::kAttemptSpacing;
            s.phase = Phase::Wait;
            return;
        }
        give_up(r, now);
        return;
    }
    // Others and the separate reference share the 0.5 / 1 / 2 s ladder of spec 3.2.
    if (s.background_retries > 0) {                         // reference background ladder
        if (s.background_retries < sup::kBackgroundRetries) {
            int64_t d = sup::kBackgroundDelays[s.background_retries];
            if (d > sup::kBackgroundCap) d = sup::kBackgroundCap;
            s.retry_at = now + d;
            ++s.background_retries;
            s.phase = Phase::Degraded;
        } else {
            s.retry_at = 0;
            s.phase = Phase::Failed;
        }
        return;
    }
    if (s.others_restarts < sup::kOthersRestarts) {
        s.retry_at = now + sup::kOthersDelays[s.others_restarts];
        ++s.others_restarts;
        s.phase = Phase::Wait;
        return;
    }
    give_up(r, now);
}

void Supervisor::give_up(Role r, int64_t now) {
    RoleState& s = rs(r);
    s.retry_at = 0;
    if (r == Role::Mic) {
        // Spec 3.1: "3 Versuche im Abstand von 0,5 s; danach degraded (Aufnahme laeuft nur
        // mit Others weiter)", then the 1/2/4/8/16 s background ladder.
        publish(WF_EVENT_DEGRADED, r, s.attempt_id, now, 0, L"microphone unavailable");
        s.phase = Phase::Degraded;
        if (!driver_blocked_) {
            s.background_retries = 1;
            s.retry_at = now + sup::kBackgroundDelays[0];
        }
        return;
    }
    if (r == Role::Others) {
        others_failed_ = true;
        publish(WF_EVENT_OTHERS_FAILED, r, s.attempt_id, now, 0, L"meeting audio unavailable");
        s.phase = Phase::Failed;
        return;
    }
    // Spec 3.2: "Fehlt im App-Modus die Referenz ... das Mikro laeuft ungefiltert weiter,
    // Retries wie beim Mikro."
    s.reference_missing = true;
    publish(WF_EVENT_REFERENCE_MISSING, r, s.attempt_id, now, 0, L"echo reference unavailable");
    if (driver_blocked_) {
        s.phase = Phase::Failed;
        return;
    }
    s.phase = Phase::Degraded;
    s.background_retries = 1;
    s.retry_at = now + sup::kBackgroundDelays[0];
}

void Supervisor::on_first_packet(Role r, uint32_t attempt, int64_t now) {
    RoleState& s = rs(r);
    if (attempt != s.attempt_id) return;            // stale attempt
    if (s.phase != Phase::AwaitFirst && s.phase != Phase::Running) return;
    const bool was_awaiting = (s.phase != Phase::Running);
    s.phase = Phase::Running;
    s.open_attempts = 0;
    s.first_buffer_restarts = 0;
    s.others_restarts = 0;
    s.background_retries = 0;
    s.retry_at = 0;
    if (!was_awaiting) return;
    if (s.channel) {
        if (r == Role::Mic) counters_.mic_raw = s.channel->raw_accepted.load(std::memory_order_relaxed);
    }
    // The hand-over this attempt closes is accounted for (its gap comes from the DSP).
    next_handover_[static_cast<int>(r)] = WF_GAP_NONE;
    // Spec 3.1: a mic that runs on the system default in place of the configured endpoint
    // says so ("fallback:<reason>"), so the app can show "Name - Fallback (reason)". The
    // fallback detail stays exactly that on the mic (the app parses the reason out of it);
    // otherwise the detail names the endpoint (AP3): "mic:<name>" / "render:<name>", or for a
    // render fallback "fallback:<reason>|render:<name>" (composed by the plant).
    const bool fallback = s.channel && s.channel->fallback_detail[0] != 0;
    const wchar_t* detail = !s.channel ? nullptr
                            : fallback ? s.channel->fallback_detail
                                       : s.channel->endpoint_label;
    publish(WF_EVENT_DEVICE_ACQUIRED, r, attempt, now, fallback ? s.channel->fallback_hr : 0,
            detail);
    if (r == Role::Reference && s.reference_missing) {
        s.reference_missing = false;
        publish(WF_EVENT_REFERENCE_RESTORED, r, attempt, now);
    }
}

void Supervisor::handle(const SupMsg& m, int64_t now) {
    const Role r = static_cast<Role>(m.role < kRoleCount ? m.role : 0);
    switch (m.kind) {
        case SupMsg::Kind::Start: {
            if (started_) break;
            started_ = true;
            for (int i = 0; i < kRoleCount; ++i) {
                if (role_active_[i]) begin_start(static_cast<Role>(i), now);
            }
            // The keep-alive runs in every mode. In mode 2 it waits for the loopback's open,
            // which decides the endpoint (default, pinned or its fallback); in modes 0/1 it
            // starts now on the pinned endpoint or the default.
            ka_.want = true;
            if (!device_mode()) keepalive_retarget(params_.render_device_id, now);
            break;
        }
        case SupMsg::Kind::Stop: {
            if (stop_requested_) break;
            stop_requested_ = true;
            device_change_at_ = 0;
            format_change_at_ = 0;
            render_change_at_ = 0;
            ka_.want = false;
            for (int i = 0; i < kRoleCount; ++i) {
                if (!role_active_[i]) continue;
                RoleState& s = roles_[i];
                s.retry_at = 0;
                if (s.step) {
                    s.after = After::Final;     // the in-flight step decides
                } else if (s.channel) {
                    begin_stop(static_cast<Role>(i), After::Final, now);
                } else {
                    s.phase = Phase::Stopped;
                }
            }
            break;
        }
        case SupMsg::Kind::SetMicDevice:
        case SupMsg::Kind::DefaultDeviceChanged: {
            if (stop_requested_) break;
            // A default change before the start is already reflected by the first open
            // (the real session registers for notifications only after Start was posted).
            if (m.kind == SupMsg::Kind::DefaultDeviceChanged && !started_) break;
            // Spec 3.1: "Ereignisse auf 200 ms entprellt". Each new event restarts the
            // debounce window, so a burst of notifications causes one switch.
            std::memcpy(pending_device_, m.device_id, sizeof(pending_device_));
            pending_device_[255] = 0;
            device_change_at_ = now + sup::kDeviceDebounce;
            break;
        }
        case SupMsg::Kind::FormatChanged: {
            if (stop_requested_) break;
            // Spec 3.1: "50 ms zusammenfassen, dann Einheit neu aufbauen" - the window
            // starts with the first event so a burst produces exactly one rebuild.
            if (format_change_at_ == 0) format_change_at_ = now + sup::kFormatCoalesce;
            break;
        }
        case SupMsg::Kind::StreamFirstPacket:
            on_first_packet(r, m.attempt, now);
            break;
        case SupMsg::Kind::RenderDefaultChanged: {
            if (stop_requested_ || !started_) break;
            // A pinned render_device_id ignores default changes (it falls back only when it
            // is lost; after that it is no longer pinned and follows like an empty one).
            if (render_pinned()) break;
            if (m.hr != 0) {
                // eMultimedia: the second half of the console/multimedia pair Windows sends
                // for one change. It only extends a pending window; alone it changes nothing
                // the session follows (the session opens and follows the console role).
                if (render_change_at_ != 0) render_change_at_ = now + sup::kDeviceDebounce;
                break;
            }
            copy_w(render_pending_id_, m.device_id);
            render_change_at_ = now + sup::kDeviceDebounce;   // each event restarts the window
            break;
        }
        case SupMsg::Kind::RenderEndpointLost: {
            if (stop_requested_ || !started_) break;
            if (!render_pinned() || !same_endpoint_id(m.device_id, params_.render_device_id)) break;
            render_pending_lost_ = true;
            render_change_at_ = now + sup::kDeviceDebounce;
            break;
        }
        case SupMsg::Kind::StreamDeviceLost: {
            RoleState& s = rs(r);
            if (m.attempt != s.attempt_id || stop_requested_ || s.step) break;
            publish(WF_EVENT_DEVICE_LOST, r, m.attempt, now, m.hr,
                    s.channel ? s.channel->endpoint_label : nullptr);
            if (r == Role::Others && device_mode()) {
                // AP3: the loopback's render endpoint was invalidated (unplugged, disabled,
                // its format changed). Same path as a render switch: re-resolve the endpoint
                // (the default, the pinned one, or its fallback), reset the canceller, report
                // ONE gap - the DSP's, at its true length - and never spend the Others
                // restart budget on it.
                if (host_) host_->sup_render_reset();
                render_reset_requested_ = true;
                next_handover_[static_cast<int>(r)] = WF_GAP_DEVICE_LOST;
                rebuild(r, now);
                break;
            }
            publish_gap(r, WF_GAP_DEVICE_LOST, now, 0.0);
            begin_stop(r, After::StartNow, now);
            break;
        }
        case SupMsg::Kind::StreamError: {
            RoleState& s = rs(r);
            if (m.attempt != s.attempt_id || stop_requested_ || s.step) break;
            if (m.hr == kAccessDenied) {
                // Spec 3.1: a permission error stops every retry for this role.
                publish(WF_EVENT_PERMISSION_DENIED, r, m.attempt, now, m.hr);
                begin_stop(r, After::Final, now);
                break;
            }
            publish(WF_EVENT_ERROR, r, m.attempt, now, m.hr);
            publish_gap(r, WF_GAP_RESTART, now, 0.0);
            schedule_after_failure(r, now);
            // schedule_after_failure set the phase and retry_at; tear the stream down and
            // honour that decision once the stop step returns.
            const After next = (s.phase == Phase::Wait || s.phase == Phase::Degraded)
                                   ? After::Retry
                                   : After::None;
            begin_stop(r, next, now);
            break;
        }
        case SupMsg::Kind::StreamStarted:
        case SupMsg::Kind::StreamStopped:
        case SupMsg::Kind::None:
        default:
            break;
    }
}

void Supervisor::check_timers(int64_t now) {
    if (device_change_at_ != 0 && now >= device_change_at_) {
        device_change_at_ = 0;
        RoleState& s = rs(Role::Mic);
        std::memcpy(params_.mic_device_id, pending_device_, sizeof(params_.mic_device_id));
        s.open_attempts = 0;
        s.first_buffer_restarts = 0;
        s.background_retries = 0;
        s.retry_at = 0;
        // AP3: name the endpoint the mic switches to ("mic:<name>"; an empty id is the
        // default the plant opens: communications, else console).
        wchar_t name[128] = {};
        plant_->endpoint_name(false, params_.mic_device_id, name, 128);
        wchar_t detail[128] = {};
        compose_detail(detail, 128, L"mic:", name);
        publish(WF_EVENT_DEVICE_SWITCHED, Role::Mic, s.attempt_id, now, 0, detail);
        publish_gap(Role::Mic, WF_GAP_RESTART, now, 0.0);
        if (host_) host_->sup_mic_reset(false);   // spec 3.3 step 5: mic device change
        if (!s.step) begin_stop(Role::Mic, After::StartNow, now);
    }
    if (format_change_at_ != 0 && now >= format_change_at_) {
        format_change_at_ = 0;
        RoleState& s = rs(Role::Mic);
        // Only format-key notifications of the mic endpoint get here (Session filters), and
        // the plant still confirms against the format the running stream was opened with:
        // a notification without a real change - or a burst of them - rebuilds nothing.
        if (plant_->mic_format_changed(params_.mic_device_id)) {
            publish(WF_EVENT_FORMAT_CHANGED, Role::Mic, s.attempt_id, now);
            if (host_) host_->sup_mic_reset(true);   // spec 3.3 step 5: mic format change
            s.open_attempts = 0;
            s.first_buffer_restarts = 0;
            s.retry_at = 0;
            if (!s.step) begin_stop(Role::Mic, After::StartNow, now);
        }
    }
    if (render_change_at_ != 0 && now >= render_change_at_) handle_render_timer(now);
    for (int i = 0; i < kRoleCount; ++i) {
        if (!role_active_[i]) continue;
        const Role r = static_cast<Role>(i);
        RoleState& s = roles_[i];
        if (s.step) continue;
        if (s.phase == Phase::AwaitFirst && now >= s.first_buffer_deadline) {
            // Spec 3.1: the device counts as acquired only after the first buffer.
            if (s.first_buffer_restarts < sup::kFirstBufferRestarts) {
                s.retry_at = now + sup::kFirstBufferDelays[s.first_buffer_restarts];
                ++s.first_buffer_restarts;
                publish_gap(r, WF_GAP_RESTART, now, 0.0);
                begin_stop(r, After::Retry, now);
            } else {
                begin_stop(r, After::GiveUp, now);
            }
            continue;
        }
        if ((s.phase == Phase::Wait || s.phase == Phase::Degraded) && s.retry_at != 0 &&
            now >= s.retry_at) {
            s.retry_at = 0;
            begin_start(r, now);
        }
    }
}

void Supervisor::check_watchdog(int64_t now) {
    if (now < next_watchdog_) return;
    next_watchdog_ = now + sup::kWatchdogInterval;
    if (stop_requested_) return;

    for (int i = 0; i < kRoleCount; ++i) {
        if (!role_active_[i]) continue;
        const Role r = static_cast<Role>(i);
        RoleState& s = roles_[i];
        if (s.phase != Phase::Running || s.step || !s.channel) continue;
        // Spec 3.2: the include-mode app source may fall silent; it is exempt.
        if (s.channel->app_source) continue;
        const bool is_reference =
            (r == Role::Reference) ||
            (r == Role::Others && !role_active_[static_cast<int>(Role::Reference)]);
        if (is_reference && params_.keepalive_mode != WF_KEEPALIVE_NONE &&
            !plant_->reference_watchdog_armed(now)) {
            continue;
        }
        const int64_t last = s.channel->last_packet_100ns.load(std::memory_order_acquire);
        if (last == 0 || now - last < sup::kWatchdogTimeout) continue;

        ++counters_.watchdog_recoveries;
        publish(WF_EVENT_WATCHDOG_RECOVERY, r, s.attempt_id, now);
        publish_gap(r, WF_GAP_WATCHDOG_RECOVERY, now,
                    static_cast<double>(now - last) / k::kTicksPerSecond);
        begin_stop(r, After::StartNow, now);
    }
}

void Supervisor::poll(int64_t now) {
    // Steps can complete inline (fake plant), so run until nothing moves any more. The
    // bound keeps a pathological restart loop from monopolising the thread for a tick.
    for (int iter = 0; iter < 8; ++iter) {
        bool progress = false;

        SupMsg m;
        while (queue_->take(m)) {
            handle(m, now);
            progress = true;
        }

        for (int i = 0; i < kRoleCount; ++i) {
            if (!role_active_[i]) continue;
            RoleState& s = roles_[i];
            if (!s.step) continue;
            const Role r = static_cast<Role>(i);
            if (s.step->done.load(std::memory_order_acquire)) {
                const StepCtx::Kind kind = s.step->kind;
                const int32_t hr = s.step->hr.load(std::memory_order_relaxed);
                s.step.reset();
                progress = true;
                if (kind == StepCtx::Kind::Start) {
                    // A failed open resolves the endpoint anew on its retry anyway.
                    const bool rebuild_now = s.rebuild_pending && !failed(hr) &&
                                             s.after != After::Final;
                    s.rebuild_pending = false;
                    if (failed(hr)) {
                        if (s.after == After::Final) {
                            s.after = After::None;
                            s.channel.reset();
                            if (host_) host_->sup_set_channel(r, nullptr);
                            s.phase = Phase::Stopped;
                        } else {
                            on_start_failed(r, hr, now);
                        }
                    } else if (s.after == After::Final) {
                        begin_stop(r, After::Final, now);
                    } else {
                        s.phase = Phase::AwaitFirst;
                        s.first_buffer_deadline = now + sup::kFirstBufferTimeout;
                        if (r == Role::Mic && s.channel) {
                            counters_.mic_raw =
                                s.channel->raw_accepted.load(std::memory_order_relaxed);
                        }
                        if (host_) host_->sup_set_channel(r, s.channel);
                        // AP3: in mode 2 the loopback's open decided the render endpoint;
                        // the keep-alive follows it.
                        if (r == Role::Others && device_mode()) {
                            on_render_channel_started(now, rebuild_now);
                        }
                        if (rebuild_now) {
                            // A render switch arrived while this open was in flight: it may
                            // have resolved the old default. Open again.
                            begin_stop(r, After::StartNow, now);
                        } else if (s.channel &&
                                   s.channel->packets.load(std::memory_order_acquire) > 0) {
                            // A packet may already have arrived while the step was finishing.
                            on_first_packet(r, s.attempt_id, now);
                        }
                    }
                } else {
                    on_step_finished(r, hr, now);
                }
            } else if (now >= s.step_deadline) {
                // Spec 3.1: "Zeitlimit je Treiber-Schritt 5 s; haengt ein Versuch, gilt er
                // als verwaist". The worker keeps the only reference from here on.
                s.step.reset();
                note_orphan(r, now);
                progress = true;
                if (s.after == After::Final || stop_requested_) {
                    s.after = After::None;
                    s.channel.reset();
                    if (host_) host_->sup_set_channel(r, nullptr);
                    s.phase = Phase::Stopped;
                } else {
                    on_start_failed(r, kStepOrphaned, now);
                }
            }
        }

        if (check_timers_progress(now)) progress = true;
        if (started_ && service_keepalive_progress(now)) progress = true;
        if (!progress) break;
    }
    check_watchdog(now);
}

// check_timers() with a "did anything happen" answer for the poll loop.
bool Supervisor::check_timers_progress(int64_t now) {
    const bool pending = (device_change_at_ != 0 && now >= device_change_at_) ||
                         (format_change_at_ != 0 && now >= format_change_at_) ||
                         (render_change_at_ != 0 && now >= render_change_at_);
    bool role_due = false;
    for (int i = 0; i < kRoleCount && !role_due; ++i) {
        if (!role_active_[i]) continue;
        const RoleState& s = roles_[i];
        if (s.step) continue;
        if (s.phase == Phase::AwaitFirst && now >= s.first_buffer_deadline) role_due = true;
        if ((s.phase == Phase::Wait || s.phase == Phase::Degraded) && s.retry_at != 0 &&
            now >= s.retry_at) {
            role_due = true;
        }
    }
    if (!pending && !role_due) return false;
    check_timers(now);
    return true;
}

// ---------------------------------------------------------------------------
// AP3: render endpoint following and the keep-alive
// ---------------------------------------------------------------------------
//
// State machine (all on the supervisor's poll, every timer in virtual time for the fake):
//
//   RenderDefaultChanged(console, id) --+-- pinned render_device_id: ignored
//   RenderDefaultChanged(multimedia) ---+-- restarts a pending 200 ms window only
//   RenderEndpointLost(pinned id) ------+-- pending "lost"
//              | 200 ms after the LAST of them (A -> B -> A collapses into one decision)
//              v
//   handle_render_timer: target == the endpoint we run on? -> nothing (no event, no gap)
//              | otherwise move_render(target):
//              |   mode 2: DEVICE_SWITCHED(Others, "render:<name>") (or DEVICE_LOST for a
//              |           lost pinned endpoint), canceller reset, restart budget reset,
//              |           loopback stop -> start (break before make; the open resolves the
//              |           default or the pinned endpoint's fallback). When the open returns,
//              |           the keep-alive moves to exactly that endpoint (make before break).
//              |           The hole in front of the new first packet is ONE WF_GAP_RESTART
//              |           (WF_GAP_DEVICE_LOST) gap with its true length.
//              |   modes 0/1: DEVICE_SWITCHED(Reference, "render:<name>"), canceller reset,
//              |           keep-alive moves (start new, then stop old); Others is untouched.
//
// A loopback stream that reports AUDCLNT_E_DEVICE_INVALIDATED takes the same path at once.
void Supervisor::handle_render_timer(int64_t now) {
    render_change_at_ = 0;
    const bool lost = render_pending_lost_;
    render_pending_lost_ = false;
    if (lost) {
        // Nothing to do when the pinned id was abandoned already (a DEVICE_LOST rebuild got
        // there first), or while an open is in flight: that open falls back by itself.
        if (!render_pinned()) return;
        if (device_mode()) {
            if (rs(Role::Others).step) return;
        } else if (ka_.step) {
            return;
        }
        move_render(params_.render_device_id, true, now);
        return;
    }
    if (render_pinned()) return;
    // A -> B -> A inside the window, or a notification for the endpoint we already run on.
    if (render_pending_id_[0] && same_endpoint_id(render_pending_id_, render_current_)) return;
    move_render(render_pending_id_, false, now);
}

void Supervisor::move_render(const wchar_t* target, bool lost, int64_t now) {
    if (host_) host_->sup_render_reset();   // the echo path is a different one from here on
    render_reset_requested_ = true;
    wchar_t name[128] = {};
    wchar_t detail[128] = {};
    if (device_mode()) {
        RoleState& s = rs(Role::Others);
        if (lost) {
            if (s.channel && s.channel->endpoint_label[0]) {
                copy_w(detail, s.channel->endpoint_label);
            } else {
                plant_->endpoint_name(true, target, name, 128);
                compose_detail(detail, 128, L"render:", name);
            }
            publish(WF_EVENT_DEVICE_LOST, Role::Others, s.attempt_id, now, kDeviceInvalidated, detail);
        } else {
            plant_->endpoint_name(true, target, name, 128);
            compose_detail(detail, 128, L"render:", name);
            publish(WF_EVENT_DEVICE_SWITCHED, Role::Others, s.attempt_id, now, 0, detail);
        }
        next_handover_[static_cast<int>(Role::Others)] = lost ? WF_GAP_DEVICE_LOST : WF_GAP_RESTART;
        rebuild(Role::Others, now);
        return;
    }
    // Modes 0/1: process loopback is not bound to an endpoint; only the keep-alive (and the
    // echo path the canceller has learned) moves. The keep-alive step resolves the target:
    // the default, or the pinned endpoint - whose failure falls back to the default and is
    // announced when it returns (abandon_pinned_render).
    if (!lost) {
        plant_->endpoint_name(true, target, name, 128);
        compose_detail(detail, 128, L"render:", name);
        publish(WF_EVENT_DEVICE_SWITCHED, Role::Reference, rs(Role::Reference).attempt_id, now, 0,
                detail);
    }
    keepalive_retarget(lost ? params_.render_device_id : L"", now, true);
}

// A planned rebuild of a role: stop and reopen at once, with a fresh restart budget. A routine
// device change must never exhaust the 0.5/1/2 s ladder that ends in OthersFailed.
void Supervisor::rebuild(Role r, int64_t now) {
    RoleState& s = rs(r);
    s.open_attempts = 0;
    s.first_buffer_restarts = 0;
    s.others_restarts = 0;
    s.background_retries = 0;
    if (s.phase == Phase::Failed || s.phase == Phase::Stopped || stop_requested_) return;
    if (s.step) {
        // A start in flight may have resolved the old endpoint: reopen once it returns. A
        // stop in flight is followed by a start that resolves the endpoint anew anyway.
        if (s.step->kind == StepCtx::Kind::Start) s.rebuild_pending = true;
        return;
    }
    s.retry_at = 0;
    begin_stop(r, After::StartNow, now);   // no channel (waiting for a retry): starts at once
}

void Supervisor::on_render_channel_started(int64_t now, bool another_open_follows) {
    RoleState& s = rs(Role::Others);
    if (!s.channel) return;
    // An open that landed on another endpoint without a planned move (a retry after a failed
    // open, a watchdog rebuild while the default changed) moves the echo path all the same.
    const bool moved = render_current_[0] && s.channel->endpoint_id[0] &&
                       !same_endpoint_id(render_current_, s.channel->endpoint_id);
    if (moved && !render_reset_requested_ && host_) host_->sup_render_reset();
    if (!another_open_follows) render_reset_requested_ = false;
    note_render_endpoint(s.channel->endpoint_id);
    // The pinned endpoint failed and the default was opened in its place: from now on the
    // session follows the default and never hops back by itself (the DEVICE_ACQUIRED of this
    // attempt carries "fallback:<reason>|render:<name>").
    if (s.channel->fallback_detail[0] && render_pinned()) {
        abandon_pinned_render(s.channel->fallback_detail, s.channel->fallback_hr, now, false);
    }
    keepalive_retarget(s.channel->endpoint_id, now);
}

// WfDiagnostics.render_switches counts real moves, whatever triggered them (a followed default
// change, a lost pinned endpoint, an invalidated loopback that reopened elsewhere).
void Supervisor::note_render_endpoint(const wchar_t* id) {
    if (render_current_[0] && id && id[0] && !same_endpoint_id(render_current_, id)) {
        ++counters_.render_switches;
    }
    copy_w(render_current_, id);
}

void Supervisor::abandon_pinned_render(const wchar_t* fallback_detail, int32_t hr, int64_t now,
                                       bool announce) {
    params_.render_device_id[0] = 0;
    if (announce) {
        publish(WF_EVENT_DEVICE_SWITCHED, Role::Reference, rs(Role::Reference).attempt_id, now, hr,
                fallback_detail);
    }
}

void Supervisor::keepalive_retarget(const wchar_t* target, int64_t now, bool force) {
    (void)now;
    const bool same = (target && target[0]) ? same_endpoint_id(target, ka_.target)
                                            : ka_.target[0] == 0;
    if (!force && same && (ka_.running || ka_.dirty || ka_.step)) return;
    copy_w(ka_.target, target);
    ka_.dirty = true;
    ka_.failures = 0;
    ka_.gave_up = false;
    ka_.retry_at = 0;
}

void Supervisor::launch_keepalive(StepCtx::Kind kind, int64_t now) {
    auto ctx = std::make_shared<StepCtx>();
    ctx->kind = kind;
    ctx->params = params_;
    ctx->params.role = Role::Others;
    copy_w(ctx->params.render_device_id, ka_.target);
    // Mode 2: exactly the loopback's endpoint. Modes 0/1: a pinned id may fall back.
    ctx->allow_fallback = !device_mode() && ka_.target[0] != 0;
    ctx->plant = plant_;
    ka_.step = ctx;
    ka_.step_deadline = now + sup::kStepTimeout;
    plant_->launch(ctx);
}

bool Supervisor::service_keepalive_progress(int64_t now) {
    if (ka_.step) {
        if (ka_.step->done.load(std::memory_order_acquire)) {
            const std::shared_ptr<StepCtx> st = std::move(ka_.step);
            ka_.step.reset();
            const int32_t hr = st->hr.load(std::memory_order_relaxed);
            if (st->kind == StepCtx::Kind::KeepAliveStop) {
                ka_.running = false;
                ka_.on[0] = 0;
            } else if (failed(hr)) {
                keepalive_failed(hr, false, st->endpoint_label, now);
            } else {
                ka_.running = true;
                copy_w(ka_.on, st->endpoint_id);
                copy_w(ka_.label, st->endpoint_label);
                ka_.started_at = now;
                ++counters_.keepalive_starts;
                // Still dirty when the target moved while this start was in flight.
                const bool same = ka_.target[0] ? same_endpoint_id(ka_.target, st->params.render_device_id)
                                                : st->params.render_device_id[0] == 0;
                if (same) ka_.dirty = false;
                if (!device_mode()) {
                    note_render_endpoint(st->endpoint_id);
                    if (st->fallback_detail[0] && render_pinned()) {
                        abandon_pinned_render(st->fallback_detail, st->fallback_hr, now, true);
                        ka_.target[0] = 0;
                    }
                }
            }
            return true;
        }
        if (now >= ka_.step_deadline) {
            // Same 5 s limit as every driver step; the worker keeps what it touches.
            const bool was_stop = ka_.step->kind == StepCtx::Kind::KeepAliveStop;
            ka_.step.reset();
            note_orphan(Role::Others, now);
            if (was_stop || !ka_.want) {
                ka_.running = false;
                ka_.on[0] = 0;
            } else {
                keepalive_failed(kStepOrphaned, false, ka_.label, now);
            }
            return true;
        }
        return false;
    }
    if (!ka_.want) {
        if (!ka_.running) return false;
        launch_keepalive(StepCtx::Kind::KeepAliveStop, now);
        return true;
    }
    if (ka_.running) {
        int32_t hr = 0;
        if (plant_->keepalive_exited(&hr)) {
            ka_.running = false;
            ka_.on[0] = 0;
            keepalive_failed(hr, true, ka_.label, now);
            return true;
        }
    }
    if (ka_.dirty && !ka_.gave_up && !driver_blocked_ && (ka_.retry_at == 0 || now >= ka_.retry_at)) {
        ka_.retry_at = 0;
        launch_keepalive(StepCtx::Kind::KeepAliveStart, now);
        return true;
    }
    return false;
}

// Spec 3.3 keep-alive, AP3: a failure is visible (WF_EVENT_WARNING, never fatal - the
// capture streams keep running without it) and retried after 0.5 / 1 / 2 s; the third
// failure in a row gives up until the render endpoint changes.
void Supervisor::keepalive_failed(int32_t hr, bool exited, const wchar_t* label, int64_t now) {
    if (exited && now - ka_.started_at >= sup::kKeepAliveHealthy) ka_.failures = 0;
    ++ka_.failures;
    ++counters_.keepalive_failures;
    ka_.dirty = true;
    wchar_t detail[128] = {};
    wchar_t head[64] = {};
    swprintf(head, 64, exited ? L"keepalive:stopped 0x%08X" : L"keepalive:start failed 0x%08X",
             static_cast<unsigned>(hr));
    compose_detail(detail, 128, head, nullptr);
    append_detail(detail, 128, label);
    if (ka_.failures >= sup::kKeepAliveRetries) {
        ka_.gave_up = true;
        ka_.retry_at = 0;
        append_detail(detail, 128, L"gave up");
    } else {
        const int64_t delay = sup::kKeepAliveDelays[ka_.failures - 1];
        ka_.retry_at = now + delay;
        wchar_t retry[32] = {};
        swprintf(retry, 32, L"retry in %.1f s", static_cast<double>(delay) / k::kTicksPerSecond);
        append_detail(detail, 128, retry);
    }
    publish(WF_EVENT_WARNING, Role::Others, rs(Role::Others).attempt_id, now, hr, detail);
}

}  // namespace wf
