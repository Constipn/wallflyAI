// Every constant of spec 3.1 / 3.2 the supervisor implements, replayed in virtual time:
// the 3 x 0.5 s attempt cycle, the 2.5 s first-buffer deadline with its 0.25/0.5/0.75 s
// restarts, the permission stop, the 0.5 s watchdog (and the app-source exemption), the
// 50 ms format coalescing with exactly one filter reset (and no rebuild for property changes
// that are not a real mic format change), and the 5 s step limit with two
// orphans turning into "audio driver blocked".
#include <cwchar>
#include <vector>

#include "test_util.h"

using harness::Fake;
using harness::kTicksPerMs;

namespace {

constexpr uint32_t kRate = 16000;
constexpr int kBlock = 160;
constexpr int64_t kBlockTicks = 100000;
constexpr int32_t kAccessDenied = static_cast<int32_t>(0x80070005);   // E_ACCESSDENIED
constexpr int32_t kGenericFail = static_cast<int32_t>(0x80004005);    // E_FAIL

void push_silence(Fake& f, uint32_t source, int64_t n) {
    float buf[kBlock] = {};
    f.push(source, buf, kBlock, kRate, f.t0 + n * kBlockTicks);
}

// `ms` of healthy capture: one mic and one Others block per 10 ms tick, continuing the
// block counter `n`, so neither the watchdog nor a first-buffer rule fires on its own.
void run_healthy(Fake& f, int64_t& n, int ms) {
    for (int i = 0; i < ms / 10; ++i) {
        push_silence(f, WF_FAKE_SRC_MIC, n);
        push_silence(f, WF_FAKE_SRC_OTHERS, n);
        ++n;
        f.set_now(f.t0 + n * kBlockTicks);
        f.tick();
    }
}

std::vector<double> offsets_of(const Fake& f, uint32_t kind, uint32_t source) {
    std::vector<double> out;
    for (const WfEvent& e : f.events) {
        if (e.kind == kind && e.source == source) out.push_back(e.at_offset_sec);
    }
    return out;
}

// ---------------------------------------------------------------------------
// "3 Versuche im Abstand von 0,5 s; danach degraded"
// ---------------------------------------------------------------------------
void test_degraded_after_three_attempts() {
    Fake f;
    CHECK(f.create());
    CHECK(f.signal(WF_FAKE_SIGNAL_START_FAILS, WF_FAKE_SRC_MIC, kGenericFail) == WF_OK);
    CHECK(f.start());
    f.tick();                                  // attempt 1 fails at offset 0
    CHECK(f.count(WF_EVENT_DEGRADED) == 0);
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 0);

    f.advance_ms(500);                         // attempt 2 at 0.5 s
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 1);
    CHECK(f.count(WF_EVENT_DEGRADED) == 0);

    f.advance_ms(500);                         // attempt 3 at 1.0 s -> give up
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 2);
    CHECK(f.count(WF_EVENT_DEGRADED, WF_SOURCE_ME) == 1);
    CHECK(f.metrics().state == WF_STATE_DEGRADED);

    const std::vector<double> starts = offsets_of(f, WF_EVENT_RESTARTING, WF_SOURCE_ME);
    CHECK(starts.size() == 2);
    if (starts.size() == 2) {
        CHECK_NEAR(starts[0], 0.5, 1e-6);
        CHECK_NEAR(starts[1], 1.0, 1e-6);
    }
    const WfEvent* degraded = f.first(WF_EVENT_DEGRADED);
    CHECK(degraded != nullptr);
    if (degraded) CHECK_NEAR(degraded->at_offset_sec, 1.0, 1e-6);

    // Background ladder: 1 / 2 / 4 / 8 / 16 s after the degradation.
    f.advance_ms(1100);                        // first background retry at 2.0 s
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 3);
    const std::vector<double> all = offsets_of(f, WF_EVENT_RESTARTING, WF_SOURCE_ME);
    CHECK_NEAR(all[2], 2.0, 1e-6);
    CHECK(f.reasons_ok());   // Restarting/Degraded carry WF_GAP_NONE
    std::printf("  degraded: attempts at 0.5 / 1.0 s, background retry at 2.0 s\n");
}

