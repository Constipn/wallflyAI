// wf_hw_smoke: one real recording through the public ABI, on whatever hardware the
// machine has. Not registered with ctest - it needs a microphone and a render endpoint,
// so it is run by hand and its numbers go into the phase report.
//
//   wf_hw_smoke [seconds] [--mode system|include|device] [--pid N] [--expect-audio]
//
// seconds defaults to 5; --mode selects WfSessionConfig.others_mode (system = 0, process
// loopback excluding our own tree; include = 1, process loopback of --pid's tree; device =
// 2, classic loopback of the default render endpoint); --expect-audio says something
// audible plays on Others during the run, so a silent Others track fails.
//
// The verdict is smoke_verdict.h (tested in tests/session/test_smoke_verdict.cpp): ~100 Me
// and ~100 Others frames per second, gaps <= 0.1 % of the run, no watchdog recovery or
// restart, no overflow, wall clock not ahead of the session clock (standby), no error
// events, no drops. Exit code 0 = SMOKE OK, 1 = SMOKE FAIL, 2 = usage / setup error.
#include "api/wallfly_audio.h"

#include <windows.h>

#include <cstdio>
#include <cstdlib>
#include <cstring>
#include <string>
#include <vector>

#include "smoke_verdict.h"

namespace {

const char* event_name(uint32_t kind) {
    switch (kind) {
        case WF_EVENT_DEVICE_ACQUIRED: return "DeviceAcquired";
        case WF_EVENT_DEVICE_LOST: return "DeviceLost";
        case WF_EVENT_DEVICE_SWITCHED: return "DeviceSwitched";
        case WF_EVENT_FORMAT_CHANGED: return "FormatChanged";
        case WF_EVENT_GAP: return "Gap";
        case WF_EVENT_DEGRADED: return "Degraded";
        case WF_EVENT_REFERENCE_MISSING: return "ReferenceMissing";
        case WF_EVENT_REFERENCE_RESTORED: return "ReferenceRestored";
        case WF_EVENT_WATCHDOG_RECOVERY: return "WatchdogRecovery";
        case WF_EVENT_RESTARTING: return "Restarting";
        case WF_EVENT_PERMISSION_DENIED: return "PermissionDenied";
        case WF_EVENT_DRIVER_BLOCKED: return "DriverBlocked";
        case WF_EVENT_FALLBACK_DEVICE_LOOPBACK: return "FallbackDeviceLoopback";
        case WF_EVENT_CLOCK_JITTER_WARNING: return "ClockJitterWarning";
        case WF_EVENT_OTHERS_FAILED: return "OthersFailed";
        case WF_EVENT_STOPPED: return "Stopped";
        case WF_EVENT_ERROR: return "Error";
        case WF_EVENT_WARNING: return "Warning";
        default: return "?";
    }
}

const char* gap_name(uint32_t reason) {
    switch (reason) {
        case WF_GAP_INBOX_OVERFLOW: return "InboxOverflow";
        case WF_GAP_TIMESTAMP_DISCONTINUITY: return "TimestampDiscontinuity";
        case WF_GAP_LATE_FRAME: return "LateFrame";
        case WF_GAP_DEVICE_LOST: return "DeviceLost";
        case WF_GAP_RESTART: return "Restart";
        case WF_GAP_WATCHDOG_RECOVERY: return "WatchdogRecovery";
        case WF_GAP_PROCESSING_OVERFLOW: return "ProcessingOverflow";
        case WF_GAP_NONE: return "-";
        default: return "?";
    }
}

const char* state_name(uint32_t s) {
    switch (s) {
        case WF_STATE_IDLE: return "Idle";
        case WF_STATE_STARTING: return "Starting";
        case WF_STATE_RUNNING: return "Running";
        case WF_STATE_DEGRADED: return "Degraded";
        case WF_STATE_DRIVER_BLOCKED: return "DriverBlocked";
        case WF_STATE_OTHERS_FAILED: return "OthersFailed";
        case WF_STATE_STOPPING: return "Stopping";
        case WF_STATE_STOPPED: return "Stopped";
        default: return "?";
    }
}

struct Channel {
    unsigned long long frames = 0;
    double first = -1.0;
    double last = 0.0;
    double peak = 0.0;
    unsigned long long unfiltered = 0;
};

void note(Channel& c, const WfFrame& f) {
    ++c.frames;
    if (c.first < 0.0) c.first = f.offset_sec;
    c.last = f.offset_sec;
    if (f.flags & WF_FRAME_UNFILTERED) ++c.unfiltered;
    for (unsigned i = 0; i < WF_FRAME_SAMPLES; ++i) {
        const double v = f.samples[i] < 0 ? -f.samples[i] : f.samples[i];
        if (v > c.peak) c.peak = v;
    }
}

const char* power_name(uint32_t p) {
    switch (p) {
        case WF_POWER_REQUEST_NONE: return "none";
        case WF_POWER_REQUEST_HELD: return "held";
        case WF_POWER_REQUEST_FAILED: return "FAILED";
        default: return "?";
    }
}

double wall_now_sec() {
    FILETIME ft;
    GetSystemTimePreciseAsFileTime(&ft);
    ULARGE_INTEGER u;
    u.LowPart = ft.dwLowDateTime;
    u.HighPart = ft.dwHighDateTime;
    return static_cast<double>(u.QuadPart) / 1e7;
}

// The session clock's unit: QPC in 100 ns (wf_session_get_origin).
int64_t qpc_now_100ns() {
    LARGE_INTEGER c, f;
    QueryPerformanceCounter(&c);
    QueryPerformanceFrequency(&f);
    return static_cast<int64_t>(static_cast<double>(c.QuadPart) * 1e7 /
                                static_cast<double>(f.QuadPart));
}

void usage() {
    std::printf("usage: wf_hw_smoke [seconds] [--mode system|include|device] [--pid N] "
                "[--expect-audio]\n");
}

}  // namespace

