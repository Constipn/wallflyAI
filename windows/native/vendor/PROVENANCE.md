# Vendored WebRTC AEC3 + Noise Suppressor (Windows)

This directory carries the echo canceller and noise suppressor that `WallflyAudio.dll`
links when the build is configured with `-DWF_WITH_AEC3=ON`. Spec 3.3 requires Windows to
run **the same DSP as the Mac**, so nothing here was re-derived from upstream: `webrtc/` is
a byte-identical copy of the shipping Mac target.

## What is here

| Path | Contents |
|---|---|
| `webrtc/vendor/` | AEC3 and its base (258 files) |
| `webrtc/vendor-ns/` | Noise suppressor, its 256-point FFT, `LICENSE`, `PATENTS` (33 files) |
| `webrtc/ns-shim/` | Two forwarding headers that map the NS include paths onto the AEC3 tree (ours, deliberately not hashed upstream) |
| `webrtc/PROVENANCE.md`, `webrtc/PROVENANCE-NS.md` | The Mac provenance statements, copied verbatim |
| `webrtc/SHA256SUMS`, `webrtc/SHA256SUMS-NS` | The Mac manifests, copied verbatim (260 + 33 entries) |
| `webrtc/LICENSE`, `webrtc/JSONCPP-LICENSE` | WebRTC BSD-3 and the historical JsonCpp notice |
| `CMakeLists.txt` | The only Windows-specific part: which files are compiled, and with which flags |
| `SHA256SUMS` | Per-file manifest of everything in this directory (300 entries, 1.60 MiB) |

299 files, ~1.60 MiB. Nothing under `webrtc/` is edited.

## Chain of custody

**Immediate source** — `reference-macos/Sources/WebRTCAEC/` (Wallfly 1.13.0 macOS target),
copied 1:1 on 2026-11-01, keeping the relative layout.

**Upstream, as recorded by the Mac provenance files:**

- **AEC3** — <https://github.com/Enaium/webrtc-aec3>, commit
  `2cec2f52e26646f93bd2d5498bbabf59cba18da9` (retrieved 2026-10-18). BSD 3-Clause.
  Upstream does not pin the original WebRTC revision; that limitation is inherited.
- **Noise suppressor** — <https://webrtc.googlesource.com/src>, commit
  `8505a9838ea91c66c96c173d30cd66f9dbcc7548` (branch `refs/branch-heads/6422`, M125,
  commit date 2024-04-23; retrieved 2026-10-19). BSD 3-Clause plus the WebRTC patent grant.

**Verification.** Every vendored file was checked against the two Mac manifests after the
copy: **260/260** entries in `webrtc/SHA256SUMS` and **33/33** in `webrtc/SHA256SUMS-NS`
match byte for byte — **no mismatches, nothing missing**. The only files under `webrtc/`
that no manifest covers are the two `ns-shim/` forwarding headers (which upstream
`PROVENANCE-NS.md` explicitly describes as "our forwarding headers, not hashed"), the two
`PROVENANCE*.md` files and the two manifests themselves.

Re-check at any time from this directory:

```powershell
sha256sum -c SHA256SUMS                        # everything here
cd webrtc; sha256sum -c SHA256SUMS             # Mac manifest (paths Sources/WebRTCAEC/...)
```

**Independent cross-check.** Before the Mac tree was available, the upstream archives were
fetched directly and their layout compared against it. They are evidence only — `_downloads/`
is gitignored and **nothing in it is compiled, included or linked**:

| URL | Bytes | SHA-256 |
|---|---|---|
| `https://github.com/Enaium/webrtc-aec3/archive/2cec2f5.zip` | 1 060 318 | `92125077c71e126df16002a631e2527ece6803d7750ca48893712fda97bca8b6` |
| `https://webrtc.googlesource.com/src/+archive/8505a98/modules/audio_processing/ns.tar.gz` | 20 018 | `88fd11aae57fc69ab9d65fef1140162f3498ca9bbb2261edccc677b9356aa91c` |
| `https://webrtc.googlesource.com/src/+archive/8505a98/common_audio/third_party/ooura/fft_size_256.tar.gz` | 4 493 | `fbcae385d5a08b7a1826229039099136694f9700085c613ad5c828a358422815` |

