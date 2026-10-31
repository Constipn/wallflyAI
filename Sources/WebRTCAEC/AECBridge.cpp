#include "AECBridge.h"
#include "api/echo_canceller3_factory.h"
#include "api/environment.h"
#include "audio_processing/audio_buffer.h"
#include "audio_processing/high_pass_filter.h"
#include "modules/audio_processing/ns/noise_suppressor.h"
#include <algorithm>
#include <cmath>
#include <memory>

struct MNAEC {
    webrtc::Environment environment;
    std::unique_ptr<webrtc::EchoControl> echo;
    std::unique_ptr<webrtc::NoiseSuppressor> ns;
    int nsLevel = 0;
    webrtc::AudioBuffer render{16000, 1, 16000, 1, 16000, 1};
    webrtc::AudioBuffer capture{16000, 1, 16000, 1, 16000, 1};
    webrtc::HighPassFilter highpass{16000, 1};
    MNAEC() { reset(); }
    void reset() {
        // Upstream delay estimator; no device-name bypass, AGC, or fixed acoustic delay.
        // Upstream AEC3 tuning except the near-end state's masking thresholds (8/12, upstream
        // 1.09/1.1 and .1/.3): keeps own double talk while echo dominates the normal state. The former
        // permissive normal tuning and SNR 5 leaked music/echo into "Ich" (sweep 2026-10-19).
        webrtc::EchoCanceller3Config config;
        config.suppressor.nearend_tuning.mask_lf = {8.f, 12.f, .3f};
        config.suppressor.nearend_tuning.mask_hf = {8.f, 12.f, .3f};
        echo = webrtc::EchoCanceller3Factory(config).Create(environment, 16000, 1, 1);
        highpass.Reset();
        makeNoiseSuppressor();
    }
    void makeNoiseSuppressor() {
        ns.reset();
        if (nsLevel == 0) return;
        webrtc::NsConfig nsConfig;
        nsConfig.target_level = static_cast<webrtc::NsConfig::SuppressionLevel>(nsLevel - 1);
        ns = std::make_unique<webrtc::NoiseSuppressor>(nsConfig, 16000, 1);
    }
};
static void fill(webrtc::AudioBuffer& buffer, const float *samples) {
    for (int i = 0; i < 160; ++i)
        buffer.channels()[0][i] = std::isfinite(samples[i]) ? std::clamp(samples[i], -1.f, 1.f) * 32768.f : 0.f;
}
extern "C" MNAEC *mn_aec_create() { try { return new MNAEC; } catch (...) { return nullptr; } }
extern "C" int mn_aec_render(MNAEC *aec, const float *samples) {
    if (!aec || !samples) return 0;
    try { fill(aec->render, samples); aec->echo->AnalyzeRender(&aec->render); return 1; } catch (...) { return 0; }
}
extern "C" int mn_aec_capture(MNAEC *aec, const float *samples, float *output) {
    if (!aec || !samples || !output) return 0;
    try {
        // Same order as upstream audio_processing_impl.cc (16 kHz: one band, no split).
        fill(aec->capture, samples);
        aec->echo->AnalyzeCapture(&aec->capture);
        aec->highpass.Process(&aec->capture, false);
        if (aec->ns) aec->ns->Analyze(aec->capture);
        aec->echo->ProcessCapture(&aec->capture, false);
        if (aec->ns) aec->ns->Process(&aec->capture);
        for (int i = 0; i < 160; ++i) output[i] = aec->capture.channels()[0][i] / 32768.f;
        return 1;
    } catch (...) { return 0; }
}
extern "C" int mn_aec_set_ns(MNAEC *aec, int level) {
    if (!aec || level < 0 || level > 4) return 0;
    try { aec->nsLevel = level; aec->makeNoiseSuppressor(); return 1; } catch (...) { return 0; }
}
extern "C" int mn_aec_metrics(MNAEC *aec, double *erle, int *delay_ms) {
    if (!aec || !erle || !delay_ms) return 0;
    try { auto metrics = aec->echo->GetMetrics(); *erle = metrics.echo_return_loss_enhancement; *delay_ms = metrics.delay_ms; return 1; }
    catch (...) { return 0; }
}
extern "C" int mn_aec_reset(MNAEC *aec) {
    if (!aec) return 0;
    try { aec->reset(); return 1; } catch (...) { return 0; }
}
extern "C" void mn_aec_destroy(MNAEC *aec) { delete aec; }
