// Backpressure: what happens when a stage cannot keep up. A stalled DSP thread loses
// nothing as long as the capture ring (1024 slots) holds the backlog - the tick works it off
// in 100 ms slices; only a ring overflow is a loss, reported ONCE per interval and source as
// "Input buffer overflowed" (spec 3.5; before 2026-11-23 the 240 ms inbox dropped audio the
// ring still held, and every such loss came with a TimestampDiscontinuity twin). The echo
// processor's 49-frame store records "Processing buffer overflowed", adjacent gaps are
// coalesced into one event (Mac CaptureEventInbox), and both output queues drop the newest
// item and count it, so the caller can always tell loss from silence.
#include <map>
#include <vector>

#include "session/event_queue.h"
#include "test_util.h"

using harness::Fake;

namespace {

constexpr uint32_t kRate = 16000;
constexpr int kBlock = 160;
constexpr int64_t kBlockTicks = 100000;
constexpr uint32_t kDataDiscontinuity = 0x1u;   // AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY

void push_block(Fake& f, uint32_t source, int64_t n, uint32_t flags = 0) {
    float buf[kBlock] = {};
    f.push(source, buf, kBlock, kRate, f.t0 + n * kBlockTicks, flags);
}

// One block of each source per 10 ms tick, continuing the block counter `n`.
void run_healthy(Fake& f, int64_t& n, int ms) {
    for (int i = 0; i < ms / 10; ++i) {
        push_block(f, WF_FAKE_SRC_MIC, n);
        push_block(f, WF_FAKE_SRC_OTHERS, n);
        ++n;
        f.set_now(f.t0 + n * kBlockTicks);
        f.tick();
    }
}

// `ms` of capture on both sources while the DSP gets no tick at all. The clock moves with
// the packets (only the DSP is stalled), so the 0.5 s watchdog has no reason to fire.
void stall(Fake& f, int64_t& n, int ms) {
    for (int i = 0; i < ms / 10; ++i) {
        push_block(f, WF_FAKE_SRC_MIC, n);
        push_block(f, WF_FAKE_SRC_OTHERS, n);
        ++n;
        f.set_now(f.t0 + n * kBlockTicks);
    }
}

WfDiagnostics diagnostics(Fake& f) {
    WfDiagnostics d;
    std::memset(&d, 0, sizeof(d));
    d.size = sizeof(d);
    wf_session_get_diagnostics(f.s, &d);
    return d;
}

size_t valid_samples(const Fake& f, uint32_t source) {
    size_t n = 0;
    for (const WfFrame& fr : f.frames) {
        if (fr.source == source) n += static_cast<size_t>(fr.valid_hi - fr.valid_lo);
    }
    return n;
}

// ---------------------------------------------------------------------------
// Spec 3.5 "CaptureInbox je Quelle, max. 240 ms" - but on Windows the cross-thread buffer is
// the capture ring, so a DSP stall longer than 240 ms must NOT overflow the inbox any more
// (it did until 2026-11-23: 500 ms before one tick lost 260 ms as 26 overflow events). The
// CaptureInbox's own drop-oldest rule is still covered by tests/core/test_inbox.cpp.
// ---------------------------------------------------------------------------
void test_inbox_overflow() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    // 500 ms of audio arrives before the DSP gets a single tick: more than twice the
    // 240 ms inbox. The clock moves with the packets (only the DSP is stalled), otherwise
    // the 0.5 s watchdog would rebuild the stream.
    for (int n = 0; n < 50; ++n) {
        f.set_now(f.t0 + n * kBlockTicks);
        push_block(f, WF_FAKE_SRC_MIC, n);
    }
    f.set_now(f.t0 + 50 * kBlockTicks);
    f.tick();
    CHECK(f.stop() == WF_OK);

    CHECK(f.count_gap(WF_GAP_INBOX_OVERFLOW) == 0);
    CHECK(f.count(WF_EVENT_GAP) == 0);
    CHECK(f.metrics().inbox_overflows == 0);
    CHECK(f.metrics().ring_overflows == 0);
    CHECK(valid_samples(f, WF_SOURCE_ME) == 50u * kBlock);   // every mic sample came out
    const WfDiagnostics d = diagnostics(f);
    CHECK(d.ring_high_water_me == 50);
    CHECK(d.dsp_slices_max == 5);                             // 500 ms in 100 ms slices
    std::printf("  inbox: 500 ms stall -> %llu overflow events, %zu Me samples, %u slices\n",
                static_cast<unsigned long long>(f.metrics().inbox_overflows),
                valid_samples(f, WF_SOURCE_ME), d.dsp_slices_max);
}

