// Frame-level behaviour of a session: timeline positions, meeting offsets, the 224
// sample latency compensation of the Me channel, attempt fencing, the unfiltered
// fallback, the level formula, the stop flush and its ordering, partial frames
// (WfFrame.valid_lo/hi), the enable_aec = 0 passthrough and wf_session_get_origin.
#include "test_util.h"

using harness::Fake;
using harness::kTicksPerMs;
using harness::ramp;

namespace {

constexpr uint32_t kRate = 16000;
constexpr int kBlock = 160;                 // one 10 ms packet
constexpr int64_t kBlockTicks = 100000;     // 10 ms in 100 ns units

// Pushes the 10 ms packet whose first sample is at absolute input position n*160.
void push_block(Fake& f, uint32_t source, int64_t n, bool signal_on) {
    float buf[kBlock];
    for (int i = 0; i < kBlock; ++i) {
        buf[i] = signal_on ? ramp(n * kBlock + i) : 0.0f;
    }
    f.push(source, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
}

void push_const(Fake& f, uint32_t source, int64_t n, float value) {
    float buf[kBlock];
    for (int i = 0; i < kBlock; ++i) buf[i] = value;
    f.push(source, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
}

// ---------------------------------------------------------------------------
// Offsets, timeline positions, base_duration and the -224 compensation.
// ---------------------------------------------------------------------------
void test_timeline_and_compensation() {
    const double base = 7.5;
    Fake f;
    CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, base));
    CHECK(f.start());
    f.tick();   // opens the streams (the fake plant runs the steps inline)

    const int blocks = 60;
    for (int n = 0; n < blocks; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }

    // Others passes through unchanged: frame n sits at sample n*160.
    int64_t expect_others = 0;
    size_t others_seen = 0;
    bool others_ok = true;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_OTHERS) continue;
        if (fr.timeline_sample != expect_others) others_ok = false;
        // Meeting offset = position/16000 + base_duration (the stream origin is the
        // session origin here, because the first packet arrives at t0).
        const double want = static_cast<double>(expect_others) / kRate + base;
        if (std::fabs(fr.offset_sec - want) > 1e-9) others_ok = false;
        if (fr.attempt_id != 1) others_ok = false;
        // Grid-aligned 10 ms packets: every frame is complete.
        if (fr.valid_lo != 0 || fr.valid_hi != kBlock) others_ok = false;
        expect_others += kBlock;
        ++others_seen;
    }
    CHECK(others_ok);
    CHECK(others_seen >= 55);

    // Me: output sample k of capture frame n belongs to position n*160 + k - 224, so a
    // Me frame at timeline position T carries the input samples T + 224 .. T + 383.
    //
    // The sample values can only be compared against the input when the passthrough stub
    // (src/aec/aec_stub.cpp) is linked. With the vendored AEC3 + NS bridge the Me channel
    // is genuinely filtered - high-pass, echo canceller, noise suppressor - so the content
    // differs from the input by design. The timeline mapping below is checked either way,
    // and that is what this test is about; the ERLE suites cover the filtering itself.
    uint32_t caps = 0;
    wf_capabilities(&caps);
    const bool passthrough_aec = (caps & WF_CAP_AEC3) == 0;

    size_t me_seen = 0;
    bool me_ok = true;
    bool me_offsets_ok = true;
    int64_t expect_me = 0;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_ME) continue;
        if (fr.timeline_sample != expect_me) me_ok = false;
        if (fr.valid_lo != 0 || fr.valid_hi != kBlock) me_ok = false;
        const double want = static_cast<double>(expect_me) / kRate + base;
        if (std::fabs(fr.offset_sec - want) > 1e-9) me_offsets_ok = false;
        if (passthrough_aec) {
            for (int k = 0; k < kBlock; ++k) {
                const int64_t input_pos = fr.timeline_sample + k + 224;
                if (input_pos >= static_cast<int64_t>(blocks) * kBlock) break;
                if (std::fabs(fr.samples[k] - ramp(input_pos)) > 1e-6f) {
                    me_ok = false;
                    break;
                }
            }
        }
        expect_me += kBlock;
        ++me_seen;
    }
    CHECK(me_ok);
    CHECK(me_offsets_ok);
    CHECK(me_seen >= 50);
    std::printf("  timeline: %zu Others frames, %zu Me frames, base=%.1f\n", others_seen,
                me_seen, base);

    const WfMetrics m = f.metrics();
    CHECK(m.state == WF_STATE_RUNNING);
    CHECK(m.frames_me == me_seen);
    CHECK(m.frames_others == others_seen);
    CHECK(m.frames_dropped == 0);
    CHECK(m.events_dropped == 0);
    CHECK(m.inbox_overflows == 0);
    CHECK(m.ring_overflows == 0);
    CHECK(m.unfiltered_frames == 0);          // the reference was always there
    CHECK(m.ref_mode_actual == WF_REF_MODE_PROCESS_EXCLUDE);
    CHECK(m.attempt_id == 1);
    CHECK(f.count(WF_EVENT_DEVICE_ACQUIRED) == 2);   // mic and Others
}

