// C bridge to the vendored WebRTC AEC3 + NS, exactly as specified in spec 3.3.
// The implementation is ported 1:1 from the Mac sources (Sources/WebRTCAEC/AECBridge.cpp);
// until it lands, tests/core/mock_aec.cpp provides a recording mock with the same symbols.
#pragma once

// Spec 3.3 "C-Bruecke": fixed 16 kHz mono, 160 samples = 10 ms per call.
#define MN_AEC_FRAME 160
#define MN_AEC_RATE  16000

#ifdef __cplusplus
extern "C" {
#endif

typedef struct mn_aec mn_aec;

mn_aec* mn_aec_create(void);
void    mn_aec_destroy(mn_aec* aec);

// AnalyzeRender on the reference (playback) frame.
void    mn_aec_render(mn_aec* aec, const float* frame160);

// AnalyzeCapture -> highpass -> NS.Analyze -> AEC3.ProcessCapture -> NS.Process.
void    mn_aec_capture(mn_aec* aec, const float* in160, float* out160);

// 0 = off, 1..4 = 6/12/18/21 dB. The app uses level 2 (12 dB).
void    mn_aec_set_ns(mn_aec* aec, int level);

// ERLE in dB and the estimated delay in ms.
void    mn_aec_metrics(mn_aec* aec, double* erle_db, int* delay_ms);

// Full filter reset. Spec 3.3 step 5: only on mic device switch or mic format change.
void    mn_aec_reset(mn_aec* aec);

#ifdef __cplusplus
}
#endif
