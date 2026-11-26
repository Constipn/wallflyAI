// src/core/sinc_resampler.cpp: the production conversion to 16 kHz (spec 3.4).
// Checks the output rate, the reported algorithmic delay, the equal-rate passthrough and
// that a 1 kHz tone survives 48 kHz -> 16 kHz with less than 0.1 dB level error.
#include "core/resampler.h"
#include "test_util.h"

#include "audio_processing/resampler/push_sinc_resampler.h"

#include <cmath>
#include <cstdio>
#include <vector>

using wf::production_resampler_factory;

namespace {

constexpr double kPi = 3.14159265358979323846;
constexpr double kOut = 16000.0;

std::vector<float> tone(double rate, double hz, double seconds, float amp) {
    const size_t n = static_cast<size_t>(rate * seconds);
    std::vector<float> v(n);
    for (size_t i = 0; i < n; ++i) {
        v[i] = amp * static_cast<float>(std::sin(2.0 * kPi * hz * static_cast<double>(i) / rate));
    }
    return v;
}

double rms(const std::vector<float>& v, size_t from, size_t to) {
    double p = 0.0;
    for (size_t i = from; i < to && i < v.size(); ++i) {
        p += static_cast<double>(v[i]) * v[i];
    }
    const size_t n = (to > from) ? (to - from) : 1;
    return std::sqrt(p / static_cast<double>(n));
}

// Feeds the signal in irregular packet sizes, the way WASAPI delivers it, so the block
// buffering inside the wrapper is exercised rather than one tidy call.
std::vector<float> convert(double in_rate, const std::vector<float>& in, double* delay_out) {
    auto r = production_resampler_factory()(in_rate, kOut);
    if (delay_out) *delay_out = r->algorithmic_delay_seconds();
    std::vector<float> out;
    out.reserve(in.size());
    const size_t packets[] = {37, 480, 101, 1024, 7};
    size_t pos = 0, k = 0;
    while (pos < in.size()) {
        const size_t take = (in.size() - pos < packets[k % 5]) ? (in.size() - pos) : packets[k % 5];
        r->process(in.data() + pos, take, out);
        pos += take;
        ++k;
    }
    return out;
}

void test_rates_and_counts() {
    struct Case { double rate; const char* name; };
    const Case cases[] = {{48000.0, "48k"}, {44100.0, "44.1k"}, {8000.0, "8k"},
                          {32000.0, "32k"}, {22050.0, "22.05k"}, {11025.0, "11.025k"}};
    for (const Case& c : cases) {
        const std::vector<float> in = tone(c.rate, 1000.0, 2.0, 0.5f);
        double delay = -1.0;
        const std::vector<float> out = convert(c.rate, in, &delay);
        // Expected output length, allowing one whole block of input still buffered.
        const double expect = static_cast<double>(in.size()) * kOut / c.rate;
        const double block_ms = 1000.0 * static_cast<double>(out.size()) / kOut;
        std::printf("  %-9s %6zu in -> %6zu out (expect ~%.0f), delay %.1f us, %.0f ms\n",
                    c.name, in.size(), out.size(), expect, delay * 1e6, block_ms);
        CHECK(static_cast<double>(out.size()) <= expect + 1.0);
        CHECK(static_cast<double>(out.size()) >= expect - kOut * 0.05);   // < 50 ms pending
        // Reported delay is PushSincResampler's own figure: kKernelSize/2 input samples.
        const double want =
            static_cast<double>(webrtc::PushSincResampler::AlgorithmicDelaySeconds(
                static_cast<int>(c.rate)));
        CHECK_NEAR(delay, want, 1e-9);
        CHECK(delay > 0.0);
    }
}

void test_passthrough_at_equal_rates() {
    // 16 kHz in, 16 kHz out: byte-for-byte copy and zero delay, like LinearResampler.
    // The whole session layer relies on this - a 16 kHz device must not be filtered.
    const std::vector<float> in = tone(kOut, 1000.0, 0.5, 0.5f);
    double delay = -1.0;
    const std::vector<float> out = convert(kOut, in, &delay);
    CHECK(delay == 0.0);
    CHECK(out.size() == in.size());
    bool identical = (out.size() == in.size());
    for (size_t i = 0; identical && i < in.size(); ++i) {
        if (out[i] != in[i]) identical = false;
    }
    CHECK(identical);
}

void test_tone_level_preserved() {
    // A 1 kHz tone is far below the 8 kHz Nyquist, so the conversion must not change its
    // level. Measured over the steady interior, past the filter's start-up.
    const double amp = 0.5;
    const std::vector<float> in = tone(48000.0, 1000.0, 2.0, static_cast<float>(amp));
    double delay = 0.0;
    const std::vector<float> out = convert(48000.0, in, &delay);
    CHECK(out.size() > 16000);

    const double r_out = rms(out, 1600, out.size() - 1600);     // skip 100 ms at each end
    const double r_ref = amp / std::sqrt(2.0);                  // RMS of a sine
    const double err_db = 20.0 * std::log10(r_out / r_ref);
    std::printf("  48k->16k 1 kHz: rms %.6f vs %.6f -> %+.4f dB\n", r_out, r_ref, err_db);
    CHECK(std::fabs(err_db) < 0.1);

    // The output must really be a 1 kHz tone, not a mirrored image: correlate against a
    // locally generated 1 kHz sine over a window and expect a strong match at some phase.
    const std::vector<float> ref16 = tone(kOut, 1000.0, 1.0, static_cast<float>(amp));
    double best = 0.0;
    for (int lag = 0; lag < 16; ++lag) {
        double xy = 0.0, xx = 0.0, yy = 0.0;
        for (size_t i = 0; i + static_cast<size_t>(lag) < 8000; ++i) {
            const double x = ref16[i];
            const double y = out[3200 + i + static_cast<size_t>(lag)];
            xy += x * y; xx += x * x; yy += y * y;
        }
        const double corr = std::fabs(xy) / std::sqrt((xx > 0 ? xx : 1e-20) * (yy > 0 ? yy : 1e-20));
        if (corr > best) best = corr;
    }
    std::printf("  48k->16k 1 kHz: best |correlation| with a clean 1 kHz sine %.4f\n", best);
    CHECK(best > 0.99);
}

void test_reset_clears_state() {
    auto r = production_resampler_factory()(48000.0, kOut);
    const std::vector<float> in = tone(48000.0, 1000.0, 0.1, 0.5f);
    std::vector<float> a, b;
    r->process(in.data(), in.size(), a);
    r->reset();
    r->process(in.data(), in.size(), b);
    CHECK(a.size() == b.size());
    bool identical = (a.size() == b.size());
    for (size_t i = 0; identical && i < a.size(); ++i) {
        if (a[i] != b[i]) identical = false;
    }
    CHECK(identical);   // after a reset the same input must give the same output again
}

}  // namespace

void test_sinc_resampler() {
    test_rates_and_counts();
    test_passthrough_at_equal_rates();
    test_tone_level_preserved();
    test_reset_clears_state();
}