// ---------------------------------------------------------------------------
// F1: a stall the ring can hold loses nothing on either source - 0 gaps, every frame, and
// the canceller never runs a frame without its reference.
// ---------------------------------------------------------------------------
void test_dsp_stall_within_ring_loses_nothing(int stall_ms) {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    run_healthy(f, n, 200);        // anchored and converging before the stall
    stall(f, n, stall_ms);
    f.tick();                      // one tick works the whole backlog off
    run_healthy(f, n, 200);
    CHECK(f.stop() == WF_OK);

    CHECK(f.count(WF_EVENT_GAP) == 0);
    const WfMetrics m = f.metrics();
    CHECK(m.inbox_overflows == 0);
    CHECK(m.ring_overflows == 0);
    CHECK(m.frames_dropped == 0);
    CHECK(m.unfiltered_frames == 0);    // the reference was there for every mic frame
    CHECK(m.ref_discarded == 0);
    CHECK(f.count_gap(WF_GAP_PROCESSING_OVERFLOW) == 0);

    // Others: one frame per block, on a gapless grid. Me: every captured sample.
    std::vector<int64_t> others;
    for (const WfFrame& fr : f.frames) {
        if (fr.source == WF_SOURCE_OTHERS) others.push_back(fr.timeline_sample);
    }
    CHECK(others.size() == static_cast<size_t>(n));
    bool contiguous = true;
    for (size_t i = 0; i < others.size(); ++i) {
        if (others[i] != static_cast<int64_t>(i) * kBlock) contiguous = false;
    }
    CHECK(contiguous);
    CHECK(valid_samples(f, WF_SOURCE_ME) == static_cast<size_t>(n) * kBlock);

    const WfDiagnostics d = diagnostics(f);
    CHECK(d.ring_high_water_me == static_cast<uint32_t>(stall_ms / 10));
    CHECK(d.ring_high_water_others == static_cast<uint32_t>(stall_ms / 10));
    CHECK(d.dsp_slices_max == static_cast<uint32_t>(stall_ms / 100));
    CHECK_NEAR(d.dsp_tick_gap_max_ms, static_cast<double>(stall_ms), 1e-6);
    CHECK(d.ring_loss_gaps == 0);
    std::printf("  stall %4d ms: 0 gaps, %zu Others frames, %zu Me samples, %u slices, "
                "high water %u slots\n",
                stall_ms, others.size(), valid_samples(f, WF_SOURCE_ME), d.dsp_slices_max,
                d.ring_high_water_me);
}

// ---------------------------------------------------------------------------
// AEC alignment across a stall: the canceller must see exactly the render/capture sequence
// it sees in real time. The same echo-laden capture is run once tick by tick and once with
// a 1.5 s DSP stall in the middle; the cleaned Me signal must be identical sample for sample.
// The two streams deliberately use different packet sizes (reference 10 ms, mic 15 ms, both
// 48 kHz), so a slice's mic audio regularly reaches past its reference: those mic frames
// must wait for the next slice (slice clock), not run unfiltered against the wall clock.
// ---------------------------------------------------------------------------
struct EchoRun {
    std::map<int64_t, WfFrame> me;   // by timeline_sample
    WfMetrics metrics;
};

EchoRun run_echo(int stall_at_tick, int stall_ticks) {
    constexpr uint32_t kRate48 = 48000;
    constexpr int kTicks = 400;             // 4 s
    constexpr int kRefPacket = 480;         // 10 ms
    constexpr int kMicPacket = 720;         // 15 ms
    constexpr int kDelay = 144;             // echo path: 3 ms
    const size_t total = static_cast<size_t>(kTicks) * kRefPacket;
    std::vector<float> ref(total, 0.0f);
    uint32_t seed = 12345u;
    for (float& v : ref) {
        seed = seed * 1664525u + 1013904223u;
        v = (static_cast<float>(seed >> 8) / 16777216.0f - 0.5f) * 0.6f;
    }
    std::vector<float> mic(total, 0.0f);
    for (size_t i = kDelay; i < mic.size(); ++i) mic[i] = 0.5f * ref[i - kDelay];
    const auto ts_of = [](const Fake& f, size_t sample) {
        return f.t0 + static_cast<int64_t>(static_cast<double>(sample) * 1e7 / kRate48 + 0.5);
    };

    EchoRun out;
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    size_t mic_at = 0;
    for (int b = 0; b < kTicks; ++b) {
        // Real-time delivery: a packet arrives once its last sample is captured.
        f.set_now(f.t0 + (b + 1) * kBlockTicks);
        const size_t ref_at = static_cast<size_t>(b) * kRefPacket;
        f.push(WF_FAKE_SRC_OTHERS, ref.data() + ref_at, kRefPacket, kRate48, ts_of(f, ref_at));
        const size_t captured = ref_at + kRefPacket;
        while (mic_at + kMicPacket <= captured) {
            f.push(WF_FAKE_SRC_MIC, mic.data() + mic_at, kMicPacket, kRate48, ts_of(f, mic_at));
            mic_at += kMicPacket;
        }
        const bool stalled = b >= stall_at_tick && b < stall_at_tick + stall_ticks;
        if (!stalled) f.tick();
    }
    CHECK(f.stop() == WF_OK);
    for (const WfFrame& fr : f.frames) {
        if (fr.source == WF_SOURCE_ME) out.me[fr.timeline_sample] = fr;
    }
    out.metrics = f.metrics();
    CHECK(f.count(WF_EVENT_GAP) == 0);
    return out;
}

