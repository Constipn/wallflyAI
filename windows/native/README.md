# WallflyAudio native engine

`WallflyAudio.dll` is the Windows port of the macOS capture and DSP path
(`AudioCapture.swift` and `EchoAudioProcessor.swift` in `Sources/MeetingNotes` of the macOS app).
It captures the microphone and the meeting audio through WASAPI, runs the vendored WebRTC
AEC3 + noise suppressor and hands 10 ms frames and events to the .NET app through a pull
based C ABI (`src/api/wallfly_audio.h`).

## Layout

| Path | Contents |
|---|---|
| `src/api/` | The C ABI (`wallfly_audio.h`) and its thin validating shell. |
| `src/session/` | Session wiring, supervisor state machine, 10 ms DSP thread, event coalescer, fake capture path. |
| `src/core/` | Portable DSP core: ring, CaptureInbox, CaptureNormalizer, Framer, EchoProcessor, levels (Mac ports). |
| `src/aec/` | `mn_aec_*` bridge: WebRTC AEC3 + NS (`AECBridge.cpp`) or the passthrough stub (`aec_stub.cpp`). |
| `src/wasapi/` | WASAPI primitives: mic, process loopback, device loopback, keep-alive render, notifications. |
| `vendor/` | Byte-identical copy of the Mac `Sources/WebRTCAEC` tree (see `PROVENANCE.md`, `SHA256SUMS`). |
| `tests/core`, `tests/aec`, `tests/session`, `tests/test_main.cpp` | ctest suites (below). |
| `tests/hardware/wf_hw_smoke.cpp` | Manual smoke test on real endpoints (not in ctest). |
| `tools/audioprobe/` | `wallfly-audioprobe`, the WASAPI measurement CLI (incl. `compare`, which capture path hears the meeting); it writes its reports to `analysisudio-probe` next to this folder. |

## Build

```powershell
cd windows\native
.\bootstrap-toolchain.ps1          # once: llvm-mingw 20261020, CMake 4.4.5, Ninja 1.13.2 into windows\.tools (SHA-256 pinned)

# Production configuration: vendored AEC3 + NS linked in (WF_CAP_AEC3)
..\.tools\cmake\bin\cmake.exe --preset llvm-mingw-release -DWF_WITH_AEC3=ON
..\.tools\cmake\bin\cmake.exe --build --preset llvm-mingw-release
..\.tools\cmake\bin\ctest.exe --preset llvm-mingw-release

# Passthrough stub instead of AEC3, in its own build directory
..\.tools\cmake\bin\cmake.exe -S . -B build\release-off -G Ninja `
  -DCMAKE_TOOLCHAIN_FILE="$PWD\cmake\toolchain-llvm-mingw.cmake" -DCMAKE_BUILD_TYPE=Release `
  -DCMAKE_MAKE_PROGRAM="$PWD\..\.tools\ninja\ninja.exe" -DWF_WITH_AEC3=OFF
..\.tools\cmake\bin\cmake.exe --build build\release-off
..\.tools\cmake\bin\ctest.exe --test-dir build\release-off
```

`WF_WITH_AEC3` defaults to OFF and is cached, so pass it explicitly. `llvm-mingw-debug`
builds into `build\debug`. All targets link the runtime statically and build with
`-Wall -Wextra -ffp-contract=off`; the build is warning-free in both configurations.

## Tests

