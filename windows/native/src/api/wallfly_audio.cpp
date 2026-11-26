// C ABI implementation. Everything here is a thin, validating shell over wf::Session;
// the only logic that lives in this file is argument checking, the string copying into
// the fixed-size ABI arrays and the MTA worker the two enumeration calls run on.
#include "api/wallfly_audio.h"

#include <windows.h>

#include <cstring>
#include <memory>
#include <string>
#include <vector>

#include "session/power_request.h"
#include "session/session.h"
#include "wasapi/com_util.h"
#include "wasapi/enumerator.h"
#include "wasapi/process_loopback.h"
#include "wasapi/sessions.h"

namespace {

using wf::Session;

Session* as_session(void* p) { return static_cast<Session*>(p); }

void copy_w(wchar_t* dst, size_t cap, const wchar_t* src) {
    if (cap == 0) return;
    size_t i = 0;
    if (src) {
        while (src[i] && i < cap - 1) {
            dst[i] = src[i];
            ++i;
        }
    }
    dst[i] = 0;
}

void copy_utf8(wchar_t* dst, size_t cap, const std::string& s) {
    const std::wstring w = wf::widen(s.c_str());
    copy_w(dst, cap, w.c_str());
}

EDataFlow flow_of(uint32_t flow, bool* ok) {
    *ok = true;
    switch (flow) {
        case WF_FLOW_RENDER: return eRender;
        case WF_FLOW_CAPTURE: return eCapture;
        case WF_FLOW_ALL: return eAll;
        default: *ok = false; return eAll;
    }
}

// ---------------------------------------------------------------------------
// MTA worker for the two enumeration calls. Callers may be STA (WPF), and both
// IMMDeviceEnumerator and IAudioSessionManager2 want an MTA thread here.
// ---------------------------------------------------------------------------
struct ListJob {
    EDataFlow flow = eAll;
    bool sessions = false;
    HRESULT hr = E_FAIL;
    std::vector<wf::EndpointInfo> endpoints;
    std::vector<wf::SessionInfo> audio_sessions;
};

struct ListJobArg {
    std::shared_ptr<ListJob> job;
};

DWORD WINAPI list_worker(void* p) {
    ListJobArg* arg = static_cast<ListJobArg*>(p);
    {
        wf::MtaScope mta;
        if (arg->job->sessions) {
            arg->job->hr = wf::list_sessions(arg->job->flow, &arg->job->audio_sessions);
        } else {
            arg->job->hr = wf::list_endpoints(arg->job->flow, DEVICE_STATE_ACTIVE,
                                              &arg->job->endpoints);
        }
    }
    delete arg;
    return 0;
}

// Returns false when the worker did not finish within 10 s (the job is then abandoned).
bool run_list_job(const std::shared_ptr<ListJob>& job) {
    auto* arg = new ListJobArg{job};
    HANDLE h = CreateThread(nullptr, 0, &list_worker, arg, 0, nullptr);
    if (!h) {
        delete arg;
        return false;
    }
    const DWORD r = WaitForSingleObject(h, 10000);
    CloseHandle(h);
    return r == WAIT_OBJECT_0;
}

template <class T>
int32_t finish_list(const std::vector<T>& items, uint32_t cap, uint32_t* count,
                    bool have_out) {
    *count = static_cast<uint32_t>(items.size());
    if (!have_out || static_cast<size_t>(cap) < items.size()) return WF_E_BUFFER_TOO_SMALL;
    return WF_OK;
}

}  // namespace

