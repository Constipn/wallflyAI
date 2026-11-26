// Session enumeration. Parent PIDs come from a Toolhelp snapshot (documented and
// admin-free) instead of NtQueryInformationProcess; image names from
// QueryFullProcessImageNameW with PROCESS_QUERY_LIMITED_INFORMATION.
#include "wasapi/sessions.h"
#include "wasapi/com_util.h"
#include "wasapi/enumerator.h"

#include "wasapi/meter_defs.h"

#include <tlhelp32.h>
#include <unordered_map>

namespace wf {
namespace {

struct ProcEntry { DWORD parent; std::string name; };

const std::unordered_map<DWORD, ProcEntry>& proc_snapshot(bool refresh) {
    static std::unordered_map<DWORD, ProcEntry> map;
    if (refresh || map.empty()) {
        map.clear();
        HANDLE snap = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snap != INVALID_HANDLE_VALUE) {
            PROCESSENTRY32W pe{};
            pe.dwSize = sizeof(pe);
            if (Process32FirstW(snap, &pe)) {
                do {
                    map[pe.th32ProcessID] = ProcEntry{pe.th32ParentProcessID, narrow(pe.szExeFile)};
                } while (Process32NextW(snap, &pe));
            }
            CloseHandle(snap);
        }
    }
    return map;
}

} // namespace

const char* session_state_name(int state) {
    switch (state) {
        case AudioSessionStateInactive: return "inactive";
        case AudioSessionStateActive: return "active";
        case AudioSessionStateExpired: return "expired";
        default: return "?";
    }
}

std::string process_name_for_pid(DWORD pid, std::string* full_path) {
    if (full_path) full_path->clear();
    if (pid == 0) return "System Idle";
    HANDLE h = OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, FALSE, pid);
    if (h) {
        WCHAR buf[MAX_PATH * 2];
        DWORD n = static_cast<DWORD>(sizeof(buf) / sizeof(buf[0]));
        if (QueryFullProcessImageNameW(h, 0, buf, &n)) {
            CloseHandle(h);
            std::string full = narrow(buf);
            if (full_path) *full_path = full;
            size_t slash = full.find_last_of("\\/");
            return slash == std::string::npos ? full : full.substr(slash + 1);
        }
        CloseHandle(h);
    }
    const auto& map = proc_snapshot(false);
    auto it = map.find(pid);
    return it != map.end() ? it->second.name : std::string("(unknown)");
}

DWORD parent_pid_for_pid(DWORD pid) {
    const auto& map = proc_snapshot(false);
    auto it = map.find(pid);
    return it != map.end() ? it->second.parent : 0;
}

HRESULT list_sessions(EDataFlow flow, std::vector<SessionInfo>* out) {
    out->clear();
    proc_snapshot(true); // one snapshot per call, reused for every session below

    std::vector<EndpointInfo> endpoints;
    HRESULT hr = list_endpoints(flow, DEVICE_STATE_ACTIVE, &endpoints);
    if (FAILED(hr)) return hr;

    for (const EndpointInfo& ep : endpoints) {
        ComPtr<IMMDevice> dev;
        if (FAILED(open_endpoint(ep.id.c_str(), &dev))) continue;

        ComPtr<IAudioSessionManager2> mgr;
        if (FAILED(dev->Activate(__uuidof(IAudioSessionManager2), CLSCTX_ALL, nullptr,
                                 mgr.put_void()))) {
            continue;
        }
        ComPtr<IAudioSessionEnumerator> sessions;
        if (FAILED(mgr->GetSessionEnumerator(sessions.put()))) continue;

        int count = 0;
        if (FAILED(sessions->GetCount(&count))) continue;
        for (int i = 0; i < count; ++i) {
            ComPtr<IAudioSessionControl> ctl;
            if (FAILED(sessions->GetSession(i, ctl.put()))) continue;
            ComPtr<IAudioSessionControl2> ctl2;
            if (FAILED(ctl->QueryInterface(__uuidof(IAudioSessionControl2), ctl2.put_void()))) continue;

            SessionInfo s;
            s.endpoint_id = ep.id;
            s.endpoint_name = ep.name;
            s.endpoint_is_render = ep.is_render;
            ctl2->GetProcessId(&s.pid);
            s.is_system_sounds = (ctl2->IsSystemSoundsSession() == S_OK);

            AudioSessionState st = AudioSessionStateInactive;
            if (SUCCEEDED(ctl->GetState(&st))) s.state = static_cast<int>(st);

            CoTaskMem<WCHAR> disp;
            if (SUCCEEDED(ctl->GetDisplayName(disp.put())) && disp.get()) s.display_name = narrow(disp.get());

            s.process_name = process_name_for_pid(s.pid, &s.process_path);
            s.parent_pid = parent_pid_for_pid(s.pid);
            if (s.parent_pid) s.parent_name = process_name_for_pid(s.parent_pid, nullptr);

            ComPtr<IAudioMeterInformation> meter;
            s.peak_hr = ctl->QueryInterface(__uuidof(IAudioMeterInformation), meter.put_void());
            if (SUCCEEDED(s.peak_hr)) {
                float peak = 0.0f;
                s.peak_hr = meter->GetPeakValue(&peak);
                s.peak = SUCCEEDED(s.peak_hr) ? peak : -1.0f;
            } else {
                s.peak = -1.0f;
            }
            out->push_back(std::move(s));
        }
    }
    return S_OK;
}

HRESULT endpoint_peak(const wchar_t* endpoint_id, float* peak) {
    if (!peak) return E_POINTER;
    *peak = -1.0f;
    ComPtr<IMMDevice> dev;
    HRESULT hr = endpoint_id && *endpoint_id ? open_endpoint(endpoint_id, &dev)
                                             : open_default_endpoint(eRender, eConsole, &dev);
    if (FAILED(hr)) return hr;
    ComPtr<IAudioMeterInformation> meter;
    hr = dev->Activate(__uuidof(IAudioMeterInformation), CLSCTX_ALL, nullptr, meter.put_void());
    if (FAILED(hr)) return hr;
    return meter->GetPeakValue(peak);
}

} // namespace wf