| Target | What | Checks (ON / OFF) |
|---|---|---|
| `wf_core_tests` | DSP core against a mock AEC: ring, inbox, drift (including the drift metric across holes), framing, echo processor (including the `enable_aec = 0` passthrough: not one bridge call), levels, Mac echo/time-map replays, one loss = one report (inbox -> normalizer) | 2497 / not re-run |
| `wf_aec_tests` | ON only: C bridge contract, PushSincResampler, ERLE acceptance against the Mac baselines | 89 / - |
| `wf_session_tests` | The DLL through its C ABI and the fake capture path in virtual time: ABI layout and argument checks, frames (timeline, -224 compensation, partial frames, stop flush and stop ordering, `enable_aec = 0`, `wf_session_get_origin`), robustness (supervisor timings, `WF_GAP_NONE`, mic format change vs. endpoint property notifications, microphone fallback to the system default), queues (backpressure: a DSP stall the ring holds loses nothing, the canceller output across a 1.5 s stall is bit-identical to real time, a ring overflow is one gap per source; gap coalescing, processing overflow; a render switch mid-stream: the Me output from the switch on is bit-identical to a fresh session's), `onset` (replay of the recorded process-loopback packet pattern at a playback onset, with and without keep-alive), `standby` (power request lifecycle, `WfDiagnostics`), `smoke_verdict` (the `wf_hw_smoke` verdict, incl. the 2026-11-02 numbers), `devices` (AP3: default-render follow, A->B->A, five switches without OthersFailed, invalidated loopback, pinned render endpoint ignore / fallback / no hop back, keep-alive move in modes 0/1, mic on the communications default, keep-alive failure warnings and retries, `WfDiagnostics` prefix for 72-byte callers). Plus `dsp_stop`: the threaded stop protocol of `DspThread` (flush on the DSP thread, bounded wait, hung tick, producer take-over) with one real thread, linked statically next to the DLL | 1441 / not re-run |
| `wf_tests` | C ABI probe, the exact export list (23 names), plus the pure WASAPI helpers (format description, channel averaging) | pass / pass |

Manual: `build\release\bin\wf_hw_smoke.exe [seconds] [--mode system|include|device] [--pid N]
[--expect-audio]` (default 5 s, mode system = others_mode 0; include needs `--pid`). It reads in
the documented order (below) and also reports the `wf_session_stop` result and duration, the
gaps per source, FormatChanged/Restarting counts, ERLE/delay sampled while Others carries
signal, `WfDiagnostics`, the power request and wall vs. session clock. The verdict
(`tests/hardware/smoke_verdict.h`, tested in `wf_session_tests`) prints `SMOKE OK` (exit 0) or
`SMOKE FAIL` (exit 1) with every reason: a source below 99.9 frames/s (Others in include mode
only with `--expect-audio`), `--expect-audio` with an Others peak below 0.001, Me/Others gaps
covering more than 0.1 % of the run, any watchdog recovery or restart, wall clock more than 2 s
ahead of the session clock (suspend / standby), any inbox or ring overflow, dropped frames or
events, problem events, a missing Stopped. Usage errors exit 2. To test a playback onset, start a player OUTSIDE its process tree first (exclude mode
hides our own tree), e.g. from PowerShell
`Start-Process powershell -ArgumentList '-File','play.ps1',...` with a script that sleeps ~1.5 s
and then plays a WAV through `Media.SoundPlayer.PlaySync()`. Since AP3 the smoke also prints
every event detail and a `render :` line (keep-alive starts/failures, render switches, canceller
resets for render changes).
`build\release\bin\wallfly-audioprobe.exe compare [--seconds N] [--pid N] [--wav <dir>]
[--out <dir>]` (default 60 s) captures IN PARALLEL: process-include on the oldest
`ms-teams.exe`, on every `ms-teams_modulehost.exe` and on `--pid`, process-exclude of its own
tree, and device loopback on the default console and (when different) communications render
endpoint, each endpoint with a silent keep-alive; one line per second with peak dBFS and
non-zero % per stream plus the other processes' render sessions, then a summary (active
seconds = peak > -50 dBFS) and `hears audio: <streams>`. stdout only: a report only with
`--out`, WAV files (mono float32 per stream) only with `--wav`.
`build\release\bin\wallfly-audioprobe.exe onset --seconds 8 --out <dir> [--no-mic]
[--keepalive] [--buffer-ms N] [--restart-mic-ms N]` logs every process-loopback and mic packet
(QPC, frames, flags, device position, derived steps) plus every endpoint notification with its
property key while such a player starts and stops.

## ABI (version 1)

