// EchoProcessor: port of EchoAudioProcessor (EchoAudioProcessor.swift:250-432).
//
// Per mic frame n, in the Mac's order:
//   1. a frame with index <= lastMicrophone is dropped as a late frame;
//   2. "covered" = a reference frame n exists AND its valid range covers the mic's; if not,
//      the frame waits until now >= end of its VALID part + 100 ms (Swift: mic.time +
//      mic.duration + 0.1), then runs anyway;
//   3. a mic hole first moves lastRender to n-1 so render and capture stay 1:1;
//   4. every buffered reference frame with index <= n is consumed, rendered when its index
//      is newer than lastRender - render always precedes the matching capture and future
//      playback is never fed;
//   5. an uncovered frame is counted as unfiltered and gets a single zero render frame.
//      It is never a gap and never resets the filter.
//
// Latency compensation runs in FED space, not timeline space: output sample k of the m-th
// fed frame is fed sample m*160 + k - 224, and that fed frame's own timeline index decides
// where it lands. A microphone hole therefore never shifts the cleaned audio and never
// discards the 224-sample tail of the audio before the hole (Swift: EchoAudioProcessor.feed).
//
// Reference ageing is driven by the WALL CLOCK, at the end of every process() call:
// "frames[.reference] = filter { key >= Int((now - 0.5) * 100) }".
//
// mn_aec_reset is reached only through the two mic entry points below, mirroring
// resetEchoCanceller(), plus the Windows render-endpoint change (on_render_changed); a
// reference loss, a time jump or a buffer overflow never resets.
//
// Config::enable_aec = false (WfSessionConfig.enable_aec = 0, a Windows addition - the Mac
// always cancels) bypasses the bridge completely: it is never created, so no mn_aec_* call
// happens at all. A mic frame leaves in the same process() call that sees it, unchanged
// apart from the bridge's own [-1, 1] / NaN scrubbing, at its own timeline index (latency 0,
// no flush frames, no wait for the reference, never kFrameUnfiltered). Late and duplicate
// frames are still dropped as LateFrame gaps so the Me timeline stays strictly increasing.
// The reference still passes through to Others but is not buffered, because nothing would
// ever consume it: no store growth, no ageing work, no "Processing buffer overflowed" gaps
// for a buffer that has no purpose. metrics() reports ERLE 0 dB / delay 0 ms.
//
// Deliberate Windows deviations from EchoAudioProcessor.swift:
//  * the Mac emits emitClean() chunks the moment the canceller produced them (three PCM
//    buffers for the first two mic frames); WfFrame is a fixed 160 sample frame, so we emit
//    one frame per timeline index as soon as it is complete. Same samples, same positions,
//    same total, up to one frame later and in frame-sized pieces.
//  * the Mac distinguishes "Input buffer overflowed" (CaptureInbox) from "Processing buffer
//    overflowed" (the 49 frame cap here). The C ABI in src/api enumerates six gap reasons
//    the session layer maps the canceller's overflow to WF_GAP_PROCESSING_OVERFLOW and the inbox's to WF_GAP_INBOX_OVERFLOW.
//  * the Mac has one CaptureSource per hardware source (.microphone/.reference/.meeting);
//    the core knows only Me and Others, so a reference overflow is reported on Others.
#pragma once
#include <cstdint>
#include <vector>

#include "aec/AECBridge.h"
#include "core/types.h"

namespace wf {

// Index-ordered frame store, the C++ equivalent of Swift's [Int: EchoFrame] dictionary:
// insertion keeps the index order and an existing entry with the same index is REPLACED,
// never duplicated. Storage is allocated once in init().
class FrameStore {
public:
    void init(size_t capacity);
    void clear() { v_.clear(); }
    bool empty() const { return v_.empty(); }
    size_t size() const { return v_.size(); }
    const Frame160& front() const { return v_.front(); }
    const Frame160& at(size_t i) const { return v_[i]; }
    const Frame160* find(int64_t index) const;
    void insert(const Frame160& f);
    void erase_front();

private:
    std::vector<Frame160> v_;   // ascending by index
};

class EchoProcessor {
public:
    using NowFn = int64_t (*)(void* ctx);   // host clock in 100 ns units, injectable

    struct Config {
        int  ns_level = k::kNsLevel;
        bool enable_aec = true;
    };

    struct Stats {
        uint64_t me_frames = 0;
        uint64_t others_frames = 0;
        uint64_t unfiltered_frames = 0;   // reference missing or not covering the mic frame
        uint64_t late_dropped = 0;        // mic frames with index <= lastMicrophone
        uint64_t ref_discarded = 0;       // reference aged out by the 0.5 s wall-clock cutoff
        uint64_t ref_overflow = 0;        // reference frames dropped at the 49 cap
        uint64_t cap_overflow = 0;        // mic frames dropped at the 49 cap
        uint64_t resets = 0;              // mn_aec_reset calls
        uint64_t render_resets = 0;       // ... of them because the render endpoint changed
        uint64_t realignments = 0;        // kept for the session layer; fed space never splits
        uint64_t rendered = 0;
        uint64_t captured = 0;
    };

