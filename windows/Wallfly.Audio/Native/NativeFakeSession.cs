namespace Wallfly.Audio.Native;

/// <summary><c>WF_FAKE_SRC_*</c>.</summary>
internal enum FakeSource : uint
{
    Mic = 0,
    Others = 1,
    Reference = 2
}

/// <summary><c>WF_FAKE_SIGNAL_*</c>.</summary>
internal enum FakeSignal : uint
{
    DeviceLost = 1,
    FormatChange = 2,
    NoFirstPacket = 3,
    StartFails = 4,
    Clear = 5,
    HangStep = 6,
    DefaultDeviceChanged = 7,
    StreamError = 8,
    PropertyChanged = 9,
    /// <summary>Only the configured microphone fails to open (<c>hresult</c> 0 = not found); the start falls back to the default.</summary>
    ConfiguredMicFails = 10,
    /// <summary>The next <c>wf_session_start</c> fails.</summary>
    SessionStartFails = 11,
    // (2026-11-23) Fake endpoints: four render ("Fake Speakers i") and four capture endpoints, index 0 the default of every role.
    /// <summary><c>hresult</c> = render index: the console (and multimedia) default render endpoint moves there.</summary>
    DefaultRenderChanged = 12,
    /// <summary><c>hresult</c> = render index: only the communications default render endpoint moves.</summary>
    DefaultCommunicationsRenderChanged = 13,
    /// <summary><c>hresult</c> = (DEVICE_STATE_* &lt;&lt; 8) | render index.</summary>
    RenderEndpointState = 14,
    DefaultCaptureChanged = 15,
    DefaultCommunicationsCaptureChanged = 16,
    /// <summary>Every keep-alive start fails with <c>hresult</c> (0: E_FAIL) until <see cref="Clear"/> on Others.</summary>
    KeepAliveStartFails = 17,
    KeepAliveExits = 18
}

/// <summary>
/// Test-only driver for <c>wf_session_create_fake</c> and the <c>wf_fake_*</c> injection path: the DLL's
/// production pipeline (inbox, normalizer, framer, echo processor, queues) runs on virtual time, and
/// <see cref="Tick"/> executes one supervisor poll plus one 10 ms DSP tick on the calling thread.
/// <para>
/// Pass it to <see cref="NativeAudioEngine(NativeFakeSession)"/>; <see cref="NativeAudioEngine.StartAsync"/>
/// then creates the fake session, sets the virtual clock to <see cref="Origin"/> and starts it, so
/// <see cref="Origin"/> becomes the session origin (meeting offset <c>BaseDurationSeconds</c>). The engine's
/// drain thread consumes the queues exactly as for a real session. Do not call into this object
/// concurrently with <see cref="NativeAudioEngine.StopAsync"/>: in a fake session the stop flush runs on
/// the stopping thread and would become a second producer.
/// </para>
/// </summary>
internal sealed unsafe class NativeFakeSession
{
    public const long TicksPerSecond = 10_000_000;
    public const long TicksPerSample = TicksPerSecond / WfConst.SampleRate;   // 625
    public const long TicksPerFrame = TicksPerSample * WfConst.FrameSamples;  // 100 000 = 10 ms

    private NativeSessionHandle? session;
    private long now;

    /// <param name="origin100ns">Virtual QPC time of the session origin; non-zero so offset 0 is never time 0.</param>
    public NativeFakeSession(long origin100ns = 1000 * TicksPerSecond)
    {
        Origin = origin100ns;
        now = origin100ns;
    }

    /// <summary>Virtual time (100 ns) at which the engine starts the session.</summary>
    public long Origin { get; }

    /// <summary>Current virtual time in 100 ns units.</summary>
    public long Now => Volatile.Read(ref now);

    /// <summary>Virtual QPC time of input sample <paramref name="position"/> of a stream that starts at <see cref="Origin"/>.</summary>
    public long TimeOfSample(long position) => Origin + position * TicksPerSample;

    internal void Attach(NativeSessionHandle handle) => session = handle;

    /// <summary>Called by the engine: creates the fake session and sets its clock to <see cref="Origin"/>.</summary>
    internal int Create(WfSessionConfig* config, out nint handle)
    {
        nint created = 0;
        var code = WallflyAudioNative.wf_session_create_fake(config, &created);
        handle = created;
        if (code != WfResult.Ok) return code;
        code = WallflyAudioNative.wf_fake_set_now(created, Origin);
        if (code != WfResult.Ok)
        {
            WallflyAudioNative.wf_session_destroy(created);
            handle = 0;
            return code;
        }
        Volatile.Write(ref now, Origin);
        return WfResult.Ok;
    }

    /// <summary>Injects one capture packet (<c>wf_fake_push</c>); <paramref name="flags"/> takes AUDCLNT_BUFFERFLAGS_* bits.</summary>
    public void Push(FakeSource source, ReadOnlySpan<float> samples, long qpc100ns, uint rate = WfConst.SampleRate,
        ulong devicePosition = 0, uint flags = 0)
    {
        var handle = Session;
        int code;
        lock (handle.Gate)
        {
            fixed (float* data = samples)
                code = WallflyAudioNative.wf_fake_push(Live(handle), (uint)source, data, (uint)samples.Length, rate, qpc100ns, devicePosition, flags);
        }
        WfResult.Check(code, "wf_fake_push");
    }

    /// <summary>Pushes a 16 kHz packet whose first sample sits at input position <paramref name="position"/>.</summary>
    public void PushAt(FakeSource source, ReadOnlySpan<float> samples, long position) =>
        Push(source, samples, TimeOfSample(position));

    /// <summary>Moves the virtual clock (<c>wf_fake_set_now</c>); time only moves forward.</summary>
    public void SetNow(long qpc100ns)
    {
        var handle = Session;
        int code;
        lock (handle.Gate)
        {
            code = WallflyAudioNative.wf_fake_set_now(Live(handle), qpc100ns);
            if (code == WfResult.Ok) Volatile.Write(ref now, qpc100ns);
        }
        WfResult.Check(code, "wf_fake_set_now");
    }

    /// <summary>One supervisor poll plus one 10 ms DSP tick at the current virtual time (<c>wf_fake_tick</c>).</summary>
    public void Tick()
    {
        var handle = Session;
        int code;
        lock (handle.Gate) code = WallflyAudioNative.wf_fake_tick(Live(handle));
        WfResult.Check(code, "wf_fake_tick");
    }

    /// <summary>Advances the clock in 10 ms steps and ticks at every step.</summary>
    public void Advance(TimeSpan duration)
    {
        for (long step = 0; step < duration.Ticks / TicksPerFrame; step++)
        {
            SetNow(Now + TicksPerFrame);
            Tick();
        }
    }

    /// <summary>Injects a fault (<c>wf_fake_signal</c>).</summary>
    public void Signal(FakeSignal signal, FakeSource source = FakeSource.Mic, int hresult = 0)
    {
        var handle = Session;
        int code;
        lock (handle.Gate) code = WallflyAudioNative.wf_fake_signal(Live(handle), (uint)signal, (uint)source, hresult);
        WfResult.Check(code, "wf_fake_signal");
    }

    private NativeSessionHandle Session =>
        session ?? throw new InvalidOperationException("The fake session is not attached to an engine.");

    private static nint Live(NativeSessionHandle handle) => handle.Value != 0
        ? handle.Value
        : throw new InvalidOperationException("The fake session is not running (start the engine first; it is gone after stop).");
}