| Struct | Size | Align | Notes |
|---|---|---|---|
| `WfDeviceInfo` | 800 | 4 | |
| `WfAudioSessionInfo` | 1048 | 4 | |
| `WfSessionConfig` | 1072 | 8 | `size` must equal `sizeof(WfSessionConfig)` |
| `WfFrame` | 672 | 8 | source @0, flags @4, attempt_id @8, **valid_lo @12, valid_hi @14** (uint16, former `reserved`), timeline_sample @16, offset_sec @24, samples[160] @32 |
| `WfEvent` | 296 | 8 | |
| `WfLevels` | 16 | 4 | |
| `WfMetrics` | 176 | 8 | no `size` field: can never grow |
| `WfDiagnostics` | 88 | 8 | `size` @0 (caller's sizeof in, bytes written out), power_request @4, dsp_tick_gap_max_ms @8, capture_gap_max_ms_me/others/reference @16/24/32, ring_high_water_me/others/reference @40/44/48, ring_capacity_slots @52, dsp_slices_max @56, reserved0 @60, ring_loss_gaps @64; appended 2026-11-23 (72 -> 88): render_switches @72, aec_render_resets @76, keepalive_failures @80, keepalive_starts @84 |

`tests/session/test_abi_layout.cpp` pins every offset with `static_assert`; the managed
mirror (`windows/Wallfly.Audio`) must use the same numbers. Neither `WfFrame` nor `WfEvent`
has a spare byte left, so there is no shared frame/event tick (see "Frame / event order").

Additions inside v1 (additive only, `WF_ABI_VERSION` stays 1, no existing struct layout
changed; 23 exports, the managed binding requires the first 20):

- (2026-11-23, AP3 device changes) `WF_EVENT_WARNING` (18, never fatal; today only the
  keep-alive: `keepalive:start failed 0x%08X|render:<name>|retry in <s> s` / `...|gave up` /
  `keepalive:stopped 0x%08X|...`, source OTHERS, hresult = the failure). Event details: fields
  separated by `|`; `mic:<friendly name>` on Me DEVICE_ACQUIRED (no fallback) / DEVICE_SWITCHED /
  DEVICE_LOST, `render:<friendly name>` on Others in mode 2 and on the REFERENCE DEVICE_SWITCHED
  of modes 0/1; a pinned render endpoint's fallback `fallback:<reason>|render:<name>` (reason
  `selected output missing` or `<name> unavailable`). The Me fallback detail stays exactly
  `fallback:<reason>` (the app parses the reason out of it). `WfDiagnostics` grew from 72 to 88
  bytes at the end (render_switches, aec_render_resets, keepalive_failures, keepalive_starts; a
  caller passing size 72 gets the old prefix, byte for byte). Fake only: `WF_FAKE_SIGNAL_12..18`
  (default render / communications render / render endpoint state / default capture /
  communications capture / keep-alive start fails / keep-alive exits), `WF_FAKE_QUERY_*` and
  `int32_t wf_fake_query(void*, uint32_t what, wchar_t* text, uint32_t text_chars, uint32_t*
  value)` (export 23), and four simulated render and capture endpoints (header "Fake endpoints").
  Gap change: the hole of a planned Others hand-over in mode 2 (render switch, invalidated
  loopback, lost pinned endpoint) is ONE gap - the DSP's re-anchor gap with its true position
  and length, reason `WF_GAP_RESTART` / `WF_GAP_DEVICE_LOST` - instead of a 0 s supervisor gap
  plus a `WF_GAP_TIMESTAMP_DISCONTINUITY` twin. Mic restarts are unchanged.

- (2026-11-23) `int32_t wf_session_get_diagnostics(void* session, WfDiagnostics* out)`: the caller
  sets `out->size` (>= 8) to its `sizeof(WfDiagnostics)`; the DLL writes `min(size, 72)` bytes
  and stores that count in `size`. Fields only ever get appended. All values are per session
  (since start, kept after stop) and reading never resets them: the longest pause between two
  DSP ticks, the longest pause between two capture packet ARRIVALS per stream (over all its
  attempts, not across a restart), the most ring slots ever queued per stream (of 1024), the
  most 100 ms catch-up slices one tick needed, the number of ring-loss gaps reported, and the
  power request state (`WF_POWER_REQUEST_NONE/HELD/FAILED` = 0/1/2).
