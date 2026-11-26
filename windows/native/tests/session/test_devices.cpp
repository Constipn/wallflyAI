// AP3 (2026-11-23): device changes - headset <-> loudspeakers - in virtual time.
//
// The fake plant simulates four render and four capture endpoints with a default per role;
// the WF_FAKE_SIGNAL_* endpoint signals change that model and deliver the
// IMMNotificationClient callbacks a real system sends, through the session's production
// filter, and wf_fake_query tells which endpoint the device loopback, the keep-alive and the
// mic actually run on.
//
//   H1  a default-render change moves the device loopback and the keep-alive (one gap,
//       canceller reset, DEVICE_SWITCHED "render:<name>"), A->B->A collapses, five switches
//       never exhaust the Others restart budget;
//       an invalidated loopback takes the same path;
//   a pinned render endpoint ignores default changes, falls back when it is lost, and does
//       not hop back when it returns;
//   in the process-loopback modes only the keep-alive moves;
//   H2  an unconfigured mic follows the communications default (console without one; one
//       rebuild for a console + communications pair); a configured mic is unaffected;
//   keep-alive failures are reported (WF_EVENT_WARNING) and retried 0.5 / 1 / 2 s.
#include <cwchar>
#include <string>
#include <vector>

#include "test_util.h"

using harness::Fake;

