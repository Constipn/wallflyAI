namespace Wallfly.Audio;

/// <summary>Where a frame or an event comes from. Mirrors <c>WF_SOURCE_*</c> of wallfly_audio.h.</summary>
public enum AudioSource
{
    /// <summary>The cleaned microphone.</summary>
    Me = 0,
    /// <summary>The meeting audio that is stored and transcribed.</summary>
    Others = 1,
    /// <summary>Echo reference only, never emitted as frames.</summary>
    Reference = 2,
    /// <summary>The session itself (Degraded, Stopped, Error, ...).</summary>
    Session = 3
}

/// <summary>Mirrors <c>WF_FRAME_*</c>.</summary>
[Flags]
public enum AudioFrameFlags
{
    None = 0,
    /// <summary>Ran with a zero echo reference.</summary>
    Unfiltered = 1,
    /// <summary>First frame after a gap in this source.</summary>
    FirstAfterGap = 2,
    /// <summary>
    /// Reserved (<c>WF_FRAME_SYNTHETIC_SILENCE</c>); <c>WallflyAudio.dll</c> never sets it: the stop flush
    /// only pushes the real cleaned echo-canceller tail out.
    /// </summary>
    SyntheticSilence = 4
}

/// <summary>Mirrors <c>WF_EVENT_*</c>.</summary>
public enum AudioEngineEventKind
{
    DeviceAcquired = 1,
    DeviceLost = 2,
    DeviceSwitched = 3,
    FormatChanged = 4,
    Gap = 5,
    Degraded = 6,
    ReferenceMissing = 7,
    ReferenceRestored = 8,
    WatchdogRecovery = 9,
    Restarting = 10,
    PermissionDenied = 11,
    DriverBlocked = 12,
    FallbackDeviceLoopback = 13,
    ClockJitterWarning = 14,
    OthersFailed = 15,
    Stopped = 16,
    Error = 17,
    /// <summary>
    /// <c>WF_EVENT_WARNING</c> (2026-11-23): never fatal, the recording goes on. Today only the silent keep-alive render stream
    /// (source Others): <c>keepalive:start failed 0x…|render:&lt;name&gt;|retry in &lt;s&gt; s</c>, <c>…|gave up</c> or
    /// <c>keepalive:stopped 0x…|…</c>; <see cref="AudioEngineEvent.HResult"/> is the failure.
    /// </summary>
    Warning = 18
}

/// <summary>Mirrors <c>WF_GAP_*</c>; only meaningful for <see cref="AudioEngineEventKind.Gap"/>.</summary>
public enum AudioGapReason
{
    InboxOverflow = 0,
    TimestampDiscontinuity = 1,
    LateFrame = 2,
    DeviceLost = 3,
    Restart = 4,
    WatchdogRecovery = 5,
    /// <summary>
    /// The echo processor's 49-frame store dropped its oldest frame (Mac "Processing buffer overflowed"):
    /// <see cref="AudioSource.Me"/> for microphone frames, <see cref="AudioSource.Reference"/> for echo
    /// reference frames (the Others frames of that time were already delivered).
    /// </summary>
    ProcessingOverflow = 6,
    /// <summary><c>WF_GAP_NONE</c>: the reason of every event that is not a <see cref="AudioEngineEventKind.Gap"/>.</summary>
    None = 7
}

/// <summary>Mirrors <c>WF_OTHERS_*</c>.</summary>
public enum OthersMode
{
    SystemExclude = 0,
    ProcessInclude = 1,
    DeviceLoopback = 2
}

/// <summary>Mirrors <c>WF_KEEPALIVE_*</c>.</summary>
public enum KeepAliveMode { None = 0, Render = 1, Clock = 2 }

/// <summary>Mirrors <c>WF_TIMESTAMP_*</c>.</summary>
public enum TimestampMode { RawQpc = 0, Smoothed = 1 }

/// <summary>Mirrors <c>WF_STATE_*</c>.</summary>
public enum AudioEngineState
{
    Idle = 0,
    Starting = 1,
    Running = 2,
    Degraded = 3,
    DriverBlocked = 4,
    OthersFailed = 5,
    Stopping = 6,
    Stopped = 7
}

/// <summary>Mirrors <c>WF_REF_MODE_*</c>.</summary>
public enum ReferenceMode { None = 0, ProcessExclude = 1, DeviceLoopback = 2 }

/// <summary>Mirrors <c>WF_CAP_*</c> of <c>wf_capabilities</c>.</summary>
[Flags]
public enum AudioEngineCapabilities
{
    None = 0,
    ProcessLoopback = 1,
    EchoCancellation = 2,
    Fake = 4
}

/// <summary>Endpoint data flow for <see cref="IAudioDeviceCatalog"/>.</summary>
public enum AudioFlow { Render = 0, Capture = 1 }

public static class AudioFormat
{
    public const int SampleRate = 16_000;
    public const int FrameSamples = 160;
    public const double FrameSeconds = FrameSamples / (double)SampleRate;
}
