namespace Wallfly.Audio;

/// <summary>Mirrors <c>WfSessionConfig</c>. Every field may keep its default.</summary>
public sealed class AudioEngineConfig
{
    /// <summary>Empty = system default capture endpoint.</summary>
    public string MicrophoneDeviceId { get; set; } = "";
    /// <summary>Empty = system default render endpoint.</summary>
    public string RenderDeviceId { get; set; } = "";
    public OthersMode OthersMode { get; set; } = OthersMode.SystemExclude;
    /// <summary>Only for <see cref="Wallfly.Audio.OthersMode.ProcessInclude"/>.</summary>
    public uint TargetProcessId { get; set; }
    /// <summary>0 off, 1..4 = 6/12/18/21 dB. The app uses 2.</summary>
    public uint NoiseSuppressionLevel { get; set; } = 2;
    public bool EnableEchoCancellation { get; set; } = true;
    public bool PreferRawMicrophone { get; set; } = true;
    public bool AllowDeviceLoopbackFallback { get; set; } = true;
    public KeepAliveMode KeepAliveMode { get; set; } = KeepAliveMode.None;
    public TimestampMode TimestampMode { get; set; } = TimestampMode.RawQpc;
    /// <summary>Duration of earlier sessions of this meeting; added to every frame offset.</summary>
    public double BaseDurationSeconds { get; set; }
}

/// <summary>
/// One 10 ms frame on the shared 16 kHz timeline. <see cref="Samples"/> is only valid while the
/// <see cref="IAudioEngine.FrameReceived"/> handler runs; consumers must copy what they keep.
/// <para>
/// <see cref="OffsetSeconds"/> is the meeting time of <c>Samples[0]</c>. Only
/// <c>Samples[ValidLo..ValidHi)</c> carries captured audio (<c>WfFrame.valid_lo/valid_hi</c>): a stream
/// that starts, resumes or ends inside a frame yields a partial frame whose other samples are 0 and must
/// be neither archived nor transcribed (spec 3.6: gaps are not written as silence). A full frame has
/// <c>ValidLo = 0</c>, <c>ValidHi = 160</c>, the defaults.
/// </para>
/// </summary>
public readonly record struct AudioFrame(
    AudioSource Source,
    AudioFrameFlags Flags,
    uint AttemptId,
    long TimelineSample,
    double OffsetSeconds,
    ReadOnlyMemory<float> Samples,
    ushort ValidLo = 0,
    ushort ValidHi = AudioFormat.FrameSamples)
{
    /// <summary>The samples that carry audio: <c>Samples[ValidLo..ValidHi)</c>, clamped to <see cref="Samples"/>.</summary>
    public ReadOnlyMemory<float> ValidSamples
    {
        get
        {
            var low = Math.Min((int)ValidLo, Samples.Length);
            return Samples[low..Math.Clamp((int)ValidHi, low, Samples.Length)];
        }
    }

    /// <summary>Meeting time of the first valid sample: <c>OffsetSeconds + ValidLo / 16000</c>.</summary>
    public double ValidOffsetSeconds => OffsetSeconds + Math.Min((int)ValidLo, Samples.Length) / (double)AudioFormat.SampleRate;
}

/// <summary>
/// One control-plane event. Mirrors <c>WfEvent</c>; <see cref="Reason"/> is a gap reason only for
/// <see cref="AudioEngineEventKind.Gap"/> and <see cref="AudioGapReason.None"/> (<c>WF_GAP_NONE</c>) otherwise.
/// </summary>
public readonly record struct AudioEngineEvent(
    AudioEngineEventKind Kind,
    AudioSource Source = AudioSource.Session,
    uint AttemptId = 0,
    AudioGapReason Reason = AudioGapReason.None,
    int HResult = 0,
    uint Sequence = 0,
    double AtOffsetSeconds = 0,
    double DurationSeconds = 0,
    string Detail = "")
{
    /// <summary>
    /// Spec 3.1: the microphone was acquired on the system default because the configured one failed in the same
    /// attempt (<c>WF_EVENT_DEVICE_ACQUIRED</c> on Me with detail <c>fallback:&lt;reason&gt;</c>).
    /// </summary>
    public bool IsMicrophoneFallback =>
        Kind == AudioEngineEventKind.DeviceAcquired && Source == AudioSource.Me &&
        (Detail ?? "").StartsWith(MicrophonePolicy.FallbackPrefix, StringComparison.Ordinal);

    /// <summary>
    /// The reason of a <see cref="IsMicrophoneFallback"/> event ("selected microphone missing", "USB microphone unavailable"),
    /// without a following <c>|mic:&lt;name&gt;</c> field.
    /// </summary>
    public string? MicrophoneFallbackReason =>
        IsMicrophoneFallback ? Fields.Fallback ?? "" : null;

    /// <summary>The <c>|</c>-separated fields of <see cref="Detail"/> (<c>mic:</c>, <c>render:</c>, <c>fallback:</c>, <c>keepalive:</c>).</summary>
    public AudioEventDetail Fields => AudioEventDetail.Parse(Detail);
}

