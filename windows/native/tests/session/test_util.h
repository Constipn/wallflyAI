// Hand-rolled harness for the session suites, same style as tests/core/test_util.h.
// Everything here drives the DLL through its public C ABI and the fake capture path -
// no hardware, no real threads, no wall-clock waiting.
#pragma once
#include <cmath>
#include <cstdio>
#include <cstring>
#include <string>
#include <vector>

#include "api/wallfly_audio.h"

extern int g_failures;
extern int g_checks;

#define CHECK(cond)                                                                \
    do {                                                                           \
        ++g_checks;                                                                \
        if (!(cond)) {                                                             \
            std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond);            \
            ++g_failures;                                                          \
        }                                                                          \
    } while (0)

#define CHECK_NEAR(a, b, eps)                                                      \
    do {                                                                           \
        ++g_checks;                                                                \
        const double _a = (double)(a), _b = (double)(b);                           \
        if (!(std::fabs(_a - _b) <= (double)(eps))) {                              \
            std::printf("FAIL %s:%d  %s ~= %s  (%.9g vs %.9g)\n", __FILE__,        \
                        __LINE__, #a, #b, _a, _b);                                 \
            ++g_failures;                                                          \
        }                                                                          \
    } while (0)

void test_abi_layout();
void test_frames();
void test_robustness();
void test_queues();
void test_dsp_stop();
void test_onset();
void test_standby();
void test_smoke_verdict();
void test_devices();

namespace harness {

inline constexpr int64_t kTicksPerMs = 10000;
inline constexpr int64_t kTicksPerSec = 10000000;
// A non-zero virtual origin, so "offset 0" can never be confused with "time 0".
inline constexpr int64_t kStart = 10 * kTicksPerSec * 100;   // 1000 s

// One fake session plus everything read out of it so far.
struct Fake {
    void*   s = nullptr;
    int64_t t0 = kStart;     // session origin
    int64_t t = kStart;      // current virtual time
    std::vector<WfFrame> frames;
    std::vector<WfEvent> events;

    ~Fake() { destroy(); }

    bool create(uint32_t others_mode = WF_OTHERS_SYSTEM_EXCLUDE, double base_duration = 0.0,
                uint32_t target_pid = 0, uint32_t enable_aec = 1,
                const wchar_t* mic_device_id = nullptr,
                const wchar_t* render_device_id = nullptr) {
        WfSessionConfig cfg;
        std::memset(&cfg, 0, sizeof(cfg));
        cfg.size = sizeof(cfg);
        cfg.others_mode = others_mode;
        cfg.target_pid = target_pid;
        cfg.ns_level = 2;
        cfg.enable_aec = enable_aec;
        cfg.prefer_raw = 1;
        cfg.base_duration_sec = base_duration;
        for (size_t i = 0; mic_device_id && mic_device_id[i] && i + 1 < 256; ++i) {
            cfg.mic_device_id[i] = mic_device_id[i];
        }
        for (size_t i = 0; render_device_id && render_device_id[i] && i + 1 < 256; ++i) {
            cfg.render_device_id[i] = render_device_id[i];
        }
        if (wf_session_create_fake(&cfg, &s) != WF_OK) return false;
        t0 = kStart;
        t = kStart;
        return wf_fake_set_now(s, t) == WF_OK;
    }

    void destroy() {
        if (!s) return;
        wf_session_destroy(s);
        s = nullptr;
    }

    bool start() { return wf_session_start(s) == WF_OK; }
    int32_t stop() {
        const int32_t rc = wf_session_stop(s);
        drain();
        return rc;
    }

    void tick() {
        wf_fake_tick(s);
        drain();
    }

    // Ticks without emptying the output queues - used to make them overflow on purpose.
    void tick_raw() { wf_fake_tick(s); }

    void set_now(int64_t when) {
        t = when;
        wf_fake_set_now(s, t);
    }

    // Moves the virtual clock in 10 ms steps, ticking at every step.
    void advance_ms(int ms) {
        for (int i = 0; i < ms / 10; ++i) {
            set_now(t + 10 * kTicksPerMs);
            tick();
        }
    }

    int32_t push(uint32_t source, const float* samples, uint32_t n, uint32_t rate, int64_t ts,
                 uint32_t flags = 0, uint64_t device_position = 0) {
        return wf_fake_push(s, source, samples, n, rate, ts, device_position, flags);
    }

    int32_t signal(uint32_t sig, uint32_t source = WF_FAKE_SRC_MIC, int32_t hr = 0) {
        return wf_fake_signal(s, sig, source, hr);
    }

    void drain() {
        WfFrame fbuf[256];
        for (;;) {
            uint32_t n = 0;
            if (wf_session_read_frames(s, fbuf, 256, &n) != WF_OK || n == 0) break;
            frames.insert(frames.end(), fbuf, fbuf + n);
            if (n < 256) break;
        }
        WfEvent ebuf[128];
        for (;;) {
            uint32_t n = 0;
            if (wf_session_read_events(s, ebuf, 128, &n) != WF_OK || n == 0) break;
            events.insert(events.end(), ebuf, ebuf + n);
            if (n < 128) break;
        }
    }

    WfMetrics metrics() {
        WfMetrics m;
        std::memset(&m, 0, sizeof(m));
        wf_session_get_metrics(s, &m);
        return m;
    }
    WfDiagnostics diagnostics() {
        WfDiagnostics d;
        std::memset(&d, 0, sizeof(d));
        d.size = sizeof(d);
        wf_session_get_diagnostics(s, &d);
        return d;
    }
    // wf_fake_query: the endpoint id WF_FAKE_QUERY_* runs on ("" when none), its counter.
    std::wstring query(uint32_t what, uint32_t* value = nullptr) {
        wchar_t text[256] = {};
        uint32_t v = 0;
        if (wf_fake_query(s, what, text, 256, &v) != WF_OK) return L"<error>";
        if (value) *value = v;
        return std::wstring(text);
    }
    WfLevels levels() {
        WfLevels l;
        std::memset(&l, 0, sizeof(l));
        wf_session_get_levels(s, &l);
        return l;
    }

    int count(uint32_t kind) const {
        int n = 0;
        for (const WfEvent& e : events) {
            if (e.kind == kind) ++n;
        }
        return n;
    }
    int count(uint32_t kind, uint32_t source) const {
        int n = 0;
        for (const WfEvent& e : events) {
            if (e.kind == kind && e.source == source) ++n;
        }
        return n;
    }
    int count_gap(uint32_t reason) const {
        int n = 0;
        for (const WfEvent& e : events) {
            if (e.kind == WF_EVENT_GAP && e.reason == reason) ++n;
        }
        return n;
    }
    // WF_GAP_NONE on every non-gap event, a real WF_GAP_* (0..6) on every gap.
    bool reasons_ok() const {
        for (const WfEvent& e : events) {
            if (e.kind == WF_EVENT_GAP ? e.reason > WF_GAP_PROCESSING_OVERFLOW
                                       : e.reason != WF_GAP_NONE) {
                return false;
            }
        }
        return true;
    }
    const WfEvent* first(uint32_t kind) const {
        for (const WfEvent& e : events) {
            if (e.kind == kind) return &e;
        }
        return nullptr;
    }
    // Index of the first event of `kind` at or after `from`, or -1.
    int index_of(uint32_t kind, size_t from = 0) const {
        for (size_t i = from; i < events.size(); ++i) {
            if (events[i].kind == kind) return static_cast<int>(i);
        }
        return -1;
    }
    size_t frames_of(uint32_t source) const {
        size_t n = 0;
        for (const WfFrame& f : frames) {
            if (f.source == source) ++n;
        }
        return n;
    }
    double offset_of(int64_t when) const {
        return static_cast<double>(when - t0) / static_cast<double>(kTicksPerSec);
    }
};

// Deterministic test signal: the value of absolute input sample `p`.
inline float ramp(int64_t p) {
    return static_cast<float>(((p % 2000) + 2000) % 2000) * 1e-4f;   // 0 .. 0.1999
}

}  // namespace harness
