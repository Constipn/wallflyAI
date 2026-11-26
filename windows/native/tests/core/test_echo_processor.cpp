// EchoProcessor against the recording mock: render before capture, the 100 ms wait and the
// unfiltered fallback, the 0.5 s reference discard, the 49-frame cap, late/duplicate drops,
// resets only on device/format change, the 224-sample mapping and the zero flush on stop,
// and the enable_aec = false passthrough (no bridge call at all).
#include "core/echo_processor.h"
#include "mock_aec.h"
#include "test_util.h"

#include <cmath>
#include <vector>

using namespace wf;

namespace {

int64_t g_now = 0;
int64_t now_fn(void*) { return g_now; }

constexpr int64_t kOrigin = 0;

// The spec numbers are pinned here so a change to constants.h cannot silently move the
// mock and the processor together (spec 3.3 step 6 and the constants appendix).
static_assert(k::kAec3DelaySamples == 128, "AEC3 framing delay is 128 samples");
static_assert(k::kNsDelaySamples == 96, "NS delay is 96 samples");
static_assert(k::kLatencyCompensationSamples == 224, "128 + 96 = 224");
static_assert(k::kFlushFrames == 2, "two zero frames flush the 224 sample tail");
static_assert(k::kNsLevel == 2, "NS level 2 = 12 dB");
static_assert(k::kFrameSamples == 160 && k::kSampleRate == 16000, "16 kHz, 10 ms frames");
static_assert(k::kMaxBufferedFramesPerSource == 49, "at most 49 frames per source");
static_assert(k::kReferenceMaxAgeFrames == 50, "reference older than 0.5 s is discarded");
static_assert(k::kReferenceWaitTicks == 1000000, "frame end + 100 ms");
constexpr int kMockDelay = 224;   // literal on purpose: the mock must not follow a typo

Frame160 make_frame(int64_t index, float value) {
    Frame160 f;
    f.index = index;
    f.host_100ns = kOrigin + index * k::kTicksPerFrame;
    for (int i = 0; i < k::kFrameSamples; ++i) f.s[i] = value;
    return f;
}

// A reproducible "voice" so a wrong render/capture order cannot cancel by accident.
float sig(int64_t position) {
    return 0.5f * std::sin(static_cast<double>(position) * 0.037) +
           0.2f * std::sin(static_cast<double>(position) * 0.211);
}

Frame160 make_signal_frame(int64_t index) {
    Frame160 f;
    f.index = index;
    f.host_100ns = kOrigin + index * k::kTicksPerFrame;
    for (int i = 0; i < k::kFrameSamples; ++i) f.s[i] = sig(index * k::kFrameSamples + i);
    return f;
}

struct Fixture {
    GapLog gaps;
    EchoProcessor ep;
    std::vector<Frame160> me;
    std::vector<Frame160> others;

