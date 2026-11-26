// LinearResampler: the test/fallback conversion to 16 kHz. Keeps one sample of state so
// blocks join seamlessly; output k is taken at input position k * in_rate / out_rate.
#include "core/resampler.h"

#include <cmath>

namespace wf {

LinearResampler::LinearResampler(double in_rate, double out_rate) {
    if (in_rate <= 0.0 || out_rate <= 0.0) in_rate = out_rate = 1.0;
    step_ = in_rate / out_rate;
    passthrough_ = (in_rate == out_rate);
}

void LinearResampler::reset() {
    pos_ = 0.0;
    prev_ = 0.0f;
    have_prev_ = false;
}

void LinearResampler::process(const float* in, size_t n, std::vector<float>& out) {
    if (!in || n == 0) return;
    if (passthrough_) {
        out.insert(out.end(), in, in + n);
        return;
    }
    // Virtual index -1 is the previous block's last sample; 0..n-1 is this block.
    const double last = static_cast<double>(n) - 1.0;
    while (std::floor(pos_) + 1.0 <= last) {   // needs in[i] and in[i+1]
        const double fi = std::floor(pos_);
        const double f = pos_ - fi;
        const long i = static_cast<long>(fi);
        float a;
        if (i < 0) {
            a = have_prev_ ? prev_ : in[0];
        } else {
            a = in[static_cast<size_t>(i)];
        }
        const float b = in[static_cast<size_t>(i + 1)];
        out.push_back(a + (b - a) * static_cast<float>(f));
        pos_ += step_;
    }
    prev_ = in[n - 1];
    have_prev_ = true;
    pos_ -= static_cast<double>(n);
}

ResamplerFactory default_resampler_factory() {
    return [](double in_rate, double out_rate) -> std::unique_ptr<IResampler> {
        return std::unique_ptr<IResampler>(new LinearResampler(in_rate, out_rate));
    };
}

#if !defined(WF_WITH_AEC3) || !WF_WITH_AEC3
// Stub build: no vendored WebRTC, so the production path uses the linear converter too.
// The WF_WITH_AEC3=ON definition lives in src/core/sinc_resampler.cpp.
ResamplerFactory production_resampler_factory() { return default_resampler_factory(); }
#endif

}  // namespace wf