- (2026-11-23) `int32_t wf_power_requests_active(uint32_t* count)`: process-wide number of power
  requests the DLL's sessions hold (fake sessions' counted no-op included).
- (2026-11-23) `WF_FAKE_SIGNAL_SESSION_START_FAILS` (11, fake only): the next `wf_session_start`
  fails with `WF_E_FAIL` after it took the power request.

- `int32_t wf_session_get_origin(void* session, int64_t* origin_qpc_100ns, double* base_duration_sec)`:
  the host time of `wf_session_start` in 100 ns QPC units (the unit of `pu64QPCPosition`;
  the virtual clock for a fake session) and the accepted `base_duration_sec`. Meeting time of
  host time t is `(t - origin) / 1e7 + base_duration_sec` (exactly `WfEvent.at_offset_sec`);
  sample position p sits at `p / 16000 + base_duration_sec` (`WfFrame.offset_sec`).
  `WF_E_STATE` before start, `WF_E_INVALID_ARG` for a null argument.
- `WF_GAP_NONE` (7): `WfEvent.reason` of every event that is not `WF_EVENT_GAP` (it was 0,
  which is `WF_GAP_INBOX_OVERFLOW`).
- Argument checks: `wf_session_read_frames`, `wf_session_read_events`, `wf_devices_list` and
  `wf_audio_sessions_list` return `WF_E_INVALID_ARG` for a null buffer with a capacity > 0
  (the list calls before enumerating). Capacity 0 stays valid: reads return `WF_OK` with
  count 0, the list calls `WF_E_BUFFER_TOO_SMALL` with the required count.
- `wf_session_destroy` returns `WF_E_FAIL` when it had to abandon a thread (see Stop).
- `WF_FAKE_SIGNAL_PROPERTY_CHANGED` (9, fake sessions only): an endpoint property notification
  without a format change (mic or render endpoint, format or other key).
- **Microphone fallback** (spec 3.1): when the configured `mic_device_id` fails to open
  (missing, disabled, unplugged or failing), the same start attempt opens the system default
  console capture endpoint instead - no further tier, and never after `E_ACCESSDENIED`, which
  still stops every retry. The attempt's `WF_EVENT_DEVICE_ACQUIRED` on `WF_SOURCE_ME` then carries
  `detail` = `fallback:<reason>` (`selected microphone missing` or `<friendly name> unavailable`)
  and `hresult` = the configured endpoint's failure; otherwise `detail` stays empty. `WfMetrics`
  reports the stream that runs (state RUNNING, `mic_raw` of the default's open, same
  `attempt_id`). While on the fallback the mic follows default-device changes, and a state change
  of the configured endpoint (it comes back) reopens it first. `WF_FAKE_SIGNAL_CONFIGURED_MIC_FAILS`
  (10, fake sessions only) makes only the configured endpoint fail (`hresult` 0 = not found).
  `WF_FAKE_SIGNAL_FORMAT_CHANGE` now really changes the fake mic endpoint's format and goes
  through the same notification filter as the real callbacks.

Windows-only behaviour:

- **`enable_aec`** (`WfSessionConfig`, 0 = off, so the app must set 1): with 0 the mic
  bypasses the bridge completely - no AEC3, no noise suppressor (`ns_level` is ignored), no
  latency shift (Me frame n carries capture frame n at `timeline_sample = n*160`), no wait for
  the reference, never `WF_FRAME_UNFILTERED`, no flush frames on stop. `erle_db`, `delay_ms`,
  `unfiltered_frames` and `aec_resets` stay 0. Samples keep the bridge's input rule (NaN/Inf
  to 0, clamp to [-1, 1]). The reference is still captured and published as Others but is not
  buffered for a canceller that does not exist.
