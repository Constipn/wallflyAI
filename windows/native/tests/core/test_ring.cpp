// AudioRing: SPSC stress with a real producer thread (every sample must arrive in order
// or be counted as an overflow) and the epoch rule that discards slots from before a restart.
#include "core/ring.h"
#include "test_util.h"

#include <thread>

using namespace wf;

namespace {

void test_basic() {
    AudioRing ring;
    float block[160];
    for (int i = 0; i < 160; ++i) block[i] = static_cast<float>(i);
    CHECK(ring.push(block, 160, 1000, 77, 0));
    RingSlot slot;
    CHECK(ring.pop(slot));
    CHECK(slot.frames == 160);
    CHECK(slot.qpc_100ns == 1000);
    CHECK(slot.device_position == 77);
    CHECK(slot.data[159] == 159.0f);
    CHECK(!ring.pop(slot));
    CHECK(!ring.push(block, AudioRing::kSlotFrames + 1, 0, 0, 0));
}

void test_overflow() {
    AudioRing ring;
    float one = 1.0f;
    for (uint64_t i = 0; i < AudioRing::kSlots; ++i) CHECK(ring.push(&one, 1, 0, i, 0));
    CHECK(ring.size() == AudioRing::kSlots);
    CHECK(!ring.push(&one, 1, 0, 0, 0));          // producer drops instead of blocking
    CHECK(ring.overflows() == 1);
    RingSlot slot;
    CHECK(ring.pop(slot));
    CHECK(ring.push(&one, 1, 0, 0, 0));
}

void test_epoch() {
    AudioRing ring;
    float v = 1.0f;
    for (int i = 0; i < 5; ++i) CHECK(ring.push(&v, 1, i, 0, 0));
    ring.bump_epoch();                             // stream rebuilt: old slots are stale
    v = 2.0f;
    for (int i = 0; i < 3; ++i) CHECK(ring.push(&v, 1, 100 + i, 0, 0));
    RingSlot slot;
    int got = 0;
    while (ring.pop(slot)) {
        CHECK(slot.data[0] == 2.0f);
        CHECK(slot.qpc_100ns >= 100);
        ++got;
    }
    CHECK(got == 3);
    CHECK(ring.discarded() == 5);
}

void test_spsc_stress() {
    static AudioRing ring;
    constexpr int kBlocks = 20000;
    constexpr uint32_t kFrames = 64;

    std::thread producer([] {
        float block[kFrames];
        for (int b = 0; b < kBlocks; ++b) {
            for (uint32_t i = 0; i < kFrames; ++i) block[i] = static_cast<float>(b);
            while (!ring.push(block, kFrames, b, static_cast<uint64_t>(b) * kFrames, 0)) {
                std::this_thread::yield();         // keep the data, this test allows no drops
            }
        }
    });

    RingSlot slot;
    int received = 0;
    while (received < kBlocks) {
        if (!ring.pop(slot)) {
            std::this_thread::yield();
            continue;
        }
        if (slot.qpc_100ns != received || slot.frames != kFrames ||
            slot.data[0] != static_cast<float>(received) ||
            slot.data[kFrames - 1] != static_cast<float>(received) ||
            slot.device_position != static_cast<uint64_t>(received) * kFrames) {
            CHECK(false);
            break;
        }
        ++received;
    }
    producer.join();
    CHECK(received == kBlocks);
    CHECK(!ring.pop(slot));
}

}  // namespace

void test_ring() {
    test_basic();
    test_overflow();
    test_epoch();
    test_spsc_stress();
}
