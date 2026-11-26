// Hand-rolled test harness for wf_aec_tests, same style as tests/core/test_util.h.
#pragma once
#include <cmath>
#include <cstdio>

extern int g_failures;
extern int g_checks;

#define CHECK(cond)                                                                \
    do {                                                                           \
        ++g_checks;                                                                \
        if (!(cond)) {                                                             \
            std::printf("FAIL %s:%d  %s\n", __FILE__, __LINE__, #cond);            \
            ++g_failures;                                                          \
        }                                                                          \
    } while (0)

#define CHECK_NEAR(a, b, eps)                                                      \
    do {                                                                           \
        ++g_checks;                                                                \
        const double _a = (double)(a), _b = (double)(b);                           \
        if (!(std::fabs(_a - _b) <= (double)(eps))) {                              \
            std::printf("FAIL %s:%d  %s ~= %s  (%.9g vs %.9g)\n", __FILE__,        \
                        __LINE__, #a, #b, _a, _b);                                 \
            ++g_failures;                                                          \
        }                                                                          \
    } while (0)

void test_aec_erle();
void test_bridge();
void test_sinc_resampler();
