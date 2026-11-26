// QPC helpers. WASAPI reports pu64QPCPosition already converted to 100 ns units,
// so every timestamp in this layer uses the same unit and the same monotonic clock.
#pragma once
#include <windows.h>
#include <stdint.h>

namespace wf {

inline int64_t qpc_frequency() {
    static int64_t f = [] { LARGE_INTEGER li{}; QueryPerformanceFrequency(&li); return li.QuadPart; }();
    return f;
}

inline int64_t qpc_now_100ns() {
    LARGE_INTEGER li{};
    QueryPerformanceCounter(&li);
    const int64_t f = qpc_frequency();
    if (f <= 0) return 0;
    // Split to avoid overflow on long uptimes.
    const int64_t whole = li.QuadPart / f;
    const int64_t rest = li.QuadPart % f;
    return whole * 10000000LL + (rest * 10000000LL) / f;
}

} // namespace wf
