// Session implementation plus RealPlant, the WASAPI side of the plant interface.
#include "session/session.h"

#include <audioclient.h>

#include <cmath>
#include <cstring>

#include "wasapi/clock.h"
#include "wasapi/device_loopback.h"
#include "wasapi/enumerator.h"
#include "wasapi/keepalive_render.h"
#include "wasapi/mic_stream.h"
#include "wasapi/process_loopback.h"
#include "wasapi/sessions.h"

namespace wf {

namespace {

constexpr size_t kIdChars = sizeof(WfSessionConfig::mic_device_id) / sizeof(wchar_t);

void copy_id(wchar_t (&dst)[kIdChars], const wchar_t* src) {
    std::memset(dst, 0, sizeof(dst));
    if (!src) return;
    size_t i = 0;
    while (src[i] && i < kIdChars - 1) {
        dst[i] = src[i];
        ++i;
    }
}

bool same_id(const wchar_t* a, const std::wstring& b) {
    if (!a) return b.empty();
    return b.compare(a) == 0;
}

// MMDevice endpoint ids compared the way the endpoint API treats them: case-insensitive.
bool same_endpoint(const wchar_t* a, const std::wstring& b) {
    if (!a || !a[0] || b.empty()) return false;
    return CompareStringOrdinal(a, -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}

// Spec 3.1 fallback hint: why the configured microphone `id` could not be used. An id that
// no longer resolves to an ACTIVE endpoint is "missing" (Mac: the pinned device is not in
// the inventory); one that exists but did not open is "<friendly name> unavailable".
std::wstring configured_mic_failure(const wchar_t* id) {
    ComPtr<IMMDevice> dev;
    DWORD state = 0;
    if (FAILED(open_endpoint(id, &dev)) || FAILED(dev->GetState(&state)) ||
        state != DEVICE_STATE_ACTIVE) {
        return L"selected microphone missing";
    }
    const std::wstring name = widen(endpoint_name(dev.get()).c_str());
    return (name.empty() ? std::wstring(L"selected microphone") : name) + L" unavailable";
}

// AP3: the same rule for a pinned render endpoint (WfSessionConfig.render_device_id).
std::wstring configured_render_failure(const wchar_t* id) {
    ComPtr<IMMDevice> dev;
    DWORD state = 0;
    if (FAILED(open_endpoint(id, &dev)) || FAILED(dev->GetState(&state)) ||
        state != DEVICE_STATE_ACTIVE) {
        return L"selected output missing";
    }
    const std::wstring name = widen(endpoint_name(dev.get()).c_str());
    return (name.empty() ? std::wstring(L"selected output") : name) + L" unavailable";
}

// Friendly name of an open endpoint for event details; "unknown endpoint" when unreadable.
std::wstring friendly(const std::string& utf8) {
    return utf8.empty() ? std::wstring(L"unknown endpoint") : widen(utf8.c_str());
}

// ---------------------------------------------------------------------------
// RealPlant: opens and closes the WASAPI streams
// ---------------------------------------------------------------------------
class RealStreamHolder final : public StreamHolder {
public:
    ~RealStreamHolder() override { stop_all(); }

    void stop_all() {
        if (mic) mic->stop();
        if (device) device->stop();
        if (process) process->stop();
    }

    std::unique_ptr<MicStream> mic;
    std::unique_ptr<DeviceLoopbackStream> device;
    std::unique_ptr<ProcessLoopbackStream> process;
};

struct StepWorkerArg {
    std::shared_ptr<StepCtx> ctx;
    std::shared_ptr<std::atomic<int>> workers;
};

DWORD WINAPI step_worker_main(void* p) {
    StepWorkerArg* arg = static_cast<StepWorkerArg*>(p);
    {
        MtaScope mta;             // WASAPI activation needs COM on this thread
        run_step(arg->ctx);
    }
    arg->workers->fetch_sub(1, std::memory_order_acq_rel);
    delete arg;                   // releases the last reference if the step was orphaned
    return 0;
}

class RealPlant final : public IPlant {
public:
    explicit RealPlant(std::shared_ptr<MsgQueue> queue)
        : queue_(std::move(queue)), workers_(std::make_shared<std::atomic<int>>(0)) {}

    void launch(const std::shared_ptr<StepCtx>& ctx) override {
        auto* arg = new StepWorkerArg{ctx, workers_};
        workers_->fetch_add(1, std::memory_order_acq_rel);
        HANDLE h = CreateThread(nullptr, 0, &step_worker_main, arg, 0, nullptr);
        if (!h) {
            workers_->fetch_sub(1, std::memory_order_acq_rel);
            delete arg;
            ctx->hr.store(static_cast<int32_t>(E_FAIL), std::memory_order_relaxed);
            ctx->done.store(1, std::memory_order_release);
            return;
        }
        CloseHandle(h);   // fire and forget: completion is reported through ctx->done
    }

    int32_t do_start(StepCtx& ctx) override;

    void do_stop(StepCtx& ctx) override {
        if (!ctx.channel) return;
        if (ctx.params.role == Role::Mic) {
            // No mic stream from here on: nothing whose format could change.
            AcquireSRWLockExclusive(&lock_);
            if (ctx.channel->attempt_id == mic_attempt_) {
                mic_endpoint_[0] = 0;
                mic_format_ = PcmFormat{};
            }
            ReleaseSRWLockExclusive(&lock_);
        }
        ctx.channel->holder.reset();   // the destructor stops the streams
    }

    std::shared_ptr<StreamChannel> make_channel(Role r, uint32_t attempt,
                                                bool app_source) override {
        return std::make_shared<StreamChannel>(r, attempt, app_source, queue_);
    }

    bool reference_watchdog_armed(int64_t now_100ns) override {
        // The render endpoint the reference hears: the loopback's / the keep-alive's.
        wchar_t id[kIdChars];
        AcquireSRWLockShared(&lock_);
        std::memcpy(id, render_endpoint_, sizeof(id));
        ReleaseSRWLockShared(&lock_);
        float peak = 0.0f;
        if (SUCCEEDED(endpoint_peak(id[0] ? id : nullptr, &peak)) && peak > sup::kSilencePeak) {
            return true;
        }
        return keepalive_clock_stalled(now_100ns);
    }

    bool mic_format_changed(const wchar_t* device_id) override;

    bool mic_endpoint(wchar_t* out, size_t cap) override {
        if (!out || cap == 0) return false;
        AcquireSRWLockShared(&lock_);
        size_t i = 0;
        for (; mic_endpoint_[i] && i + 1 < cap; ++i) out[i] = mic_endpoint_[i];
        out[i] = 0;
        ReleaseSRWLockShared(&lock_);
        return out[0] != 0;
    }

    uint32_t mic_default_role() override {
        AcquireSRWLockShared(&lock_);
        const uint32_t r = mic_role_;
        ReleaseSRWLockShared(&lock_);
        return r;
    }

    int32_t do_keepalive(StepCtx& ctx) override;

    bool keepalive_exited(int32_t* hr) override {
        std::unique_ptr<KeepAliveRender> dead;
        HRESULT why = S_OK;
        AcquireSRWLockExclusive(&ka_lock_);
        if (keepalive_ && keepalive_->exited(&why)) dead = std::move(keepalive_);
        ReleaseSRWLockExclusive(&ka_lock_);
        if (!dead) return false;
        dead->stop();   // the thread has already left; this only releases the client
        if (hr) *hr = static_cast<int32_t>(why);
        return true;
    }

    void endpoint_name(bool render, const wchar_t* id, wchar_t* out, size_t cap) override {
        ComPtr<IMMDevice> dev;
        HRESULT hr = E_FAIL;
        if (id && id[0]) {
            hr = open_endpoint(id, &dev);
        } else if (render) {
            hr = open_default_endpoint(eRender, eConsole, &dev);
        } else {
            hr = open_default_endpoint(eCapture, eCommunications, &dev);
            if (FAILED(hr)) hr = open_default_endpoint(eCapture, eConsole, &dev);
        }
        const std::wstring name = friendly(SUCCEEDED(hr) ? wf::endpoint_name(dev.get()) : std::string());
        compose_detail(out, cap, nullptr, name.c_str());
    }

    void wait_workers(uint32_t timeout_ms) override {
        const DWORD deadline = GetTickCount() + timeout_ms;
        while (workers_->load(std::memory_order_acquire) > 0) {
            if (static_cast<LONG>(GetTickCount() - deadline) >= 0) return;
            Sleep(10);
        }
    }

private:
    // Spec 3.3: a keep-alive position that stops advancing means the render engine
    // stalled, which is one of the two conditions that arm the reference watchdog.
    bool keepalive_clock_stalled(int64_t now_100ns) {
        uint64_t frames = 0, freq = 0;
        int64_t qpc = 0;
        bool stalled = false;
        AcquireSRWLockExclusive(&ka_lock_);
        if (keepalive_ && keepalive_->running() &&
            SUCCEEDED(keepalive_->clock_position(&frames, &freq, &qpc))) {
            if (!have_clock_ || frames != last_frames_) {
                have_clock_ = true;
                last_frames_ = frames;
                last_clock_at_ = now_100ns;
            } else {
                stalled = now_100ns - last_clock_at_ >= 200 * k::kTicksPerMs;
            }
        }
        ReleaseSRWLockExclusive(&ka_lock_);
        return stalled;
    }

    void set_render_endpoint(const std::wstring& id) {
        AcquireSRWLockExclusive(&lock_);
        copy_id(render_endpoint_, id.c_str());
        ReleaseSRWLockExclusive(&lock_);
    }

    std::shared_ptr<MsgQueue> queue_;
    std::shared_ptr<std::atomic<int>> workers_;
    SRWLOCK lock_ = SRWLOCK_INIT;
    // AP3: the render endpoint the reference currently hears (the device loopback's in mode
    // 2, the keep-alive's in modes 0/1), for the spec 3.3 watchdog gate. Empty = default.
    wchar_t render_endpoint_[kIdChars] = {};
    // The current mic stream, recorded by its open (lock_): the endpoint it runs on, the
    // format it was opened with and whether RAW was accepted. mic_format_changed() only
    // reads it, so asking twice can never flip the answer.
    uint32_t  mic_attempt_ = 0;
    wchar_t   mic_endpoint_[kIdChars] = {};
    PcmFormat mic_format_{};
    bool      mic_raw_ = false;
    uint32_t  mic_role_ = kMicRoleExplicit;
    // AP3: the keep-alive belongs to the plant (ka_lock_), not to a stream, so it can move
    // to another render endpoint without touching any capture stream.
    SRWLOCK ka_lock_ = SRWLOCK_INIT;
    std::unique_ptr<KeepAliveRender> keepalive_;
    bool     have_clock_ = false;
    uint64_t last_frames_ = 0;
    int64_t  last_clock_at_ = 0;
};

// KeepAliveStart: start the new one first, then stop the old one (make before break), so
// the endpoint being left and the one being joined are both awake across the move.
int32_t RealPlant::do_keepalive(StepCtx& ctx) {
    if (ctx.kind == StepCtx::Kind::KeepAliveStop) {
        std::unique_ptr<KeepAliveRender> old;
        AcquireSRWLockExclusive(&ka_lock_);
        old = std::move(keepalive_);
        have_clock_ = false;
        ReleaseSRWLockExclusive(&ka_lock_);
        if (old) old->stop();
        return 0;
    }
    const wchar_t* id = ctx.params.render_device_id[0] ? ctx.params.render_device_id : nullptr;
    auto ka = std::unique_ptr<KeepAliveRender>(new KeepAliveRender());
    HRESULT hr = ka->start(id);
    if (FAILED(hr) && id && ctx.allow_fallback && hr != E_ACCESSDENIED) {
        // Modes 0/1 with a pinned render endpoint that is gone: the default instead.
        const std::wstring reason = configured_render_failure(id);
        auto fallback = std::unique_ptr<KeepAliveRender>(new KeepAliveRender());
        const HRESULT fallback_hr = fallback->start(nullptr);
        if (SUCCEEDED(fallback_hr)) {
            compose_detail(ctx.fallback_detail, 128, L"fallback:", reason.c_str());
            wchar_t label[128];
            compose_detail(label, 128, L"render:", friendly(fallback->device_name()).c_str());
            append_detail(ctx.fallback_detail, 128, label);
            ctx.fallback_hr = static_cast<int32_t>(hr);
            ka = std::move(fallback);
            hr = fallback_hr;
        }
    }
    if (FAILED(hr)) {
        std::string name = ka->device_name();
        if (name.empty() && id) {
            ComPtr<IMMDevice> dev;
            if (SUCCEEDED(open_endpoint(id, &dev))) name = wf::endpoint_name(dev.get());
        }
        compose_detail(ctx.endpoint_label, 128, L"render:", friendly(name).c_str());
        return static_cast<int32_t>(hr);
    }
    copy_id(ctx.endpoint_id, ka->device_id().c_str());
    compose_detail(ctx.endpoint_label, 128, L"render:", friendly(ka->device_name()).c_str());
    if (ctx.params.others_mode != WF_OTHERS_DEVICE_LOOPBACK) set_render_endpoint(ka->device_id());
    std::unique_ptr<KeepAliveRender> old;
    AcquireSRWLockExclusive(&ka_lock_);
    old = std::move(keepalive_);
    keepalive_ = std::move(ka);
    have_clock_ = false;
    ReleaseSRWLockExclusive(&ka_lock_);
    if (old) old->stop();
    return 0;
}

int32_t RealPlant::do_start(StepCtx& ctx) {
    const std::shared_ptr<StreamChannel>& ch = ctx.channel;
    if (!ch) return static_cast<int32_t>(E_POINTER);
    auto holder = std::unique_ptr<RealStreamHolder>(new RealStreamHolder());
    const PlantParams& p = ctx.params;
    const wchar_t* render_id = p.render_device_id[0] ? p.render_device_id : nullptr;
    HRESULT hr = E_FAIL;

    switch (p.role) {
        case Role::Mic: {
            MicOptions opt;
            opt.prefer_raw = p.prefer_raw != 0;
            holder->mic.reset(new MicStream());
            const wchar_t* id = p.mic_device_id[0] ? p.mic_device_id : nullptr;
            uint32_t role = kMicRoleExplicit;
            if (id) {
                hr = holder->mic->start(id, opt, ch.get());
            } else {
                // AP3: without a configured mic, the default COMMUNICATIONS capture endpoint
                // (what Teams records from), the console default when there is none or it
                // does not open (not after E_ACCESSDENIED).
                std::wstring comm;
                if (SUCCEEDED(default_endpoint_id(eCapture, eCommunications, &comm)) &&
                    !comm.empty()) {
                    role = kMicRoleCommunications;
                    hr = holder->mic->start(comm.c_str(), opt, ch.get());
                    std::wstring console;
                    if (FAILED(hr) && hr != E_ACCESSDENIED &&
                        SUCCEEDED(default_endpoint_id(eCapture, eConsole, &console)) &&
                        !same_endpoint(comm.c_str(), console)) {
                        holder->mic.reset(new MicStream());
                        role = kMicRoleConsole;
                        hr = holder->mic->start(nullptr, opt, ch.get());
                    }
                } else {
                    role = kMicRoleConsole;
                    hr = holder->mic->start(nullptr, opt, ch.get());
                }
            }
            // Spec 3.1: "Schlaegt die gespeicherte fehl, wird im selben Versuch der Standard
            // probiert. Es gibt keinen weiteren Ausweich-Tier." Not after E_ACCESSDENIED (a
            // permission error stops every retry) and not when the configured endpoint IS
            // the default (Mac MicrophoneCandidates: the default only when it differs).
            std::wstring default_id;
            if (FAILED(hr) && id && mic_fallback_allowed(static_cast<int32_t>(hr)) &&
                SUCCEEDED(default_endpoint_id(eCapture, eConsole, &default_id)) &&
                !same_endpoint(id, default_id)) {
                const std::wstring reason = configured_mic_failure(id);
                auto fallback = std::unique_ptr<MicStream>(new MicStream());
                const HRESULT fallback_hr = fallback->start(nullptr, opt, ch.get());
                if (SUCCEEDED(fallback_hr)) {
                    set_mic_fallback(*ch, static_cast<int32_t>(hr), reason.c_str());
                    holder->mic = std::move(fallback);
                    hr = fallback_hr;
                } else if (fallback_hr == E_ACCESSDENIED) {
                    hr = fallback_hr;   // the default reports the permission problem: stop retrying
                }
            }
            if (SUCCEEDED(hr)) {
                const StreamDiagnostics& d = holder->mic->diag();
                ch->raw_accepted.store(d.raw_accepted ? 1u : 0u, std::memory_order_relaxed);
                copy_id(ch->endpoint_id, holder->mic->device_id().c_str());
                compose_detail(ch->endpoint_label, 128, L"mic:",
                               friendly(holder->mic->device_name()).c_str());
                AcquireSRWLockExclusive(&lock_);
                // An orphaned older step that finishes late must not overwrite the record.
                if (ch->attempt_id >= mic_attempt_) {
                    mic_attempt_ = ch->attempt_id;
                    copy_id(mic_endpoint_, holder->mic->device_id().c_str());
                    mic_format_ = d.format;
                    mic_raw_ = d.raw_accepted;
                    mic_role_ = role;
                }
                ReleaseSRWLockExclusive(&lock_);
            }
            break;
        }
        case Role::Others: {
            if (p.others_mode == WF_OTHERS_DEVICE_LOOPBACK) {
                // Measurement 20261101-1346: device loopback delivers nothing while the
                // endpoint is idle; the keep-alive (a plant-level step since AP3) starts on
                // exactly the endpoint this open resolves, as soon as it returns.
                holder->device.reset(new DeviceLoopbackStream());
                DeviceLoopbackOptions opt;
                hr = holder->device->start(render_id, opt, ch.get());
                if (FAILED(hr) && render_id && hr != E_ACCESSDENIED) {
                    // AP3: the pinned render endpoint is gone or does not open - the default
                    // in the same attempt (as the mic of spec 3.1), announced by the
                    // DEVICE_ACQUIRED detail "fallback:<reason>|render:<name>".
                    const std::wstring reason = configured_render_failure(render_id);
                    auto fallback = std::unique_ptr<DeviceLoopbackStream>(new DeviceLoopbackStream());
                    const HRESULT fallback_hr = fallback->start(nullptr, opt, ch.get());
                    if (SUCCEEDED(fallback_hr)) {
                        compose_detail(ch->fallback_detail, 128, L"fallback:", reason.c_str());
                        wchar_t label[128];
                        compose_detail(label, 128, L"render:",
                                       friendly(fallback->device_name()).c_str());
                        append_detail(ch->fallback_detail, 128, label);
                        ch->fallback_hr = static_cast<int32_t>(hr);
                        holder->device = std::move(fallback);
                        hr = fallback_hr;
                    }
                }
                if (SUCCEEDED(hr)) {
                    copy_id(ch->endpoint_id, holder->device->device_id().c_str());
                    compose_detail(ch->endpoint_label, 128, L"render:",
                                   friendly(holder->device->device_name()).c_str());
                    set_render_endpoint(holder->device->device_id());
                }
            } else if (p.others_mode == WF_OTHERS_PROCESS_INCLUDE) {
                ProcessLoopbackOptions opt;
                opt.mode = ProcessLoopbackMode::IncludeTree;
                opt.target_pid = p.target_pid;
                holder->process.reset(new ProcessLoopbackStream());
                hr = holder->process->start(opt, ch.get());
            } else {
                // The keep-alive runs in this mode too (an idle render endpoint that wakes up
                // stalls process loopback for 0.43-0.52 s); it is a plant-level step since AP3.
                ProcessLoopbackOptions opt;
                opt.mode = ProcessLoopbackMode::ExcludeTree;
                opt.target_pid = 0;   // our own process tree
                holder->process.reset(new ProcessLoopbackStream());
                hr = holder->process->start(opt, ch.get());
            }
            break;
        }
        case Role::Reference: {
            // Include mode only: the exclude-mode mix is the echo reference and is never
            // published as frames (spec 3.2). Not endpoint-bound; the keep-alive runs as a
            // plant-level step, as in mode 0.
            ProcessLoopbackOptions opt;
            opt.mode = ProcessLoopbackMode::ExcludeTree;
            opt.target_pid = 0;
            holder->process.reset(new ProcessLoopbackStream());
            hr = holder->process->start(opt, ch.get());
            break;
        }
    }

    if (FAILED(hr)) return static_cast<int32_t>(hr);   // ~RealStreamHolder stops the rest
    ch->holder = std::move(holder);
    return 0;
}

// Before 2026-11-01 this compared a plain GetMixFormat (the PROCESSED format, 4 ch on the
// Intel SST array) with the RAW stream's format (2 ch) and then overwrote the stored value,
// while every mic open wrote the RAW format back - so every notification "changed" the
// format and the mic rebuilt itself in a loop. Now both sides are read the same way and
// the open-time record is never touched here.
bool RealPlant::mic_format_changed(const wchar_t* device_id) {
    (void)device_id;   // the record names the endpoint the stream actually runs on
    wchar_t id[kIdChars];
    PcmFormat opened{};
    bool raw = false;
    AcquireSRWLockShared(&lock_);
    std::memcpy(id, mic_endpoint_, sizeof(id));
    opened = mic_format_;
    raw = mic_raw_;
    ReleaseSRWLockShared(&lock_);
    if (!id[0] || opened.rate == 0) return false;   // no mic stream: the next open reads it

    ComPtr<IMMDevice> dev;
    if (FAILED(open_endpoint(id, &dev))) return true;   // cannot tell; let the open ladder decide
    PcmFormat now{};
    // Same category (MicOptions default) and RAW option as the running stream's open.
    if (FAILED(query_capture_format(dev.get(), AudioCategory_Other, raw, &now))) return true;
    return !same_pcm_format(opened, now);
}

}  // namespace

// ---------------------------------------------------------------------------
// Session
// ---------------------------------------------------------------------------
Session* Session::create(const WfSessionConfig& cfg, bool fake, int32_t* err) {
    const auto fail = [err](int32_t code) -> Session* {
        if (err) *err = code;
        return nullptr;
    };
    if (cfg.size != sizeof(WfSessionConfig)) return fail(WF_E_INVALID_ARG);
    if (cfg.others_mode > WF_OTHERS_DEVICE_LOOPBACK) return fail(WF_E_INVALID_ARG);
    if (cfg.ns_level > 4) return fail(WF_E_INVALID_ARG);
    if (cfg.keepalive_mode > WF_KEEPALIVE_CLOCK) return fail(WF_E_INVALID_ARG);
    // Reserved but not built: clock-driven zeros would make a dead reference stream look
    // healthy, and the measurements do not need them (spec 3.3 calls it a last resort).
    if (cfg.keepalive_mode == WF_KEEPALIVE_CLOCK) return fail(WF_E_UNSUPPORTED);
    if (cfg.timestamp_mode > WF_TIMESTAMP_SMOOTHED) return fail(WF_E_INVALID_ARG);
    if (cfg.others_mode == WF_OTHERS_PROCESS_INCLUDE && cfg.target_pid == 0) {
        return fail(WF_E_INVALID_ARG);
    }
    if (!(cfg.base_duration_sec >= 0.0) || !std::isfinite(cfg.base_duration_sec)) {
        return fail(WF_E_INVALID_ARG);
    }

    auto* s = new Session();
    s->cfg_ = cfg;
    s->fake_ = fake;
    s->power_.reset(new PowerRequest(fake));

    if (!fake && cfg.others_mode != WF_OTHERS_DEVICE_LOOPBACK &&
        !process_loopback_api_available()) {
        if (!cfg.allow_device_loopback_fallback) {
            delete s;
            return fail(WF_E_UNSUPPORTED);
        }
        s->cfg_.others_mode = WF_OTHERS_DEVICE_LOOPBACK;
        s->fallback_to_device_loopback_ = true;
    }
    // Spec 3.3 keep-alive. The silent render stream itself runs in EVERY mode (RealPlant
    // starts it with the stream that feeds the canceller):
    //  * classic loopback (mode 2) delivers nothing at all while the endpoint is idle;
    //  * process loopback (modes 0 and 1) keeps delivering zeros while idle, but when an
    //    idle render endpoint wakes up because another process starts playing it delivers
    //    NOTHING for 0.43-0.52 s (measured 2026-11-01, audioprobe onset: no flags, no
    //    burst, QPC resumes at real time), which became a 0.4-0.6 s Others/reference hole
    //    and, past 0.5 s, a watchdog rebuild that lost the start of the new audio. With our
    //    own silent render stream keeping the endpoint awake the same onset costs one
    //    ~20-60 ms step.
    // keepalive_mode keeps selecting the reference watchdog of spec 3.3 only: RENDER (and
    // mode 2, where it is forced as before) gates it on render signal / a stalled
    // keep-alive clock; NONE leaves the plain 0.5 s watchdog of modes 0/1 untouched.
    if (s->cfg_.others_mode == WF_OTHERS_DEVICE_LOOPBACK) {
        s->cfg_.keepalive_mode = WF_KEEPALIVE_RENDER;
    }

    s->event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
    if (!s->event_) {
        delete s;
        return fail(WF_E_FAIL);
    }
    s->frames_.init();
    s->events_.init();
    s->sup_events_.init(kPendingEventCapacity);
    s->queue_ = std::make_shared<MsgQueue>();
    if (fake) {
        auto plant = std::make_shared<FakePlant>(s->queue_);
        s->fake_plant_ = plant.get();
        s->plant_ = plant;
    } else {
        s->plant_ = std::make_shared<RealPlant>(s->queue_);
    }
    if (err) *err = WF_OK;
    return s;
}

bool Session::destroy(Session* s) {
    if (!s) return true;
    if (!s->shutdown()) return false;   // a thread still runs on `s`: abandon, never free
    delete s;
    return true;
}

bool Session::shutdown() {
    stop();
    if (power_) power_->release();   // stop() released it already; also covers a failed start
    // The supervisor leaves its loop by itself once every role is stopped; a stop step that
    // hangs is orphaned after 5 s (kStepTimeout), so 6 s covers a healthy teardown.
    if (sup_thread_) {
        if (WaitForSingleObject(sup_thread_, 6000) != WAIT_OBJECT_0) {
            SetEvent(sup_stop_);
            if (WaitForSingleObject(sup_thread_, 2000) != WAIT_OBJECT_0) return false;
        }
        CloseHandle(sup_thread_);
        sup_thread_ = nullptr;
    }
    // After a successful stop the DSP thread is already gone. After a failed one it gets
    // another 2 s to come back from whatever held it; if it does not, it is abandoned.
    if (!dsp_.join_thread(2000)) return false;
    if (notifications_registered_) {
        notifications_.unregister();
        notifications_registered_ = false;
    }
    // Spec / plan: wait up to 5 s for driver workers, then abandon them. An orphaned
    // worker holds the only reference to its step, channel and plant, so it cleans up
    // on its own whenever the driver finally returns.
    if (plant_) plant_->wait_workers(5000);
    AcquireSRWLockExclusive(&channels_lock_);
    for (int i = 0; i < kRoleCount; ++i) channels_[i].reset();
    ReleaseSRWLockExclusive(&channels_lock_);
    return true;
}

// Only reached through destroy() after a successful shutdown(), or from create() before
// anything was started: no thread can still use the session here.
Session::~Session() {
    if (power_) power_->release();
    if (sup_stop_) {
        CloseHandle(sup_stop_);
        sup_stop_ = nullptr;
    }
    if (event_) {
        CloseHandle(event_);
        event_ = nullptr;
    }
}

int64_t Session::now() const {
    return fake_ ? fake_now_.load(std::memory_order_acquire) : qpc_now_100ns();
}

double Session::sup_offset(int64_t host_100ns) { return timeline_.offset_of(host_100ns); }

void Session::sup_publish(const WfEvent& e) { sup_events_.forward(e); }

void Session::sup_set_channel(Role r, std::shared_ptr<StreamChannel> ch) {
    AcquireSRWLockExclusive(&channels_lock_);
    channels_[static_cast<int>(r)] = std::move(ch);
    ReleaseSRWLockExclusive(&channels_lock_);
}

void Session::sup_mic_reset(bool format_change) { dsp_.request_mic_reset(format_change); }

void Session::sup_render_reset() { dsp_.request_render_reset(); }

std::shared_ptr<StreamChannel> Session::dsp_channel(Role r) {
    AcquireSRWLockShared(&channels_lock_);
    std::shared_ptr<StreamChannel> ch = channels_[static_cast<int>(r)];
    ReleaseSRWLockShared(&channels_lock_);
    return ch;
}

void Session::dsp_publish(const WfEvent& e) { staged_events_.stage(e, events_); }

bool Session::dsp_push_frame(const WfFrame& f) { return frames_.push(f); }

void Session::dsp_forward_supervisor_events() {
    WfEvent e;
    while (sup_events_.pop(&e, 1) == 1) staged_events_.stage(e, events_);
}

size_t Session::dsp_commit_events() { return staged_events_.commit(events_); }

void Session::dsp_notify() {
    if (event_) SetEvent(event_);
}

void Session::dsp_stopped(int64_t now) {
    WfEvent e = make_event(WF_EVENT_STOPPED, WF_SOURCE_SESSION,
                           mic_attempt_.load(std::memory_order_relaxed), sup_offset(now));
    // Published before the commit that makes Stopped visible, so a reader that has seen
    // Stopped also sees WfMetrics.state == WF_STATE_STOPPED.
    finished_.store(1, std::memory_order_release);
    staged_events_.stage(e, events_);
}

void Session::publish_direct(const WfEvent& e) {
    staged_events_.stage(e, events_);
    staged_events_.commit(events_);
    dsp_notify();
}

void Session::capture_supervisor_state() {
    const Supervisor::Counters& c = supervisor_.counters();
    state_.store(supervisor_.state(), std::memory_order_relaxed);
    restarts_.store(c.restarts, std::memory_order_relaxed);
    watchdog_recoveries_.store(c.watchdog_recoveries, std::memory_order_relaxed);
    orphaned_steps_.store(c.orphaned_steps, std::memory_order_relaxed);
    mic_attempt_.store(c.mic_attempt, std::memory_order_relaxed);
    mic_raw_.store(c.mic_raw, std::memory_order_relaxed);
    ref_mode_actual_.store(c.ref_mode_actual, std::memory_order_relaxed);
    render_switches_.store(c.render_switches, std::memory_order_relaxed);
    keepalive_failures_.store(c.keepalive_failures, std::memory_order_relaxed);
    keepalive_starts_.store(c.keepalive_starts, std::memory_order_relaxed);
}

int32_t Session::start() {
    if (started_.load(std::memory_order_acquire)) return WF_E_STATE;
    // Before any stream opens: the machine must not drop into (Modern) Standby during a
    // recording (first unattended 1-hour run, 2026-11-02). A refused request is reported through
    // WfDiagnostics.power_request; the recording itself goes on.
    power_->acquire();
    origin_100ns_ = now();
    timeline_.set_origin(origin_100ns_);
    timeline_.set_base_duration(cfg_.base_duration_sec);

    DspThread::Config dc;
    dc.others_mode = cfg_.others_mode;
    dc.ns_level = cfg_.ns_level;
    dc.timestamp_mode = cfg_.timestamp_mode;
    dc.enable_aec = cfg_.enable_aec != 0;
    dc.base_duration_sec = cfg_.base_duration_sec;
    dc.session_origin_100ns = origin_100ns_;
    dsp_.configure(this, dc, &Session::clock_fn, this);

    PlantParams pp;
    pp.others_mode = cfg_.others_mode;
    pp.target_pid = cfg_.target_pid;
    pp.prefer_raw = cfg_.prefer_raw;
    pp.keepalive_mode = cfg_.keepalive_mode;
    copy_id(pp.mic_device_id, cfg_.mic_device_id);
    copy_id(pp.render_device_id, cfg_.render_device_id);
    const bool reference_separate = (cfg_.others_mode == WF_OTHERS_PROCESS_INCLUDE);
    supervisor_.configure(this, plant_, queue_, pp, reference_separate);

    if (fallback_to_device_loopback_) {
        WfEvent e = make_event(WF_EVENT_FALLBACK_DEVICE_LOOPBACK, WF_SOURCE_OTHERS, 0, 0.0);
        set_detail(e, L"process loopback unavailable");
        sup_publish(e);
    }
    supervisor_.request_start(origin_100ns_);
    started_.store(1, std::memory_order_release);

    if (fake_) {
        if (fake_fail_start_) {   // the fake counterpart of a thread that cannot be created
            fake_fail_start_ = false;
            power_->release();
            return WF_E_FAIL;
        }
        capture_supervisor_state();
        return WF_OK;
    }
    if (!dsp_.start_thread()) {
        power_->release();
        return WF_E_FAIL;
    }
    sup_stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!sup_stop_) {
        power_->release();
        return WF_E_FAIL;
    }
    sup_thread_ = CreateThread(nullptr, 0, &Session::supervisor_thread_main, this, 0, nullptr);
    if (!sup_thread_) {
        power_->release();
        return WF_E_FAIL;
    }
    return WF_OK;
}

DWORD WINAPI Session::supervisor_thread_main(void* self) {
    static_cast<Session*>(self)->supervisor_run();
    return 0;
}

void Session::supervisor_run() {
    MtaScope mta;   // endpoint_peak / GetMixFormat run on this thread
    // IMMNotificationClient callbacks only post messages (spec 3.1), so registering here
    // keeps every COM object of the control plane on one apartment.
    if (SUCCEEDED(notifications_.register_with_system(&Session::notification_fn, this))) {
        notifications_registered_ = true;
    }
    for (;;) {
        HANDLE waits[2] = {sup_stop_, queue_->wake_handle()};
        const DWORD r = WaitForMultipleObjects(2, waits, FALSE, 10);
        supervisor_.poll(now());
        capture_supervisor_state();
        if (r == WAIT_OBJECT_0) break;
        if (supervisor_.stop_complete()) break;
    }
    if (notifications_registered_) {
        notifications_.unregister();
        notifications_registered_ = false;
    }
}

void Session::notification_fn(void* ctx, const DeviceNotification& n) {
    Session* s = static_cast<Session*>(ctx);
    wchar_t mic_id[kIdChars];
    AcquireSRWLockShared(&s->cfg_lock_);
    std::memcpy(mic_id, s->cfg_.mic_device_id, sizeof(mic_id));
    ReleaseSRWLockShared(&s->cfg_lock_);
    const bool explicit_mic = mic_id[0] != 0;
    // render_device_id never changes after create(), so it needs no lock.
    const wchar_t* pinned_render = s->cfg_.render_device_id;
    SupMsg m;
    m.role = static_cast<uint32_t>(Role::Mic);
    m.at_100ns = n.qpc_100ns;
    switch (n.kind) {
        case DeviceNotification::DefaultChanged: {
            if (n.flow == eRender) {
                // AP3: the console default is what the session opens and follows (plain
                // system loopback); its multimedia twin of the same change only extends the
                // debounce window, the communications role is not followed. Whether the
                // session is pinned is the supervisor's decision (a lost pinned endpoint
                // turns it into a follower).
                if (n.role == eCommunications) return;
                m.kind = SupMsg::Kind::RenderDefaultChanged;
                m.role = static_cast<uint32_t>(Role::Others);
                m.hr = (n.role == eConsole) ? 0 : 1;
                copy_id(m.device_id, n.device_id.c_str());
                break;
            }
            if (n.flow != eCapture || n.role == eMultimedia) return;
            if (explicit_mic) {
                // A configured mic ignores default changes - unless it currently runs on the
                // system default in its place (spec 3.1 fallback): then it follows the
                // (console) default like before, and the reopen tries the configured
                // endpoint first again.
                if (n.role != eConsole) return;
                wchar_t endpoint[kIdChars];
                if (!s->plant_ || !s->plant_->mic_endpoint(endpoint, kIdChars)) return;
                if (same_endpoint(mic_id, std::wstring(endpoint))) return;
                copy_id(m.device_id, mic_id);
            } else if (s->plant_) {
                // AP3: an unconfigured mic runs on the communications default (Teams'
                // device) and follows that role; a console change only matters while it runs
                // on the console default because there was no communications one. A change
                // to the endpoint it already runs on rebuilds nothing, so a console +
                // communications pair naming the same endpoint causes one rebuild at most.
                if (n.role == eConsole &&
                    s->plant_->mic_default_role() == kMicRoleCommunications) {
                    return;
                }
                wchar_t endpoint[kIdChars];
                if (s->plant_->mic_endpoint(endpoint, kIdChars) &&
                    same_endpoint(n.device_id.c_str(), std::wstring(endpoint))) {
                    return;
                }
            }
            m.kind = SupMsg::Kind::DefaultDeviceChanged;   // empty id == system default
            break;
        }
        case DeviceNotification::StateChanged:
        case DeviceNotification::Removed:
            // AP3: the pinned render endpoint left DEVICE_STATE_ACTIVE or was removed. (A
            // pinned endpoint that comes back is ignored: no hop back mid-recording.)
            if (pinned_render[0] && same_endpoint(pinned_render, n.device_id)) {
                if (n.kind == DeviceNotification::StateChanged && n.state == DEVICE_STATE_ACTIVE) {
                    return;
                }
                m.kind = SupMsg::Kind::RenderEndpointLost;
                m.role = static_cast<uint32_t>(Role::Others);
                m.hr = static_cast<int32_t>(n.state);
                copy_id(m.device_id, pinned_render);
                break;
            }
            if (!explicit_mic || !same_id(mic_id, n.device_id)) return;
            m.kind = SupMsg::Kind::DefaultDeviceChanged;
            copy_id(m.device_id, mic_id);
            break;
        case DeviceNotification::PropertyChanged: {
            // Spec 3.1 "Formatwechsel im Geraet": only a format key of the endpoint the mic
            // stream runs on can mean a mic format change. OnPropertyValueChanged reports
            // every key of every endpoint; measured 2026-11-01: the render endpoint's
            // {5b5e03cb-8070-420d-b794-8a28c89f6274},0 fires whenever a stream starts or
            // stops while audio plays - our own mic rebuild included, which used to close
            // the loop "notification -> rebuild -> notification".
            if (!is_format_property(n.key)) return;
            wchar_t endpoint[kIdChars];
            if (!s->plant_ || !s->plant_->mic_endpoint(endpoint, kIdChars)) return;
            if (!same_endpoint(endpoint, n.device_id)) return;
            m.kind = SupMsg::Kind::FormatChanged;
            copy_id(m.device_id, endpoint);
            break;
        }
        default:
            return;
    }
    s->queue_->post(m);
}

int32_t Session::set_mic_device(const wchar_t* device_id) {
    if (!started_.load(std::memory_order_acquire)) {
        AcquireSRWLockExclusive(&cfg_lock_);
        copy_id(cfg_.mic_device_id, device_id);
        ReleaseSRWLockExclusive(&cfg_lock_);
        return WF_OK;
    }
    if (stopped_.load(std::memory_order_acquire)) return WF_E_STATE;
    AcquireSRWLockExclusive(&cfg_lock_);
    copy_id(cfg_.mic_device_id, device_id);
    ReleaseSRWLockExclusive(&cfg_lock_);
    SupMsg m;
    m.kind = SupMsg::Kind::SetMicDevice;
    m.role = static_cast<uint32_t>(Role::Mic);
    m.at_100ns = now();
    copy_id(m.device_id, device_id);
    queue_->post(m);
    return WF_OK;
}

int32_t Session::stop() {
    if (!started_.load(std::memory_order_acquire)) return WF_E_STATE;
    // Idempotent: a repeated call queues nothing and reports the first call's outcome.
    if (stopped_.exchange(1, std::memory_order_acq_rel)) {
        return stop_rc_.load(std::memory_order_acquire);
    }

    int32_t rc = WF_OK;
    if (fake_ || !dsp_.thread_running()) {
        // No DSP thread (fake session, or its start failed): the calling thread is the only
        // producer, exactly as inside wf_fake_tick.
        dsp_.flush_stop(now());
    } else if (!dsp_.finish_thread(kStopWaitMs)) {
        // The DSP thread did not run the flush in time. Never flush from here while it may
        // still produce; only if it is provably between ticks may this thread take over -
        // and then only to say that the stop failed.
        rc = WF_E_FAIL;
        if (dsp_.take_producer()) {
            WfEvent e = make_event(WF_EVENT_ERROR, WF_SOURCE_SESSION,
                                   mic_attempt_.load(std::memory_order_relaxed),
                                   sup_offset(now()));
            e.hresult = static_cast<int32_t>(HRESULT_FROM_WIN32(WAIT_TIMEOUT));
            set_detail(e, L"stop: the DSP thread did not finish the flush");
            publish_direct(e);
        }
    }
    // Only now are the streams told to stop, so the flush above drained them while they
    // were still attached. The supervisor releases them in the background; destroy() waits.
    supervisor_.request_stop(now());
    if (fake_) {
        for (int i = 0; i < 64 && !supervisor_.stop_complete(); ++i) supervisor_.poll(now());
        capture_supervisor_state();
    }
    // The recording is over (or failed): the machine may sleep again.
    power_->release();
    stop_rc_.store(rc, std::memory_order_release);
    return rc;
}

uint32_t Session::read_frames(WfFrame* out, uint32_t cap) {
    if (!out || cap == 0) return 0;
    return static_cast<uint32_t>(frames_.pop(out, cap));
}

uint32_t Session::read_events(WfEvent* out, uint32_t cap) {
    if (!out || cap == 0) return 0;
    return static_cast<uint32_t>(events_.pop(out, cap));
}

void Session::get_levels(WfLevels* out) { dsp_.levels(out, now()); }

void Session::get_metrics(WfMetrics* out) {
    std::memset(out, 0, sizeof(*out));
    const DspMetrics& m = dsp_.metrics();
    out->erle_db = m.erle_db.load(std::memory_order_relaxed);
    out->drift_ppm_me = m.drift_ppm_me.load(std::memory_order_relaxed);
    out->drift_ppm_others = m.drift_ppm_others.load(std::memory_order_relaxed);
    out->jitter_p95_ms_me = m.jitter_p95_ms_me.load(std::memory_order_relaxed);
    out->jitter_p95_ms_others = m.jitter_p95_ms_others.load(std::memory_order_relaxed);
    out->unfiltered_frames = m.unfiltered_frames.load(std::memory_order_relaxed);
    out->late_dropped = m.late_dropped.load(std::memory_order_relaxed);
    out->ref_discarded = m.ref_discarded.load(std::memory_order_relaxed);
    out->inbox_overflows = m.inbox_overflows.load(std::memory_order_relaxed);
    out->timestamp_errors = m.timestamp_errors.load(std::memory_order_relaxed);
    out->discontinuities = m.discontinuities.load(std::memory_order_relaxed);
    out->frames_me = m.frames_me.load(std::memory_order_relaxed);
    out->frames_others = m.frames_others.load(std::memory_order_relaxed);
    out->aec_resets = m.aec_resets.load(std::memory_order_relaxed);
    out->ring_overflows = m.ring_overflows.load(std::memory_order_relaxed);
    out->delay_ms = m.delay_ms.load(std::memory_order_relaxed);

    out->watchdog_recoveries = watchdog_recoveries_.load(std::memory_order_relaxed);
    out->restarts = restarts_.load(std::memory_order_relaxed);
    out->frames_dropped = frames_.dropped();
    out->events_dropped = events_.dropped() + sup_events_.dropped() + queue_->dropped();
    // Stopped is queued: the session is stopped even while the supervisor is still releasing
    // the streams in the background (it would report STOPPING until then).
    out->state = finished_.load(std::memory_order_acquire) ? WF_STATE_STOPPED
                                                          : state_.load(std::memory_order_relaxed);
    out->mic_raw = mic_raw_.load(std::memory_order_relaxed);
    out->ref_mode_actual = ref_mode_actual_.load(std::memory_order_relaxed);
    out->attempt_id = mic_attempt_.load(std::memory_order_relaxed);
    out->orphaned_steps = orphaned_steps_.load(std::memory_order_relaxed);
}

void Session::get_diagnostics(WfDiagnostics* out) {
    const DspMetrics& m = dsp_.metrics();
    constexpr double kMs = 1.0 / static_cast<double>(k::kTicksPerMs);
    const int me = static_cast<int>(Role::Mic);
    const int oth = static_cast<int>(Role::Others);
    const int ref = static_cast<int>(Role::Reference);
    out->power_request = power_ ? power_->state() : WF_POWER_REQUEST_NONE;
    out->dsp_tick_gap_max_ms =
        static_cast<double>(m.tick_gap_max_100ns.load(std::memory_order_relaxed)) * kMs;
    out->capture_gap_max_ms_me =
        static_cast<double>(m.capture_gap_max_100ns[me].load(std::memory_order_relaxed)) * kMs;
    out->capture_gap_max_ms_others =
        static_cast<double>(m.capture_gap_max_100ns[oth].load(std::memory_order_relaxed)) * kMs;
    out->capture_gap_max_ms_reference =
        static_cast<double>(m.capture_gap_max_100ns[ref].load(std::memory_order_relaxed)) * kMs;
    out->ring_high_water_me = m.ring_high_water[me].load(std::memory_order_relaxed);
    out->ring_high_water_others = m.ring_high_water[oth].load(std::memory_order_relaxed);
    out->ring_high_water_reference = m.ring_high_water[ref].load(std::memory_order_relaxed);
    out->ring_capacity_slots = static_cast<uint32_t>(AudioRing::kSlots);
    out->dsp_slices_max = m.slices_max.load(std::memory_order_relaxed);
    out->ring_loss_gaps = m.ring_loss_gaps.load(std::memory_order_relaxed);
    out->render_switches = render_switches_.load(std::memory_order_relaxed);
    out->aec_render_resets =
        static_cast<uint32_t>(m.aec_render_resets.load(std::memory_order_relaxed));
    out->keepalive_failures = keepalive_failures_.load(std::memory_order_relaxed);
    out->keepalive_starts = keepalive_starts_.load(std::memory_order_relaxed);
}

int32_t Session::get_origin(int64_t* origin_100ns, double* base_duration_sec) const {
    // start() writes origin_100ns_ before its release store of started_.
    if (!started_.load(std::memory_order_acquire)) return WF_E_STATE;
    *origin_100ns = origin_100ns_;
    *base_duration_sec = cfg_.base_duration_sec;   // fixed since create()
    return WF_OK;
}

// ---------------------------------------------------------------------------
// Fake path
// ---------------------------------------------------------------------------
int32_t Session::fake_push(uint32_t source, const float* samples, uint32_t frames, uint32_t rate,
                           int64_t qpc_100ns, uint64_t device_position, uint32_t flags) {
    if (!fake_plant_) return WF_E_NOT_FAKE;
    Role r;
    if (!fake_source_to_role(source, &r) || !samples) return WF_E_INVALID_ARG;
    return fake_plant_->push(r, samples, frames, rate, qpc_100ns, device_position, flags, now());
}

int32_t Session::fake_set_now(int64_t qpc_100ns) {
    if (!fake_plant_) return WF_E_NOT_FAKE;
    if (qpc_100ns < fake_now_.load(std::memory_order_acquire)) return WF_E_INVALID_ARG;
    fake_now_.store(qpc_100ns, std::memory_order_release);
    return WF_OK;
}

int32_t Session::fake_tick() {
    if (!fake_plant_) return WF_E_NOT_FAKE;
    if (!started_.load(std::memory_order_acquire)) return WF_E_STATE;
    if (stopped_.load(std::memory_order_acquire)) return WF_E_STATE;
    const int64_t t = now();
    supervisor_.poll(t);
    capture_supervisor_state();
    dsp_.tick(t);
    return WF_OK;
}

int32_t Session::fake_signal(uint32_t signal, uint32_t source, int32_t hresult) {
    if (!fake_plant_) return WF_E_NOT_FAKE;
    if (signal == WF_FAKE_SIGNAL_SESSION_START_FAILS) {
        fake_fail_start_ = true;
        return WF_OK;
    }
    if (signal >= WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED &&
        signal <= WF_FAKE_SIGNAL_KEEPALIVE_EXITS) {
        return fake_endpoint_signal(signal, hresult);   // `source` is ignored
    }
    Role r = Role::Mic;
    if (!fake_source_to_role(source, &r)) return WF_E_INVALID_ARG;
    if (signal == WF_FAKE_SIGNAL_FORMAT_CHANGE || signal == WF_FAKE_SIGNAL_PROPERTY_CHANGED) {
        // Both arrive the way the real ones do: as an OnPropertyValueChanged notification
        // through notification_fn, so the endpoint/key filter under test is production code.
        // {5b5e03cb-...},0 is the key measured on the render endpoint while audio plays.
        static const PROPERTYKEY kActivityKey = {
            {0x5b5e03cb, 0x8070, 0x420d, {0xb7, 0x94, 0x8a, 0x28, 0xc8, 0x9f, 0x62, 0x74}}, 0};
        DeviceNotification n{};
        n.kind = DeviceNotification::PropertyChanged;
        n.qpc_100ns = now();
        bool on_mic = true;
        bool format_key = true;
        if (signal == WF_FAKE_SIGNAL_FORMAT_CHANGE) {
            fake_plant_->change_mic_format();
        } else {
            on_mic = (r == Role::Mic);
            format_key = (hresult != 0);
        }
        n.key = format_key ? kPkeyAudioEngineDeviceFormat : kActivityKey;
        n.device_id = on_mic ? fake_plant_->mic_endpoint_or_default()
                             : std::wstring(FakePlant::kRenderEndpoint);
        notification_fn(this, n);
        return WF_OK;
    }
    return fake_plant_->signal(signal, r, hresult, now());
}

void Session::fake_notify_default(EDataFlow flow, ERole role, const std::wstring& id) {
    DeviceNotification n{};
    n.kind = DeviceNotification::DefaultChanged;
    n.flow = flow;
    n.role = role;
    n.device_id = id;
    n.qpc_100ns = now();
    notification_fn(this, n);
}

// AP3 endpoint signals: change the fake endpoint model, then deliver the callbacks a real
// system sends for that change through the production filter (notification_fn).
int32_t Session::fake_endpoint_signal(uint32_t signal, int32_t hresult) {
    using FP = FakePlant;
    switch (signal) {
        case WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED:
            if (!fake_plant_->set_default(true, hresult, FP::kConsole)) return WF_E_INVALID_ARG;
            fake_notify_default(eRender, eConsole, FP::render_id(hresult));
            fake_notify_default(eRender, eMultimedia, FP::render_id(hresult));
            return WF_OK;
        case WF_FAKE_SIGNAL_DEFAULT_COMMS_RENDER_CHANGED:
            if (!fake_plant_->set_default(true, hresult, FP::kCommunications)) return WF_E_INVALID_ARG;
            fake_notify_default(eRender, eCommunications, FP::render_id(hresult));
            return WF_OK;
        case WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE: {
            const int index = hresult & 0xff;
            const uint32_t state = static_cast<uint32_t>(hresult) >> 8;
            uint32_t moved = 0;
            if (!fake_plant_->set_render_state(index, state, &moved)) return WF_E_INVALID_ARG;
            DeviceNotification n{};
            n.kind = DeviceNotification::StateChanged;
            n.state = state;
            n.device_id = FP::render_id(index);
            n.qpc_100ns = now();
            notification_fn(this, n);
            if (moved & FP::kConsole) {
                const std::wstring id = fake_plant_->default_id(true, FP::kConsole);
                fake_notify_default(eRender, eConsole, id);
                fake_notify_default(eRender, eMultimedia, id);
            }
            if (moved & FP::kCommunications) {
                fake_notify_default(eRender, eCommunications,
                                    fake_plant_->default_id(true, FP::kCommunications));
            }
            return WF_OK;
        }
        case WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED:
            if (hresult < 0 || !fake_plant_->set_default(false, hresult, FP::kConsole)) {
                return WF_E_INVALID_ARG;
            }
            fake_notify_default(eCapture, eConsole, FP::capture_id(hresult));
            fake_notify_default(eCapture, eMultimedia, FP::capture_id(hresult));
            return WF_OK;
        case WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED:
            if (!fake_plant_->set_default(false, hresult, FP::kCommunications)) return WF_E_INVALID_ARG;
            // No communications default any more: Windows reports a null id.
            fake_notify_default(eCapture, eCommunications,
                                hresult < 0 ? std::wstring() : FP::capture_id(hresult));
            return WF_OK;
        case WF_FAKE_SIGNAL_KEEPALIVE_START_FAILS:
            fake_plant_->keepalive_fail(hresult != 0 ? hresult : static_cast<int32_t>(E_FAIL));
            return WF_OK;
        case WF_FAKE_SIGNAL_KEEPALIVE_EXITS:
            return fake_plant_->keepalive_exit(hresult);
        default:
            return WF_E_INVALID_ARG;
    }
}

int32_t Session::fake_query(uint32_t what, wchar_t* text, uint32_t text_chars, uint32_t* value) {
    if (!fake_plant_) return WF_E_NOT_FAKE;
    if (!text && text_chars > 0) return WF_E_INVALID_ARG;
    return fake_plant_->query(what, text, text_chars, value);
}

}  // namespace wf
