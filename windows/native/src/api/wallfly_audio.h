// WallflyAudio.dll - C ABI v1, consumed by the .NET app via P/Invoke.
//
// Contract summary (implementation plan, "Phase 1 - Nativer Audio-Kern"):
//   * Pull model. The DLL owns an SPSC frame queue (>= 2 s), an event queue and one
//     auto-reset Win32 event. A .NET background thread waits on the event and drains
//     both queues in blocks; the DLL never calls back into managed code.
//   * Every struct is POD with sequential layout, caller allocated, no pointers inside,
//     fixed-size wchar_t arrays instead of strings. The .NET side mirrors this header
//     1:1; tests/session/test_abi_layout.cpp pins sizeof/offsetof of all of them.
//   * Enumerations are plain uint32_t constants, never C enums, so the managed mirror
//     cannot disagree about the underlying type.
//   * All functions return int32_t: 0 (WF_OK) on success, a negative WF_E_* on failure.
//     Out parameters are only written on success unless stated otherwise.
//   * Additions inside ABI v1 are strictly additive (new functions, constants and new
//     structs, never a change to an existing struct): wf_session_get_origin, WF_GAP_NONE,
//     the "fallback:" detail of WF_EVENT_DEVICE_ACQUIRED,
//     WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS; and (2026-11-23) WfDiagnostics +
//     wf_session_get_diagnostics, wf_power_requests_active, WF_POWER_REQUEST_*,
//     WF_FAKE_SIGNAL_SESSION_START_FAILS; and (2026-11-23, AP3 device changes)
//     WF_EVENT_WARNING, the "mic:"/"render:" event details, four fields appended to
//     WfDiagnostics, WF_FAKE_SIGNAL_12..18, WF_FAKE_QUERY_* + wf_fake_query. WfMetrics has no size field and is written whole
//     into a caller-allocated struct, so it can never grow; WfDiagnostics carries its own
//     size and is the place for every later counter.
//
// Frame / event ordering. Frames and events travel in two queues with ONE producer, the
// DSP thread. Within each 10 ms tick it queues that tick's frames before it commits that
// tick's events, and WF_EVENT_STOPPED is queued after the last frame of the session.
// WfFrame and WfEvent have no spare bytes for a shared tick counter, so a consumer that
// needs "no event overtakes a frame queued before it" reads in this order per wake-up:
//   1. snapshot a block of events (wf_session_read_events),
//   2. drain the frame queue until it is empty (wf_session_read_frames),
//   3. only then process the event block from step 1.
// Every frame queued before an event of that block has been read in step 2, because the
// DSP thread queued it before the event became visible. (Draining frames first and events
// second does NOT give this guarantee: frames queued between the two reads would be read
// in the next wake-up, after their events - including after WF_EVENT_STOPPED.)
// WfEvent.sequence numbers the events; frames carry no sequence number.
//
// Audio format everywhere in this ABI: 16 kHz, mono, float32 in [-1, 1], 160 samples
// (10 ms) per frame. Times are seconds of meeting time (see WfFrame.offset_sec).
#pragma once
#include <stdint.h>

#if defined(WF_BUILDING_DLL)
#  define WF_API __declspec(dllexport)
#else
#  define WF_API __declspec(dllimport)
#endif

