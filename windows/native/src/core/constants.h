// Every DSP constant of the portable core, taken from the Wallfly Windows spec.
// Citations refer to "Wallfly-Windows-Spezifikation.md" (sections 3.3-3.6 and the
// constants appendix). Times are kept in 100 ns ticks, the unit of QPC timestamps.
#pragma once
#include <cstdint>

namespace wf::k {

// ---- clock ------------------------------------------------------------------
// QPC timestamps (pu64QPCPosition) are in 100 ns units.
inline constexpr int64_t kTicksPerSecond = 10'000'000;
inline constexpr int64_t kTicksPerMs     = 10'000;

// ---- framing (spec 3.3 "C-Bruecke", appendix "Abtastrate / Rahmen") ---------
// "Feste 16 kHz, mono, 160 Samples = 10 ms pro Aufruf."
inline constexpr int     kSampleRate   = 16000;
inline constexpr int     kFrameSamples = 160;
inline constexpr double  kFrameSeconds = 0.010;
// 10'000'000 / 16000 == 625 exactly: one sample is an integer number of ticks.
inline constexpr int64_t kTicksPerSample = kTicksPerSecond / kSampleRate;  // 625
inline constexpr int64_t kTicksPerFrame  = kTicksPerSample * kFrameSamples; // 100'000

// ---- AEC3 / NS configuration (spec 3.3, appendix "NS-Stufe", "Near-End-Masken")
// "mn_aec_set_ns(level) mit 0 = aus, 1-4 = 6/12/18/21 dB. Die App nutzt Stufe 2 = 12 dB."
inline constexpr int kNsLevel = 2;
// "suppressor.nearend_tuning.mask_lf und mask_hf auf {8, 12, 0.3}" (upstream differs).
inline constexpr float kNearendMaskLf[3] = {8.0f, 12.0f, 0.3f};
inline constexpr float kNearendMaskHf[3] = {8.0f, 12.0f, 0.3f};

// ---- latency compensation (spec 3.3 step 6, appendix "Latenzausgleich") -----
// "Die AEC3-Rahmung verzoegert um 128 Samples, NS um 96, zusammen 224 Samples.
//  Ausgabe-Sample k von Rahmen n gehoert zu Position n*160 + k - 224."
inline constexpr int kAec3DelaySamples = 128;
inline constexpr int kNsDelaySamples   = 96;
inline constexpr int kLatencyCompensationSamples = kAec3DelaySamples + kNsDelaySamples; // 224
// Zero capture frames needed on stop() to push the 224-sample tail out: ceil(224/160).
inline constexpr int kFlushFrames = (kLatencyCompensationSamples + kFrameSamples - 1) / kFrameSamples; // 2

// ---- reference handling (spec 3.3 steps 3+4, appendix) ----------------------
// "Fehlt die Referenz, wartet ein Mikrofon-Rahmen bis jetzt >= Rahmenende + 100 ms."
inline constexpr int64_t kReferenceWaitTicks = 100 * kTicksPerMs;   // 100 ms
// "Referenz aelter als 0,5 s wird verworfen."
inline constexpr double  kReferenceMaxAgeSeconds = 0.5;
inline constexpr int64_t kReferenceMaxAgeFrames  = 50;              // 0.5 s / 10 ms
// "Pro Quelle werden hoechstens 49 Rahmen gepuffert."
inline constexpr int kMaxBufferedFramesPerSource = 49;

// ---- drift (spec 3.3 "Drift", appendix "Drift") -----------------------------
// "Abweichungen unter 1 ms werden ignoriert."
inline constexpr int64_t kDriftIgnoreTicks = 1 * kTicksPerMs;
// "Darueber wird hoechstens 1 Sample pro 10 ms eingefuegt oder entfernt (interpoliert)."
inline constexpr int64_t kDriftAdjustIntervalTicks = 10 * kTicksPerMs;
inline constexpr int     kDriftMaxAdjustSamples    = 1;
// "Bei einem Sprung ueber 3 ms oder Drift ueber 20 ms: Konverter neu aufbauen und neu
//  ankern (Diskontinuitaet, kein AEC-Reset)."
inline constexpr int64_t kReanchorJumpTicks  = 3 * kTicksPerMs;
inline constexpr int64_t kReanchorDriftTicks = 20 * kTicksPerMs;

// ---- buffers (spec 3.5, appendix "Inbox / Ringpuffer") ----------------------
// "CaptureInbox je Quelle, max. 240 ms; bei Ueberlauf wird das Aelteste verworfen."
inline constexpr double kInboxSeconds = 0.240;
// "Ringpuffer (ca. 2 s, 1 024 Blockplaetze)" (spec 3.1 / 3.5).
inline constexpr int    kRingSlots      = 1024;
inline constexpr int    kRingSlotFrames = 480;   // 10 ms at 48 kHz; 1024 slots ~ 2 s
inline constexpr double kRingSeconds    = 2.0;

// ---- level meter (spec 3.6 "Pegelanzeige", appendix "Pegel") ----------------
// "RMS -> (20*log10(max(rms, 1e-5)) + 60) / 60, begrenzt auf 0-1, hoechstens alle 80 ms.
//  Nach 0,35 s ohne Signal faellt sie auf 0."
inline constexpr float   kLevelRmsFloor      = 1e-5f;
inline constexpr float   kLevelDbOffset      = 60.0f;
inline constexpr float   kLevelDbRange       = 60.0f;
inline constexpr int64_t kLevelIntervalTicks = 80 * kTicksPerMs;
inline constexpr int64_t kLevelDecayTicks    = 350 * kTicksPerMs;

// ---- archive anchors (spec 3.6 "Luecken werden nicht als Stille geschrieben") ---
// "Ein neuer Anker entsteht bei einer Abweichung von mehr als 1,5 Samples."
inline constexpr double kAnchorThresholdSamples = 1.5;
inline constexpr double kAnchorThresholdSeconds = kAnchorThresholdSamples / kSampleRate;

}  // namespace wf::k