namespace {

constexpr uint32_t kRate = 16000;
constexpr int kBlock = 160;
constexpr int64_t kBlockTicks = 100000;   // 10 ms
constexpr int32_t kNotFound = static_cast<int32_t>(0x80070490);
constexpr int32_t kGenericFail = static_cast<int32_t>(0x80004005);
constexpr uint32_t kStateActive = 0x1;      // DEVICE_STATE_ACTIVE
constexpr uint32_t kStateUnplugged = 0x8;   // DEVICE_STATE_UNPLUGGED

const wchar_t kRender0[] = L"{0.0.0.00000000}.{fake-default-render}";
const wchar_t kRender1[] = L"{0.0.0.00000000}.{fake-render-1}";
const wchar_t kRender2[] = L"{0.0.0.00000000}.{fake-render-2}";
const wchar_t kCapture0[] = L"{0.0.1.00000000}.{fake-default-capture}";
const wchar_t kCapture1[] = L"{0.0.1.00000000}.{fake-capture-1}";
const wchar_t kCapture2[] = L"{0.0.1.00000000}.{fake-capture-2}";
const wchar_t kCapture3[] = L"{0.0.1.00000000}.{fake-capture-3}";

// One 10 ms step: the selected sources deliver block `n`, the clock moves on, one tick.
void step(Fake& f, int64_t& n, bool mic = true, bool others = true) {
    float buf[kBlock] = {};
    for (int i = 0; i < kBlock; ++i) buf[i] = 0.01f;
    if (mic) f.push(WF_FAKE_SRC_MIC, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
    if (others) f.push(WF_FAKE_SRC_OTHERS, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
    ++n;
    f.set_now(f.t0 + n * kBlockTicks);
    f.tick();
}

void run(Fake& f, int64_t& n, int ms, bool others = true) {
    for (int i = 0; i < ms / 10; ++i) step(f, n, true, others);
}

int count_from(const Fake& f, size_t from, uint32_t kind, uint32_t source) {
    int c = 0;
    for (size_t i = from; i < f.events.size(); ++i) {
        if (f.events[i].kind == kind && f.events[i].source == source) ++c;
    }
    return c;
}

std::vector<WfEvent> gaps_from(const Fake& f, size_t from) {
    std::vector<WfEvent> out;
    for (size_t i = from; i < f.events.size(); ++i) {
        if (f.events[i].kind == WF_EVENT_GAP) out.push_back(f.events[i]);
    }
    return out;
}

const WfEvent* last_of(const Fake& f, uint32_t kind, uint32_t source) {
    const WfEvent* e = nullptr;
    for (const WfEvent& x : f.events) {
        if (x.kind == kind && x.source == source) e = &x;
    }
    return e;
}

bool detail_is(const WfEvent* e, const wchar_t* text) {
    return e != nullptr && std::wcscmp(e->detail, text) == 0;
}

uint32_t counter(Fake& f, uint32_t what) {
    uint32_t v = 0;
    f.query(what, &v);
    return v;
}

int32_t render_state(int index, uint32_t state) {
    return static_cast<int32_t>((state << 8) | static_cast<uint32_t>(index));
}

// Steps until the supervisor carried out the pending switch (DEVICE_SWITCHED or DEVICE_LOST
// on `source` appears), Others delivering, and returns how many steps that took.
int until_event(Fake& f, int64_t& n, uint32_t kind, uint32_t source, int max_steps = 60) {
    const int before = f.count(kind, source);
    for (int i = 1; i <= max_steps; ++i) {
        step(f, n);
        if (f.count(kind, source) > before) return i;
    }
    return -1;
}

// ---------------------------------------------------------------------------
// H1
// ---------------------------------------------------------------------------
void test_default_render_change_reopens_loopback_and_keepalive() {
    Fake f;
    CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK));
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    run(f, n, 500);
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
    CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_OTHERS), L"render:Fake Speakers 0"));
    CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_ME), L"mic:Fake Microphone 0"));
    CHECK(f.diagnostics().keepalive_starts == 1);
    CHECK(f.metrics().aec_resets == 0);

    // Headset (0) -> loudspeakers (1): console + multimedia pair, debounced 200 ms.
    const size_t ev0 = f.events.size();
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 1) == WF_OK);
    run(f, n, 150);
    CHECK(f.count(WF_EVENT_DEVICE_SWITCHED) == 0);          // still inside the window
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);
    const int steps = until_event(f, n, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS);
    CHECK(steps >= 1 && steps <= 6);                          // 200 ms after the change
    // The new loopback's first packet arrives 60 ms after its open (an endpoint onset).
    for (int i = 0; i < 6; ++i) step(f, n, true, false);
    run(f, n, 500);

    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender1);
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender1);
    CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 2);         // exactly one rebuild
    CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS),
                    L"render:Fake Speakers 1"));
    CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_OTHERS),
                    L"render:Fake Speakers 1"));
    CHECK(count_from(f, ev0, WF_EVENT_RESTARTING, WF_SOURCE_OTHERS) == 1);
    CHECK(count_from(f, ev0, WF_EVENT_RESTARTING, WF_SOURCE_ME) == 0);   // the mic is untouched
    // Exactly one gap for the hand-over, on Others, WF_GAP_RESTART, with its true length:
    // the block that was in the old ring at the switch plus the 60 ms onset.
    const std::vector<WfEvent> gaps = gaps_from(f, ev0);
    CHECK(gaps.size() == 1);
    double gap_sec = -1.0;
    if (gaps.size() == 1) {
        CHECK(gaps[0].source == WF_SOURCE_OTHERS && gaps[0].reason == WF_GAP_RESTART);
        gap_sec = gaps[0].duration_sec;
        CHECK_NEAR(gap_sec, 0.070, 1e-6);
        CHECK(gap_sec <= 1.0);
    }
    const WfMetrics m = f.metrics();
    const WfDiagnostics d = f.diagnostics();
    CHECK(m.aec_resets == 1);                                 // the canceller starts over
    CHECK(d.aec_render_resets == 1);
    CHECK(d.render_switches == 1);
    CHECK(d.keepalive_starts == 2);
    CHECK(d.keepalive_failures == 0);
    CHECK(m.state == WF_STATE_RUNNING);
    CHECK(f.count(WF_EVENT_OTHERS_FAILED) == 0);
    CHECK(f.reasons_ok());

    // A -> B -> A inside the debounce window (here: B -> A -> B, B is where we run): the
    // decision is taken once, on the last default, which is the current endpoint - nothing.
    const size_t ev1 = f.events.size();
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 0) == WF_OK);
    run(f, n, 50);
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 1) == WF_OK);
    run(f, n, 500);
    CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 2);
    CHECK(count_from(f, ev1, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS) == 0);
    CHECK(gaps_from(f, ev1).empty());
    // B -> A -> C inside the window: one rebuild, straight to C.
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 0) == WF_OK);
    run(f, n, 50);
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 2) == WF_OK);
    run(f, n, 500);
    CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 3);
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender2);
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender2);
    CHECK(count_from(f, ev1, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS) == 1);
    // The communications role alone is not followed.
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_RENDER_CHANGED, 0, 1) == WF_OK);
    run(f, n, 400);
    CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 3);

    // Five switches in a row, 300 ms apart, while every open of the new loopback FAILS: a
    // routine switch never spends the 0.5/1/2 s budget that ends in OthersFailed.
    CHECK(f.signal(WF_FAKE_SIGNAL_START_FAILS, WF_FAKE_SRC_OTHERS, kGenericFail) == WF_OK);
    for (int i = 0; i < 5; ++i) {
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, (i % 2) ? 1 : 0) == WF_OK);
        run(f, n, 300, false);
    }
    CHECK(f.count(WF_EVENT_OTHERS_FAILED) == 0);
    CHECK(f.metrics().state != WF_STATE_OTHERS_FAILED);
    CHECK(f.signal(WF_FAKE_SIGNAL_CLEAR, WF_FAKE_SRC_OTHERS) == WF_OK);
    run(f, n, 1500);
    CHECK(f.count(WF_EVENT_OTHERS_FAILED) == 0);
    CHECK(f.metrics().state == WF_STATE_RUNNING);
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);      // the last default of the storm
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
    // ... and five successful switches (no failures) the same.
    for (int i = 0; i < 5; ++i) {
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, (i % 2) ? 0 : 1) == WF_OK);
        run(f, n, 300);
    }
    CHECK(f.count(WF_EVENT_OTHERS_FAILED) == 0);
    CHECK(f.metrics().state == WF_STATE_RUNNING);
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender1);
    CHECK(f.reasons_ok());
    CHECK(f.stop() == WF_OK);
    std::printf("  render switch: rebuild after %d steps, 1 gap %.3f s (Restart), "
                "%u switches, %u aec render resets, %u keep-alive starts\n",
                steps, gap_sec, f.diagnostics().render_switches,
                f.diagnostics().aec_render_resets, f.diagnostics().keepalive_starts);
}