// ---------------------------------------------------------------------------
// attempt_id increments on every (re)start and fences stale frames.
// ---------------------------------------------------------------------------
void test_attempt_fencing() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    int n = 0;
    for (; n < 20; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    const size_t before = f.frames.size();
    CHECK(f.metrics().attempt_id == 1);

    // Device loss: Gap(DeviceLost) -> Restarting -> (first packet) DeviceAcquired.
    CHECK(f.signal(WF_FAKE_SIGNAL_DEVICE_LOST, WF_FAKE_SRC_MIC) == WF_OK);
    f.set_now(f.t0 + (n + 1) * kBlockTicks);
    f.tick();
    ++n;
    CHECK(f.count(WF_EVENT_DEVICE_LOST, WF_SOURCE_ME) == 1);
    CHECK(f.count_gap(WF_GAP_DEVICE_LOST) == 1);
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 1);
    const int lost = f.index_of(WF_EVENT_DEVICE_LOST);
    const int restarting = f.index_of(WF_EVENT_RESTARTING);
    CHECK(lost >= 0 && restarting > lost);
    CHECK(f.metrics().attempt_id == 2);

    for (; n < 50; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    CHECK(f.count(WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_ME) == 2);

    size_t me_a1 = 0, me_a2 = 0;
    for (size_t i = 0; i < f.frames.size(); ++i) {
        const WfFrame& fr = f.frames[i];
        if (fr.source != WF_SOURCE_ME) continue;
        if (fr.attempt_id == 1) ++me_a1;
        if (fr.attempt_id == 2) ++me_a2;
    }
    CHECK(me_a1 > 0);
    CHECK(me_a2 > 0);
    CHECK(f.frames.size() > before);
    CHECK(f.metrics().restarts >= 1);
    CHECK(f.reasons_ok());   // DeviceLost/Restarting/DeviceAcquired: WF_GAP_NONE, the gap: 3
    std::printf("  fencing: %zu Me frames at attempt 1, %zu at attempt 2\n", me_a1, me_a2);
}

// ---------------------------------------------------------------------------
// Missing reference: the mic frame waits frame end + 100 ms, then runs unfiltered.
// ---------------------------------------------------------------------------
void test_unfiltered_after_100ms() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    // 10 ms of mic audio at t0, no Others at all.
    push_block(f, WF_FAKE_SRC_MIC, 0, true);
    push_block(f, WF_FAKE_SRC_MIC, 1, true);
    push_block(f, WF_FAKE_SRC_MIC, 2, true);
    f.set_now(f.t0 + 3 * kBlockTicks);
    f.tick();
    // Frame 0's deadline is t0 + 10 ms + 100 ms, so nothing may be emitted yet.
    CHECK(f.frames_of(WF_SOURCE_ME) == 0);
    CHECK(f.metrics().unfiltered_frames == 0);

    f.set_now(f.t0 + 100 * kTicksPerMs);
    f.tick();
    CHECK(f.frames_of(WF_SOURCE_ME) == 0);   // still inside the 110 ms window

    f.set_now(f.t0 + 115 * kTicksPerMs);
    f.tick();
    CHECK(f.metrics().unfiltered_frames >= 1);

    // Feed more so a complete Me frame makes it through the 224 sample FIFO.
    for (int n = 3; n < 30; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        f.set_now(f.t0 + (115 + (n - 2) * 10) * kTicksPerMs);
        f.tick();
    }
    CHECK(f.frames_of(WF_SOURCE_ME) > 0);
    bool all_unfiltered = true;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_ME) continue;
        if ((fr.flags & WF_FRAME_UNFILTERED) == 0) all_unfiltered = false;
    }
    CHECK(all_unfiltered);
    CHECK(f.frames_of(WF_SOURCE_OTHERS) == 0);
    std::printf("  unfiltered: %llu frames ran with a zero reference\n",
                static_cast<unsigned long long>(f.metrics().unfiltered_frames));
}