// ---------------------------------------------------------------------------
// "Frist 2,5 s; bei Stopp davor bis zu 3 Neustarts nach 0,25/0,5/0,75 s"
// ---------------------------------------------------------------------------
void test_first_buffer_timeout() {
    Fake f;
    CHECK(f.create());
    CHECK(f.signal(WF_FAKE_SIGNAL_NO_FIRST_PACKET, WF_FAKE_SRC_MIC) == WF_OK);
    CHECK(f.start());
    f.tick();

    f.advance_ms(2400);
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 0);   // inside the 2.5 s window
    f.advance_ms(400);                                        // -> restart at 2.75 s
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 1);
    f.advance_ms(3000);                                       // -> restart at 5.75 s
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 2);
    f.advance_ms(3300);                                       // -> restart at 9.00 s
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 3);
    CHECK(f.count(WF_EVENT_DEGRADED, WF_SOURCE_ME) == 0);
    f.advance_ms(2500);                                       // 4th deadline at 11.5 s
    CHECK(f.count(WF_EVENT_DEGRADED, WF_SOURCE_ME) == 1);

    const std::vector<double> starts = offsets_of(f, WF_EVENT_RESTARTING, WF_SOURCE_ME);
    CHECK(starts.size() >= 3);
    if (starts.size() >= 3) {
        CHECK_NEAR(starts[0], 2.75, 1e-6);    // 2.5 s deadline + 0.25 s
        CHECK_NEAR(starts[1], 5.75, 1e-6);    // + 2.5 s deadline + 0.50 s
        CHECK_NEAR(starts[2], 9.00, 1e-6);    // + 2.5 s deadline + 0.75 s
    }
    CHECK(f.count(WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_ME) == 0);
    std::printf("  first buffer: restarts at 2.75 / 5.75 / 9.00 s, degraded at 11.5 s\n");
}

// ---------------------------------------------------------------------------
// "Berechtigungsfehler stoppt alle Retries"
// ---------------------------------------------------------------------------
void test_permission_denied() {
    Fake f;
    CHECK(f.create());
    CHECK(f.signal(WF_FAKE_SIGNAL_START_FAILS, WF_FAKE_SRC_MIC, kAccessDenied) == WF_OK);
    CHECK(f.start());
    f.tick();
    CHECK(f.count(WF_EVENT_PERMISSION_DENIED, WF_SOURCE_ME) == 1);
    const WfEvent* e = f.first(WF_EVENT_PERMISSION_DENIED);
    CHECK(e != nullptr && e->hresult == kAccessDenied);

    f.advance_ms(20000);   // past the whole 3 x 0.5 s cycle and the 1/2/4/8/16 s ladder
    CHECK(f.count(WF_EVENT_PERMISSION_DENIED, WF_SOURCE_ME) == 1);
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 0);
    CHECK(f.count(WF_EVENT_DEGRADED, WF_SOURCE_ME) == 0);
    CHECK(f.metrics().attempt_id == 1);   // the mic was never opened a second time
    CHECK(f.reasons_ok());
    std::printf("  permission: 1 event, 0 retries over 20 s\n");
}

// ---------------------------------------------------------------------------
// "Watchdog prueft alle 100 ms; 0,5 s ohne Callback fuehrt zur Wiederherstellung",
// and spec 3.2: the include-mode app source is exempt.
// ---------------------------------------------------------------------------
void test_watchdog() {
    {
        Fake f;
        CHECK(f.create());
        CHECK(f.start());
        f.tick();
        for (int n = 0; n < 50; ++n) {
            push_silence(f, WF_FAKE_SRC_MIC, n);
            push_silence(f, WF_FAKE_SRC_OTHERS, n);
            f.set_now(f.t0 + (n + 1) * kBlockTicks);
            f.tick();
        }
        CHECK(f.count(WF_EVENT_WATCHDOG_RECOVERY) == 0);
        f.advance_ms(700);                       // nothing is delivered any more
        CHECK(f.count(WF_EVENT_WATCHDOG_RECOVERY, WF_SOURCE_ME) >= 1);
        CHECK(f.count_gap(WF_GAP_WATCHDOG_RECOVERY) >= 1);
        CHECK(f.metrics().watchdog_recoveries >= 1);
        std::printf("  watchdog: mic recovered after 0.5 s of silence\n");
    }
    {
        // others_mode 1: Others is the include-mode app source, the reference is a
        // separate exclude-mode stream. Only Others is allowed to fall silent.
        Fake f;
        CHECK(f.create(WF_OTHERS_PROCESS_INCLUDE, 0.0, 4242));
        CHECK(f.start());
        f.tick();
        for (int n = 0; n < 40; ++n) {
            push_silence(f, WF_FAKE_SRC_MIC, n);
            push_silence(f, WF_FAKE_SRC_OTHERS, n);
            push_silence(f, WF_FAKE_SRC_REFERENCE, n);
            f.set_now(f.t0 + (n + 1) * kBlockTicks);
            f.tick();
        }
        CHECK(f.count(WF_EVENT_DEVICE_ACQUIRED) == 3);   // mic, Others, reference
        for (int n = 40; n < 160; ++n) {                 // 1.2 s without Others
            push_silence(f, WF_FAKE_SRC_MIC, n);
            push_silence(f, WF_FAKE_SRC_REFERENCE, n);
            f.set_now(f.t0 + (n + 1) * kBlockTicks);
            f.tick();
        }
        CHECK(f.count(WF_EVENT_WATCHDOG_RECOVERY, WF_SOURCE_OTHERS) == 0);
        CHECK(f.count(WF_EVENT_WATCHDOG_RECOVERY, WF_SOURCE_ME) == 0);
        CHECK(f.count(WF_EVENT_WATCHDOG_RECOVERY, WF_SOURCE_REFERENCE) == 0);
        // The reference is never published as frames in include mode.
        CHECK(f.metrics().ref_mode_actual == WF_REF_MODE_PROCESS_EXCLUDE);
        std::printf("  watchdog: app source exempt over 1.2 s of silence\n");
    }
}

