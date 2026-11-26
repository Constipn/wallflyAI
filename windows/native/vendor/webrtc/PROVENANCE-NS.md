# WebRTC Noise Suppression

Vendored from https://webrtc.googlesource.com/src at commit
`8505a9838ea91c66c96c173d30cd66f9dbcc7548` (branch `refs/branch-heads/6422`,
M125; commit date 2024-04-23), retrieved 2026-10-19 by sparse checkout.
BSD 3-Clause plus the WebRTC patent grant; see `vendor-ns/LICENSE`,
`vendor-ns/PATENTS` and the individual source headers.

Files, unmodified and in upstream layout under `vendor-ns/`:

- `modules/audio_processing/ns/*` (all `.cc`/`.h` and `BUILD.gn`, without
  `noise_suppressor_unittest.cc`)
- `common_audio/third_party/ooura/fft_size_256/fft4g.{cc,h}`
- `LICENSE`, `PATENTS`

Includes resolved via `ns-shim/` (our forwarding headers, not hashed):
`api/array_view.h` maps to the vendored AEC3 `rtc_base/array_view.h`
(`rtc::ArrayView` as `std::span`) plus `rtc_base/checks.h`, which upstream
`array_view.h` provides transitively; `modules/audio_processing/audio_buffer.h`
maps to the vendored AEC3 `AudioBuffer`. At 16 kHz there is a single band.

`AECBridge.cpp` creates `NoiseSuppressor(NsConfig, 16000, 1)` when enabled
(`mn_aec_set_ns`, 6/12/18/21 dB; off by default in the C API, 12 dB in the app)
and follows upstream `audio_processing_impl.cc` order: AnalyzeCapture,
high-pass, NS Analyze, AEC3 ProcessCapture, NS Process. NS adds 96 samples of
latency (verified by test).

Verify with `shasum -a 256 -c Sources/WebRTCAEC/SHA256SUMS-NS` from the
repository root.