// ---------------------------------------------------------------------------
// Level formula (spec 3.6): (20*log10(max(rms,1e-5)) + 60) / 60, clamped to 0..1.
// ---------------------------------------------------------------------------
void test_levels() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    const float amplitude = 0.1f;             // rms == 0.1 for a constant signal
    for (int n = 0; n < 40; ++n) {
        push_const(f, WF_FAKE_SRC_OTHERS, n, amplitude);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    const double want = (20.0 * std::log10(static_cast<double>(amplitude)) + 60.0) / 60.0;
    WfLevels l = f.levels();
    CHECK_NEAR(l.others, want, 1e-4);
    CHECK(l.others_active == 1);
    CHECK(l.me == 0.0f);                       // nothing was fed to the mic
    std::printf("  levels: others=%.4f (expected %.4f)\n", static_cast<double>(l.others), want);

    // Spec 3.6: the meter falls back to 0 after 0.35 s without a new value.
    f.advance_ms(500);
    l = f.levels();
    CHECK(l.others == 0.0f);
    CHECK(l.others_active == 0);
}

// ---------------------------------------------------------------------------
// stop(): flush the 224 sample tail and emit Stopped last. The zero frames that push the
// tail out carry an empty valid range (Mac flushEchoTail), so what comes out is the real
// cleaned tail - complete frames, contiguous, never flagged as synthetic silence.
// ---------------------------------------------------------------------------
void test_stop_flush() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    const int blocks = 20;
    for (int n = 0; n < blocks; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    const size_t me_before = f.frames_of(WF_SOURCE_ME);
    CHECK(f.count(WF_EVENT_STOPPED) == 0);

    CHECK(f.stop() == WF_OK);
    const size_t me_after = f.frames_of(WF_SOURCE_ME);
    CHECK(me_after > me_before);
    // Every captured mic frame comes out exactly once, and nothing beyond the last one.
    CHECK(me_after == static_cast<size_t>(blocks));
    size_t synthetic = 0;
    bool full = true;
    bool contiguous = true;
    int64_t expect_me = 0;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_ME) continue;
        if ((fr.flags & WF_FRAME_SYNTHETIC_SILENCE) != 0) ++synthetic;
        if (fr.valid_lo != 0 || fr.valid_hi != kBlock) full = false;
        if (fr.timeline_sample != expect_me) contiguous = false;
        expect_me += kBlock;
    }
    CHECK(synthetic == 0);
    CHECK(full);
    CHECK(contiguous);
    CHECK(f.count(WF_EVENT_STOPPED) == 1);
    CHECK(f.events.back().kind == WF_EVENT_STOPPED);
    CHECK(f.count(WF_EVENT_GAP) == 0);         // the tail is emitted, not reported lost
    CHECK(f.metrics().state == WF_STATE_STOPPED);
    CHECK(f.reasons_ok());
    CHECK(wf_session_stop(f.s) == WF_OK);      // idempotent
    CHECK(wf_fake_tick(f.s) == WF_E_STATE);    // no ticking after stop
    std::printf("  stop: %zu Me frames before, %zu after the flush, 0 synthetic\n", me_before,
                me_after);
}

// ---------------------------------------------------------------------------
// Partial frames (Mac EchoFrame.valid): a stream that starts 5 ms into frame 0 and ends
// 5 ms into frame 20 yields a partial head [80, 160) and a partial tail [0, 80) on both
// channels. The tail is pushed through by Framer::finish on stop. Samples outside the
// valid range are zero, and the Others content sits exactly at its timeline position.
// ---------------------------------------------------------------------------
void push_shifted(Fake& f, uint32_t source, int64_t first_pos, int n) {
    float buf[kBlock];
    for (int i = 0; i < n; ++i) buf[i] = 0.05f + ramp(first_pos + i);   // never exactly 0
    f.push(source, buf, static_cast<uint32_t>(n), kRate,
           f.t0 + first_pos * (kBlockTicks / kBlock));
}

