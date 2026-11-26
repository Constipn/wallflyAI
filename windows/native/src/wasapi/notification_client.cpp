// NotificationClient implementation. The object is stack- or member-owned by the caller,
// so Release() never deletes; the extra reference the enumerator holds is dropped in
// unregister(), which the owner must call before destruction.
#include "wasapi/notification_client.h"
#include "wasapi/clock.h"

#include <cstdio>

namespace wf {

// Declared locally (as in enumerator.cpp) so the build does not depend on which PKEY
// symbols the toolchain's import libraries carry. Values from mmdeviceapi.h.
extern const PROPERTYKEY kPkeyAudioEngineDeviceFormat = {
    {0xf19f064d, 0x082c, 0x4e27, {0xbc, 0x73, 0x68, 0x82, 0xa1, 0xbb, 0x8e, 0x4c}}, 0};

namespace {
const PROPERTYKEY kPkeyAudioEngineOemFormat = {
    {0xe4870e26, 0x3cc5, 0x4cd2, {0xba, 0x46, 0xca, 0x0a, 0x9a, 0x70, 0xed, 0x04}}, 3};
const GUID kFmtidAudioEndpoint = {
    0x1da5d803, 0xd492, 0x4edd, {0x8c, 0x23, 0xe0, 0xc0, 0xff, 0xee, 0x7f, 0x0e}};
const GUID kFmtidDeviceName = {
    0xa45c254e, 0xdf1c, 0x4efd, {0x80, 0x20, 0x67, 0xd1, 0x46, 0xa8, 0x50, 0xe0}};

bool same_key(const PROPERTYKEY& a, const PROPERTYKEY& b) {
    return a.pid == b.pid && IsEqualGUID(a.fmtid, b.fmtid);
}
}  // namespace

bool is_format_property(const PROPERTYKEY& key) {
    return same_key(key, kPkeyAudioEngineDeviceFormat) || same_key(key, kPkeyAudioEngineOemFormat);
}

std::string property_key_text(const PROPERTYKEY& key) {
    const GUID& g = key.fmtid;
    char buf[96];
    std::snprintf(buf, sizeof(buf), "{%08lx-%04x-%04x-%02x%02x-%02x%02x%02x%02x%02x%02x},%lu",
                  static_cast<unsigned long>(g.Data1), g.Data2, g.Data3, g.Data4[0], g.Data4[1],
                  g.Data4[2], g.Data4[3], g.Data4[4], g.Data4[5], g.Data4[6], g.Data4[7],
                  static_cast<unsigned long>(key.pid));
    std::string s = buf;
    if (same_key(key, kPkeyAudioEngineDeviceFormat)) s += " AudioEngine_DeviceFormat";
    else if (same_key(key, kPkeyAudioEngineOemFormat)) s += " AudioEngine_OEMFormat";
    else if (IsEqualGUID(g, kFmtidAudioEndpoint)) s += " AudioEndpoint_*";
    else if (IsEqualGUID(g, kFmtidDeviceName)) s += " Device_*Name";
    return s;
}

const char* notification_kind_name(DeviceNotification::Kind k) {
    switch (k) {
        case DeviceNotification::DefaultChanged: return "default-changed";
        case DeviceNotification::StateChanged: return "state-changed";
        case DeviceNotification::Added: return "added";
        case DeviceNotification::Removed: return "removed";
        case DeviceNotification::PropertyChanged: return "property-changed";
    }
    return "?";
}

const char* data_flow_name(EDataFlow f) {
    switch (f) {
        case eRender: return "render";
        case eCapture: return "capture";
        case eAll: return "all";
        default: return "?";
    }
}

const char* role_name(ERole r) {
    switch (r) {
        case eConsole: return "console";
        case eMultimedia: return "multimedia";
        case eCommunications: return "communications";
        default: return "?";
    }
}

HRESULT NotificationClient::register_with_system(DeviceNotificationFn fn, void* ctx) {
    fn_ = fn;
    ctx_ = ctx;
    HRESULT hr = CoCreateInstance(__uuidof(MMDeviceEnumerator), nullptr, CLSCTX_ALL,
                                  __uuidof(IMMDeviceEnumerator), enumerator_.put_void());
    if (FAILED(hr)) return hr;
    return enumerator_->RegisterEndpointNotificationCallback(this);
}

void NotificationClient::unregister() {
    if (enumerator_) {
        enumerator_->UnregisterEndpointNotificationCallback(this);
        enumerator_.reset();
    }
    fn_ = nullptr;
}

HRESULT STDMETHODCALLTYPE NotificationClient::QueryInterface(REFIID riid, void** ppv) {
    if (!ppv) return E_POINTER;
    if (IsEqualIID(riid, __uuidof(IUnknown)) || IsEqualIID(riid, __uuidof(IMMNotificationClient))) {
        *ppv = static_cast<IMMNotificationClient*>(this);
        AddRef();
        return S_OK;
    }
    *ppv = nullptr;
    return E_NOINTERFACE;
}

ULONG STDMETHODCALLTYPE NotificationClient::AddRef() { return InterlockedIncrement(&ref_); }
ULONG STDMETHODCALLTYPE NotificationClient::Release() {
    LONG r = InterlockedDecrement(&ref_);
    return static_cast<ULONG>(r < 0 ? 0 : r);
}

void NotificationClient::post(DeviceNotification n) {
    n.qpc_100ns = qpc_now_100ns();
    DeviceNotificationFn fn = fn_;
    if (fn) fn(ctx_, n);
}

HRESULT STDMETHODCALLTYPE NotificationClient::OnDeviceStateChanged(LPCWSTR id, DWORD state) {
    DeviceNotification n{};
    n.kind = DeviceNotification::StateChanged;
    n.state = state;
    if (id) n.device_id = id;
    post(std::move(n));
    return S_OK;
}

HRESULT STDMETHODCALLTYPE NotificationClient::OnDeviceAdded(LPCWSTR id) {
    DeviceNotification n{};
    n.kind = DeviceNotification::Added;
    if (id) n.device_id = id;
    post(std::move(n));
    return S_OK;
}

HRESULT STDMETHODCALLTYPE NotificationClient::OnDeviceRemoved(LPCWSTR id) {
    DeviceNotification n{};
    n.kind = DeviceNotification::Removed;
    if (id) n.device_id = id;
    post(std::move(n));
    return S_OK;
}

HRESULT STDMETHODCALLTYPE NotificationClient::OnDefaultDeviceChanged(EDataFlow flow, ERole role,
                                                                     LPCWSTR id) {
    DeviceNotification n{};
    n.kind = DeviceNotification::DefaultChanged;
    n.flow = flow;
    n.role = role;
    if (id) n.device_id = id;
    post(std::move(n));
    return S_OK;
}

HRESULT STDMETHODCALLTYPE NotificationClient::OnPropertyValueChanged(LPCWSTR id,
                                                                     const PROPERTYKEY key) {
    DeviceNotification n{};
    n.kind = DeviceNotification::PropertyChanged;
    n.key = key;
    if (id) n.device_id = id;
    post(std::move(n));
    return S_OK;
}

} // namespace wf
