// Classic WASAPI loopback (AUDCLNT_STREAMFLAGS_LOOPBACK) on a render endpoint.
// Hears exactly one output device and delivers nothing while that device is idle --
// the two reasons the spec prefers process loopback; kept as the documented fallback.
#pragma once
#include "wasapi/capture_loop.h"

namespace wf {

struct DeviceLoopbackOptions {
    bool prefer_event = true;
    uint32_t buffer_ms = 20;
};

class DeviceLoopbackStream {
public:
    // endpoint_id == nullptr -> default console render endpoint.
    HRESULT start(const wchar_t* endpoint_id, const DeviceLoopbackOptions& opt, IPacketSink* sink);
    void stop() { runner_.stop(); }
    const StreamDiagnostics& diag() const { return diag_; }
    const std::wstring& device_id() const { return device_id_; }
    const std::string& device_name() const { return device_name_; }

private:
    CaptureRunner runner_;
    StreamDiagnostics diag_;
    std::wstring device_id_;
    std::string device_name_;
};

} // namespace wf