// ---------------------------------------------------------------------------
// "Formatwechsel im Geraet: 50 ms zusammenfassen, dann Einheit neu aufbauen"
// plus spec 3.3 step 5: exactly one filter reset.
// ---------------------------------------------------------------------------
void test_format_change_coalesced() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    for (int n = 0; n < 20; ++n) {
        push_silence(f, WF_FAKE_SRC_MIC, n);
        push_silence(f, WF_FAKE_SRC_OTHERS, n);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    CHECK(f.metrics().aec_resets == 0);
    // The last fed mic frames are still inside the canceller's latency.
    const size_t me_before = f.frames_of(WF_SOURCE_ME);
    CHECK(me_before < 20);

    CHECK(f.signal(WF_FAKE_SIGNAL_FORMAT_CHANGE) == WF_OK);
    f.advance_ms(10);
    CHECK(f.signal(WF_FAKE_SIGNAL_FORMAT_CHANGE) == WF_OK);
    f.advance_ms(10);
    CHECK(f.signal(WF_FAKE_SIGNAL_FORMAT_CHANGE) == WF_OK);
    f.advance_ms(20);
    CHECK(f.count(WF_EVENT_FORMAT_CHANGED) == 0);    // still inside the 50 ms window
    f.advance_ms(40);
    CHECK(f.count(WF_EVENT_FORMAT_CHANGED) == 1);
    CHECK(f.metrics().aec_resets == 1);
    // Mac append(): flushEchoTail() before the reset - the delayed tail is emitted (all 20
    // mic frames, contiguous) instead of being reported as a Restart gap.
    CHECK(f.frames_of(WF_SOURCE_ME) == 20);
    CHECK(f.count_gap(WF_GAP_RESTART) == 0);
    int64_t expect = 0;
    bool contiguous = true;
    for (const WfFrame& fr : f.frames) {
        if (fr.source != WF_SOURCE_ME) continue;
        if (fr.timeline_sample != expect || fr.attempt_id != 1) contiguous = false;        expect += kBlock;
    }
    CHECK(contiguous);
    f.advance_ms(500);
    CHECK(f.count(WF_EVENT_FORMAT_CHANGED) == 1);
    CHECK(f.metrics().aec_resets == 1);
    std::printf("  format change: 3 notifications -> 1 rebuild, 1 filter reset, "
                "tail %zu -> %zu Me frames\n", me_before, f.frames_of(WF_SOURCE_ME));
}

