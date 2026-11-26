cmake_minimum_required(VERSION 3.25)
project(WallflyAudio LANGUAGES C CXX)

set(CMAKE_CXX_STANDARD 20)
set(CMAKE_CXX_STANDARD_REQUIRED ON)
set(CMAKE_C_STANDARD 11)
set(CMAKE_POSITION_INDEPENDENT_CODE ON)

# Common flags for all targets. -ffp-contract=off keeps the AEC3 arithmetic on the
# same non-fused path as the Mac build (parity is checked via ERLE, not bit-equality).
add_compile_options(
  -Wall -Wextra
  -ffp-contract=off
  -fno-exceptions -fno-rtti
)
add_compile_definitions(
  WEBRTC_WIN
  _WIN32_WINNT=0x0A00
  WINVER=0x0A00
  NOMINMAX
  WIN32_LEAN_AND_MEAN
  UNICODE _UNICODE
)
# Static runtime: the DLL must only depend on kernel32/ole32/mmdevapi/avrt/ucrt.
add_link_options(-static -static-libstdc++ -static-libgcc)

# ---- WASAPI capture primitives ----------------------------------------------
# Shared by the DLL and by tools/audioprobe so measurements exercise the real code.
add_library(wf_wasapi STATIC
  src/wasapi/guids.cpp
  src/wasapi/com_util.cpp
  src/wasapi/format_convert.cpp
  src/wasapi/capture_loop.cpp
  src/wasapi/enumerator.cpp
  src/wasapi/mic_stream.cpp
  src/wasapi/device_loopback.cpp
  src/wasapi/process_loopback.cpp
  src/wasapi/keepalive_render.cpp
  src/wasapi/notification_client.cpp
  src/wasapi/sessions.cpp
)
target_include_directories(wf_wasapi PUBLIC src)
# mmdevapi is reached via LoadLibraryW/GetProcAddress, not the import library;
# the KSDATAFORMAT subtype GUIDs are defined locally, so ksuser is not needed.
target_link_libraries(wf_wasapi PUBLIC ole32 oleaut32 uuid avrt)

# ---- vendored WebRTC (AEC3 + noise suppressor) -------------------------------
# Exactly one implementation of src/aec/AECBridge.h is linked into the DLL: the vendored
# WebRTC AEC3 + NS bridge when WF_WITH_AEC3 is ON, the passthrough stub otherwise.
# wf_capabilities() reports WF_CAP_AEC3 only in the first case. The same switch decides
# which resampler the session layer installs, so vendor/ is configured before src/core.
option(WF_WITH_AEC3 "Link the vendored WebRTC AEC3 + NS bridge instead of the passthrough stub" OFF)
if(WF_WITH_AEC3)
  # vendor/ holds the byte-identical copy of the Mac Sources/WebRTCAEC tree and builds it
  # as the static library wf_webrtc. It is only configured in this branch, so an OFF build
  # neither compiles nor needs the vendored sources.
  add_subdirectory(vendor)
endif()

# ---- platform-independent DSP core ------------------------------------------
# Defined before the session layer so wf_session can link it by target name.
add_subdirectory(src/core)

# ---- echo canceller bridge ---------------------------------------------------
if(WF_WITH_AEC3)
  set(WF_AEC_SOURCES src/aec/AECBridge.cpp)
else()
  set(WF_AEC_SOURCES src/aec/aec_stub.cpp)
endif()
add_library(wf_aec STATIC ${WF_AEC_SOURCES})
target_include_directories(wf_aec PUBLIC src)
if(WF_WITH_AEC3)
  # PUBLIC: src/api/wallfly_audio.cpp tests this macro to report WF_CAP_AEC3, and it links
  # wf_aec, so the definition reaches it without a second name for the same fact.
  target_compile_definitions(wf_aec PUBLIC WF_WITH_AEC3=1)
  target_link_libraries(wf_aec PUBLIC wf_webrtc)
endif()

# ---- session layer -----------------------------------------------------------
# Supervisor state machine, 10 ms DSP thread, the session wiring and the fake capture
# path. Depends on the portable core (wf_core) and the WASAPI primitives (wf_wasapi).
add_library(wf_session STATIC
  src/session/supervisor.cpp
  src/session/dsp_thread.cpp
  src/session/session.cpp
  src/session/fake_capture.cpp
  src/session/power_request.cpp
)
target_include_directories(wf_session PUBLIC src)
target_link_libraries(wf_session PUBLIC wf_core wf_wasapi)

