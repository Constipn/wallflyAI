// Entry point of wf_session_tests: drives the session layer through the public C ABI
// and the fake capture path. No hardware and, except for the dsp_stop suite (one real DSP
// thread, a few short bounded waits), no real threads and no wall-clock waiting - the
// other suites are a few milliseconds of virtual time.
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
        {"abi_layout", test_abi_layout},
        {"frames", test_frames},
        {"robustness", test_robustness},
        {"queues", test_queues},
        {"dsp_stop", test_dsp_stop},
        {"onset", test_onset},
        {"standby", test_standby},
        {"smoke_verdict", test_smoke_verdict},
        {"devices", test_devices},
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
