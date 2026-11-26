// LevelMeter: the spec 3.6 formula, the 80 ms cadence and the fall to 0 after 0.35 s.
#include "core/levels.h"
#include "core/timeline.h"
#include "test_util.h"

#include <cmath>
#include <vector>

using namespace wf;

namespace {

void test_formula() {
    CHECK_NEAR(LevelMeter::level_from_rms(1.0f), 1.0f, 1e-6);          // 0 dBFS
    CHECK_NEAR(LevelMeter::level_from_rms(0.1f), 2.0 / 3.0, 1e-6);     // -20 dBFS
    CHECK_NEAR(LevelMeter::level_from_rms(0.01f), 1.0 / 3.0, 1e-6);    // -40 dBFS
    CHECK_NEAR(LevelMeter::level_from_rms(0.001f), 0.0f, 1e-6);        // -60 dBFS = bottom
    CHECK_NEAR(LevelMeter::level_from_rms(0.0f), 0.0f, 1e-6);          // floor 1e-5 -> clamp
    CHECK_NEAR(LevelMeter::level_from_rms(2.0f), 1.0f, 1e-6);          // clamped at 1
}

// Changed against the earlier spec reading: the Mac publishes the RMS of the ONE block that
// crosses the 80 ms throttle (AudioCapture.swift:156, LegacyAudioCapture.level), and the
// first block after a reset publishes immediately - it never averages across the window.
void test_cadence_and_value() {
    LevelMeter m;
    m.reset();
    std::vector<float> block(160, 0.1f);             // constant 0.1 -> rms 0.1
    int64_t t = 0;
    m.push(block.data(), block.size(), t);           // the first block publishes at once
    CHECK(m.updates() == 1);
    CHECK_NEAR(m.value(t), 2.0 / 3.0, 1e-4);
    t += k::kTicksPerFrame;
    // 10..70 ms: inside the 80 ms throttle, nothing new is published.
    for (int i = 1; i < 8; ++i) {
        m.push(block.data(), block.size(), t);
        t += k::kTicksPerFrame;
    }
    CHECK(m.updates() == 1);
    m.push(block.data(), block.size(), t);           // 80 ms reached
    CHECK(m.updates() == 2);
    CHECK_NEAR(m.value(t), 2.0 / 3.0, 1e-4);

    // Value holds between updates, then decays to 0 after 0.35 s without new audio.
    CHECK_NEAR(m.value(t + 3 * k::kTicksPerFrame), 2.0 / 3.0, 1e-4);
    CHECK_NEAR(m.value(t + k::kLevelDecayTicks), 2.0 / 3.0, 1e-4);
    CHECK_NEAR(m.value(t + k::kLevelDecayTicks + 1), 0.0f, 1e-6);
}

void test_silence_drops_to_zero() {
    LevelMeter m;
    m.reset();
    std::vector<float> loud(160, 0.5f), quiet(160, 0.0f);
    int64_t t = 0;
    for (int i = 0; i < 16; ++i) {
        m.push(loud.data(), loud.size(), t);
        t += k::kTicksPerFrame;
    }
    CHECK(m.value(t) > 0.8f);
    for (int i = 0; i < 16; ++i) {
        m.push(quiet.data(), quiet.size(), t);
        t += k::kTicksPerFrame;
    }
    CHECK_NEAR(m.value(t), 0.0f, 1e-6);              // below the 1e-5 floor
}

// timeline.h: meeting offsets and the 1.5 sample anchor rule the archive writer mirrors.
void test_timeline_and_anchors() {
    Timeline tl(1'000'000, 12.5);                    // origin 0.1 s, 12.5 s of earlier sessions
    CHECK_NEAR(tl.offset_of(1'000'000), 12.5, 1e-9);
    CHECK_NEAR(tl.offset_of(2'000'000), 12.6, 1e-9);
    CHECK_NEAR(tl.offset_of_position(16000, 1'000'000), 13.5, 1e-9);
    CHECK_NEAR(tl.offset_of_frame(100, 1'000'000), 12.5 + 1.0, 1e-9);

    AnchorTracker tracker;
    AudioAnchor a;
    CHECK(tracker.note(0, 12.5, &a));                // the first anchor always lands
    CHECK(a.frame == 0);
    CHECK(!tracker.note(16000, 13.5));               // exactly on the prediction
    CHECK(!tracker.note(32000, 14.5 + 1.0 / 16000.0));          // 1 sample off: keep
    CHECK(!tracker.note(48000, 15.5 + 1.5 / 16000.0));          // exactly 1.5: still keep
    CHECK(tracker.note(64000, 16.5 + 2.0 / 16000.0, &a));       // > 1.5 samples: new anchor
    CHECK(a.frame == 64000);
    CHECK_NEAR(a.offset, 16.5 + 2.0 / 16000.0, 1e-9);
    CHECK_NEAR(predict_offset(a, 64000 + 16000), a.offset + 1.0, 1e-9);
}

}  // namespace

void test_levels() {
    test_formula();
    test_cadence_and_value();
    test_silence_drops_to_zero();
    test_timeline_and_anchors();
}