- **Stop protocol**: the DSP thread is the only producer of frames and events, the stop
  flush included. `wf_session_stop` posts a stop request; the DSP thread drains the rings one
  last time, runs the flush and queues `WF_EVENT_STOPPED` after the last frame, then exits.
  Only after that are the streams told to stop; the supervisor releases them in the
  background and `wf_session_destroy` waits for it (8 s) and for the driver workers (5 s).
  `wf_session_stop` waits for the DSP thread at most 2.5 s (3 s quit budget). If it does not
  finish in time the call returns `WF_E_FAIL`; when the DSP thread is provably between ticks
  the stopping thread takes its producer role over and queues `WF_EVENT_ERROR` (hresult
  `HRESULT_FROM_WIN32(WAIT_TIMEOUT)`) as the last event, otherwise nothing is queued (a thread
  that recovers still delivers its flush and Stopped later). `wf_session_destroy` then
  abandons a thread that never returns: the session and its event handle are leaked on
  purpose instead of freed under it. A repeated `wf_session_stop` queues nothing and returns
  the first call's result. `WfMetrics.state` is `WF_STATE_STOPPED` from the moment Stopped is
  queued.
- **Frame / event order**: within each tick the DSP thread queues the frames before it
  commits the events, and Stopped comes after the last frame. A consumer that must never see
  an event before a frame queued earlier reads per wake-up: (1) a block of events, (2) frames
  until empty, (3) then handles that event block. Draining frames first and events second
  does not give that guarantee (frames queued between the two reads, including the stop
  flush, would arrive after their events). `Wallfly.Audio` already reads in this order.
- **Keep-alive in every mode** (since 2026-11-01): the silent render stream of spec 3.3 runs
  with the stream that feeds the canceller in modes 0, 1 and 2, whatever `keepalive_mode`
  says; the field now only selects the reference watchdog (0: the plain 0.5 s watchdog as
  before, 1 and always mode 2: the spec 3.3 gate; 2 stays `WF_E_UNSUPPORTED`), so no
  watchdog got weaker. Reason (audioprobe `onset`): when another
  process starts playing on an IDLE render endpoint, process loopback delivers nothing for
  0.43-0.52 s while the endpoint wakes up - no flags, no catch-up burst, the next packet's QPC
  is real time again - so Others and the echo reference got a 0.4-0.5 s hole, and past 0.5 s
  a watchdog rebuild that lost the start of the new audio. With the endpoint kept awake the
  same onset is one ~20-60 ms step. Price: when the endpoint is idle at `wf_session_start`, the
  wake-up happens then and delays the first buffer of BOTH streams by ~0.4 s (A/B, 5 runs
  each: Me 0.16-0.40 s -> 0.29-0.84 s, Others 0.03-0.12 s -> 0.60-0.84 s); the mic frames
  before Others' first packet run unfiltered (~50 frames, nothing was playing).
  Since AP3 (2026-11-23) the keep-alive belongs to the plant and runs as its own driver steps
  (5 s limit, orphan rules as every step): in mode 2 on exactly the endpoint the loopback's open
  resolved (started right after that open returns), in modes 0/1 on the pinned or default render
  endpoint. A move starts the new one before it stops the old one. A failed start or a keep-alive
  thread that ends on its own (`KeepAliveRender::exited`, before: a silent exit) is a
  `WF_EVENT_WARNING`, retried after 0.5 / 1 / 2 s; the third failure in a row gives up until the
  render endpoint changes (an exit after >= 10 s of healthy run starts a new series). Never
  fatal: the capture streams keep running without it.
