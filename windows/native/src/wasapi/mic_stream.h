// Shared-mode microphone capture on one endpoint. Opens with AUDCLNT_STREAMOPTIONS_RAW
// when the endpoint allows it (spec 3.1: driver echo/noise filters must stay out of the
// signal) and never with AudioCategory_Communications unless explicitly asked for.
#pragma once
#include "wasapi/capture_loop.h"

namespace wf {

struct MicOptions {
    bool prefer_raw = true;
    bool use_communications_category = false; // only for the ducking measurement (M4)
    bool prefer_event = true;
    uint32_t buffer_ms = 20;
};

class MicStream {
public:
    // endpoint_id == nullptr -> default console capture endpoint.
    HRESULT start(const wchar_t* endpoint_id, const MicOptions& opt, IPacketSink* sink);
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
