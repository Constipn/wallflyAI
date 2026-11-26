namespace Wallfly.Audio;

/// <summary>Virtual clock for tests and the UI smoke mode.</summary>
public sealed class FakeAudioClock : IAudioClock
{
    private double now;
    public double NowSeconds => Volatile.Read(ref now);
    public void Advance(double seconds) => Volatile.Write(ref now, NowSeconds + Math.Max(0, seconds));
    public void SetNow(double seconds) => Volatile.Write(ref now, Math.Max(NowSeconds, seconds));
}

/// <summary>
/// Deterministic <see cref="IAudioEngine"/> without WASAPI: tests and <c>--smoke-test</c> push frames
/// and events directly. Frames are delivered synchronously on the pushing thread, which keeps the
/// ordering guarantee of the real drain thread.
/// </summary>
public sealed class FakeAudioEngine : IAudioEngine
{
    private readonly object gate = new();
    private AudioLevels levels;
    private AudioMetrics metrics = new() { State = AudioEngineState.Idle };
    private AudioDiagnostics? diagnostics = new();
    private uint sequence;
    private readonly Dictionary<AudioSource, long> timeline = new();
    private int disposed;

    public FakeAudioClock FakeClock { get; } = new();
    public IAudioClock Clock => FakeClock;
    public AudioEngineCapabilities Capabilities { get; init; } = AudioEngineCapabilities.Fake | AudioEngineCapabilities.EchoCancellation;
    public AudioEngineConfig? StartedWith { get; private set; }
    public bool IsStarted { get; private set; }
    public bool IsStopped { get; private set; }
    public bool IsDisposed => Volatile.Read(ref disposed) != 0;
    public string? MicrophoneDeviceId { get; private set; }
    /// <summary>Lets a test stall, cancel or fail the stop step.</summary>
    public Func<Task>? OnStop { get; init; }
    /// <summary>Current (re)start counter; <see cref="Restart"/> fences older frames out.</summary>
    public uint AttemptId { get; private set; } = 1;
    /// <summary>
    /// Spec 3.1 microphone fallback: when set, the microphone of the next start or switch is acquired on the system
    /// default with this reason (the Me <see cref="AudioEngineEventKind.DeviceAcquired"/> carries <c>fallback:&lt;reason&gt;</c>,
    /// as <c>WallflyAudio.dll</c> reports it).
    /// </summary>
    public string? MicrophoneFallbackReason { get; set; }
    /// <summary>
    /// The friendly name the DLL reports for the microphone that runs (2026-11-23: <c>mic:&lt;name&gt;</c> on the Me
    /// <see cref="AudioEngineEventKind.DeviceAcquired"/>, after a fallback as <c>fallback:&lt;reason&gt;|mic:&lt;name&gt;</c>);
    /// null = no name (an older DLL), the detail stays as before.
    /// </summary>
    public string? MicrophoneName { get; set; }
    /// <summary>
    /// The friendly name of the output endpoint a device-loopback start reports (<c>render:&lt;name&gt;</c> on the Others
    /// <see cref="AudioEngineEventKind.DeviceAcquired"/>); null = no detail.
    /// </summary>
    public string? RenderName { get; set; }
    /// <summary>
    /// AP3 pinned-output fallback: when set, a device-loopback start on a pinned endpoint reports that the endpoint did not
    /// open and the default was recorded instead (<c>fallback:&lt;reason&gt;|render:&lt;RenderName&gt;</c> on the Others
    /// <see cref="AudioEngineEventKind.DeviceAcquired"/>, as <c>WallflyAudio.dll</c> does).
    /// </summary>
    public string? RenderFallbackReason { get; set; }

    public event Action<AudioFrame>? FrameReceived;
    public event Action<AudioEngineEvent>? EventRaised;

    public AudioLevels Levels { get { lock (gate) return levels; } }
    public AudioMetrics Metrics { get { lock (gate) return metrics; } }
    /// <summary>What <see cref="IAudioEngine.Diagnostics"/> answers: all zero until <see cref="SetDiagnostics"/>; null mimics an older DLL.</summary>
    public AudioDiagnostics? Diagnostics { get { lock (gate) return diagnostics; } }

    public Task StartAsync(AudioEngineConfig config, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(config);
        StartedWith = config; IsStarted = true; MicrophoneDeviceId = config.MicrophoneDeviceId;
        lock (gate) metrics = new() { State = AudioEngineState.Running, AttemptId = AttemptId };
        RaiseMicrophoneAcquired();
        var render = config.OthersMode == OthersMode.DeviceLoopback && RenderName != null ? AudioEventDetail.RenderPrefix + RenderName : "";
        var fallback = config.OthersMode == OthersMode.DeviceLoopback && config.RenderDeviceId.Length > 0 && RenderFallbackReason != null;
        Raise(new(AudioEngineEventKind.DeviceAcquired, AudioSource.Others, AttemptId,
            HResult: fallback ? MicrophonePolicy.EndpointNotFound : 0,
            Detail: fallback ? AudioEventDetail.FallbackPrefix + RenderFallbackReason + (render.Length > 0 ? "|" + render : "") : render));
        return Task.CompletedTask;
    }

    /// <summary>
    /// What the DLL reports when it moved the output endpoint by itself (2026-11-23): <see cref="AudioEngineEventKind.DeviceSwitched"/>
    /// with <c>render:&lt;name&gt;</c> on Others (device loopback) or Reference (process-loopback modes: keep-alive and echo path).
    /// </summary>
    public void RaiseRenderSwitched(string name, AudioSource source = AudioSource.Others, double atOffset = 0) =>
        Raise(new(AudioEngineEventKind.DeviceSwitched, source, AttemptId, AtOffsetSeconds: atOffset, Detail: AudioEventDetail.RenderPrefix + name));

