# wf_core: the platform-independent DSP core (ring, inbox, normalizer, framer, echo
# processor, levels). It references the mn_aec_* bridge but does not provide it - link
# src/aec/AECBridge.cpp (production) or tests/core/mock_aec.cpp (tests) alongside it.
add_library(wf_core STATIC
  ring.cpp
  inbox.cpp
  linear_resampler.cpp
  normalizer.cpp
  framer.cpp
  echo_processor.cpp
  levels.cpp
)

# Headers are included as "core/..." and "aec/...", so src/ is the include root.
target_include_directories(wf_core PUBLIC "${CMAKE_CURRENT_SOURCE_DIR}/..")
target_compile_features(wf_core PUBLIC cxx_std_20)

# Same flags as the root build (repeating them is harmless when added from there).
target_compile_options(wf_core PRIVATE -Wall -Wextra -ffp-contract=off -fno-exceptions -fno-rtti)
target_compile_definitions(wf_core PUBLIC WEBRTC_WIN NOMINMAX)

# production_resampler_factory() has exactly one definition. With the vendored WebRTC it is
# the PushSincResampler wrapper; without it, linear_resampler.cpp provides the linear
# fallback under the same name. default_resampler_factory() stays linear either way, so the
# core unit tests keep measuring drift and framing against one fixed conversion.
if(WF_WITH_AEC3)
  target_sources(wf_core PRIVATE sinc_resampler.cpp)
  target_link_libraries(wf_core PUBLIC wf_webrtc)
  target_compile_definitions(wf_core PUBLIC WF_WITH_AEC3=1)
endif()