void test_dsp_stall_keeps_the_echo_canceller_aligned() {
    const EchoRun live = run_echo(0, 0);
    const EchoRun stalled = run_echo(100, 150);   // 1.5 s without a tick from 1.0 s on
    CHECK(live.me.size() == stalled.me.size());
    double max_diff = 0.0;
    size_t compared = 0;
    for (const auto& [pos, a] : live.me) {
        auto it = stalled.me.find(pos);
        if (it == stalled.me.end()) {
            CHECK(false);
            continue;
        }
        const WfFrame& b = it->second;
        CHECK(a.valid_lo == b.valid_lo && a.valid_hi == b.valid_hi && a.flags == b.flags);
        for (int i = a.valid_lo; i < a.valid_hi; ++i) {
            const double d = std::fabs(static_cast<double>(a.samples[i]) - b.samples[i]);
            if (d > max_diff) max_diff = d;
        }
        ++compared;
    }
    CHECK(max_diff == 0.0);                                   // bit-identical
    CHECK(live.metrics.unfiltered_frames == stalled.metrics.unfiltered_frames);
    CHECK(stalled.metrics.unfiltered_frames == 0);
    CHECK(live.metrics.erle_db == stalled.metrics.erle_db);
    std::printf("  aec across a 1.5 s stall: %zu Me frames compared, max diff %.3g, "
                "erle %.2f / %.2f dB, unfiltered %llu / %llu\n",
                compared, max_diff, live.metrics.erle_db, stalled.metrics.erle_db,
                static_cast<unsigned long long>(live.metrics.unfiltered_frames),
                static_cast<unsigned long long>(stalled.metrics.unfiltered_frames));
}

// ---------------------------------------------------------------------------
// AP3: a render switch mid-stream (headset -> loudspeakers, mode 2) restarts the canceller
// cleanly. Before the switch the mic hears the old endpoint's audio over one echo path; from
// the switch on it hears the new endpoint's audio over another, and the new loopback delivers
// that reference 60 ms after it opened. The cleaned Me signal from the switch on must be what
// a FRESH session produces from exactly the audio after the switch: no stale reference of
// the old endpoint, no filter state learned on the old path, the same mic/reference
// alignment (slice clock) and the same latency compensation. Deterministic: with a reset
// bridge and identical inputs the output is bit-identical.
// ---------------------------------------------------------------------------
struct SwitchRun {
    std::map<int64_t, WfFrame> me;   // by timeline_sample
    WfMetrics metrics;
    WfDiagnostics diag;
    std::vector<WfEvent> gaps;
};

constexpr int kSwS0 = 150;               // the default changes after block 150 (1.5 s)
// Block whose tick carries the switch out: the notification is taken in the next poll (+10
// ms), the 200 ms debounce runs from there.
constexpr int kSwBlock = kSwS0 + 21;
constexpr int kSwOnset = 6;              // the new loopback delivers from kSwBlock + 6 on
constexpr int kSwTotal = 450;            // 4.5 s

std::vector<float> noise(uint32_t seed, size_t n, float amp) {
    std::vector<float> v(n);
    for (float& x : v) {
        seed = seed * 1664525u + 1013904223u;
        x = (static_cast<float>(seed >> 8) / 16777216.0f - 0.5f) * amp;
    }
    return v;
}

