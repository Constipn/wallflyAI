// Forwarding header for vendor-ns: upstream "api/array_view.h" -> vendored rtc::ArrayView (std::span).
// Upstream array_view.h also provides rtc_base/checks.h, which some NS sources rely on transitively.
#include "rtc_base/array_view.h"
#include "rtc_base/checks.h"
