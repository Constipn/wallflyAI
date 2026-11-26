// FakePlant: the test-injection plant behind wf_session_create_fake.
//
// It implements the same IPlant interface the WASAPI plant does, but:
//   * driver steps run inline on the caller's thread, so a start or stop is "instant"
//     unless WF_FAKE_SIGNAL_HANG_STEP asks it to never complete (which is how the 5 s
//     step limit, the orphan counter and DriverBlocked are exercised);
//   * there is no capture thread. wf_fake_push writes into the very same AudioRing a
//     real capture thread would write to, so everything downstream - inbox, normalizer,
//     framer, echo processor, queues - is the production code path;
//   * time comes from the session's virtual clock (wf_fake_set_now), so every spec 3.1
//     constant (2.5 s, 0.25/0.5/0.75 s, 0.5 s, 1/2/4/8/16 s, 200 ms, 50 ms, 100 ms,
//     0.5 s) is reachable in microseconds of wall time;
//   * the mic endpoint is simulated as far as the format-change path needs it: an
//     endpoint id (the configured one or the default it resolves to), a current endpoint
//     format and the format the current mic stream was opened with. The property
//     notifications themselves go through Session's production filter;
//   * WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS makes only the configured endpoint fail, so a
//     start falls back to the console default capture endpoint exactly as RealPlant does
//     (spec 3.1);
//   * AP3 (2026-11-23): four render and four capture endpoints with a state and a default
//     per role (wallfly_audio.h "Fake endpoints"), resolved exactly like RealPlant resolves
//     them: device loopback on the pinned render endpoint or the console default (a pinned
//     endpoint that is not ACTIVE falls back), the keep-alive on the endpoint it is asked
//     for, an unconfigured mic on the communications default (console when there is none).
//     Session turns the endpoint signals into the IMMNotificationClient callbacks a real
//     system sends and feeds them through its production filter.
#pragma once
#include <memory>
#include <string>

#include "session/supervisor.h"
#include "wasapi/format_convert.h"

namespace wf {

// Maps a WF_FAKE_SRC_* value to a role; returns false for anything else.
bool fake_source_to_role(uint32_t fake_source, Role* out);

class FakePlant final : public IPlant {
public:
    static constexpr const wchar_t* kDefaultCaptureEndpoint =
        L"{0.0.1.00000000}.{fake-default-capture}";
    static constexpr const wchar_t* kRenderEndpoint = L"{0.0.0.00000000}.{fake-default-render}";
    static constexpr int kEndpoints = 4;

    explicit FakePlant(std::shared_ptr<MsgQueue> queue);

    // ---- IPlant ----------------------------------------------------------
    void launch(const std::shared_ptr<StepCtx>& ctx) override;
    int32_t do_start(StepCtx& ctx) override;
    void do_stop(StepCtx& ctx) override;
    std::shared_ptr<StreamChannel> make_channel(Role r, uint32_t attempt, bool app_source) override;
    bool reference_watchdog_armed(int64_t now_100ns) override;
    bool mic_format_changed(const wchar_t* device_id) override;
    bool mic_endpoint(wchar_t* out, size_t cap) override;
    uint32_t mic_default_role() override { return mic_role_; }
    int32_t do_keepalive(StepCtx& ctx) override;
    bool keepalive_exited(int32_t* hr) override;
    void endpoint_name(bool render, const wchar_t* id, wchar_t* out, size_t cap) override;

    // ---- injection -------------------------------------------------------
    // Pushes one packet into the role's ring. Returns WF_OK, WF_E_STATE when that role
    // has no running stream, or WF_E_INVALID_ARG.
    int32_t push(Role r, const float* samples, uint32_t frames, uint32_t rate,
                 int64_t qpc_100ns, uint64_t device_position, uint32_t flags,
                 int64_t now_100ns);
    int32_t signal(uint32_t sig, Role r, int32_t hr, int64_t now_100ns);

    // WF_FAKE_SIGNAL_FORMAT_CHANGE: the mic endpoint's shared-mode format really changes
    // (48 kHz <-> 44.1 kHz, so every change differs from the format before it).
    void change_mic_format();
    // The endpoint a mic notification names: the open mic's endpoint, else the one the mic
    // would open (configured id or the default it resolves to).
    std::wstring mic_endpoint_or_default() const;

    bool has_channel(Role r) const { return channels_[static_cast<int>(r)] != nullptr; }

    // ---- AP3 endpoint model (Session turns these into notifications) -------
    enum DefaultRole : uint32_t { kConsole = 1, kCommunications = 2 };
    static std::wstring render_id(int index);
    static std::wstring capture_id(int index);
    // Moves the default `roles` (kConsole covers console + multimedia) of the flow to
    // `index` (-1: communications capture only, "no default"). False for a bad index.
    bool set_default(bool render, int index, uint32_t roles);
    // Sets a render endpoint's DEVICE_STATE_*; every default it held while it stops being
    // ACTIVE moves to the lowest ACTIVE index. `moved` gets the kConsole/kCommunications
    // roles that moved. False for a bad index.
    bool set_render_state(int index, uint32_t state, uint32_t* moved);
    std::wstring default_id(bool render, uint32_t role) const;   // "" when there is none
    void keepalive_fail(int32_t hr) { keepalive_fail_hr_ = hr; }
    int32_t keepalive_exit(int32_t hr);
    int32_t query(uint32_t what, wchar_t* text, uint32_t text_chars, uint32_t* value) const;

private:
    struct Endpoint {
        std::wstring id;
        std::wstring name;
        uint32_t state = 1;   // DEVICE_STATE_ACTIVE
    };
    int find(bool render, const wchar_t* id) const;
    bool active(bool render, int index) const;
    std::wstring name_of(bool render, const std::wstring& id) const;

    std::shared_ptr<MsgQueue> queue_;
    std::shared_ptr<StreamChannel> channels_[kRoleCount];
    int32_t start_hr_[kRoleCount] = {0, 0, 0};       // non-zero -> the start step fails
    int32_t configured_mic_hr_ = 0;                  // non-zero -> the configured mic endpoint fails
    bool    no_first_packet_[kRoleCount] = {false, false, false};
    bool    hang_next_step_ = false;
    bool    watchdog_armed_ = true;

    std::wstring mic_endpoint_;        // endpoint of the open mic stream; empty while none
    std::wstring last_mic_endpoint_;   // the endpoint the mic last opened on
    uint32_t     mic_role_ = kMicRoleExplicit;
    uint32_t     mic_opens_ = 0;
    PcmFormat endpoint_format_{SampleFmt::F32, 2, 48000, 8};
    PcmFormat mic_open_format_{};

    Endpoint render_[kEndpoints];
    Endpoint capture_[kEndpoints];
    int default_render_ = 0;
    int default_comms_render_ = 0;
    int default_capture_ = 0;
    int default_comms_capture_ = 0;    // -1: no communications default

    std::wstring loopback_endpoint_;   // device loopback (mode 2) of the current Others stream
    uint32_t     loopback_opens_ = 0;
    std::wstring keepalive_endpoint_;  // empty while no keep-alive runs
    uint32_t     keepalive_attempts_ = 0;
    int32_t      keepalive_fail_hr_ = 0;
    bool         keepalive_exit_pending_ = false;
    int32_t      keepalive_exit_hr_ = 0;
};

}  // namespace wf