bool zero_outside(const WfFrame& fr) {
    for (int k = 0; k < kBlock; ++k) {
        if (k >= fr.valid_lo && k < fr.valid_hi) continue;
        if (fr.samples[k] != 0.0f) return false;
    }
    return true;
}

void test_partial_frames() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    // 20 packets of 160 samples, each starting at position 80 + j*160, so every packet
    // straddles two frames: frame 0 gets [80,160), frame 20 gets [0,80).
    const int packets = 20;
    for (int j = 0; j < packets; ++j) {
        const int64_t pos = 80 + static_cast<int64_t>(j) * kBlock;
        push_shifted(f, WF_FAKE_SRC_MIC, pos, kBlock);
        push_shifted(f, WF_FAKE_SRC_OTHERS, pos, kBlock);
        f.set_now(f.t0 + (pos + kBlock) * (kBlockTicks / kBlock));
        f.tick();
    }
    // The tail of frame 20 is still inside the framers: nothing at 3200 yet.
    bool tail_before_stop = false;
    for (const WfFrame& fr : f.frames) {
        if (fr.timeline_sample == 20 * kBlock) tail_before_stop = true;
    }
    CHECK(!tail_before_stop);
    CHECK(f.stop() == WF_OK);

    const int64_t last_pos = 20 * kBlock;
    for (uint32_t source : {WF_SOURCE_OTHERS, WF_SOURCE_ME}) {
        const WfFrame* head = nullptr;
        const WfFrame* tail = nullptr;
        size_t n = 0;
        bool middle_full = true;
        bool zeros_ok = true;
        bool contiguous = true;
        int64_t expect = 0;
        for (const WfFrame& fr : f.frames) {
            if (fr.source != source) continue;
            if (!head) head = &fr;
            tail = &fr;
            ++n;
            if (fr.timeline_sample != expect) contiguous = false;
            expect += kBlock;
            if (fr.timeline_sample != 0 && fr.timeline_sample != last_pos &&
                (fr.valid_lo != 0 || fr.valid_hi != kBlock)) {
                middle_full = false;
            }
            if (!zero_outside(fr)) zeros_ok = false;
            if ((fr.flags & WF_FRAME_SYNTHETIC_SILENCE) != 0) zeros_ok = false;
        }
        CHECK(n == 21);                        // frames 0 .. 20
        CHECK(contiguous);
        CHECK(middle_full);
        CHECK(zeros_ok);
        CHECK(head != nullptr && head->timeline_sample == 0 && head->valid_lo == 80 &&
              head->valid_hi == kBlock);
        CHECK(tail != nullptr && tail->timeline_sample == last_pos && tail->valid_lo == 0 &&
              tail->valid_hi == 80);
        if (head && tail) {
            std::printf("  partial %s: %zu frames, head [%u,%u) at %lld, tail [%u,%u) at %lld\n",
                        source == WF_SOURCE_ME ? "Me    " : "Others", n,
                        static_cast<unsigned>(head->valid_lo), static_cast<unsigned>(head->valid_hi),
                        static_cast<long long>(head->timeline_sample),
                        static_cast<unsigned>(tail->valid_lo), static_cast<unsigned>(tail->valid_hi),
                        static_cast<long long>(tail->timeline_sample));
        }
    }

    // Others passes through unchanged: the carrying samples are the input at exactly
    // their timeline position, the head and the tail included.
    bool others_exact = true;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_OTHERS) continue;
        for (int k = fr.valid_lo; k < fr.valid_hi; ++k) {
            const float want = 0.05f + ramp(fr.timeline_sample + k);
            if (std::fabs(fr.samples[k] - want) > 1e-6f) others_exact = false;
        }
    }
    CHECK(others_exact);
    CHECK(f.count(WF_EVENT_GAP) == 0);
    CHECK(f.metrics().frames_dropped == 0);
}