    EchoProcessor();
    ~EchoProcessor();
    EchoProcessor(const EchoProcessor&) = delete;
    EchoProcessor& operator=(const EchoProcessor&) = delete;

    void configure(const Config& cfg, GapLog* gaps, int64_t origin_100ns);
    void set_clock(NowFn fn, void* ctx) { now_fn_ = fn; now_ctx_ = ctx; }

    // The reference passes through to Others unchanged (its own timeline) and is queued
    // for the canceller. Frames with digital silence count as a valid reference.
    void push_reference(const Frame160& f, std::vector<Frame160>* others_out);
    void push_capture(const Frame160& f);

    // Emits every Me frame that can be produced now. Call after pushes and on each tick.
    void process(std::vector<Frame160>& me_out);
    // EchoAudioProcessor.finish: drain without waiting, then flushEchoTail.
    void stop(std::vector<Frame160>& me_out);

    // The only two reasons for a filter reset (spec 3.3 step 5).
    // Device change mirrors reset(.microphone): the buffered frames and the delayed tail
    // become gaps, the reference buffer and lastRender are cleared.
    void on_mic_device_changed();
    // Format change mirrors append()'s formatChanged branch: flushEchoTail FIRST, so the
    // audio still inside the canceller is emitted instead of lost. Pass the output vector;
    // without it the delayed tail can only be reported as a gap.
    void on_mic_format_changed(std::vector<Frame160>* me_out = nullptr);
    // Windows addition (AP3, 2026-11-23): the echo path moved to another render endpoint
    // (headset <-> loudspeakers). The mic itself is unchanged, so its delayed tail is
    // emitted first (flushEchoTail, as on a format change) instead of becoming a gap; then
    // every buffered reference frame of the old endpoint is dropped (none can be rendered
    // against a later mic frame) and the canceller restarts from scratch, so it converges on
    // the new path instead of carrying the old one. Mic frames already buffered stay and run
    // against whatever reference the new endpoint delivers for them.
    void on_render_changed(std::vector<Frame160>* me_out = nullptr);

    void metrics(double* erle_db, int* delay_ms) const;
    const Stats& stats() const { return stats_; }
    int64_t last_microphone_index() const { return last_mic_; }
    int64_t last_render_index() const { return last_render_; }
    // EchoCanceller.latencySamples: 128 (AEC3 framing) plus 96 when the suppressor runs;
    // 0 with enable_aec == false.
    int latency_samples() const { return latency_samples_; }
    int flush_frames() const { return flush_frames_; }

private:
    // One frame handed to mn_aec_capture, in fed order. This is Swift's fedFrames entry
    // plus the pendingMicrophone range that belongs to it.
    struct Fed {
        int64_t  fed_index = 0;
        int64_t  index = 0;        // timeline frame index
        int      valid_lo = 0;
        int      valid_hi = 0;
        uint32_t flags = 0;
    };

    int64_t now() const;
    void run(std::vector<Frame160>& me_out, bool draining);
    void run_passthrough(std::vector<Frame160>& me_out);   // enable_aec == false
    void age_reference();
    void feed_output(int64_t index, int valid_lo, int valid_hi, uint32_t flags,
                     std::vector<Frame160>& me_out);
    void drain_fed(std::vector<Frame160>& me_out);
    void flush_tail(std::vector<Frame160>& me_out);
    void discard_pending(GapReason reason);
    void reset_canceller();
    void gap(const Frame160& f, SourceKind source, GapReason reason);

    mn_aec* aec_ = nullptr;
    GapLog* gaps_ = nullptr;
    Config  cfg_;
    int64_t origin_100ns_ = 0;
    NowFn   now_fn_ = nullptr;
    void*   now_ctx_ = nullptr;

    FrameStore ref_;
    FrameStore cap_;
    int64_t last_mic_ = -1;        // Swift lastMicrophone
    int64_t last_render_ = -1;     // Swift lastRender
    bool    finished_ = false;
    int     latency_samples_ = k::kLatencyCompensationSamples;
    int     flush_frames_ = k::kFlushFrames;

    // Latency-compensated output in FED space: fifo_[0] sits at fed position fifo_fed_pos_.
    std::vector<Fed>   fed_;       // fed frames whose output is not fully emitted yet
    std::vector<float> fifo_;
    int64_t fed_count_ = 0;
    int64_t fifo_fed_pos_ = 0;
    bool    fifo_live_ = false;
    float   out_[k::kFrameSamples] = {};
    Stats   stats_;
};

}  // namespace wf
