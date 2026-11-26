// MMDeviceEnumerator wrapper. PROPERTYKEYs are declared locally rather than taken from
// propkey.h so the build does not depend on which PKEY symbols llvm-mingw's import
// libraries happen to carry.
#include "wasapi/enumerator.h"
#include "wasapi/format_convert.h"

#include <audioclient.h>
#include <propsys.h>

namespace wf {
namespace {

const PROPERTYKEY kPkeyFriendlyName = {
    {0xa45c254e, 0xdf1c, 0x4efd, {0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0}}, 14};
const PROPERTYKEY kPkeyRawProcessingSupported = {
    {0x8943b373, 0x388c, 0x4395, {0xb5, 0x57, 0xbc, 0x6d, 0xba, 0xff, 0xaf, 0xdb}}, 2};

HRESULT make_enumerator(ComPtr<IMMDeviceEnumerator>* out) {
    return CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                            __uuidof(IMMDeviceEnumerator), out->put_void());
}

} // namespace

const char* device_state_name(DWORD state) {
    switch (state) {
        case DEVICE_STATE_ACTIVE: return "active";
        case DEVICE_STATE_DISABLED: return "disabled";
        case DEVICE_STATE_NOTPRESENT: return "not-present";
        case DEVICE_STATE_UNPLUGGED: return "unplugged";
        default: return "?";
    }
}

std::wstring endpoint_id(IMMDevice* dev) {
    CoTaskMem<WCHAR> id;
    if (!dev || FAILED(dev->GetId(id.put()))) return {};
    return std::wstring(id.get());
}

std::string endpoint_name(IMMDevice* dev) {
    if (!dev) return {};
    ComPtr<IPropertyStore> store;
    if (FAILED(dev->OpenPropertyStore(STGM_READ, store.put()))) return {};
    PropVar pv;
    if (FAILED(store->GetValue(kPkeyFriendlyName, &pv.v))) return {};
    if (pv.v.vt != VT_LPWSTR || !pv.v.pwszVal) return {};
    return narrow(pv.v.pwszVal);
}

HRESULT default_endpoint_id(EDataFlow flow, ERole role, std::wstring* out) {
    ComPtr<IMMDeviceEnumerator> en;
    HRESULT hr = make_enumerator(&en);
    if (FAILED(hr)) return hr;
    ComPtr<IMMDevice> dev;
    hr = en->GetDefaultAudioEndpoint(flow, role, dev.put());
    if (FAILED(hr)) return hr;
    *out = endpoint_id(dev.get());
    return S_OK;
}

HRESULT open_default_endpoint(EDataFlow flow, ERole role, ComPtr<IMMDevice>* out) {
    ComPtr<IMMDeviceEnumerator> en;
    HRESULT hr = make_enumerator(&en);
    if (FAILED(hr)) return hr;
    return en->GetDefaultAudioEndpoint(flow, role, out->put());
}

HRESULT open_endpoint(const wchar_t* id, ComPtr<IMMDevice>* out) {
    if (!id || !*id) return open_default_endpoint(eCapture, eConsole, out);
    ComPtr<IMMDeviceEnumerator> en;
    HRESULT hr = make_enumerator(&en);
    if (FAILED(hr)) return hr;
    return en->GetDevice(id, out->put());
}

HRESULT list_endpoints(EDataFlow flow, DWORD state_mask, std::vector<EndpointInfo>* out) {
    out->clear();
    ComPtr<IMMDeviceEnumerator> en;
    HRESULT hr = make_enumerator(&en);
    if (FAILED(hr)) return hr;

    std::wstring def_render_console, def_render_comm, def_render_media;
    std::wstring def_capture_console, def_capture_comm, def_capture_media;
    auto def_id = [&](EDataFlow f, ERole r, std::wstring* s) {
        ComPtr<IMMDevice> d;
        if (SUCCEEDED(en->GetDefaultAudioEndpoint(f, r, d.put()))) *s = endpoint_id(d.get());
    };
    def_id(eRender, eConsole, &def_render_console);
    def_id(eRender, eCommunications, &def_render_comm);
    def_id(eRender, eMultimedia, &def_render_media);
    def_id(eCapture, eConsole, &def_capture_console);
    def_id(eCapture, eCommunications, &def_capture_comm);
    def_id(eCapture, eMultimedia, &def_capture_media);

    ComPtr<IMMDeviceCollection> coll;
    hr = en->EnumAudioEndpoints(flow, state_mask, coll.put());
    if (FAILED(hr)) return hr;
    UINT count = 0;
    hr = coll->GetCount(&count);
    if (FAILED(hr)) return hr;

    for (UINT i = 0; i < count; ++i) {
        ComPtr<IMMDevice> dev;
        if (FAILED(coll->Item(i, dev.put()))) continue;

        EndpointInfo info;
        info.id = endpoint_id(dev.get());
        info.name = endpoint_name(dev.get());
        dev->GetState(&info.state);

        ComPtr<IMMEndpoint> ep;
        EDataFlow this_flow = flow;
        if (SUCCEEDED(dev->QueryInterface(__uuidof(IMMEndpoint), ep.put_void()))) {
            ep->GetDataFlow(&this_flow);
        }
        info.is_render = (this_flow == eRender);

        if (info.is_render) {
            info.is_default_console = (info.id == def_render_console);
            info.is_default_communications = (info.id == def_render_comm);
            info.is_default_multimedia = (info.id == def_render_media);
        } else {
            info.is_default_console = (info.id == def_capture_console);
            info.is_default_communications = (info.id == def_capture_comm);
            info.is_default_multimedia = (info.id == def_capture_media);
        }

        ComPtr<IPropertyStore> store;
        if (SUCCEEDED(dev->OpenPropertyStore(STGM_READ, store.put()))) {
            PropVar pv;
            info.raw_prop_hr = store->GetValue(kPkeyRawProcessingSupported, &pv.v);
            if (SUCCEEDED(info.raw_prop_hr)) {
                if (pv.v.vt == VT_BOOL) info.raw_supported = (pv.v.boolVal != VARIANT_FALSE) ? 1 : 0;
                else if (pv.v.vt == VT_UI4) info.raw_supported = pv.v.ulVal ? 1 : 0;
                else if (pv.v.vt == VT_EMPTY) info.raw_supported = -1;
            }
        }

        if (info.state == DEVICE_STATE_ACTIVE) {
            ComPtr<IAudioClient> client;
            info.mix_hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void());
            if (SUCCEEDED(info.mix_hr)) {
                CoTaskMem<WAVEFORMATEX> mix;
                info.mix_hr = client->GetMixFormat(mix.put());
                if (SUCCEEDED(info.mix_hr) && mix.get()) {
                    info.mix_format = format_summary(mix.get());
                    info.mix_rate = mix.get()->nSamplesPerSec;
                    info.mix_channels = mix.get()->nChannels;
                }
            }
        }
        out->push_back(std::move(info));
    }
    return S_OK;
}

} // namespace wf
