// Silent render stream: writes zeros to a render endpoint so classic loopback keeps
// delivering packets while nothing else plays (spec 3.3 keepalive_mode = 1). Its
// IAudioClock position lets a caller tell a stalled engine from genuine silence.
#pragma once
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <stdint.h>
#include <atomic>
#include <string>

#include "wasapi/com_util.h"
#include "wasapi/format_convert.h"

namespace wf {

class KeepAliveRender {
public:
    KeepAliveRender() = default;
    KeepAliveRender(const KeepAliveRender&) = delete;
    KeepAliveRender& operator=(const KeepAliveRender&) = delete;
    ~KeepAliveRender() { stop(); }

    // endpoint_id == nullptr -> default console render endpoint.
    HRESULT start(const wchar_t* endpoint_id, uint32_t buffer_ms = 20);
    void stop();
    bool running() const { return thread_ != nullptr; }

    // Device position in frames and the QPC (100 ns) it was sampled at. A position that
    // stops advancing means the render engine stalled.
    HRESULT clock_position(uint64_t* frames, uint64_t* freq, int64_t* qpc_100ns);

    const std::string& device_name() const { return device_name_; }
    const std::wstring& device_id() const { return device_id_; }
    const std::string& format_text() const { return format_text_; }
    bool event_driven() const { return event_ != nullptr; }
    HRESULT init_hr() const { return init_hr_; }

    // True once the render thread has left its loop on its own (IAudioClient::Start,
    // GetCurrentPadding or GetBuffer failed, e.g. AUDCLNT_E_DEVICE_INVALIDATED) instead of
    // being asked to stop; `hr` receives the failure. Before 2026-11-23 the thread ended
    // silently and the endpoint fell idle without anyone noticing. Any thread may ask.
    bool exited(HRESULT* hr) const {
        if (!exited_.load(std::memory_order_acquire)) return false;
        if (hr) *hr = exit_hr_.load(std::memory_order_relaxed);
        return true;
    }

private:
    static DWORD WINAPI thread_main(void* self);
    void run();
    void exit_with(HRESULT hr) {
        exit_hr_.store(static_cast<long>(hr), std::memory_order_relaxed);
        exited_.store(true, std::memory_order_release);
    }

    ComPtr<IAudioClient> client_;
    ComPtr<IAudioRenderClient> render_;
    ComPtr<IAudioClock> clock_;
    HANDLE event_ = nullptr;
    HANDLE thread_ = nullptr;
    HANDLE stop_ = nullptr;
    uint32_t buffer_frames_ = 0;
    uint16_t block_align_ = 0;
    std::string device_name_;
    std::wstring device_id_;
    std::string format_text_;
    HRESULT init_hr_ = S_OK;
    std::atomic<bool> exited_{false};
    std::atomic<long> exit_hr_{0};
};

} // namespace wf