// AUDCLNT_E_DEVICE_INVALIDATED on the loopback (the default endpoint was unplugged): the same
// path at once - re-resolve, one DEVICE_LOST gap, canceller reset - and the default change
// that follows does not rebuild a second time.
void test_invalidated_loopback_takes_the_render_path() {
    Fake f;
    CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK));
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    run(f, n, 300);
    const size_t ev0 = f.events.size();
    // Unplug render 0: its state changes, the defaults move to 1, the stream is invalidated.
    CHECK(f.signal(WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE, 0, render_state(0, kStateUnplugged)) == WF_OK);
    CHECK(f.signal(WF_FAKE_SIGNAL_DEVICE_LOST, WF_FAKE_SRC_OTHERS) == WF_OK);
    step(f, n, true, false);
    for (int i = 0; i < 4; ++i) step(f, n, true, false);
    run(f, n, 600);
    CHECK(count_from(f, ev0, WF_EVENT_DEVICE_LOST, WF_SOURCE_OTHERS) == 1);
    CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_LOST, WF_SOURCE_OTHERS), L"render:Fake Speakers 0"));
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender1);
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender1);
    CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 2);         // one rebuild, not two
    CHECK(count_from(f, ev0, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS) == 0);
    const std::vector<WfEvent> gaps = gaps_from(f, ev0);
    CHECK(gaps.size() == 1 && gaps[0].reason == WF_GAP_DEVICE_LOST &&
          gaps[0].source == WF_SOURCE_OTHERS && gaps[0].duration_sec > 0.0 &&
          gaps[0].duration_sec <= 1.0);
    CHECK(f.diagnostics().aec_render_resets == 1);
    CHECK(f.diagnostics().render_switches == 1);
    CHECK(f.metrics().state == WF_STATE_RUNNING);
    CHECK(f.reasons_ok());
    std::printf("  invalidated loopback: 1 rebuild onto the new default, 1 DeviceLost gap %.3f s\n",
                gaps.empty() ? -1.0 : gaps[0].duration_sec);
}

// ---------------------------------------------------------------------------
// Pinned render endpoint
// ---------------------------------------------------------------------------
void test_pinned_render_endpoint_ignores_default_changes() {
    Fake f;
    CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK, 0.0, 0, 1, nullptr, kRender1));
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    run(f, n, 300);
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender1);
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender1);
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 2) == WF_OK);
    run(f, n, 600);
    CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 0) == WF_OK);
    run(f, n, 600);
    CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender1);
    CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender1);
    CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 1);
    CHECK(f.count(WF_EVENT_DEVICE_SWITCHED) == 0);
    CHECK(f.count(WF_EVENT_GAP) == 0);
    CHECK(f.metrics().aec_resets == 0);
    CHECK(f.diagnostics().render_switches == 0);
    std::printf("  pinned render: 2 default changes ignored\n");
}

