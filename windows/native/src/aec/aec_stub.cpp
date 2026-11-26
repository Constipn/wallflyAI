// Passthrough implementation of src/aec/AECBridge.h.
//
// It exists so WallflyAudio.dll links and the whole session layer can be built and
// tested before the vendored WebRTC AEC3 + NS lands as src/aec/AECBridge.cpp. CMake
// compiles exactly one of the two: this file when WF_WITH_AEC3 is OFF (the default),
// AECBridge.cpp when it is ON. wf_capabilities() reports WF_CAP_AEC3 only in the
// latter case, so the managed side can tell a real canceller from this stub.
//
// Behaviour: capture copies in -> out unchanged (no delay, so the 224-sample latency
// compensation of the core still lines up with "no processing"), render is a no-op and
// the metrics report ERLE 0 dB / delay 0 ms. NaN and Inf are still scrubbed and the
// signal is still clipped to [-1, 1], because the real bridge does that too and the
// downstream stages (level meter, WAV archive) must not see garbage either way.
#include "aec/AECBridge.h"

#include <cmath>
#include <cstring>

namespace {

struct StubState {
    int ns_level = 0;
    unsigned long long renders = 0;
    unsigned long long captures = 0;
    unsigned long long resets = 0;
};

inline float scrub(float v) {
    if (!(v == v)) return 0.0f;             // NaN
    if (v > 1.0f) return 1.0f;
    if (v < -1.0f) return -1.0f;
    if (std::isinf(v)) return v > 0.0f ? 1.0f : -1.0f;
    return v;
}

}  // namespace

// The handle is a real object so a caller that creates several sessions gets several
// independent states, exactly like the vendored bridge.
struct mn_aec {
    StubState st;
};

extern "C" {

mn_aec* mn_aec_create(void) { return new mn_aec(); }

void mn_aec_destroy(mn_aec* aec) { delete aec; }

void mn_aec_render(mn_aec* aec, const float* frame160) {
    (void)frame160;
    if (aec) ++aec->st.renders;
}

void mn_aec_capture(mn_aec* aec, const float* in160, float* out160) {
    if (aec) ++aec->st.captures;
    if (!out160) return;
    if (!in160) {
        std::memset(out160, 0, sizeof(float) * MN_AEC_FRAME);
        return;
    }
    for (int i = 0; i < MN_AEC_FRAME; ++i) out160[i] = scrub(in160[i]);
}

void mn_aec_set_ns(mn_aec* aec, int level) {
    if (aec) aec->st.ns_level = level;
}

void mn_aec_metrics(mn_aec* aec, double* erle_db, int* delay_ms) {
    (void)aec;
    if (erle_db) *erle_db = 0.0;
    if (delay_ms) *delay_ms = 0;
}

void mn_aec_reset(mn_aec* aec) {
    if (aec) ++aec->st.resets;
}

}  // extern "C"