// `fresh`: a new session that only ever sees the audio from kSwBlock on.
SwitchRun run_render_switch(bool fresh) {
    const size_t total = static_cast<size_t>(kSwTotal) * kBlock;
    const std::vector<float> ref1 = noise(111u, total, 0.6f);   // old endpoint (headset)
    const std::vector<float> ref2 = noise(222u, total, 0.6f);   // new endpoint (speakers)
    std::vector<float> mic(total, 0.0f);
    const size_t cut = static_cast<size_t>(kSwBlock) * kBlock;
    for (size_t p = 0; p < total; ++p) {
        if (p < cut) mic[p] = p >= 48 ? 0.5f * ref1[p - 48] : 0.0f;       // 3 ms path
        else mic[p] = p >= 160 ? 0.3f * ref2[p - 160] : 0.0f;             // 10 ms path
    }

    SwitchRun out;
    Fake f;
    CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK));
    CHECK(f.start());
    f.tick();
    const int first = fresh ? kSwBlock : 0;
    for (int b = first; b < kSwTotal; ++b) {
        const int64_t j = b - first;                 // block on this session's clock
        f.set_now(f.t0 + (j + 1) * kBlockTicks);
        const size_t at = static_cast<size_t>(b) * kBlock;
        if (!fresh && b <= kSwBlock) {
            // The old endpoint; block kSwBlock lands in the old ring as the switch happens.
            f.push(WF_FAKE_SRC_OTHERS, ref1.data() + at, kBlock, kRate, f.t0 + j * kBlockTicks);
        } else if (b >= kSwBlock + kSwOnset) {
            f.push(WF_FAKE_SRC_OTHERS, ref2.data() + at, kBlock, kRate, f.t0 + j * kBlockTicks);
        }
        f.push(WF_FAKE_SRC_MIC, mic.data() + at, kBlock, kRate, f.t0 + j * kBlockTicks);
        const int switched_before = f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS);
        f.tick();
        if (!fresh && b == kSwS0) {
            CHECK(wf_fake_signal(f.s, WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 1) == WF_OK);
        }
        if (!fresh && b == kSwBlock) {
            CHECK(switched_before == 0 && f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS) == 1);
        }
    }
    if (!fresh) {
        uint32_t opens = 0;
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK, &opens) == L"{0.0.0.00000000}.{fake-render-1}");
        CHECK(opens == 2);
    }
    CHECK(f.stop() == WF_OK);
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_ME) continue;
        // On the switched session's grid: block kSwBlock of the fresh one is block 0.
        const int64_t pos = fr.timeline_sample + (fresh ? static_cast<int64_t>(cut) : 0);
        out.me[pos] = fr;
    }
    for (const WfEvent& e : f.events) {
        if (e.kind == WF_EVENT_GAP) out.gaps.push_back(e);
    }
    out.metrics = f.metrics();
    out.diag = diagnostics(f);
    return out;
}

void test_render_switch_restarts_the_echo_canceller_cleanly() {
    const SwitchRun switched = run_render_switch(false);
    const SwitchRun fresh = run_render_switch(true);
    const int64_t cut = static_cast<int64_t>(kSwBlock) * kBlock;

    // The switched session: one hand-over gap on Others - blocks kSwBlock (it reached the old
    // ring as the switch happened) to kSwBlock + 5 - one canceller reset, nothing else.
    CHECK(switched.gaps.size() == 1);
    if (switched.gaps.size() == 1) {
        CHECK(switched.gaps[0].source == WF_SOURCE_OTHERS &&
              switched.gaps[0].reason == WF_GAP_RESTART);
        CHECK_NEAR(switched.gaps[0].duration_sec, kSwOnset * 0.010, 1e-6);
    }
    CHECK(fresh.gaps.empty());
    CHECK(switched.metrics.aec_resets == 1);
    CHECK(switched.diag.aec_render_resets == 1);
    CHECK(fresh.metrics.aec_resets == 0);

    // Every Me frame of the fresh session has its twin in the switched one, sample for sample.
    size_t compared = 0, missing = 0;
    double max_diff = 0.0;
    bool same_shape = true;
    for (const auto& [pos, b] : fresh.me) {
        auto it = switched.me.find(pos);
        if (it == switched.me.end()) {
            ++missing;
            continue;
        }
        const WfFrame& a = it->second;
        if (a.valid_lo != b.valid_lo || a.valid_hi != b.valid_hi || a.flags != b.flags) {
            same_shape = false;
        }
        for (int i = b.valid_lo; i < b.valid_hi; ++i) {
            const double d = std::fabs(static_cast<double>(a.samples[i]) - b.samples[i]);
            if (d > max_diff) max_diff = d;
        }
        ++compared;
    }
    size_t after_cut = 0;
    for (const auto& kv : switched.me) {
        if (kv.first >= cut) ++after_cut;
    }
    CHECK(missing == 0);
    CHECK(compared == after_cut);                      // and nothing extra after the switch
    CHECK(compared >= static_cast<size_t>(kSwTotal - kSwBlock - 1));
    CHECK(same_shape);
    CHECK(max_diff <= 1e-6);
    // The frames while the new reference was not there yet ran unfiltered in both.
    CHECK(switched.metrics.unfiltered_frames == fresh.metrics.unfiltered_frames);
    CHECK(fresh.metrics.unfiltered_frames == static_cast<uint64_t>(kSwOnset));
    // Before the switch the mic was cleaned normally: the old reference was there.
    size_t before_cut = 0;
    for (const auto& kv : switched.me) {
        if (kv.first < cut) ++before_cut;
    }
    CHECK(before_cut == static_cast<size_t>(kSwBlock));   // the delayed tail was flushed out
    std::printf("  aec across a render switch: %zu Me frames vs a fresh session, max diff %.3g, "
                "unfiltered %llu / %llu, erle %.2f / %.2f dB, gap %.3f s\n",
                compared, max_diff,
                static_cast<unsigned long long>(switched.metrics.unfiltered_frames),
                static_cast<unsigned long long>(fresh.metrics.unfiltered_frames),
                switched.metrics.erle_db, fresh.metrics.erle_db,
                switched.gaps.empty() ? -1.0 : switched.gaps[0].duration_sec);
}

