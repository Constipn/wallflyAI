// KeepAliveRender implementation. The stream is marked AUDCLNT_BUFFERFLAGS_SILENT so the
// audio engine knows the data is zeros; it still counts as an active render stream, which
// is what keeps a loopback capture alive.
#include "wasapi/keepalive_render.h"
#include "wasapi/clock.h"
#include "wasapi/enumerator.h"

#include <avrt.h>

namespace wf {

HRESULT KeepAliveRender::start(const wchar_t* endpoint_id_in, uint32_t buffer_ms) {
    if (thread_) return E_UNEXPECTED;

    ComPtr<IMMDevice> dev;
    HRESULT hr = endpoint_id_in && *endpoint_id_in ? open_endpoint(endpoint_id_in, &dev)
                                                   : open_default_endpoint(eRender, eConsole, &dev);
    if (FAILED(hr)) return init_hr_ = hr;
    device_name_ = endpoint_name(dev.get());
    device_id_ = endpoint_id(dev.get());
    exited_.store(false, std::memory_order_relaxed);
    exit_hr_.store(0, std::memory_order_relaxed);

    hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client_.put_void());
    if (FAILED(hr)) return init_hr_ = hr;

    CoTaskMem<WAVEFORMATEX> mix;
    hr = client_->GetMixFormat(mix.put());
    if (FAILED(hr)) return init_hr_ = hr;
    format_text_ = format_summary(mix.get());
    block_align_ = mix.get()->nBlockAlign;

    const REFERENCE_TIME dur = static_cast<REFERENCE_TIME>(buffer_ms) * 10000;
    hr = client_->Initialize(AUDCLNT_SHAREMODE_SHARED, AUDCLNT_STREAMFLAGS_EVENTCALLBACK, dur, 0,
                             mix.get(), nullptr);
    if (SUCCEEDED(hr)) {
        event_ = CreateEventW(nullptr, FALSE, FALSE, nullptr);
        hr = event_ ? client_->SetEventHandle(event_) : HRESULT_FROM_WIN32(GetLastError());
    }
    if (FAILED(hr)) {
        // Retry polled: Initialize may only be called once, so re-activate first.
        if (event_) { CloseHandle(event_); event_ = nullptr; }
        client_.reset();
        hr = dev->Activate(__uuidof(IAudioClient), CLSCTX_ALL, nullptr, client_.put_void());
        if (SUCCEEDED(hr)) hr = client_->Initialize(AUDCLNT_SHAREMODE_SHARED, 0, dur, 0, mix.get(), nullptr);
        if (FAILED(hr)) return init_hr_ = hr;
    }

    hr = client_->GetBufferSize(&buffer_frames_);
    if (FAILED(hr)) return init_hr_ = hr;
    hr = client_->GetService(__uuidof(IAudioRenderClient), render_.put_void());
    if (FAILED(hr)) return init_hr_ = hr;
    client_->GetService(__uuidof(IAudioClock), clock_.put_void()); // optional

    stop_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    if (!stop_) return init_hr_ = HRESULT_FROM_WIN32(GetLastError());
    thread_ = CreateThread(nullptr, 0, &KeepAliveRender::thread_main, this, 0, nullptr);
    if (!thread_) {
        init_hr_ = HRESULT_FROM_WIN32(GetLastError());
        CloseHandle(stop_);
        stop_ = nullptr;
        return init_hr_;
    }
    return init_hr_ = S_OK;
}

void KeepAliveRender::stop() {
    if (thread_) {
        SetEvent(stop_);
        WaitForSingleObject(thread_, 3000);
        CloseHandle(thread_);
        thread_ = nullptr;
        CloseHandle(stop_);
        stop_ = nullptr;
    }
    clock_.reset();
    render_.reset();
    client_.reset();
    if (event_) { CloseHandle(event_); event_ = nullptr; }
}

HRESULT KeepAliveRender::clock_position(uint64_t* frames, uint64_t* freq, int64_t* qpc_100ns) {
    if (!clock_) return E_NOINTERFACE;
    UINT64 pos = 0, f = 0, qpc = 0;
    HRESULT hr = clock_->GetFrequency(&f);
    if (FAILED(hr)) return hr;
    hr = clock_->GetPosition(&pos, &qpc);
    if (FAILED(hr)) return hr;
    if (frames) *frames = pos;
    if (freq) *freq = f;
    if (qpc_100ns) *qpc_100ns = static_cast<int64_t>(qpc ? qpc : static_cast<UINT64>(qpc_now_100ns()));
    return S_OK;
}

DWORD WINAPI KeepAliveRender::thread_main(void* self) {
    static_cast<KeepAliveRender*>(self)->run();
    return 0;
}

void KeepAliveRender::run() {
    MtaScope mta;
    DWORD task_index = 0;
    HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task_index);

    // Pre-fill one buffer of silence, then start.
    BYTE* data = nullptr;
    if (SUCCEEDED(render_->GetBuffer(buffer_frames_, &data))) {
        render_->ReleaseBuffer(buffer_frames_, AUDCLNT_BUFFERFLAGS_SILENT);
    }
    HRESULT hr = client_->Start();
    if (FAILED(hr)) {
        exit_with(hr);   // reported through exited(), not swallowed
        if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
        return;
    }

    for (;;) {
        HANDLE waits[2];
        DWORD n = 0;
        waits[n++] = stop_;
        if (event_) waits[n++] = event_;
        if (WaitForMultipleObjects(n, waits, FALSE, 10) == WAIT_OBJECT_0) break;

        UINT32 padding = 0;
        hr = client_->GetCurrentPadding(&padding);
        if (FAILED(hr)) {
            exit_with(hr);
            break;
        }
        const UINT32 avail = buffer_frames_ > padding ? buffer_frames_ - padding : 0;
        if (avail == 0) continue;
        hr = render_->GetBuffer(avail, &data);
        if (FAILED(hr)) {
            exit_with(hr);
            break;
        }
        render_->ReleaseBuffer(avail, AUDCLNT_BUFFERFLAGS_SILENT);
    }

    client_->Stop();
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
}

} // namespace wf