// ---------------------------------------------------------------------------
// Stop ordering. The flush runs on the producer and queues Stopped after the last frame;
// the streams are detached only after it, so packets that reached the rings without a
// tick still come out. Read in the documented order (events, then frames until empty),
// everything queued before Stopped is in hand when Stopped is. Nothing follows Stopped,
// and a second stop is a no-op.
// ---------------------------------------------------------------------------
void test_stop_ordering() {
    {
        Fake g;
        CHECK(g.create());
        CHECK(wf_session_stop(g.s) == WF_E_STATE);   // never started
    }
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    const int ticked = 20;
    const int pending = 3;
    for (int n = 0; n < ticked; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    for (int n = ticked; n < ticked + pending; ++n) {   // in the rings, never ticked
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
    }
    f.set_now(f.t0 + (ticked + pending) * kBlockTicks);
    CHECK(wf_session_stop(f.s) == WF_OK);

    // 1. one block of events, 2. frames until empty, 3. look at the events.
    WfEvent ev[128];
    uint32_t ne = 0;
    CHECK(wf_session_read_events(f.s, ev, 128, &ne) == WF_OK);
    f.events.insert(f.events.end(), ev, ev + ne);
    f.drain();
    CHECK(ne > 0 && ne < 128);
    CHECK(ne > 0 && ev[ne - 1].kind == WF_EVENT_STOPPED);
    CHECK(f.count(WF_EVENT_STOPPED) == 1);
    bool sequence_ok = true;
    for (size_t i = 1; i < f.events.size(); ++i) {
        if (f.events[i].sequence <= f.events[i - 1].sequence) sequence_ok = false;
    }
    CHECK(sequence_ok);
    const WfMetrics m = f.metrics();
    CHECK(m.state == WF_STATE_STOPPED);
    CHECK(m.frames_me + m.frames_others == f.frames.size());   // all in hand with Stopped
    CHECK(f.frames_of(WF_SOURCE_OTHERS) == static_cast<size_t>(ticked + pending));
    CHECK(f.frames_of(WF_SOURCE_ME) == static_cast<size_t>(ticked + pending));
    CHECK(m.frames_dropped == 0 && m.events_dropped == 0);
    CHECK(f.reasons_ok());

    // Nothing after Stopped: more input, a refused tick and two more stops add nothing.
    push_block(f, WF_FAKE_SRC_MIC, ticked + pending, true);
    push_block(f, WF_FAKE_SRC_OTHERS, ticked + pending, true);
    CHECK(wf_fake_tick(f.s) == WF_E_STATE);
    CHECK(wf_session_stop(f.s) == WF_OK);
    CHECK(wf_session_stop(f.s) == WF_OK);
    WfFrame fb[8];
    uint32_t n = 99;
    CHECK(wf_session_read_frames(f.s, fb, 8, &n) == WF_OK && n == 0);
    n = 99;
    CHECK(wf_session_read_events(f.s, ev, 128, &n) == WF_OK && n == 0);
    const WfMetrics after = f.metrics();
    CHECK(after.frames_me == m.frames_me && after.frames_others == m.frames_others);
    CHECK(after.state == WF_STATE_STOPPED);
    std::printf("  stop ordering: %zu frames (%d never ticked) before Stopped, seq %u, 0 after\n",
                f.frames.size(), 2 * pending, ne > 0 ? ev[ne - 1].sequence : 0u);
}

// ---------------------------------------------------------------------------
// enable_aec = 0: the mic bypasses the bridge. Me frame n is capture frame n (timeline
// n*160, samples unshifted and unchanged) and leaves in the tick that framed it - with or
// without a reference, never flagged unfiltered. Stop adds no flush frames; ERLE, delay,
// unfiltered and reset counters stay 0. Same result with the AEC3 build and the stub.
// ---------------------------------------------------------------------------
void test_aec_disabled() {
    const double base = 2.0;
    Fake f;
    CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, base, 0, 0));
    CHECK(f.start());
    f.tick();

    const int both = 30;       // mic and Others
    const int total = 40;      // then 100 ms of mic only (below the 0.5 s watchdog)
    bool immediate = true;
    for (int n = 0; n < total; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        if (n < both) push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
        if (f.frames_of(WF_SOURCE_ME) != static_cast<size_t>(n + 1)) immediate = false;
    }
    CHECK(immediate);
    const size_t me_before_stop = f.frames_of(WF_SOURCE_ME);
    CHECK(f.stop() == WF_OK);
    CHECK(f.frames_of(WF_SOURCE_ME) == me_before_stop);   // nothing delayed, nothing flushed

    int64_t expect = 0;
    bool grid_ok = true, samples_ok = true, flags_ok = true;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_ME) continue;
        if (fr.timeline_sample != expect || fr.valid_lo != 0 || fr.valid_hi != kBlock) {
            grid_ok = false;
        }
        if (std::fabs(fr.offset_sec - (static_cast<double>(expect) / kRate + base)) > 1e-9) {
            grid_ok = false;
        }
        for (int k = 0; k < kBlock; ++k) {
            if (fr.samples[k] != ramp(expect + k)) samples_ok = false;
        }
        if (fr.flags != 0) flags_ok = false;
        expect += kBlock;
    }
    CHECK(grid_ok);
    CHECK(samples_ok);
    CHECK(flags_ok);
    CHECK(f.frames_of(WF_SOURCE_ME) == static_cast<size_t>(total));
    CHECK(f.frames_of(WF_SOURCE_OTHERS) == static_cast<size_t>(both));
    const WfMetrics m = f.metrics();
    CHECK(m.unfiltered_frames == 0);
    CHECK(m.erle_db == 0.0);
    CHECK(m.delay_ms == 0);
    CHECK(m.aec_resets == 0);
    CHECK(m.ref_discarded == 0);
    CHECK(f.count(WF_EVENT_GAP) == 0);
    CHECK(!f.events.empty() && f.events.back().kind == WF_EVENT_STOPPED);
    CHECK(f.reasons_ok());
    std::printf("  aec off: %zu Me frames at n*160, unshifted, 0 unfiltered, 0 flush frames\n",
                f.frames_of(WF_SOURCE_ME));
}