# ---- the DLL -----------------------------------------------------------------
add_library(wallfly_audio SHARED
  src/api/wallfly_audio.cpp
)
target_include_directories(wallfly_audio PUBLIC src)
target_compile_definitions(wallfly_audio PRIVATE WF_BUILDING_DLL=1)
target_link_libraries(wallfly_audio PRIVATE wf_session wf_core wf_wasapi wf_aec)
set_target_properties(wallfly_audio PROPERTIES
  OUTPUT_NAME "WallflyAudio"
  PREFIX ""
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# ---- measurement CLI (R-M, M1..M6) ------------------------------------------
add_executable(wallfly-audioprobe
  tools/audioprobe/main.cpp
  tools/audioprobe/common.cpp
  tools/audioprobe/collector.cpp
  tools/audioprobe/report.cpp
  tools/audioprobe/stats.cpp
  tools/audioprobe/cmd_devices.cpp
  tools/audioprobe/cmd_jitter.cpp
  tools/audioprobe/cmd_silence.cpp
  tools/audioprobe/cmd_sessions.cpp
  tools/audioprobe/cmd_ducking.cpp
  tools/audioprobe/cmd_switch.cpp
  tools/audioprobe/cmd_onset.cpp
  tools/audioprobe/cmd_compare.cpp
)
target_include_directories(wallfly-audioprobe PRIVATE src tools/audioprobe)
target_link_libraries(wallfly-audioprobe PRIVATE wf_wasapi shell32)
set_target_properties(wallfly-audioprobe PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin"
)

# ---- tests -------------------------------------------------------------------
enable_testing()
add_executable(wf_tests tests/test_main.cpp)
target_link_libraries(wf_tests PRIVATE wallfly_audio wf_wasapi)
set_target_properties(wf_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
add_test(NAME wf_tests COMMAND wf_tests)

# Session-layer suites. They drive the DLL through its public C ABI and the fake
# capture path only - no hardware, no real threads, no wall-clock waiting. The one
# exception is test_dsp_stop.cpp: the threaded stop protocol of DspThread is not reachable
# through the ABI without hardware, so it links the session layer statically next to the
# DLL and drives one real DSP thread with a few short, bounded waits.
add_executable(wf_session_tests
  tests/session/test_main_session.cpp
  tests/session/test_abi_layout.cpp
  tests/session/test_frames.cpp
  tests/session/test_robustness.cpp
  tests/session/test_queues.cpp
  tests/session/test_dsp_stop.cpp
  tests/session/test_onset.cpp
  tests/session/test_standby.cpp
  tests/session/test_smoke_verdict.cpp
  tests/session/test_devices.cpp
)
# `tests` for hardware/smoke_verdict.h: the smoke tool's verdict is a pure header tested here.
target_include_directories(wf_session_tests PRIVATE src tests/session tests)
target_link_libraries(wf_session_tests PRIVATE wallfly_audio wf_session wf_aec)
set_target_properties(wf_session_tests PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
add_test(NAME wf_session_tests COMMAND wf_session_tests)

# ---- DSP core tests -----------------------------------------------------------
add_subdirectory(tests/core)

# ---- vendored AEC3 + NS tests ---------------------------------------------------
# ERLE acceptance, the C bridge contract and the PushSincResampler wrapper. Nothing to
# register in a stub build, so the suite only exists with WF_WITH_AEC3=ON.
if(WF_WITH_AEC3)
  add_subdirectory(tests/aec)
endif()

# ---- hardware smoke (not part of ctest: it needs real endpoints) --------------
# Run by hand: build/release/bin/wf_hw_smoke.exe [seconds] [--mode system|include|device]
#   [--pid N] [--expect-audio]
add_executable(wf_hw_smoke tests/hardware/wf_hw_smoke.cpp)
target_include_directories(wf_hw_smoke PRIVATE src tests/hardware)
target_link_libraries(wf_hw_smoke PRIVATE wallfly_audio)
set_target_properties(wf_hw_smoke PROPERTIES
  RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
