// Pins the binary layout of every ABI struct. The .NET mirror must assert exactly the
// same numbers; they are printed so they can be copied into the managed test.
//
// A static_assert here is a compile error, not a test failure, which is deliberate: a
// layout change must never be able to reach a build that the managed side then loads.
#include <cstddef>
#include <vector>

#include "test_util.h"

namespace {

#define PIN(type, field, off)                                                               \
    static_assert(offsetof(type, field) == (off), #type "." #field " moved")

// ---- WfDeviceInfo (800 bytes) ---------------------------------------------
static_assert(sizeof(wchar_t) == 2, "the ABI assumes 16-bit wchar_t (Windows)");
static_assert(sizeof(WfDeviceInfo) == 800, "WfDeviceInfo size changed");
static_assert(alignof(WfDeviceInfo) == 4, "WfDeviceInfo alignment changed");
PIN(WfDeviceInfo, flow, 0);
PIN(WfDeviceInfo, state, 4);
PIN(WfDeviceInfo, mix_rate, 8);
PIN(WfDeviceInfo, mix_channels, 12);
PIN(WfDeviceInfo, is_default_console, 16);
PIN(WfDeviceInfo, is_default_communications, 20);
PIN(WfDeviceInfo, raw_supported, 24);
PIN(WfDeviceInfo, reserved, 28);
PIN(WfDeviceInfo, id, 32);
PIN(WfDeviceInfo, name, 544);

// ---- WfAudioSessionInfo (1048 bytes) ---------------------------------------
static_assert(sizeof(WfAudioSessionInfo) == 1048, "WfAudioSessionInfo size changed");
PIN(WfAudioSessionInfo, pid, 0);
PIN(WfAudioSessionInfo, parent_pid, 4);
PIN(WfAudioSessionInfo, state, 8);
PIN(WfAudioSessionInfo, is_render, 12);
PIN(WfAudioSessionInfo, is_system_sounds, 16);
PIN(WfAudioSessionInfo, peak, 20);
PIN(WfAudioSessionInfo, process_name, 24);
PIN(WfAudioSessionInfo, display_name, 280);
PIN(WfAudioSessionInfo, endpoint_id, 536);

// ---- WfSessionConfig (1072 bytes) ------------------------------------------
static_assert(sizeof(WfSessionConfig) == 1072, "WfSessionConfig size changed");
static_assert(alignof(WfSessionConfig) == 8, "WfSessionConfig alignment changed");
PIN(WfSessionConfig, size, 0);
PIN(WfSessionConfig, others_mode, 4);
PIN(WfSessionConfig, target_pid, 8);
PIN(WfSessionConfig, ns_level, 12);
PIN(WfSessionConfig, enable_aec, 16);
PIN(WfSessionConfig, prefer_raw, 20);
PIN(WfSessionConfig, allow_device_loopback_fallback, 24);
PIN(WfSessionConfig, keepalive_mode, 28);
PIN(WfSessionConfig, timestamp_mode, 32);
PIN(WfSessionConfig, reserved0, 36);
PIN(WfSessionConfig, base_duration_sec, 40);
PIN(WfSessionConfig, mic_device_id, 48);
PIN(WfSessionConfig, render_device_id, 560);

// ---- WfFrame (672 bytes) ----------------------------------------------------
static_assert(sizeof(WfFrame) == 672, "WfFrame size changed");
static_assert(alignof(WfFrame) == 8, "WfFrame alignment changed");
PIN(WfFrame, source, 0);
PIN(WfFrame, flags, 4);
PIN(WfFrame, attempt_id, 8);
PIN(WfFrame, valid_lo, 12);          // carved out of the former uint32_t reserved
PIN(WfFrame, valid_hi, 14);
PIN(WfFrame, timeline_sample, 16);
PIN(WfFrame, offset_sec, 24);
PIN(WfFrame, samples, 32);
static_assert(sizeof(WfFrame::valid_lo) == 2 && sizeof(WfFrame::valid_hi) == 2,
              "WfFrame valid range fields are uint16_t");
static_assert(sizeof(WfFrame::samples) == WF_FRAME_SAMPLES * sizeof(float), "frame length");

// ---- WF_GAP_* values (the managed enum mirrors them) -------------------------
static_assert(WF_GAP_INBOX_OVERFLOW == 0u && WF_GAP_TIMESTAMP_DISCONTINUITY == 1u &&
                  WF_GAP_LATE_FRAME == 2u && WF_GAP_DEVICE_LOST == 3u &&
                  WF_GAP_RESTART == 4u && WF_GAP_WATCHDOG_RECOVERY == 5u &&
                  WF_GAP_PROCESSING_OVERFLOW == 6u && WF_GAP_NONE == 7u,
              "WF_GAP_* values changed");
static_assert(WF_E_INVALID_ARG == -1 && WF_E_STATE == -2 && WF_E_NOT_FAKE == -3 &&
                  WF_E_FAIL == -4 && WF_E_BUFFER_TOO_SMALL == -5 && WF_E_UNSUPPORTED == -6,
              "WF_E_* values changed");

// ---- WfEvent (296 bytes) ----------------------------------------------------
static_assert(sizeof(WfEvent) == 296, "WfEvent size changed");
static_assert(alignof(WfEvent) == 8, "WfEvent alignment changed");
PIN(WfEvent, kind, 0);
PIN(WfEvent, source, 4);
PIN(WfEvent, attempt_id, 8);
PIN(WfEvent, reason, 12);
PIN(WfEvent, hresult, 16);
PIN(WfEvent, sequence, 20);
PIN(WfEvent, at_offset_sec, 24);
PIN(WfEvent, duration_sec, 32);
PIN(WfEvent, detail, 40);

// ---- WfLevels (16 bytes) ----------------------------------------------------
static_assert(sizeof(WfLevels) == 16, "WfLevels size changed");
PIN(WfLevels, me, 0);
PIN(WfLevels, others, 4);
PIN(WfLevels, me_active, 8);
PIN(WfLevels, others_active, 12);

// ---- WfMetrics (176 bytes) --------------------------------------------------
static_assert(sizeof(WfMetrics) == 176, "WfMetrics size changed");
static_assert(alignof(WfMetrics) == 8, "WfMetrics alignment changed");
PIN(WfMetrics, erle_db, 0);
PIN(WfMetrics, drift_ppm_me, 8);
PIN(WfMetrics, drift_ppm_others, 16);
PIN(WfMetrics, jitter_p95_ms_me, 24);
PIN(WfMetrics, jitter_p95_ms_others, 32);
PIN(WfMetrics, unfiltered_frames, 40);
PIN(WfMetrics, late_dropped, 48);
PIN(WfMetrics, ref_discarded, 56);
PIN(WfMetrics, inbox_overflows, 64);
PIN(WfMetrics, timestamp_errors, 72);
PIN(WfMetrics, discontinuities, 80);
PIN(WfMetrics, watchdog_recoveries, 88);
PIN(WfMetrics, restarts, 96);
PIN(WfMetrics, frames_me, 104);
PIN(WfMetrics, frames_others, 112);
PIN(WfMetrics, frames_dropped, 120);
PIN(WfMetrics, events_dropped, 128);
PIN(WfMetrics, aec_resets, 136);
PIN(WfMetrics, ring_overflows, 144);
PIN(WfMetrics, delay_ms, 152);
PIN(WfMetrics, state, 156);
PIN(WfMetrics, mic_raw, 160);
PIN(WfMetrics, ref_mode_actual, 164);
PIN(WfMetrics, attempt_id, 168);
PIN(WfMetrics, orphaned_steps, 172);

// ---- WfDiagnostics (88 bytes, additive within v1, versioned by `size`) -------
// WfMetrics above can never grow (no size field, written whole); new counters go here.
// 72 bytes until 2026-11-23 (AP3 appended four uint32 at 72..87; the prefix is unchanged).
static_assert(sizeof(WfDiagnostics) == 88, "WfDiagnostics size changed (append only!)");
static_assert(alignof(WfDiagnostics) == 8, "WfDiagnostics alignment changed");
PIN(WfDiagnostics, size, 0);
PIN(WfDiagnostics, power_request, 4);
PIN(WfDiagnostics, dsp_tick_gap_max_ms, 8);
PIN(WfDiagnostics, capture_gap_max_ms_me, 16);
PIN(WfDiagnostics, capture_gap_max_ms_others, 24);
PIN(WfDiagnostics, capture_gap_max_ms_reference, 32);
PIN(WfDiagnostics, ring_high_water_me, 40);
PIN(WfDiagnostics, ring_high_water_others, 44);
PIN(WfDiagnostics, ring_high_water_reference, 48);
PIN(WfDiagnostics, ring_capacity_slots, 52);
PIN(WfDiagnostics, dsp_slices_max, 56);
PIN(WfDiagnostics, reserved0, 60);
PIN(WfDiagnostics, ring_loss_gaps, 64);
PIN(WfDiagnostics, render_switches, 72);       // AP3, appended 2026-11-23
PIN(WfDiagnostics, aec_render_resets, 76);
PIN(WfDiagnostics, keepalive_failures, 80);
PIN(WfDiagnostics, keepalive_starts, 84);
static_assert(WF_POWER_REQUEST_NONE == 0u && WF_POWER_REQUEST_HELD == 1u &&
                  WF_POWER_REQUEST_FAILED == 2u,
              "WF_POWER_REQUEST_* values changed");
static_assert(WF_FAKE_SIGNAL_SESSION_START_FAILS == 11u, "fake signal value changed");
// AP3 additions (2026-11-23).
static_assert(WF_EVENT_ERROR == 17u && WF_EVENT_WARNING == 18u, "WF_EVENT_* values changed");
static_assert(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED == 12u &&
                  WF_FAKE_SIGNAL_DEFAULT_COMMS_RENDER_CHANGED == 13u &&
                  WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE == 14u &&
                  WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED == 15u &&
                  WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED == 16u &&
                  WF_FAKE_SIGNAL_KEEPALIVE_START_FAILS == 17u &&
                  WF_FAKE_SIGNAL_KEEPALIVE_EXITS == 18u,
              "fake signal values changed");
static_assert(WF_FAKE_QUERY_LOOPBACK == 1u && WF_FAKE_QUERY_KEEPALIVE == 2u &&
                  WF_FAKE_QUERY_MIC == 3u,
              "fake query values changed");

#undef PIN

}  // namespace

void test_abi_layout() {
    std::printf("--- ABI layout (mirror these in the .NET side) ---\n");
    std::printf("  sizeof(wchar_t)          = %zu\n", sizeof(wchar_t));
    std::printf("  sizeof(WfDeviceInfo)       = %4zu  align %zu\n", sizeof(WfDeviceInfo),
                alignof(WfDeviceInfo));
    std::printf("  sizeof(WfAudioSessionInfo) = %4zu  align %zu\n", sizeof(WfAudioSessionInfo),
                alignof(WfAudioSessionInfo));
    std::printf("  sizeof(WfSessionConfig)    = %4zu  align %zu\n", sizeof(WfSessionConfig),
                alignof(WfSessionConfig));
    std::printf("  sizeof(WfFrame)            = %4zu  align %zu  (valid_lo @%zu, valid_hi @%zu)\n",
                sizeof(WfFrame), alignof(WfFrame), offsetof(WfFrame, valid_lo),
                offsetof(WfFrame, valid_hi));
    std::printf("  sizeof(WfEvent)            = %4zu  align %zu\n", sizeof(WfEvent),
                alignof(WfEvent));
    std::printf("  sizeof(WfLevels)           = %4zu  align %zu\n", sizeof(WfLevels),
                alignof(WfLevels));
    std::printf("  sizeof(WfMetrics)          = %4zu  align %zu\n", sizeof(WfMetrics),
                alignof(WfMetrics));
    std::printf("  sizeof(WfDiagnostics)      = %4zu  align %zu\n", sizeof(WfDiagnostics),
                alignof(WfDiagnostics));

    // The ABI contract itself.
    CHECK(wf_abi_version() == WF_ABI_VERSION);
    uint32_t caps = 0xFFFFFFFFu;
    CHECK(wf_capabilities(&caps) == WF_OK);
    CHECK((caps & ~(WF_CAP_PROCESS_LOOPBACK | WF_CAP_AEC3 | WF_CAP_FAKE)) == 0);
    CHECK((caps & WF_CAP_FAKE) != 0);
    CHECK(wf_capabilities(nullptr) == WF_E_INVALID_ARG);
    std::printf("  capabilities = 0x%x\n", caps);

    // wf_devices_list must report the required size when the buffer is too small.
    uint32_t count = 0;
    const int32_t rc = wf_devices_list(WF_FLOW_CAPTURE, nullptr, 0, &count);
    CHECK(rc == WF_E_BUFFER_TOO_SMALL || rc == WF_OK);
    std::printf("  capture endpoints = %u\n", count);
    CHECK(wf_devices_list(WF_FLOW_CAPTURE, nullptr, 0, nullptr) == WF_E_INVALID_ARG);
    CHECK(wf_devices_list(99u, nullptr, 0, &count) == WF_E_INVALID_ARG);
    // A null buffer with a non-zero capacity is a caller bug, not a size query, and it is
    // refused before the enumeration runs; *count stays untouched.
    uint32_t untouched = 77u;
    CHECK(wf_devices_list(WF_FLOW_CAPTURE, nullptr, 4, &untouched) == WF_E_INVALID_ARG);
    CHECK(untouched == 77u);
    CHECK(wf_audio_sessions_list(WF_FLOW_ALL, nullptr, 4, &untouched) == WF_E_INVALID_ARG);
    CHECK(untouched == 77u);
    CHECK(wf_audio_sessions_list(WF_FLOW_ALL, nullptr, 0, nullptr) == WF_E_INVALID_ARG);
    if (count > 0) {
        std::vector<WfDeviceInfo> devices(count);
        uint32_t filled = 0;
        CHECK(wf_devices_list(WF_FLOW_CAPTURE, devices.data(), count, &filled) == WF_OK);
        CHECK(filled == count);
        CHECK(devices[0].id[0] != 0);
        CHECK(devices[0].flow == WF_FLOW_CAPTURE);
        // One entry short must be refused, with the required size reported.
        uint32_t too_small = 0;
        CHECK(wf_devices_list(WF_FLOW_CAPTURE, devices.data(), count - 1, &too_small) ==
              WF_E_BUFFER_TOO_SMALL);
        CHECK(too_small == count);
    }
    uint32_t sessions = 0;
    const int32_t src = wf_audio_sessions_list(WF_FLOW_ALL, nullptr, 0, &sessions);
    CHECK(src == WF_E_BUFFER_TOO_SMALL || src == WF_OK);
    if (sessions > 0) {
        std::vector<WfAudioSessionInfo> list(sessions);
        uint32_t filled = 0;
        CHECK(wf_audio_sessions_list(WF_FLOW_ALL, list.data(), sessions, &filled) == WF_OK);
        CHECK(filled == sessions);
    }
    std::printf("  audio sessions   = %u\n", sessions);

    // Config validation: `size` is the versioning gate.
    WfSessionConfig cfg;
    std::memset(&cfg, 0, sizeof(cfg));
    void* s = nullptr;
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_INVALID_ARG);   // size == 0
    cfg.size = sizeof(cfg) - 4;
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_INVALID_ARG);
    cfg.size = sizeof(cfg);
    cfg.others_mode = 7;
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_INVALID_ARG);
    cfg.others_mode = WF_OTHERS_PROCESS_INCLUDE;                   // needs a target pid
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_INVALID_ARG);
    cfg.others_mode = WF_OTHERS_SYSTEM_EXCLUDE;
    cfg.ns_level = 9;
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_INVALID_ARG);
    cfg.ns_level = 2;
    cfg.keepalive_mode = 5;
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_INVALID_ARG);
    cfg.keepalive_mode = WF_KEEPALIVE_CLOCK;     // reserved, deliberately not built
    CHECK(wf_session_create_fake(&cfg, &s) == WF_E_UNSUPPORTED);
    cfg.keepalive_mode = WF_KEEPALIVE_NONE;
    CHECK(wf_session_create_fake(&cfg, &s) == WF_OK);
    CHECK(s != nullptr);
    CHECK(wf_session_event_handle(s) != nullptr);
    // The fake entry points are rejected on a real session, and vice versa.
    CHECK(wf_fake_tick(s) == WF_E_STATE);      // not started yet

    // Reads: null buffer + capacity > 0 is WF_E_INVALID_ARG; capacity 0 is a valid no-op.
    WfFrame one_frame;
    WfEvent one_event;
    uint32_t n = 77u;
    CHECK(wf_session_read_frames(s, nullptr, 1, &n) == WF_E_INVALID_ARG);
    CHECK(wf_session_read_events(s, nullptr, 1, &n) == WF_E_INVALID_ARG);
    CHECK(n == 77u);
    CHECK(wf_session_read_frames(s, nullptr, 0, &n) == WF_OK && n == 0);
    n = 77u;
    CHECK(wf_session_read_events(s, nullptr, 0, &n) == WF_OK && n == 0);
    CHECK(wf_session_read_frames(s, &one_frame, 1, nullptr) == WF_E_INVALID_ARG);
    CHECK(wf_session_read_events(s, &one_event, 1, nullptr) == WF_E_INVALID_ARG);
    CHECK(wf_session_read_frames(nullptr, &one_frame, 1, &n) == WF_E_INVALID_ARG);
    CHECK(wf_session_read_events(nullptr, &one_event, 1, &n) == WF_E_INVALID_ARG);
    CHECK(wf_session_read_frames(s, &one_frame, 1, &n) == WF_OK && n == 0);   // empty queue

    // wf_session_get_diagnostics: size-versioned. Too small or null is refused; a prefix
    // size gets exactly that prefix (an older caller), the full size gets everything.
    WfDiagnostics diag;
    std::memset(&diag, 0xAB, sizeof(diag));
    diag.size = 4;
    CHECK(wf_session_get_diagnostics(s, &diag) == WF_E_INVALID_ARG);
    CHECK(wf_session_get_diagnostics(s, nullptr) == WF_E_INVALID_ARG);
    CHECK(wf_session_get_diagnostics(nullptr, &diag) == WF_E_INVALID_ARG);
    diag.size = 16;
    CHECK(wf_session_get_diagnostics(s, &diag) == WF_OK);
    CHECK(diag.size == 16);
    CHECK(diag.power_request == WF_POWER_REQUEST_NONE);        // not started
    CHECK(diag.dsp_tick_gap_max_ms == 0.0);
    const unsigned char* raw = reinterpret_cast<const unsigned char*>(&diag);
    bool prefix_only = true;
    for (size_t i = 16; i < sizeof(diag); ++i) {
        if (raw[i] != 0xAB) prefix_only = false;
    }
    CHECK(prefix_only);
    diag.size = sizeof(diag) + 64;                             // a newer caller
    CHECK(wf_session_get_diagnostics(s, &diag) == WF_OK);
    CHECK(diag.size == sizeof(WfDiagnostics));
    CHECK(diag.ring_capacity_slots == 1024);
    CHECK(diag.reserved0 == 0);
    uint32_t held = 99;
    CHECK(wf_power_requests_active(nullptr) == WF_E_INVALID_ARG);
    CHECK(wf_power_requests_active(&held) == WF_OK);
    CHECK(held == 0);

    CHECK(wf_session_destroy(s) == WF_OK);
    CHECK(wf_session_destroy(nullptr) == WF_E_INVALID_ARG);
    CHECK(wf_session_start(nullptr) == WF_E_INVALID_ARG);
}
