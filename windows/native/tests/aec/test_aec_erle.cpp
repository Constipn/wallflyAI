// Synthetic ERLE acceptance for the vendored AEC3 (spec 3.3, plan "Tests").
//
// The corpus is a 1:1 port of the Mac test EchoAudioTests.echoAndDoubleTalk
// (EchoAudioTests.swift in the Swift tests of the macOS app): the same LCG, the same
// speech-band shaping, the same two-tap echo path and the same measurement window. Only
// then is the comparison against the Mac numbers meaningful, because ERLE on this corpus
// depends entirely on the exact excitation.
//
// Corpus, 12 s at 16 kHz:
//   far  = speech-like, seed 42,  gain 0.45, silenced from 9 s
//   near = speech-like, seed 999, gain 0.35, silent before 5 s
//   mic  = far delayed by d and attenuated, plus one reflection, plus near
//   -> far only 0-5 s, double talk 5-9 s, near only 9-12 s
// ERLE is measured over 2-5 s (far only; the first 2 s let the filter converge), exactly
// the Mac's echoRange of 32000..<80000.
//
// NS stays off here, as on the Mac (EchoCanceller() defaults to no noise suppression), so
// the bridge latency is AEC3's 128 samples; the test measures energy, not alignment.
#include "aec/AECBridge.h"
#include "test_util.h"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <vector>

namespace {

constexpr int kRate = MN_AEC_RATE;
constexpr int kCount = kRate * 12;

// Mac: Float(Int32(truncatingIfNeeded: state >> 32)) / Float(Int32.max), with the same
// 64-bit LCG constants. Unsigned overflow wraps in C++ just as Swift's &* / &+ do.
std::vector<float> speech_like(uint64_t seed, float gain) {
    std::vector<float> out(static_cast<size_t>(kCount));
    uint64_t state = seed;
    float low = 0.0f, slow = 0.0f;
    for (int i = 0; i < kCount; ++i) {
        state = state * 6364136223846793005ULL + 1442695040888963407ULL;
        const float white = static_cast<float>(static_cast<int32_t>(
                                static_cast<uint32_t>(state >> 32))) /
                            static_cast<float>(INT32_MAX);
        low = 0.5f * low + 0.5f * white;
        slow = 0.85f * slow + 0.15f * low;
        // Swift computes the envelope in Double and narrows to Float; mirror that.
        const float envelope = static_cast<float>(
            0.7 + 0.3 * std::sin(static_cast<double>(i) * 2.0 * 3.14159265358979323846 *
                                 3.0 / 16000.0));
        out[static_cast<size_t>(i)] = gain * (low - slow) * envelope;
    }
    return out;
}

// Swift squares in Float and accumulates in Double.
double power(const std::vector<float>& v, int from, int to) {
    double p = 0.0;
    for (int i = from; i < to; ++i) {
        const float s = v[static_cast<size_t>(i)];
        p += static_cast<double>(s * s);
    }
    return p;
}

struct Result {
    double erle;
    bool finite;
};

Result run_delay(int delay_ms) {
    std::vector<float> far = speech_like(42, 0.45f);
    std::vector<float> near = speech_like(999, 0.35f);
    for (int i = 0; i < 5 * kRate; ++i) near[static_cast<size_t>(i)] = 0.0f;
    for (int i = 9 * kRate; i < kCount; ++i) far[static_cast<size_t>(i)] = 0.0f;

    // Mac echo path: direct tap plus one reflection, both scaled by the same `gain`.
    const int delay = delay_ms * 16;
    const float gain = (delay_ms == 16) ? 0.25f : 0.65f;
    const int reflection_delay = (delay_ms == 180) ? 503 : 211;
    const float reflection_gain = (delay_ms == 16) ? 0.1f : ((delay_ms == 80) ? 0.25f : 0.35f);

    std::vector<float> mic(static_cast<size_t>(kCount));
    for (int i = 0; i < kCount; ++i) {
        float echo = 0.0f;
        if (i >= delay) echo += far[static_cast<size_t>(i - delay)] * gain;
        if (i >= delay + reflection_delay) {
            echo += far[static_cast<size_t>(i - delay - reflection_delay)] * gain *
                    reflection_gain;
        }
        mic[static_cast<size_t>(i)] = echo + near[static_cast<size_t>(i)];
    }

    // Render before the matching capture, one 10 ms frame at a time (spec 3.3 step 2).
    mn_aec* aec = mn_aec_create();
    std::vector<float> out(static_cast<size_t>(kCount));
    for (int i = 0; i < kCount; i += MN_AEC_FRAME) {
        mn_aec_render(aec, &far[static_cast<size_t>(i)]);
        mn_aec_capture(aec, &mic[static_cast<size_t>(i)], &out[static_cast<size_t>(i)]);
    }

    double erle_metric = 0.0;
    int delay_metric = 0;
    mn_aec_metrics(aec, &erle_metric, &delay_metric);
    mn_aec_destroy(aec);

    bool finite = true;
    for (float v : out) {
        if (!std::isfinite(v)) { finite = false; break; }
    }

    const int lo = 32000, hi = 80000;   // Mac echoRange: 2 s .. 5 s, far only
    const double erle =
        10.0 * std::log10(power(mic, lo, hi) / (power(out, lo, hi) > 1e-20
                                                    ? power(out, lo, hi)
                                                    : 1e-20));
    std::printf("  delay=%3d ms  ERLE=%6.2f dB  (Mac baseline %5.2f)  "
                "metrics: erle=%.2f dB delay=%d ms\n",
                delay_ms, erle,
                delay_ms == 16 ? 63.13 : (delay_ms == 80 ? 69.57 : 68.15),
                erle_metric, delay_metric);
    return {erle, finite};
}

}  // namespace

void test_aec_erle() {
    // Mac baselines on this corpus: 63.13 / 69.57 / 68.15 dB. NOTE: those were measured
    // with the former permissive suppressor tuning (see vendor PROVENANCE.md, 2026-10-19);
    // the vendored config we build is the later one, so the numbers are a reference point,
    // not a bit-exact target. The Mac test itself only asserts ERLE >= 20 dB.
    const int delays[] = {16, 80, 180};
    for (int d : delays) {
        const Result r = run_delay(d);
        CHECK(r.finite);
        CHECK(r.erle >= 60.0);
    }
}