- **Device changes (AP3, 2026-11-23)**. Render, empty `render_device_id` (= follow): the
  console default render change (`OnDefaultDeviceChanged` eRender/eConsole; its eMultimedia
  twin only extends a pending window; eCommunications is ignored) is debounced 200 ms from the
  last notification; the decision is taken once, on the newest default, and nothing happens when
  that is the endpoint already in use (A->B->A). Otherwise mode 2: `DEVICE_SWITCHED` (Others,
  `render:<name>`), canceller reset, Others restart budget reset, loopback stop -> start on the
  default (break before make; the keep-alive follows when the open returns), and ONE
  `WF_GAP_RESTART` gap for the hole (fake: the packet that was in the old ring + the new
  endpoint's onset, e.g. 70 ms). Modes 0/1: `DEVICE_SWITCHED` (Reference, `render:<name>`),
  canceller reset, keep-alive moved; no capture stream is touched, no gap. An
  `AUDCLNT_E_DEVICE_INVALIDATED` on the mode-2 loopback takes the same path at once
  (`DEVICE_LOST render:<old name>`, one `WF_GAP_DEVICE_LOST` gap). A routine switch never spends
  the 0.5/1/2 s Others budget, so five switches in a row (even with failing opens) never end in
  OthersFailed. Pinned `render_device_id`: default changes are ignored; removal / a state other
  than ACTIVE (`OnDeviceStateChanged`, `OnDeviceRemoved`) or an invalidated stream reopens with the
  pinned id, which fails and falls back to the default in the same attempt
  (`DEVICE_ACQUIRED fallback:selected output missing|render:<name>`). From then on the session
  follows the default for the rest of the recording and does NOT hop back when the pinned
  endpoint returns - the app decides (it can restart the engine on it). Canceller: a render
  change flushes the delayed mic tail (no gap), drops every buffered reference frame of the old
  endpoint and resets AEC3 + NS (`EchoProcessor::on_render_changed`; counted in
  `WfMetrics.aec_resets` and `WfDiagnostics.aec_render_resets`); from there the output is the
  output of a fresh session fed the same audio (bit-identical in the fake test). Microphone,
  empty `mic_device_id`: opens the default COMMUNICATIONS capture endpoint (the console default
  when there is none or it fails to open) and follows the role it runs on - a console-only
  change is ignored while it runs on the communications default, a change naming the endpoint it
  already uses rebuilds nothing, so a console + communications pair is one rebuild. A configured
  mic behaves as before (its fallback still opens the console default and then follows console
  changes).
- **Mic format change detection**: `IMMNotificationClient::OnPropertyValueChanged` fires for
  every key of every endpoint (measured: the render endpoint's
  `{5b5e03cb-8070-420d-b794-8a28c89f6274},0` on every stream start/stop while audio plays).
  Only `PKEY_AudioEngine_DeviceFormat` / `PKEY_AudioEngine_OEMFormat` of the endpoint the mic
  stream runs on are forwarded; after the 50 ms window the plant re-reads the format a stream
  opened the same way would get (same RAW option and category) and compares rate, channels and
  sample format with the format recorded at the open. Only a difference rebuilds the mic (one
  `WF_EVENT_FORMAT_CHANGED`, one filter reset). Before, any property change on any endpoint
  was a candidate and the check compared the processed mix format (4 ch on the Intel SST
  array) with the RAW stream format (2 ch) - always "changed" - so the mic rebuilt itself in a
  ~0.2-0.35 s loop while other audio played (each rebuild caused the next notification).
- **DSP backlog in slices** (since 2026-11-23): the capture ring (1024 slots, ~10 s of 10 ms
  packets) is the cross-thread buffer, not the 240 ms CaptureInbox. A tick works the rings off
  in time-aligned slices: window = earliest head timestamp of all sources + 100 ms; Reference,
  Others and Mic each take the slots starting inside the window (at most 100 ms of audio);
  pipeline, canceller and emit run; repeat until the rings are empty. While a backlog remains
  the canceller's clock is the window end, so mic frames wait for their reference exactly as in
  real time (the Me output across a 1.5 s stall is bit-identical to real-time processing). Before,
  a tick moved the whole ring into the inbox first, and every stall > 240 ms dropped audio the
  ring still held - the ~0.3 s "storms" of InboxOverflow + TimestampDiscontinuity pairs on both
  sources in the 23-minute recording. Only a full ring loses audio now.
- **One loss, one gap**: a full ring drops the newest packet and remembers it; the next packet
  that fits carries the loss, and the DSP reports one `WF_GAP_INBOX_OVERFLOW` per lost interval
  and source at its true position. The packet then tells the normalizer that the hole in front
  of it is accounted for (`InputPacket::hole_reported_100ns`), so no `TIMESTAMP_DISCONTINUITY`
  twin is logged; an excess beyond the reported loss (> 3 ms, e.g. a device hole on top) is
  still reported on its own. The CaptureInbox's own overflow hands its dropped duration on the
  same way. Before, the twin came from the normalizer's re-anchor on the first packet after the
  dropped ones (`anchor_to` with `emit_gap`).
- **Power request** (since 2026-11-23): from `wf_session_start` to `wf_session_stop` (also given
  back by a failed start and by destroy) a session holds one `PowerCreateRequest` with System,
  Execution and Display required. Display is part of it on purpose: on a Modern Standby (S0
  low-power idle) machine the display-off timeout is the standby entry, which a System request
  alone does not prevent; that is how the unattended 1-hour run of 2026-11-02 went into standby
  after 146 s. `powercfg /requests` shows it while a recording runs. Fake sessions only count it.
- **Drift metric**: `drift_ppm_*` is measured over the anchored stretches only; a hole
  (endpoint stall, restart, inbox overflow) is lost audio, not clock drift. The old
  first-packet-to-now estimate turned a 0.1 s hole in a 5 s run into ~20000 ppm (and the
  124769 ppm of a quiet run into a ~0.6 s hole).

Semantics that follow the Mac:

- **Partial frames** (`EchoFrame.valid`): a stream that starts, resumes or ends inside a
  10 ms frame yields a frame where only `samples[valid_lo, valid_hi)` carry audio; the rest
  is 0.0f and must not be archived. A full frame has `valid_lo = 0, valid_hi = 160`.
- **Stop** (`EchoAudioProcessor.finish`): every stream's trailing partial frame is pushed
  through, then the AEC latency tail (224 samples, 128 with NS off, none with `enable_aec`
  0) is flushed with zero frames that carry no samples. `WF_FRAME_SYNTHETIC_SILENCE` is
  reserved and never set. Packets that reached the capture rings before the stop request
  are still drained by the flush, because the streams are detached only after it.
- **Mic format change**: the canceller's delayed tail is emitted before the filter reset
  (`flushEchoTail`), not reported as a gap.
- **Gap reasons**: `WF_GAP_INBOX_OVERFLOW` (0) is "Input buffer overflowed" - since
  2026-11-23 the capture ring (see "One loss, one gap"), before the 240 ms CaptureInbox; `WF_GAP_PROCESSING_OVERFLOW` (6) is the echo processor's 49-frame store
  ("Processing buffer overflowed"), reported on `WF_SOURCE_ME` for mic frames and on
  `WF_SOURCE_REFERENCE` for reference frames. Non-gap events carry `WF_GAP_NONE` (7).
- **Gap coalescing** (`CaptureEventInbox.append`): within one 10 ms DSP tick, a gap merges
  into the last staged gap of the same source and reason when it starts within
  `previous.start + previous.duration + 11 ms`. Merging never crosses a tick; the
  persistent merge of the Mac (`Meeting.appendRecordingGap`) is the consumer's job.

## Measured parity

ERLE acceptance (`wf_aec_tests`, same probe signal and delays as the Mac baselines):

| Echo delay | Windows | Mac |
|---|---|---|
| 16 ms | 63.13 dB | 63.13 dB |
| 80 ms | 69.56 dB | 69.57 dB |
| 180 ms | 68.14 dB | 68.15 dB |

WASAPI behaviour (measured with `wallfly-audioprobe` on 2026-11-01, 20 s / 10 s runs; the reports are not part of this repository):

- Timestamp jitter (M1): QPC residual p95 0.07 ms mic, 0.00 ms process-exclude, 0.07 ms
  device loopback; 0 TIMESTAMP_ERROR; cross-stream drift +0.2 / -0.0 ppm. PASS (criteria:
  p95 < 3 ms, 0 errors, drift < 20 ppm), hence `timestamp_mode = 0` (raw QPC) by default.
- Silence (M2): process-exclude loopback keeps delivering 100 packets/s of zeros while the
  machine is silent (largest gap 12 ms); device loopback delivers nothing while idle
  (0 packets) and 100 packets/s with the keep-alive render stream.
- Onset (audioprobe `onset`, 2026-11-01, 6 runs, a player outside our process tree starts at
  ~2 s and stops ~3 s later): process-exclude delivers 480-frame packets with QPC exactly
  +10.000 ms apart, `device_position` always 0, flags always 0, receive-minus-QPC lag
  9-12 ms. At the onset on an idle endpoint: no packet for 431 / 440 / 446 / 478 ms, then
  one QPC step of the same length and +10 ms again (lag unchanged, no burst, no
  DATA_DISCONTINUITY / TIMESTAMP_ERROR, frame count unchanged); with mic or without, with a
  200 ms buffer request the same. At the stop: one 29-32 ms step. With our keep-alive render
  stream running: onset one 31 ms step, stop 13 ms. The running RAW mic is not affected by
  either; a mic or loopback STARTING during an endpoint wake-up gets its first buffer only
  after it (~0.45 s). The render endpoint fires `{5b5e03cb-...},0` property changes at every
  stream start/stop while audio plays (26 in 8 s with a mic reopened every 300 ms).

Hardware smoke (`wf_hw_smoke.exe`, others_mode 0, AEC3 ON, 2026-11-01, after the keep-alive,
format-filter and drift changes; machine shared with other test runs):

| Run | Me frames | Others frames | Gaps Me / Others | Restarts / FormatChanged / AEC resets / watchdog | Unfiltered | Drift Me / Others | Jitter p95 Me / Others |
|---|---|---|---|---|---|---|---|
| 5 s quiet (x10) | 100.00/s | 100.00/s | 0 / 0 | 0 / 0 / 0 / 0 | 0-64 (startup, while Others waits for the endpoint) | -14..+27 / 0.0 ppm | 0.06-0.13 / 0.00 ms |
| 10 s, playback at 2.5-4 s (x4) | 946-977 (100.00/s) | 914-947 | 0 / 2 per run (onset 20-43 ms + stop 7-11 ms) | 0 / 0 / 0 / 0 | 5-60 | -1.8..+5.2 / 0.0 ppm | 0.08-0.10 / 0.00 ms |
| before: 10 s, playback at ~2.5 s | 667 (71.7/s) | 839 (84.7/s) | 11 / 3 (0.463 + 0.514 + 1.066 s, the last two overlap) | 13 / 12 / 12 / 1 | 155 | 282681 / 154454 ppm | 0.08 / 0.00 ms |

ERLE stays 0.2 dB (delay 4-12 ms) on this machine because the mic array hardly hears the
speakers (Me peak -45 dBFS while Others peaks at -0.1 dBFS); the AEC acceptance numbers above
come from `wf_aec_tests`. When the mic takes longer than ~0.5 s to deliver its first buffer,
the reference store reaches its 49-frame cap and reports one 10 ms
`WF_GAP_PROCESSING_OVERFLOW` per tick on `WF_SOURCE_REFERENCE` until the mic arrives, as the
Mac does on `.reference`. Sporadic simultaneous `WF_GAP_INBOX_OVERFLOW` on both sources (0.1-
0.67 s) appeared in 2 of ~20 runs, before and after the change, while other test processes
loaded the machine: the DSP thread did not run for > 240 ms. Since 2026-11-23 such a stall
loses nothing (the ring holds ~10 s); `WfDiagnostics.dsp_tick_gap_max_ms` and the ring
high-water marks show it instead. 15 s smoke after that change (mode system, quiet machine):
100.00 frames/s on both sources, 0 gaps, tick gap max 10.7 ms, ring high water 2 slots,
power request held, wall = session clock, `SMOKE OK`.
