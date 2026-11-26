// IMMNotificationClient wrapper. The callbacks run on an MMDevice worker thread, so they
// only copy the notification into the caller's handler -- no COM calls, no device work.
// The supervisor turns these into debounced device-change messages later.
//
// OnPropertyValueChanged fires for every endpoint and every property key - several times
// a second on the render endpoint while any stream plays (measured 2026-11-01, README
// "Mic format change") - so the key travels along and consumers filter on it
// (is_format_property) and on the endpoint id.
#pragma once
#include <mmdeviceapi.h>
#include <stdint.h>
#include <string>

#include "wasapi/com_util.h"

namespace wf {

struct DeviceNotification {
    enum Kind { DefaultChanged, StateChanged, Added, Removed, PropertyChanged } kind;
    EDataFlow flow = eRender;   // valid for DefaultChanged
    ERole role = eConsole;      // valid for DefaultChanged
    DWORD state = 0;            // valid for StateChanged
    PROPERTYKEY key{};          // valid for PropertyChanged
    std::wstring device_id;
    int64_t qpc_100ns = 0;
};

using DeviceNotificationFn = void (*)(void* ctx, const DeviceNotification&);

class NotificationClient final : public IMMNotificationClient {
public:
    HRESULT register_with_system(DeviceNotificationFn fn, void* ctx);
    void unregister();

    HRESULT STDMETHODCALLTYPE QueryInterface(REFIID riid, void** ppv) override;
    ULONG STDMETHODCALLTYPE AddRef() override;
    ULONG STDMETHODCALLTYPE Release() override;
    HRESULT STDMETHODCALLTYPE OnDeviceStateChanged(LPCWSTR id, DWORD state) override;
    HRESULT STDMETHODCALLTYPE OnDeviceAdded(LPCWSTR id) override;
    HRESULT STDMETHODCALLTYPE OnDeviceRemoved(LPCWSTR id) override;
    HRESULT STDMETHODCALLTYPE OnDefaultDeviceChanged(EDataFlow flow, ERole role, LPCWSTR id) override;
    HRESULT STDMETHODCALLTYPE OnPropertyValueChanged(LPCWSTR id, const PROPERTYKEY key) override;

private:
    void post(DeviceNotification n);
    LONG ref_ = 1;
    DeviceNotificationFn fn_ = nullptr;
    void* ctx_ = nullptr;
    ComPtr<IMMDeviceEnumerator> enumerator_;
};

const char* notification_kind_name(DeviceNotification::Kind k);
// PKEY_AudioEngine_DeviceFormat (mmdeviceapi.h), defined locally like the keys of
// enumerator.cpp.
extern const PROPERTYKEY kPkeyAudioEngineDeviceFormat;
// PKEY_AudioEngine_DeviceFormat or PKEY_AudioEngine_OEMFormat: the only two property keys
// whose change can mean that the shared-mode format of an endpoint changed.
bool is_format_property(const PROPERTYKEY& key);
// "{fmtid},pid" plus the name of the few keys we know, for probe reports.
std::string property_key_text(const PROPERTYKEY& key);
const char* data_flow_name(EDataFlow f);
const char* role_name(ERole r);

} // namespace wf