// ---------------------------------------------------------------------------
// Live run 2026-11-01: with the default mic every endpoint property change was taken for a
// mic format change - the render endpoint fires {5b5e03cb-...},0 whenever a stream starts
// or stops while audio plays, our own mic rebuild included - and the confirmation always
// said "changed" (RAW stream format 2 ch vs. processed mix format 4 ch, and every open
// wrote the RAW value back), so the mic rebuilt itself every ~0.35 s with a filter reset
// each time. Spec 3.1: only a real format change of the mic endpoint rebuilds the unit,
// 50 ms coalesced, once, with exactly one filter reset.
// ---------------------------------------------------------------------------
void test_property_changes_filtered() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    int64_t n = 0;
    run_healthy(f, n, 200);
    const auto untouched = [&f]() {
        const WfMetrics m = f.metrics();
        return f.count(WF_EVENT_FORMAT_CHANGED) == 0 && f.count(WF_EVENT_RESTARTING) == 0 &&
               m.aec_resets == 0 && m.restarts == 0 && m.attempt_id == 1;
    };

    // 1. The measured pattern: the render endpoint's activity key every 200 ms while
    //    another process plays, and that endpoint's own format key once.
    for (int i = 0; i < 10; ++i) {
        CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_OTHERS, 0) == WF_OK);
        run_healthy(f, n, 200);
    }
    CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_OTHERS, 1) == WF_OK);
    run_healthy(f, n, 200);
    CHECK(untouched());

    // 2. The mic's own endpoint, a key that is not a format key.
    CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_MIC, 0) == WF_OK);
    run_healthy(f, n, 200);
    CHECK(untouched());

    // 3. The mic's endpoint with PKEY_AudioEngine_DeviceFormat, but the format is still the
    //    one the stream opened with: it passes the filter and the 50 ms window, and the
    //    confirmation answers "unchanged" - also when asked again and again.
    for (int i = 0; i < 3; ++i) {
        CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_MIC, 1) == WF_OK);
        run_healthy(f, n, 30);
    }
    run_healthy(f, n, 200);
    CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_MIC, 1) == WF_OK);
    run_healthy(f, n, 200);
    CHECK(untouched());

    // 4. A real format change: exactly one rebuild and one filter reset after 50 ms.
    CHECK(f.signal(WF_FAKE_SIGNAL_FORMAT_CHANGE) == WF_OK);
    run_healthy(f, n, 40);
    CHECK(f.count(WF_EVENT_FORMAT_CHANGED) == 0);
    run_healthy(f, n, 30);
    CHECK(f.count(WF_EVENT_FORMAT_CHANGED, WF_SOURCE_ME) == 1);
    CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 1);
    CHECK(f.metrics().aec_resets == 1);
    CHECK(f.metrics().attempt_id == 2);

    // 5. What follows a rebuild on hardware - the render activity key again, a DeviceFormat
    //    notification that only repeats the new format - rebuilds nothing: no loop.
    for (int i = 0; i < 5; ++i) {
        CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_OTHERS, 0) == WF_OK);
        CHECK(f.signal(WF_FAKE_SIGNAL_PROPERTY_CHANGED, WF_FAKE_SRC_MIC, 1) == WF_OK);
        run_healthy(f, n, 200);
    }
    const WfMetrics m = f.metrics();
    CHECK(f.count(WF_EVENT_FORMAT_CHANGED) == 1);
    CHECK(m.restarts == 1);
    CHECK(m.aec_resets == 1);
    CHECK(m.attempt_id == 2);
    CHECK(f.count(WF_EVENT_WATCHDOG_RECOVERY) == 0);
    CHECK(f.reasons_ok());
    std::printf("  property changes: 21 non-format/other-endpoint/unchanged notifications -> "
                "0 rebuilds; 1 real change -> %d rebuild, %llu filter reset\n",
                f.count(WF_EVENT_FORMAT_CHANGED), static_cast<unsigned long long>(m.aec_resets));
}

// ---------------------------------------------------------------------------
// Device switch: debounced 200 ms, old stream stopped before the new one starts.
// ---------------------------------------------------------------------------
void test_device_switch_debounce() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    for (int n = 0; n < 20; ++n) {
        push_silence(f, WF_FAKE_SRC_MIC, n);
        push_silence(f, WF_FAKE_SRC_OTHERS, n);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
    }
    // A burst of notifications must collapse into one switch.
    for (int i = 0; i < 5; ++i) {
        CHECK(f.signal(WF_FAKE_SIGNAL_DEFAULT_DEVICE_CHANGED) == WF_OK);
        f.advance_ms(30);
    }
    CHECK(f.count(WF_EVENT_DEVICE_SWITCHED) == 0);   // the window keeps restarting
    f.advance_ms(210);
    CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME) == 1);
    CHECK(f.count_gap(WF_GAP_RESTART) >= 1);
    CHECK(f.metrics().aec_resets == 1);              // a device change resets the filter
    CHECK(f.metrics().attempt_id == 2);

    // The explicit API entry point takes the same debounced path.
    CHECK(wf_session_set_mic_device(f.s, L"{0.0.1.00000000}.{test-endpoint}") == WF_OK);
    f.advance_ms(100);
    CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME) == 1);
    f.advance_ms(160);
    CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME) == 2);
    CHECK(f.metrics().aec_resets == 2);
    CHECK(f.metrics().attempt_id == 3);
    CHECK(f.reasons_ok());
    std::printf("  device switch: 5 notifications in 150 ms -> 1 switch, API switch -> 1 more\n");
}