#ifdef __cplusplus
extern "C" {
#endif

#define WF_ABI_VERSION 1u

// Samples per frame / sample rate. Fixed by the AEC bridge (spec 3.3).
#define WF_FRAME_SAMPLES 160u
#define WF_SAMPLE_RATE   16000u

// ---------------------------------------------------------------------------
// Return codes
// ---------------------------------------------------------------------------
#define WF_OK                  0
#define WF_E_INVALID_ARG      -1   // null pointer, unknown enum value, bad config
#define WF_E_STATE            -2   // wrong session state for this call
#define WF_E_NOT_FAKE         -3   // wf_fake_* called on a real session
#define WF_E_FAIL             -4   // internal failure (see wf_session_read_events)
#define WF_E_BUFFER_TOO_SMALL -5   // *count holds the number of items required
#define WF_E_UNSUPPORTED      -6   // the requested mode is not available on this machine

// ---------------------------------------------------------------------------
// Capability flags returned by wf_capabilities()
// ---------------------------------------------------------------------------
#define WF_CAP_PROCESS_LOOPBACK 0x1u  // ActivateAudioInterfaceAsync + PROCESS_LOOPBACK (Win11 >= 20348)
#define WF_CAP_AEC3             0x2u  // the vendored WebRTC AEC3 + NS is linked in
#define WF_CAP_FAKE             0x4u  // wf_session_create_fake and the wf_fake_* path exist

// ---------------------------------------------------------------------------
// Enumerations (plain uint32_t constants)
// ---------------------------------------------------------------------------

// Data flow for wf_devices_list / wf_audio_sessions_list.
#define WF_FLOW_RENDER  0u
#define WF_FLOW_CAPTURE 1u
#define WF_FLOW_ALL     2u

// WfFrame.source / WfEvent.source.
#define WF_SOURCE_ME        0u  // the cleaned microphone
#define WF_SOURCE_OTHERS    1u  // the meeting audio that is stored and transcribed
#define WF_SOURCE_REFERENCE 2u  // echo reference only, never emitted as frames
#define WF_SOURCE_SESSION   3u  // the session itself (Degraded, Stopped, Error, ...)

// WfSessionConfig.others_mode.
#define WF_OTHERS_SYSTEM_EXCLUDE  0u  // process loopback, exclude own process tree:
                                      // one stream is both Others and the echo reference
#define WF_OTHERS_PROCESS_INCLUDE 1u  // process loopback, include target_pid's tree as
                                      // Others; a second exclude-mode stream is the
                                      // reference and is never emitted as frames
#define WF_OTHERS_DEVICE_LOOPBACK 2u  // classic loopback on one render endpoint (it
                                      // delivers nothing while the endpoint is idle)

// WfSessionConfig.keepalive_mode (spec 3.3 "Was die Windows-Aufnahme liefern muss" 2.).
// Since 2026-11-01 the silent render keep-alive runs in EVERY mode, whatever this field
// says: process loopback does deliver zeros while the machine is silent, but when another
// process starts playing on the idle render endpoint it delivers nothing for 0.43-0.52 s
// while the endpoint wakes up (no flags, no catch-up burst), which was a hole in Others and
// in the echo reference, and past 0.5 s a watchdog rebuild. With our silent stream keeping
// the endpoint awake that onset costs ~20-60 ms. Price: when the endpoint is idle at
// wf_session_start, the first buffers of BOTH streams arrive ~0.4 s later (the wake-up
// happens then). The field now only selects the reference watchdog of spec 3.3.
#define WF_KEEPALIVE_NONE   0u  // default: plain 0.5 s watchdog on the reference stream
#define WF_KEEPALIVE_RENDER 1u  // spec 3.3 gate: the reference watchdog fires only while the
                                // render endpoint carries signal or the keep-alive clock
                                // stalls (always so in mode 2)
#define WF_KEEPALIVE_CLOCK  2u  // clock-driven zeros. Reserved and NOT implemented:
                                // wf_session_create returns WF_E_UNSUPPORTED, because
                                // synthesising the reference hides real stream failures
                                // and nothing measured so far needs it.

// WfSessionConfig.timestamp_mode (spec 3.3 1.).
#define WF_TIMESTAMP_RAW_QPC  0u  // default: use pu64QPCPosition as delivered. Measured
                                  // 2026-11-01: also the process-loopback QPC is real time
                                  // across an endpoint wake-up stall, so a hole stays a hole
                                  // (WF_GAP_TIMESTAMP_DISCONTINUITY) at its true position
#define WF_TIMESTAMP_SMOOTHED 1u  // low-pass the QPC anchor (tau ~ 2 s) before the 3 ms rules

// WfMetrics.state.
#define WF_STATE_IDLE           0u  // created, not started
#define WF_STATE_STARTING       1u  // streams opening, no first buffer yet
#define WF_STATE_RUNNING        2u  // mic and Others both delivering
#define WF_STATE_DEGRADED       3u  // mic gave up, recording continues Others-only
#define WF_STATE_DRIVER_BLOCKED 4u  // 2 driver steps hung; "Audio driver blocked"
#define WF_STATE_OTHERS_FAILED  5u  // the Others channel gave up; the caller should stop
#define WF_STATE_STOPPING       6u
#define WF_STATE_STOPPED        7u

// WfMetrics.ref_mode_actual - which stream currently feeds the echo canceller.
#define WF_REF_MODE_NONE            0u
#define WF_REF_MODE_PROCESS_EXCLUDE 1u
#define WF_REF_MODE_DEVICE_LOOPBACK 2u

// WfFrame.flags (bit mask).
#define WF_FRAME_UNFILTERED        0x1u  // ran with a zero reference (spec 3.3 step 3)
#define WF_FRAME_FIRST_AFTER_GAP   0x2u  // first frame after a gap in this source
#define WF_FRAME_SYNTHETIC_SILENCE 0x4u  // reserved, never set: the stop flush only pushes
                                         // the real cleaned tail out (Mac flushEchoTail);
                                         // its zero frames carry no samples of their own

// WfEvent.kind.
#define WF_EVENT_DEVICE_ACQUIRED     1u   // first buffer of a (re)started stream. On
                                          // WF_SOURCE_ME the detail is
                                          // "fallback:<reason>" when the configured
                                          // mic_device_id could not be opened and the
                                          // system default capture endpoint was opened in
                                          // the same attempt instead (spec 3.1; never after
                                          // E_ACCESSDENIED, which stops every retry);
                                          // hresult is then the configured endpoint's
                                          // failure. <reason> is "selected microphone
                                          // missing" or "<friendly name> unavailable".
                                          // Otherwise (2026-11-23) "mic:<friendly name>".
                                          // On WF_SOURCE_OTHERS in mode 2: "render:<friendly
                                          // name>", or "fallback:<reason>|render:<name>" when
                                          // the pinned render_device_id could not be opened
                                          // and the default render endpoint was opened
                                          // instead (<reason> "selected output missing" or
                                          // "<name> unavailable"; hresult = its failure).
                                          // Process-loopback streams: empty. See "Event
                                          // detail" below
#define WF_EVENT_DEVICE_LOST         2u   // AUDCLNT_E_DEVICE_INVALIDATED or equivalent;
                                          // detail "mic:<name>" / "render:<name>" of the
                                          // endpoint that was lost (2026-11-23)
#define WF_EVENT_DEVICE_SWITCHED     3u   // debounced default-device / explicit switch;
                                          // detail "mic:<name>" / "render:<name>" of the
                                          // endpoint switched TO (2026-11-23). Source ME: the
                                          // mic; OTHERS: mode 2 moved its loopback (one
                                          // WF_GAP_RESTART follows, carrying the hand-over
                                          // hole); REFERENCE: modes 0/1 moved only the
                                          // keep-alive and the echo path (no Others gap)
#define WF_EVENT_FORMAT_CHANGED      4u  // the mic ENDPOINT changed its format (PKEY_AudioEngine_
                                         // DeviceFormat/OEMFormat of that endpoint, confirmed
                                         // against the open-time format), coalesced 50 ms,
                                         // one rebuild, one filter reset
#define WF_EVENT_GAP                 5u   // WfEvent.reason holds a WF_GAP_* value. Gaps of
                                          // one source and reason that start within
                                          // previous start + duration + 11 ms are merged
                                          // into one event before they are queued (Mac
                                          // CaptureEventInbox.append), per 10 ms DSP tick
#define WF_EVENT_DEGRADED            6u   // 3 start attempts failed; Others-only from here
#define WF_EVENT_REFERENCE_MISSING   7u   // app mode: no echo reference, mic runs unfiltered
#define WF_EVENT_REFERENCE_RESTORED  8u
#define WF_EVENT_WATCHDOG_RECOVERY   9u   // 0.5 s without a callback -> stream rebuilt
#define WF_EVENT_RESTARTING         10u   // a (re)start attempt is being launched
#define WF_EVENT_PERMISSION_DENIED  11u   // E_ACCESSDENIED; all retries stop
#define WF_EVENT_DRIVER_BLOCKED     12u   // 2 orphaned driver steps
#define WF_EVENT_FALLBACK_DEVICE_LOOPBACK 13u  // process loopback unavailable, mode 2 used
#define WF_EVENT_CLOCK_JITTER_WARNING     14u  // timestamp jitter p95 > 3 ms
#define WF_EVENT_OTHERS_FAILED      15u   // Others gave up after 0.5/1/2 s restarts
#define WF_EVENT_STOPPED            16u   // last event of a session; queues are complete
#define WF_EVENT_ERROR              17u   // unrecoverable; WfEvent.hresult says why
#define WF_EVENT_WARNING            18u   // (2026-11-23) NOT fatal, the recording goes on;
                                          // detail says what. Today only the silent
                                          // keep-alive render stream (source OTHERS):
                                          // "keepalive:start failed 0x%08X|render:<name>|
                                          // retry in <s> s" / "...|gave up" or
                                          // "keepalive:stopped 0x%08X|..." (its thread ended
                                          // on its own); hresult = the failure. The session
                                          // retries 0.5 / 1 / 2 s, then gives up until the
                                          // render endpoint changes

// Event detail grammar (2026-11-23): fields separated by '|', the first field is the primary
// one. "mic:<friendly name>" / "render:<friendly name>" name the flow and the endpoint;
// "fallback:<reason>" keeps its pre-existing meaning (on WF_SOURCE_ME it stays the WHOLE
// detail, exactly as before, so "fallback:" + reason parses unchanged). Names are the
// endpoint's PKEY_Device_FriendlyName ("unknown endpoint" when unreadable), truncated to
// the 128 characters of WfEvent.detail.

// WfEvent.reason. 0..5 match wf::GapReason of the DSP core; 6 is assigned by the session
// layer (the core reports both buffer overflows as InboxOverflow). Every event that is not
// a WF_EVENT_GAP carries WF_GAP_NONE (7), so 0 always means a real inbox overflow.
#define WF_GAP_INBOX_OVERFLOW          0u  // audio lost before the DSP got to it (Mac "Input
                                           // buffer overflowed"): since 2026-11-23 the
                                           // capture ring (1024 slots, ~10 s) was full and
                                           // the capture thread had to drop it. Reported
                                           // ONCE per lost interval and source, at its true
                                           // position - no TimestampDiscontinuity twin. A
                                           // DSP stall shorter than the ring loses nothing.
#define WF_GAP_TIMESTAMP_DISCONTINUITY 1u  // clock jump / DATA_DISCONTINUITY
#define WF_GAP_LATE_FRAME              2u  // late or duplicate frame dropped
#define WF_GAP_DEVICE_LOST             3u
#define WF_GAP_RESTART                 4u
#define WF_GAP_WATCHDOG_RECOVERY       5u
#define WF_GAP_PROCESSING_OVERFLOW     6u  // the echo processor's 49-frame store dropped its
                                           // oldest frame (Mac "Processing buffer overflowed").
                                           // Source ME for microphone frames, REFERENCE for
                                           // echo reference frames (the Others frames of that
                                           // time were already delivered)
#define WF_GAP_NONE                    7u  // not a gap: WfEvent.reason of every event whose
                                           // kind is not WF_EVENT_GAP

// wf_fake_push source selector.
#define WF_FAKE_SRC_MIC       0u
#define WF_FAKE_SRC_OTHERS    1u
#define WF_FAKE_SRC_REFERENCE 2u

// wf_fake_signal signal selector. `source` is a WF_FAKE_SRC_* value, `hresult` is used
// by the signals that carry one.
#define WF_FAKE_SIGNAL_DEVICE_LOST       1u  // the running stream reports device loss now
#define WF_FAKE_SIGNAL_FORMAT_CHANGE     2u  // the mic endpoint really changes its format
                                             // (48 <-> 44.1 kHz) and reports it with a
                                             // PKEY_AudioEngine_DeviceFormat notification
#define WF_FAKE_SIGNAL_NO_FIRST_PACKET   3u  // starts succeed but deliver nothing (2.5 s timeout)
#define WF_FAKE_SIGNAL_START_FAILS       4u  // the start step fails with `hresult`
#define WF_FAKE_SIGNAL_CLEAR             5u  // clears signals 3, 4 and 10 for that source
                                             // (and 17 for WF_FAKE_SRC_OTHERS)
#define WF_FAKE_SIGNAL_HANG_STEP         6u  // the next driver step never completes (orphan)
#define WF_FAKE_SIGNAL_DEFAULT_DEVICE_CHANGED 7u  // IMMNotificationClient default change
#define WF_FAKE_SIGNAL_STREAM_ERROR      8u  // the running stream fails with `hresult`
#define WF_FAKE_SIGNAL_PROPERTY_CHANGED  9u  // IMMNotificationClient::OnPropertyValueChanged
                                             // without a format change. `source` MIC: on the
                                             // mic's endpoint, OTHERS: on the render endpoint;
                                             // `hresult` 0: a non-format key (the one measured
                                             // on the render endpoint while audio plays),
                                             // 1: PKEY_AudioEngine_DeviceFormat
#define WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS 10u  // `source` MIC only: from the next start on,
                                             // the CONFIGURED mic endpoint (non-empty
                                             // mic_device_id) fails to open with `hresult`
                                             // (0: HRESULT_FROM_WIN32(ERROR_NOT_FOUND), the
                                             // endpoint is missing); the system default still
                                             // opens, so the start falls back to it (spec
                                             // 3.1). Cleared by WF_FAKE_SIGNAL_CLEAR
#define WF_FAKE_SIGNAL_SESSION_START_FAILS 11u  // the next wf_session_start fails with
                                             // WF_E_FAIL after it took the power request
                                             // (as when a session thread cannot be
                                             // created); `source` and `hresult` are ignored
// (2026-11-23) Fake endpoints. A fake session simulates four render and four capture
// endpoints, index 0..3: render 0 is "{0.0.0.00000000}.{fake-default-render}", render i>0
// "{0.0.0.00000000}.{fake-render-<i>}", friendly name "Fake Speakers <i>"; capture 0 is
// "{0.0.1.00000000}.{fake-default-capture}", capture i>0 "{0.0.1.00000000}.{fake-capture-<i>}",
// friendly name "Fake Microphone <i>". All start ACTIVE; index 0 is the default of every
// role. The signals below change that state and deliver the IMMNotificationClient callbacks
// a real system sends, through the session's production notification filter. `source` is
// ignored by signals 12-18.
#define WF_FAKE_SIGNAL_DEFAULT_RENDER_CHANGED       12u  // `hresult` = render index: the
                                             // console AND multimedia default render endpoint
                                             // move there (OnDefaultDeviceChanged eRender /
                                             // eConsole, then eRender / eMultimedia)
#define WF_FAKE_SIGNAL_DEFAULT_COMMS_RENDER_CHANGED 13u  // `hresult` = render index: only the
                                             // communications default render endpoint moves
#define WF_FAKE_SIGNAL_RENDER_ENDPOINT_STATE        14u  // `hresult` = (DEVICE_STATE_* << 8) |
                                             // render index: OnDeviceStateChanged for it. When
                                             // the endpoint stops being ACTIVE, every default
                                             // role it held moves to the lowest ACTIVE index
                                             // (with its OnDefaultDeviceChanged), and streams
                                             // running on it are NOT failed by this signal
                                             // (use WF_FAKE_SIGNAL_DEVICE_LOST for that)
#define WF_FAKE_SIGNAL_DEFAULT_CAPTURE_CHANGED      15u  // `hresult` = capture index: console
                                             // and multimedia default capture move there
#define WF_FAKE_SIGNAL_DEFAULT_COMMS_CAPTURE_CHANGED 16u // `hresult` = capture index, or -1:
                                             // there is no communications default any more
#define WF_FAKE_SIGNAL_KEEPALIVE_START_FAILS        17u  // every keep-alive start fails with
                                             // `hresult` (0: E_FAIL) until
                                             // WF_FAKE_SIGNAL_CLEAR on WF_FAKE_SRC_OTHERS
#define WF_FAKE_SIGNAL_KEEPALIVE_EXITS              18u  // the running keep-alive's thread ends
                                             // on its own now with `hresult` (0:
                                             // AUDCLNT_E_DEVICE_INVALIDATED)

// wf_fake_query selector (2026-11-23, fake sessions only).
#define WF_FAKE_QUERY_LOOPBACK  1u  // text: endpoint id the device loopback (mode 2) runs on;
                                    // value: device-loopback opens so far
#define WF_FAKE_QUERY_KEEPALIVE 2u  // text: endpoint id the keep-alive runs on; value:
                                    // keep-alive start ATTEMPTS so far (failed ones included)
#define WF_FAKE_QUERY_MIC       3u  // text: endpoint id the mic runs on; value: mic opens

// WfDiagnostics.power_request: the session's Win32 power request (System + Execution +
// Display required), held from wf_session_start until wf_session_stop.
#define WF_POWER_REQUEST_NONE   0u  // not started, stopped, or start failed
#define WF_POWER_REQUEST_HELD   1u  // held (a fake session holds a counted no-op)
#define WF_POWER_REQUEST_FAILED 2u  // PowerCreateRequest/PowerSetRequest refused; the
                                    // recording runs, but the machine may sleep

// ---------------------------------------------------------------------------
// Structures
// ---------------------------------------------------------------------------

// One audio endpoint. `id` is the Windows counterpart of the macOS device UID and the
// value that goes into WfSessionConfig.mic_device_id.
typedef struct WfDeviceInfo {
    uint32_t flow;                       // WF_FLOW_RENDER / WF_FLOW_CAPTURE
    uint32_t state;                      // DEVICE_STATE_* as reported by MMDevice
    uint32_t mix_rate;                   // mix format sample rate, 0 if unreadable
    uint32_t mix_channels;               // mix format channel count, 0 if unreadable
    uint32_t is_default_console;         // 1 when this is the default console endpoint
    uint32_t is_default_communications;  // 1 when this is the default comms endpoint
    int32_t  raw_supported;              // 1 yes, 0 no, -1 property absent/unreadable
    uint32_t reserved;                   // 0
    wchar_t  id[256];                    // endpoint id, NUL terminated, truncated if longer
    wchar_t  name[128];                  // friendly name, NUL terminated
} WfDeviceInfo;

// One audio session (IAudioSessionManager2): which process plays or records where.
typedef struct WfAudioSessionInfo {
    uint32_t pid;
    uint32_t parent_pid;                 // 0 when unreadable
    uint32_t state;                      // AudioSessionState (0 inactive, 1 active, 2 expired)
    uint32_t is_render;                  // 1 render endpoint, 0 capture endpoint
    uint32_t is_system_sounds;           // 1 for the system sounds session
    float    peak;                       // current peak 0..1, -1 when unreadable
    wchar_t  process_name[128];          // base name, e.g. L"ms-teams.exe"
    wchar_t  display_name[128];          // session display name, often empty
    wchar_t  endpoint_id[256];
} WfAudioSessionInfo;

// Session configuration. `size` must be set to sizeof(WfSessionConfig) by the caller;
// a different value is rejected with WF_E_INVALID_ARG. All other fields may be zero,
// which selects the documented defaults - except enable_aec, where 0 means "canceller
// off", so a recording with echo cancellation must set it to 1 explicitly.
typedef struct WfSessionConfig {
    uint32_t size;                       // = sizeof(WfSessionConfig)
    uint32_t others_mode;                // WF_OTHERS_*; default 0 (system exclude)
    uint32_t target_pid;                 // only for WF_OTHERS_PROCESS_INCLUDE
    uint32_t ns_level;                   // 0 off, 1..4 = 6/12/18/21 dB; the app uses 2.
                                         // Only effective with enable_aec = 1 (the noise
                                         // suppressor runs inside the AEC bridge)
    uint32_t enable_aec;                 // 1 run AEC3 + NS on the mic (the app's setting).
                                         // 0 passes the mic through untouched: no AEC3, no
                                         // noise suppressor, no latency shift (Me frame n
                                         // carries capture frame n, timeline_sample = n*160),
                                         // no wait for the reference, never
                                         // WF_FRAME_UNFILTERED, no flush frames on stop;
                                         // WfMetrics.erle_db, delay_ms, unfiltered_frames
                                         // and aec_resets stay 0. The echo reference is
                                         // still captured (Others) but never buffered
    uint32_t prefer_raw;                 // 1 open the mic with AUDCLNT_STREAMOPTIONS_RAW
    uint32_t allow_device_loopback_fallback;  // 1 fall back to mode 2 without process loopback
    uint32_t keepalive_mode;             // WF_KEEPALIVE_*: the keep-alive always runs, this
                                         // selects the watchdog gate; forced to 1 in mode 2,
                                         // 2 is rejected
    uint32_t timestamp_mode;             // WF_TIMESTAMP_*; default 0 (measured, M1)
    uint32_t reserved0;                  // 0
    double   base_duration_sec;          // duration of earlier sessions of this meeting
    wchar_t  mic_device_id[256];         // empty = the default COMMUNICATIONS capture
                                         // endpoint (as Teams uses it), the console default
                                         // when there is none; followed while recording
                                         // (since 2026-11-23; before: console default)
    wchar_t  render_device_id[256];      // empty = the default console render endpoint,
                                         // FOLLOWED while recording (2026-11-23): a debounced
                                         // default change moves the device loopback (mode 2)
                                         // and the keep-alive there. Set = pinned: default
                                         // changes are ignored; when it is removed or stops
                                         // being active the session falls back to the
                                         // default ("fallback:" detail) and stays there for
                                         // the rest of the session - it never hops back on
                                         // its own (the caller decides)
} WfSessionConfig;

// One 10 ms frame on the shared 16 kHz timeline.
//
// A stream that starts, resumes or ends inside a frame produces a PARTIAL frame (Mac
// EchoFrame.valid): only samples[valid_lo .. valid_hi) carry audio. The samples outside
// that range are 0.0f, so a consumer that ignores the range still gets silence there, but
// they were never captured and must not be archived as audio. A complete frame has
// valid_lo = 0 and valid_hi = 160; valid_lo < valid_hi always holds for a queued frame.
// No reserved bytes are left (the former reserved field became valid_lo/valid_hi).
typedef struct WfFrame {
    uint32_t source;          // WF_SOURCE_ME or WF_SOURCE_OTHERS
    uint32_t flags;           // WF_FRAME_* bit mask
    uint32_t attempt_id;      // (re)start counter of the producing stream; frames of an
                              // older attempt can be fenced out by the caller
    uint16_t valid_lo;        // first sample of samples[] that carries audio (0 if full)
    uint16_t valid_hi;        // one past the last carrying sample (160 if full)
    int64_t  timeline_sample; // sample position of samples[0] on the shared 16 kHz grid
                              // anchored at the session origin (wf_session_get_origin).
                              // Others: n*160 of capture frame n. Me: the AEC output has
                              // been shifted by the DSP latency (AEC3 128 + NS 96 = 224
                              // samples, 128 with ns_level 0), so the content of capture
                              // frame n sits at n*160 + k - 224 and is re-cut onto the
                              // same 160 grid; with enable_aec = 0 there is no shift and
                              // Me frame n sits at n*160. The first carrying sample sits
                              // at timeline_sample + valid_lo.
    double   offset_sec;      // meeting time of samples[0] (not of valid_lo). Every source
                              // is cut on the session grid, so this is exactly
                              //   timeline_sample/16000 + base_duration_sec
    float    samples[160];    // mono float32 in [-1, 1]; 0 outside [valid_lo, valid_hi)
} WfFrame;

// One control-plane event. Events are ordered and never dropped silently: a full queue
// increments WfMetrics.events_dropped. The struct has no reserved bytes (296 = 6*4 +
// 2*8 + 128*2), so it carries no frame/event tick; see "Frame / event ordering" above.
typedef struct WfEvent {
    uint32_t kind;            // WF_EVENT_*
    uint32_t source;          // WF_SOURCE_*
    uint32_t attempt_id;      // attempt the event belongs to, 0 when not applicable
    uint32_t reason;          // WF_GAP_* for WF_EVENT_GAP, otherwise WF_GAP_NONE
    int32_t  hresult;         // HRESULT when the event carries one, otherwise 0 (S_OK)
    uint32_t sequence;        // monotonic counter; gaps in it mean events were dropped
    double   at_offset_sec;   // meeting time of the event: (host time - session origin)
                              //   / 1e7 + base_duration_sec (wf_session_get_origin)
    double   duration_sec;    // gap length for WF_EVENT_GAP, otherwise 0
    wchar_t  detail[128];     // short human readable detail, may be empty
} WfEvent;

// Current meter values (spec 3.6 "Pegelanzeige").
typedef struct WfLevels {
    float    me;              // 0..1, (20*log10(max(rms,1e-5))+60)/60, 0 after 0.35 s idle
    float    others;
    uint32_t me_active;       // 1 while the mic stream is delivering
    uint32_t others_active;   // 1 while the Others stream is delivering
} WfLevels;

// Counters and health of a running session. Everything is cumulative since wf_session_start.
typedef struct WfMetrics {
    double   erle_db;              // echo return loss enhancement reported by the bridge
    double   drift_ppm_me;         // measured mic device clock vs. the host clock, over
                                   // the anchored stretches only (holes are not drift)
    double   drift_ppm_others;     // same for the Others/reference stream
    double   jitter_p95_ms_me;     // p95 of |timestamp - prediction| over the last packets
    double   jitter_p95_ms_others;
    uint64_t unfiltered_frames;    // mic frames processed with a zero reference
    uint64_t late_dropped;         // late or duplicate mic frames dropped
    uint64_t ref_discarded;        // reference frames older than 0.5 s discarded
    uint64_t inbox_overflows;      // CaptureInbox overflow events over all sources
    uint64_t timestamp_errors;     // AUDCLNT_BUFFERFLAGS_TIMESTAMP_ERROR packets
    uint64_t discontinuities;      // AUDCLNT_BUFFERFLAGS_DATA_DISCONTINUITY packets
    uint64_t watchdog_recoveries;  // 0.5 s watchdog rebuilds
    uint64_t restarts;             // stream (re)starts after the initial one
    uint64_t frames_me;            // Me frames handed to the output queue
    uint64_t frames_others;        // Others frames handed to the output queue
    uint64_t frames_dropped;       // frames lost because the output queue was full
    uint64_t events_dropped;       // events lost because the event queue was full
    uint64_t aec_resets;           // mn_aec_reset calls (mic device or format change only)
    uint64_t ring_overflows;       // capture ring overruns (DSP thread starved)
    int32_t  delay_ms;             // echo path delay estimated by the bridge
    uint32_t state;                // WF_STATE_*
    uint32_t mic_raw;              // 1 when the mic was opened with RAW processing
    uint32_t ref_mode_actual;      // WF_REF_MODE_*
    uint32_t attempt_id;           // current mic attempt
    uint32_t orphaned_steps;       // driver steps that blew the 5 s limit
} WfMetrics;

// Recording-health diagnostics (2026-11-23, additive within ABI v1). Versioned by `size`:
// the caller sets it to the sizeof(WfDiagnostics) it was built with; the DLL writes
// min(size, its own sizeof) bytes and stores the number of bytes it wrote in `size`.
// Fields are only ever appended. Every value is per session (since wf_session_start, kept
// after stop); reading never resets anything. Per-source arrays are not used so that a
// managed mirror stays a flat struct.
typedef struct WfDiagnostics {
    uint32_t size;                       // in: caller's sizeof; out: bytes written (>= 8)
    uint32_t power_request;              // WF_POWER_REQUEST_*
    double   dsp_tick_gap_max_ms;        // longest pause between two DSP ticks (nominal 10)
    double   capture_gap_max_ms_me;      // longest pause between two capture packet
    double   capture_gap_max_ms_others;  //   ARRIVALS of a stream (over all its attempts;
    double   capture_gap_max_ms_reference;  // the pause across a restart is not counted)
    uint32_t ring_high_water_me;         // most slots ever queued in a stream's capture
    uint32_t ring_high_water_others;     //   ring (one slot = one capture packet, <= 480
    uint32_t ring_high_water_reference;  //   frames, typically 10 ms)
    uint32_t ring_capacity_slots;        // 1024: a high-water mark at this value means loss
    uint32_t dsp_slices_max;             // most 100 ms catch-up slices one tick needed (1 =
                                         // never behind by more than one slice)
    uint32_t reserved0;                  // 0
    uint64_t ring_loss_gaps;             // WF_GAP_INBOX_OVERFLOW gaps reported (lost
                                         // intervals, all sources); WfMetrics.ring_overflows
                                         // counts the dropped packets
    // ---- appended 2026-11-23 (sizeof 72 -> 88); an older caller's size 72 gets the
    // prefix above unchanged ----
    uint32_t render_switches;            // render endpoint moves: the endpoint the device
                                         // loopback (mode 2) / the keep-alive (modes 0/1)
                                         // runs on changed - a followed default change, a
                                         // lost pinned endpoint's fallback, an invalidated
                                         // loopback that reopened elsewhere
    uint32_t aec_render_resets;          // canceller resets because the echo path moved to
                                         // another render endpoint (also counted in
                                         // WfMetrics.aec_resets, which counts every reset)
    uint32_t keepalive_failures;         // keep-alive starts that failed plus keep-alive
                                         // threads that ended on their own
    uint32_t keepalive_starts;           // successful keep-alive starts (1 per endpoint
                                         // normally; more after moves and retries)
} WfDiagnostics;

// ---------------------------------------------------------------------------
// Functions
// ---------------------------------------------------------------------------

// ABI version of this DLL; must equal WF_ABI_VERSION of the managed mirror.
WF_API uint32_t wf_abi_version(void);

// Capability bits (WF_CAP_*). Returns WF_E_INVALID_ARG when `flags` is null.
WF_API int32_t wf_capabilities(uint32_t* flags);

// Lists audio endpoints of `flow` (WF_FLOW_*). Writes at most `cap` entries to `out` and
// the number of endpoints found to `*count`. With cap == 0 (`out` may then be null) only
// `*count` is written and WF_E_BUFFER_TOO_SMALL is returned, so the caller can size its
// array. `out` == null with cap > 0 is WF_E_INVALID_ARG (checked before enumerating), as
// is a null `count`. Runs on an internal MTA worker thread, so STA callers are safe.
WF_API int32_t wf_devices_list(uint32_t flow, WfDeviceInfo* out, uint32_t cap, uint32_t* count);

// Same contract (including the argument checks) for the audio sessions of every active
// endpoint of `flow`.
WF_API int32_t wf_audio_sessions_list(uint32_t flow, WfAudioSessionInfo* out, uint32_t cap,
                                      uint32_t* count);

// Creates a session from `cfg` (validated, including cfg->size). The session is idle
// until wf_session_start. `*out_session` receives an opaque handle.
WF_API int32_t wf_session_create(const WfSessionConfig* cfg, void** out_session);

// Creates a session whose capture, clock and driver steps are driven by the wf_fake_*
// functions instead of WASAPI. No threads are started: everything runs on the caller's
// thread inside wf_fake_tick, so robustness scenarios finish in milliseconds.
WF_API int32_t wf_session_create_fake(const WfSessionConfig* cfg, void** out_session);

// Starts capture: sets the session clock origin, opens the streams and starts the DSP
// and supervisor threads (real sessions only).
WF_API int32_t wf_session_start(void* session);

// Switches the microphone endpoint while running. `device_id` may be null or empty for
// the system default. The old stream is stopped before the new one starts and the gap
// is reported as WF_EVENT_DEVICE_SWITCHED plus a WF_GAP_RESTART gap.
WF_API int32_t wf_session_set_mic_device(void* session, const wchar_t* device_id);

// Stops the session and emits what is still inside the pipeline (Mac
// EchoAudioProcessor.finish): every stream's trailing partial frame is pushed through, then
// the AEC latency tail (224 samples, 128 with ns_level 0, none with enable_aec 0) is flushed
// with zero frames that carry no samples of their own - the frames queued by the flush are
// the real cleaned tail.
//
// The flush runs ON THE DSP THREAD, which stays the only producer of frames and events:
// wf_session_stop posts a stop request, the DSP thread drains the capture rings one last
// time, flushes, queues WF_EVENT_STOPPED as the very last event (after the last frame) and
// exits. Only then are the capture streams told to stop; they are released in the
// background (wf_session_destroy waits for that). wf_session_stop waits for the DSP thread
// at most 2.5 s, inside the 3 s quit budget. Returns:
//   WF_OK         the flush ran and WF_EVENT_STOPPED is queued; WfMetrics.state is STOPPED.
//   WF_E_STATE    the session was never started.
//   WF_E_FAIL     the DSP thread did not finish within the bound. The session is failed:
//                 no Stopped is promised. If the DSP thread was idle (not inside a tick),
//                 the DSP's producer role is taken over and WF_EVENT_ERROR (hresult
//                 HRESULT_FROM_WIN32(WAIT_TIMEOUT)) is queued as the last event; if it is
//                 stuck inside a tick nothing more is queued by this call (should the
//                 thread recover, its flush and Stopped still follow). Call
//                 wf_session_destroy next; it abandons a DSP thread that never returns.
// Safe to call more than once: later calls return the first call's result at once (WF_OK
// while a first call on another thread is still running) and queue nothing.
WF_API int32_t wf_session_stop(void* session);

// Stops if needed (see wf_session_stop), waits up to 8 s for the control-plane thread to
// release the streams and up to 5 s for the driver workers, then releases the session.
// Driver steps that are still hung are abandoned; they own everything they touch and
// free it when they finally return. A DSP or control-plane thread that does not return
// in time is abandoned too: the session's memory and its event handle are then leaked on
// purpose instead of being freed under a running thread, and WF_E_FAIL is returned.
// The handle is invalid afterwards in both cases.
WF_API int32_t wf_session_destroy(void* session);

// Auto-reset Win32 event (HANDLE) that is signalled whenever frames or events became
// available. Owned by the session, valid until wf_session_destroy. Never close it.
WF_API void* wf_session_event_handle(void* session);

// Drains up to `cap` frames into `out` and writes the number copied to `*count`.
// Returns WF_OK even when nothing was available (*count == 0). `out` == null with
// cap > 0, a null `session` or a null `count` is WF_E_INVALID_ARG; cap == 0 is a valid
// no-op (*count = 0). See "Frame / event ordering" at the top for the read order.
WF_API int32_t wf_session_read_frames(void* session, WfFrame* out, uint32_t cap, uint32_t* count);

// Drains up to `cap` events into `out`. Same contract as wf_session_read_frames.
WF_API int32_t wf_session_read_events(void* session, WfEvent* out, uint32_t cap, uint32_t* count);

// Current meter values.
WF_API int32_t wf_session_get_levels(void* session, WfLevels* out);

// Current counters and health.
WF_API int32_t wf_session_get_metrics(void* session, WfMetrics* out);

// The exact clock origin of the session, so the caller can map its own host timestamps
// onto meeting time without re-deriving it from frames:
//   *origin_qpc_100ns  host time captured by wf_session_start, in 100 ns units of
//                      QueryPerformanceCounter (QPC * 1e7 / QPC frequency, the unit of
//                      WASAPI's pu64QPCPosition); the virtual clock for a fake session.
//   *base_duration_sec WfSessionConfig.base_duration_sec as accepted by create.
// Meeting time of host time t = (t - origin)/1e7 + base_duration_sec, which is exactly
// how WfEvent.at_offset_sec is computed; sample position p of the shared grid sits at
// p/16000 + base_duration_sec (WfFrame.offset_sec). The origin never changes after start.
// Returns WF_E_INVALID_ARG for a null pointer argument and WF_E_STATE before
// wf_session_start (no origin yet); both outputs are written only on WF_OK.
WF_API int32_t wf_session_get_origin(void* session, int64_t* origin_qpc_100ns,
                                     double* base_duration_sec);

// Recording-health diagnostics (see WfDiagnostics). `out->size` must be set by the caller
// (>= 8); WF_E_INVALID_ARG for a null argument or a smaller size. Valid in every state
// (all zero before wf_session_start). Additive within ABI v1.
WF_API int32_t wf_session_get_diagnostics(void* session, WfDiagnostics* out);

// Process-wide number of power requests the sessions of this DLL hold right now (a fake
// session's counted no-op included): 1 per started, not yet stopped session, 0 after every
// session is stopped or destroyed. WF_E_INVALID_ARG for a null `count`. Additive within v1.
WF_API int32_t wf_power_requests_active(uint32_t* count);

// ---- fake capture injection (fake sessions only) --------------------------------
// All four return WF_E_NOT_FAKE on a real session.

// Injects one capture packet for `source` (WF_FAKE_SRC_*) into the same ring the real
// capture thread would write to. `samples` is mono float at `rate`; `qpc_100ns` is the
// packet timestamp in 100 ns units on the session clock; `flags` takes the WASAPI
// AUDCLNT_BUFFERFLAGS_* bits (1 discontinuity, 2 silent, 4 timestamp error).
WF_API int32_t wf_fake_push(void* session, uint32_t source, const float* samples,
                            uint32_t frames, uint32_t rate, int64_t qpc_100ns,
                            uint64_t device_position, uint32_t flags);

// Sets the virtual clock the supervisor timers, the echo processor and the level meters
// read. Time only ever moves forward.
WF_API int32_t wf_fake_set_now(void* session, int64_t qpc_100ns);

// Runs one supervisor poll followed by one 10 ms DSP tick at the current virtual time.
WF_API int32_t wf_fake_tick(void* session);

// Injects a fault (WF_FAKE_SIGNAL_*) for `source` (WF_FAKE_SRC_*); `hresult` is used by
// the signals that carry one (for example E_ACCESSDENIED with WF_FAKE_SIGNAL_START_FAILS).
WF_API int32_t wf_fake_signal(void* session, uint32_t signal, uint32_t source, int32_t hresult);

// (2026-11-23, additive) Reads the fake plant's state for `what` (WF_FAKE_QUERY_*): the
// endpoint id into `text` (NUL terminated, truncated to `text_chars`; empty when nothing
// runs) and a counter into `*value`. `text` may be null when `text_chars` is 0, `value`
// may be null. WF_E_NOT_FAKE on a real session, WF_E_INVALID_ARG for an unknown `what`.
WF_API int32_t wf_fake_query(void* session, uint32_t what, wchar_t* text, uint32_t text_chars,
                             uint32_t* value);

#ifdef __cplusplus
}  // extern "C"
#endif
