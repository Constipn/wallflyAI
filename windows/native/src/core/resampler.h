// Resampler interface (spec 3.4): the production path plugs in WebRTC's PushSincResampler,
// the fallback below is a linear interpolator used by the tests. Implementations must keep
// the time of sample 0, i.e. report their algorithmic delay so the caller can compensate it.
#pragma once
#include <cstddef>
#include <functional>
#include <memory>
#include <vector>

namespace wf {

class IResampler {
public:
    virtual ~IResampler() = default;
    // Appends the converted samples to `out` (never clears it).
    virtual void process(const float* in, size_t n, std::vector<float>& out) = 0;
    // Seconds by which the output lags the input content; 0 keeps sample 0 in place.
    virtual double algorithmic_delay_seconds() const = 0;
    virtual void reset() = 0;
};

// Built at (re)anchor time only, never on the per-frame path.
using ResamplerFactory = std::function<std::unique_ptr<IResampler>(double in_rate, double out_rate)>;

// Linear interpolation; exact pass-through when in_rate == out_rate. Output sample k
// carries input time k / out_rate, so the algorithmic delay is zero.
class LinearResampler final : public IResampler {
public:
    LinearResampler(double in_rate, double out_rate);
    void process(const float* in, size_t n, std::vector<float>& out) override;
    double algorithmic_delay_seconds() const override { return 0.0; }
    void reset() override;

private:
    double step_ = 1.0;     // input samples per output sample
    double pos_ = 0.0;      // next output position, relative to in[0] of the current block
    float  prev_ = 0.0f;    // last sample of the previous block (virtual index -1)
    bool   passthrough_ = false;
    bool   have_prev_ = false;
};

// Default factory used by the core when the caller injects nothing. Always linear, so the
// core unit tests measure the drift and framing rules against one fixed conversion.
ResamplerFactory default_resampler_factory();

// Factory the session layer installs on the real capture path (src/session/dsp_thread.cpp).
// With WF_WITH_AEC3=ON this is WebRTC's PushSincResampler (src/core/sinc_resampler.cpp),
// which reports its real algorithmic delay; otherwise it falls back to the linear one
// (src/core/linear_resampler.cpp). Exactly one definition is linked, the same way
// src/aec/AECBridge.cpp and src/aec/aec_stub.cpp are.
ResamplerFactory production_resampler_factory();

}  // namespace wf
