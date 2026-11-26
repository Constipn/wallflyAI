# wf_core_tests: hand-rolled suites for the portable DSP core. mock_aec.cpp supplies the
# mn_aec_* symbols that wf_core references until src/aec/AECBridge.cpp lands.
add_executable(wf_core_tests
  test_main_core.cpp
  mock_aec.cpp
  test_ring.cpp
  test_inbox.cpp
  test_drift.cpp
  test_framing.cpp
  test_echo_processor.cpp
  test_levels.cpp
  test_echo_mac.cpp
  test_timemap_mac.cpp
)

target_link_libraries(wf_core_tests PRIVATE wf_core)
target_include_directories(wf_core_tests PRIVATE "${CMAKE_CURRENT_SOURCE_DIR}")
target_compile_options(wf_core_tests PRIVATE -Wall -Wextra -ffp-contract=off -fno-exceptions -fno-rtti)

find_package(Threads)
if(Threads_FOUND)
  target_link_libraries(wf_core_tests PRIVATE Threads::Threads)
endif()

set_target_properties(wf_core_tests PROPERTIES RUNTIME_OUTPUT_DIRECTORY "${CMAKE_BINARY_DIR}/bin")
add_test(NAME wf_core_tests COMMAND wf_core_tests)
