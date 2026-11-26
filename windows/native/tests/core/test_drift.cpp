// CaptureNormalizer: the spec 3.3 drift rules - ignore below 1 ms, at most one sample per
// 10 ms, re-anchor on a jump > 3 ms or drift > 20 ms, plus the WASAPI timestamp flags and
// the drift metric across holes.
#include "core/normalizer.h"
#include "test_util.h"

#include <cmath>
#include <vector>

using namespace wf;

namespace {

// Drift constants pinned to the spec appendix.
static_assert(k::kDriftIgnoreTicks == 10000, "ignore below 1 ms");
static_assert(k::kDriftAdjustIntervalTicks == 100000, "at most 1 sample per 10 ms");
static_assert(k::kDriftMaxAdjustSamples == 1, "1 sample");
static_assert(k::kReanchorJumpTicks == 30000, "re-anchor above a 3 ms jump");
static_assert(k::kReanchorDriftTicks == 200000, "re-anchor above 20 ms of drift");
static_assert(k::kTicksPerSample == 625, "16 kHz sample = 625 * 100 ns");
static_assert(k::kInboxSeconds == 0.240, "inbox holds 240 ms");
static_assert(k::kRingSlots == 1024, "1024 ring slots");
static_assert(k::kAnchorThresholdSamples == 1.5, "new anchor above 1.5 samples");

constexpr int64_t kPacketTicks = 100000;   // 10 ms
constexpr uint32_t kFrames = 160;          // 10 ms at 16 kHz

struct Driver {
    GapLog gaps;
    CaptureNormalizer norm;
    std::vector<float> in;
    std::vector<float> out;
    int64_t start = 0;
    bool disc = false;

    explicit Driver(bool app_source = false) : in(kFrames, 0.25f) {
        CaptureNormalizer::Config cfg;
        cfg.source = SourceKind::Me;
        cfg.app_source = app_source;
        norm.configure(cfg, &gaps);
        out.reserve(4096);
    }

