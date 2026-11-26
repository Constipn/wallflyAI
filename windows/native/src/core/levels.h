// Level meter per source (spec 3.6 "Pegelanzeige"): RMS -> (20*log10(max(rms,1e-5))+60)/60
// clamped to 0..1, published at most every 80 ms, falling back to 0 after 0.35 s of silence.
// Mac parity: the published value is the RMS of the ONE block that crosses the 80 ms
// throttle (AudioCapture.swift:156), not an average over the window, and the first block
// after a reset publishes immediately.
#pragma once
#include <cstdint>
#include <cstddef>

#include "core/constants.h"

namespace wf {

class LevelMeter {
public:
    void reset();
    // Publishes a new value when 80 ms have passed since the last one (or on the first block).
    void push(const float* samples, size_t n, int64_t now_100ns);
    // Current displayed level; decays to 0 after 0.35 s without a new value.
    float value(int64_t now_100ns) const;
    uint64_t updates() const { return updates_; }

    static float level_from_rms(float rms);

private:
    float   level_ = 0.0f;
    int64_t last_update_100ns_ = 0;
    bool    have_value_ = false;
    uint64_t updates_ = 0;
};

}  // namespace wf