int main(int argc, char** argv) {
    unsigned seconds = 5u;
    uint32_t others_mode = WF_OTHERS_SYSTEM_EXCLUDE;
    wf_smoke::Mode mode = wf_smoke::Mode::System;
    uint32_t pid = 0;
    bool expect_audio = false;
    for (int i = 1; i < argc; ++i) {
        const std::string a = argv[i];
        if (a == "--mode" && i + 1 < argc) {
            const std::string m = argv[++i];
            if (m == "system") {
                others_mode = WF_OTHERS_SYSTEM_EXCLUDE;
                mode = wf_smoke::Mode::System;
            } else if (m == "include") {
                others_mode = WF_OTHERS_PROCESS_INCLUDE;
                mode = wf_smoke::Mode::Include;
            } else if (m == "device") {
                others_mode = WF_OTHERS_DEVICE_LOOPBACK;
                mode = wf_smoke::Mode::Device;
            } else {
                usage();
                return 2;
            }
        } else if (a == "--pid" && i + 1 < argc) {
            pid = static_cast<uint32_t>(strtoul(argv[++i], nullptr, 10));
        } else if (a == "--expect-audio") {
            expect_audio = true;
        } else if (!a.empty() && a[0] != '-' && atoi(a.c_str()) > 0) {
            seconds = static_cast<unsigned>(atoi(a.c_str()));
        } else {
            usage();
            return 2;
        }
    }
    if (others_mode == WF_OTHERS_PROCESS_INCLUDE && pid == 0) {
        std::printf("--mode include needs --pid <process id of the app to record>\n");
        return 2;
    }

    uint32_t caps = 0;
    wf_capabilities(&caps);
    std::printf("WallflyAudio ABI v%u, capabilities 0x%x (process-loopback %s, aec3 %s)\n",
                wf_abi_version(), caps, (caps & WF_CAP_PROCESS_LOOPBACK) ? "yes" : "no",
                (caps & WF_CAP_AEC3) ? "yes" : "no");

    uint32_t device_count = 0;
    wf_devices_list(WF_FLOW_CAPTURE, nullptr, 0, &device_count);
    std::printf("active capture endpoints: %u\n", device_count);

    WfSessionConfig cfg;
    std::memset(&cfg, 0, sizeof(cfg));
    cfg.size = sizeof(cfg);
    cfg.others_mode = others_mode;
    cfg.target_pid = pid;
    cfg.ns_level = 2;
    cfg.enable_aec = 1;
    cfg.prefer_raw = 1;
    cfg.allow_device_loopback_fallback = 1;
    cfg.keepalive_mode = WF_KEEPALIVE_NONE;      // the keep-alive runs anyway; plain watchdog
    cfg.timestamp_mode = WF_TIMESTAMP_RAW_QPC;   // measured: p95 jitter < 0.1 ms

    void* s = nullptr;
    int32_t rc = wf_session_create(&cfg, &s);
    if (rc != WF_OK) {
        std::printf("wf_session_create failed: %d\n", rc);
        return 2;
    }
    HANDLE ev = static_cast<HANDLE>(wf_session_event_handle(s));
    const double wall_start = wall_now_sec();
    rc = wf_session_start(s);
    if (rc != WF_OK) {
        std::printf("wf_session_start failed: %d\n", rc);
        wf_session_destroy(s);
        return 2;
    }
    int64_t origin = 0;
    double base = 0.0;
    wf_session_get_origin(s, &origin, &base);
    double wall_sec = 0.0, session_sec = 0.0;
    std::vector<wf_smoke::Interval> track_gaps;   // Me + Others, for the union
    WfDiagnostics running_diag;                   // sampled while running (power request)
    std::memset(&running_diag, 0, sizeof(running_diag));

    Channel me, others;
    unsigned events_seen = 0, errors = 0, bad_reasons = 0;
    // Per source (Me / Others / reference): gap count and summed seconds; plus the format
    // changes and restarts the live run of 2026-11-01 looped on.
    unsigned gaps[3] = {0, 0, 0}, format_changes = 0, restarting = 0;
    double gap_sec[3] = {0.0, 0.0, 0.0};
    // ERLE / delay sampled every 0.5 s while Others carries signal (level > 0.2).
    double erle_sum = 0.0, erle_max = 0.0;
    int delay_last = 0;
    unsigned erle_samples = 0;
    ULONGLONG next_sample = 0;
    WfFrame fbuf[256];
    WfEvent ebuf[64];
    int32_t stop_rc = WF_OK;
    ULONGLONG stop_ms = 0;

    const ULONGLONG t0 = GetTickCount64();
    bool stopping = false;
    bool stopped = false;
    while (!stopped) {
        if (!stopping && GetTickCount64() - t0 >= seconds * 1000ULL) {
            const ULONGLONG before = GetTickCount64();
            wall_sec = wall_now_sec() - wall_start;
            session_sec = static_cast<double>(qpc_now_100ns() - origin) / 1e7;
            stop_rc = wf_session_stop(s);   // bounded: the DSP thread flushes, <= 2.5 s
            stop_ms = GetTickCount64() - before;
            stopping = true;
        }
        if (ev) WaitForSingleObject(ev, 50);
        // Documented read order: snapshot an event block, drain the frames, then handle
        // the events - so no event (Stopped included) overtakes a frame queued before it.
        for (;;) {
            uint32_t ne = 0;
            if (wf_session_read_events(s, ebuf, 64, &ne) != WF_OK) ne = 0;
            uint32_t n = 0;
            bool more = true;
            while (more) {
                if (wf_session_read_frames(s, fbuf, 256, &n) != WF_OK || n == 0) break;
                for (uint32_t i = 0; i < n; ++i) {
                    note(fbuf[i].source == WF_SOURCE_ME ? me : others, fbuf[i]);
                }
                more = (n == 256);
            }
            for (uint32_t i = 0; i < ne; ++i) {
                const WfEvent& e = ebuf[i];
                ++events_seen;
                if (e.kind == WF_EVENT_ERROR || e.kind == WF_EVENT_PERMISSION_DENIED ||
                    e.kind == WF_EVENT_DRIVER_BLOCKED || e.kind == WF_EVENT_OTHERS_FAILED ||
                    e.kind == WF_EVENT_DEGRADED) {
                    ++errors;
                }
                if ((e.kind == WF_EVENT_GAP) == (e.reason == WF_GAP_NONE)) ++bad_reasons;
                if (e.kind == WF_EVENT_GAP && e.source < 3) {
                    ++gaps[e.source];
                    gap_sec[e.source] += e.duration_sec;
                    if (e.source == WF_SOURCE_ME || e.source == WF_SOURCE_OTHERS) {
                        track_gaps.push_back({e.at_offset_sec, e.duration_sec});
                    }
                }
                if (e.kind == WF_EVENT_FORMAT_CHANGED) ++format_changes;
                if (e.kind == WF_EVENT_RESTARTING) ++restarting;
                if (e.kind == WF_EVENT_GAP) {
                    std::printf("  event %3u %-14s src=%u %-22s at %.3f s for %.3f s\n",
                                e.sequence, event_name(e.kind), e.source, gap_name(e.reason),
                                e.at_offset_sec, e.duration_sec);
                } else {
                    // The detail (AP3: "mic:<name>", "render:<name>", "fallback:...") as UTF-8.
                    char detail[512] = {};
                    WideCharToMultiByte(CP_UTF8, 0, e.detail, -1, detail, sizeof(detail) - 1,
                                        nullptr, nullptr);
                    std::printf("  event %3u %-14s src=%u hr=0x%08x at %.3f s%s%s\n", e.sequence,
                                event_name(e.kind), e.source,
                                static_cast<unsigned>(e.hresult), e.at_offset_sec,
                                detail[0] ? "  detail: " : "", detail);
                }
                if (e.kind == WF_EVENT_STOPPED) stopped = true;
            }
            if (ne < 64) break;
        }
        if (!stopping && GetTickCount64() >= next_sample) {
            next_sample = GetTickCount64() + 500;
            WfLevels lv;
            WfMetrics mm;
            std::memset(&lv, 0, sizeof(lv));
            std::memset(&mm, 0, sizeof(mm));
            wf_session_get_levels(s, &lv);
            wf_session_get_metrics(s, &mm);
            running_diag.size = sizeof(running_diag);
            wf_session_get_diagnostics(s, &running_diag);
            if (lv.others > 0.2f) {
                erle_sum += mm.erle_db;
                if (mm.erle_db > erle_max) erle_max = mm.erle_db;
                delay_last = mm.delay_ms;
                ++erle_samples;
            }
        }
        if (stopping && GetTickCount64() - t0 > (seconds + 10) * 1000ULL) break;
    }
    WfLevels levels;
    std::memset(&levels, 0, sizeof(levels));
    wf_session_get_levels(s, &levels);
    WfMetrics m;
    std::memset(&m, 0, sizeof(m));
    wf_session_get_metrics(s, &m);

    const double me_span = me.frames > 1 ? (me.last - me.first) : 0.0;
    const double others_span = others.frames > 1 ? (others.last - others.first) : 0.0;
    WfDiagnostics diag;
    std::memset(&diag, 0, sizeof(diag));
    diag.size = sizeof(diag);
    wf_session_get_diagnostics(s, &diag);

    std::printf("\n--- %u s smoke, others_mode=%u%s ---\n", seconds, others_mode,
                expect_audio ? ", audio expected on Others" : "");
    std::printf("Me     : %6llu frames  %.2f/s  span %.3f s  peak %.4f  unfiltered %llu\n",
                me.frames, me_span > 0 ? (me.frames - 1) / me_span : 0.0, me_span, me.peak,
                me.unfiltered);
    std::printf("Others : %6llu frames  %.2f/s  span %.3f s  peak %.4f\n", others.frames,
                others_span > 0 ? (others.frames - 1) / others_span : 0.0, others_span,
                others.peak);
    std::printf("levels : me %.3f (active %u)  others %.3f (active %u)\n",
                static_cast<double>(levels.me), levels.me_active,
                static_cast<double>(levels.others), levels.others_active);
    std::printf("state  : %s  mic_raw=%u ref_mode=%u attempt=%u orphans=%u\n",
                state_name(m.state), m.mic_raw, m.ref_mode_actual, m.attempt_id,
                m.orphaned_steps);
    std::printf("timing : drift me %.1f ppm / others %.1f ppm, jitter p95 me %.3f ms / "
                "others %.3f ms\n",
                m.drift_ppm_me, m.drift_ppm_others, m.jitter_p95_ms_me, m.jitter_p95_ms_others);
    std::printf("health : ts_err %llu  disc %llu  inbox_ovf %llu  ring_ovf %llu  late %llu  "
                "ref_disc %llu\n",
                m.timestamp_errors, m.discontinuities, m.inbox_overflows, m.ring_overflows,
                m.late_dropped, m.ref_discarded);
    std::printf("queues : frames_dropped %llu  events_dropped %llu  restarts %llu  "
                "watchdog %llu\n",
                m.frames_dropped, m.events_dropped, m.restarts, m.watchdog_recoveries);
    std::printf("aec    : erle %.1f dB  delay %d ms  resets %llu\n", m.erle_db, m.delay_ms,
                m.aec_resets);
    if (erle_samples > 0) {
        std::printf("playing: %u samples with Others level > 0.2: erle mean %.1f / max %.1f dB, "
                    "delay %d ms\n",
                    erle_samples, erle_sum / erle_samples, erle_max, delay_last);
    }
    std::printf("gaps   : Me %u (%.3f s)  Others %u (%.3f s)  reference %u (%.3f s)  "
                "FormatChanged %u  Restarting %u\n",
                gaps[0], gap_sec[0], gaps[1], gap_sec[1], gaps[2], gap_sec[2], format_changes,
                restarting);
    std::printf("events : %u total, %u of them problems, %u with a wrong reason\n", events_seen,
                errors, bad_reasons);
    std::printf("diag   : tick gap max %.1f ms  capture gap max me %.1f / others %.1f / ref %.1f "
                "ms  ring high water me %u / others %u / ref %u of %u  slices max %u  "
                "ring losses %llu\n",
                diag.dsp_tick_gap_max_ms, diag.capture_gap_max_ms_me,
                diag.capture_gap_max_ms_others, diag.capture_gap_max_ms_reference,
                diag.ring_high_water_me, diag.ring_high_water_others,
                diag.ring_high_water_reference, diag.ring_capacity_slots, diag.dsp_slices_max,
                static_cast<unsigned long long>(diag.ring_loss_gaps));
    std::printf("render : keep-alive starts %u / failures %u  render switches %u  "
                "aec render resets %u\n",
                diag.keepalive_starts, diag.keepalive_failures, diag.render_switches,
                diag.aec_render_resets);
    std::printf("clocks : wall %.3f s  session %.3f s  power request while running: %s\n",
                wall_sec, session_sec, power_name(running_diag.power_request));
    std::printf("stop   : rc %d after %llu ms, Stopped %s, frames read %llu of %llu queued, "
                "origin %lld, base %.1f s\n",
                stop_rc, static_cast<unsigned long long>(stop_ms), stopped ? "seen" : "MISSING",
                me.frames + others.frames,
                static_cast<unsigned long long>(m.frames_me + m.frames_others),
                static_cast<long long>(origin), base);

    wf_session_destroy(s);

    wf_smoke::Numbers n;
    n.mode = mode;
    n.expect_audio = expect_audio;
    n.requested_sec = seconds;
    n.session_sec = session_sec;
    n.wall_sec = wall_sec;
    n.me_frames = me.frames;
    n.others_frames = others.frames;
    n.me_span_sec = me_span;
    n.others_span_sec = others_span;
    n.others_peak = others.peak;
    n.gap_events = track_gaps.size();
    n.gap_union_sec = wf_smoke::gap_union_seconds(track_gaps);
    n.watchdog_recoveries = m.watchdog_recoveries;
    n.restarts = m.restarts;
    n.inbox_overflows = m.inbox_overflows;
    n.ring_overflows = m.ring_overflows;
    n.frames_dropped = m.frames_dropped;
    n.events_dropped = m.events_dropped;
    n.problem_events = errors;
    n.bad_reasons = bad_reasons;
    n.stop_ok = stop_rc == WF_OK;
    n.stopped_seen = stopped;
    n.frames_read = me.frames + others.frames;
    n.frames_queued = m.frames_me + m.frames_others;
    const std::vector<std::string> why = wf_smoke::verdict(n);
    std::printf("verdict: gaps on Me/Others cover %.3f s (%llu events)\n", n.gap_union_sec,
                static_cast<unsigned long long>(n.gap_events));
    if (why.empty()) {
        std::printf("\nSMOKE OK\n");
        return 0;
    }
    std::printf("\nSMOKE FAIL\n");
    for (const std::string& w : why) std::printf("  - %s\n", w.c_str());
    return 1;
}
