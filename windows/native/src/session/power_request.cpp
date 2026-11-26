// PowerRequest implementation.
#include "session/power_request.h"

#include "api/wallfly_audio.h"

namespace wf {

namespace {
std::atomic<uint32_t> g_active{0};
}  // namespace

uint32_t PowerRequest::active_count() { return g_active.load(std::memory_order_acquire); }

bool PowerRequest::acquire() {
    if (state() == WF_POWER_REQUEST_HELD) return true;
    if (fake_) {
        state_.store(WF_POWER_REQUEST_HELD, std::memory_order_release);
        g_active.fetch_add(1, std::memory_order_acq_rel);
        return true;
    }
    wchar_t reason[] = L"Wallfly is recording a meeting";
    REASON_CONTEXT ctx;
    ctx.Version = POWER_REQUEST_CONTEXT_VERSION;
    ctx.Flags = POWER_REQUEST_CONTEXT_SIMPLE_STRING;
    ctx.Reason.SimpleReasonString = reason;
    HANDLE h = PowerCreateRequest(&ctx);
    if (h == INVALID_HANDLE_VALUE || h == nullptr) {
        state_.store(WF_POWER_REQUEST_FAILED, std::memory_order_release);
        return false;
    }
    system_set_ = PowerSetRequest(h, PowerRequestSystemRequired) != FALSE;
    execution_set_ = PowerSetRequest(h, PowerRequestExecutionRequired) != FALSE;
    display_set_ = PowerSetRequest(h, PowerRequestDisplayRequired) != FALSE;
    if (!system_set_ && !execution_set_ && !display_set_) {
        CloseHandle(h);
        state_.store(WF_POWER_REQUEST_FAILED, std::memory_order_release);
        return false;
    }
    handle_ = h;
    state_.store(WF_POWER_REQUEST_HELD, std::memory_order_release);
    g_active.fetch_add(1, std::memory_order_acq_rel);
    return true;
}

void PowerRequest::release() {
    uint32_t held = WF_POWER_REQUEST_HELD;
    if (!state_.compare_exchange_strong(held, WF_POWER_REQUEST_NONE, std::memory_order_acq_rel)) {
        // Never held (or already released): a failed acquire just forgets its state.
        uint32_t failed = WF_POWER_REQUEST_FAILED;
        state_.compare_exchange_strong(failed, WF_POWER_REQUEST_NONE, std::memory_order_acq_rel);
        return;
    }
    if (handle_) {
        if (display_set_) PowerClearRequest(handle_, PowerRequestDisplayRequired);
        if (execution_set_) PowerClearRequest(handle_, PowerRequestExecutionRequired);
        if (system_set_) PowerClearRequest(handle_, PowerRequestSystemRequired);
        CloseHandle(handle_);
        handle_ = nullptr;
    }
    system_set_ = execution_set_ = display_set_ = false;
    g_active.fetch_sub(1, std::memory_order_acq_rel);
}

}  // namespace wf
