# wf_aec_tests: the suites that need the vendored WebRTC AEC3 + NS. Only added when
# WF_WITH_AEC3=ON - with the passthrough stub there is no canceller to measure, and the
# PushSincResampler wrapper is not compiled either.
add_executable(wf_aec_tests
  test_main_aec.cpp
  test_bridge.cpp
  test_sinc_resampler.cpp
  test_aec_erle.cpp
)

# wf_aec brings the bridge plus wf_webrtc; wf_core brings the resampler factory.
target_link_libraries(wf_aec_tests PRIVATE wf_aec wf_core)
target_include_directories(wf_aec_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(wf_aec_tests PRIVATE -Wall -Wextra -ffp-contract=off -fno-exceptions -fno-rtti)

set_target_properties(wf_aec_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
add_test(NAME wf_aec_tests COMMAND wf_aec_tests)
# The ERLE corpus is 3 x 12 s of 16 kHz audio through the full canceller.
set_tests_properties(wf_aec_tests PROPERTIES TIMEOUT 300)