    Fixture() {
        mock_aec::clear_log();
        mock_aec::set_delay(kMockDelay);
        mock_aec::set_echo_subtraction(true);
        g_now = 0;
        EchoProcessor::Config cfg;
        ep.configure(cfg, &gaps, kOrigin);
        ep.set_clock(now_fn, nullptr);
        me.reserve(256);
        others.reserve(256);
        mock_aec::clear_log();
    }
};

// One aligned reference/mic pair: pure echo, so a correct order cancels it completely.
void push_pair(Fixture& f, int64_t n) {
    f.ep.push_reference(make_signal_frame(n), &f.others);
    f.ep.push_capture(make_signal_frame(n));
    f.ep.process(f.me);
}

// The configured NS level reaches the bridge (spec: level 2 = 12 dB).
void test_ns_level() {
    Fixture f;
    CHECK(mock_aec::ns_level() == k::kNsLevel);
}

// The mock must actually notice a wrong order: capture before render leaves the echo in.
void test_mock_detects_wrong_order() {
    mock_aec::clear_log();
    mock_aec::set_delay(0);
    mock_aec::set_echo_subtraction(true);
    mn_aec* a = mn_aec_create();
    float in[k::kFrameSamples], ref[k::kFrameSamples], out[k::kFrameSamples];
    for (int i = 0; i < k::kFrameSamples; ++i) in[i] = ref[i] = sig(i);
    mn_aec_render(a, ref);
    mn_aec_capture(a, in, out);
    double energy = 0.0;
    for (int i = 0; i < k::kFrameSamples; ++i) energy += out[i] * out[i];
    CHECK(energy == 0.0);                          // correct order cancels the echo
    mn_aec_capture(a, in, out);                    // this time no render ran first
    energy = 0.0;
    for (int i = 0; i < k::kFrameSamples; ++i) energy += out[i] * out[i];
    CHECK(energy > 0.1);
}

// Pure echo (mic == reference) must cancel completely, and the call log must alternate.
void test_render_before_capture() {
    Fixture f;
    const int kFrames = 12;
    for (int64_t n = 0; n < kFrames; ++n) push_pair(f, n);
    CHECK(f.me.size() == static_cast<size_t>(kFrames) - 2);
    double energy = 0.0;
    for (const Frame160& fr : f.me)
        for (int i = 0; i < k::kFrameSamples; ++i) energy += fr.s[i] * fr.s[i];
    CHECK(energy == 0.0);                          // ordering bug would leave residual echo

    const std::vector<mock_aec::Entry>& log = mock_aec::log();
    CHECK(log.size() == static_cast<size_t>(kFrames) * 2);
    bool ok = true;
    for (size_t i = 0; i < log.size(); ++i) {
        const mock_aec::Call want = (i % 2 == 0) ? mock_aec::Call::Render : mock_aec::Call::Capture;
        if (log[i].kind != want) ok = false;
    }
    CHECK(ok);
    CHECK(f.others.size() == static_cast<size_t>(kFrames));   // Others passes through
    CHECK(f.gaps.size() == 0);
    CHECK(mock_aec::resets() == 0);
}

// Digital silence is a valid reference: no waiting, no unfiltered frames.
void test_silence_is_valid_reference() {
    Fixture f;
    for (int64_t n = 0; n < 8; ++n) {
        f.ep.push_reference(make_frame(n, 0.0f), &f.others);
        f.ep.push_capture(make_signal_frame(n));
        f.ep.process(f.me);
    }
    CHECK(f.ep.stats().unfiltered_frames == 0);
    CHECK(f.ep.stats().captured == 8);
    CHECK(f.gaps.size() == 0);
}

// No reference: hold until frame end + 100 ms, then run with one zero reference frame.
void test_wait_then_unfiltered() {
    Fixture f;
    const Frame160 mic = make_signal_frame(3);
    f.ep.push_capture(mic);
    g_now = mic.host_100ns + k::kTicksPerFrame + 50 * k::kTicksPerMs;   // only 50 ms later
    f.ep.process(f.me);
    CHECK(f.ep.stats().captured == 0);                                   // still waiting
    CHECK(mock_aec::renders() == 0);

    g_now = mic.host_100ns + k::kTicksPerFrame + k::kReferenceWaitTicks; // exactly 100 ms
    f.ep.process(f.me);
    CHECK(f.ep.stats().captured == 1);
    CHECK(f.ep.stats().unfiltered_frames == 1);
    CHECK(mock_aec::renders() == 1);                                     // one zero reference
    CHECK(mock_aec::log()[0].kind == mock_aec::Call::Render);
    CHECK(mock_aec::log()[0].first == 0.0f);
    CHECK(f.gaps.size() == 0);                                           // counted, not a gap
    CHECK(mock_aec::resets() == 0);                                      // and no filter reset

    // The unfiltered flag reaches the emitted frames.
    for (int64_t n = 4; n < 10; ++n) {
        f.ep.push_capture(make_signal_frame(n));
        g_now = kOrigin + (n + 1) * k::kTicksPerFrame + k::kReferenceWaitTicks;
        f.ep.process(f.me);
    }
    CHECK(f.me.size() > 0);
    CHECK((f.me[0].flags & kFrameUnfiltered) != 0);
    CHECK(f.ep.stats().unfiltered_frames == 7);
}

// Reference older than 0.5 s is thrown away. Changed against the earlier spec reading: the
// Mac ages the reference by the WALL CLOCK at the end of every process() call
// ("cutoff = Int((now - 0.5) * 100)"), not relative to the newest mic frame index.
void test_reference_discard() {
    Fixture f;
    for (int64_t n = 0; n < 10; ++n) f.ep.push_reference(make_frame(n, 0.3f), &f.others);
    g_now = kOrigin + 100 * k::kTicksPerFrame;         // 1 s of wall clock: all 10 are stale
    f.ep.process(f.me);                                // the 10 ms pump tick ages them out
    CHECK(f.ep.stats().ref_discarded == 10);
    CHECK(mock_aec::renders() == 0);                   // stale audio is never rendered

    const int64_t mic_index = 100;
    f.ep.push_capture(make_signal_frame(mic_index));
    g_now = kOrigin + (mic_index + 1) * k::kTicksPerFrame + k::kReferenceWaitTicks;
    f.ep.process(f.me);
    CHECK(f.ep.stats().unfiltered_frames == 1);        // nothing left to render
    CHECK(mock_aec::renders() == 1);
    CHECK(mock_aec::log()[0].first == 0.0f);           // the zero frame, not the stale audio

    // Just inside the 0.5 s window the reference survives the tick and is used.
    Fixture g;
    g.ep.push_reference(make_frame(55, 0.3f), &g.others);
    g_now = kOrigin + 100 * k::kTicksPerFrame;         // cutoff = (1.0 - 0.5) * 100 = 50
    g.ep.process(g.me);
    CHECK(g.ep.stats().ref_discarded == 0);
    g.ep.push_capture(make_signal_frame(100));
    g_now = kOrigin + 101 * k::kTicksPerFrame + k::kReferenceWaitTicks;
    g.ep.process(g.me);
    CHECK(mock_aec::log()[0].first == 0.3f);
    CHECK(g.ep.stats().unfiltered_frames == 1);        // present but not covering index 100
}

// At most 49 buffered frames per source; the oldest goes with a gap.
void test_buffer_cap() {
    Fixture f;
    for (int64_t n = 0; n < 60; ++n) f.ep.push_reference(make_frame(n, 0.1f), &f.others);
    CHECK(f.ep.stats().ref_overflow == 11);
    CHECK(f.gaps.size() == 11);
    CHECK(f.gaps.items()[0].source == SourceKind::Others);
    CHECK(f.gaps.items()[0].reason == GapReason::InboxOverflow);

    Fixture g;
    g_now = 0;                                        // nothing may be processed yet
    for (int64_t n = 0; n < 60; ++n) g.ep.push_capture(make_signal_frame(n));
    CHECK(g.ep.stats().cap_overflow == 11);
    CHECK(g.gaps.items()[0].source == SourceKind::Me);
}

// Late mic frames are dropped and logged as LateFrame gaps. Changed against the earlier
// spec reading: the Mac buffers mic frames in a DICTIONARY keyed by index and only compares
// against lastMicrophone inside process(). A frame that is merely out of order (still newer
// than the last processed one) is sorted back in, and a second frame with an already
// buffered index replaces it silently.
void test_late_and_duplicate() {
    Fixture f;
    for (int64_t n = 0; n < 5; ++n) {
        f.ep.push_reference(make_frame(n, 0.0f), &f.others);
        f.ep.push_capture(make_signal_frame(n));
        f.ep.process(f.me);
    }
    CHECK(f.ep.stats().captured == 5);
    CHECK(f.ep.last_microphone_index() == 4);

    f.ep.push_capture(make_signal_frame(4));          // duplicate of the last processed
    f.ep.push_capture(make_signal_frame(2));          // older still
    CHECK(f.ep.stats().late_dropped == 0);            // not decided at push time
    f.ep.process(f.me);
    CHECK(f.ep.stats().late_dropped == 2);
    CHECK(f.ep.stats().captured == 5);                // neither reached the canceller
    CHECK(f.gaps.size() == 2);
    CHECK(f.gaps.items()[0].reason == GapReason::LateFrame);
    CHECK(f.gaps.items()[0].source == SourceKind::Me);

    // Out of order but still ahead of lastMicrophone: processed in index order, not dropped.
    f.ep.push_capture(make_signal_frame(6));
    f.ep.push_capture(make_signal_frame(5));
    f.ep.push_capture(make_signal_frame(6));          // replaces the buffered index 6
    g_now = kOrigin + 7 * k::kTicksPerFrame + k::kReferenceWaitTicks;
    f.ep.process(f.me);
    CHECK(f.ep.stats().late_dropped == 2);            // nothing extra was dropped
    CHECK(f.ep.stats().captured == 7);                // 5 and 6, each exactly once
    CHECK(f.ep.last_microphone_index() == 6);
}

// mn_aec_reset happens only for a mic device switch or a mic format change.
void test_reset_policy() {
    Fixture f;
    for (int64_t n = 0; n < 5; ++n) {                 // reference missing -> unfiltered
        f.ep.push_capture(make_signal_frame(n));
        g_now = kOrigin + (n + 1) * k::kTicksPerFrame + k::kReferenceWaitTicks;
        f.ep.process(f.me);
    }
    f.ep.push_capture(make_signal_frame(2));          // late frame
    f.ep.push_reference(make_frame(400, 0.0f), &f.others);   // reference from the future
    f.ep.push_capture(make_signal_frame(50));         // mic time jump
    g_now = kOrigin + 51 * k::kTicksPerFrame + k::kReferenceWaitTicks;
    f.ep.process(f.me);
    CHECK(f.ep.stats().unfiltered_frames > 0);
    CHECK(mock_aec::resets() == 0);                   // none of this resets the filter

    f.ep.on_mic_device_changed();
    CHECK(mock_aec::resets() == 1);
    f.ep.on_mic_format_changed();
    CHECK(mock_aec::resets() == 2);
    CHECK(f.ep.stats().resets == 2);
}

// Output sample k of frame n sits at timeline position n*160 + k - 224.
void test_latency_mapping() {
    Fixture f;
    mock_aec::set_echo_subtraction(false);
    const int64_t kImpulseFrame = 6;
    const int kImpulseOffset = 37;
    const int64_t impulse_pos = kImpulseFrame * k::kFrameSamples + kImpulseOffset;

    for (int64_t n = 0; n < 20; ++n) {
        f.ep.push_reference(make_frame(n, 0.0f), &f.others);
        Frame160 mic = make_frame(n, 0.0f);
        if (n == kImpulseFrame) mic.s[kImpulseOffset] = 1.0f;
        f.ep.push_capture(mic);
        f.ep.process(f.me);
    }
    int64_t found = -1;
    int hits = 0;
    for (const Frame160& fr : f.me) {
        for (int i = 0; i < k::kFrameSamples; ++i) {
            if (fr.s[i] != 0.0f) {
                found = fr.index * k::kFrameSamples + i;
                ++hits;
            }
        }
    }
    CHECK(hits == 1);
    CHECK(found == impulse_pos);                      // the 224-sample delay is compensated
    mock_aec::set_echo_subtraction(true);
}

// stop() flushes the remainder with zero capture frames.
void test_stop_flush() {
    Fixture f;
    const int64_t kFrames = 10;
    for (int64_t n = 0; n < kFrames; ++n) {
        f.ep.push_reference(make_frame(n, 0.0f), &f.others);
        f.ep.push_capture(make_signal_frame(n));
        f.ep.process(f.me);
    }
    CHECK(f.me.size() == static_cast<size_t>(kFrames) - 2);
    const uint64_t captured_before = f.ep.stats().captured;
    f.ep.stop(f.me);
    CHECK(f.ep.stats().captured == captured_before + k::kFlushFrames);
    CHECK(f.me.size() == static_cast<size_t>(kFrames));
    CHECK(f.me.back().index == kFrames - 1);
    // The tail that was still inside the canceller made it out.
    double tail = 0.0;
    for (int i = 0; i < k::kFrameSamples; ++i) tail += std::fabs(f.me.back().s[i]);
    CHECK(tail > 0.0);
}

void test_metrics() {
    Fixture f;
    double erle = 0.0;
    int delay = 0;
    f.ep.metrics(&erle, &delay);
    CHECK(erle > 0.0);
    CHECK(delay >= 0);
}

// Config::enable_aec = false: the bridge is never touched (the recording mock logs no call
// at all, not even mn_aec_set_ns), mic frame n leaves unshifted at index n in the same
// process() call - with or without a reference, never unfiltered - the reference passes
// through to Others without being buffered, stop() flushes nothing, resets are not counted
// and the metrics read 0 dB / 0 ms.
void test_aec_disabled() {
    mock_aec::clear_log();
    mock_aec::set_delay(kMockDelay);
    mock_aec::set_echo_subtraction(true);
    g_now = 0;                                         // a waiting canceller would hold every frame
    GapLog gaps;
    gaps.reserve(16);
    std::vector<Frame160> me, others;
    me.reserve(256);
    others.reserve(512);
    {
        EchoProcessor ep;
        EchoProcessor::Config cfg;
        cfg.enable_aec = false;
        ep.configure(cfg, &gaps, kOrigin);
        ep.set_clock(now_fn, nullptr);
        CHECK(ep.latency_samples() == 0);
        CHECK(ep.flush_frames() == 0);

        const int64_t kFrames = 12;
        bool immediate = true;
        for (int64_t n = 0; n < kFrames; ++n) {          // reference and mic together
            ep.push_reference(make_signal_frame(n), &others);
            ep.push_capture(make_signal_frame(n));
            ep.process(me);
            if (me.size() != static_cast<size_t>(n + 1)) immediate = false;
        }
        for (int64_t n = kFrames; n < kFrames + 5; ++n) {   // mic without any reference
            ep.push_capture(make_signal_frame(n));
            ep.process(me);
            if (me.size() != static_cast<size_t>(n + 1)) immediate = false;
        }
        CHECK(immediate);
        bool exact = true;
        for (size_t i = 0; i < me.size(); ++i) {
            const Frame160& fr = me[i];
            if (fr.index != static_cast<int64_t>(i)) exact = false;   // timeline n*160
            if (fr.host_100ns != kOrigin + fr.index * k::kTicksPerFrame) exact = false;
            if (fr.valid_lo != 0 || fr.valid_hi != k::kFrameSamples) exact = false;
            if (fr.flags != 0) exact = false;                         // never unfiltered
            for (int s = 0; s < k::kFrameSamples; ++s) {
                if (fr.s[s] != sig(fr.index * k::kFrameSamples + s)) exact = false;
            }
        }
        CHECK(exact);

        // Partial frames keep their range; the bridge's scrubbing rule still applies.
        Frame160 odd = make_frame(kFrames + 5, 0.5f);
        odd.valid_lo = 80;
        odd.s[80] = std::nanf("");
        odd.s[81] = 2.0f;
        odd.s[82] = -3.0f;
        ep.push_capture(odd);
        ep.process(me);
        CHECK(me.size() == static_cast<size_t>(kFrames + 6));
        if (me.size() == static_cast<size_t>(kFrames + 6)) {
            const Frame160& o = me.back();
            CHECK(o.valid_lo == 80 && o.valid_hi == k::kFrameSamples);
            CHECK(o.s[0] == 0.0f && o.s[79] == 0.0f);                // outside the range
            CHECK(o.s[80] == 0.0f && o.s[81] == 1.0f && o.s[82] == -1.0f && o.s[83] == 0.5f);
        }

        // The reference is never buffered: 200 frames, no 49-frame cap, no gaps, no ageing.
        for (int64_t n = 100; n < 300; ++n) ep.push_reference(make_frame(n, 0.1f), &others);
        g_now = kOrigin + 1000 * k::kTicksPerFrame;
        ep.process(me);
        CHECK(others.size() == static_cast<size_t>(kFrames + 200));   // Others still passes
        CHECK(ep.stats().ref_overflow == 0);
        CHECK(ep.stats().ref_discarded == 0);
        CHECK(gaps.size() == 0);

        // Late and duplicate mic frames are still dropped, so the Me timeline only grows.
        ep.push_capture(make_signal_frame(3));
        ep.process(me);
        CHECK(ep.stats().late_dropped == 1);
        CHECK(gaps.size() == 1 && gaps.items()[0].reason == GapReason::LateFrame);
        CHECK(me.size() == static_cast<size_t>(kFrames + 6));

        // Mic device / format change: no bridge reset, nothing counted.
        ep.on_mic_device_changed();
        ep.on_mic_format_changed(&me);
        CHECK(ep.stats().resets == 0);

        // stop(): nothing is delayed, so nothing is flushed.
        const size_t before_stop = me.size();
        ep.stop(me);
        CHECK(me.size() == before_stop);

        double erle = -1.0;
        int delay = -1;
        ep.metrics(&erle, &delay);
        CHECK(erle == 0.0 && delay == 0);
        CHECK(ep.stats().unfiltered_frames == 0);
        CHECK(ep.stats().captured == 0 && ep.stats().rendered == 0);
    }
    CHECK(mock_aec::log().empty());                    // not one mn_aec_* call, set_ns included
    CHECK(mock_aec::renders() == 0 && mock_aec::captures() == 0 && mock_aec::resets() == 0);
}

}  // namespace

void test_echo_processor() {
    test_ns_level();
    test_mock_detects_wrong_order();
    test_render_before_capture();
    test_silence_is_valid_reference();
    test_wait_then_unfiltered();
    test_reference_discard();
    test_buffer_cap();
    test_late_and_duplicate();
    test_reset_policy();
    test_latency_mapping();
    test_stop_flush();
    test_metrics();
    test_aec_disabled();
}