// ---------------------------------------------------------------------------
// F2: a stall beyond the ring loses audio - reported exactly once per source, as
// WF_GAP_INBOX_OVERFLOW over exactly the dropped packets, with no TimestampDiscontinuity
// twin. A genuine extra hole on top of the loss is still reported on its own.
// ---------------------------------------------------------------------------
void test_one_loss_is_reported_once() {
    // 5 ms packets: the 1024 ring slots then hold 5.12 s, which keeps the catch-up below the
    // 2048-frame output queue (the fake reader only reads after the tick).
    constexpr int kSmall = 80;
    constexpr int64_t kSmallTicks = 50000;
    constexpr int kStallPackets = 1100;      // 76 more than the ring holds
    constexpr int kLost = kStallPackets - 1024;
    for (int extra_hole_ms : {0, 50}) {
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        float buf[kSmall] = {};
        int64_t n = 0;
        // Others packets after the loss may carry an extra (device) hole of extra_hole_ms.
        int64_t others_shift = 0;
        auto push_both = [&](int64_t k) {
            f.push(WF_FAKE_SRC_MIC, buf, kSmall, kRate, f.t0 + k * kSmallTicks);
            f.push(WF_FAKE_SRC_OTHERS, buf, kSmall, kRate, f.t0 + k * kSmallTicks + others_shift);
        };
        for (int i = 0; i < 40; ++i) {        // 200 ms healthy, two packets per tick
            push_both(n++);
            push_both(n++);
            f.set_now(f.t0 + n * kSmallTicks);
            f.tick();
        }
        const int64_t lost_from = n + 1024;
        for (int i = 0; i < kStallPackets; ++i) {
            push_both(n++);
            f.set_now(f.t0 + n * kSmallTicks);
        }
        f.tick();                              // the ring's 5.12 s come out
        others_shift = extra_hole_ms * 10000;
        for (int i = 0; i < 40; ++i) {
            push_both(n++);
            push_both(n++);
            f.set_now(f.t0 + n * kSmallTicks + others_shift);
            f.tick();
        }
        CHECK(f.stop() == WF_OK);

        int me = 0, others = 0, discontinuities = 0, other_gaps = 0;
        for (const WfEvent& e : f.events) {
            if (e.kind != WF_EVENT_GAP) continue;
            if (e.reason == WF_GAP_INBOX_OVERFLOW) {
                if (e.source == WF_SOURCE_ME) ++me;
                if (e.source == WF_SOURCE_OTHERS) ++others;
                CHECK_NEAR(e.at_offset_sec, f.offset_of(f.t0 + lost_from * kSmallTicks), 1e-9);
                CHECK_NEAR(e.duration_sec, kLost * 0.005, 1e-9);
            } else if (e.reason == WF_GAP_TIMESTAMP_DISCONTINUITY) {
                ++discontinuities;
                CHECK(e.source == WF_SOURCE_OTHERS);
                // The extra hole follows the reported loss directly.
                CHECK_NEAR(e.at_offset_sec,
                           f.offset_of(f.t0 + lost_from * kSmallTicks) + kLost * 0.005, 1e-3);
                CHECK_NEAR(e.duration_sec, extra_hole_ms / 1000.0, 1e-3);
            } else {
                ++other_gaps;
            }
        }
        CHECK(me == 1);
        CHECK(others == 1);
        CHECK(discontinuities == (extra_hole_ms > 0 ? 1 : 0));
        CHECK(other_gaps == 0);
        const WfMetrics m = f.metrics();
        CHECK(m.ring_overflows == 2u * kLost);
        CHECK(m.inbox_overflows == 0);
        CHECK(m.frames_dropped == 0);
        const WfDiagnostics d = diagnostics(f);
        CHECK(d.ring_loss_gaps == 2);
        CHECK(d.ring_high_water_me == d.ring_capacity_slots);
        CHECK(d.ring_capacity_slots == 1024);
        // Every packet the ring kept reached the output.
        CHECK(valid_samples(f, WF_SOURCE_OTHERS) == static_cast<size_t>(n - kLost) * kSmall);
        CHECK(valid_samples(f, WF_SOURCE_ME) == static_cast<size_t>(n - kLost) * kSmall);
        std::printf("  ring loss (+%d ms hole): Me %d / Others %d overflow gap(s) of %.3f s, "
                    "%d discontinuity, %llu dropped packets\n",
                    extra_hole_ms, me, others, kLost * 0.005, discontinuities,
                    static_cast<unsigned long long>(m.ring_overflows));
    }
}

