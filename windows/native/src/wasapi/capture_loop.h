// Shared capture machinery: the endpoint "open ladder" (RAW -> no RAW, event -> polling)
// and the capture thread itself (MMCSS "Pro Audio", wait on {event, stop} with a 10 ms
// timeout, then drain GetNextPacketSize). Conversion buffers are sized before Start().
#pragma once
#include <audioclient.h>
#include <mmdeviceapi.h>
#include <stdint.h>
#include <string>
#include <vector>

#include "wasapi/capture_types.h"
#include "wasapi/com_util.h"
#include "wasapi/format_convert.h"

namespace wf {

struct StreamDiagnostics {
    bool opened = false;
    bool event_driven = false;
    bool raw_requested = false;
    bool raw_accepted = false;
    HRESULT raw_hr = S_OK;
    HRESULT init_hr = S_OK;
    int init_attempt = 0;        // 1-based index in the open ladder that succeeded
    uint32_t sample_rate = 0;
    uint16_t channels = 0;
    const char* sample_format = "unknown";
    PcmFormat format{};          // the negotiated stream format (rate/channels/sample_format)
    uint32_t buffer_frames = 0;
    uint32_t stream_flags = 0;
    std::string format_text;
    std::string notes;
};

struct OpenSpec {
    DWORD extra_flags = 0;                       // e.g. AUDCLNT_STREAMFLAGS_LOOPBACK
    bool prefer_event = true;
    bool prefer_raw = false;
    AUDIO_STREAM_CATEGORY category = AudioCategory_Other;
    uint32_t buffer_ms = 20;
};

struct OpenedCapture {
    ComPtr<IAudioClient> client;
    ComPtr<IAudioCaptureClient> capture;
    HANDLE event = nullptr;      // owned by OpenedCapture; closed by close()
    PcmFormat fmt{};
    uint32_t buffer_frames = 0;
    StreamDiagnostics diag;
    void close();
};

// Opens a shared-mode capture client on `dev`, trying RAW and event callback first and
// falling back step by step. Returns the HRESULT of the last attempt on failure.
HRESULT open_capture_on_device(IMMDevice* dev, const OpenSpec& spec, OpenedCapture* out);

// The shared-mode format a capture stream opened on `dev` with this category and RAW
// option would get right now: a fresh client, the same SetClientProperties as the open
// ladder, then GetMixFormat. Comparing it with StreamDiagnostics::format of the running
// stream is the mic format-change check (spec 3.1); a plain GetMixFormat without the RAW
// option reports the processed format and would differ on RAW-capable mic arrays.
HRESULT query_capture_format(IMMDevice* dev, AUDIO_STREAM_CATEGORY category, bool raw,
                             PcmFormat* out);

// Runs a capture client on its own Win32 thread until stop() is called.
class CaptureRunner {
public:
    CaptureRunner() = default;
    CaptureRunner(const CaptureRunner&) = delete;
    CaptureRunner& operator=(const CaptureRunner&) = delete;
    ~CaptureRunner();

    // Takes ownership of everything in `opened` (including its event handle).
    HRESULT start(OpenedCapture&& opened, StreamKind kind, IPacketSink* sink);
    void stop();
    bool running() const { return thread_ != nullptr; }
    const StreamDiagnostics& diag() const { return opened_.diag; }
    uint32_t overflow_packets() const { return overflow_; }

private:
    static DWORD WINAPI thread_main(void* self);
    void run();

    OpenedCapture opened_;
    StreamKind kind_ = StreamKind::Mic;
    IPacketSink* sink_ = nullptr;
    HANDLE thread_ = nullptr;
    HANDLE stop_ = nullptr;
    std::vector<float> conv_;
    uint32_t overflow_ = 0;
};

} // namespace wf