// ---------------------------------------------------------------------------
// wf_session_get_origin: the exact origin of wf_session_start plus base_duration_sec, and
// the offsets the session publishes follow from those two numbers exactly.
// ---------------------------------------------------------------------------
void test_origin() {
    const double base = 12.25;
    Fake f;
    CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, base));
    int64_t origin = -1;
    double b = -1.0;
    CHECK(wf_session_get_origin(f.s, &origin, &b) == WF_E_STATE);   // no origin before start
    CHECK(origin == -1 && b == -1.0);                                 // outputs untouched
    CHECK(wf_session_get_origin(nullptr, &origin, &b) == WF_E_INVALID_ARG);
    CHECK(wf_session_get_origin(f.s, nullptr, &b) == WF_E_INVALID_ARG);
    CHECK(wf_session_get_origin(f.s, &origin, nullptr) == WF_E_INVALID_ARG);

    // Start 37 ms after create: the origin is the start time, not the creation time.
    f.t0 = f.t + 37 * kTicksPerMs;
    f.set_now(f.t0);
    CHECK(f.start());
    CHECK(wf_session_get_origin(f.s, &origin, &b) == WF_OK);
    CHECK(origin == f.t0);
    CHECK(b == base);
    f.tick();
    for (int n = 0; n < 10; ++n) {
        push_block(f, WF_FAKE_SRC_MIC, n, true);
        push_block(f, WF_FAKE_SRC_OTHERS, n, true);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    const int64_t stop_at = f.t + 3 * kTicksPerMs;
    f.set_now(stop_at);
    CHECK(f.stop() == WF_OK);
    bool frames_ok = !f.frames.empty();
    for (const WfFrame& fr : f.frames) {
        if (fr.offset_sec != static_cast<double>(fr.timeline_sample) / kRate + b) frames_ok = false;
    }
    CHECK(frames_ok);
    const WfEvent* stopped = f.first(WF_EVENT_STOPPED);
    CHECK(stopped != nullptr);
    if (stopped) {
        CHECK(stopped->at_offset_sec == static_cast<double>(stop_at - origin) / 1e7 + b);
    }
    int64_t origin2 = 0;
    double b2 = 0.0;
    CHECK(wf_session_get_origin(f.s, &origin2, &b2) == WF_OK);   // still valid after stop
    CHECK(origin2 == origin && b2 == b);
    std::printf("  origin: %lld (start), base %.2f s\n", static_cast<long long>(origin), b);
}

}  // namespace

void test_frames() {
    test_timeline_and_compensation();
    test_attempt_fencing();
    test_unfiltered_after_100ms();
    test_levels();
    test_stop_flush();
    test_partial_frames();
    test_stop_ordering();
    test_aec_disabled();
    test_origin();
}