void test_pinned_render_endpoint_falls_back_when_removed() {
    {
        // Unplugged, with the stream invalidated as on hardware.
        Fake f;
        CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK, 0.0, 0, 1, nullptr, kRender1));
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_OTHERS),
                        L"render:Fake Speakers 1"));
        const size_t ev0 = f.events.size();
        CHECK(f.signal(WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE, 0, render_state(1, kStateUnplugged)) == WF_OK);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEVICE_LOST, WF_FAKE_SRC_OTHERS) == WF_OK);
        for (int i = 0; i < 5; ++i) step(f, n, true, false);
        run(f, n, 600);
        CHECK(count_from(f, ev0, WF_EVENT_DEVICE_LOST, WF_SOURCE_OTHERS) == 1);
        CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_LOST, WF_SOURCE_OTHERS),
                        L"render:Fake Speakers 1"));
        const WfEvent* acq = last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_OTHERS);
        CHECK(detail_is(acq, L"fallback:selected output missing|render:Fake Speakers 0"));
        CHECK(acq != nullptr && acq->hresult == kNotFound);
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
        CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 2);
        const std::vector<WfEvent> gaps = gaps_from(f, ev0);
        CHECK(gaps.size() == 1 && gaps[0].reason == WF_GAP_DEVICE_LOST);
        CHECK(f.diagnostics().aec_render_resets == 1);
        CHECK(f.diagnostics().render_switches == 1);

        // The pinned endpoint comes back: no hop back mid-recording (the app decides).
        CHECK(f.signal(WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE, 0, render_state(1, kStateActive)) == WF_OK);
        run(f, n, 600);
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);
        CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 2);
        // From the fallback on the session follows the default like an unpinned one.
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 2) == WF_OK);
        run(f, n, 600);
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender2);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender2);
        CHECK(f.count(WF_EVENT_OTHERS_FAILED) == 0);
        CHECK(f.metrics().state == WF_STATE_RUNNING);
        CHECK(f.reasons_ok());
    }
    {
        // Disabled without a stream error: the 200 ms timer rebuilds once.
        Fake f;
        CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK, 0.0, 0, 1, nullptr, kRender1));
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        const size_t ev0 = f.events.size();
        CHECK(f.signal(WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE, 0, render_state(1, 0x2)) == WF_OK);
        run(f, n, 150);
        CHECK(count_from(f, ev0, WF_EVENT_DEVICE_LOST, WF_SOURCE_OTHERS) == 0);
        run(f, n, 600);
        CHECK(count_from(f, ev0, WF_EVENT_DEVICE_LOST, WF_SOURCE_OTHERS) == 1);
        CHECK(counter(f, WF_FAKE_QUERY_LOOPBACK) == 2);
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);
        CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_OTHERS),
                        L"fallback:selected output missing|render:Fake Speakers 0"));
        CHECK(gaps_from(f, ev0).size() == 1);
    }
    {
        // Missing already at the start: the first attempt falls back.
        Fake f;
        CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK, 0.0, 0, 1, nullptr,
                       L"{0.0.0.00000000}.{gone-headset}"));
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        const WfEvent* acq = last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_OTHERS);
        CHECK(acq != nullptr && acq->attempt_id == 1 &&
              detail_is(acq, L"fallback:selected output missing|render:Fake Speakers 0"));
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK) == kRender0);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
        CHECK(f.diagnostics().keepalive_failures == 0);
        CHECK(f.metrics().state == WF_STATE_RUNNING);
    }
    std::printf("  pinned render: lost -> 1 rebuild onto the default (fallback detail), "
                "no hop back, follows the default afterwards\n");
}

// ---------------------------------------------------------------------------
// Process-loopback modes: only the keep-alive (and the echo path) moves.
// ---------------------------------------------------------------------------
void test_keepalive_moves_with_the_default_endpoint_in_process_loopback_modes() {
    const uint32_t modes[2] = {WF_OTHERS_SYSTEM_EXCLUDE, WF_OTHERS_PROCESS_INCLUDE};
    for (uint32_t mode : modes) {
        Fake f;
        CHECK(f.create(mode, 0.0, mode == WF_OTHERS_PROCESS_INCLUDE ? 4242u : 0u));
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        for (int i = 0; i < 30; ++i) {
            float buf[kBlock] = {};
            if (mode == WF_OTHERS_PROCESS_INCLUDE) {
                f.push(WF_FAKE_SRC_REFERENCE, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
            }
            step(f, n);
        }
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
        CHECK(f.query(WF_FAKE_QUERY_LOOPBACK).empty());       // no device loopback here
        const WfMetrics before = f.metrics();
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 1) == WF_OK);
        for (int i = 0; i < 50; ++i) {
            float buf[kBlock] = {};
            if (mode == WF_OTHERS_PROCESS_INCLUDE) {
                f.push(WF_FAKE_SRC_REFERENCE, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
            }
            step(f, n);
        }
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender1);
        const WfMetrics after = f.metrics();
        CHECK(after.restarts == before.restarts);             // no stream was rebuilt
        CHECK(f.count(WF_EVENT_RESTARTING) == 0);
        CHECK(f.count(WF_EVENT_GAP) == 0);
        CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_OTHERS) == 0);
        CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_REFERENCE),
                        L"render:Fake Speakers 1"));
        const WfDiagnostics d = f.diagnostics();
        CHECK(d.keepalive_starts == 2);
        CHECK(d.render_switches == 1);
        CHECK(d.aec_render_resets == 1);                      // the echo path moved
        CHECK(after.aec_resets == 1);
        CHECK(after.state == WF_STATE_RUNNING);
        CHECK(f.stop() == WF_OK);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE).empty());      // stopped with the session
    }
    std::printf("  process loopback modes: keep-alive moved, 0 restarts, 0 gaps\n");
}

