using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Wallfly.Audio.Native;

namespace Wallfly.Audio;

/// <summary>A <c>WallflyAudio.dll</c> call failed; <see cref="Code"/> holds the <c>WF_E_*</c> value.</summary>
public sealed class AudioEngineException(string message, int code) : Exception(message)
{
    public int Code { get; } = code;
}

/// <summary>
/// <see cref="IAudioEngine"/> over <c>WallflyAudio.dll</c> (C ABI v1, pull model).
/// <para>
/// <see cref="StartAsync"/> creates and starts one native session and one background thread named
/// <c>WallflyAudio.Drain</c>. That thread waits on the session's auto-reset event
/// (<c>wf_session_event_handle</c>) and drains <c>wf_session_read_events</c> and
/// <c>wf_session_read_frames</c> in batches. Before an event batch is raised, every frame that is
/// already queued is raised first, so no event overtakes a frame the DLL queued before it - in
/// particular <see cref="AudioEngineEventKind.Stopped"/> comes after the whole stop flush. Both
/// <see cref="FrameReceived"/> and <see cref="EventRaised"/> run on that thread only, in queue order,
/// never concurrently. A handler that throws does not kill the thread: the first such exception is
/// reported once as an <see cref="AudioEngineEventKind.Error"/> event.
/// </para>
/// <para>
/// <see cref="AudioFrame.Samples"/> is always 160 samples and points into a buffer that is reused for
/// the next batch: it is only valid during the handler. <see cref="AudioFrame.ValidLo"/> and
/// <see cref="AudioFrame.ValidHi"/> carry <c>WfFrame.valid_lo/valid_hi</c> (partial frames at stream
/// start, resume and end); samples outside that range are 0 and were never captured.
/// </para>
/// <para>
/// One engine runs one session: after <see cref="StopAsync"/> create a new engine for the next start.
/// </para>
/// </summary>
public sealed class NativeAudioEngine : IAudioEngine
{
    /// <summary>How long <see cref="StopAsync"/> waits for the native Stopped event, and then for the drain thread.</summary>
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    private const int FrameBatch = 64;
    private const int EventBatch = 32;
    private const int IdleWaitMilliseconds = 250;

    private enum Phase { Created, Starting, Running, Stopped }

    private readonly object gate = new();
    private readonly NativeSessionHandle session = new();
    private readonly NativeFakeSession? fake;
    private readonly NativeSessionClock clock;
    private readonly ManualResetEvent quitRequested = new(false);
    private readonly ManualResetEventSlim stoppedDelivered = new(false);
    private Phase phase;
    private bool disposed;
    private Task? startTask;
    private Task? stopTask;
    private Thread? drainThread;
    private NativeEventWaitHandle? nativeEvent;
    private AudioMetrics finalMetrics = new() { State = AudioEngineState.Idle };
    private AudioDiagnostics? finalDiagnostics;
    private long lastSequence;
    private int handlerFaults;
    /// <summary>Why <c>wf_session_stop</c> failed (WF_E_FAIL: no Stopped will come), or null.</summary>
    private string? stopFailure;
    private volatile bool stopErrorRaised;

    /// <summary>The production engine: <c>wf_session_create</c> on WASAPI.</summary>
    public NativeAudioEngine()
    {
        clock = new(NativeSessionClock.QpcNow100ns);
    }

    /// <summary>
    /// Why <c>WallflyAudio.dll</c> cannot be used (missing next to the application, or not ABI v1), or
    /// null when it can. Probed once per process.
    /// </summary>
    public static string? UnavailableReason => NativeAudioLibrary.UnavailableReason;

    /// <summary>
    /// The production engine, checked up front: throws <see cref="NotSupportedException"/> (the exception
    /// <see cref="StartAsync"/> would fail with) when <c>WallflyAudio.dll</c> is missing or incompatible.
    /// </summary>
    public static NativeAudioEngine Create()
    {
        NativeAudioLibrary.EnsureAvailable();
        return new();
    }

    /// <summary>
    /// Test engine over <c>wf_session_create_fake</c>: capture, clock and driver steps come from
    /// <paramref name="fake"/>; frames and events still flow through the drain thread.
    /// </summary>
    internal NativeAudioEngine(NativeFakeSession fake)
    {
        this.fake = fake;
        fake.Attach(session);
        clock = new(() => fake.Now);
    }

