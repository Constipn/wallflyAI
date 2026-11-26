// Hand-rolled test harness, same style as windows/native/tests/test_main.cpp:
// CHECK macros, a global failure counter and one entry point per test file.
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

void test_ring();
void test_inbox();
void test_drift();
void test_framing();
void test_echo_processor();
void test_levels();
void test_echo_mac();      // port of EchoAudioTests.swift
void test_timemap_mac();   // port of AudioTimeMapTests.swift