extern "C" {

uint32_t wf_abi_version(void) { return WF_ABI_VERSION; }

int32_t wf_capabilities(uint32_t* flags) {
    if (!flags) return WF_E_INVALID_ARG;
    uint32_t f = WF_CAP_FAKE;
    if (wf::process_loopback_api_available()) f |= WF_CAP_PROCESS_LOOPBACK;
#if defined(WF_WITH_AEC3) && WF_WITH_AEC3
    f |= WF_CAP_AEC3;   // src/aec/AECBridge.cpp is linked in, not the passthrough stub
#endif
    *flags = f;
    return WF_OK;
}

int32_t wf_devices_list(uint32_t flow, WfDeviceInfo* out, uint32_t cap, uint32_t* count) {
    if (!count) return WF_E_INVALID_ARG;
    if (!out && cap > 0) return WF_E_INVALID_ARG;   // before the (slow) enumeration
    bool ok = false;
    const EDataFlow f = flow_of(flow, &ok);
    if (!ok) return WF_E_INVALID_ARG;

    auto job = std::make_shared<ListJob>();
    job->flow = f;
    job->sessions = false;
    if (!run_list_job(job)) return WF_E_FAIL;
    if (FAILED(job->hr)) return WF_E_FAIL;

    const bool have_out = (out != nullptr && cap > 0);
    const int32_t rc = finish_list(job->endpoints, cap, count, have_out);
    if (rc != WF_OK) return rc;

    for (size_t i = 0; i < job->endpoints.size(); ++i) {
        const wf::EndpointInfo& e = job->endpoints[i];
        WfDeviceInfo& d = out[i];
        std::memset(&d, 0, sizeof(d));
        d.flow = e.is_render ? WF_FLOW_RENDER : WF_FLOW_CAPTURE;
        d.state = e.state;
        d.mix_rate = e.mix_rate;
        d.mix_channels = e.mix_channels;
        d.is_default_console = e.is_default_console ? 1u : 0u;
        d.is_default_communications = e.is_default_communications ? 1u : 0u;
        d.raw_supported = e.raw_supported;
        copy_w(d.id, sizeof(d.id) / sizeof(wchar_t), e.id.c_str());
        copy_utf8(d.name, sizeof(d.name) / sizeof(wchar_t), e.name);
    }
    return WF_OK;
}

int32_t wf_audio_sessions_list(uint32_t flow, WfAudioSessionInfo* out, uint32_t cap,
                               uint32_t* count) {
    if (!count) return WF_E_INVALID_ARG;
    if (!out && cap > 0) return WF_E_INVALID_ARG;   // before the (slow) enumeration
    bool ok = false;
    const EDataFlow f = flow_of(flow, &ok);
    if (!ok) return WF_E_INVALID_ARG;

    auto job = std::make_shared<ListJob>();
    job->flow = f;
    job->sessions = true;
    if (!run_list_job(job)) return WF_E_FAIL;
    if (FAILED(job->hr)) return WF_E_FAIL;

    const bool have_out = (out != nullptr && cap > 0);
    const int32_t rc = finish_list(job->audio_sessions, cap, count, have_out);
    if (rc != WF_OK) return rc;

    for (size_t i = 0; i < job->audio_sessions.size(); ++i) {
        const wf::SessionInfo& s = job->audio_sessions[i];
        WfAudioSessionInfo& a = out[i];
        std::memset(&a, 0, sizeof(a));
        a.pid = s.pid;
        a.parent_pid = s.parent_pid;
        a.state = static_cast<uint32_t>(s.state);
        a.is_render = s.endpoint_is_render ? 1u : 0u;
        a.is_system_sounds = s.is_system_sounds ? 1u : 0u;
        a.peak = s.peak;
        copy_utf8(a.process_name, sizeof(a.process_name) / sizeof(wchar_t), s.process_name);
        copy_utf8(a.display_name, sizeof(a.display_name) / sizeof(wchar_t), s.display_name);
        copy_w(a.endpoint_id, sizeof(a.endpoint_id) / sizeof(wchar_t), s.endpoint_id.c_str());
    }
    return WF_OK;
}

int32_t wf_session_create(const WfSessionConfig* cfg, void** out_session) {
    if (!cfg || !out_session) return WF_E_INVALID_ARG;
    int32_t err = WF_OK;
    Session* s = Session::create(*cfg, false, &err);
    if (!s) return err;
    *out_session = s;
    return WF_OK;
}

int32_t wf_session_create_fake(const WfSessionConfig* cfg, void** out_session) {
    if (!cfg || !out_session) return WF_E_INVALID_ARG;
    int32_t err = WF_OK;
    Session* s = Session::create(*cfg, true, &err);
    if (!s) return err;
    *out_session = s;
    return WF_OK;
}

int32_t wf_session_start(void* session) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->start();
}

