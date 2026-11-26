// Production resampler (spec 3.4): WebRTC's PushSincResampler behind wf::IResampler.
//
// The Mac uses AVAudioConverter with primeMethod = .normal, which keeps the time of sample
// 0 and reports a fixed input latency the caller compensates. PushSincResampler is the
// equivalent here: a windowed-sinc converter whose algorithmic delay is exactly
// kKernelSize/2 input samples, reported through algorithmic_delay_seconds() so
// CaptureNormalizer can subtract it and word timestamps stay put.
//
// PushSincResampler converts a fixed block per call, so this wrapper buffers the incoming
// packets - which arrive at whatever size WASAPI hands us - into whole blocks. The block is
// the shortest multiple of 10 ms for which both the input and the output sample count are
// integers: 10 ms for 8/16/44.1/48 kHz, 20 ms for 22.05 kHz, 40 ms for 11.025 kHz. A rate
// where no such block exists within 1 s (a non-integer rate) falls back to the linear
// converter rather than silently resampling at a slightly wrong ratio.
//
// Equal rates pass through untouched with zero delay, exactly like LinearResampler: the
// capture path is 16 kHz in, 16 kHz out whenever the device already runs at 16 kHz, and
// running the sinc filter there would add delay and ripple for nothing.
#include "core/resampler.h"

#include "audio_processing/resampler/push_sinc_resampler.h"

#include <cmath>
#include <cstdint>

namespace wf {
namespace {

bool integral(double x) { return std::fabs(x - static_cast<double>(std::llround(x))) < 1e-9; }

class SincResampler final : public IResampler {
public:
    SincResampler(double in_rate, double out_rate) {
        if (in_rate <= 0.0 || out_rate <= 0.0) {
            passthrough_ = true;
            return;
        }
        if (in_rate == out_rate) {
            passthrough_ = true;
            return;
        }
        for (int k = 1; k <= 100; ++k) {
            const double s = in_rate * k / 100.0;
            const double d = out_rate * k / 100.0;
            if (integral(s) && integral(d) && s >= 1.0 && d >= 1.0) {
                src_block_ = static_cast<size_t>(std::llround(s));
                dst_block_ = static_cast<size_t>(std::llround(d));
                break;
            }
        }
        if (src_block_ == 0) {
            // No whole-block ratio: keep the exact rate relationship with the linear path.
            fallback_.reset(new LinearResampler(in_rate, out_rate));
            return;
        }
        impl_.reset(new webrtc::PushSincResampler(src_block_, dst_block_));
        delay_ = static_cast<double>(
            webrtc::PushSincResampler::AlgorithmicDelaySeconds(static_cast<int>(in_rate)));
        scratch_.assign(dst_block_, 0.0f);
        // One block of headroom plus a typical WASAPI packet, so process() never allocates.
        pending_.reserve(src_block_ * 4);
    }

    void process(const float* in, size_t n, std::vector<float>& out) override {
        if (!in || n == 0) return;
        if (passthrough_) {
            out.insert(out.end(), in, in + n);
            return;
        }
        if (fallback_) {
            fallback_->process(in, n, out);
            return;
        }
        pending_.insert(pending_.end(), in, in + n);
        size_t consumed = 0;
        while (pending_.size() - consumed >= src_block_) {
            impl_->Resample(pending_.data() + consumed, src_block_, scratch_.data(), dst_block_);
            out.insert(out.end(), scratch_.begin(), scratch_.end());
            consumed += src_block_;
        }
        if (consumed > 0) {
            pending_.erase(pending_.begin(),
                           pending_.begin() + static_cast<std::ptrdiff_t>(consumed));
        }
    }

    double algorithmic_delay_seconds() const override { return delay_; }

    void reset() override {
        pending_.clear();
        if (fallback_) {
            fallback_->reset();
            return;
        }
        if (impl_) impl_.reset(new webrtc::PushSincResampler(src_block_, dst_block_));
    }

private:
    bool passthrough_ = false;
    size_t src_block_ = 0;
    size_t dst_block_ = 0;
    double delay_ = 0.0;
    std::unique_ptr<webrtc::PushSincResampler> impl_;
    std::unique_ptr<IResampler> fallback_;
    std::vector<float> pending_;
    std::vector<float> scratch_;
};

}  // namespace

ResamplerFactory production_resampler_factory() {
    return [](double in_rate, double out_rate) -> std::unique_ptr<IResampler> {
        return std::unique_ptr<IResampler>(new SincResampler(in_rate, out_rate));
    };
}

}  // namespace wf