// ---------------------------------------------------------------------------
// Spec 3.1 "Geraetewahl: gespeicherte Geraete-UID, sonst Systemstandard. Schlaegt die
// gespeicherte fehl, wird im selben Versuch der Standard probiert. Es gibt keinen weiteren
// Ausweich-Tier; die UI zeigt 'Name - Ersatz (Grund)'." (Mac MicrophoneFallbackTests)
// ---------------------------------------------------------------------------
bool starts_with(const wchar_t* text, const wchar_t* prefix) {
    for (size_t i = 0; prefix[i]; ++i) {
        if (text[i] != prefix[i]) return false;
    }
    return true;
}

const WfEvent* acquired_me(const Fake& f) {
    for (const WfEvent& e : f.events) {
        if (e.kind == WF_EVENT_DEVICE_ACQUIRED && e.source == WF_SOURCE_ME) return &e;
    }
    return nullptr;
}

void test_mic_fallback() {
    static const wchar_t kConfigured[] = L"{0.0.1.00000000}.{configured-usb}";
    constexpr int32_t kNotFound = static_cast<int32_t>(0x80070490);   // E_NOTFOUND
    {
        // The configured endpoint is missing: the default opens in the SAME attempt.
        Fake f;
        CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, 0.0, 0, 1, kConfigured));
        CHECK(f.signal(WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS) == WF_OK);
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run_healthy(f, n, 500);
        const WfEvent* e = acquired_me(f);
        CHECK(e != nullptr);
        if (e) {
            CHECK(e->attempt_id == 1);
            CHECK(e->hresult == kNotFound);
            CHECK(std::wcscmp(e->detail, L"fallback:selected microphone missing") == 0);
        }
        CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 0);
        CHECK(f.count(WF_EVENT_DEGRADED) == 0);
        CHECK(f.count(WF_EVENT_PERMISSION_DENIED) == 0);
        const WfMetrics m = f.metrics();
        CHECK(m.state == WF_STATE_RUNNING);
        CHECK(m.attempt_id == 1);
        CHECK(m.mic_raw == 1);
        CHECK(f.frames_of(WF_SOURCE_ME) > 0);
        CHECK(f.frames_of(WF_SOURCE_OTHERS) > 0);
        // Others is never a "fallback".
        for (const WfEvent& o : f.events) {
            if (o.kind == WF_EVENT_DEVICE_ACQUIRED && o.source == WF_SOURCE_OTHERS) {
                CHECK(o.detail[0] == 0 && o.hresult == 0);
            }
        }
        CHECK(f.reasons_ok());
    }
    {
        // A configured endpoint that exists but does not open (e.g. held exclusively).
        Fake f;
        CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, 0.0, 0, 1, kConfigured));
        CHECK(f.signal(WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS, WF_FAKE_SRC_MIC, kGenericFail) == WF_OK);
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run_healthy(f, n, 200);
        const WfEvent* e = acquired_me(f);
        CHECK(e != nullptr && starts_with(e->detail, L"fallback:") &&
              std::wcscmp(e->detail, L"fallback:selected microphone unavailable") == 0 &&
              e->hresult == kGenericFail);
        CHECK(f.metrics().state == WF_STATE_RUNNING);
    }
    {
        // A permission error is never hidden by another device: no fallback, no retries.
        Fake f;
        CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, 0.0, 0, 1, kConfigured));
        CHECK(f.signal(WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS, WF_FAKE_SRC_MIC, kAccessDenied) == WF_OK);
        CHECK(f.start());
        f.tick();
        CHECK(f.count(WF_EVENT_PERMISSION_DENIED, WF_SOURCE_ME) == 1);
        f.advance_ms(20000);
        CHECK(f.count(WF_EVENT_PERMISSION_DENIED, WF_SOURCE_ME) == 1);
        CHECK(f.count(WF_EVENT_DEVICE_ACQUIRED, WF_SOURCE_ME) == 0);
        CHECK(f.count(WF_EVENT_RESTARTING, WF_SOURCE_ME) == 0);
        CHECK(f.metrics().attempt_id == 1);
        CHECK(f.frames_of(WF_SOURCE_ME) == 0);
    }
    {
        // No configured endpoint: the default is the only candidate, never a "fallback".
        // (AP3, 2026-11-23: the detail names the endpoint, "mic:<name>"; it was empty.)
        Fake f;
        CHECK(f.create());
        CHECK(f.signal(WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS) == WF_OK);
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run_healthy(f, n, 200);
        const WfEvent* e = acquired_me(f);
        CHECK(e != nullptr && std::wcscmp(e->detail, L"mic:Fake Microphone 0") == 0 &&
              e->hresult == 0);
    }
    {
        // The configured endpoint comes back: the next (re)open uses it again, without the
        // fallback marker. The fallback state belongs to the attempt, not to the session.
        Fake f;
        CHECK(f.create(WF_OTHERS_SYSTEM_EXCLUDE, 0.0, 0, 1, kConfigured));
        CHECK(f.signal(WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS) == WF_OK);
        CHECK(f.start());
        f.tick();
        int64_t n = 0;
        run_healthy(f, n, 200);
        CHECK(f.signal(WF_FAKE_SIGNAL_CLEAR) == WF_OK);
        CHECK(wf_session_set_mic_device(f.s, kConfigured) == WF_OK);
        run_healthy(f, n, 500);
        int acquired = 0;
        for (const WfEvent& e : f.events) {
            if (e.kind != WF_EVENT_DEVICE_ACQUIRED || e.source != WF_SOURCE_ME) continue;
            ++acquired;
            if (e.attempt_id == 1) CHECK(starts_with(e.detail, L"fallback:"));
            // AP3: no fallback marker; the detail names the configured endpoint ("mic:<name>";
            // a fake endpoint outside the model is named by its id).
            if (e.attempt_id == 2) {
                CHECK(starts_with(e.detail, L"mic:") && !starts_with(e.detail, L"fallback:") &&
                      e.hresult == 0);
            }
        }
        CHECK(acquired == 2);
        CHECK(f.count(WF_EVENT_DEVICE_SWITCHED, WF_SOURCE_ME) == 1);
    }
    std::printf("  mic fallback: missing/unavailable -> default in attempt 1, access denied -> no fallback\n");
}

