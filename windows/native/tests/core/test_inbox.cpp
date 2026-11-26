// CaptureInbox: 240 ms bound at the source rate, FIFO order, oldest-first drop on
// overflow with an InboxOverflow gap carrying {start, duration}.
#include "core/inbox.h"
#include "core/normalizer.h"
#include "test_util.h"

#include <vector>

using namespace wf;

namespace {

InputPacket make(const std::vector<float>& buf, uint32_t rate, int64_t qpc, uint64_t pos) {
    InputPacket p;
    p.data = buf.data();
    p.frames = static_cast<uint32_t>(buf.size());
    p.rate = rate;
    p.qpc_100ns = qpc;
    p.device_position = pos;
    return p;
}

void test_capacity_and_order() {
    GapLog gaps;
    CaptureInbox inbox;
    inbox.configure(SourceKind::Me, 48000, &gaps);
    CHECK(inbox.capacity_frames() == 11520);        // 0.240 s * 48 kHz

    std::vector<float> block(480, 0.0f);
    for (int i = 0; i < 10; ++i) {
        block[0] = static_cast<float>(i);
        CHECK(inbox.push(make(block, 48000, i * 100000, static_cast<uint64_t>(i) * 480)));
    }
    CHECK(inbox.queued_frames() == 4800);
    InputPacket out;
    for (int i = 0; i < 10; ++i) {
        CHECK(inbox.pop(out));
        CHECK(out.frames == 480);
        CHECK(out.data[0] == static_cast<float>(i));
        CHECK(out.qpc_100ns == i * 100000);
        CHECK(out.device_position == static_cast<uint64_t>(i) * 480);
    }
    CHECK(!inbox.pop(out));
    CHECK(gaps.size() == 0);
}

void test_overflow_drops_oldest() {
    GapLog gaps;
    CaptureInbox inbox;
    inbox.configure(SourceKind::Others, 16000, &gaps);
    CHECK(inbox.capacity_frames() == 3840);          // 0.240 s * 16 kHz

    std::vector<float> block(160, 0.0f);
    bool overflowed = false;
    for (int i = 0; i < 30; ++i) {                   // 30 * 10 ms = 300 ms > 240 ms
        block[0] = static_cast<float>(i);
        if (!inbox.push(make(block, 16000, i * 100000, static_cast<uint64_t>(i) * 160)))
            overflowed = true;
    }
    CHECK(overflowed);
    CHECK(inbox.queued_frames() <= inbox.capacity_frames());
    CHECK(inbox.stats().dropped_packets == 6);       // 30 - 24 packets fit
    CHECK(gaps.size() >= 1);
    const Gap& g = gaps.items().front();
    CHECK(g.source == SourceKind::Others);
    CHECK(g.reason == GapReason::InboxOverflow);
    CHECK(g.start_100ns == 0);                       // the first packet was the first dropped
    CHECK_NEAR(g.duration_sec, 0.010, 1e-9);

    InputPacket out;                                 // the oldest survivor is packet 6
    CHECK(inbox.pop(out));
    CHECK(out.data[0] == 6.0f);
}

void test_oversized_packet() {
    GapLog gaps;
    CaptureInbox inbox;
    inbox.configure(SourceKind::Me, 16000, &gaps);
    std::vector<float> big(5000, 0.0f);              // 312 ms in one packet
    big[5000 - 1] = 9.0f;
    CHECK(!inbox.push(make(big, 16000, 500, 0)));
    CHECK(inbox.queued_frames() == inbox.capacity_frames());
    CHECK(gaps.size() == 1);
    CHECK_NEAR(gaps.items()[0].duration_sec, (5000.0 - 3840.0) / 16000.0, 1e-9);
    InputPacket out;
    CHECK(inbox.pop(out));
    CHECK(out.frames == 3840);
    CHECK(out.data[3839] == 9.0f);                   // the tail was kept
}

void test_rate_change_reconfigures() {
    GapLog gaps;
    CaptureInbox inbox;
    inbox.configure(SourceKind::Me, 16000, &gaps);
    std::vector<float> a(160, 0.5f);
    CHECK(inbox.push(make(a, 16000, 0, 0)));
    std::vector<float> b(480, 0.25f);
    CHECK(inbox.push(make(b, 48000, 100000, 0)));
    CHECK(inbox.rate() == 48000);
    CHECK(inbox.capacity_frames() == 11520);
    InputPacket out;
    CHECK(inbox.pop(out));
    CHECK(out.frames == 480);                        // the 16 kHz leftovers were dropped
    CHECK(!inbox.pop(out));
}

// One loss, one report: the first survivor after an overflow carries the dropped duration as
// hole_reported_100ns, and the normalizer then logs no TimestampDiscontinuity twin for that
// hole - only an independent excess beyond it (here 50 ms of real device hole).
void test_overflow_is_reported_once() {
    for (int extra_ms : {0, 50}) {
        GapLog gaps;
        CaptureInbox inbox;
        inbox.configure(SourceKind::Me, 16000, &gaps);
        CaptureNormalizer norm;
        CaptureNormalizer::Config cfg;
        cfg.source = SourceKind::Me;
        norm.configure(cfg, &gaps);
        std::vector<float> block(160, 0.1f);
        std::vector<float> out;
        out.reserve(4096);
        // Anchor the normalizer on 5 packets, then 30 packets in one go: 6 are dropped.
        int64_t ts = 0;
        for (int i = 0; i < 5; ++i, ts += 100000) {
            CHECK(inbox.push(make(block, 16000, ts, 0)));
            InputPacket p;
            CHECK(inbox.pop(p));
            CHECK(p.hole_reported_100ns == 0);
            norm.process(p, out);
        }
        const size_t before = gaps.size();
        for (int i = 0; i < 30; ++i, ts += 100000) {
            // From packet 6 on, an extra device hole of extra_ms sits before the survivors.
            const int64_t shift = (i >= 6) ? extra_ms * 10000 : 0;
            inbox.push(make(block, 16000, ts + shift, 0));
        }
        CHECK(gaps.size() == before + 6);            // one InboxOverflow per drop (merged
                                                     // later by the event layer)
        InputPacket p;
        bool first = true;
        while (inbox.pop(p)) {
            CHECK(p.hole_reported_100ns == (first ? 600000 : 0));   // 6 x 10 ms, then none
            first = false;
            norm.process(p, out);
        }
        int overflow = 0, discontinuity = 0;
        double overflow_sec = 0.0;
        for (const Gap& g : gaps.items()) {
            if (g.reason == GapReason::InboxOverflow) {
                ++overflow;
                overflow_sec += g.duration_sec;
            }
            if (g.reason == GapReason::TimestampDiscontinuity) {
                ++discontinuity;
                CHECK_NEAR(g.duration_sec, extra_ms / 1000.0, 1e-3);
            }
        }
        CHECK(overflow == 6);
        CHECK_NEAR(overflow_sec, 0.060, 1e-9);
        CHECK(discontinuity == (extra_ms > 0 ? 1 : 0));   // no twin for the 60 ms
    }
}

}  // namespace

void test_inbox() {
    test_capacity_and_order();
    test_overflow_drops_oldest();
    test_oversized_packet();
    test_rate_change_reconfigures();
    test_overflow_is_reported_once();
}
