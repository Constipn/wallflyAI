// Entry point of wf_core_tests: runs every core suite and reports the totals.
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
    std::setvbuf(stdout, nullptr, _IONBF, 0);   // unbuffered: a crash keeps its output
    const Suite suites[] = {
        {"ring", test_ring},
        {"inbox", test_inbox},
        {"drift", test_drift},
        {"framing", test_framing},
        {"echo_processor", test_echo_processor},
        {"levels", test_levels},
        {"echo_mac", test_echo_mac},
        {"timemap_mac", test_timemap_mac},
    };
    for (const Suite& s : suites) {
        const int before = g_failures;
        const int checks = g_checks;
        s.fn();
        std::printf("%-16s %3d checks  %s\n", s.name, g_checks - checks,
                    g_failures == before ? "ok" : "FAILED");
    }
    std::printf("%d checks, %d failure(s)\n", g_checks, g_failures);
    return g_failures ? 1 : 0;
}
