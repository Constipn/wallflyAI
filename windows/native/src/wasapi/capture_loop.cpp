// Open ladder + capture thread. The ladder exists because RAW, the Communications
// category and event callbacks are each rejected by some endpoints (and always by the
// process-loopback virtual device); every rejection is recorded, never hidden.
#include "wasapi/capture_loop.h"
#include "wasapi/clock.h"

#include <avrt.h>
#include <cstdio>
#include <cstring>

namespace wf {

const char* stream_kind_name(StreamKind k) {
    switch (k) {
        case StreamKind::Mic: return "mic";
        case StreamKind::DeviceLoopback: return "device-loopback";
        case StreamKind::ProcessLoopbackExclude: return "process-exclude";
        case StreamKind::ProcessLoopbackInclude: return "process-include";
        case StreamKind::KeepAliveRender: return "keepalive-render";
    }
    return "?";
}

void OpenedCapture::close() {
    capture.reset();
    client.reset();
    if (event) { CloseHandle(event); event = nullptr; }
}

namespace {

// Applies the stream category and the RAW option before GetMixFormat. Order matters:
// with RAW the endpoint reports the format BELOW the processing chain (measured: the
// Intel SST mic array mixes 4 ch, its RAW stream is 2 ch), so the format a stream gets
// depends on these properties. Returns the SetClientProperties result when RAW was asked
// for (the caller then retries without it), S_OK otherwise.
HRESULT apply_client_properties(IAudioClient* client, AUDIO_STREAM_CATEGORY category,
                                bool with_raw) {
    ComPtr<IAudioClient2> client2;
    if (SUCCEEDED(client->QueryInterface(__uuidof(IAudioClient2), client2.put_void()))) {
        AudioClientProperties props{};
        props.cbSize = sizeof(props);
        props.bIsOffload = FALSE;
        props.eCategory = category;
        props.Options = with_raw ? AUDCLNT_STREAMOPTIONS_RAW : AUDCLNT_STREAMOPTIONS_NONE;
        const HRESULT phr = client2->SetClientProperties(&props);
        return with_raw ? phr : S_OK;
    }
    return with_raw ? E_NOINTERFACE : S_OK;
}

// One attempt of the ladder. Each attempt re-activates the client: an IAudioClient that
// failed Initialize must not be reused.
HRESULT try_open(IMMDevice* dev, const OpenSpec& spec, bool with_raw, bool with_event,
                 OpenedCapture* out, HRESULT* raw_hr_out, bool* raw_ok_out) {
    ComPtr<IAudioClient> client;
    HRESULT hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void());
    if (FAILED(hr)) return hr;

    *raw_hr_out = S_OK;
    *raw_ok_out = false;
    const HRESULT phr = apply_client_properties(client.get(), spec.category, with_raw);
    if (with_raw) {
        *raw_hr_out = phr;
        *raw_ok_out = SUCCEEDED(phr);
        if (FAILED(phr)) return phr;   // caller retries without RAW
    }

    CoTaskMem<WAVEFORMATEX> mix;
    hr = client->GetMixFormat(mix.put());
    if (FAILED(hr)) return hr;

    PcmFormat fmt{};
    if (!describe_format(mix.get(), &fmt)) return AUDCLNT_E_UNSUPPORTED_FORMAT;

    DWORD flags = spec.extra_flags;
    if (with_event) flags |= AUDCLNT_STREAMFLAGS_EVENTCALLBACK;
    const REFERENCE_TIME dur = static_cast<REFERENCE_TIME>(spec.buffer_ms) * 10000;
    hr = client->Initialize(AUDCLNT_SHAREMODE_SHARED, flags, dur, 0, mix.get(), nullptr);
    if (FAILED(hr)) return hr;

    HANDLE ev = nullptr;
    if (with_event) {
        ev = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        if (!ev) return HRESULT_FROM_WIN32(GetLastError());
        hr = client->SetEventHandle(ev);
        if (FAILED(hr)) { CloseHandle(ev); return hr; }
    }

    UINT32 buffer_frames = 0;
    hr = client->GetBufferSize(&buffer_frames);
    if (FAILED(hr)) { if (ev) CloseHandle(ev); return hr; }

    ComPtr<IAudioCaptureClient> cap;
    hr = client->GetService(__uuidof(IAudioCaptureClient), cap.put_void());
    if (FAILED(hr)) { if (ev) CloseHandle(ev); return hr; }

    out->close();
    out->client = client;
    out->capture = cap;
    out->event = ev;
    out->fmt = fmt;
    out->buffer_frames = buffer_frames;
    out->diag.event_driven = with_event;
    out->diag.sample_rate = fmt.rate;
    out->diag.channels = fmt.channels;
    out->diag.sample_format = sample_fmt_name(fmt.fmt);
    out->diag.format = fmt;
    out->diag.buffer_frames = buffer_frames;
    out->diag.stream_flags = flags;
    out->diag.format_text = format_summary(mix.get());
    return S_OK;
}

} // namespace

HRESULT query_capture_format(IMMDevice* dev, AUDIO_STREAM_CATEGORY category, bool raw,
                             PcmFormat* out) {
    if (!dev || !out) return E_POINTER;
    ComPtr<IAudioClient> client;
    HRESULT hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client.put_void());
    if (FAILED(hr)) return hr;
    hr = apply_client_properties(client.get(), category, raw);
    if (FAILED(hr)) return hr;
    CoTaskMem<WAVEFORMATEX> mix;
    hr = client->GetMixFormat(mix.put());
    if (FAILED(hr)) return hr;
    return describe_format(mix.get(), out) ? S_OK : AUDCLNT_E_UNSUPPORTED_FORMAT;
}

