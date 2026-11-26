// Case-by-case port of AudioTimeMapTests.swift (Swift tests of the macOS app).
// AudioTimeMap lives in src/core/timeline.h; the anchor rule the archive writer applies is
// AnchorTracker (1.5 samples), which is what the two pipeline tests exercise.
#include "core/timeline.h"
#include "test_util.h"

#include <cmath>
#include <vector>

using namespace wf;

namespace {

constexpr int kFileOthers = 1;
constexpr int kFileMe = 2;

int64_t frame_or(const AudioTimeMap& map, double offset, int64_t fallback) {
    int64_t f = 0;
    return map.frame_for_offset(offset, &f) ? f : fallback;
}
bool no_frame(const AudioTimeMap& map, double offset) {
    int64_t f = 0;
    return !map.frame_for_offset(offset, &f);
}

// ---------------------------------------------------------------------------
// AudioTimeMapTests.mapsFramesAcrossGapsAndRejectsOffsetsInsideGaps
// ---------------------------------------------------------------------------
void test_maps_frames_across_gaps() {
    // 1 s of audio at 10 s, then a 2 s capture gap, then audio from 13 s on.
    const std::vector<AudioAnchor> all = {
        AudioAnchor{16000, 13.0, kFileOthers},
        AudioAnchor{0, 10.0, kFileOthers},
        AudioAnchor{0, 99.0, kFileMe},
    };
    const AudioTimeMap map = AudioTimeMap::for_file(all, kFileOthers, 48000);
    CHECK(map.anchors().size() == 2);
    CHECK_NEAR(map.offset_for_frame(0), 10.0, 1e-12);
    CHECK_NEAR(map.offset_for_frame(8000), 10.5, 1e-12);
    CHECK_NEAR(map.offset_for_frame(16000), 13.0, 1e-12);
    CHECK_NEAR(map.offset_for_frame(24000), 13.5, 1e-12);
    CHECK(frame_or(map, 10.0, -1) == 0);
    CHECK(frame_or(map, 10.5, -1) == 8000);
    CHECK(no_frame(map, 11.5));                 // inside the gap
    CHECK(no_frame(map, 9.9));                  // before the file
    CHECK(frame_or(map, 13.25, -1) == 20000);
    CHECK(no_frame(map, 15.0));                 // past frameCount
    const int64_t round_trip[] = {0, 1, 15999, 16000, 16001, 47999};
    for (int64_t frame : round_trip) {
        CHECK(frame_or(map, map.offset_for_frame(frame), -1) == frame);
    }
}

// ---------------------------------------------------------------------------
// AudioTimeMapTests.emptyAnchorsAssumeLinearFileFromZero
// ---------------------------------------------------------------------------
void test_empty_anchors_assume_linear_file() {
    const AudioTimeMap map{std::vector<AudioAnchor>{}};
    CHECK_NEAR(map.offset_for_frame(32000), 2.0, 1e-12);
    CHECK(frame_or(map, 2.0, -1) == 32000);
    CHECK(no_frame(map, -1.0));
}

// ---------------------------------------------------------------------------
// AudioTimeMapTests.laterAnchorForSameFrameWinsAndBackwardJumpPrefersEarliestFrame
// ---------------------------------------------------------------------------
void test_later_anchor_for_same_frame_wins() {
    const AudioTimeMap map{std::vector<AudioAnchor>{
        AudioAnchor{0, 0.0, 0},
        AudioAnchor{0, 1.0, 0},
        AudioAnchor{32000, 2.5, 0},   // clock stepped back by 0.5 s
    }};
    CHECK(map.anchors().size() == 2);
    CHECK_NEAR(map.anchors()[0].offset, 1.0, 1e-12);
    CHECK_NEAR(map.anchors()[1].offset, 2.5, 1e-12);
    CHECK(frame_or(map, 2.75, -1) == 28000);    // the earliest frame in the file wins
    CHECK(frame_or(map, 3.5, -1) == 48000);
}

// ---------------------------------------------------------------------------
// AudioTimeMapTests.fallbackRebuildsAnchorsFromRecordingGapsOfOneSource
// ---------------------------------------------------------------------------
void test_fallback_rebuilds_anchors_from_gaps() {
    std::vector<AudioTimeMap::GapSpan> gaps = {
        {5.0, 1.0, SourceKind::Me},
        {5.5, 1.0, SourceKind::Me},            // overlaps: counts once
        {3.0, 4.0, SourceKind::Others},        // other source
        {10.0, 2.0, SourceKind::Me},
    };
    const AudioTimeMap map =
        AudioTimeMap::from_gaps(gaps, SourceKind::Me, kFileMe, 0.0);
    CHECK(map.anchors().size() == 3);
    CHECK(map.anchors()[0].frame == 0);
    CHECK_NEAR(map.anchors()[0].offset, 0.0, 1e-12);
    CHECK(map.anchors()[1].frame == 80000);
    CHECK_NEAR(map.anchors()[1].offset, 6.5, 1e-12);
    CHECK(map.anchors()[2].frame == 136000);
    CHECK_NEAR(map.anchors()[2].offset, 12.0, 1e-12);
    CHECK(map.anchors()[2].file == kFileMe);
    CHECK_NEAR(map.offset_for_frame(80000), 6.5, 1e-12);
    CHECK_NEAR(map.offset_for_frame(136000 + 16000), 13.0, 1e-12);
    CHECK(no_frame(map, 6.0));
    CHECK(no_frame(map, 11.0));
    CHECK(frame_or(map, 4.0, -1) == 64000);

    const AudioTimeMap later =
        AudioTimeMap::from_gaps(gaps, SourceKind::Me, 0, 5.8);
    CHECK(later.anchors().size() == 2);
    CHECK(later.anchors()[0].frame == 0);
    CHECK(later.anchors()[1].frame == 56000);
    CHECK_NEAR(later.anchors()[0].offset, 6.5, 1e-12);
    CHECK_NEAR(later.anchors()[1].offset, 12.0, 1e-12);
}

// ---------------------------------------------------------------------------
// AudioTimeMapTests.deepgramPipelineAnchorsFirstSampleAndTimestampJumps
// The Windows archive writer applies AnchorTracker's 1.5 sample rule to the same inputs.
// ---------------------------------------------------------------------------
void test_pipeline_anchors_first_sample_and_timestamp_jumps() {
    AnchorTracker tracker;
    std::vector<AudioAnchor> anchors;
    int64_t written = 0;
    const struct { int frames; double offset; } packets[] = {
        {1600, 2.0},
        {1600, 2.1},            // contiguous
        {800, 2.2 + 1e-5},      // sub-sample jitter is snapped
        {1600, 4.0},            // capture gap of 1.75 s
    };
    for (const auto& p : packets) {
        AudioAnchor a;
        if (tracker.note(written, p.offset, &a)) anchors.push_back(a);
        written += p.frames;
    }
    CHECK(anchors.size() == 2);
    CHECK(anchors[0].frame == 0);
    CHECK_NEAR(anchors[0].offset, 2.0, 1e-12);
    CHECK(anchors[1].frame == 4000);
    CHECK_NEAR(anchors[1].offset, 4.0, 1e-12);
    CHECK(written == 5600);                 // AVAudioFile(forReading: url).length == 5600

    const AudioTimeMap map{anchors};
    CHECK_NEAR(map.offset_for_frame(3200), 2.2, 1e-9);
}

// ---------------------------------------------------------------------------
// AudioTimeMapTests.chunkedPipelineAnchorsFirstSampleAndTimestampJumps
// ---------------------------------------------------------------------------
void test_chunked_pipeline_anchors() {
    AnchorTracker tracker;
    std::vector<AudioAnchor> anchors;
    int64_t written = 0;
    const double offsets[] = {0.5, 0.6, 3.0};
    for (double offset : offsets) {
        AudioAnchor a;
        if (tracker.note(written, offset, &a)) anchors.push_back(a);
        written += 1600;
    }
    CHECK(anchors.size() == 2);
    CHECK(anchors[0].frame == 0);
    CHECK_NEAR(anchors[0].offset, 0.5, 1e-12);
    CHECK(anchors[1].frame == 3200);
    CHECK_NEAR(anchors[1].offset, 3.0, 1e-12);
}

// The 1.5 sample tolerance itself (ChunkedTranscriptionPipeline.swift:116).
void test_anchor_tolerance_is_one_and_a_half_samples() {
    AnchorTracker tracker;
    CHECK(tracker.note(0, 0.0));
    CHECK(!tracker.note(1600, 0.1 + 1.4 / 16000.0));
    CHECK(!tracker.note(3200, 0.2 + 1.49 / 16000.0));   // just inside: same anchor
    CHECK(tracker.note(4800, 0.3 + 1.6 / 16000.0));     // > 1.5 samples: new anchor
}

}  // namespace

void test_timemap_mac() {
    test_maps_frames_across_gaps();
    test_empty_anchors_assume_linear_file();
    test_later_anchor_for_same_frame_wins();
    test_fallback_rebuilds_anchors_from_gaps();
    test_pipeline_anchors_first_sample_and_timestamp_jumps();
    test_chunked_pipeline_anchors();
    test_anchor_tolerance_is_one_and_a_half_samples();
}

// Not ported - no core counterpart (persistence / event plumbing):
//   meetingsWithoutAnchorsStillDecodeAndAnchorsPersist   Meeting JSON round trip
//   eventInboxKeepsEveryAnchor                           TranscriptionEventInbox