/// <summary>Mirrors <c>WfLevels</c>.</summary>
public readonly record struct AudioLevels(double Me, double Others, bool MeActive, bool OthersActive);

/// <summary>Mirrors <c>WfMetrics</c>. Cumulative since the last start.</summary>
public sealed class AudioMetrics
{
    public double ErleDb { get; init; }
    public double DriftPpmMe { get; init; }
    public double DriftPpmOthers { get; init; }
    public double JitterP95MsMe { get; init; }
    public double JitterP95MsOthers { get; init; }
    public long UnfilteredFrames { get; init; }
    public long LateDropped { get; init; }
    public long ReferenceDiscarded { get; init; }
    public long InboxOverflows { get; init; }
    public long TimestampErrors { get; init; }
    public long Discontinuities { get; init; }
    public long WatchdogRecoveries { get; init; }
    public long Restarts { get; init; }
    public long FramesMe { get; init; }
    public long FramesOthers { get; init; }
    public long FramesDropped { get; init; }
    public long EventsDropped { get; init; }
    public long EchoCancellerResets { get; init; }
    public long RingOverflows { get; init; }
    public int DelayMs { get; init; }
    public AudioEngineState State { get; init; }
    public bool MicrophoneRaw { get; init; }
    public ReferenceMode ReferenceModeActual { get; init; }
    public uint AttemptId { get; init; }
    public uint OrphanedSteps { get; init; }
}

/// <summary>Mirrors <c>WF_POWER_REQUEST_*</c>: the session's power request (system, execution, display required).</summary>
public enum PowerRequestState
{
    /// <summary>Not started, stopped, or the start failed.</summary>
    None = 0,
    Held = 1,
    /// <summary>Refused by Windows: the recording runs, but the machine may sleep.</summary>
    Failed = 2
}

/// <summary>
/// Mirrors <c>WfDiagnostics</c> (recording health, per session since its start, kept after stop; reading never resets).
/// A DLL that wrote fewer bytes (an older, shorter struct) leaves the later values 0.
/// </summary>
public sealed class AudioDiagnostics
{
    public PowerRequestState PowerRequest { get; init; }
    /// <summary>Longest pause between two DSP ticks (nominal 10 ms).</summary>
    public double DspTickGapMaxMs { get; init; }
    /// <summary>Longest pause between two capture packet arrivals of a stream (over all its attempts; restarts excluded).</summary>
    public double CaptureGapMaxMsMe { get; init; }
    public double CaptureGapMaxMsOthers { get; init; }
    public double CaptureGapMaxMsReference { get; init; }
    /// <summary>Most slots ever queued in a stream's capture ring (one slot = one capture packet, typically 10 ms).</summary>
    public uint RingHighWaterMe { get; init; }
    public uint RingHighWaterOthers { get; init; }
    public uint RingHighWaterReference { get; init; }
    /// <summary>Slots per capture ring (1024); a high-water mark at this value means loss.</summary>
    public uint RingCapacitySlots { get; init; }
    /// <summary>Most 100 ms catch-up slices one DSP tick needed (1 = never behind by more than one slice).</summary>
    public uint DspSlicesMax { get; init; }
    /// <summary>Lost intervals reported because a capture ring was full (all sources).</summary>
    public long RingLossGaps { get; init; }
    /// <summary>
    /// Render endpoint moves (2026-11-23; 0 from a DLL with the 72-byte struct): the endpoint the device loopback (mode 2) or the
    /// keep-alive (modes 0/1) runs on changed - a followed default change, a lost pinned endpoint's fallback, an invalidated
    /// loopback that reopened elsewhere.
    /// </summary>
    public uint RenderSwitches { get; init; }
    /// <summary>Echo-canceller resets because the echo path moved to another render endpoint.</summary>
    public uint AecRenderResets { get; init; }
    /// <summary>Keep-alive starts that failed plus keep-alive threads that ended on their own.</summary>
    public uint KeepAliveFailures { get; init; }
    /// <summary>Successful keep-alive starts (1 per endpoint normally; more after moves and retries).</summary>
    public uint KeepAliveStarts { get; init; }
}

