// FakePlant implementation.
#include "session/fake_capture.h"

#include <mmdeviceapi.h>

#include <cstring>
#include <cwchar>

namespace wf {

namespace {
constexpr int32_t kEndpointNotFound = static_cast<int32_t>(0x80070490);   // E_NOTFOUND
constexpr int32_t kFail = static_cast<int32_t>(0x80004005);               // E_FAIL
constexpr int32_t kDeviceInvalidated = static_cast<int32_t>(0x88890004);  // AUDCLNT_E_DEVICE_INVALIDATED

bool same_id(const wchar_t* a, const std::wstring& b) {
    if (!a || !a[0] || b.empty()) return false;
    return CompareStringOrdinal(a, -1, b.c_str(), -1, TRUE) == CSTR_EQUAL;
}

template <size_t N>
void copy_w(wchar_t (&dst)[N], const std::wstring& src) {
    size_t i = 0;
    for (; i < src.size() && i + 1 < N; ++i) dst[i] = src[i];
    dst[i] = 0;
}
}  // namespace

bool fake_source_to_role(uint32_t fake_source, Role* out) {
    switch (fake_source) {
        case WF_FAKE_SRC_MIC: *out = Role::Mic; return true;
        case WF_FAKE_SRC_OTHERS: *out = Role::Others; return true;
        case WF_FAKE_SRC_REFERENCE: *out = Role::Reference; return true;
        default: return false;
    }
}

FakePlant::FakePlant(std::shared_ptr<MsgQueue> queue) : queue_(std::move(queue)) {
    for (int i = 0; i < kEndpoints; ++i) {
        render_[i].id = render_id(i);
        render_[i].name = L"Fake Speakers " + std::to_wstring(i);
        capture_[i].id = capture_id(i);
        capture_[i].name = L"Fake Microphone " + std::to_wstring(i);
    }
}

std::wstring FakePlant::render_id(int index) {
    if (index == 0) return kRenderEndpoint;
    return L"{0.0.0.00000000}.{fake-render-" + std::to_wstring(index) + L"}";
}

std::wstring FakePlant::capture_id(int index) {
    if (index == 0) return kDefaultCaptureEndpoint;
    return L"{0.0.1.00000000}.{fake-capture-" + std::to_wstring(index) + L"}";
}

int FakePlant::find(bool render, const wchar_t* id) const {
    const Endpoint* list = render ? render_ : capture_;
    for (int i = 0; i < kEndpoints; ++i) {
        if (same_id(id, list[i].id)) return i;
    }
    return -1;
}

bool FakePlant::active(bool render, int index) const {
    if (index < 0 || index >= kEndpoints) return false;
    return (render ? render_ : capture_)[index].state == DEVICE_STATE_ACTIVE;
}

std::wstring FakePlant::name_of(bool render, const std::wstring& id) const {
    const int i = find(render, id.c_str());
    if (i >= 0) return (render ? render_ : capture_)[i].name;
    return id;   // an endpoint outside the model (a configured test id): its id is its name
}

std::wstring FakePlant::default_id(bool render, uint32_t role) const {
    int i = 0;
    if (render) i = (role == kCommunications) ? default_comms_render_ : default_render_;
    else i = (role == kCommunications) ? default_comms_capture_ : default_capture_;
    if (i < 0 || !active(render, i)) return std::wstring();
    return (render ? render_ : capture_)[i].id;
}

bool FakePlant::set_default(bool render, int index, uint32_t roles) {
    if (index < -1 || index >= kEndpoints) return false;
    if (index == -1 && (render || roles != kCommunications)) return false;
    if (render) {
        if (roles & kConsole) default_render_ = index;
        if (roles & kCommunications) default_comms_render_ = index;
    } else {
        if (roles & kConsole) default_capture_ = index;
        if (roles & kCommunications) default_comms_capture_ = index;
    }
    return true;
}

bool FakePlant::set_render_state(int index, uint32_t state, uint32_t* moved) {
    if (index < 0 || index >= kEndpoints) return false;
    render_[index].state = state;
    uint32_t m = 0;
    if (state != DEVICE_STATE_ACTIVE) {
        int next = -1;
        for (int i = 0; i < kEndpoints && next < 0; ++i) {
            if (active(true, i)) next = i;
        }
        if (next >= 0) {
            if (default_render_ == index) {
                default_render_ = next;
                m |= kConsole;
            }
            if (default_comms_render_ == index) {
                default_comms_render_ = next;
                m |= kCommunications;
            }
        }
    }
    if (moved) *moved = m;
    return true;
}

void FakePlant::launch(const std::shared_ptr<StepCtx>& ctx) {
    if (hang_next_step_) {
        // Spec 3.1: "haengt ein Versuch, gilt er als verwaist". The step never completes;
        // the supervisor abandons it after 5 s of virtual time and `ctx` keeps the
        // channel alive exactly like a real orphaned worker thread would.
        hang_next_step_ = false;
        return;
    }
    run_step(ctx);
}

int32_t FakePlant::do_start(StepCtx& ctx) {
    const int idx = static_cast<int>(ctx.params.role);
    if (start_hr_[idx] != 0) return start_hr_[idx];
    StreamChannel* ch = ctx.channel.get();

    if (ctx.params.role == Role::Mic) {
        // Spec 3.1, same rule as RealPlant::do_start: the configured endpoint failed, so the
        // system (console) default is opened in the same attempt - except after
        // E_ACCESSDENIED.
        bool fallback = false;
        const bool configured = ctx.params.mic_device_id[0] != 0;
        if (configured && configured_mic_hr_ != 0) {
            if (!mic_fallback_allowed(configured_mic_hr_)) return configured_mic_hr_;
            fallback = true;
            if (ch) {
                set_mic_fallback(*ch, configured_mic_hr_,
                                 configured_mic_hr_ == kEndpointNotFound
                                     ? L"selected microphone missing"
                                     : L"selected microphone unavailable");
            }
        }
        // AP3: an unconfigured mic opens the communications default, the console default
        // when there is none.
        std::wstring endpoint;
        uint32_t role = kMicRoleExplicit;
        if (configured && !fallback) {
            endpoint = ctx.params.mic_device_id;
        } else if (!configured && !(endpoint = default_id(false, kCommunications)).empty()) {
            role = kMicRoleCommunications;
        } else {
            endpoint = default_id(false, kConsole);
            if (!configured) role = kMicRoleConsole;
        }
        if (endpoint.empty()) return kEndpointNotFound;
        channels_[idx] = ctx.channel;
        mic_endpoint_ = endpoint;
        last_mic_endpoint_ = mic_endpoint_;
        mic_role_ = role;
        ++mic_opens_;
        mic_open_format_ = endpoint_format_;
        if (ch) {
            copy_w(ch->endpoint_id, endpoint);
            compose_detail(ch->endpoint_label, 128, L"mic:", name_of(false, endpoint).c_str());
        }
    } else if (ctx.params.role == Role::Others &&
               ctx.params.others_mode == WF_OTHERS_DEVICE_LOOPBACK) {
        // Device loopback: the pinned render endpoint, or the console default. A pinned one
        // that is not ACTIVE (or unknown) falls back to the default in the same attempt.
        std::wstring endpoint;
        const wchar_t* pinned = ctx.params.render_device_id;
        if (pinned[0]) {
            const int i = find(true, pinned);
            if (active(true, i)) {
                endpoint = render_[i].id;
            } else {
                endpoint = default_id(true, kConsole);
                if (endpoint.empty()) return kEndpointNotFound;
                if (ch) {
                    compose_detail(ch->fallback_detail, 128, L"fallback:", L"selected output missing");
                    wchar_t label[128];
                    compose_detail(label, 128, L"render:", name_of(true, endpoint).c_str());
                    append_detail(ch->fallback_detail, 128, label);
                    ch->fallback_hr = kEndpointNotFound;
                }
            }
        } else {
            endpoint = default_id(true, kConsole);
            if (endpoint.empty()) return kEndpointNotFound;
        }
        channels_[idx] = ctx.channel;
        loopback_endpoint_ = endpoint;
        ++loopback_opens_;
        if (ch) {
            copy_w(ch->endpoint_id, endpoint);
            compose_detail(ch->endpoint_label, 128, L"render:", name_of(true, endpoint).c_str());
        }
    } else {
        channels_[idx] = ctx.channel;   // process loopback: not bound to an endpoint
    }
    if (ch) {
        ch->live.store(1, std::memory_order_relaxed);
        ch->raw_accepted.store(ctx.params.prefer_raw ? 1u : 0u, std::memory_order_relaxed);
    }
    return 0;
}

void FakePlant::do_stop(StepCtx& ctx) {
    const int idx = static_cast<int>(ctx.params.role);
    if (channels_[idx] == ctx.channel) {
        channels_[idx].reset();
        if (ctx.params.role == Role::Mic) mic_endpoint_.clear();
        if (ctx.params.role == Role::Others) loopback_endpoint_.clear();
    }
    if (ctx.channel) ctx.channel->live.store(0, std::memory_order_relaxed);
}

int32_t FakePlant::do_keepalive(StepCtx& ctx) {
    if (ctx.kind == StepCtx::Kind::KeepAliveStop) {
        keepalive_endpoint_.clear();
        keepalive_exit_pending_ = false;
        return 0;
    }
    ++keepalive_attempts_;
    std::wstring endpoint;
    const wchar_t* id = ctx.params.render_device_id;
    if (id[0]) {
        const int i = find(true, id);
        if (active(true, i)) {
            endpoint = render_[i].id;
        } else if (ctx.allow_fallback) {
            endpoint = default_id(true, kConsole);
            if (!endpoint.empty()) {
                compose_detail(ctx.fallback_detail, 128, L"fallback:", L"selected output missing");
                wchar_t label[128];
                compose_detail(label, 128, L"render:", name_of(true, endpoint).c_str());
                append_detail(ctx.fallback_detail, 128, label);
                ctx.fallback_hr = kEndpointNotFound;
            }
        }
        if (endpoint.empty()) {
            compose_detail(ctx.endpoint_label, 128, L"render:", name_of(true, id).c_str());
            return kEndpointNotFound;
        }
    } else {
        endpoint = default_id(true, kConsole);
        if (endpoint.empty()) return kEndpointNotFound;
    }
    copy_w(ctx.endpoint_id, endpoint);
    compose_detail(ctx.endpoint_label, 128, L"render:", name_of(true, endpoint).c_str());
    if (keepalive_fail_hr_ != 0) return keepalive_fail_hr_;   // the old keep-alive stays as is
    keepalive_endpoint_ = endpoint;   // make before break: the new one runs, the old is gone
    keepalive_exit_pending_ = false;
    return 0;
}

bool FakePlant::keepalive_exited(int32_t* hr) {
    if (!keepalive_exit_pending_) return false;
    keepalive_exit_pending_ = false;
    keepalive_endpoint_.clear();
    if (hr) *hr = keepalive_exit_hr_;
    return true;
}

int32_t FakePlant::keepalive_exit(int32_t hr) {
    if (keepalive_endpoint_.empty()) return WF_E_STATE;
    keepalive_exit_pending_ = true;
    keepalive_exit_hr_ = hr != 0 ? hr : kDeviceInvalidated;
    return WF_OK;
}

void FakePlant::endpoint_name(bool render, const wchar_t* id, wchar_t* out, size_t cap) {
    std::wstring target;
    if (id && id[0]) {
        target = id;
    } else if (render) {
        target = default_id(true, kConsole);
    } else {
        target = default_id(false, kCommunications);
        if (target.empty()) target = default_id(false, kConsole);
    }
    const std::wstring name = target.empty() ? std::wstring(L"unknown endpoint") : name_of(render, target);
    compose_detail(out, cap, nullptr, name.c_str());
}

int32_t FakePlant::query(uint32_t what, wchar_t* text, uint32_t text_chars, uint32_t* value) const {
    const std::wstring* s = nullptr;
    uint32_t v = 0;
    switch (what) {
        case WF_FAKE_QUERY_LOOPBACK: s = &loopback_endpoint_; v = loopback_opens_; break;
        case WF_FAKE_QUERY_KEEPALIVE: s = &keepalive_endpoint_; v = keepalive_attempts_; break;
        case WF_FAKE_QUERY_MIC: s = &mic_endpoint_; v = mic_opens_; break;
        default: return WF_E_INVALID_ARG;
    }
    if (text && text_chars > 0) compose_detail(text, text_chars, nullptr, s->c_str());
    if (value) *value = v;
    return WF_OK;
}

std::shared_ptr<StreamChannel> FakePlant::make_channel(Role r, uint32_t attempt, bool app_source) {
    return std::make_shared<StreamChannel>(r, attempt, app_source, queue_);
}

bool FakePlant::reference_watchdog_armed(int64_t now_100ns) {
    (void)now_100ns;
    return watchdog_armed_;
}

bool FakePlant::mic_format_changed(const wchar_t* device_id) {
    (void)device_id;
    if (mic_endpoint_.empty()) return false;
    return !same_pcm_format(mic_open_format_, endpoint_format_);
}

bool FakePlant::mic_endpoint(wchar_t* out, size_t cap) {
    if (!out || cap == 0) return false;
    size_t i = 0;
    for (; i < mic_endpoint_.size() && i + 1 < cap; ++i) out[i] = mic_endpoint_[i];
    out[i] = 0;
    return out[0] != 0;
}

void FakePlant::change_mic_format() {
    endpoint_format_.rate = endpoint_format_.rate == 48000 ? 44100 : 48000;
}

std::wstring FakePlant::mic_endpoint_or_default() const {
    if (!mic_endpoint_.empty()) return mic_endpoint_;
    if (!last_mic_endpoint_.empty()) return last_mic_endpoint_;
    std::wstring d = default_id(false, kCommunications);
    return d.empty() ? default_id(false, kConsole) : d;
}

int32_t FakePlant::push(Role r, const float* samples, uint32_t frames, uint32_t rate,
                        int64_t qpc_100ns, uint64_t device_position, uint32_t flags,
                        int64_t now_100ns) {
    const int idx = static_cast<int>(r);
    if (frames == 0 || rate == 0) return WF_E_INVALID_ARG;
    const std::shared_ptr<StreamChannel>& ch = channels_[idx];
    if (!ch) return WF_E_STATE;
    // "The stream is open but the driver delivers nothing" - the first-buffer timeout
    // and the 0.5 s watchdog both hang off this.
    if (no_first_packet_[idx]) return WF_OK;

    WfPacket p{samples, frames, qpc_100ns, device_position, flags, rate};
    ch->deliver(p, now_100ns);
    return WF_OK;
}

int32_t FakePlant::signal(uint32_t sig, Role r, int32_t hr, int64_t now_100ns) {
    const int idx = static_cast<int>(r);
    const std::shared_ptr<StreamChannel>& ch = channels_[idx];
    switch (sig) {
        case WF_FAKE_SIGNAL_DEVICE_LOST:
            if (!ch) return WF_E_STATE;
            ch->post(SupMsg::Kind::StreamDeviceLost, hr != 0 ? hr : kDeviceInvalidated, now_100ns);
            return WF_OK;
        case WF_FAKE_SIGNAL_STREAM_ERROR:
            if (!ch) return WF_E_STATE;
            ch->post(SupMsg::Kind::StreamError, hr, now_100ns);
            return WF_OK;
        // WF_FAKE_SIGNAL_FORMAT_CHANGE and WF_FAKE_SIGNAL_PROPERTY_CHANGED never get here:
        // Session::fake_signal turns them into endpoint notifications for its own filter.
        case WF_FAKE_SIGNAL_DEFAULT_DEVICE_CHANGED: {
            SupMsg m;
            m.kind = SupMsg::Kind::DefaultDeviceChanged;
            m.role = static_cast<uint32_t>(Role::Mic);
            m.at_100ns = now_100ns;
            queue_->post(m);
            return WF_OK;
        }
        case WF_FAKE_SIGNAL_NO_FIRST_PACKET:
            no_first_packet_[idx] = true;
            return WF_OK;
        case WF_FAKE_SIGNAL_START_FAILS:
            start_hr_[idx] = hr != 0 ? hr : kFail;
            return WF_OK;
        case WF_FAKE_SIGNAL_CLEAR:
            no_first_packet_[idx] = false;
            start_hr_[idx] = 0;
            if (r == Role::Mic) configured_mic_hr_ = 0;
            if (r == Role::Others) keepalive_fail_hr_ = 0;
            return WF_OK;
        case WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS:
            if (r != Role::Mic) return WF_E_INVALID_ARG;
            configured_mic_hr_ = hr != 0 ? hr : kEndpointNotFound;
            return WF_OK;
        case WF_FAKE_SIGNAL_HANG_STEP:
            hang_next_step_ = true;
            return WF_OK;
        default:
            return WF_E_INVALID_ARG;
    }
}

}  // namespace wf