Note that googlesource `+archive` tarballs are generated on demand, so the archive-level
digests above are not guaranteed reproducible; the per-file digests in
`webrtc/SHA256SUMS-NS` are the authoritative record. The GitHub zip digest is stable.

## Windows build, against the Mac `Package.swift`

The Mac target definition (`reference-macos/Package.swift`, the `WebRTCAEC` target) is
mirrored by `CMakeLists.txt`. Differences, all deliberate:

| Mac | Windows | Why |
|---|---|---|
| `WEBRTC_MAC`, `WEBRTC_POSIX` | `WEBRTC_WIN` | platform |
| `*_sse2/_sse/_avx2` **excluded** | **compiled** (`*_avx2.cc` with `-mavx2 -mfma`) | spec 3.3: on x64 they must be compiled so the program links. They never execute — `webrtc/vendor/base/rtc_base/cpu_info.h:21` is a stub whose `Supports()` always returns false, so `DetectOptimization()` returns `kNone` and AEC3 runs the same scalar path as on arm64. **Do not replace that stub with real CPU detection.** |
| `*_neon.cc`, `*_mips.cc` compiled or excluded by arch | dropped | cannot target x86-64 |
| jsoncpp + `echo_canceller3_config_json.cc` + `rtc_base/strings/json.cc` compiled | dropped | unreachable from the bridge, and the only vendored code that throws; dropping it keeps the DLL on `-fno-exceptions` |
| exceptions on (SwiftPM default) | `-fno-exceptions`, except `rtc_base/experiments/field_trial_parser.cc` | that one file wraps `std::stoi/stod/stof` in `try/catch`, is reached from `echo_canceller3.cc`, and is compiled with `-fexceptions`; the exception never leaves the translation unit |
| — | `-DRTC_DISABLE_METRICS` | turns the `RTC_HISTOGRAM_*` recording into no-ops. Histograms only; `mn_aec_metrics` reads `EchoCanceller3::GetMetrics()`, which is unaffected |
| — | `-include <array>` on C++ sources | `audio_buffer.cc`, `ns_fft.cc` and `noise_suppressor.h` use `std::array` without including it, relying on `<span>`/`<vector>`. The libc++ in llvm-mingw is newer than the macOS one and only forward-declares it there. A force-include fixes every case without touching a vendored byte |
| — | links `winmm` | `rtc_base/time_utils.cc` calls `timeGetTime()`; reached via `logging.cc` and `audio_frame.cc`. One system DLL beyond the plan's kernel32/ole32/mmdevapi/avrt/ucrt list, present on every Windows install |
| — | `-w` on vendored sources | third-party code is not kept warning-clean; our own targets stay `-Wall -Wextra` |

Shared with the Mac: `WEBRTC_APM_DEBUG_DUMP=0`, C++20, static linking, the include search
order (`vendor`, `vendor/base`, `vendor/base/rtc_base`, `vendor/api`,
`vendor/audio_processing`, `vendor/absl`, `vendor/base/system_wrappers/include`,
`vendor/common_audio`, `vendor-ns`, `ns-shim` — the order matters, the NS upstream paths
resolve through `ns-shim` last). `-ffp-contract=off` keeps the arithmetic off the fused path.

## Result

`src/aec/AECBridge.cpp` is the Mac `AECBridge.cpp` with the DSP body unchanged — same
`EchoCanceller3Config` (upstream defaults except `suppressor.nearend_tuning.mask_lf` and
`mask_hf` = `{8, 12, .3}`), same capture order (`AnalyzeCapture` → high-pass → `NS.Analyze`
→ `AEC3.ProcessCapture` → `NS.Process`), same input conditioning (clamp to [-1, 1],
NaN/Inf → 0, × 32768), no AGC, no fixed delay, no headphone bypass.

On the Mac's own synthetic corpus (ported in `tests/aec/test_aec_erle.cpp`) this build
measures, against the Mac reference in the same units:

| echo delay | Windows x64 | Mac arm64 | Δ |
|---|---|---|---|
| 16 ms | 63.13 dB | 63.13 dB | 0.00 dB |
| 80 ms | 69.56 dB | 69.57 dB | 0.01 dB |
| 180 ms | 68.14 dB | 68.15 dB | 0.01 dB |

Well inside the ±2 dB the plan allows, which is the evidence that the scalar path, the
CPU-detection stub and `-ffp-contract=off` together reproduce the Mac DSP.
