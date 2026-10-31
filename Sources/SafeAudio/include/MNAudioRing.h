#pragma once
#include <stdbool.h>
#include <stdint.h>

#ifdef __cplusplus
extern "C" {
#endif
/// Single-producer/single-consumer ring of non-interleaved Float32 blocks with per-block metadata.
/// The producer side never allocates, locks or calls Objective-C (safe on the real-time IO thread).
typedef struct MNAudioRing MNAudioRing;
typedef struct {
    uint64_t hostTime;
    uint32_t frames;
    uint32_t epoch;
} MNAudioRingBlock;

MNAudioRing *_Nullable MNAudioRingCreate(uint32_t channels, uint32_t capacityFrames, uint32_t capacityBlocks);
void MNAudioRingDestroy(MNAudioRing *_Nullable ring);
uint32_t MNAudioRingChannels(const MNAudioRing *_Nonnull ring);
/// Producer only. A block that does not fit is dropped whole and counted as an overrun.
bool MNAudioRingWrite(MNAudioRing *_Nonnull ring, const float *_Nonnull const *_Nonnull channels, uint32_t frames, uint64_t hostTime, uint32_t epoch);
/// Consumer only. Metadata of the oldest unread block, without consuming it.
bool MNAudioRingPeek(MNAudioRing *_Nonnull ring, MNAudioRingBlock *_Nonnull block);
/// Consumer only. Consumes the oldest block, copying at most `capacity` frames per channel
/// (`channels` may be NULL to discard). `block->frames` is the block's full length.
bool MNAudioRingRead(MNAudioRing *_Nonnull ring, float *_Nullable const *_Nullable channels, uint32_t capacity, MNAudioRingBlock *_Nonnull block);
uint64_t MNAudioRingOverruns(MNAudioRing *_Nonnull ring);
#ifdef __cplusplus
}
#endif
