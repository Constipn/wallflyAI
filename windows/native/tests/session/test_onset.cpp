// Regression for the Others hole at playback onset (live run 2026-11-01: "Others loses
// 0.4-1.4 s when a new process starts playing"). Replays the packet pattern that
// `wallfly-audioprobe onset` recorded on the process-loopback exclude stream (48 kHz mono
// float, event driven, 480-frame packets, device_position always 0, flags always 0):
//
//   no keep-alive:  +10.000 ms per packet, then NO packet for 431 ms while the idle render
//                   endpoint wakes up, then one step of +433.78 ms and +10.000 ms again.
//                   Receive-minus-QPC lag is 9.1-11.7 ms before and after, so the QPC after
//                   the stall is real time, not a synthetic counter; there is no catch-up
//                   burst, no DATA_DISCONTINUITY, no TIMESTAMP_ERROR. At playback stop one
//                   +28.82 ms step. (Other runs: 440, 446, 478 ms; the live run 516 ms.)
//   keep-alive on:  the same onset costs one +31.10 ms step, the stop one +12.63 ms step.
//
// The 0.42 s are audio the engine never delivered. The normalizer must report that hole
// where it happened and keep every later packet at its real time - deriving timestamps
// from the sample count instead would move all later Others audio 0.42 s early against
// the mic. The hole must not trigger the 0.5 s watchdog or a restart, and it must not
// show up as clock drift (the old whole-run estimate read ~+53000 ppm here). With the
// keep-alive every real session now runs (RealPlant::do_start) the onset is one ~21 ms
// gap; the fake path has no render endpoint, so both recorded patterns are replayed.
#include <cmath>
#include <vector>

#include "test_util.h"

using harness::Fake;
using harness::kTicksPerMs;

namespace {

constexpr int64_t kStep = 100000;   // 10 ms in 100 ns units
constexpr int64_t kLag = 100000;    // receive time = QPC + ~10 ms (measured 9.1-11.7 ms)

struct Packet {
    int64_t recv;
    int64_t qpc;
    uint32_t source;
    uint32_t rate;
    uint32_t frames;
};

// The recorded process-exclude pattern: `before` packets, the onset step, `during`
// packets, the stop step, `after` packets. QPC starts at t0 + 2 ms like the probe's.
std::vector<Packet> others_pattern(int64_t t0, int before, int64_t onset_step, int during,
                                   int64_t stop_step, int after) {
    std::vector<Packet> v;
    int64_t q = t0 + 2 * kTicksPerMs;
    auto add = [&](int count) {
        for (int i = 0; i < count; ++i) {
            v.push_back({q + kLag, q, WF_FAKE_SRC_OTHERS, 48000, 480});
            q += kStep;
        }
    };
    add(before);
    q += onset_step - kStep;   // the packet after the stall is onset_step after the last one
    add(during);
    q += stop_step - kStep;
    add(after);
    return v;
}

struct Result {
    std::vector<WfEvent> others_gaps;
    size_t others_frames = 0;
    double last_others_offset = 0.0;
    double expected_last_offset = 0.0;
    WfMetrics m{};
    int watchdogs = 0;
    int restarts = 0;
};

// Mic: continuous 16 kHz 10 ms blocks over the whole run. Virtual time advances in 10 ms
// ticks; every packet is pushed once its receive time has come, as the capture threads do.
Result replay(const std::vector<Packet>& others) {
    Result r;
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    std::vector<Packet> all = others;
    const int64_t end = others.back().recv + 30 * kStep;
    for (int64_t q = f.t0; q + kLag < end; q += kStep) {
        all.push_back({q + kLag, q, WF_FAKE_SRC_MIC, 16000, 160});
    }
    std::vector<float> zeros(480, 0.0f);
    std::vector<bool> done(all.size(), false);
    for (int64_t now = f.t0 + kStep; now <= end; now += kStep) {
        f.set_now(now);
        for (size_t i = 0; i < all.size(); ++i) {
            if (done[i] || all[i].recv > now) continue;
            done[i] = true;
            f.push(all[i].source, zeros.data(), all[i].frames, all[i].rate, all[i].qpc);
        }
        f.tick();
    }
    r.m = f.metrics();
    CHECK(f.stop() == WF_OK);   // flushes the trailing partial frames
    for (const WfEvent& e : f.events) {
        if (e.kind == WF_EVENT_GAP && e.source == WF_SOURCE_OTHERS) r.others_gaps.push_back(e);
    }
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_OTHERS) continue;
        ++r.others_frames;
        r.last_others_offset = fr.offset_sec + static_cast<double>(fr.valid_hi) / 16000.0;
    }
    // End of the last Others packet on the meeting clock.
    r.expected_last_offset =
        static_cast<double>(others.back().qpc + kStep - f.t0) / harness::kTicksPerSec;
    r.watchdogs = f.count(WF_EVENT_WATCHDOG_RECOVERY);
    r.restarts = f.count(WF_EVENT_RESTARTING);
    CHECK(f.reasons_ok());
    return r;
}

