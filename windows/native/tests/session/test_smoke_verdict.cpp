// E1: the strict wf_hw_smoke verdict (tests/hardware/smoke_verdict.h). Healthy numbers
// pass; every failure condition fails on its own with its own reason; the numbers of the
// first 1-hour run (2026-11-02), which the old verdict printed as "SMOKE OK", fail with at
// least four reasons.
#include <string>
#include <vector>

#include "hardware/smoke_verdict.h"
#include "test_util.h"

namespace {

using wf_smoke::Interval;
using wf_smoke::Mode;
using wf_smoke::Numbers;

// A clean 60 s system-mode run: 100 frames/s on both sources, nothing lost.
Numbers healthy() {
    Numbers n;
    n.mode = Mode::System;
    n.expect_audio = true;
    n.requested_sec = 60.0;
    n.session_sec = 60.0;
    n.wall_sec = 60.2;
    n.me_frames = 6001;
    n.others_frames = 6001;
    n.me_span_sec = 60.0;
    n.others_span_sec = 60.0;
    n.others_peak = 0.25;
    n.frames_read = 12002;
    n.frames_queued = 12002;
    return n;
}

bool mentions(const std::vector<std::string>& why, const char* word) {
    for (const std::string& w : why) {
        if (w.find(word) != std::string::npos) return true;
    }
    return false;
}

// Exactly one reason, and it names `word`.
void expect_single(const Numbers& n, const char* word) {
    const std::vector<std::string> why = wf_smoke::verdict(n);
    CHECK(why.size() == 1);
    CHECK(mentions(why, word));
    if (why.size() != 1 || !mentions(why, word)) {
        std::printf("    expected one reason with '%s', got %zu:\n", word, why.size());
        for (const std::string& w : why) std::printf("      %s\n", w.c_str());
    }
}

void test_gap_union() {
    CHECK(wf_smoke::gap_union_seconds({}) == 0.0);
    // Me and Others losing the same 0.3 s count once; a separate 0.1 s adds; a zero-length
    // entry adds nothing; order does not matter.
    std::vector<Interval> g = {{5.0, 0.1}, {1.0, 0.3}, {1.1, 0.2}, {1.2, 0.0}, {1.25, 0.05}};
    CHECK_NEAR(wf_smoke::gap_union_seconds(g), 0.4, 1e-12);
    CHECK_NEAR(wf_smoke::gap_union_seconds({{0.0, 1.0}, {1.0, 1.0}}), 2.0, 1e-12);   // touching
    CHECK_NEAR(wf_smoke::frames_per_sec(6001, 60.0), 100.0, 1e-12);
    CHECK(wf_smoke::frames_per_sec(1, 60.0) == 0.0);
}

void test_conditions() {
    CHECK(wf_smoke::verdict(healthy()).empty());
    {   // a silent run without --expect-audio is fine
        Numbers n = healthy();
        n.expect_audio = false;
        n.others_peak = 0.0;
        CHECK(wf_smoke::verdict(n).empty());
    }
    {   // --expect-audio, Others silent
        Numbers n = healthy();
        n.others_peak = 0.0;
        expect_single(n, "Others peak");
        n.others_peak = 0.0005;
        expect_single(n, "Others peak");
    }
    {   // frame rate just under 99.9 % on either source (a 0.1 % hole inside the span)
        Numbers n = healthy();
        n.others_frames = 5990;                       // 99.82/s
        expect_single(n, "Others frame rate");
        n = healthy();
        n.me_frames = 5990;
        expect_single(n, "Me frame rate");
        n = healthy();
        n.me_frames = 5996;                           // 99.92/s is still fine
        CHECK(wf_smoke::verdict(n).empty());
    }
    {   // include mode: an idle app may deliver nothing - unless audio was expected
        Numbers n = healthy();
        n.mode = Mode::Include;
        n.expect_audio = false;
        n.others_peak = 0.0;
        n.others_frames = 300;
        n.frames_read = n.frames_queued = 6301;
        CHECK(wf_smoke::verdict(n).empty());
        n.expect_audio = true;
        CHECK(mentions(wf_smoke::verdict(n), "Others frame rate"));
    }
    {   // gaps: 0.1 % of 60 s is 60 ms
        Numbers n = healthy();
        n.gap_events = 3;
        n.gap_union_sec = 0.06;
        CHECK(wf_smoke::verdict(n).empty());
        n.gap_union_sec = 0.061;
        expect_single(n, "gaps cover");
    }
    {
        Numbers n = healthy();
        n.watchdog_recoveries = 1;
        expect_single(n, "watchdog");
        n = healthy();
        n.restarts = 1;
        expect_single(n, "restart");
    }
    {   // wall clock ahead of the session clock by more than 2 s
        Numbers n = healthy();
        n.wall_sec = 62.0;
        CHECK(wf_smoke::verdict(n).empty());
        n.wall_sec = 62.1;
        expect_single(n, "wall clock");
    }
    {
        Numbers n = healthy();
        n.inbox_overflows = 1;
        expect_single(n, "inbox overflow");
        n = healthy();
        n.ring_overflows = 1;
        expect_single(n, "ring overflow");
    }
    {   // the conditions the old verdict already had
        Numbers n = healthy();
        n.frames_dropped = 1;
        expect_single(n, "dropped by the output queue");
        n = healthy();
        n.events_dropped = 1;
        expect_single(n, "event(s) dropped");
        n = healthy();
        n.problem_events = 1;
        expect_single(n, "problem event");
        n = healthy();
        n.bad_reasons = 1;
        expect_single(n, "wrong reason");
        n = healthy();
        n.stop_ok = false;
        expect_single(n, "wf_session_stop");
        n = healthy();
        n.stopped_seen = false;
        expect_single(n, "STOPPED missing");
        n = healthy();
        n.frames_read = 12001;
        expect_single(n, "queued frames");
    }
}

// Numbers from the smoke log of the first unattended 1-hour run (2026-11-02).
void test_run_of_2026_11_02() {
    Numbers n;
    n.mode = Mode::System;
    n.expect_audio = true;          // audio was playing for the long run
    n.requested_sec = 3600.0;
    n.session_sec = 3600.0;
    n.wall_sec = 37940.0;
    n.me_frames = 360001;
    n.me_span_sec = 3600.0;
    n.others_frames = 37333;        // 10.37 frames/s
    n.others_span_sec = 3600.0;
    n.others_peak = 0.0;
    n.gap_events = 10430;
    n.gap_union_sec = 3227.0;       // Others lost 50-95 % of every 5-minute window
    n.inbox_overflows = 157998;
    n.watchdog_recoveries = 1;
    n.restarts = 1;
    n.frames_read = n.frames_queued = n.me_frames + n.others_frames;
    const std::vector<std::string> why = wf_smoke::verdict(n);
    CHECK(why.size() >= 4);
    CHECK(mentions(why, "Others frame rate"));
    CHECK(mentions(why, "Others peak"));
    CHECK(mentions(why, "wall clock"));
    CHECK(mentions(why, "inbox overflow"));
    CHECK(mentions(why, "watchdog"));
    CHECK(mentions(why, "restart"));
    CHECK(mentions(why, "gaps cover"));
    std::printf("  2026-11-02 run: SMOKE FAIL with %zu reasons:\n", why.size());
    for (const std::string& w : why) std::printf("    - %s\n", w.c_str());
}

}  // namespace

void test_smoke_verdict() {
    test_gap_union();
    test_conditions();
    test_run_of_2026_11_02();
}
