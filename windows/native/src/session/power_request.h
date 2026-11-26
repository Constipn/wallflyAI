// PowerRequest: keeps the machine out of sleep / Modern Standby while a session records.
//
// The first unattended 1-hour run (2026-11-02) went into
// Modern Standby 146 s after the start (Kernel-Power 506): from then on process loopback
// delivered only bursts and the recording lost 50-95 % of Others. A session therefore holds
// one Win32 power request from wf_session_start until its stop, with
//   PowerRequestSystemRequired     no idle sleep,
//   PowerRequestExecutionRequired  the process is not suspended by process lifetime
//                                  management while the request is held,
//   PowerRequestDisplayRequired    no display-off idle timeout. On an S0 low-power-idle
//                                  (Modern Standby) machine the display turning off IS the
//                                  standby entry; a system-required request alone does not
//                                  prevent it, which is the 146 s failure above.
// That is what a media player or a Teams call holds during a call. It cannot (and should not)
// stop a lid close or the power button.
//
// A fake session never calls Win32: its request is a counted no-op, so tests can assert the
// lifecycle (wf_power_requests_active) without changing the machine's power state.
// acquire()/release() are idempotent and called from the API thread (start / stop / destroy).
#pragma once
#include <windows.h>

#include <atomic>
#include <cstdint>

namespace wf {

class PowerRequest {
public:
    explicit PowerRequest(bool fake) : fake_(fake) {}
    ~PowerRequest() { release(); }
    PowerRequest(const PowerRequest&) = delete;
    PowerRequest& operator=(const PowerRequest&) = delete;

    // Creates and sets the request. False when Win32 refused (the recording goes on without
    // it; state() then reports WF_POWER_REQUEST_FAILED). A second call while held is a no-op.
    bool acquire();
    void release();
    uint32_t state() const { return state_.load(std::memory_order_acquire); }   // WF_POWER_REQUEST_*

    // Process-wide number of held requests (real and fake sessions of this DLL).
    static uint32_t active_count();

private:
    const bool fake_;
    std::atomic<uint32_t> state_{0};   // WF_POWER_REQUEST_NONE
    HANDLE handle_ = nullptr;
    bool system_set_ = false;
    bool execution_set_ = false;
    bool display_set_ = false;
};

}  // namespace wf
