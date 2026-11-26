#include "MNAudioRing.h"
#include <stdatomic.h>
#include <stdlib.h>
#include <string.h>

typedef struct {
    uint64_t start;
    uint64_t hostTime;
    uint32_t frames;
    uint32_t epoch;
} MNAudioRingEntry;

struct MNAudioRing {
    uint32_t channels, capacity, blockCapacity;
    float **data;
    MNAudioRingEntry *entries;
    uint64_t writeFrame; // producer-private
    _Atomic uint64_t writeBlock;
    _Atomic uint64_t readBlock;
    _Atomic uint64_t readFrame;
    _Atomic uint64_t overruns;
};

MNAudioRing *MNAudioRingCreate(uint32_t channels, uint32_t capacityFrames, uint32_t capacityBlocks) {
    if (channels == 0 || capacityFrames == 0 || capacityBlocks == 0) return NULL;
    MNAudioRing *ring = calloc(1, sizeof(MNAudioRing));
    if (!ring) return NULL;
    ring->channels = channels; ring->capacity = capacityFrames; ring->blockCapacity = capacityBlocks;
    ring->data = calloc(channels, sizeof(float *));
    ring->entries = calloc(capacityBlocks, sizeof(MNAudioRingEntry));
    bool ok = ring->data && ring->entries;
    for (uint32_t c = 0; ok && c < channels; c++) ok = (ring->data[c] = calloc(capacityFrames, sizeof(float))) != NULL;
    if (!ok) { MNAudioRingDestroy(ring); return NULL; }
    atomic_init(&ring->writeBlock, 0); atomic_init(&ring->readBlock, 0);
    atomic_init(&ring->readFrame, 0); atomic_init(&ring->overruns, 0);
    return ring;
}

void MNAudioRingDestroy(MNAudioRing *ring) {
    if (!ring) return;
    if (ring->data) for (uint32_t c = 0; c < ring->channels; c++) free(ring->data[c]);
    free(ring->data); free(ring->entries); free(ring);
}

uint32_t MNAudioRingChannels(const MNAudioRing *ring) { return ring->channels; }

bool MNAudioRingWrite(MNAudioRing *ring, const float *const *channels, uint32_t frames, uint64_t hostTime, uint32_t epoch) {
    uint64_t block = atomic_load_explicit(&ring->writeBlock, memory_order_relaxed);
    uint64_t readBlock = atomic_load_explicit(&ring->readBlock, memory_order_acquire);
    uint64_t readFrame = atomic_load_explicit(&ring->readFrame, memory_order_acquire);
    if (frames == 0 || frames > ring->capacity || block - readBlock >= ring->blockCapacity ||
        ring->writeFrame - readFrame + frames > ring->capacity) {
        atomic_fetch_add_explicit(&ring->overruns, 1, memory_order_relaxed);
        return false;
    }
    uint32_t offset = (uint32_t)(ring->writeFrame % ring->capacity);
    uint32_t first = frames < ring->capacity - offset ? frames : ring->capacity - offset;
    for (uint32_t c = 0; c < ring->channels; c++) {
        memcpy(ring->data[c] + offset, channels[c], first * sizeof(float));
        if (first < frames) memcpy(ring->data[c], channels[c] + first, (frames - first) * sizeof(float));
    }
    ring->entries[block % ring->blockCapacity] = (MNAudioRingEntry){ring->writeFrame, hostTime, frames, epoch};
    ring->writeFrame += frames;
    atomic_store_explicit(&ring->writeBlock, block + 1, memory_order_release);
    return true;
}

bool MNAudioRingPeek(MNAudioRing *ring, MNAudioRingBlock *block) {
    uint64_t read = atomic_load_explicit(&ring->readBlock, memory_order_relaxed);
    if (read == atomic_load_explicit(&ring->writeBlock, memory_order_acquire)) return false;
    MNAudioRingEntry entry = ring->entries[read % ring->blockCapacity];
    *block = (MNAudioRingBlock){entry.hostTime, entry.frames, entry.epoch};
    return true;
}

bool MNAudioRingRead(MNAudioRing *ring, float *const *channels, uint32_t capacity, MNAudioRingBlock *block) {
    uint64_t read = atomic_load_explicit(&ring->readBlock, memory_order_relaxed);
    if (read == atomic_load_explicit(&ring->writeBlock, memory_order_acquire)) return false;
    MNAudioRingEntry entry = ring->entries[read % ring->blockCapacity];
    if (channels) {
        uint32_t frames = entry.frames < capacity ? entry.frames : capacity;
        uint32_t offset = (uint32_t)(entry.start % ring->capacity);
        uint32_t first = frames < ring->capacity - offset ? frames : ring->capacity - offset;
        for (uint32_t c = 0; c < ring->channels; c++) {
            if (!channels[c]) continue;
            memcpy(channels[c], ring->data[c] + offset, first * sizeof(float));
            if (first < frames) memcpy(channels[c] + first, ring->data[c], (frames - first) * sizeof(float));
        }
    }
    *block = (MNAudioRingBlock){entry.hostTime, entry.frames, entry.epoch};
    // Release the samples before the block slot: the producer checks both.
    atomic_store_explicit(&ring->readFrame, entry.start + entry.frames, memory_order_release);
    atomic_store_explicit(&ring->readBlock, read + 1, memory_order_release);
    return true;
}

uint64_t MNAudioRingOverruns(MNAudioRing *ring) { return atomic_load_explicit(&ring->overruns, memory_order_relaxed); }
