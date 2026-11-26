// Process loopback (Win11 >= 20348): the whole render mix minus our own process tree
// (exclude mode, the echo reference per spec 3.2) or exactly one process tree (include
// mode, the "Others" source). The virtual device has no mix format, so we state one.
#pragma once
#include "wasapi/capture_loop.h"

namespace wf {

enum class ProcessLoopbackMode { ExcludeTree, IncludeTree };

struct ProcessLoopbackOptions {
    ProcessLoopbackMode mode = ProcessLoopbackMode::ExcludeTree;
    DWORD target_pid = 0;        // 0 -> own process id (meaningful for ExcludeTree)
    uint32_t rate = 48000;       // 16000 is the other value the audio core cares about
    uint16_t channels = 1;
    bool prefer_event = true;
    uint32_t buffer_ms = 20;
};

class ProcessLoopbackStream {
public:
    HRESULT start(const ProcessLoopbackOptions& opt, IPacketSink* sink);
    void stop() { runner_.stop(); }
    const StreamDiagnostics& diag() const { return diag_; }
    HRESULT activation_hr() const { return activation_hr_; }

private:
    CaptureRunner runner_;
    StreamDiagnostics diag_;
    HRESULT activation_hr_ = S_OK;
};

// True when mmdevapi!ActivateAudioInterfaceAsync resolves (build check is done by the
// activation itself -- an old build fails with AUDCLNT_E_UNSUPPORTED_ACTIVATION_TYPE).
bool process_loopback_api_available();

} // namespace wf
