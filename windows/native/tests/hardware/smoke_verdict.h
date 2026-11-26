// smoke_verdict: the pass/fail rule of wf_hw_smoke as a pure function, so it can be tested
// (tests/session/test_smoke_verdict.cpp) and cannot drift into "SMOKE OK" again for a run
// like the first 1-hour attempt of 2026-11-02 (Others at 10.37 frames/s with peak 0.0000,
// 10,430 gaps, 157,998 inbox overflows, one watchdog recovery, one restart and a wall clock
// of 37,940 s for a 3,600 s run).
//
// Header only, no dependency beyond the C++ standard library and the ABI's constants.
#pragma once
#include <algorithm>
#include <cstdint>
#include <cstdio>
#include <string>
#include <vector>

namespace wf_smoke {

// Expected cadence: one 10 ms frame per source and 1/100 s.
inline constexpr double kExpectedFramesPerSec = 100.0;
// A source below 99.9 % of that has holes (gaps shorten the span less than the frames).
inline constexpr double kMinFramesPerSec = 99.9;
// "Others carries audio": a peak at or below -60 dBFS is treated as silence.
inline constexpr double kMinOthersPeak = 0.001;
// Union of all Me/Others gap seconds, as a share of the run.
inline constexpr double kMaxGapShare = 0.001;   // 0.1 %
// Wall clock (system time) may run ahead of the session clock (QPC) by at most this much;
// more means the process was suspended or the machine was in (Modern) Standby.
inline constexpr double kMaxClockLeadSec = 2.0;

enum class Mode { System, Include, Device };

struct Interval {
    double start = 0.0;      // meeting offset, s
    double duration = 0.0;   // s
};

// Total length covered by the union of `gaps` (overlaps counted once).
inline double gap_union_seconds(std::vector<Interval> gaps) {
    std::sort(gaps.begin(), gaps.end(),
              [](const Interval& a, const Interval& b) { return a.start < b.start; });
    double total = 0.0, cur_lo = 0.0, cur_hi = 0.0;
    bool open = false;
    for (const Interval& g : gaps) {
        if (g.duration <= 0.0) continue;
        const double lo = g.start, hi = g.start + g.duration;
        if (!open) {
            cur_lo = lo;
            cur_hi = hi;
            open = true;
        } else if (lo <= cur_hi) {
            if (hi > cur_hi) cur_hi = hi;
        } else {
            total += cur_hi - cur_lo;
            cur_lo = lo;
            cur_hi = hi;
        }
    }
    if (open) total += cur_hi - cur_lo;
    return total;
}

// Frames per second over the span the frames cover ((n - 1) / (last - first offset)), the
// number wf_hw_smoke prints. 0 when there are fewer than two frames.
inline double frames_per_sec(uint64_t frames, double span_sec) {
    return (frames > 1 && span_sec > 0.0) ? static_cast<double>(frames - 1) / span_sec : 0.0;
}

// Everything the verdict looks at. Filled by wf_hw_smoke from one run.
struct Numbers {
    Mode   mode = Mode::System;
    bool   expect_audio = false;     // --expect-audio: something audible played on Others
    double requested_sec = 0.0;      // the run length asked for
    double session_sec = 0.0;        // session clock (QPC) from start to the stop request
    double wall_sec = 0.0;           // system wall clock over the same stretch
    uint64_t me_frames = 0;
    uint64_t others_frames = 0;
    double me_span_sec = 0.0;        // last - first frame offset
    double others_span_sec = 0.0;
    double others_peak = 0.0;        // max |sample| over all Others frames
    uint64_t gap_events = 0;         // WF_EVENT_GAP on Me + Others
    double gap_union_sec = 0.0;      // gap_union_seconds() of those gaps
    uint64_t watchdog_recoveries = 0;
    uint64_t restarts = 0;
    uint64_t inbox_overflows = 0;
    uint64_t ring_overflows = 0;
    uint64_t frames_dropped = 0;
    uint64_t events_dropped = 0;
    uint32_t problem_events = 0;     // Error, PermissionDenied, DriverBlocked, OthersFailed, Degraded
    uint32_t bad_reasons = 0;        // events whose reason does not match their kind
    bool stop_ok = true;             // wf_session_stop returned WF_OK
    bool stopped_seen = true;        // WF_EVENT_STOPPED was read
    uint64_t frames_read = 0;        // frames the tool read ...
    uint64_t frames_queued = 0;      // ... vs. WfMetrics.frames_me + frames_others
};

inline std::string fmt(const char* f, double a, double b = 0.0) {
    char buf[160];
    std::snprintf(buf, sizeof(buf), f, a, b);
    return buf;
}

// Every reason the run failed; empty means SMOKE OK.
inline std::vector<std::string> verdict(const Numbers& n) {
    std::vector<std::string> why;
    const double run = n.session_sec > 0.0 ? n.session_sec : n.requested_sec;

    if (n.me_frames == 0) why.push_back("no Me frames");
    if (n.others_frames == 0) why.push_back("no Others frames");
    const double me_fps = frames_per_sec(n.me_frames, n.me_span_sec);
    const double others_fps = frames_per_sec(n.others_frames, n.others_span_sec);
    if (n.me_frames > 0 && me_fps < kMinFramesPerSec) {
        why.push_back(fmt("Me frame rate %.2f/s < %.1f/s", me_fps, kMinFramesPerSec));
    }
    // An include-mode app source may legitimately fall silent and deliver nothing (spec
    // 3.2); its cadence is only demanded when the run says audio was playing.
    const bool others_must_flow = n.mode != Mode::Include || n.expect_audio;
    if (n.others_frames > 0 && others_must_flow && others_fps < kMinFramesPerSec) {
        why.push_back(fmt("Others frame rate %.2f/s < %.1f/s", others_fps, kMinFramesPerSec));
    }
    if (n.expect_audio && n.others_peak < kMinOthersPeak) {
        why.push_back(fmt("--expect-audio but Others peak %.4f < %.3f", n.others_peak,
                          kMinOthersPeak));
    }
    if (run > 0.0 && n.gap_union_sec > kMaxGapShare * run) {
        why.push_back(fmt("gaps cover %.3f s, more than 0.1 %% of the run (%.3f s)",
                          n.gap_union_sec, kMaxGapShare * run));
    }
    if (n.watchdog_recoveries > 0) {
        why.push_back(fmt("%.0f watchdog recovery(ies)", static_cast<double>(n.watchdog_recoveries)));
    }
    if (n.restarts > 0) why.push_back(fmt("%.0f stream restart(s)", static_cast<double>(n.restarts)));
    if (n.wall_sec - n.session_sec > kMaxClockLeadSec) {
        why.push_back(fmt("wall clock %.1f s vs session clock %.1f s (suspended / standby?)",
                          n.wall_sec, n.session_sec));
    }
    if (n.inbox_overflows > 0) {
        why.push_back(fmt("%.0f inbox overflow(s)", static_cast<double>(n.inbox_overflows)));
    }
    if (n.ring_overflows > 0) {
        why.push_back(fmt("%.0f capture ring overflow(s)", static_cast<double>(n.ring_overflows)));
    }
    if (n.frames_dropped > 0) {
        why.push_back(fmt("%.0f frame(s) dropped by the output queue",
                          static_cast<double>(n.frames_dropped)));
    }
    if (n.events_dropped > 0) {
        why.push_back(fmt("%.0f event(s) dropped", static_cast<double>(n.events_dropped)));
    }
    if (n.problem_events > 0) {
        why.push_back(fmt("%.0f problem event(s) (Error/PermissionDenied/DriverBlocked/"
                          "OthersFailed/Degraded)",
                          static_cast<double>(n.problem_events)));
    }
    if (n.bad_reasons > 0) {
        why.push_back(fmt("%.0f event(s) with a wrong reason", static_cast<double>(n.bad_reasons)));
    }
    if (!n.stop_ok) why.push_back("wf_session_stop did not return WF_OK");
    if (!n.stopped_seen) why.push_back("WF_EVENT_STOPPED missing");
    if (n.frames_read != n.frames_queued) {
        why.push_back(fmt("read %.0f of %.0f queued frames", static_cast<double>(n.frames_read),
                          static_cast<double>(n.frames_queued)));
    }
    return why;
}

}  // namespace wf_smoke
