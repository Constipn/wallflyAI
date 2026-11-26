// Endpoint enumeration: ids, friendly names, mix format, default-console/communications
// roles, device state and PKEY_Devices_AudioDevice_RawProcessingSupported where readable.
// The endpoint id is the Windows counterpart of the macOS device UID.
#pragma once
#include <mmdeviceapi.h>
#include <stdint.h>
#include <string>
#include <vector>

#include "wasapi/com_util.h"

namespace wf {

struct EndpointInfo {
    std::wstring id;
    std::string name;             // friendly name, UTF-8
    std::string mix_format;       // human readable, "" if GetMixFormat failed
    HRESULT mix_hr = S_OK;
    uint32_t mix_rate = 0;
    uint16_t mix_channels = 0;
    DWORD state = 0;              // DEVICE_STATE_*
    bool is_default_console = false;
    bool is_default_communications = false;
    bool is_default_multimedia = false;
    int raw_supported = -1;       // -1 unknown / property absent, 0 no, 1 yes
    HRESULT raw_prop_hr = S_OK;
    bool is_render = false;
};

const char* device_state_name(DWORD state);

// flow: eRender / eCapture / eAll. state_mask e.g. DEVICE_STATEMASK_ALL.
HRESULT list_endpoints(EDataFlow flow, DWORD state_mask, std::vector<EndpointInfo>* out);

HRESULT default_endpoint_id(EDataFlow flow, ERole role, std::wstring* out);
HRESULT open_endpoint(const wchar_t* id, ComPtr<IMMDevice>* out);      // nullptr id -> default console
HRESULT open_default_endpoint(EDataFlow flow, ERole role, ComPtr<IMMDevice>* out);
std::string endpoint_name(IMMDevice* dev);
std::wstring endpoint_id(IMMDevice* dev);

} // namespace wf