    /// <summary><c>wf_capabilities</c>; <see cref="AudioEngineCapabilities.None"/> when the DLL cannot be loaded.</summary>
    public AudioEngineCapabilities Capabilities => NativeAudioLibrary.Capabilities;

    /// <summary>
    /// Meeting time on the session's clock: 0 before start, then
    /// <c>BaseDurationSeconds + (QPC now - QPC at wf_session_start) / 1e7</c>. The DLL computes
    /// <c>WfFrame.offset_sec = (stream origin - session origin)/1e7 + timeline_sample/16000 + base_duration_sec</c>
    /// on the same QPC clock (100 ns units, origin read inside <c>wf_session_start</c>); the binding takes that
    /// exact origin and base from <c>wf_session_get_origin</c>, so meeting time of host time t is
    /// <c>(t - origin)/1e7 + base</c> on both sides. So a frame's
    /// <see cref="AudioFrame.OffsetSeconds"/> is this clock's reading at the instant samples[0] was captured,
    /// and <c>Clock.NowSeconds - (OffsetSeconds + 0.01)</c> is the capture-to-delivery latency of a frame.
    /// A fake session reads the virtual clock of its <see cref="NativeFakeSession"/> instead of QPC.
    /// </summary>
    public IAudioClock Clock => clock;

    /// <summary><c>wf_session_get_levels</c>; zero before start and after stop.</summary>
    public unsafe AudioLevels Levels
    {
        get
        {
            WfLevels levels;
            lock (session.Gate)
            {
                if (session.Value == 0) return default;
                if (WallflyAudioNative.wf_session_get_levels(session.Value, &levels) != WfResult.Ok) return default;
            }
            return new(levels.Me, levels.Others, levels.MeActive != 0, levels.OthersActive != 0);
        }
    }

    /// <summary><c>wf_session_get_metrics</c>; after stop the last snapshot taken before the session was destroyed.</summary>
    public AudioMetrics Metrics
    {
        get
        {
            lock (session.Gate)
                if (session.Value != 0 && ReadMetrics(session.Value) is AudioMetrics live) return live;
            return Volatile.Read(ref finalMetrics);
        }
    }

    /// <summary>
    /// <c>wf_session_get_diagnostics</c>; after stop the last snapshot taken before the session was destroyed; all zero before
    /// start. Null when this <c>WallflyAudio.dll</c> lacks the export (an older build; the recording works all the same).
    /// </summary>
    public unsafe AudioDiagnostics? Diagnostics
    {
        get
        {
            lock (session.Gate)
                if (session.Value != 0) return ReadDiagnostics(session.Value);
            if (Volatile.Read(ref finalDiagnostics) is { } last) return last;
            // Before start: all zero when the export exists (a null argument only binds it: WF_E_INVALID_ARG).
            return NativeAudioLibrary.TryOptional(static () => WallflyAudioNative.wf_session_get_diagnostics(0, null), out _) ? new AudioDiagnostics() : null;
        }
    }

    /// <summary>
    /// <c>wf_power_requests_active</c>: power requests the sessions of this process hold right now (1 per running session);
    /// null when the DLL cannot be used or lacks the export.
    /// </summary>
    public static unsafe uint? PowerRequestsActive
    {
        get
        {
            uint count = 0;
            var pointer = &count;
            return NativeAudioLibrary.TryOptional(() => WallflyAudioNative.wf_power_requests_active(pointer), out var code) && code == WfResult.Ok ? count : null;
        }
    }

    public event Action<AudioFrame>? FrameReceived;
    public event Action<AudioEngineEvent>? EventRaised;

