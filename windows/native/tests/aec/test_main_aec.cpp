// Entry point of wf_aec_tests: the suites that need the vendored WebRTC AEC3 + NS.
// Only built when WF_WITH_AEC3=ON - with the passthrough stub there is nothing to measure.
#include "test_util.h"

int g_failures = 0;
int g_checks = 0;

namespace {
struct Suite {
    const char* name;
    void (*fn)();
};
}  // namespace

int main() {
    const Suite suites[] = {
        {"bridge", test_bridge},
        {"sinc_resampler", test_sinc_resampler},
        {"aec_erle", test_aec_erle},
    };
    for (const Suite& s : suites) {
        const int before = g_failures;
        const int checks = g_checks;
        std::printf("== %s ==\n", s.name);
        s.fn();
        std::printf("%-16s %3d checks  %s\n", s.name, g_checks - checks,
                    g_failures == before ? "ok" : "FAILED");
    }
    std::printf("%d checks, %d failure(s)\n", g_checks, g_failures);
    return g_failures ? 1 : 0;
}
