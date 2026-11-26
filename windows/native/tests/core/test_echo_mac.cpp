// Case-by-case port of EchoAudioTests.swift (Swift tests of the macOS app).
// Each test keeps the Swift test name in its comment. Cases that need the real AEC3 (ERLE,
// SI-SDR, delay metrics) are listed at the bottom of this file and belong to the AEC agent.
//
// Model note that applies to every Me assertion below: the Mac emits variable-length PCM
// chunks the moment the canceller has produced them, the Windows core emits whole 160
// sample frames carrying [valid_lo, valid_hi). Totals, positions and hit counts are
// therefore asserted, not chunk boundaries.
#include "core/echo_processor.h"
#include "core/framer.h"
#include "core/inbox.h"
#include "core/normalizer.h"
#include "mock_aec.h"
#include "test_util.h"

#include <cmath>
#include <vector>

using namespace wf;

namespace {

int64_t g_now = 0;
int64_t now_fn(void*) { return g_now; }

constexpr int64_t kOrigin = 0;

std::vector<float> constant(size_t n, float v) { return std::vector<float>(n, v); }


Frame160 full_frame(int64_t index, float v) {
    Frame160 f;
    f.index = index;
    f.host_100ns = kOrigin + index * k::kTicksPerFrame;
    for (int i = 0; i < k::kFrameSamples; ++i) f.s[i] = v;
    return f;
}

// Counts how often every 16 kHz position was emitted - the C++ TimelineRecorder.
struct Recorder {
    std::vector<int> hits;
    explicit Recorder(size_t samples) : hits(samples, 0) {}
    void take(const std::vector<Frame160>& frames) {
        for (const Frame160& f : frames) {
            for (int i = f.valid_lo; i < f.valid_hi; ++i) {
                const int64_t p = f.index * k::kFrameSamples + i;
                if (p >= 0 && static_cast<size_t>(p) < hits.size()) ++hits[static_cast<size_t>(p)];
            }
        }
    }
    size_t emitted() const {
        size_t n = 0;
        for (int h : hits) n += static_cast<size_t>(h);
        return n;
    }
};

struct EchoFixture {
    GapLog gaps;
    EchoProcessor ep;
    std::vector<Frame160> me;
    std::vector<Frame160> others;

    explicit EchoFixture(int ns_level = k::kNsLevel) {
        mock_aec::clear_log();
        mock_aec::set_echo_subtraction(false);     // identity + delay: we assert routing
        mock_aec::set_delay(ns_level == 0 ? 128 : 224);
        g_now = kOrigin;
        EchoProcessor::Config cfg;
        cfg.ns_level = ns_level;
        ep.configure(cfg, &gaps, kOrigin);
        ep.set_clock(now_fn, nullptr);
        me.reserve(1024);
        others.reserve(1024);
        mock_aec::clear_log();
    }
    ~EchoFixture() {
        mock_aec::set_echo_subtraction(true);
        mock_aec::set_delay(224);
    }
};

size_t valid_samples(const std::vector<Frame160>& frames) {
    size_t n = 0;
    for (const Frame160& f : frames) n += static_cast<size_t>(valid_count(f));
    return n;
}

// ---------------------------------------------------------------------------
// EchoAudioTests.assemblerPreservesFragmentsAndPadsOnlyOnce
// ---------------------------------------------------------------------------
void test_assembler_preserves_fragments_and_pads_only_once() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Me, kOrigin, &gaps);
    std::vector<Frame160> out;

    // Swift: append 77 samples at t = 1.002 -> first = round(1.002 * 16000) = 16032.
    const std::vector<float> a = constant(77, 0.1f);
    f.push(16032, a.data(), a.size(), out);
    CHECK(out.empty());                                      // first.isEmpty

    const std::vector<float> b = constant(90, 0.2f);
    f.push(16032 + 77, b.data(), b.size(), out);
    CHECK(out.size() == 1);                                  // next.count == 1
    CHECK(out[0].index == 100);
    CHECK(out[0].valid_lo == 32 && out[0].valid_hi == 160);  // next[0].valid == 32..<160
    // next[0].time == 1.002 -> the first carrying sample sits at position 16032.
    CHECK(valid_start_100ns(out[0]) == kOrigin + 16032 * k::kTicksPerSample);
    CHECK(out[0].s[31] == 0.0f);                             // padding, not audio
    CHECK(out[0].s[32] == 0.1f);
    CHECK(out[0].s[109] == 0.2f);

    f.finish(out);
    CHECK(out.size() == 2);
    CHECK(out[1].valid_lo == 0 && out[1].valid_hi == 39);    // last?.valid == 0..<39
    f.finish(out);
    CHECK(out.size() == 2);                                  // assembler.finish() == nil
    CHECK(valid_samples(out) == 77 + 90);                    // nothing padded twice
}