// ---------------------------------------------------------------------------
// Event queue overflow is counted and visible as a jump in WfEvent.sequence.
// ---------------------------------------------------------------------------
void test_event_queue_overflow() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    // Every packet claims a data discontinuity, which is one gap event each.
    for (int n = 0; n < 700; ++n) {
        push_block(f, WF_FAKE_SRC_OTHERS, n, kDataDiscontinuity);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick_raw();                            // deliberately not draining
    }
    WfMetrics m = f.metrics();
    CHECK(m.discontinuities >= 600);
    CHECK(m.events_dropped > 0);

    // A full queue drops the newest event, so what is delivered is the oldest 512 with
    // a contiguous numbering; the loss shows up on the next event the reader gets,
    // because the sequence counter also advanced for everything that was thrown away.
    f.drain();
    CHECK(f.events.size() == 512);
    uint32_t last = 0;
    bool monotonic = true;
    for (const WfEvent& e : f.events) {
        if (e.sequence <= last) monotonic = false;
        last = e.sequence;
    }
    CHECK(monotonic);
    CHECK(last == 512);

    const size_t before = f.events.size();
    push_block(f, WF_FAKE_SRC_OTHERS, 700, kDataDiscontinuity);
    f.set_now(f.t0 + 701 * kBlockTicks);
    f.tick();
    CHECK(f.events.size() > before);
    const uint32_t next = f.events.back().sequence;
    CHECK(next > last + 1);                      // the jump proves events were lost
    std::printf("  events: %llu dropped, %zu delivered (last %u), next sequence %u\n",
                static_cast<unsigned long long>(m.events_dropped), before, last, next);
}

// ---------------------------------------------------------------------------
// Frame queue: >= 2 s of WfFrame, newest dropped and counted when it is full.
// ---------------------------------------------------------------------------
void test_frame_queue_overflow() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();

    // 2200 Others frames without a single read; the queue holds 2048.
    for (int i = 0; i < 220; ++i) {
        for (int j = 0; j < 10; ++j) push_block(f, WF_FAKE_SRC_OTHERS, i * 10 + j);
        f.set_now(f.t0 + (i + 1) * 10 * kBlockTicks);
        f.tick_raw();
    }
    const WfMetrics m = f.metrics();
    CHECK(m.frames_dropped > 0);
    CHECK(m.frames_others >= 2000);
    f.drain();
    CHECK(f.frames.size() == 2048);
    std::printf("  frames: %llu queued, %llu dropped, %zu read back\n",
                static_cast<unsigned long long>(m.frames_others),
                static_cast<unsigned long long>(m.frames_dropped), f.frames.size());
}

// ---------------------------------------------------------------------------
// Gap coalescing through the session (Mac CaptureEventInbox.append): two losses of one
// source 10 ms apart (inside the 11 ms slack) become one event; the same losses on two
// sources stay two. Until 2026-11-23 this used two inbox overflows in one tick; a stall
// no longer loses anything, so two DATA_DISCONTINUITY holes take their place.
// ---------------------------------------------------------------------------
void push_with_hole(Fake& f, uint32_t source, int64_t n) {
    // Block n claims a discontinuity: the device skipped one 10 ms block before it.
    float buf[kBlock] = {};
    f.push(source, buf, kBlock, kRate, f.t0 + n * kBlockTicks, kDataDiscontinuity,
           static_cast<uint64_t>(n) * kBlock);
}

void push_at(Fake& f, uint32_t source, int64_t n) {
    float buf[kBlock] = {};
    f.push(source, buf, kBlock, kRate, f.t0 + n * kBlockTicks, 0, static_cast<uint64_t>(n) * kBlock);
}

