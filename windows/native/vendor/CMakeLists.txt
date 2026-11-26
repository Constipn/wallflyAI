# wf_webrtc: the vendored WebRTC AEC3 + Noise Suppressor, built statically.
#
# The sources under webrtc/ are a byte-identical copy of the Mac target
# Sources/WebRTCAEC (see webrtc/PROVENANCE.md and webrtc/PROVENANCE-NS.md); every file is
# covered by webrtc/SHA256SUMS or webrtc/SHA256SUMS-NS. Nothing in webrtc/ is edited - the
# only Windows-specific adaptation lives in this file (file selection and flags) and in the
# include search order, which mirrors the Mac Package.swift headerSearchPath list.
#
# Differences from the Mac target, all deliberate (see PROVENANCE-WINDOWS.md):
#   * WEBRTC_WIN instead of WEBRTC_MAC/WEBRTC_POSIX.
#   * The *_sse2/_sse/_avx2 files are COMPILED here (the Mac excludes them for arm64).
#     Spec 3.3: on x64 they must be compiled so the program links. They are never executed,
#     because vendor/base/rtc_base/cpu_info.h is a stub whose Supports() always returns
#     false, so AEC3 selects the same scalar path as on the Mac. Do not "fix" that stub.
#   * *_neon.cc / *_mips.cc are dropped - they cannot compile for x86-64.
#   * jsoncpp and the two JSON config readers are dropped: nothing the bridge uses needs
#     them, and jsoncpp 0.6 throws, which would force -fexceptions on the whole build.

# Every .cc/.c/.cpp under the two vendored roots, exactly like Package.swift's sources().
file(GLOB_RECURSE WF_WEBRTC_SOURCES CONFIGURE_DEPENDS
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/*.cc"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/*.c"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/*.cpp"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor-ns/*.cc"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor-ns/*.c"
)

# Architectures we do not target.
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "_neon\\.cc$")
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "_mips\\.cc$")
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "cpu_features_android\\.c$")
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "cpu_features_linux\\.c$")
# JSON config plumbing: unreachable from the bridge and the only code here that throws.
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "/jsoncpp/")
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "echo_canceller3_config_json\\.cc$")
list(FILTER WF_WEBRTC_SOURCES EXCLUDE REGEX "/strings/json\\.cc$")

add_library(wf_webrtc STATIC ${WF_WEBRTC_SOURCES})

# Include search order mirrors Package.swift's headerSearchPath list. It matters: the NS
# sources use upstream paths ("api/array_view.h", "modules/audio_processing/audio_buffer.h")
# that exist in neither vendored root and resolve through ns-shim/ last.
target_include_directories(wf_webrtc SYSTEM PUBLIC
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/base"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/base/rtc_base"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/api"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/audio_processing"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/absl"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/base/system_wrappers/include"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/common_audio"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor-ns"
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/ns-shim"
)

target_compile_definitions(wf_webrtc PUBLIC
  WEBRTC_WIN
  WEBRTC_APM_DEBUG_DUMP=0   # also set in vendor/build_config.h; kept explicit like Package.swift
  RTC_DISABLE_METRICS       # histogram recording only - no effect on the signal path
)
target_compile_features(wf_webrtc PUBLIC cxx_std_20)

# winmm: vendor/base/rtc_base/time_utils.cc calls timeGetTime() on Windows, and logging.cc
# and audio_frame.cc pull it in. This is one system library beyond the set the build plan
# lists (kernel32/ole32/mmdevapi/avrt/ucrt); winmm.dll ships with every Windows install, so
# it costs nothing at deployment. The alternative - dropping the vendored logging and time
# sources - would leave RTC_LOG call sites all over AEC3 undefined.
target_link_libraries(wf_webrtc PUBLIC winmm)

# Third-party code is not ours to keep warning-clean; our own targets stay -Wall -Wextra.
# -w comes after the global flags on the command line, so it wins for these files only.
#
# -include array: several vendored sources (audio_processing/audio_buffer.cc,
# vendor-ns/.../ns_fft.cc, noise_suppressor.h) use std::array without including <array>,
# relying on <span>/<vector> to pull it in. The libc++ shipped with llvm-mingw is newer than
# the one on macOS and only forward-declares std::array there, so those files do not compile
# on their own. Force-including the standard header fixes every case at once and keeps all
# vendored files byte-identical to the manifests - no upstream source is edited. C++ only:
# splitting_filter_c.c is C and must not see a C++ header.
target_compile_options(wf_webrtc PRIVATE -w "$<$<COMPILE_LANGUAGE:CXX>:-include;array>")

# AVX2 files need the ISA enabled to compile their intrinsics. They still never run: the
# cpu_info stub reports every ISA as unsupported, so DetectOptimization() returns kNone.
foreach(_src IN LISTS WF_WEBRTC_SOURCES)
  if(_src MATCHES "_avx2\\.cc$")
    set_source_files_properties("${_src}" PROPERTIES COMPILE_OPTIONS "-mavx2;-mfma")
  endif()
endforeach()

# field_trial_parser.cc is the one compiled file that needs exceptions: its
# ParseTypedParameter<int/double/float> wrap std::stoi/stod/stof in try/catch to turn a
# malformed value into std::nullopt. It is reachable - echo_canceller3.cc parses field
# trials, and our Environment supplies NoOpFieldTrials, which returns an empty string that
# std::stoi rejects by throwing. The exception is caught inside this translation unit and
# never crosses into the rest of the DLL, which stays on -fno-exceptions.
set_source_files_properties(
  "${CMAKE_CURRENT_SOURCE_DIR}/webrtc/vendor/rtc_base/experiments/field_trial_parser.cc"
  PROPERTIES COMPILE_OPTIONS "-fexceptions")

list(LENGTH WF_WEBRTC_SOURCES _wf_webrtc_count)
message(STATUS "wf_webrtc: compiling ${_wf_webrtc_count} vendored WebRTC sources")
