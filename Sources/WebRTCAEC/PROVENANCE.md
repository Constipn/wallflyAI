# WebRTC AEC3

Vendored from https://github.com/Enaium/webrtc-aec3 at commit
`2cec2f52e26646f93bd2d5498bbabf59cba18da9` (retrieved 2026-10-18).
The complete upstream `src/` tree is preserved as `vendor/`, without changes.
BSD 3-Clause; see LICENSE and individual source headers. Upstream does not
pin the original WebRTC revision. This provenance limitation is retained.

SwiftPM compiles the standalone sources statically with C++20 and macOS/POSIX
defines. x86 SIMD implementations are excluded for the Apple Silicon target.
No third-party application binary, CMake, or build-time download is used.
`AECBridge.cpp` is our C ABI wrapper: normalized float PCM is scaled to WebRTC's
S16 float range; render analysis precedes capture analysis, high-pass and AEC3.
No AGC and no hardcoded acoustic delay.

Verify vendored bytes with `shasum -a 256 -c Sources/WebRTCAEC/SHA256SUMS`
from the repository root. The manifest covers upstream source and license.

The bundled JsonCpp snapshot carries the 2007–2010 Baptiste Lepilleur notice.
`JSONCPP-LICENSE` is the matching historical license retrieved from
https://raw.githubusercontent.com/open-source-parsers/jsoncpp/678d65e7ad4596f521c1b56638c7bf5a6b79e490/LICENSE
(tag svn-release-0.6.0-rc2). Source header notices, WebRTC and JsonCpp licenses
are included in the signed app's Resources/Licenses.

Our wrapper uses upstream `EchoCanceller3Config` defaults except
`suppressor.nearend_tuning` mask_lf/mask_hf = {8, 12, .3} (upstream
{1.09, 1.1, .3}/{.1, .3, .3}). The normal-state tuning and the dominant-nearend
SNR threshold (30) are upstream again since 2026-10-19; the former permissive
normal tuning (ENR 6/10, EMR .5) and SNR 5 let music and echo leak into the
near-end transcript. A suppressor sweep on the synthetic corpus showed the
near-end override alone keeps the double-talk thresholds. WebRTC noise
suppression is vendored separately; see PROVENANCE-NS.md. Physical-room
acceptance remains mandatory.