void test_gap_coalescing_session() {
    {
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        // Blocks 0, 2 and 4 arrive; 2 and 4 report the skipped block before them.
        push_at(f, WF_FAKE_SRC_MIC, 0);
        push_with_hole(f, WF_FAKE_SRC_MIC, 2);
        push_with_hole(f, WF_FAKE_SRC_MIC, 4);
        f.set_now(f.t0 + 5 * kBlockTicks);
        f.tick();
        CHECK(f.metrics().discontinuities == 2);                  // two losses in the core ...
        CHECK(f.count_gap(WF_GAP_TIMESTAMP_DISCONTINUITY) == 1);  // ... one event
        const WfEvent* g = f.first(WF_EVENT_GAP);
        CHECK(g != nullptr);
        if (g) {
            CHECK(g->source == WF_SOURCE_ME);
            CHECK(g->reason == WF_GAP_TIMESTAMP_DISCONTINUITY);
            CHECK_NEAR(g->at_offset_sec, 0.01, 1e-9);
            CHECK_NEAR(g->duration_sec, 0.03, 1e-9);              // 0.01 .. 0.04
        }
        // Merged gaps never take a sequence number: the numbering has no hole.
        uint32_t last = 0;
        bool dense = true;
        for (const WfEvent& e : f.events) {
            if (e.sequence != last + 1) dense = false;
            last = e.sequence;
        }
        CHECK(dense);
        CHECK(f.metrics().events_dropped == 0);
    }
    {
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        for (uint32_t src : {WF_FAKE_SRC_MIC, WF_FAKE_SRC_OTHERS}) {
            push_at(f, src, 0);
            push_with_hole(f, src, 2);
            push_with_hole(f, src, 4);
        }
        f.set_now(f.t0 + 5 * kBlockTicks);
        f.tick();
        CHECK(f.metrics().discontinuities == 4);
        CHECK(f.count_gap(WF_GAP_TIMESTAMP_DISCONTINUITY) == 2);  // one per source
        int me = 0, others = 0;
        for (const WfEvent& e : f.events) {
            if (e.kind != WF_EVENT_GAP || e.reason != WF_GAP_TIMESTAMP_DISCONTINUITY) continue;
            if (e.source == WF_SOURCE_ME) ++me;
            if (e.source == WF_SOURCE_OTHERS) ++others;
            CHECK_NEAR(e.duration_sec, 0.03, 1e-9);
        }
        CHECK(me == 1 && others == 1);
    }
    std::printf("  coalescing: 2 holes 10 ms apart -> 1 event; 2 sources -> 2 events\n");
}

// ---------------------------------------------------------------------------
// The merge rule itself, on the staging class the session uses: same source and reason,
// start inside [previous.start, previous end + 11 ms]. A different reason or source never
// merges, a non-gap event in between does not stop the merge (Mac lastIndex(where:)), and
// a full stage commits early without reordering anything.
// ---------------------------------------------------------------------------
WfEvent gap_event(uint32_t source, uint32_t reason, double at, double duration) {
    WfEvent e = wf::make_event(WF_EVENT_GAP, source, 1, at);
    e.reason = reason;
    e.duration_sec = duration;
    return e;
}

void test_gap_coalescing_rule() {
    wf::EventQueue q;
    q.init(256);
    wf::EventCoalescer c;

    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_INBOX_OVERFLOW, 1.000, 0.010), q);
    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_INBOX_OVERFLOW, 1.010, 0.010), q);    // adjacent
    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_INBOX_OVERFLOW, 1.030, 0.005), q);    // 10 ms hole
    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_PROCESSING_OVERFLOW, 1.035, 0.010), q);  // reason
    c.stage(gap_event(WF_SOURCE_OTHERS, WF_GAP_INBOX_OVERFLOW, 1.035, 0.010), q);   // source
    c.stage(wf::make_event(WF_EVENT_CLOCK_JITTER_WARNING, WF_SOURCE_ME, 1, 1.040), q);
    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_INBOX_OVERFLOW, 1.040, 0.010), q);    // past others
    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_INBOX_OVERFLOW, 1.200, 0.010), q);    // > 11 ms
    c.stage(gap_event(WF_SOURCE_ME, WF_GAP_INBOX_OVERFLOW, 1.190, 0.005), q);    // earlier start
    CHECK(c.staged() == 6);
    CHECK(c.merged() == 3);
    CHECK(q.size() == 0);                     // nothing reaches the queue before commit
    CHECK(c.commit(q) == 6);
    CHECK(c.staged() == 0);

    WfEvent out[16];
    const size_t n = q.pop(out, 16);
    CHECK(n == 6);
    if (n == 6) {
        CHECK(out[0].reason == WF_GAP_INBOX_OVERFLOW && out[0].source == WF_SOURCE_ME);
        CHECK_NEAR(out[0].at_offset_sec, 1.000, 1e-12);
        CHECK_NEAR(out[0].duration_sec, 0.050, 1e-12);     // 1.000 .. 1.050
        CHECK(out[1].reason == WF_GAP_PROCESSING_OVERFLOW);
        CHECK_NEAR(out[1].duration_sec, 0.010, 1e-12);
        CHECK(out[2].source == WF_SOURCE_OTHERS);
        CHECK(out[3].kind == WF_EVENT_CLOCK_JITTER_WARNING);
        CHECK_NEAR(out[4].at_offset_sec, 1.200, 1e-12);
        CHECK_NEAR(out[5].at_offset_sec, 1.190, 1e-12);
        bool dense = true;
        for (size_t i = 0; i < n; ++i) {
            if (out[i].sequence != static_cast<uint32_t>(i + 1)) dense = false;
        }
        CHECK(dense);
    }

    // 70 separate gaps: the 64-slot stage commits early, order and count are kept.
    for (int i = 0; i < 70; ++i) {
        c.stage(gap_event(WF_SOURCE_ME, WF_GAP_LATE_FRAME, 10.0 + i, 0.01), q);
    }
    CHECK(q.size() == wf::EventCoalescer::kCapacity);
    CHECK(c.commit(q) == 70 - wf::EventCoalescer::kCapacity);
    WfEvent many[80];
    const size_t m = q.pop(many, 80);
    CHECK(m == 70);
    bool ordered = true;
    for (size_t i = 0; i < m; ++i) {
        if (std::fabs(many[i].at_offset_sec - (10.0 + static_cast<double>(i))) > 1e-12) {
            ordered = false;
        }
    }
    CHECK(ordered);
    std::printf("  coalescing rule: 9 staged -> 6 events, 70 through a 64-slot stage in order\n");
}