/// <summary>Monotonic session clock. The native engine reads QPC; fakes supply virtual time.</summary>
public interface IAudioClock
{
    /// <summary>Seconds since the clock origin, monotonic.</summary>
    double NowSeconds { get; }
}

/// <summary>QPC-backed clock. <see cref="System.Diagnostics.Stopwatch"/> is QueryPerformanceCounter on Windows.</summary>
public sealed class QpcClock : IAudioClock
{
    private readonly long origin = System.Diagnostics.Stopwatch.GetTimestamp();
    public double NowSeconds => System.Diagnostics.Stopwatch.GetElapsedTime(origin).TotalSeconds;
}

/// <summary>
/// The capture engine behind <c>WallflyAudio.dll</c>: two 16 kHz mono float32 channels (Me = cleaned
/// microphone, Others = meeting audio) plus a control-plane event stream.
/// <para>
/// <see cref="FrameReceived"/> and <see cref="EventRaised"/> are raised on the engine's own drain
/// thread, in queue order and never concurrently with each other. Handlers must not block: they hand
/// the data to an inbox that the UI thread drains.
/// </para>
/// </summary>
public interface IAudioEngine : IDisposable
{
    /// <summary>What this engine can do on this machine (<c>wf_capabilities</c>).</summary>
    AudioEngineCapabilities Capabilities { get; }

    /// <summary>The session clock the frame offsets are measured on.</summary>
    IAudioClock Clock { get; }

    /// <summary>Current meter values (<c>wf_session_get_levels</c>).</summary>
    AudioLevels Levels { get; }

    /// <summary>Current counters and health (<c>wf_session_get_metrics</c>).</summary>
    AudioMetrics Metrics { get; }

    /// <summary>
    /// Recording-health diagnostics (<c>wf_session_get_diagnostics</c>): tick and capture gaps, ring high water, ring losses,
    /// power request. Null when the engine cannot tell (an older <c>WallflyAudio.dll</c> without the export).
    /// </summary>
    AudioDiagnostics? Diagnostics => null;

    /// <summary>Frames of both channels in timeline order. Stale attempts are fenced out by the caller.</summary>
    event Action<AudioFrame>? FrameReceived;

    /// <summary>Device, gap, degradation and failure events.</summary>
    event Action<AudioEngineEvent>? EventRaised;

    /// <summary>Creates the session from <paramref name="config"/> and starts capture.</summary>
    Task StartAsync(AudioEngineConfig config, CancellationToken cancellationToken = default);

    /// <summary>Switches the microphone endpoint while running; empty or null selects the default.</summary>
    Task SetMicrophoneAsync(string? deviceId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops capture, flushes the 224-sample echo-canceller tail and raises
    /// <see cref="AudioEngineEventKind.Stopped"/> as the last event. Safe to call more than once.
    /// </summary>
    Task StopAsync(CancellationToken cancellationToken = default);
}

/// <summary>One audio endpoint (<c>WfDeviceInfo</c>), reduced to what the settings UI needs.</summary>
public sealed record AudioDevice(string Id, string Name, bool IsDefaultConsole = false, bool IsDefaultCommunications = false);

/// <summary>Endpoint enumeration, separated from <see cref="IAudioEngine"/> so settings work without a session.</summary>
public interface IAudioDeviceCatalog
{
    IReadOnlyList<AudioDevice> List(AudioFlow flow);
}