void test_onset_without_keepalive() {
    const std::vector<Packet> p =
        others_pattern(harness::kStart, 225, 4337755, 359, 288200, 200);
    const Result r = replay(p);
    // Exactly the two holes the engine left: 433.78 - 10 ms and 28.82 - 10 ms, both as
    // timestamp discontinuities where they happened.
    CHECK(r.others_gaps.size() == 2);
    if (r.others_gaps.size() == 2) {
        const WfEvent& onset = r.others_gaps[0];
        const WfEvent& stop = r.others_gaps[1];
        CHECK(onset.reason == WF_GAP_TIMESTAMP_DISCONTINUITY);
        CHECK(stop.reason == WF_GAP_TIMESTAMP_DISCONTINUITY);
        CHECK_NEAR(onset.duration_sec, 0.42378, 0.0015);
        CHECK_NEAR(onset.at_offset_sec, 0.002 + 225 * 0.010, 0.003);
        CHECK_NEAR(stop.duration_sec, 0.01882, 0.0015);
        std::printf("  onset, no keep-alive: gap %.4f s at %.3f s, stop gap %.4f s\n",
                    onset.duration_sec, onset.at_offset_sec, stop.duration_sec);
    }
    // No watchdog, no restart: 431 ms without a packet stays under the 0.5 s limit.
    CHECK(r.watchdogs == 0);
    CHECK(r.restarts == 0);
    CHECK(r.m.watchdog_recoveries == 0);
    CHECK(r.m.restarts == 0);
    // Every delivered packet became Others audio, and the audio after the holes sits at its
    // real time: the end of the last frame is where the last packet's QPC says it ends.
    CHECK(r.others_frames >= p.size() && r.others_frames <= p.size() + 4);
    CHECK_NEAR(r.last_others_offset, r.expected_last_offset, 0.003);
    // The holes are not clock drift.
    CHECK(std::fabs(r.m.drift_ppm_others) < 100.0);
    // The mic runs on while the reference is missing: those frames are unfiltered.
    CHECK(r.m.unfiltered_frames >= 35);
    std::printf("  onset, no keep-alive: %zu Others frames, last ends at %.4f s (QPC %.4f s), "
                "drift %.1f ppm, unfiltered %llu\n",
                r.others_frames, r.last_others_offset, r.expected_last_offset,
                r.m.drift_ppm_others, static_cast<unsigned long long>(r.m.unfiltered_frames));
}

void test_onset_with_keepalive() {
    const std::vector<Packet> p = others_pattern(harness::kStart, 182, 311000, 360, 126300, 200);
    const Result r = replay(p);
    // One ~21 ms hole at the onset; the +2.63 ms stop step stays under the 3 ms re-anchor
    // limit and is absorbed by the drift correction.
    CHECK(r.others_gaps.size() == 1);
    if (!r.others_gaps.empty()) {
        CHECK(r.others_gaps[0].reason == WF_GAP_TIMESTAMP_DISCONTINUITY);
        CHECK_NEAR(r.others_gaps[0].duration_sec, 0.0211, 0.0015);
        std::printf("  onset, keep-alive: one gap %.4f s at %.3f s\n",
                    r.others_gaps[0].duration_sec, r.others_gaps[0].at_offset_sec);
    }
    CHECK(r.watchdogs == 0);
    CHECK(r.restarts == 0);
    CHECK(r.others_frames >= p.size() && r.others_frames <= p.size() + 4);
    CHECK_NEAR(r.last_others_offset, r.expected_last_offset, 0.003);
    CHECK(std::fabs(r.m.drift_ppm_others) < 1000.0);
    std::printf("  onset, keep-alive: drift %.1f ppm, unfiltered %llu\n", r.m.drift_ppm_others,
                static_cast<unsigned long long>(r.m.unfiltered_frames));
}

}  // namespace

void test_onset() {
    test_onset_without_keepalive();
    test_onset_with_keepalive();
}