// ---------------------------------------------------------------------------
// The echo processor's 49-frame cap is "Processing buffer overflowed", not the inbox's
// "Input buffer overflowed": WF_GAP_PROCESSING_OVERFLOW, on ME for mic frames and on
// REFERENCE for reference frames - the Others track itself has no hole.
// ---------------------------------------------------------------------------
void test_processing_overflow() {
    {
        // The clock stands still, so no mic frame reaches its 100 ms deadline and no
        // reference ever covers it: the store fills to 49 and then drops its oldest.
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        for (int n = 0; n < 55; ++n) {
            push_block(f, WF_FAKE_SRC_MIC, n);
            f.tick();
        }
        int me = 0;
        const WfEvent* first = nullptr;
        for (const WfEvent& e : f.events) {
            if (e.kind != WF_EVENT_GAP || e.reason != WF_GAP_PROCESSING_OVERFLOW) continue;
            if (e.source == WF_SOURCE_ME) ++me;
            if (!first) first = &e;
        }
        CHECK(me == 6);                                     // frames 0 .. 5, one per tick
        CHECK(f.count_gap(WF_GAP_INBOX_OVERFLOW) == 0);
        CHECK(f.metrics().inbox_overflows == 0);
        CHECK(first != nullptr);
        if (first) {
            CHECK(first->source == WF_SOURCE_ME);
            CHECK(first->attempt_id == 1);
            CHECK_NEAR(first->at_offset_sec, 0.0, 1e-9);
            CHECK_NEAR(first->duration_sec, 0.01, 1e-9);
        }
        std::printf("  processing overflow: %d Me gaps after 55 waiting mic frames\n", me);
    }
    {
        // Others only: the reference store keeps 0.5 s by wall clock but at most 49 frames.
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        const int blocks = 80;
        for (int n = 0; n < blocks; ++n) {
            push_block(f, WF_FAKE_SRC_OTHERS, n);
            f.set_now(f.t0 + (n + 1) * kBlockTicks);
            f.tick();
        }
        int reference = 0;
        int others = 0;
        for (const WfEvent& e : f.events) {
            if (e.kind != WF_EVENT_GAP) continue;
            if (e.source == WF_SOURCE_REFERENCE && e.reason == WF_GAP_PROCESSING_OVERFLOW) {
                ++reference;
            }
            if (e.source == WF_SOURCE_OTHERS) ++others;
        }
        CHECK(reference >= 1);
        CHECK(others == 0);
        CHECK(f.frames_of(WF_SOURCE_OTHERS) == static_cast<size_t>(blocks));
        std::printf("  processing overflow: %d reference gaps, Others complete (%zu frames)\n",
                    reference, f.frames_of(WF_SOURCE_OTHERS));
    }
}

}  // namespace

void test_queues() {
    test_inbox_overflow();
    test_dsp_stall_within_ring_loses_nothing(500);
    test_dsp_stall_within_ring_loses_nothing(1500);
    test_dsp_stall_keeps_the_echo_canceller_aligned();
    test_render_switch_restarts_the_echo_canceller_cleanly();
    test_one_loss_is_reported_once();
    test_event_queue_overflow();
    test_frame_queue_overflow();
    test_gap_coalescing_session();
    test_gap_coalescing_rule();
    test_processing_overflow();
}
