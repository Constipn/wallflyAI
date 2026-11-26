#ifndef MEETING_NOTES_AEC_H
#define MEETING_NOTES_AEC_H
#ifdef __cplusplus
extern "C" {
#endif
// Serial use only. Float PCM in [-1, 1], exactly 160 samples at 16 kHz mono.
typedef struct MNAEC MNAEC;
MNAEC *mn_aec_create(void);
int mn_aec_render(MNAEC *, const float *samples);
int mn_aec_capture(MNAEC *, const float *samples, float *output);
// 0 = off (default), 1..4 = WebRTC NS 6/12/18/21 dB; keeps the echo filter. Returns 0 on invalid level.
int mn_aec_set_ns(MNAEC *, int level);
// AEC3 metrics: full-band ERLE (dB) and estimated render-to-capture delay (ms, 4-ms blocks).
int mn_aec_metrics(MNAEC *, double *erle, int *delay_ms);
int mn_aec_reset(MNAEC *);
void mn_aec_destroy(MNAEC *);
#ifdef __cplusplus
}
#endif
#endif
