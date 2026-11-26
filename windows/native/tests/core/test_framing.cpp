// Framer: frame index n = p / 160 on the shared grid. Mirrors EchoFrameAssembler, so a
// stream that starts or ends off the grid yields PARTIAL frames instead of trimmed audio,
// forward jumps leave the skipped indices missing instead of zero filled, and the framer
// itself records no gaps.
#include "core/framer.h"
#include "test_util.h"

#include <vector>

using namespace wf;

namespace {

std::vector<float> ramp(size_t n, float base) {
    std::vector<float> v(n);
    for (size_t i = 0; i < n; ++i) v[i] = base + static_cast<float>(i);
    return v;
}

// Contiguous input: indices follow p / 160 and the host time follows the origin.
void test_index_formula() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Me, 5'000'000, &gaps);   // origin at 0.5 s
    std::vector<Frame160> out;
    const std::vector<float> block = ramp(160, 0.0f);
    for (int i = 0; i < 10; ++i) f.push(i * 160, block.data(), block.size(), out);
    CHECK(out.size() == 10);
    for (size_t i = 0; i < out.size(); ++i) {
        if (out[i].index != static_cast<int64_t>(i)) CHECK(false);
        if (out[i].host_100ns != 5'000'000 + static_cast<int64_t>(i) * k::kTicksPerFrame)
            CHECK(false);
        if (out[i].s[0] != 0.0f || out[i].s[159] != 159.0f) CHECK(false);
    }
    CHECK(gaps.size() == 0);
}

// Starting off the grid: the head stays as a PARTIAL frame.
// Changed against the earlier spec reading: EchoFrameAssembler never trims a stream to the
// 160 grid, it zero fills the frame and records [valid_lo, valid_hi) (Mac parity).
void test_partial_head_and_tail() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Others, 0, &gaps);
    std::vector<Frame160> out;
    const std::vector<float> block = ramp(500, 0.0f);
    f.push(1000, block.data(), block.size(), out);   // 1000 = 6*160 + 40 .. 1499
    CHECK(f.stats().trimmed_samples == 0);           // nothing is thrown away any more
    CHECK(out.size() == 3);                          // frames 6 (partial), 7, 8
    CHECK(out[0].index == 6);
    CHECK(out[0].valid_lo == 40);
    CHECK(out[0].valid_hi == 160);
    CHECK(out[0].s[39] == 0.0f);                     // zero filled below valid_lo
    CHECK(out[0].s[40] == 0.0f);                     // ramp starts at 0
    CHECK(out[0].s[41] == 1.0f);
    CHECK(out[1].index == 7);
    CHECK(out[1].valid_lo == 0 && out[1].valid_hi == 160);
    CHECK(out[2].index == 8);
    CHECK(f.stats().partial_frames == 1);
    CHECK(gaps.size() == 0);                         // the assembler records no gaps

    // The trailing fragment comes out of finish(), once.
    CHECK(f.has_partial());
    f.finish(out);
    CHECK(out.size() == 4);
    CHECK(out[3].index == 9);
    CHECK(out[3].valid_lo == 0 && out[3].valid_hi == 60);
    f.finish(out);
    CHECK(out.size() == 4);                          // finish is idempotent

    // Every input sample reached exactly one frame.
    size_t carried = 0;
    for (const Frame160& fr : out) carried += static_cast<size_t>(valid_count(fr));
    CHECK(carried == 500);
}

// Samples at negative positions are dropped (Swift: "guard position >= 0").
void test_negative_positions_dropped() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Me, 0, &gaps);
    std::vector<Frame160> out;
    const std::vector<float> block = ramp(200, 0.0f);
    f.push(-50, block.data(), block.size(), out);    // -50 .. 149
    CHECK(f.stats().trimmed_samples == 50);
    f.finish(out);
    CHECK(out.size() == 1);
    CHECK(out[0].index == 0);
    CHECK(out[0].valid_lo == 0 && out[0].valid_hi == 150);
    CHECK(out[0].s[0] == 50.0f);                     // input sample at position 0
    CHECK(gaps.size() == 0);
}

// Blocks that do not fall on frame boundaries are stitched together.
void test_partial_blocks() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Me, 0, &gaps);
    std::vector<Frame160> out;
    const std::vector<float> block = ramp(100, 0.0f);
    int64_t pos = 0;
    for (int i = 0; i < 16; ++i) {                   // 1600 samples in 100-sample pieces
        f.push(pos, block.data(), block.size(), out);
        pos += 100;
    }
    CHECK(out.size() == 10);
    CHECK(out[9].index == 9);
    CHECK(f.next_position() == 1600);
}

// A forward jump leaves the skipped indices out; the gap itself belongs to the normalizer.
// Changed against the earlier spec reading: EchoFrameAssembler records no RecordingGap, the
// packet-level discontinuity is reported once by CaptureNormalizer (Mac: append()).
void test_forward_jump() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Me, 0, &gaps);
    std::vector<Frame160> out;
    const std::vector<float> block = ramp(160, 1.0f);
    for (int i = 0; i < 5; ++i) f.push(i * 160, block.data(), block.size(), out);
    CHECK(out.size() == 5);
    f.push(5 * 160 + 3 * 160, block.data(), block.size(), out);   // 30 ms missing
    CHECK(out.size() == 6);
    CHECK(out[5].index == 8);                        // 6 and 7 never appear, no zero fill
    CHECK((out[5].flags & kFrameFirstAfterGap) != 0);
    CHECK(gaps.size() == 0);
    CHECK(f.stats().skipped_frames == 3);
}

// Data landing back inside an already emitted frame re-emits that index, exactly as the
// Mac dictionary does; the echo processor is the layer that drops the late frame.
void test_backwards_data_reemits_index() {
    GapLog gaps;
    Framer f;
    f.configure(SourceKind::Me, 0, &gaps);
    std::vector<Frame160> out;
    const std::vector<float> a = ramp(160, 0.0f);
    for (int i = 0; i < 5; ++i) f.push(i * 160, a.data(), a.size(), out);
    CHECK(out.size() == 5);

    // Half of this block is older than what was already framed.
    const std::vector<float> b = ramp(320, 1000.0f);
    f.push(4 * 160, b.data(), b.size(), out);
    CHECK(gaps.size() == 0);                         // no gap from the assembler
    CHECK(f.stats().late_samples == 0);
    CHECK(out.size() == 7);
    CHECK(out[5].index == 4);                        // index 4 appears a second time
    CHECK(out[5].s[0] == 1000.0f);
    CHECK(out[6].index == 5);
    CHECK(out[6].s[0] == 1160.0f);

    // A fully late block is re-framed too, at its own index.
    f.push(0, a.data(), a.size(), out);
    CHECK(out.size() == 8);
    CHECK(out[7].index == 0);
    CHECK(gaps.size() == 0);
}

}  // namespace

void test_framing() {
    test_index_formula();
    test_partial_head_and_tail();
    test_negative_positions_dropped();
    test_partial_blocks();
    test_forward_jump();
    test_backwards_data_reemits_index();
}
