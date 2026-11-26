// LevelMeter implementation, following AudioCapture.swift:156 + LegacyAudioCapture.level
// and RecordingActivity.expireSignals. Host clock driven, so the 80 ms cadence and the
// 0.35 s decay are testable with an injected time.
#include "core/levels.h"

#include <cmath>

namespace wf {

float LevelMeter::level_from_rms(float rms) {
    const float r = rms < k::kLevelRmsFloor ? k::kLevelRmsFloor : rms;
    const float v = (20.0f * std::log10(r) + k::kLevelDbOffset) / k::kLevelDbRange;
    if (v < 0.0f) return 0.0f;
    if (v > 1.0f) return 1.0f;
    return v;
}

void LevelMeter::reset() {
    level_ = 0.0f;
    last_update_100ns_ = 0;
    have_value_ = false;
    updates_ = 0;
}

void LevelMeter::push(const float* samples, size_t n, int64_t now_100ns) {
    if (!samples || n == 0) return;
    // Swift: "if now - (lastMeter[speaker] ?? 0) >= 0.08 { meter(speaker, level(buffer)) }".
    // The first block always publishes, and the published value is the RMS of THAT block -
    // the Mac never averages across the throttle window.
    if (have_value_ && now_100ns - last_update_100ns_ < k::kLevelIntervalTicks) return;
    double sum = 0.0;
    for (size_t i = 0; i < n; ++i) {
        const double v = static_cast<double>(samples[i]);
        sum += v * v;
    }
    level_ = level_from_rms(static_cast<float>(std::sqrt(sum / static_cast<double>(n))));
    last_update_100ns_ = now_100ns;
    have_value_ = true;
    ++updates_;
}

float LevelMeter::value(int64_t now_100ns) const {
    if (!have_value_) return 0.0f;
    if (now_100ns - last_update_100ns_ > k::kLevelDecayTicks) return 0.0f;
    return level_;
}

}  // namespace wf
