// Contract of the C bridge in src/aec/AECBridge.h as implemented by src/aec/AECBridge.cpp:
// input conditioning (NaN/Inf -> 0, clamp to [-1, 1]), the NS level range, metrics and the
// effect of a reset. The behavioural checks compare two independently created instances fed
// equivalent input, so they assert the conditioning exactly instead of guessing at the DSP.
#include "aec/AECBridge.h"
#include "test_util.h"

#include <cmath>
#include <cstdint>
#include <cstdio>
#include <limits>
#include <vector>

namespace {

constexpr int kN = MN_AEC_FRAME;

// Feeds `frames` capture frames of the constant `value` (no render) and returns the output.
std::vector<float> capture_const(float value, int frames) {
    mn_aec* aec = mn_aec_create();
    std::vector<float> in(static_cast<size_t>(kN), value);
    std::vector<float> out(static_cast<size_t>(kN) * static_cast<size_t>(frames));
    for (int f = 0; f < frames; ++f) {
        mn_aec_capture(aec, in.data(), &out[static_cast<size_t>(f) * kN]);
    }
    mn_aec_destroy(aec);
    return out;
}

bool same(const std::vector<float>& a, const std::vector<float>& b) {
    if (a.size() != b.size()) return false;
    for (size_t i = 0; i < a.size(); ++i) {
        if (!(a[i] == b[i])) return false;
    }
    return true;
}

bool all_finite(const std::vector<float>& v) {
    for (float x : v) {
        if (!std::isfinite(x)) return false;
    }
    return true;
}

// Deterministic speech-band-ish excitation, enough to drive the filter for metrics/reset.
std::vector<float> noise(uint64_t seed, float gain, int n) {
    std::vector<float> out(static_cast<size_t>(n));
    uint64_t state = seed;
    float low = 0.0f;
    for (int i = 0; i < n; ++i) {
        state = state * 6364136223846793005ULL + 1442695040888963407ULL;
        const float white =
            static_cast<float>(static_cast<int32_t>(static_cast<uint32_t>(state >> 32))) /
            static_cast<float>(INT32_MAX);
        low = 0.5f * low + 0.5f * white;
        out[static_cast<size_t>(i)] = gain * low;
    }
    return out;
}

// Runs an echo scenario (mic = far delayed by 80 ms * 0.5) and returns the ERLE over the
// last `tail_frames` frames, measured directly from the signals.
double drive_echo(mn_aec* aec, const std::vector<float>& far, int delay, float gain,
                  int tail_frames) {
    const int n = static_cast<int>(far.size());
    std::vector<float> mic(static_cast<size_t>(n), 0.0f);
    for (int i = delay; i < n; ++i) mic[static_cast<size_t>(i)] = far[static_cast<size_t>(i - delay)] * gain;

    std::vector<float> out(static_cast<size_t>(n));
    for (int i = 0; i + kN <= n; i += kN) {
        mn_aec_render(aec, &far[static_cast<size_t>(i)]);
        mn_aec_capture(aec, &mic[static_cast<size_t>(i)], &out[static_cast<size_t>(i)]);
    }
    const int from = n - tail_frames * kN;
    double pin = 0.0, pout = 0.0;
    for (int i = from; i < n; ++i) {
        pin += static_cast<double>(mic[static_cast<size_t>(i)]) * mic[static_cast<size_t>(i)];
        pout += static_cast<double>(out[static_cast<size_t>(i)]) * out[static_cast<size_t>(i)];
    }
    return 10.0 * std::log10(pin / (pout > 1e-20 ? pout : 1e-20));
}

void test_nan_and_inf_become_zero() {
    const float nan_v = std::numeric_limits<float>::quiet_NaN();
    const float inf_p = std::numeric_limits<float>::infinity();

    const std::vector<float> zeros = capture_const(0.0f, 8);
    // NaN and +-Inf are all mapped to 0, so each must produce exactly the silent result.
    CHECK(same(capture_const(nan_v, 8), zeros));
    CHECK(same(capture_const(inf_p, 8), zeros));
    CHECK(same(capture_const(-inf_p, 8), zeros));
    CHECK(all_finite(zeros));

    // A render frame of NaN must not poison the filter either.
    mn_aec* aec = mn_aec_create();
    std::vector<float> bad(static_cast<size_t>(kN), nan_v);
    std::vector<float> out(static_cast<size_t>(kN));
    for (int f = 0; f < 8; ++f) {
        mn_aec_render(aec, bad.data());
        mn_aec_capture(aec, bad.data(), out.data());
        CHECK(all_finite(out));
    }
    mn_aec_destroy(aec);
}

void test_clipping() {
    // Anything above +1 clamps to +1, so 5.0 and 1.0 must be indistinguishable downstream.
    CHECK(same(capture_const(5.0f, 8), capture_const(1.0f, 8)));
    CHECK(same(capture_const(-5.0f, 8), capture_const(-1.0f, 8)));
    // And the output of a full-scale input stays finite and in range.
    const std::vector<float> loud = capture_const(1.0f, 8);
    CHECK(all_finite(loud));
    bool bounded = true;
    for (float v : loud) {
        if (std::fabs(v) > 1.0f + 1e-3f) bounded = false;
    }
    CHECK(bounded);
}

void test_silence_stays_silent() {
    // 1000 frames (10 s) of digital silence must come out as digital silence: no comfort
    // noise, no DC from the high-pass, nothing that would show up in the level meter.
    const std::vector<float> out = capture_const(0.0f, 1000);
    bool silent = true;
    float peak = 0.0f;
    for (float v : out) {
        const float a = std::fabs(v);
        if (a > peak) peak = a;
        if (a != 0.0f) silent = false;
    }
    std::printf("  1000 silent frames -> peak %.3g\n", static_cast<double>(peak));
    CHECK(silent);
}

void test_ns_levels() {
    mn_aec* aec = mn_aec_create();
    const std::vector<float> in = noise(7, 0.2f, kN);
    std::vector<float> out(static_cast<size_t>(kN));
    // 0 = off, 1..4 = 6/12/18/21 dB (spec 3.3); every level must be accepted and usable.
    for (int level = 0; level <= 4; ++level) {
        mn_aec_set_ns(aec, level);
        for (int f = 0; f < 4; ++f) {
            mn_aec_capture(aec, in.data(), out.data());
            CHECK(all_finite(out));
        }
    }
    // Out of range values are ignored; the canceller keeps working at the last good level.
    mn_aec_set_ns(aec, -1);
    mn_aec_set_ns(aec, 5);
    mn_aec_set_ns(aec, 1000);
    mn_aec_capture(aec, in.data(), out.data());
    CHECK(all_finite(out));
    mn_aec_destroy(aec);

    // Level 2 (the app's setting) must attenuate a noise-only capture.
    mn_aec* plain = mn_aec_create();
    mn_aec* suppressed = mn_aec_create();
    mn_aec_set_ns(suppressed, 2);
    const std::vector<float> n = noise(11, 0.1f, kN * 200);
    double p_plain = 0.0, p_sup = 0.0;
    std::vector<float> a(static_cast<size_t>(kN)), b(static_cast<size_t>(kN));
    for (int i = 0; i + kN <= static_cast<int>(n.size()); i += kN) {
        mn_aec_capture(plain, &n[static_cast<size_t>(i)], a.data());
        mn_aec_capture(suppressed, &n[static_cast<size_t>(i)], b.data());
        if (i >= kN * 100) {   // after the NS start-up phase
            for (int k = 0; k < kN; ++k) {
                p_plain += static_cast<double>(a[static_cast<size_t>(k)]) * a[static_cast<size_t>(k)];
                p_sup += static_cast<double>(b[static_cast<size_t>(k)]) * b[static_cast<size_t>(k)];
            }
        }
    }
    const double ns_db = 10.0 * std::log10(p_plain / (p_sup > 1e-20 ? p_sup : 1e-20));
    std::printf("  NS level 2 attenuates stationary noise by %.2f dB\n", ns_db);
    CHECK(ns_db > 3.0);
    mn_aec_destroy(plain);
    mn_aec_destroy(suppressed);
}

void test_metrics_and_reset() {
    const std::vector<float> far = noise(42, 0.4f, kN * 500);   // 5 s
    mn_aec* aec = mn_aec_create();

    const double erle_converged = drive_echo(aec, far, 80 * 16, 0.5f, 100);
    double erle_db = 0.0;
    int delay_ms = -1;
    mn_aec_metrics(aec, &erle_db, &delay_ms);
    std::printf("  converged: measured ERLE %.2f dB, metrics erle=%.2f dB delay=%d ms\n",
                erle_converged, erle_db, delay_ms);
    CHECK(std::isfinite(erle_db));
    CHECK(erle_db > 0.0);          // the filter has learned something
    CHECK(delay_ms >= 0);          // a delay estimate is available
    CHECK(erle_converged > 20.0);

    // Reset must throw the learned filter away: the same signal right afterwards starts
    // from scratch, so the first second cancels far worse than the converged state did.
    mn_aec_reset(aec);
    const std::vector<float> shortfar = noise(42, 0.4f, kN * 100);   // 1 s
    const double erle_after_reset = drive_echo(aec, shortfar, 80 * 16, 0.5f, 50);
    std::printf("  after reset: ERLE %.2f dB (was %.2f dB)\n", erle_after_reset,
                erle_converged);
    CHECK(erle_after_reset < erle_converged);

    // And it recovers when the signal keeps coming.
    const double erle_recovered = drive_echo(aec, far, 80 * 16, 0.5f, 100);
    std::printf("  recovered: ERLE %.2f dB\n", erle_recovered);
    CHECK(erle_recovered > erle_after_reset);
    mn_aec_destroy(aec);
}

void test_null_guards() {
    // The header returns void, so the only contract is "does not crash"; a null input
    // yields silence, matching src/aec/aec_stub.cpp.
    std::vector<float> out(static_cast<size_t>(kN), 1.0f);
    mn_aec_render(nullptr, nullptr);
    mn_aec_capture(nullptr, nullptr, out.data());
    bool zeroed = true;
    for (float v : out) {
        if (v != 0.0f) zeroed = false;
    }
    CHECK(zeroed);

    mn_aec* aec = mn_aec_create();
    CHECK(aec != nullptr);
    mn_aec_render(aec, nullptr);
    std::vector<float> out2(static_cast<size_t>(kN), 1.0f);
    mn_aec_capture(aec, nullptr, out2.data());
    bool zeroed2 = true;
    for (float v : out2) {
        if (v != 0.0f) zeroed2 = false;
    }
    CHECK(zeroed2);
    double e = -1.0;
    int d = -1;
    mn_aec_metrics(nullptr, &e, &d);
    CHECK(e == 0.0);
    CHECK(d == 0);
    mn_aec_set_ns(nullptr, 2);
    mn_aec_reset(nullptr);
    mn_aec_destroy(aec);
    mn_aec_destroy(nullptr);
}

void test_independent_instances() {
    // Two handles must not share state. Measured through the signal, not through
    // GetMetrics(): the AEC3 metric is a short-window value that says nothing about how
    // much an instance has learned once the far end goes quiet.
    const std::vector<float> warmup = noise(5, 0.4f, kN * 400);    // 4 s
    const std::vector<float> probe = noise(5, 0.4f, kN * 100);     // 1 s

    mn_aec* trained = mn_aec_create();
    mn_aec* fresh = mn_aec_create();
    drive_echo(trained, warmup, 16 * 16, 0.5f, 50);                // converge `trained`

    // Same probe into both: the trained filter still cancels, the untouched one starts cold.
    const double erle_trained = drive_echo(trained, probe, 16 * 16, 0.5f, 50);
    const double erle_fresh = drive_echo(fresh, probe, 16 * 16, 0.5f, 50);
    std::printf("  same probe: trained instance %.2f dB, untouched instance %.2f dB\n",
                erle_trained, erle_fresh);
    CHECK(erle_trained > erle_fresh + 10.0);
    mn_aec_destroy(trained);
    mn_aec_destroy(fresh);
}

}  // namespace

void test_bridge() {
    test_null_guards();
    test_nan_and_inf_become_zero();
    test_clipping();
    test_silence_stays_silent();
    test_ns_levels();
    test_metrics_and_reset();
    test_independent_instances();
}
