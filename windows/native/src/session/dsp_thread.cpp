// DspThread implementation.
//
// Note on origins: every source is framed on ONE grid anchored at the session origin
// (the host time of wf_session_start), not on its own first sample. Spec 3.3 step 1
// requires exactly that - "Index = round(t * 16000) / 160 auf einer gemeinsamen Uhr" -
// because the echo canceller matches reference frame n with mic frame n. Each stream's
// own anchor becomes SourceState::pos_offset, so sample positions, frame indices and
// meeting offsets are directly comparable across sources.
#include "session/dsp_thread.h"

#include <avrt.h>

#include <algorithm>
#include <cmath>
#include <cstring>

#include "session/event_queue.h"

#ifndef CREATE_WAITABLE_TIMER_HIGH_RESOLUTION
#define CREATE_WAITABLE_TIMER_HIGH_RESOLUTION 0x00000002
#endif

namespace wf {

namespace {
// Plan: "ClockJitterWarning bei p95 > 3 ms".
constexpr double kJitterWarnMs = 3.0;
// timestamp_mode 1 low-passes the QPC anchor with tau ~ 2 s. At the ~10 ms packet
// cadence of every stream we measured that is one 0.005 step per packet.
constexpr double kAnchorAlpha = 0.005;
// Reference before Others before mic: within one slice the order does not matter (the
// canceller sorts by frame index), but it keeps the intent of spec 3.3 step 2 - the render
// side of a slice is always in the canceller before the mic frames of the same slice.
constexpr Role kPipelineOrder[kRoleCount] = {Role::Reference, Role::Others, Role::Mic};

// wf::GapReason -> WF_GAP_*, spelled out so the two lists cannot drift apart silently.
// WF_GAP_PROCESSING_OVERFLOW has no core value; drain_echo_gaps() assigns it.
uint32_t abi_gap_reason(GapReason r) {
    switch (r) {
        case GapReason::InboxOverflow: return WF_GAP_INBOX_OVERFLOW;
        case GapReason::TimestampDiscontinuity: return WF_GAP_TIMESTAMP_DISCONTINUITY;
        case GapReason::LateFrame: return WF_GAP_LATE_FRAME;
        case GapReason::DeviceLost: return WF_GAP_DEVICE_LOST;
        case GapReason::Restart: return WF_GAP_RESTART;
        case GapReason::WatchdogRecovery: return WF_GAP_WATCHDOG_RECOVERY;
    }
    return WF_GAP_RESTART;
}
}  // namespace

// ---------------------------------------------------------------------------
// JitterTracker
// ---------------------------------------------------------------------------
void JitterTracker::reset() {
    n_ = 0;
    p95_.store(0.0, std::memory_order_relaxed);
    warned_ = false;
    warn_pending_ = false;
}

void JitterTracker::add(double ms) {
    buf_[n_++] = ms;
    if (n_ < kWindow) return;
    std::memcpy(scratch_, buf_, sizeof(scratch_));
    std::sort(scratch_, scratch_ + kWindow);
    const double v = scratch_[(kWindow * 95) / 100];
    p95_.store(v, std::memory_order_relaxed);
    n_ = 0;
    if (v > kJitterWarnMs && !warned_) {
        warned_ = true;
        warn_pending_ = true;
    }
}

bool JitterTracker::take_warning() {
    if (!warn_pending_) return false;
    warn_pending_ = false;
    return true;
}

// ---------------------------------------------------------------------------
// DspThread
// ---------------------------------------------------------------------------
// The owning session only frees a DspThread whose thread is gone (Session::destroy abandons
// it otherwise), so this join returns at once; waiting forever beats freeing under it.
DspThread::~DspThread() { join_thread(INFINITE); }

void DspThread::configure(IDspHost* host, const Config& cfg, NowFn fn, void* ctx) {
    host_ = host;
    cfg_ = cfg;
    now_fn_ = fn;
    now_ctx_ = ctx;
    timeline_.set_origin(cfg.session_origin_100ns);
    timeline_.set_base_duration(cfg.base_duration_sec);

    const bool include_mode = (cfg.others_mode == WF_OTHERS_PROCESS_INCLUDE);

    // Spec 3.4: the real capture path converts with the high quality resampler and
    // compensates its algorithmic delay. With WF_WITH_AEC3=ON that is WebRTC's
    // PushSincResampler, otherwise the linear fallback. Built once per (re)anchor here,
    // never on the per-frame path.
    const ResamplerFactory rf = production_resampler_factory();

    SourceState& mic = src_[static_cast<int>(Role::Mic)];
    mic.active = true;
    mic.kind = SourceKind::Me;
    mic.wf_source = WF_SOURCE_ME;
    mic.is_reference = false;
    mic.emits_others = false;
    mic.norm.configure(CaptureNormalizer::Config{SourceKind::Me, false}, &mic.gaps, rf);
    mic.gaps.reserve(64);

    SourceState& oth = src_[static_cast<int>(Role::Others)];
    oth.active = true;
    oth.kind = SourceKind::Others;
    oth.wf_source = WF_SOURCE_OTHERS;
    oth.emits_others = true;
    // In exclude and device-loopback mode the one Others stream is also the reference.
    oth.is_reference = !include_mode;
    oth.norm.configure(CaptureNormalizer::Config{SourceKind::Others, include_mode}, &oth.gaps, rf);
    oth.gaps.reserve(64);

    SourceState& ref = src_[static_cast<int>(Role::Reference)];
    ref.active = include_mode;
    ref.kind = SourceKind::Others;
    ref.wf_source = WF_SOURCE_REFERENCE;
    ref.is_reference = true;
    ref.emits_others = false;   // spec 3.2: the app-mode reference is never stored or sent
    ref.norm.configure(CaptureNormalizer::Config{SourceKind::Others, false}, &ref.gaps, rf);
    ref.gaps.reserve(64);

    EchoProcessor::Config ec;
    ec.ns_level = static_cast<int>(cfg.ns_level);
    ec.enable_aec = cfg.enable_aec;
    echo_gaps_.reserve(64);
    echo_.configure(ec, &echo_gaps_, cfg.session_origin_100ns);
    // The canceller reads the session clock through echo_clock(), which substitutes the
    // slice time while a backlog is worked off (see process_backlog()).
    echo_.set_clock(&DspThread::echo_clock, this);

    meter_me_.reset();
    meter_others_.reset();

    // Reserve every scratch buffer so the tick never allocates.
    norm_out_.reserve(static_cast<size_t>(k::kSampleRate) / 2);
    frames_.reserve(64);
    me_frames_.reserve(64);
    others_frames_.reserve(64);
}

void DspThread::request_mic_reset(bool format_change) {
    if (format_change) {
        mic_format_resets_.fetch_add(1, std::memory_order_release);
    } else {
        mic_device_resets_.fetch_add(1, std::memory_order_release);
    }
}

void DspThread::request_render_reset() { render_resets_.fetch_add(1, std::memory_order_release); }

void DspThread::pull_channel(Role r, int64_t now) {
    (void)now;
    SourceState& s = src_[static_cast<int>(r)];
    std::shared_ptr<StreamChannel> ch = host_ ? host_->dsp_channel(r) : nullptr;
    if (ch.get() == s.channel.get()) return;
    if (s.channel) {
        s.ring_overflows += s.channel->ring.overflows();
        const int64_t g = s.channel->arrival_gap_max_100ns.load(std::memory_order_relaxed);
        if (g > s.capture_gap_prev) s.capture_gap_prev = g;
    }
    s.channel = std::move(ch);
    if (!s.channel) return;
    s.attempt = s.channel->attempt_id;
    s.handover_gap = s.channel->handover_gap;
    // A restart keeps the meeting timeline: reset() forgets the anchor but not the
    // origin, so sample positions stay comparable across attempts.
    s.norm.reset();
    s.framer.reset();
    s.inbox.reset();
    s.have_prev_ts = false;
    s.rate = 0;
}

void DspThread::prepare_ring(SourceState& s, int role_index) {
    if (!s.channel) return;
    const uint32_t rate = s.channel->rate.load(std::memory_order_relaxed);
    if (rate != 0 && s.rate != rate) {
        s.rate = rate;
        s.inbox.configure(s.kind, rate, &s.gaps);
        s.have_prev_ts = false;
    }
    // The ring only grows between two ticks, so its size here is the high-water mark.
    const uint64_t used = s.channel->ring.size();
    const uint32_t slots = used > AudioRing::kSlots ? static_cast<uint32_t>(AudioRing::kSlots)
                                                    : static_cast<uint32_t>(used);
    std::atomic<uint32_t>& hw = metrics_.ring_high_water[role_index];
    if (slots > hw.load(std::memory_order_relaxed)) hw.store(slots, std::memory_order_relaxed);
}

int64_t DspThread::head_time(const SourceState& s, int64_t qpc, uint32_t flags) const {
    if ((flags & kBufTimestampError) && s.have_prev_ts) return s.prev_end_100ns;
    return qpc;
}

bool DspThread::slice_window(int64_t* window_end) {
    bool found = false;
    int64_t earliest = 0;
    for (int i = 0; i < kRoleCount; ++i) {
        SourceState& s = src_[i];
        if (!s.active || !s.channel || s.rate == 0) continue;
        int64_t qpc = 0;
        uint32_t frames = 0, flags = 0;
        if (!s.channel->ring.peek(&qpc, &frames, &flags)) continue;
        const int64_t t = head_time(s, qpc, flags);
        if (!found || t < earliest) earliest = t;
        found = true;
    }
    if (found) *window_end = earliest + kSliceTicks;
    return found;
}

bool DspThread::rings_pending() {
    for (int i = 0; i < kRoleCount; ++i) {
        SourceState& s = src_[i];
        if (!s.active || !s.channel || s.rate == 0) continue;
        if (s.channel->ring.peek(nullptr, nullptr, nullptr)) return true;
    }
    return false;
}

void DspThread::drain_ring(SourceState& s, int64_t window_end) {
    if (!s.channel || s.rate == 0) return;
    const uint32_t rate = s.rate;
    const uint64_t budget = static_cast<uint64_t>(rate) * kSliceTicks / k::kTicksPerSecond;
    uint64_t moved = 0;
    int64_t head_qpc = 0;
    uint32_t head_frames = 0, head_flags = 0;
    while (s.channel->ring.peek(&head_qpc, &head_frames, &head_flags)) {
        if (head_time(s, head_qpc, head_flags) >= window_end) break;
        if (moved > 0 && moved + head_frames > budget) break;
        if (!s.channel->ring.pop(slot_)) break;
        moved += slot_.frames;
        const int64_t raw = slot_.qpc_100ns;
        int64_t ts = raw;
        int64_t hole = 0;
        if (slot_.lost_frames > 0) {
            // The capture thread found the ring full and dropped these frames. Reported
            // here, once, at its true position; the normalizer is told that the hole in
            // front of this slot is accounted for, so it logs no discontinuity twin for it.
            const double lost_sec = static_cast<double>(slot_.lost_frames) / rate;
            s.gaps.add(s.kind, GapReason::InboxOverflow, slot_.lost_qpc_100ns, lost_sec);
            hole = static_cast<int64_t>(std::llround(lost_sec * k::kTicksPerSecond));
            ++ring_loss_gaps_;
        } else if (s.have_prev_ts) {
            // (A known loss is not clock jitter, and smoothing must not smear it.)
            const double jitter_ms =
                std::fabs(static_cast<double>(raw - s.prev_end_100ns)) / k::kTicksPerMs;
            s.jitter.add(jitter_ms);
            if (cfg_.timestamp_mode == WF_TIMESTAMP_SMOOTHED) {
                const double corr = kAnchorAlpha * static_cast<double>(raw - s.prev_end_100ns);
                ts = s.prev_end_100ns + static_cast<int64_t>(std::llround(corr));
            }
        }
        InputPacket p;
        p.data = slot_.data;
        p.frames = slot_.frames;
        p.rate = rate;
        p.qpc_100ns = ts;
        p.device_position = slot_.device_position;
        p.flags = slot_.flags;
        p.hole_reported_100ns = hole;
        // A slice is at most 100 ms (plus one slot when that slot alone is longer), far
        // below the 240 ms inbox, so this push never overflows.
        s.inbox.push(p);
        s.prev_end_100ns =
            ts + static_cast<int64_t>(std::llround(static_cast<double>(slot_.frames) *
                                                   k::kTicksPerSecond / rate));
        s.have_prev_ts = true;
    }
}

int64_t DspThread::echo_clock(void* self) {
    const DspThread* d = static_cast<const DspThread*>(self);
    if (d->echo_now_ != 0) return d->echo_now_;
    if (d->now_fn_) return d->now_fn_(d->now_ctx_);
    // No session clock configured: QPC in 100 ns units, the session clock's domain.
    LARGE_INTEGER c, f;
    QueryPerformanceCounter(&c);
    QueryPerformanceFrequency(&f);
    return static_cast<int64_t>(static_cast<double>(c.QuadPart) * k::kTicksPerSecond /
                                static_cast<double>(f.QuadPart));
}

// The slice loop (see dsp_thread.h). Gaps are staged per slice, so the gap logs never
// outgrow their reservation however long the backlog is.
size_t DspThread::process_backlog(int64_t now) {
    size_t out = 0;
    uint32_t slices = 0;
    while (slices < kMaxSlicesPerTick) {
        int64_t window_end = 0;
        if (!slice_window(&window_end)) break;
        for (Role r : kPipelineOrder) {
            SourceState& s = src_[static_cast<int>(r)];
            if (!s.active) continue;
            drain_ring(s, window_end);
            run_pipeline(s);
        }
        ++slices;
        const bool more = rings_pending();
        // Real-time equivalence: while more audio is queued, "now" for the canceller is the
        // end of what it has been given so far. A mic frame then waits for its reference,
        // and the reference ages, exactly as when the audio was live.
        echo_now_ = more ? (window_end < now ? window_end : now) : 0;
        echo_.process(me_frames_);
        echo_now_ = 0;
        const size_t emitted = emit_frames(now);
        out += emitted;
        me_frames_.clear();
        others_frames_.clear();
        for (int i = 0; i < kRoleCount; ++i) {
            if (src_[i].active) drain_gaps(src_[i]);
        }
        drain_echo_gaps();
        if (!more) break;
        // A long backlog: let the reader start on the frames before the tick is over.
        if (emitted > 0) host_->dsp_notify();
    }
    if (slices == 0) {
        // Nothing new: the canceller still runs, so waiting mic frames reach their deadline.
        echo_.process(me_frames_);
        out += emit_frames(now);
        me_frames_.clear();
        others_frames_.clear();
    }
    if (slices > metrics_.slices_max.load(std::memory_order_relaxed)) {
        metrics_.slices_max.store(slices, std::memory_order_relaxed);
    }
    return out;
}

void DspThread::run_pipeline(SourceState& s) {
    InputPacket p;
    while (s.inbox.pop(p)) {
        bool discontinuity = false;
        const int64_t pos = s.norm.process(p, norm_out_, &discontinuity);
        if (!s.framer_ready && s.norm.has_origin()) {
            // Each normalizer counts samples from its own first sample. Shifting that
            // onto the session grid is what makes "reference frame n" and "mic frame n"
            // the same 10 ms of wall time (spec 3.3 step 1: one common clock).
            s.pos_offset = static_cast<int64_t>(
                std::llround(static_cast<double>(s.norm.origin_100ns() -
                                                 cfg_.session_origin_100ns) /
                             k::kTicksPerSample));
            s.framer.configure(s.kind, cfg_.session_origin_100ns, &s.gaps);
            s.framer_ready = true;
        }
        if (!s.framer_ready || norm_out_.empty()) continue;
        frames_.clear();
        s.framer.push(pos + s.pos_offset, norm_out_.data(), norm_out_.size(), frames_);
        route_frames(s);
    }
}

// Hands the frames in frames_ to their consumer: the mic to the canceller, the reference to
// the canceller (which passes it through to Others when it is also the meeting audio), an
// include-mode app stream straight to Others.
void DspThread::route_frames(const SourceState& s) {
    for (const Frame160& f : frames_) {
        if (s.kind == SourceKind::Me) {
            echo_.push_capture(f);
        } else if (s.is_reference) {
            echo_.push_reference(f, s.emits_others ? &others_frames_ : nullptr);
        } else if (s.emits_others) {
            others_frames_.push_back(f);
        }
    }
}

const DspThread::SourceState& DspThread::reference_state() const {
    const SourceState& ref = src_[static_cast<int>(Role::Reference)];
    return ref.active ? ref : src_[static_cast<int>(Role::Others)];
}

void DspThread::publish_gap(const Gap& g, uint32_t wf_source, uint32_t reason, uint32_t attempt) {
    WfEvent e = make_event(WF_EVENT_GAP, wf_source, attempt, timeline_.offset_of(g.start_100ns));
    e.reason = reason;
    e.duration_sec = g.duration_sec;
    host_->dsp_publish(e);
}

// Gaps of one stream's inbox and normalizer, reported on that stream's own source. AP3: the
// first re-anchor gap of a planned hand-over (render switch, lost render endpoint) is the
// hand-over's one gap and carries its reason (the supervisor publishes none of its own).
void DspThread::drain_gaps(SourceState& s) {
    for (const Gap& g : s.gaps.items()) {
        uint32_t reason = abi_gap_reason(g.reason);
        if (s.handover_gap != WF_GAP_NONE && g.reason == GapReason::TimestampDiscontinuity) {
            reason = s.handover_gap;
            s.handover_gap = WF_GAP_NONE;
        }
        publish_gap(g, s.wf_source, reason, s.attempt);
    }
    s.gaps.clear();
}

// Gaps of the canceller. Its GapReason::InboxOverflow is the 49-frame cap of its frame
// stores - the Mac's "Processing buffer overflowed", not the CaptureInbox's "Input buffer
// overflowed" - so it goes out as WF_GAP_PROCESSING_OVERFLOW. Its Others-tagged gaps can
// only concern the echo reference (the canceller holds no other Others audio, and the
// meeting frames were passed through before the store), so they are reported on
// WF_SOURCE_REFERENCE, the Mac's .reference, never as a hole in the Others track.
void DspThread::drain_echo_gaps() {
    const SourceState& mic = src_[static_cast<int>(Role::Mic)];
    const SourceState& ref = reference_state();
    for (const Gap& g : echo_gaps_.items()) {
        const bool me = (g.source == SourceKind::Me);
        const uint32_t reason = (g.reason == GapReason::InboxOverflow)
                                    ? WF_GAP_PROCESSING_OVERFLOW
                                    : abi_gap_reason(g.reason);
        publish_gap(g, me ? WF_SOURCE_ME : WF_SOURCE_REFERENCE, reason,
                    me ? mic.attempt : ref.attempt);
    }
    echo_gaps_.clear();
}

// One Frame160 -> one WfFrame. Only [valid_lo, valid_hi) is copied and the range travels
// along; the rest of samples[] stays 0.0f, so a partial head or tail is never mistaken for
// captured silence (Mac emit(samples, range:)). A frame without a carrying sample is not
// queued. The meter sees the carrying samples only, as on the Mac.
void DspThread::emit_frame(const Frame160& f, uint32_t wf_source, uint32_t attempt,
                           LevelMeter& meter, std::atomic<uint64_t>& counter, int64_t now) {
    const int lo = f.valid_lo;
    const int hi = f.valid_hi < k::kFrameSamples ? f.valid_hi : k::kFrameSamples;
    if (hi <= lo) return;
    const size_t n = static_cast<size_t>(hi - lo);

    WfFrame o;
    std::memset(&o, 0, sizeof(o));
    o.source = wf_source;
    o.flags = f.flags;
    o.attempt_id = attempt;
    o.valid_lo = static_cast<uint16_t>(lo);
    o.valid_hi = static_cast<uint16_t>(hi);
    o.timeline_sample = f.index * k::kFrameSamples;
    // Both sources sit on the session grid, so the meeting offset of sample position p is
    // simply p / 16000 + base_duration (spec 3.6).
    o.offset_sec = timeline_.offset_of_position(o.timeline_sample, cfg_.session_origin_100ns);
    std::memcpy(o.samples + lo, f.s + lo, n * sizeof(float));
    if (host_->dsp_push_frame(o)) counter.fetch_add(1, std::memory_order_relaxed);
    meter.push(f.s + lo, n, now);
}

size_t DspThread::emit_frames(int64_t now) {
    const SourceState& mic = src_[static_cast<int>(Role::Mic)];
    const SourceState& oth = src_[static_cast<int>(Role::Others)];
    for (const Frame160& f : me_frames_) {
        emit_frame(f, WF_SOURCE_ME, mic.attempt, meter_me_, metrics_.frames_me, now);
    }
    for (const Frame160& f : others_frames_) {
        emit_frame(f, WF_SOURCE_OTHERS, oth.attempt, meter_others_, metrics_.frames_others, now);
    }
    return me_frames_.size() + others_frames_.size();
}

void DspThread::publish_metrics() {
    double erle = 0.0;
    int delay = 0;
    echo_.metrics(&erle, &delay);
    metrics_.erle_db.store(erle, std::memory_order_relaxed);
    metrics_.delay_ms.store(delay, std::memory_order_relaxed);

    const EchoProcessor::Stats& es = echo_.stats();
    metrics_.unfiltered_frames.store(es.unfiltered_frames, std::memory_order_relaxed);
    metrics_.late_dropped.store(es.late_dropped, std::memory_order_relaxed);
    metrics_.ref_discarded.store(es.ref_discarded, std::memory_order_relaxed);
    metrics_.aec_resets.store(es.resets, std::memory_order_relaxed);
    metrics_.aec_render_resets.store(es.render_resets, std::memory_order_relaxed);

    uint64_t overflows = 0, ts_errors = 0, discontinuities = 0, ring_overflows = 0;
    for (int i = 0; i < kRoleCount; ++i) {
        const SourceState& s = src_[i];
        if (!s.active) continue;
        overflows += s.inbox.stats().overflow_events;
        ts_errors += s.norm.stats().timestamp_errors;
        discontinuities += s.norm.stats().discontinuities;
        ring_overflows += s.ring_overflows;
        if (s.channel) ring_overflows += s.channel->ring.overflows();
    }
    metrics_.inbox_overflows.store(overflows, std::memory_order_relaxed);
    metrics_.timestamp_errors.store(ts_errors, std::memory_order_relaxed);
    metrics_.discontinuities.store(discontinuities, std::memory_order_relaxed);
    metrics_.ring_overflows.store(ring_overflows, std::memory_order_relaxed);
    metrics_.ring_loss_gaps.store(ring_loss_gaps_, std::memory_order_relaxed);
    for (int i = 0; i < kRoleCount; ++i) {
        const SourceState& s = src_[i];
        int64_t g = s.capture_gap_prev;
        if (s.channel) {
            const int64_t c = s.channel->arrival_gap_max_100ns.load(std::memory_order_relaxed);
            if (c > g) g = c;
        }
        metrics_.capture_gap_max_100ns[i].store(g, std::memory_order_relaxed);
    }

    metrics_.drift_ppm_me.store(src_[static_cast<int>(Role::Mic)].norm.stats().drift_ppm,
                                std::memory_order_relaxed);
    metrics_.drift_ppm_others.store(src_[static_cast<int>(Role::Others)].norm.stats().drift_ppm,
                                    std::memory_order_relaxed);
    metrics_.jitter_p95_ms_me.store(src_[static_cast<int>(Role::Mic)].jitter.p95(),
                                    std::memory_order_relaxed);
    metrics_.jitter_p95_ms_others.store(src_[static_cast<int>(Role::Others)].jitter.p95(),
                                        std::memory_order_relaxed);
}

void DspThread::tick(int64_t now) {
    if (!host_) return;
    // Supervisor events first, so a Restarting always precedes the gaps of that restart.
    host_->dsp_forward_supervisor_events();

    // WfDiagnostics.dsp_tick_gap_max_ms: the longest this thread went without a tick.
    if (last_tick_ != 0 &&
        now - last_tick_ > metrics_.tick_gap_max_100ns.load(std::memory_order_relaxed)) {
        metrics_.tick_gap_max_100ns.store(now - last_tick_, std::memory_order_relaxed);
    }
    last_tick_ = now;

    me_frames_.clear();
    others_frames_.clear();
    size_t frames_out = 0;

    // Spec 3.3 step 5: reset the filter only on mic device or mic format change, and do
    // it from this thread, which is the only one that touches the bridge.
    const uint32_t dev = mic_device_resets_.load(std::memory_order_acquire);
    while (consumed_device_resets_ != dev) {
        ++consumed_device_resets_;
        echo_.on_mic_device_changed();
    }
    const uint32_t fmt = mic_format_resets_.load(std::memory_order_acquire);
    while (consumed_format_resets_ != fmt) {
        ++consumed_format_resets_;
        // Mac append(): flushEchoTail() before the reset, so the audio still inside the
        // canceller is emitted instead of becoming a gap.
        echo_.on_mic_format_changed(&me_frames_);
    }
    // AP3: the echo path moved to another render endpoint. Same place and the same tail rule
    // as a format change (the mic is unchanged, its delayed audio is emitted); the old
    // endpoint's buffered reference is dropped and the filter starts over.
    const uint32_t rnd = render_resets_.load(std::memory_order_acquire);
    while (consumed_render_resets_ != rnd) {
        ++consumed_render_resets_;
        echo_.on_render_changed(&me_frames_);
    }
    // That tail belongs to the attempt that captured it, so it leaves before pull_channel()
    // moves the mic on to its next attempt.
    frames_out += emit_frames(now);
    me_frames_.clear();

    for (int i = 0; i < kRoleCount; ++i) {
        if (!src_[i].active) continue;
        pull_channel(static_cast<Role>(i), now);
        prepare_ring(src_[i], i);
    }
    frames_out += process_backlog(now);

    for (int i = 0; i < kRoleCount; ++i) {
        SourceState& s = src_[i];
        if (!s.active) continue;
        drain_gaps(s);
        if (s.jitter.take_warning()) {
            WfEvent e = make_event(WF_EVENT_CLOCK_JITTER_WARNING, s.wf_source, s.attempt,
                                   timeline_.offset_of(now));
            e.duration_sec = s.jitter.p95() / 1000.0;
            host_->dsp_publish(e);
        }
    }
    drain_echo_gaps();

    // Levels (spec 3.6): the meters decay to 0 after 0.35 s without a value.
    level_me_.store(meter_me_.value(now), std::memory_order_relaxed);
    level_others_.store(meter_others_.value(now), std::memory_order_relaxed);
    for (int i = 0; i < 2; ++i) {
        const SourceState& s = src_[i];
        const int64_t last = s.channel ? s.channel->last_packet_100ns.load(std::memory_order_acquire)
                                       : 0;
        const uint32_t live = (last != 0 && now - last < k::kLevelDecayTicks) ? 1u : 0u;
        if (i == 0) me_active_.store(live, std::memory_order_relaxed);
        else others_active_.store(live, std::memory_order_relaxed);
    }

    publish_metrics();
    // The Mac drain(): everything this tick staged reaches the public queue in one go.
    const size_t events = host_->dsp_commit_events();
    if (events > 0 || frames_out > 0) host_->dsp_notify();
}

void DspThread::flush_stop(int64_t now) {
    if (!host_) return;
    tick(now);                       // last drain of everything still in the rings
    me_frames_.clear();
    others_frames_.clear();
    // EchoAudioProcessor.finish: every stream's trailing partial frame goes in BEFORE the
    // canceller drains, so the last < 160 samples reach Others and the mic tail reaches
    // the canceller instead of being dropped with the framer.
    for (Role r : kPipelineOrder) {
        SourceState& s = src_[static_cast<int>(r)];
        if (!s.active || !s.framer_ready) continue;
        frames_.clear();
        s.framer.finish(frames_);
        route_frames(s);
    }
    // Spec 3.3 step 6: drain without waiting, then feed zero frames until the latency tail
    // (224 samples, 128 with NS off, none with the canceller off) is out. The zero frames
    // carry an empty valid range, so only the real cleaned tail is emitted.
    echo_.stop(me_frames_);
    emit_frames(now);
    for (int i = 0; i < kRoleCount; ++i) {
        SourceState& s = src_[i];
        if (!s.active) continue;
        // A loss no later slot carried (the stream stopped while its ring was full).
        int64_t lost_at = 0;
        uint64_t lost = 0;
        if (s.channel && s.rate != 0 && s.channel->ring.pending_loss(&lost_at, &lost)) {
            s.gaps.add(s.kind, GapReason::InboxOverflow, lost_at,
                       static_cast<double>(lost) / s.rate);
            ++ring_loss_gaps_;
        }
        drain_gaps(s);
    }
    drain_echo_gaps();
    publish_metrics();
    // Every frame of the session is queued by now; Stopped goes last.
    host_->dsp_stopped(now);
    host_->dsp_commit_events();
    host_->dsp_notify();
}

void DspThread::levels(WfLevels* out, int64_t now) const {
    if (!out) return;
    (void)now;
    out->me = level_me_.load(std::memory_order_relaxed);
    out->others = level_others_.load(std::memory_order_relaxed);
    out->me_active = me_active_.load(std::memory_order_relaxed);
    out->others_active = others_active_.load(std::memory_order_relaxed);
}

// ---------------------------------------------------------------------------
// Thread
// ---------------------------------------------------------------------------
DWORD WINAPI DspThread::thread_main(void* self) {
    static_cast<DspThread*>(self)->run();
    return 0;
}

bool DspThread::enter_producer() {
    uint32_t expected = kProducerIdle;
    return producer_.compare_exchange_strong(expected, kProducerBusy, std::memory_order_acq_rel,
                                             std::memory_order_acquire);
}

void DspThread::run() {
    DWORD task_index = 0;
    HANDLE mmcss = AvSetMmThreadCharacteristicsW(L"Pro Audio", &task_index);
    for (;;) {
        // exit_ first: WaitForMultipleObjects reports the lowest signalled index.
        HANDLE waits[3] = {exit_, finish_, timer_};
        const DWORD r = WaitForMultipleObjects(3, waits, FALSE, 50);
        if (r == WAIT_OBJECT_0) break;
        // Fails only when a stopping thread gave up on us and took the producer role over:
        // from then on this thread must not touch a queue, so it just leaves.
        if (!enter_producer()) break;
        if (r == WAIT_OBJECT_0 + 1) {
            flush_stop(now());
            producer_.store(kProducerFlushed, std::memory_order_release);
            break;
        }
        tick(now());
        producer_.store(kProducerIdle, std::memory_order_release);
    }
    if (mmcss) AvRevertMmThreadCharacteristics(mmcss);
}

bool DspThread::start_thread() {
    if (thread_) return true;
    exit_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    finish_ = CreateEventW(nullptr, TRUE, FALSE, nullptr);
    timer_ = CreateWaitableTimerExW(nullptr, nullptr, CREATE_WAITABLE_TIMER_HIGH_RESOLUTION,
                                    TIMER_ALL_ACCESS);
    if (!timer_) timer_ = CreateWaitableTimerExW(nullptr, nullptr, 0, TIMER_ALL_ACCESS);
    LARGE_INTEGER due;
    due.QuadPart = -10 * k::kTicksPerMs;   // first shot in 10 ms
    if (!exit_ || !finish_ || !timer_ ||
        !SetWaitableTimer(timer_, &due, 10, nullptr, nullptr, FALSE)) {
        close_thread_handles();
        return false;
    }
    producer_.store(kProducerIdle, std::memory_order_relaxed);
    thread_ = CreateThread(nullptr, 0, &DspThread::thread_main, this, 0, nullptr);
    if (!thread_) {
        close_thread_handles();
        return false;
    }
    return true;
}

bool DspThread::finish_thread(uint32_t timeout_ms) {
    if (!thread_ || !finish_) return false;
    SetEvent(finish_);
    WaitForSingleObject(thread_, timeout_ms);
    // Also true when the wait ran out a moment after the flush completed.
    return producer_.load(std::memory_order_acquire) == kProducerFlushed;
}

bool DspThread::take_producer() {
    uint32_t expected = kProducerIdle;
    return producer_.compare_exchange_strong(expected, kProducerTaken, std::memory_order_acq_rel,
                                             std::memory_order_acquire);
}

bool DspThread::join_thread(uint32_t timeout_ms) {
    if (thread_) {
        SetEvent(exit_);
        if (WaitForSingleObject(thread_, timeout_ms) != WAIT_OBJECT_0) return false;
        CloseHandle(thread_);
        thread_ = nullptr;
    }
    close_thread_handles();
    return true;
}

// Only called while no thread runs (failed start, or after a successful join).
void DspThread::close_thread_handles() {
    if (timer_) {
        CancelWaitableTimer(timer_);
        CloseHandle(timer_);
        timer_ = nullptr;
    }
    if (finish_) {
        CloseHandle(finish_);
        finish_ = nullptr;
    }
    if (exit_) {
        CloseHandle(exit_);
        exit_ = nullptr;
    }
}

}  // namespace wf
