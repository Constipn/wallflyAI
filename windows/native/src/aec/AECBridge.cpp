// WebRTC AEC3 + Noise Suppressor behind the C bridge of src/aec/AECBridge.h (spec 3.3).
//
// Ported from the Mac bridge (Sources/WebRTCAEC/AECBridge.cpp of the macOS app). The DSP body -
// the AEC3 config, the input conditioning and the call order - is unchanged; only the C
// surface was adapted to this repository's AECBridge.h, which differs from the Mac header:
//
//   Mac                                   here
//   typedef struct MNAEC MNAEC            typedef struct mn_aec mn_aec
//   int  mn_aec_render(...)               void mn_aec_render(...)
//   int  mn_aec_capture(...)              void mn_aec_capture(...)
//   int  mn_aec_set_ns(...)               void mn_aec_set_ns(...)
//   int  mn_aec_metrics(...)              void mn_aec_metrics(...)
//   int  mn_aec_reset(...)                void mn_aec_reset(...)
//
// Because no function here has a return channel, the Mac's try/catch wrappers - whose only
// purpose was to turn an exception into a 0 return - have no counterpart and are dropped.
// That also keeps the whole DLL on -fno-exceptions as the build plan requires; WebRTC is
// written for that mode and the vendored code does not throw (jsoncpp, which does, is not
// compiled - see vendor/CMakeLists.txt). The null-pointer guards are kept, and
// mn_aec_capture zeroes its output on a null input so it matches src/aec/aec_stub.cpp.
//
// Everything else is the Mac behaviour verbatim:
//   * Input float is clamped to [-1, 1], NaN and Inf become 0, then scaled by 32768.
//   * AEC3 is created through EchoCanceller3Factory with upstream defaults except
//     suppressor.nearend_tuning.mask_lf / mask_hf = {8, 12, .3}.
//   * Capture order is upstream audio_processing_impl.cc:
//     AnalyzeCapture -> high-pass -> NS.Analyze -> AEC3.ProcessCapture -> NS.Process.
//   * NS level 0 = off (no NoiseSuppressor at all), 1..4 = 6/12/18/21 dB. The app uses 2.
//   * No AGC, no fixed acoustic delay, no headphone bypass.
#include "aec/AECBridge.h"

#include "api/echo_canceller3_factory.h"
#include "api/environment.h"
#include "audio_processing/audio_buffer.h"
#include "audio_processing/high_pass_filter.h"
#include "modules/audio_processing/ns/noise_suppressor.h"

#include <algorithm>
#include <cmath>
#include <cstring>
#include <memory>

// Named after the Mac struct's role; the handle type is the one AECBridge.h declares.
struct mn_aec {
    webrtc::Environment environment;
    std::unique_ptr<webrtc::EchoControl> echo;
    std::unique_ptr<webrtc::NoiseSuppressor> ns;
    int nsLevel = 0;
    webrtc::AudioBuffer render{MN_AEC_RATE, 1, MN_AEC_RATE, 1, MN_AEC_RATE, 1};
    webrtc::AudioBuffer capture{MN_AEC_RATE, 1, MN_AEC_RATE, 1, MN_AEC_RATE, 1};
    webrtc::HighPassFilter highpass{MN_AEC_RATE, 1};

    mn_aec() { reset(); }

    void reset() {
        // Upstream delay estimator; no device-name bypass, AGC, or fixed acoustic delay.
        // Upstream AEC3 tuning except the near-end state's masking thresholds (8/12, upstream
        // 1.09/1.1 and .1/.3): keeps own double talk while echo dominates the normal state.
        webrtc::EchoCanceller3Config config;
        config.suppressor.nearend_tuning.mask_lf = {8.f, 12.f, .3f};
        config.suppressor.nearend_tuning.mask_hf = {8.f, 12.f, .3f};
        echo = webrtc::EchoCanceller3Factory(config).Create(environment, MN_AEC_RATE, 1, 1);
        highpass.Reset();
        makeNoiseSuppressor();
    }

    void makeNoiseSuppressor() {
        ns.reset();
        if (nsLevel == 0) return;
        webrtc::NsConfig nsConfig;
        nsConfig.target_level = static_cast<webrtc::NsConfig::SuppressionLevel>(nsLevel - 1);
        ns = std::make_unique<webrtc::NoiseSuppressor>(nsConfig, MN_AEC_RATE, 1);
    }
};

namespace {

// WebRTC works in the S16 float range; our callers hand over normalized float.
void fill(webrtc::AudioBuffer& buffer, const float* samples) {
    for (int i = 0; i < MN_AEC_FRAME; ++i) {
        buffer.channels()[0][i] =
            std::isfinite(samples[i]) ? std::clamp(samples[i], -1.f, 1.f) * 32768.f : 0.f;
    }
}

}  // namespace

extern "C" {

mn_aec* mn_aec_create(void) { return new mn_aec(); }

void mn_aec_destroy(mn_aec* aec) { delete aec; }

void mn_aec_render(mn_aec* aec, const float* frame160) {
    if (!aec || !frame160) return;
    fill(aec->render, frame160);
    aec->echo->AnalyzeRender(&aec->render);
}

void mn_aec_capture(mn_aec* aec, const float* in160, float* out160) {
    if (!out160) return;
    if (!aec || !in160) {
        std::memset(out160, 0, sizeof(float) * MN_AEC_FRAME);
        return;
    }
    // Same order as upstream audio_processing_impl.cc (16 kHz: one band, no split).
    fill(aec->capture, in160);
    aec->echo->AnalyzeCapture(&aec->capture);
    aec->highpass.Process(&aec->capture, false);
    if (aec->ns) aec->ns->Analyze(aec->capture);
    aec->echo->ProcessCapture(&aec->capture, false);
    if (aec->ns) aec->ns->Process(&aec->capture);
    for (int i = 0; i < MN_AEC_FRAME; ++i) out160[i] = aec->capture.channels()[0][i] / 32768.f;
}

void mn_aec_set_ns(mn_aec* aec, int level) {
    // Out-of-range levels leave the current setting untouched, as on the Mac (which
    // reported the rejection through its int return; this header has none).
    if (!aec || level < 0 || level > 4) return;
    aec->nsLevel = level;
    aec->makeNoiseSuppressor();
}

void mn_aec_metrics(mn_aec* aec, double* erle_db, int* delay_ms) {
    if (!aec) {
        if (erle_db) *erle_db = 0.0;
        if (delay_ms) *delay_ms = 0;
        return;
    }
    const webrtc::EchoControl::Metrics metrics = aec->echo->GetMetrics();
    if (erle_db) *erle_db = metrics.echo_return_loss_enhancement;
    if (delay_ms) *delay_ms = metrics.delay_ms;
}

void mn_aec_reset(mn_aec* aec) {
    if (!aec) return;
    aec->reset();
}

}  // extern "C"