    public Task SetMicrophoneAsync(string? deviceId, CancellationToken cancellationToken = default)
    {
        MicrophoneDeviceId = deviceId ?? "";
        Restart();
        Raise(new(AudioEngineEventKind.DeviceSwitched, AudioSource.Me, AttemptId));
        RaiseMicrophoneAcquired();
        return Task.CompletedTask;
    }

    /// <summary>The Me <see cref="AudioEngineEventKind.DeviceAcquired"/> of the current attempt, with the fallback marker when one is set.</summary>
    public void RaiseMicrophoneAcquired()
    {
        var fallback = MicrophoneDeviceId is { Length: > 0 } && MicrophoneFallbackReason != null;
        var name = MicrophoneName is { } microphone ? AudioEventDetail.MicrophonePrefix + microphone : "";
        Raise(new(AudioEngineEventKind.DeviceAcquired, AudioSource.Me, AttemptId,
            HResult: fallback ? MicrophonePolicy.EndpointNotFound : 0,
            Detail: fallback ? MicrophonePolicy.FallbackPrefix + MicrophoneFallbackReason + (name.Length > 0 ? "|" + name : "") : name));
    }

    public async Task StopAsync(CancellationToken cancellationToken = default)
    {
        IsStopped = true;
        lock (gate) metrics = new() { State = AudioEngineState.Stopped, AttemptId = AttemptId };
        if (OnStop != null) await OnStop().ConfigureAwait(false);
        Raise(new(AudioEngineEventKind.Stopped));
    }

    public void Dispose() => Interlocked.Exchange(ref disposed, 1);

    /// <summary>Starts a new attempt; frames pushed with an older id must be dropped by the consumer.</summary>
    public uint Restart() => ++AttemptId;

    public void Raise(AudioEngineEvent engineEvent)
    {
        lock (gate) engineEvent = engineEvent with { Sequence = ++sequence };
        EventRaised?.Invoke(engineEvent);
    }

    public void PushGap(AudioSource source, double atOffset, double duration, AudioGapReason reason) =>
        Raise(new(AudioEngineEventKind.Gap, source, AttemptId, reason, AtOffsetSeconds: atOffset, DurationSeconds: duration));

    /// <summary>
    /// Pushes one 160-sample frame. <paramref name="samples"/> is padded or truncated to the frame size.
    /// <paramref name="validLo"/>/<paramref name="validHi"/> make it a partial frame like the DLL's: samples
    /// outside that range are set to 0 (they were never captured).
    /// </summary>
    public void PushFrame(AudioSource source, ReadOnlyMemory<float> samples, double offsetSeconds,
        long timelineSample = -1, uint? attemptId = null, AudioFrameFlags flags = AudioFrameFlags.None,
        ushort validLo = 0, ushort validHi = AudioFormat.FrameSamples)
    {
        if (validLo >= validHi || validHi > AudioFormat.FrameSamples)
            throw new ArgumentOutOfRangeException(nameof(validHi), "A frame needs 0 <= validLo < validHi <= 160.");
        var frame = new float[AudioFormat.FrameSamples];
        samples.Span[..Math.Min(samples.Length, frame.Length)].CopyTo(frame);
        frame.AsSpan(0, validLo).Clear();
        frame.AsSpan(validHi).Clear();
        long position;
        lock (gate)
        {
            if (timelineSample >= 0) timeline[source] = timelineSample;
            else if (!timeline.TryGetValue(source, out var current)) timeline[source] = 0;
            else timeline[source] = current + AudioFormat.FrameSamples;
            position = timeline[source];
        }
        UpdateLevel(source, frame);
        FrameReceived?.Invoke(new(source, flags, attemptId ?? AttemptId, position, offsetSeconds, frame, validLo, validHi));
    }

    /// <summary>Pushes <paramref name="seconds"/> of a constant value as consecutive 10 ms frames.</summary>
    public void PushConstant(AudioSource source, float value, double seconds, double startOffset = 0, uint? attemptId = null)
    {
        var frames = Math.Max(1, (int)Math.Round(seconds / AudioFormat.FrameSeconds));
        var buffer = new float[AudioFormat.FrameSamples];
        buffer.AsSpan().Fill(value);
        for (var index = 0; index < frames; index++)
            PushFrame(source, buffer, startOffset + index * AudioFormat.FrameSeconds,
                timelineSample: index == 0 ? 0 : -1, attemptId: attemptId);
    }

    public void SetLevels(AudioLevels value) { lock (gate) levels = value; }
    public void SetMetrics(AudioMetrics value) { lock (gate) metrics = value; }
    public void SetDiagnostics(AudioDiagnostics? value) { lock (gate) diagnostics = value; }

    private void UpdateLevel(AudioSource source, float[] frame)
    {
        var energy = 0.0;
        foreach (var sample in frame) energy += sample * (double)sample;
        var rms = Math.Sqrt(energy / frame.Length);
        var level = rms <= 1e-5 ? 0 : Math.Clamp((20 * Math.Log10(rms) + 60) / 60, 0, 1);
        lock (gate)
            levels = source == AudioSource.Me
                ? levels with { Me = level, MeActive = true }
                : levels with { Others = level, OthersActive = true };
    }
}

/// <summary>Fixed list for tests and <c>--smoke-test</c>.</summary>
public sealed class FakeAudioDeviceCatalog(params AudioDevice[] devices) : IAudioDeviceCatalog
{
    public IReadOnlyList<AudioDevice> List(AudioFlow flow) =>
        devices.Length > 0 ? devices : [new(NativeAudioDeviceCatalog.SystemDefaultId, "Test device")];
}
