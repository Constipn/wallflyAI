// Audio session inventory per endpoint (IAudioSessionManager2): which process plays or
// records on which device, its parent process, session state and per-session peak meter.
// This is the Windows stand-in for "which app currently produces the meeting audio".
#pragma once
#include <audiopolicy.h>
#include <mmdeviceapi.h>
#include <stdint.h>
#include <string>
#include <vector>

namespace wf {

struct SessionInfo {
    std::wstring endpoint_id;
    std::string endpoint_name;
    bool endpoint_is_render = false;
    DWORD pid = 0;
    DWORD parent_pid = 0;
    std::string process_name;        // base name, e.g. "ms-teams.exe"
    std::string process_path;        // full path when readable
    std::string parent_name;
    std::string display_name;        // IAudioSessionControl::GetDisplayName (often empty)
    int state = 0;                   // AudioSessionState
    bool is_system_sounds = false;
    float peak = 0.0f;               // -1 when IAudioMeterInformation was unavailable
    HRESULT peak_hr = S_OK;
};

const char* session_state_name(int state);

// flow: eRender, eCapture or eAll. Enumerates every ACTIVE endpoint of that flow.
HRESULT list_sessions(EDataFlow flow, std::vector<SessionInfo>* out);

// Endpoint-wide peak meter (IAudioMeterInformation on the IMMDevice itself).
// endpoint_id == nullptr -> default console render endpoint.
HRESULT endpoint_peak(const wchar_t* endpoint_id, float* peak);

// Process helpers (also used by the probe's reports).
std::string process_name_for_pid(DWORD pid, std::string* full_path);
DWORD parent_pid_for_pid(DWORD pid);

} // namespace wf