HRESULT open_capture_on_device(IMMDevice* dev, const OpenSpec& spec, OpenedCapture* out) {
    if (!dev || !out) return E_POINTER;
    out->diag = StreamDiagnostics{};
    out->diag.raw_requested = spec.prefer_raw;

    struct Attempt { bool raw; bool ev; };
    Attempt ladder[4];
    int n = 0;
    if (spec.prefer_raw) {
        ladder[n++] = {true, spec.prefer_event};
        if (spec.prefer_event) ladder[n++] = {true, false};
    }
    ladder[n++] = {false, spec.prefer_event};
    if (spec.prefer_event) ladder[n++] = {false, false};

    HRESULT last = E_FAIL;
    char note[256];
    for (int i = 0; i < n; ++i) {
        HRESULT raw_hr = S_OK;
        bool raw_ok = false;
        HRESULT hr = try_open(dev, spec, ladder[i].raw, ladder[i].ev, out, &raw_hr, &raw_ok);
        if (ladder[i].raw) {
            out->diag.raw_hr = raw_hr;
            out->diag.raw_accepted = raw_ok && SUCCEEDED(hr);
        }
        if (SUCCEEDED(hr)) {
            out->diag.opened = true;
            out->diag.init_hr = S_OK;
            out->diag.init_attempt = i + 1;
            return S_OK;
        }
        last = hr;
        std::snprintf(note, sizeof(note), "attempt %d (raw=%d event=%d) -> %s; ", i + 1,
                      ladder[i].raw ? 1 : 0, ladder[i].ev ? 1 : 0, hr_str(hr).c_str());
        out->diag.notes += note;
    }
    out->diag.init_hr = last;
    return last;
}

CaptureRunner::~CaptureRunner() { stop(); }

HRESULT CaptureRunner::start(OpenedCapture&& opened, StreamKind kind, IPacketSink* sink) {
    if (thread_) return E_UNEXPECTED;
    opened_ = std::move(opened);
    kind_ = kind;
    sink_ = sink;
    overflow_ = 0;

    // Worst case one WASAPI packet is the whole endpoint buffer; one extra second of
    // headroom keeps the capture loop allocation-free even on odd drivers.
    const size_t cap = static_cast<size_t>(opened_.buffer_frames) * 2 +
                       static_cast<size_t>(opened_.fmt.rate ? opened_.fmt.rate : 48000);
    conv_.assign(cap, 0.0f);

    stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop_) return HRESULT_FROM_WIN32(GetLastError());

    thread_ = CreateThread(nullptr, 0, &CaptureRunner::thread_main, this, 0, nullptr);
    if (!thread_) {
        HRESULT hr = HRESULT_FROM_WIN32(GetLastError());
        CloseHandle(stop_);
        stop_ = nullptr;
        return hr;
    }
    return S_OK;
}

void CaptureRunner::stop() {
    if (!thread_) return;
    SetEvent(stop_);
    WaitForSingleObject(thread_, 4000);
    CloseHandle(thread_);
    thread_ = nullptr;
    CloseHandle(stop_);
    stop_ = nullptr;
    opened_.close();
}

DWORD WINAPI CaptureRunner::thread_main(void* self) {
    static_cast<CaptureRunner*>(self)->run();
    return 0;
}

void CaptureRunner::run() {
    MtaScope mta;
    DWORD task_index = 0;
    HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task_index);

    HRESULT hr = opened_.client->Start();
    sink_->on_event(StreamEvent{SUCCEEDED(hr) ? StreamEvent::Started : StreamEvent::Error, hr});
    if (FAILED(hr)) {
        if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
        return;
    }

    bool first = true;
    bool fatal = false;
    const uint32_t capacity = static_cast<uint32_t>(conv_.size());

    for (;;) {
        HANDLE waits[2];
        DWORD n = 0;
        waits[n++] = stop_;
        if (opened_.event) waits[n++] = opened_.event;
        const DWORD r = WaitForMultipleObjects(n, waits, FALSE, 10);
        if (r == WAIT_OBJECT_0) break; // stop requested

        for (;;) {
            UINT32 packet = 0;
            hr = opened_.capture->GetNextPacketSize(&packet);
            if (FAILED(hr)) { fatal = true; break; }
            if (packet == 0) break;

            BYTE* data = nullptr;
            UINT32 frames = 0;
            DWORD flags = 0;
            UINT64 devpos = 0, qpc = 0;
            hr = opened_.capture->GetBuffer(&data, &frames, &flags, &devpos, &qpc);
            if (hr == AUDCLNT_S_BUFFER_EMPTY) break;
            if (FAILED(hr)) { fatal = true; break; }

            uint32_t use = frames;
            if (use > capacity) { use = capacity; ++overflow_; }
            if ((flags & AUDCLNT_BUFFERFLAGS_SILENT) || data == nullptr) {
                std::memset(conv_.data(), 0, sizeof(float) * use);
            } else {
                to_mono_float(opened_.fmt, data, use, conv_.data());
            }

            if (first) {
                first = false;
                sink_->on_event(StreamEvent{StreamEvent::FirstPacket, S_OK});
            }
            WfPacket p{conv_.data(), use, static_cast<int64_t>(qpc), devpos,
                       static_cast<uint32_t>(flags), opened_.fmt.rate};
            sink_->on_packet(p);

            opened_.capture->ReleaseBuffer(frames);
        }
        if (fatal) break;
    }

    if (fatal) {
        sink_->on_event(StreamEvent{hr == AUDCLNT_E_DEVICE_INVALIDATED ? StreamEvent::DeviceInvalidated
                                                                       : StreamEvent::Error,
                                    hr});
    }
    opened_.client->Stop();
    sink_->on_event(StreamEvent{StreamEvent::Stopped, fatal ? hr : S_OK});
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
}

} // namespace wf