    // Returns the error between the host timestamp and the timeline time assigned to it.
    double feed(int64_t ts, uint32_t flags = 0, uint64_t devpos = 0, uint32_t frames = kFrames) {
        InputPacket p;
        p.data = in.data();
        p.frames = frames;
        p.rate = 16000;
        p.qpc_100ns = ts;
        p.device_position = devpos;
        p.flags = flags;
        start = norm.process(p, out, &disc);
        return static_cast<double>(ts - norm.host_time_of(start)) / k::kTicksPerSecond;
    }
};

// Steady clock: contiguous positions, no corrections, no gaps.
void test_steady() {
    Driver d;
    int64_t expect = 0;
    for (int i = 0; i < 500; ++i) {
        d.feed(static_cast<int64_t>(i) * kPacketTicks, 0, static_cast<uint64_t>(i) * kFrames);
        if (d.start != expect) CHECK(false);
        expect += static_cast<int64_t>(d.out.size());
    }
    CHECK(d.norm.stats().inserted_samples == 0);
    CHECK(d.norm.stats().removed_samples == 0);
    CHECK(d.norm.stats().reanchors == 0);
    CHECK(d.gaps.size() == 0);
    CHECK(d.norm.origin_100ns() == 0);
}

// A constant offset below 1 ms must never trigger a correction.
void test_sub_millisecond_ignored() {
    Driver d;
    for (int i = 0; i < 300; ++i) {
        int64_t ts = static_cast<int64_t>(i) * kPacketTicks;
        if (i > 0) ts += 9000;                       // +0.9 ms, constant
        d.feed(ts);
    }
    CHECK(d.norm.stats().inserted_samples == 0);
    CHECK(d.norm.stats().removed_samples == 0);
    CHECK(d.norm.stats().reanchors == 0);
}

// +-250 ppm over 60 s stays inside ~1 ms and never re-anchors.
void test_ppm(double ppm) {
    Driver d;
    const int packets = 6000;                        // 60 s
    double worst = 0.0;
    int64_t last_adjust_ts = -1;
    int64_t worst_gap = 1 << 30;
    uint64_t adjusts = 0;
    for (int i = 0; i < packets; ++i) {
        const int64_t ts = static_cast<int64_t>(
            std::llround(static_cast<double>(i) * kPacketTicks * (1.0 + ppm * 1e-6)));
        const double err = d.feed(ts, 0, static_cast<uint64_t>(i) * kFrames);
        if (std::fabs(err) > worst) worst = std::fabs(err);
        const uint64_t now_adjusts =
            d.norm.stats().inserted_samples + d.norm.stats().removed_samples;
        if (now_adjusts != adjusts) {
            adjusts = now_adjusts;
            if (last_adjust_ts >= 0 && ts - last_adjust_ts < worst_gap)
                worst_gap = static_cast<int64_t>(ts - last_adjust_ts);
            last_adjust_ts = ts;
        }
    }
    CHECK(worst < 0.0011);                           // 1 ms deadband + one sample of overshoot
    CHECK(d.norm.stats().reanchors == 0);
    CHECK(worst_gap >= k::kDriftAdjustIntervalTicks);
    if (ppm > 0) {
        CHECK(d.norm.stats().inserted_samples > 200);   // 60 s * 250 ppm = 15 ms = 240 samples
        CHECK(d.norm.stats().inserted_samples < 280);
        CHECK(d.norm.stats().removed_samples == 0);
    } else {
        CHECK(d.norm.stats().removed_samples > 200);
        CHECK(d.norm.stats().removed_samples < 280);
        CHECK(d.norm.stats().inserted_samples == 0);
    }
    CHECK_NEAR(d.norm.stats().drift_ppm, ppm, 15.0);
}

// The limiter is "at most one sample per 10 ms OF AUDIO in the packet", i.e.
// max(1, produced / 160) per packet (Mac CaptureNormalizer.convert:176).
// Changed against the earlier spec reading: there is no host-clock spacing rule. A big
// clock error may legitimately correct on consecutive packets; what bounds the rate is the
// feedback of the applied sample into the error, not a timer.
void test_adjust_rate_limit() {
    Driver d;
    uint64_t adjusts = 0;
    for (int i = 0; i < 1000; ++i) {                 // 10 ms packets -> limit 1
        const int64_t ts = static_cast<int64_t>(
            std::llround(static_cast<double>(i) * kPacketTicks * (1.0 + 5000e-6)));
        d.feed(ts, 0, static_cast<uint64_t>(i) * kFrames);
        const uint64_t now = d.norm.stats().inserted_samples + d.norm.stats().removed_samples;
        if (now != adjusts) {
            CHECK(now == adjusts + 1);               // never more than one per 10 ms packet
            adjusts = now;
        }
    }
    CHECK(d.norm.stats().reanchors == 0);            // 5000 ppm is still correctable
    // 5000 ppm over 10 s = 50 ms = 800 samples, all of it absorbed one sample at a time.
    CHECK(adjusts > 700 && adjusts < 850);

    // 2.5 ms packets (40 output samples): the floor of max(1, 40/160) keeps one sample
    // per packet available, and 10000 ppm stays correctable without a re-anchor.
    Driver e;
    uint64_t adj_e = 0;
    bool more_than_one = false;
    for (int i = 0; i < 2000; ++i) {                 // 2000 * 2.5 ms = 5 s
        const int64_t ts = static_cast<int64_t>(
            std::llround(static_cast<double>(i) * 25000.0 * (1.0 + 10000e-6)));
        e.feed(ts, 0, static_cast<uint64_t>(i) * 40, 40);
        const uint64_t now = e.norm.stats().inserted_samples + e.norm.stats().removed_samples;
        if (now != adj_e) {
            if (now != adj_e + 1) more_than_one = true;
            adj_e = now;
        }
    }
    CHECK(!more_than_one);
    CHECK(adj_e > 100);                              // the limiter was actually exercised
    CHECK(e.norm.stats().reanchors == 0);

    // A 40 ms packet may correct up to four samples at once (limit = 640 / 160).
    Driver g;
    for (int i = 0; i < 40; ++i) {                   // build up ~7 ms of drift first
        const int64_t ts = static_cast<int64_t>(
            std::llround(static_cast<double>(i) * 400000.0 * (1.0 + 18000e-6)));
        g.feed(ts, 0, static_cast<uint64_t>(i) * 640, 640);
    }
    const uint64_t big = g.norm.stats().inserted_samples + g.norm.stats().removed_samples;
    CHECK(big >= 40 * 4 - 8);                        // ~4 samples on almost every packet
}

// A jump > 3 ms against the predicted continuity rebuilds and re-anchors.
void test_jump_reanchor() {
    Driver d;
    for (int i = 0; i < 10; ++i) d.feed(static_cast<int64_t>(i) * kPacketTicks);
    CHECK(d.gaps.size() == 0);
    const int64_t jumped = 10 * kPacketTicks + 50000;   // 5 ms late
    d.feed(jumped);
    CHECK(d.disc);
    CHECK(d.norm.stats().reanchors == 1);
    CHECK(d.gaps.size() == 1);
    CHECK(d.gaps.items()[0].reason == GapReason::TimestampDiscontinuity);
    CHECK_NEAR(d.gaps.items()[0].duration_sec, 0.005, 1e-4);
    CHECK(d.start == 1680);                           // round(10.5 ms+ ... ) = 100 ms + 5 ms
    // A 2 ms jump stays below the threshold.
    Driver e;
    for (int i = 0; i < 10; ++i) e.feed(static_cast<int64_t>(i) * kPacketTicks);
    e.feed(10 * kPacketTicks + 20000);
    CHECK(!e.disc);
    CHECK(e.norm.stats().reanchors == 0);
}

// A drift the corrector cannot keep up with crosses 20 ms and re-anchors.
void test_drift_reanchor() {
    Driver d;
    for (int i = 0; i < 300; ++i) {
        const int64_t ts = static_cast<int64_t>(
            std::llround(static_cast<double>(i) * kPacketTicks * (1.0 + 30000e-6)));
        d.feed(ts, 0, static_cast<uint64_t>(i) * kFrames);
    }
    CHECK(d.norm.stats().reanchors >= 1);
    CHECK(d.gaps.size() >= 1);
    CHECK(d.gaps.items()[0].reason == GapReason::TimestampDiscontinuity);
    CHECK(d.gaps.items()[0].duration_sec > 0.019);
}

// AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR: the time comes from the previous packet's end.
void test_timestamp_error() {
    Driver d;
    for (int i = 0; i < 5; ++i) d.feed(static_cast<int64_t>(i) * kPacketTicks);
    const int64_t before = d.norm.next_position();
    d.feed(999999999, kBufTimestampError);           // unusable QPC value
    CHECK(d.norm.stats().timestamp_errors == 1);
    CHECK(d.norm.stats().reanchors == 0);
    CHECK(d.start == before);                        // still contiguous
    CHECK(d.gaps.size() == 0);
}

// AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY: gap length from the device position delta.
void test_data_discontinuity() {
    Driver d;
    for (int i = 0; i < 5; ++i)
        d.feed(static_cast<int64_t>(i) * kPacketTicks, 0, static_cast<uint64_t>(i) * kFrames);
    // 20 ms of audio lost: device position jumps by 320 frames and so does the clock.
    d.feed(5 * kPacketTicks + 200000, kBufDataDiscontinuity, 5ull * kFrames + 320);
    CHECK(d.norm.stats().discontinuities == 1);
    CHECK(d.gaps.size() == 1);                       // exactly one, not one per rule
    CHECK_NEAR(d.gaps.items()[0].duration_sec, 0.020, 1e-9);
    CHECK(d.disc);                                   // > 3 ms re-anchors
    CHECK(d.norm.stats().reanchors == 1);

    // Below 3 ms: a gap is recorded but the stream is not re-anchored.
    Driver e;
    for (int i = 0; i < 5; ++i)
        e.feed(static_cast<int64_t>(i) * kPacketTicks, 0, static_cast<uint64_t>(i) * kFrames);
    e.feed(5 * kPacketTicks + 10000, kBufDataDiscontinuity, 5ull * kFrames + 16);
    CHECK(e.gaps.size() == 1);
    CHECK_NEAR(e.gaps.items()[0].duration_sec, 0.001, 1e-9);
    CHECK(!e.disc);
    CHECK(e.norm.stats().reanchors == 0);
}

// Spec 3.2: an app reference source produces no discontinuity gaps.
void test_app_source_no_gaps() {
    Driver d(true);
    for (int i = 0; i < 5; ++i)
        d.feed(static_cast<int64_t>(i) * kPacketTicks, 0, static_cast<uint64_t>(i) * kFrames);
    d.feed(5 * kPacketTicks + 500000, kBufDataDiscontinuity, 5ull * kFrames + 800);
    CHECK(d.disc);                                   // still re-anchors
    CHECK(d.norm.stats().reanchors == 1);
    CHECK(d.gaps.size() == 0);                       // but stays silent about it
}

// A resampler with an algorithmic delay must not move the time of sample 0.
class DelayedPassthrough final : public IResampler {
public:
    explicit DelayedPassthrough(double delay_sec) : delay_(delay_sec) {}
    void process(const float* in, size_t n, std::vector<float>& out) override {
        out.insert(out.end(), in, in + n);
    }
    double algorithmic_delay_seconds() const override { return delay_; }
    void reset() override {}

private:
    double delay_;
};

void test_delay_compensation() {
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    norm.configure(cfg, &gaps, [](double, double) -> std::unique_ptr<IResampler> {
        return std::unique_ptr<IResampler>(new DelayedPassthrough(0.002));   // 2 ms
    });
    std::vector<float> in(kFrames, 0.1f), out;
    InputPacket p;
    p.data = in.data();
    p.frames = kFrames;
    p.rate = 16000;
    p.qpc_100ns = 1000000;      // 100 ms
    CHECK(norm.process(p, out) == 0);
    CHECK(norm.origin_100ns() == 1000000 - 20000);   // sample 0 keeps its content time
    CHECK(out.size() == kFrames);
}

// 48 kHz input downsampled to 16 kHz keeps the position count and the anchor.
void test_resampling_rate() {
    GapLog gaps;
    CaptureNormalizer norm;
    CaptureNormalizer::Config cfg;
    norm.configure(cfg, &gaps);
    std::vector<float> in(480, 0.0f), out;
    for (size_t i = 0; i < in.size(); ++i)
        in[i] = static_cast<float>(std::sin(static_cast<double>(i) * 0.01));
    int64_t produced = 0;
    for (int i = 0; i < 100; ++i) {
        InputPacket p;
        p.data = in.data();
        p.frames = 480;
        p.rate = 48000;
        p.qpc_100ns = static_cast<int64_t>(i) * kPacketTicks;
        p.device_position = static_cast<uint64_t>(i) * 480;
        const int64_t start = norm.process(p, out);
        CHECK(start == produced);
        produced += static_cast<int64_t>(out.size());
    }
    CHECK(produced >= 15990 && produced <= 16000);    // ~1 s of 16 kHz audio
    CHECK(norm.stats().reanchors == 0);
    CHECK(gaps.size() == 0);
}

// drift_ppm measures the clock over the anchored stretches only. A hole (here 0.43 s, the
// process-loopback endpoint wake-up of 2026-11-01) is lost audio, not drift: the old
// first-packet-to-now estimate read it as ~+97000 ppm over these 4.4 s.
void test_drift_ignores_holes(double ppm) {
    Driver d;
    int64_t hole = 0;
    for (int i = 0; i < 400; ++i) {
        if (i == 200) hole = 4300000;                // 430 ms without a packet
        const int64_t ts = static_cast<int64_t>(std::llround(
                               static_cast<double>(i) * kPacketTicks * (1.0 + ppm * 1e-6))) +
                           hole;
        d.feed(ts);
    }
    CHECK(d.norm.stats().reanchors == 1);
    CHECK(d.gaps.size() == 1);
    CHECK_NEAR(d.norm.stats().drift_ppm, ppm, 2.0);
}

}  // namespace

void test_drift() {
    test_steady();
    test_sub_millisecond_ignored();
    test_ppm(250.0);
    test_ppm(-250.0);
    test_adjust_rate_limit();
    test_jump_reanchor();
    test_drift_reanchor();
    test_timestamp_error();
    test_data_discontinuity();
    test_app_source_no_gaps();
    test_delay_compensation();
    test_resampling_rate();
    test_drift_ignores_holes(0.0);
    test_drift_ignores_holes(100.0);
}
