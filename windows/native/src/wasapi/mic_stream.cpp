// MicStream: resolve the endpoint, run the open ladder, hand the client to CaptureRunner.
// Whether RAW was accepted is kept in the diagnostics so measurements can report it (M3).
#include "wasapi/mic_stream.h"
#include "wasapi/enumerator.h"

namespace wf {

HRESULT MicStream::start(const wchar_t* endpoint_id_in, const MicOptions& opt, IPacketSink* sink) {
    ComPtr<IMMDevice> dev;
    HRESULT hr = endpoint_id_in && *endpoint_id_in ? open_endpoint(endpoint_id_in, &dev)
                                                   : open_default_endpoint(eCapture, eConsole, &dev);
    if (FAILED(hr)) { diag_.init_hr = hr; return hr; }

    device_id_ = endpoint_id(dev.get());
    device_name_ = endpoint_name(dev.get());

    OpenSpec spec;
    spec.extra_flags = 0;
    spec.prefer_event = opt.prefer_event;
    spec.prefer_raw = opt.prefer_raw;
    spec.category = opt.use_communications_category ? AudioCategory_Communications : AudioCategory_Other;
    spec.buffer_ms = opt.buffer_ms;

    OpenedCapture opened;
    hr = open_capture_on_device(dev.get(), spec, &opened);
    diag_ = opened.diag;
    if (FAILED(hr)) { opened.close(); return hr; }

    hr = runner_.start(std::move(opened), StreamKind::Mic, sink);
    if (FAILED(hr)) diag_.init_hr = hr;
    return hr;
}

} // namespace wf