// ---------------------------------------------------------------------------
// "Zeitlimit je Treiber-Schritt 5 s ... nach 2 verwaisten heisst es Audio driver blocked"
// ---------------------------------------------------------------------------
void test_driver_blocked() {
    Fake f;
    CHECK(f.create());
    CHECK(f.start());
    f.tick();
    // Keep Others healthy so the mic is the only role launching driver steps.
    CHECK(f.signal(WF_FAKE_SIGNAL_HANG_STEP) == WF_OK);

    for (int n = 0; n < 1400; ++n) {               // 14 s of virtual time
        push_silence(f, WF_FAKE_SRC_OTHERS, n);
        f.set_now(f.t0 + (n + 1) * kBlockTicks);
        f.tick();
        // The mic's first-buffer stop step at 2.5 s hangs and is orphaned at 7.5 s;
        // arming again at 7.6 s makes the 8.0 s restart hang too (orphan at 13.0 s).
        if (n == 760) CHECK(f.signal(WF_FAKE_SIGNAL_HANG_STEP) == WF_OK);
    }
    const WfMetrics m = f.metrics();
    CHECK(m.orphaned_steps == 2);
    CHECK(f.count(WF_EVENT_DRIVER_BLOCKED) == 1);
    CHECK(m.state == WF_STATE_DEGRADED || m.state == WF_STATE_DRIVER_BLOCKED);
    CHECK(f.reasons_ok());
    std::printf("  driver blocked: %u orphaned steps\n", m.orphaned_steps);
}

}  // namespace

void test_robustness() {
    test_degraded_after_three_attempts();
    test_first_buffer_timeout();
    test_permission_denied();
    test_watchdog();
    test_format_change_coalesced();
    test_property_changes_filtered();
    test_device_switch_debounce();
    test_mic_fallback();
    test_driver_blocked();
}