    public Task StartAsync(AudioEngineConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        cancellationToken.ThrowIfCancellationRequested();
        var native = BuildConfig(config);
        lock (gate)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (phase != Phase.Created)
                throw new InvalidOperationException("A NativeAudioEngine runs one session; create a new engine for the next recording.");
            phase = Phase.Starting;
            startTask = Task.Run(() => StartCore(native, cancellationToken), CancellationToken.None);
            return startTask;
        }
    }

    public Task SetMicrophoneAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (deviceId?.Length > WfSessionConfig.DeviceIdChars - 1)
            throw new ArgumentException($"The device id is longer than {WfSessionConfig.DeviceIdChars - 1} characters.", nameof(deviceId));
        int code;
        lock (session.Gate)
        {
            if (session.Value == 0) throw new InvalidOperationException("The audio engine is not running.");
            code = WallflyAudioNative.wf_session_set_mic_device(session.Value, deviceId ?? "");
        }
        WfResult.Check(code, "wf_session_set_mic_device");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken = default)
    {
        Task task;
        lock (gate) task = stopTask ??= StopCoreAsync();
        return task.WaitAsync(cancellationToken);
    }

    public void Dispose()
    {
        Task stop;
        lock (gate)
        {
            if (disposed) return;
            disposed = true;
        }
        stop = StopAsync();
        // A handler on the drain thread that disposes the engine must not wait for its own thread.
        if (Thread.CurrentThread == drainThread) return;
        try { stop.Wait(StopTimeout * 3); }
        catch (AggregateException) { }
    }

    // ---- start -------------------------------------------------------------------------------

    private static unsafe WfSessionConfig BuildConfig(AudioEngineConfig config)
    {
        var native = new WfSessionConfig
        {
            Size = (uint)sizeof(WfSessionConfig),
            OthersMode = (uint)config.OthersMode,
            TargetPid = config.TargetProcessId,
            NsLevel = config.NoiseSuppressionLevel,
            EnableAec = config.EnableEchoCancellation ? 1u : 0u,
            PreferRaw = config.PreferRawMicrophone ? 1u : 0u,
            AllowDeviceLoopbackFallback = config.AllowDeviceLoopbackFallback ? 1u : 0u,
            KeepaliveMode = (uint)config.KeepAliveMode,
            TimestampMode = (uint)config.TimestampMode,
            BaseDurationSec = config.BaseDurationSeconds
        };
        WfString.Write(native.MicDeviceId, WfSessionConfig.DeviceIdChars, config.MicrophoneDeviceId, nameof(config.MicrophoneDeviceId));
        WfString.Write(native.RenderDeviceId, WfSessionConfig.DeviceIdChars, config.RenderDeviceId, nameof(config.RenderDeviceId));
        return native;
    }

    private unsafe void StartCore(WfSessionConfig config, CancellationToken cancellationToken)
    {
        nint handle = 0;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            NativeAudioLibrary.EnsureAvailable();
            int code;
            if (fake != null) code = fake.Create(&config, out handle);
            else code = WallflyAudioNative.wf_session_create(&config, &handle);
            if (code != WfResult.Ok)
                throw new AudioEngineException($"wf_session_create rejected the configuration ({WfResult.Name(code)}).", code);

            code = WallflyAudioNative.wf_session_start(handle);
            if (code != WfResult.Ok) throw new AudioEngineException($"wf_session_start failed with {WfResult.Name(code)}.", code);
            long origin;
            double baseSeconds;
            code = WallflyAudioNative.wf_session_get_origin(handle, &origin, &baseSeconds);
            if (code != WfResult.Ok) throw new AudioEngineException($"wf_session_get_origin failed with {WfResult.Name(code)}.", code);
            clock.Start(origin, baseSeconds);

            var eventHandle = WallflyAudioNative.wf_session_event_handle(handle);
            if (eventHandle == 0) throw new AudioEngineException("wf_session_event_handle returned no handle.", WfResult.Fail);
            nativeEvent = new(eventHandle);
            var thread = new Thread(DrainLoop) { IsBackground = true, Name = "WallflyAudio.Drain", Priority = ThreadPriority.AboveNormal };
            session.Attach(handle);
            handle = 0;   // owned by the engine from here on
            lock (gate)
            {
                drainThread = thread;
                phase = Phase.Running;
            }
            thread.Start();
        }
        catch
        {
            if (handle != 0) WallflyAudioNative.wf_session_destroy(handle);
            nativeEvent?.Dispose();
            nativeEvent = null;
            lock (gate) if (phase == Phase.Starting) phase = Phase.Created;
            throw;
        }
    }

    // ---- stop --------------------------------------------------------------------------------

    private async Task StopCoreAsync()
    {
        Task? starting;
        lock (gate) starting = startTask;
        if (starting != null)
        {
            try { await starting.ConfigureAwait(false); }
            catch (Exception) { /* a failed start leaves nothing to stop */ }
        }
        await Task.Run(StopCore).ConfigureAwait(false);
    }

    private void StopCore()
    {
        Thread? thread;
        lock (gate)
        {
            phase = Phase.Stopped;
            thread = drainThread;
        }
        var handle = session.Peek();
        if (handle == 0 || thread == null) return;

        // Flushes the pipeline (trailing partial frames, the 224-sample AEC tail) and queues Stopped
        // as the very last event; waits at most 2.5 s for the DSP thread. WF_E_FAIL: the DSP thread did
        // not finish, no Stopped follows (at most an Error event with WAIT_TIMEOUT is queued last).
        var code = WallflyAudioNative.wf_session_stop(handle);
        if (code == WfResult.Ok) stoppedDelivered.Wait(StopTimeout);
        else
        {
            Volatile.Write(ref stopFailure, $"WallflyAudio: wf_session_stop failed with {WfResult.Name(code)}; the capture did not finish its flush");
            Trace.TraceError(stopFailure);
        }
        // Either the drain thread already left after Stopped, or it drains what is left, reports the
        // missing Stopped and leaves now.
        quitRequested.Set();
        if (!thread.Join(StopTimeout))
        {
            // A handler is blocking the drain thread; it may still be inside wf_session_read_*, so the
            // session cannot be destroyed safely. Leak it rather than crash.
            Trace.TraceError("WallflyAudio: the drain thread did not finish; the native session is leaked.");
            return;
        }
        if (ReadMetrics(handle) is AudioMetrics last) Volatile.Write(ref finalMetrics, last);
        if (ReadDiagnostics(handle) is AudioDiagnostics diagnostics) Volatile.Write(ref finalDiagnostics, diagnostics);
        session.Detach();
        // WF_E_FAIL: a session thread did not return and was abandoned (its memory is leaked on purpose).
        // Stopped has already been raised as the last event, so this can only be logged.
        code = WallflyAudioNative.wf_session_destroy(handle);
        if (code != WfResult.Ok) Trace.TraceError($"WallflyAudio: wf_session_destroy returned {WfResult.Name(code)}; a session thread was abandoned.");
        nativeEvent?.Dispose();
        quitRequested.Dispose();
        stoppedDelivered.Dispose();
    }

    // ---- drain thread ------------------------------------------------------------------------

    private unsafe void DrainLoop()
    {
        var handle = session.Peek();
        var frames = (WfFrame*)NativeMemory.AlignedAlloc((nuint)(FrameBatch * sizeof(WfFrame)), 16);
        var events = (WfEvent*)NativeMemory.AlignedAlloc((nuint)(EventBatch * sizeof(WfEvent)), 16);
        var slots = new float[FrameBatch][];
        for (var index = 0; index < slots.Length; index++) slots[index] = new float[AudioFormat.FrameSamples];
        var waits = new WaitHandle[] { nativeEvent!, quitRequested };
        try
        {
            for (;;)
            {
                var signalled = WaitHandle.WaitAny(waits, IdleWaitMilliseconds);
                if (Drain(handle, frames, events, slots)) break;
                if (signalled != 1) continue;
                // Stop was requested and Stopped did not arrive (stop timeout, or wf_session_stop failed):
                // keep the contract (Stopped last) and make the failure visible - once, so a stop failure
                // the DLL already reported (Error with WAIT_TIMEOUT) is not reported twice.
                var sequence = (uint)(Interlocked.Read(ref lastSequence) + 1);
                var at = clock.NowSeconds;
                if (!stopErrorRaised)
                    RaiseEvent(new(AudioEngineEventKind.Error, AudioSource.Session, Sequence: sequence++, AtOffsetSeconds: at,
                        Detail: Volatile.Read(ref stopFailure) ?? "WallflyAudio: no Stopped event within the stop timeout"));
                RaiseEvent(new(AudioEngineEventKind.Stopped, AudioSource.Session, Sequence: sequence, AtOffsetSeconds: at));
                break;
            }
        }
        finally
        {
            NativeMemory.AlignedFree(frames);
            NativeMemory.AlignedFree(events);
            stoppedDelivered.Set();
        }
    }

    /// <summary>Drains everything queued; returns true once Stopped was raised.</summary>
    private unsafe bool Drain(nint handle, WfFrame* frames, WfEvent* events, float[][] slots)
    {
        for (;;)
        {
            uint count = 0;
            WallflyAudioNative.wf_session_read_events(handle, events, EventBatch, &count);
            // Every frame that was queued before these events became visible goes out first.
            DrainFrames(handle, frames, slots);
            var stopped = false;
            for (var index = 0; index < count; index++)
            {
                var item = ToEvent(&events[index]);
                Interlocked.Exchange(ref lastSequence, item.Sequence);
                if (item is { Kind: AudioEngineEventKind.Error, HResult: WfConst.StopTimeoutHResult }) stopErrorRaised = true;
                RaiseEvent(item);
                if (item.Kind == AudioEngineEventKind.Stopped) stopped = true;
            }
            if (stopped)
            {
                stoppedDelivered.Set();
                return true;
            }
            if (count < EventBatch) return false;
        }
    }

    private unsafe void DrainFrames(nint handle, WfFrame* frames, float[][] slots)
    {
        for (;;)
        {
            uint count = 0;
            WallflyAudioNative.wf_session_read_frames(handle, frames, FrameBatch, &count);
            for (var index = 0; index < count; index++)
            {
                var frame = &frames[index];
                var slot = slots[index];
                int low = Math.Min((int)frame->ValidLo, WfFrame.SampleCount);
                int high = Math.Clamp((int)frame->ValidHi, low, WfFrame.SampleCount);
                var target = slot.AsSpan();
                target[..low].Clear();
                new ReadOnlySpan<float>(frame->Samples + low, high - low).CopyTo(target[low..high]);
                target[high..].Clear();
                RaiseFrame(new((AudioSource)frame->Source, (AudioFrameFlags)frame->Flags, frame->AttemptId,
                    frame->TimelineSample, frame->OffsetSec, slot, (ushort)low, (ushort)high));
            }
            if (count < FrameBatch) return;
        }
    }

    private static unsafe AudioEngineEvent ToEvent(WfEvent* item) => new(
        (AudioEngineEventKind)item->Kind,
        (AudioSource)item->Source,
        item->AttemptId,
        (AudioGapReason)item->Reason,
        item->HResult,
        item->Sequence,
        item->AtOffsetSec,
        item->DurationSec,
        WfString.Read(item->Detail, WfEvent.DetailChars));

    private void RaiseFrame(AudioFrame frame)
    {
        try { FrameReceived?.Invoke(frame); }
        catch (Exception error) { ReportHandlerFault(error); }
    }

    private void RaiseEvent(AudioEngineEvent item)
    {
        try { EventRaised?.Invoke(item); }
        catch (Exception error) { ReportHandlerFault(error); }
    }

    private void ReportHandlerFault(Exception error)
    {
        Trace.TraceError("WallflyAudio: an audio handler threw: " + error);
        if (Interlocked.Increment(ref handlerFaults) != 1) return;
        try
        {
            EventRaised?.Invoke(new(AudioEngineEventKind.Error, AudioSource.Session, AtOffsetSeconds: clock.NowSeconds,
                Detail: "audio handler failed: " + error.GetType().Name + ": " + error.Message));
        }
        catch (Exception) { /* already reported to the trace */ }
    }

    private static unsafe AudioMetrics? ReadMetrics(nint handle)
    {
        WfMetrics metrics;
        if (WallflyAudioNative.wf_session_get_metrics(handle, &metrics) != WfResult.Ok) return null;
        return new()
        {
            ErleDb = metrics.ErleDb,
            DriftPpmMe = metrics.DriftPpmMe,
            DriftPpmOthers = metrics.DriftPpmOthers,
            JitterP95MsMe = metrics.JitterP95MsMe,
            JitterP95MsOthers = metrics.JitterP95MsOthers,
            UnfilteredFrames = (long)metrics.UnfilteredFrames,
            LateDropped = (long)metrics.LateDropped,
            ReferenceDiscarded = (long)metrics.RefDiscarded,
            InboxOverflows = (long)metrics.InboxOverflows,
            TimestampErrors = (long)metrics.TimestampErrors,
            Discontinuities = (long)metrics.Discontinuities,
            WatchdogRecoveries = (long)metrics.WatchdogRecoveries,
            Restarts = (long)metrics.Restarts,
            FramesMe = (long)metrics.FramesMe,
            FramesOthers = (long)metrics.FramesOthers,
            FramesDropped = (long)metrics.FramesDropped,
            EventsDropped = (long)metrics.EventsDropped,
            EchoCancellerResets = (long)metrics.AecResets,
            RingOverflows = (long)metrics.RingOverflows,
            DelayMs = metrics.DelayMs,
            State = (AudioEngineState)metrics.State,
            MicrophoneRaw = metrics.MicRaw != 0,
            ReferenceModeActual = (ReferenceMode)metrics.RefModeActual,
            AttemptId = metrics.AttemptId,
            OrphanedSteps = metrics.OrphanedSteps
        };
    }

    /// <summary>
    /// The size-versioned <c>WfDiagnostics</c> of a session; null when the export is missing or the call fails. Values the
    /// DLL did not write (a shorter struct of an older build) stay 0.
    /// </summary>
    private static unsafe AudioDiagnostics? ReadDiagnostics(nint handle)
    {
        var native = new WfDiagnostics { Size = (uint)sizeof(WfDiagnostics) };
        var pointer = &native;
        if (!NativeAudioLibrary.TryOptional(() => WallflyAudioNative.wf_session_get_diagnostics(handle, pointer), out var code) || code != WfResult.Ok) return null;
        return ToDiagnostics(native);
    }

    /// <summary>
    /// <c>WfDiagnostics</c> as the DLL filled it. <c>native.Size</c> is the number of bytes it wrote: a field beyond that (the
    /// four appended in 2026-11-23 from a DLL with the 72-byte struct) stays 0 whatever the buffer holds.
    /// </summary>
    internal static AudioDiagnostics ToDiagnostics(in WfDiagnostics native)
    {
        var written = (int)native.Size;
        uint Appended(uint value, int offset) => written >= offset + sizeof(uint) ? value : 0;
        return new()
        {
            PowerRequest = (PowerRequestState)native.PowerRequest,
            DspTickGapMaxMs = native.DspTickGapMaxMs,
            CaptureGapMaxMsMe = native.CaptureGapMaxMsMe,
            CaptureGapMaxMsOthers = native.CaptureGapMaxMsOthers,
            CaptureGapMaxMsReference = native.CaptureGapMaxMsReference,
            RingHighWaterMe = native.RingHighWaterMe,
            RingHighWaterOthers = native.RingHighWaterOthers,
            RingHighWaterReference = native.RingHighWaterReference,
            RingCapacitySlots = native.RingCapacitySlots,
            DspSlicesMax = native.DspSlicesMax,
            RingLossGaps = (long)native.RingLossGaps,
            RenderSwitches = Appended(native.RenderSwitches, 72),
            AecRenderResets = Appended(native.AecRenderResets, 76),
            KeepAliveFailures = Appended(native.KeepaliveFailures, 80),
            KeepAliveStarts = Appended(native.KeepaliveStarts, 84)
        };
    }

    /// <summary>The session's auto-reset event. Owned by the DLL (valid until <c>wf_session_destroy</c>), never closed here.</summary>
    private sealed class NativeEventWaitHandle : WaitHandle
    {
        public NativeEventWaitHandle(nint handle) => SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
    }
}

/// <summary>
/// Session clock of <see cref="NativeAudioEngine"/>; see <see cref="NativeAudioEngine.Clock"/> for how it
/// relates to <see cref="AudioFrame.OffsetSeconds"/>.
/// </summary>
internal sealed class NativeSessionClock(Func<long> now100ns) : IAudioClock
{
    private long origin;
    private double baseSeconds;
    private volatile bool started;

    public double NowSeconds => started ? baseSeconds + (now100ns() - origin) / 1e7 : 0;

    public void Start(long origin100ns, double baseDurationSeconds)
    {
        origin = origin100ns;
        baseSeconds = baseDurationSeconds;
        started = true;
    }

    /// <summary>QPC in 100 ns units, computed exactly like the DLL's <c>qpc_now_100ns</c> (Stopwatch is QPC on Windows).</summary>
    public static long QpcNow100ns()
    {
        var ticks = Stopwatch.GetTimestamp();
        var frequency = Stopwatch.Frequency;
        return ticks / frequency * 10_000_000 + ticks % frequency * 10_000_000 / frequency;
    }
}