// ---------------------------------------------------------------------------
// H2: microphone default role
// ---------------------------------------------------------------------------
void test_mic_follows_the_communications_default() {
    {
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture0);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 1);

        // Only the communications default moves (a headset set as "default communication
        // device"): the mic follows.
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED, 0, 1) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture1);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 2);
        CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME), L"mic:Fake Microphone 1"));
        CHECK(detail_is(last_of(f, WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_ME), L"mic:Fake Microphone 1"));
        CHECK(f.metrics().aec_resets == 1);                   // a mic device change resets

        // Only the console default moves: not the role the mic runs on.
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 2) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture1);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 2);

        // Both roles to the same endpoint at once: one rebuild, not two.
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 3) == WF_OK);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED, 0, 3) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture3);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 3);
        CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME) == 2);

        // No communications default any more: the console default (2), and from then on the
        // mic follows the console role ...
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 2) == WF_OK);
        run(f, n, 300);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 3);            // console change: still ignored
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED, 0, -1) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture2);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 4);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 0) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture0);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 5);
        // ... until a communications default exists again.
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED, 0, 1) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture1);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 6);
        CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME) == 5);
        CHECK(f.metrics().state == WF_STATE_RUNNING);
        CHECK(f.count(WF_EVENT_GAP, WF_SOURCE_OTHERS) == 0);   // Others never noticed
        CHECK(f.reasons_ok());
    }
    {
        // A configured mic is unaffected by default changes of either role.
        static const wchar_t kConfigured[] = L"{0.0.1.00000000}.{configured-usb}";
        Fake f;
        CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, 0.0, 0, 1, kConfigured));
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kConfigured);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED, 0, 1) == WF_OK);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 2) == WF_OK);
        run(f, n, 500);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kConfigured);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 1);
        CHECK(f.count(WF_EVENT_DEVICE_SWITCHED) == 0);
    }
    {
        // No communications default at the start: the console default, and console changes
        // are followed.
        Fake f;
        CHECK(f.create());
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED, 0, -1) == WF_OK);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 2) == WF_OK);
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture2);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED, 0, 1) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_MIC) == kCapture1);
        CHECK(counter(f, WF_FAKE_QUERY_MIC) == 2);
    }
    std::printf("  mic: follows the communications default (console without one), "
                "1 rebuild per role pair, configured mic unaffected\n");
}

