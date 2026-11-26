// DeviceLoopbackStream: same thread model as the mic, only the stream flag and the
// endpoint flow differ. RAW is never requested here (it is a render endpoint property
// that does not apply to the loopback tap).
#include "wasapi/device_loopback.h"
#include "wasapi/enumerator.h"

namespace wf {

HRESULT DeviceLoopbackStream::start(const wchar_t* endpoint_id_in,
                                    const DeviceLoopbackOptions& opt, IPacketSink* sink) {
    ComPtr<IMMDevice> dev;
    HRESULT hr = endpoint_id_in && *endpoint_id_in ? open_endpoint(endpoint_id_in, &dev)
                                                   : open_default_endpoint(eRender, eConsole, &dev);
    if (FAILED(hr)) { diag_.init_hr = hr; return hr; }

    device_id_ = endpoint_id(dev.get());
    device_name_ = endpoint_name(dev.get());

    OpenSpec spec;
    spec.extra_flags = AUDCLNT_STREAMFLAGS_LOOPBACK;
    spec.prefer_event = opt.prefer_event;
    spec.prefer_raw = false;
    spec.category = AudioCategory_Other;
    spec.buffer_ms = opt.buffer_ms;

    OpenedCapture opened;
    hr = open_capture_on_device(dev.get(), spec, &opened);
    diag_ = opened.diag;
    if (FAILED(hr)) { opened.close(); return hr; }

    hr = runner_.start(std::move(opened), StreamKind::DeviceLoopback, sink);
    if (FAILED(hr)) diag_.init_hr = hr;
    return hr;
}

} // namespace wf
