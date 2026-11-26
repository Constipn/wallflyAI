// Recording mock for the AEC bridge: captures the call order, optionally subtracts the
// last rendered frame (so render-after-capture leaves residual echo) and runs the result
// through a 224-sample delay line, mirroring the real AEC3 + NS latency.
#include "aec/AECBridge.h"
#include "mock_aec.h"

#include <cstring>
#include <vector>

namespace {

struct State {
    float render[MN_AEC_FRAME] = {};
    bool  have_render = false;
    bool  subtract = true;
    int   delay = 224;
    std::vector<float> line;   // delay line, `delay` samples of history
    size_t line_idx = 0;
    std::vector<mock_aec::Entry> log;
    int renders = 0, captures = 0, resets = 0, ns = -1;
    double erle = 42.0;
    int delay_ms = 7;
};

State& st() {
    static State s;
    return s;
}

}  // namespace

struct mn_aec {
    int dummy;
};

extern "C" {

mn_aec* mn_aec_create(void) {
    static mn_aec instance{0};
    State& s = st();
    s.line.assign(static_cast<size_t>(s.delay), 0.0f);
    s.line_idx = 0;
    s.have_render = false;
    return &instance;
}

void mn_aec_destroy(mn_aec*) {}

void mn_aec_render(mn_aec*, const float* frame160) {
    State& s = st();
    if (frame160) std::memcpy(s.render, frame160, sizeof(s.render));
    else std::memset(s.render, 0, sizeof(s.render));
    s.have_render = true;
    ++s.renders;
    s.log.push_back({mock_aec::Call::Render, frame160 ? frame160[0] : 0.0f});
}

void mn_aec_capture(mn_aec*, const float* in160, float* out160) {
    State& s = st();
    ++s.captures;
    s.log.push_back({mock_aec::Call::Capture, in160 ? in160[0] : 0.0f});

    float diff[MN_AEC_FRAME];
    for (int i = 0; i < MN_AEC_FRAME; ++i) {
        const float x = in160 ? in160[i] : 0.0f;
        diff[i] = (s.subtract && s.have_render) ? (x - s.render[i]) : x;
    }
    // A fresh render frame is consumed by exactly one capture call.
    s.have_render = false;

    const size_t d = s.line.size();
    if (d == 0) {
        if (out160) std::memcpy(out160, diff, sizeof(diff));
        return;
    }
    for (int i = 0; i < MN_AEC_FRAME; ++i) {   // circular delay line, d samples
        const float old = s.line[s.line_idx];
        s.line[s.line_idx] = diff[i];
        s.line_idx = (s.line_idx + 1) % d;
        if (out160) out160[i] = old;
    }
}

void mn_aec_set_ns(mn_aec*, int level) {
    State& s = st();
    s.ns = level;
    s.log.push_back({mock_aec::Call::SetNs, static_cast<float>(level)});
}

void mn_aec_metrics(mn_aec*, double* erle_db, int* delay_ms) {
    if (erle_db) *erle_db = st().erle;
    if (delay_ms) *delay_ms = st().delay_ms;
}

void mn_aec_reset(mn_aec*) {
    State& s = st();
    ++s.resets;
    s.have_render = false;
    s.line.assign(s.line.size(), 0.0f);
    s.line_idx = 0;
    std::memset(s.render, 0, sizeof(s.render));
    s.log.push_back({mock_aec::Call::Reset, 0.0f});
}

}  // extern "C"

namespace mock_aec {

void set_echo_subtraction(bool on) { st().subtract = on; }

void set_delay(int samples) {
    State& s = st();
    s.delay = samples < 0 ? 0 : samples;
    s.line.assign(static_cast<size_t>(s.delay), 0.0f);
    s.line_idx = 0;
}

void clear_log() {
    State& s = st();
    s.log.clear();
    s.renders = s.captures = s.resets = 0;
}

const std::vector<Entry>& log() { return st().log; }
int renders() { return st().renders; }
int captures() { return st().captures; }
int resets() { return st().resets; }
int ns_level() { return st().ns; }

}  // namespace mock_aec