int32_t wf_session_set_mic_device(void* session, const wchar_t* device_id) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->set_mic_device(device_id);
}

int32_t wf_session_stop(void* session) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->stop();
}

int32_t wf_session_destroy(void* session) {
    if (!session) return WF_E_INVALID_ARG;
    // Deletes the session, or abandons (leaks) it when one of its threads did not return.
    return Session::destroy(as_session(session)) ? WF_OK : WF_E_FAIL;
}

void* wf_session_event_handle(void* session) {
    if (!session) return nullptr;
    return as_session(session)->event_handle();
}

int32_t wf_session_read_frames(void* session, WfFrame* out, uint32_t cap, uint32_t* count) {
    if (!session || !count) return WF_E_INVALID_ARG;
    if (!out && cap > 0) return WF_E_INVALID_ARG;
    *count = as_session(session)->read_frames(out, cap);
    return WF_OK;
}

int32_t wf_session_read_events(void* session, WfEvent* out, uint32_t cap, uint32_t* count) {
    if (!session || !count) return WF_E_INVALID_ARG;
    if (!out && cap > 0) return WF_E_INVALID_ARG;
    *count = as_session(session)->read_events(out, cap);
    return WF_OK;
}

int32_t wf_session_get_levels(void* session, WfLevels* out) {
    if (!session || !out) return WF_E_INVALID_ARG;
    as_session(session)->get_levels(out);
    return WF_OK;
}

int32_t wf_session_get_metrics(void* session, WfMetrics* out) {
    if (!session || !out) return WF_E_INVALID_ARG;
    as_session(session)->get_metrics(out);
    return WF_OK;
}

int32_t wf_session_get_origin(void* session, int64_t* origin_qpc_100ns, double* base_duration_sec) {
    if (!session || !origin_qpc_100ns || !base_duration_sec) return WF_E_INVALID_ARG;
    return as_session(session)->get_origin(origin_qpc_100ns, base_duration_sec);
}

int32_t wf_session_get_diagnostics(void* session, WfDiagnostics* out) {
    if (!session || !out) return WF_E_INVALID_ARG;
    const uint32_t size = out->size;
    if (size < 8) return WF_E_INVALID_ARG;
    WfDiagnostics d;
    std::memset(&d, 0, sizeof(d));
    as_session(session)->get_diagnostics(&d);
    // Size-versioned: an older caller gets the prefix it knows, a newer one our full struct.
    const uint32_t n = size < sizeof(d) ? size : static_cast<uint32_t>(sizeof(d));
    d.size = n;
    std::memcpy(out, &d, n);
    return WF_OK;
}

int32_t wf_power_requests_active(uint32_t* count) {
    if (!count) return WF_E_INVALID_ARG;
    *count = wf::PowerRequest::active_count();
    return WF_OK;
}

int32_t wf_fake_push(void* session, uint32_t source, const float* samples, uint32_t frames,
                     uint32_t rate, int64_t qpc_100ns, uint64_t device_position,
                     uint32_t flags) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->fake_push(source, samples, frames, rate, qpc_100ns,
                                          device_position, flags);
}

int32_t wf_fake_set_now(void* session, int64_t qpc_100ns) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->fake_set_now(qpc_100ns);
}

int32_t wf_fake_tick(void* session) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->fake_tick();
}

int32_t wf_fake_signal(void* session, uint32_t signal, uint32_t source, int32_t hresult) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->fake_signal(signal, source, hresult);
}

int32_t wf_fake_query(void* session, uint32_t what, wchar_t* text, uint32_t text_chars,
                      uint32_t* value) {
    if (!session) return WF_E_INVALID_ARG;
    return as_session(session)->fake_query(what, text, text_chars, value);
}

}  // extern "C"