// ---------------------------------------------------------------------------
// EchoAudioTests.missingReferencePassesMicrophoneThrough
// ---------------------------------------------------------------------------
void test_missing_reference_passes_microphone_through() {
    EchoFixture f(0);                                        // noiseSuppression: .off
    CHECK(f.ep.latency_samples() == 128);                    // EchoCanceller.latencySamples

    f.ep.push_capture(full_frame(0, 0.2f));
    g_now = kOrigin + 990000;                                // process(now: 0.099)
    f.ep.process(f.me);
    CHECK(f.me.empty());
    CHECK(f.gaps.size() == 0);
    CHECK(f.ep.stats().unfiltered_frames == 0);

    // After the 100 ms wait the frame passes the canceller with silent playback:
    // no gap, no reset.
    g_now = kOrigin + 1110000;                               // process(now: 0.111)
    f.ep.process(f.me);
    CHECK(f.ep.stats().captured == 1);
    CHECK(f.gaps.size() == 0);
    CHECK(f.ep.stats().unfiltered_frames == 1);
    CHECK(f.ep.stats().resets == 0);
    CHECK(mock_aec::renders() == 1);
    CHECK(mock_aec::log()[0].kind == mock_aec::Call::Render);
    CHECK(mock_aec::log()[0].first == 0.0f);                 // one zero reference frame

    // Received digital silence is a valid reference, not a missing one.
    f.ep.push_reference(full_frame(1, 0.0f), nullptr);
    f.ep.push_capture(full_frame(1, 0.2f));
    g_now = kOrigin + 1200000;
    f.ep.process(f.me);
    CHECK(f.ep.stats().captured == 2);
    CHECK(f.ep.stats().unfiltered_frames == 1);              // still 1: silence counts
    CHECK(f.gaps.size() == 0);

    f.ep.stop(f.me);
    Recorder r(2 * k::kFrameSamples);
    r.take(f.me);
    CHECK(r.emitted() == 320);                               // both mic frames came out
    bool once = true;
    for (int h : r.hits) {
        if (h != 1) once = false;
    }
    CHECK(once);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.referenceNeverLeaksIntoAppTranscriptionAndTimeSurvivesReset
// ---------------------------------------------------------------------------
void test_reference_never_leaks_into_app_transcription() {
    EchoFixture f(0);
    // App mode (globalIsMeeting == false): the playback tap is reference only, so the
    // session passes no Others sink. It must never reach the Others track.
    f.ep.push_reference(full_frame(0, 0.1f), nullptr);
    CHECK(f.others.empty());
    f.ep.push_capture(full_frame(0, 0.1f));
    f.ep.process(f.me);
    CHECK(f.ep.stats().captured == 1);
    CHECK(f.others.empty());
    CHECK(f.ep.stats().others_frames == 0);

    // Global mode: the same reference frame is also the Others track.
    EchoFixture g(0);
    g.ep.push_reference(full_frame(0, 0.1f), &g.others);
    CHECK(g.others.size() == 1);
    CHECK(g.ep.stats().others_frames == 1);

    // finish() is idempotent: a second stop emits nothing more.
    g.ep.push_capture(full_frame(0, 0.1f));
    g.ep.process(g.me);
    g.ep.stop(g.me);
    const size_t after_stop = g.me.size();
    g.ep.stop(g.me);
    CHECK(g.me.size() == after_stop);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.aecLatencyAndFinalPartialBlockPreserveMicrophoneDuration
// ---------------------------------------------------------------------------
void test_aec_latency_and_final_partial_block_preserve_duration() {
    EchoFixture f;                                           // default NS -> 224 samples
    GapLog fg;
    Framer mic_framer, ref_framer;
    mic_framer.configure(SourceKind::Me, kOrigin, &fg);
    ref_framer.configure(SourceKind::Others, kOrigin, &fg);

    std::vector<float> signal(777);
    for (size_t i = 0; i < signal.size(); ++i) {
        signal[i] = static_cast<float>(0.1 * std::sin(static_cast<double>(i) * 0.15));
    }
    const std::vector<float> zeros(777, 0.0f);

    std::vector<Frame160> frames;
    ref_framer.push(0, zeros.data(), zeros.size(), frames);
    ref_framer.finish(frames);
    for (const Frame160& fr : frames) f.ep.push_reference(fr, nullptr);
    frames.clear();
    mic_framer.push(0, signal.data(), signal.size(), frames);
    mic_framer.finish(frames);
    CHECK(frames.size() == 5);                               // 4 full + one 137 sample tail
    CHECK(frames[4].valid_hi == 137);
    for (const Frame160& fr : frames) f.ep.push_capture(fr);

    g_now = kOrigin + 5000000;
    f.ep.process(f.me);
    f.ep.stop(f.me);

    // received.reduce(0) { $0 + $1.1 } == 777
    CHECK(valid_samples(f.me) == 777);
    // The emitted positions are contiguous from 0 (Swift walks `expected` forward).
    int64_t expected = 0;
    bool contiguous = true;
    for (const Frame160& fr : f.me) {
        if (fr.index * k::kFrameSamples + fr.valid_lo != expected) contiguous = false;
        expected += valid_count(fr);
    }
    CHECK(contiguous);
    CHECK(expected == 777);
    CHECK(f.ep.stats().resets == 0);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.delayedBatchMatchesReferenceBeforeEnforcingWaitDeadline
// ---------------------------------------------------------------------------
void test_delayed_batch_matches_reference_before_wait_deadline() {
    EchoFixture f;
    // Callback delivery order is arbitrary; both sources already exist in this batch, so
    // the wait deadline must never fire and no frame may become a gap.
    const int kFrames = 20;                                  // 3200 samples
    for (int64_t n = 0; n < kFrames; ++n) f.ep.push_capture(full_frame(n, 0.1f));
    for (int64_t n = 0; n < kFrames; ++n) f.ep.push_reference(full_frame(n, 0.1f), &f.others);
    g_now = kOrigin;                                         // no deadline has passed yet
    f.ep.process(f.me);
    CHECK(f.ep.stats().captured == kFrames);                 // covered, so no waiting
    f.ep.stop(f.me);
    CHECK(valid_samples(f.me) == 3200);                      // microphoneSamples == 3200
    CHECK(f.gaps.size() == 0);
    CHECK(f.ep.stats().unfiltered_frames == 0);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.referenceOutageKeepsFilter  (structure only; ERLE needs real AEC3)
// ---------------------------------------------------------------------------
void test_reference_outage_keeps_filter() {
    EchoFixture f(0);
    const int kFrames = 800;                                 // 8 s
    Recorder r(static_cast<size_t>(kFrames) * k::kFrameSamples);
    // 1 s playback-tap outage at 3-4 s. Every packet is delivered 5 ms after its end.
    for (int64_t n = 0; n < kFrames; ++n) {
        g_now = kOrigin + (n + 1) * k::kTicksPerFrame + 5 * k::kTicksPerMs;
        if (n < 300 || n >= 400) f.ep.push_reference(full_frame(n, 0.05f), nullptr);
        f.ep.push_capture(full_frame(n, 0.2f));
        f.me.clear();
        f.ep.process(f.me);
        r.take(f.me);
    }
    f.me.clear();
    f.ep.stop(f.me);
    r.take(f.me);

    bool once = true;
    for (int h : r.hits) {
        if (h != 1) once = false;
    }
    CHECK(once);                                             // recorder.hits.allSatisfy { $0 == 1 }
    CHECK(r.emitted() == static_cast<size_t>(kFrames) * k::kFrameSamples);
    CHECK(f.ep.stats().resets == 0);                         // processor.aecResets == 0
    CHECK(f.ep.stats().unfiltered_frames >= 100);
    CHECK(f.gaps.size() == 0);                               // no microphone gaps
}

// ---------------------------------------------------------------------------
// EchoAudioTests.micHoleKeepsAlignment  (structure only; ERLE needs real AEC3)
// ---------------------------------------------------------------------------
void test_mic_hole_keeps_alignment() {
    EchoFixture f;                                           // default NS: 224 > one frame
    const int kFrames = 800;
    const int64_t kHoleFrom = 400, kHoleTo = 420;
    Recorder r(static_cast<size_t>(kFrames) * k::kFrameSamples);
    for (int64_t n = 0; n < kFrames; ++n) {
        g_now = kOrigin + (n + 1) * k::kTicksPerFrame + 5 * k::kTicksPerMs;
        f.ep.push_reference(full_frame(n, 0.05f), nullptr);
        if (n < kHoleFrom || n >= kHoleTo) f.ep.push_capture(full_frame(n, 0.2f));
        f.me.clear();
        f.ep.process(f.me);
        r.take(f.me);
    }
    f.me.clear();
    f.ep.stop(f.me);
    r.take(f.me);

    const size_t delivered =
        static_cast<size_t>(kFrames - (kHoleTo - kHoleFrom)) * k::kFrameSamples;
    CHECK(r.emitted() == delivered);                         // emitted == delivered
    bool pattern = true;
    for (size_t i = 0; i < r.hits.size(); ++i) {
        const int64_t frame = static_cast<int64_t>(i) / k::kFrameSamples;
        const int want = (frame >= kHoleFrom && frame < kHoleTo) ? 0 : 1;
        if (r.hits[i] != want) pattern = false;
    }
    CHECK(pattern);                     // hits[i] == (hole.contains(i / 160) ? 0 : 1)
    CHECK(f.ep.stats().resets == 0);
    // The mic hole itself is never a gap of the processor; the packet-level
    // "Audio timestamps interrupted" is recorded once by CaptureNormalizer.
    CHECK(f.gaps.size() == 0);
    // Render and capture stayed 1:1 across the hole: the 20 skipped playback frames were
    // consumed without being rendered.
    CHECK(f.ep.stats().rendered == f.ep.stats().captured);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.lateReferenceDoesNotRepeatOthersAndForwardMicJumpIsRecorded
// ---------------------------------------------------------------------------
void test_late_reference_and_forward_mic_jump() {
    // The same reference packet offered twice keeps Others at 160 samples.
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    cfg.source = SourceKind::Others;
    norm.configure(cfg, &gaps);
    std::vector<float> block(160, 0.1f), out;
    InputPacket p;
    p.data = block.data();
    p.frames = 160;
    p.rate = 16000;
    p.qpc_100ns = 0;
    size_t others = 0;
    norm.process(p, out);
    others += out.size();
    norm.process(p, out);                                   // identical timestamp: late
    others += out.size();
    CHECK(others == 160);                                   // #expect(others == 160)
    CHECK(norm.stats().late_packets == 1);
    CHECK(gaps.size() == 1);
    CHECK(gaps.items()[0].reason == GapReason::LateFrame);

    // A forward microphone jump of 90 ms is recorded as one gap of that length.
    GapLog mgaps;
    CaptureNormalizer mic;
    CaptureNormalizer::Config mcfg;
    mcfg.source = SourceKind::Me;
    mic.configure(mcfg, &mgaps);
    p.qpc_100ns = 0;
    mic.process(p, out);
    p.qpc_100ns = 1000000;                                  // timestamp 10.1, origin 10
    mic.process(p, out);
    CHECK(mgaps.size() == 1);
    CHECK(mgaps.items()[0].source == SourceKind::Me);
    CHECK(mgaps.items()[0].reason == GapReason::TimestampDiscontinuity);
    CHECK_NEAR(mgaps.items()[0].duration_sec, 0.09, 1e-9);
    CHECK(mgaps.items()[0].start_100ns == 100000);          // start == expected end (0.01)
}

// ---------------------------------------------------------------------------
// EchoAudioTests.statefulConversionAndStereoDownmix(rate:)
// Stereo downmix lives in the WASAPI layer on Windows; the stateful part is core.
// ---------------------------------------------------------------------------
void test_stateful_conversion(double rate) {
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    norm.configure(cfg, &gaps);
    std::vector<float> in(4096, 0.2f), out;
    const int sizes[] = {127, 1024, 333, 2048, 77, 4096, 2048};
    int64_t cursor = 0, received = 0;
    bool contiguous = true, no_boundary = true;
    for (int size : sizes) {
        InputPacket p;
        p.data = in.data();
        p.frames = static_cast<uint32_t>(size);
        p.rate = static_cast<uint32_t>(rate);
        // Swift: at: 10 + Double(cursor) / rate
        p.qpc_100ns = 100000000 + static_cast<int64_t>(
                                      static_cast<double>(cursor) / rate * k::kTicksPerSecond);
        bool disc = false;
        const int64_t start = norm.process(p, out, &disc);
        if (start != received) contiguous = false;          // result.time == 10 + received/16000
        if (disc) no_boundary = false;
        received += static_cast<int64_t>(out.size());
        cursor += size;
    }
    CHECK(contiguous);
    CHECK(no_boundary);
    CHECK(std::fabs(static_cast<double>(received) -
                    static_cast<double>(cursor) * 16000.0 / rate) < 64.0);
    CHECK(norm.stats().reanchors == 0);
    CHECK(gaps.size() == 0);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.converterFlushRetainsActualDuration(rate:)
// Our IResampler is push based and keeps no tail, so there is no finish() to flush.
// ---------------------------------------------------------------------------
void test_conversion_retains_actual_duration(double rate) {
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    norm.configure(cfg, &gaps);
    std::vector<float> in(4000, 0.1f), out;
    const int sizes[] = {117, 933, 819, 4000, 31};
    int64_t count = 0, offset = 0;
    for (int size : sizes) {
        InputPacket p;
        p.data = in.data();
        p.frames = static_cast<uint32_t>(size);
        p.rate = static_cast<uint32_t>(rate);
        p.qpc_100ns = static_cast<int64_t>(static_cast<double>(offset) / rate *
                                           k::kTicksPerSecond);
        norm.process(p, out);
        count += static_cast<int64_t>(out.size());
        offset += size;
    }
    const double want = static_cast<double>(offset) * 16000.0 / rate;
    CHECK(std::fabs(static_cast<double>(count) - want) <= 2.0);
    CHECK(norm.stats().reanchors == 0);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.deviceClockDriftCannotAccumulateUnboundedTimestampError(ppm:)
// ---------------------------------------------------------------------------
void test_device_clock_drift_bounded(double ppm) {
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    norm.configure(cfg, &gaps);
    std::vector<float> in(480, 0.1f), out;
    double largest = 0.0;
    int boundaries = 0;
    int64_t produced = 0;
    for (int frame = 0; frame < 5000; ++frame) {
        const double host = static_cast<double>(frame) * 0.01 * (1.0 + ppm / 1e6);
        InputPacket p;
        p.data = in.data();
        p.frames = 480;
        p.rate = 48000;
        p.qpc_100ns = static_cast<int64_t>(std::llround(host * k::kTicksPerSecond));
        p.device_position = static_cast<uint64_t>(frame) * 480;
        bool disc = false;
        const int64_t start = norm.process(p, out, &disc);
        const double assigned =
            static_cast<double>(norm.host_time_of(start)) / k::kTicksPerSecond;
        const double diff = std::fabs(assigned - host);
        if (diff > largest) largest = diff;
        if (disc) ++boundaries;
        produced += static_cast<int64_t>(out.size());
    }
    const double host = 5000 * 0.01 * (1.0 + ppm / 1e6);
    CHECK(largest < 0.004);
    CHECK(boundaries == 0);
    CHECK(norm.stats().inserted_samples + norm.stats().removed_samples > 0);
    CHECK(std::fabs(static_cast<double>(produced) - host * 16000.0) <= 32.0);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.largeJumpRemainsBoundary
// ---------------------------------------------------------------------------
void test_large_jump_remains_boundary() {
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    norm.configure(cfg, &gaps);
    std::vector<float> in(480, 0.1f), out;
    int boundaries = 0;
    for (int frame = 0; frame < 200; ++frame) {
        const double host =
            static_cast<double>(frame) * 0.01 + (frame >= 100 ? 0.025 : 0.0);
        InputPacket p;
        p.data = in.data();
        p.frames = 480;
        p.rate = 48000;
        p.qpc_100ns = static_cast<int64_t>(std::llround(host * k::kTicksPerSecond));
        p.device_position = static_cast<uint64_t>(frame) * 480;
        bool disc = false;
        norm.process(p, out, &disc);
        if (disc) ++boundaries;
    }
    CHECK(boundaries == 1);
    CHECK(norm.stats().inserted_samples + norm.stats().removed_samples == 0);
}

// ---------------------------------------------------------------------------
// EchoAudioTests.inboxBoundsBacklogAndRejectsOldGenerations
// Generations belong to the session layer on Windows; the bound is core.
// ---------------------------------------------------------------------------
void test_inbox_bounds_backlog() {
    GapLog gaps;
    CaptureInbox inbox;
    inbox.configure(SourceKind::Me, 16000, &gaps);
    std::vector<float> block(160, 0.0f);
    for (int i = 0; i < 100; ++i) {                          // 1 s of audio
        InputPacket p;
        p.data = block.data();
        p.frames = 160;
        p.rate = 16000;
        p.qpc_100ns = static_cast<int64_t>(i) * 100000;
        inbox.push(p);
    }
    size_t drained = 0;
    InputPacket out;
    while (inbox.pop(out)) ++drained;
    CHECK(drained <= 50);                                    // packets.count <= 50
    CHECK(inbox.queued_frames() == 0);
    CHECK(gaps.size() > 0);                                  // !gaps.isEmpty
    CHECK(gaps.items()[0].reason == GapReason::InboxOverflow);
    CHECK(inbox.capacity_frames() == static_cast<size_t>(0.240 * 16000));
}

}  // namespace

void test_echo_mac() {
    test_assembler_preserves_fragments_and_pads_only_once();
    test_missing_reference_passes_microphone_through();
    test_reference_never_leaks_into_app_transcription();
    test_aec_latency_and_final_partial_block_preserve_duration();
    test_delayed_batch_matches_reference_before_wait_deadline();
    test_reference_outage_keeps_filter();
    test_mic_hole_keeps_alignment();
    test_late_reference_and_forward_mic_jump();
    test_stateful_conversion(44100.0);
    test_stateful_conversion(48000.0);
    test_conversion_retains_actual_duration(44100.0);
    test_conversion_retains_actual_duration(48000.0);
    test_device_clock_drift_bounded(500.0);
    test_device_clock_drift_bounded(-500.0);
    test_large_jump_remains_boundary();
    test_inbox_bounds_backlog();
}

// Not ported - they measure the real AEC3/NS and belong in the AEC agent's test_aec_erle:
//   echoAndDoubleTalk(delayMS:)                        ERLE >= 20, SI-SDR >= 10, lag == latency
//   silenceResetAndNearSpeechAreNotMuted               near-end energy survives a reset
//   shortInterjectionsSurviveEchoAndBackgroundNoise    gain > 0.4, correlation > 0.7
//   nonlinearReverberantEchoIsStronglySuppressed       baseline ERLE + 5 dB
//   metricsReportCausalEchoDelay                       delayMs in 72...84
//   referenceOutageKeepsFilter / micHoleKeepsAlignment: their ERLE assertions only
// Not ported - no core counterpart (persistence / @MainActor lifecycle):
//   gapsAndDeviceSettingsRemainBackwardCompatible      Meeting/AppSettings JSON
//   AudioRecoveryTests.*                               AudioCapture lifecycle and watchdog