// ---------------------------------------------------------------------------
// Keep-alive failures: visible, retried, never fatal.
// ---------------------------------------------------------------------------
void test_keepalive_failure_is_reported_and_retried() {
    {
        Fake f;
        CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK));
        CHECK(f.signal(WF_FAKE_SIGNAL_KEEPALIVE_START_FAILS, 0, kGenericFail) == WF_OK);
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 100);
        CHECK(f.count(WF_EVENT_WARNING, WF_SOURCE_OTHERS) == 1);
        const WfEvent* w = last_of(f, WF_EVENT_WARNING, WF_SOURCE_OTHERS);
        CHECK(detail_is(w, L"keepalive:start failed 0x80004005|render:Fake Speakers 0|retry in 0.5 s"));
        CHECK(w != nullptr && w->hresult == kGenericFail);
        run(f, n, 500);                                       // retry 1 after 0.5 s
        CHECK(f.count(WF_EVENT_WARNING, WF_SOURCE_OTHERS) == 2);
        CHECK(detail_is(last_of(f, WF_EVENT_WARNING, WF_SOURCE_OTHERS),
                        L"keepalive:start failed 0x80004005|render:Fake Speakers 0|retry in 1.0 s"));
        run(f, n, 1000);                                      // retry 2 after 1 s -> gives up
        CHECK(f.count(WF_EVENT_WARNING, WF_SOURCE_OTHERS) == 3);
        CHECK(detail_is(last_of(f, WF_EVENT_WARNING, WF_SOURCE_OTHERS),
                        L"keepalive:start failed 0x80004005|render:Fake Speakers 0|gave up"));
        run(f, n, 5000);
        CHECK(counter(f, WF_FAKE_QUERY_KEEPALIVE) == 3);     // no endless retries
        CHECK(f.count(WF_EVENT_WARNING) == 3);
        // Never fatal: the loopback keeps running, frames keep coming.
        const WfMetrics m = f.metrics();
        CHECK(m.state == WF_STATE_RUNNING);
        CHECK(f.count(WF_EVENT_ERROR) == 0 && f.count(WF_EVENT_OTHERS_FAILED) == 0);
        CHECK(f.frames_of(WF_SOURCE_OTHERS) > 600);
        CHECK(f.diagnostics().keepalive_failures == 3);
        CHECK(f.diagnostics().keepalive_starts == 0);
        // The next render endpoint gets a fresh series.
        CHECK(f.signal(WF_FAKE_SIGNAL_CLEAR, WF_FAKE_SRC_OTHERS) == WF_OK);
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED, 0, 1) == WF_OK);
        run(f, n, 400);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender1);
        CHECK(f.diagnostics().keepalive_starts == 1);
        CHECK(f.count(WF_EVENT_WARNING) == 3);
        CHECK(f.reasons_ok());
    }
    {
        // The keep-alive's thread ends on its own (endpoint invalidated): reported, restarted.
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run(f, n, 300);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
        CHECK(f.signal(WF_FAKE_SIGNAL_KEEPALIVE_EXITS, 0, 0) == WF_OK);
        run(f, n, 100);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE).empty());
        CHECK(detail_is(last_of(f, WF_EVENT_WARNING, WF_SOURCE_OTHERS),
                        L"keepalive:stopped 0x88890004|render:Fake Speakers 0|retry in 0.5 s"));
        run(f, n, 500);
        CHECK(f.query(WF_FAKE_QUERY_KEEPALIVE) == kRender0);
        const WfDiagnostics d = f.diagnostics();
        CHECK(d.keepalive_failures == 1);
        CHECK(d.keepalive_starts == 2);
        CHECK(f.count(WF_EVENT_WARNING) == 1);
        CHECK(f.metrics().restarts == 0);                     // no capture stream touched
        CHECK(f.metrics().state == WF_STATE_RUNNING);
    }
    std::printf("  keep-alive: 3 failed starts -> 3 warnings (0.5 / 1 s retries, then gave up), "
                "exit -> warning + restart after 0.5 s\n");
}

// ---------------------------------------------------------------------------
// ABI: an older caller (sizeof 72) still gets exactly the old prefix.
// ---------------------------------------------------------------------------
void test_diagnostics_prefix_for_older_callers() {
    Fake f;
    CHECK(f.create(WF_OTHERS_DEVICE_LOOPBACK));
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    run(f, n, 100);
    unsigned char buf[sizeof(WfDiagnostics)];
    std::memset(buf, 0xAB, sizeof(buf));
    WfDiagnostics* d = reinterpret_cast<WfDiagnostics*>(buf);
    d->size = 72;
    CHECK(wf_session_get_diagnostics(f.s, d) == WF_OK);
    CHECK(d->size == 72);
    bool untouched = true;
    for (size_t i = 72; i < sizeof(buf); ++i) untouched = untouched && buf[i] == 0xAB;
    CHECK(untouched);
    CHECK(f.diagnostics().size == sizeof(WfDiagnostics));
    CHECK(f.diagnostics().keepalive_starts == 1);
}

}  // namespace

void test_devices() {
    test_default_render_change_reopens_loopback_and_keepalive();
    test_invalidated_loopback_takes_the_render_path();
    test_pinned_render_endpoint_ignores_default_changes();
    test_pinned_render_endpoint_falls_back_when_removed();
    test_keepalive_moves_with_the_default_endpoint_in_process_loopback_modes();
    test_mic_follows_the_communications_default();
    test_keepalive_failure_is_reported_and_retried();
    test_diagnostics_prefix_for_older_callers();
}
