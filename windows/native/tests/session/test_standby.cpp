// Standby protection and recording-health diagnostics (AP4, 2026-11-23).
//
// test_power_request_lifecycle: a session holds exactly one power request while it runs and
// none after stop, destroy or a failed start - never across sessions. A fake session's
// request is a counted no-op (no Win32 call), so this runs without touching the machine's
// power state; wf_power_requests_active is the process-wide count of the DLL.
//
// test_diagnostics: WfDiagnostics' tick gap, capture gap and ring high-water numbers in
// virtual time, and that reading them never resets them.
#include "test_util.h"

using harness::Fake;
using harness::kTicksPerMs;

namespace {

constexpr uint32_t kRate = 16000;
constexpr int kBlock = 160;
constexpr int64_t kBlockTicks = 100000;

uint32_t active_requests() {
    uint32_t n = 0xFFFFFFFFu;
    CHECK(wf_power_requests_active(&n) == WF_OK);
    return n;
}

WfDiagnostics diagnostics(void* s) {
    WfDiagnostics d;
    std::memset(&d, 0, sizeof(d));
    d.size = sizeof(d);
    CHECK(wf_session_get_diagnostics(s, &d) == WF_OK);
    return d;
}

void push_both(Fake& f, int64_t n) {
    float buf[kBlock] = {};
    f.push(WF_FAKE_SRC_MIC, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
    f.push(WF_FAKE_SRC_OTHERS, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
}

void test_power_request_lifecycle() {
    const uint32_t base = active_requests();
    CHECK(base == 0);

    // Running: exactly one; a second start neither fails over nor takes a second one.
    {
        Fake f;
        CHECK(f.create());
        CHECK(active_requests() == base);                 // created is not running
        CHECK(diagnostics(f.s).power_request == WF_POWER_REQUEST_NONE);
        CHECK(f.start());
        CHECK(active_requests() == base + 1);
        CHECK(diagnostics(f.s).power_request == WF_POWER_REQUEST_HELD);
        CHECK(wf_session_start(f.s) == WF_E_STATE);
        CHECK(active_requests() == base + 1);
        for (int64_t n = 0; n < 5; ++n) {
            push_both(f, n);
            f.set_now(f.t0 + (n + 1) * kBlockTicks);
            f.tick();
        }
        CHECK(active_requests() == base + 1);
        CHECK(f.stop() == WF_OK);
        CHECK(active_requests() == base);                 // stop releases it
        CHECK(diagnostics(f.s).power_request == WF_POWER_REQUEST_NONE);
        CHECK(f.stop() == WF_OK);                         // idempotent, nothing more to free
        CHECK(active_requests() == base);
        f.destroy();
        CHECK(active_requests() == base);
    }
    // Destroyed while running, without a stop.
    {
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        CHECK(active_requests() == base + 1);
        f.destroy();
        CHECK(active_requests() == base);
    }
    // A start that fails after taking the request gives it back at once.
    {
        Fake f;
        CHECK(f.create());
        CHECK(f.signal(WF_FAKE_SIGNAL_SESSION_START_FAILS) == WF_OK);
        CHECK(wf_session_start(f.s) == WF_E_FAIL);
        CHECK(active_requests() == base);
        CHECK(diagnostics(f.s).power_request == WF_POWER_REQUEST_NONE);
        f.destroy();
        CHECK(active_requests() == base);
    }
    // Two sessions are two requests; neither leaks into the other.
    {
        Fake a, b;
        CHECK(a.create() && b.create());
        CHECK(a.start() && b.start());
        CHECK(active_requests() == base + 2);
        CHECK(a.stop() == WF_OK);
        CHECK(active_requests() == base + 1);
        CHECK(diagnostics(b.s).power_request == WF_POWER_REQUEST_HELD);
        b.destroy();
        CHECK(active_requests() == base);
        a.destroy();
        CHECK(active_requests() == base);
    }
    // A real session that was never started holds nothing either (no Win32 call made).
    {
        WfSessionConfig cfg;
        std::memset(&cfg, 0, sizeof(cfg));
        cfg.size = sizeof(cfg);
        cfg.ns_level = 2;
        cfg.enable_aec = 1;
        cfg.allow_device_loopback_fallback = 1;
        void* s = nullptr;
        if (wf_session_create(&cfg, &s) == WF_OK) {
            CHECK(diagnostics(s).power_request == WF_POWER_REQUEST_NONE);
            CHECK(active_requests() == base);
            CHECK(wf_session_destroy(s) == WF_OK);
        }
        CHECK(active_requests() == base);
    }
    std::printf("  power request: 1 while running; 0 after stop, destroy, failed start\n");
}

void test_diagnostics() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    for (int i = 0; i < 20; ++i) {                 // 200 ms healthy, 10 ms ticks
        push_both(f, n++);
        f.set_now(f.t0 + n * kBlockTicks);
        f.tick();
    }
    WfDiagnostics d = diagnostics(f.s);
    CHECK_NEAR(d.dsp_tick_gap_max_ms, 10.0, 1e-9);
    CHECK_NEAR(d.capture_gap_max_ms_me, 10.0, 1e-9);
    CHECK_NEAR(d.capture_gap_max_ms_others, 10.0, 1e-9);
    CHECK(d.capture_gap_max_ms_reference == 0.0);   // no separate reference in mode 0
    CHECK(d.ring_high_water_me == 1);
    CHECK(d.dsp_slices_max == 1);

    // 300 ms without a DSP tick while the capture keeps delivering; then a 200 ms pause of
    // the mic stream alone (the DSP keeps ticking).
    for (int i = 0; i < 30; ++i) {
        push_both(f, n++);
        f.set_now(f.t0 + n * kBlockTicks);
    }
    f.tick();
    for (int i = 0; i < 20; ++i) {
        float buf[kBlock] = {};
        f.push(WF_FAKE_SRC_OTHERS, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
        ++n;
        f.set_now(f.t0 + n * kBlockTicks);
        f.tick();
    }
    push_both(f, n++);
    f.set_now(f.t0 + n * kBlockTicks);
    f.tick();

    d = diagnostics(f.s);
    CHECK_NEAR(d.dsp_tick_gap_max_ms, 300.0, 1e-9);
    CHECK_NEAR(d.capture_gap_max_ms_me, 210.0, 1e-9);
    CHECK_NEAR(d.capture_gap_max_ms_others, 10.0, 1e-9);
    CHECK(d.ring_high_water_me == 30);
    CHECK(d.ring_high_water_others == 30);
    CHECK(d.dsp_slices_max == 3);
    // Reading does not reset, and the numbers survive the stop.
    CHECK(f.stop() == WF_OK);
    const WfDiagnostics again = diagnostics(f.s);
    CHECK(again.dsp_tick_gap_max_ms >= d.dsp_tick_gap_max_ms);
    CHECK(again.ring_high_water_me == 30);
    CHECK(again.capture_gap_max_ms_me == d.capture_gap_max_ms_me);
    std::printf("  diagnostics: tick gap %.0f ms, capture gap me %.0f ms, high water %u, "
                "slices %u\n",
                d.dsp_tick_gap_max_ms, d.capture_gap_max_ms_me, d.ring_high_water_me,
                d.dsp_slices_max);
}

}  // namespace

void test_standby() {
    test_power_request_lifecycle();
    test_diagnostics();
}
