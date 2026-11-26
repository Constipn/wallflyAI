using System.Runtime.InteropServices;

// WallflyAudio.dll ships next to the app (and next to the test assembly); never search the
// current directory or PATH for it.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.AssemblyDirectory)]

namespace Wallfly.Audio.Native;

// Managed mirror of windows/native/src/api/wallfly_audio.h, C ABI v1.
//
// Every struct is blittable: sequential layout, default packing, fixed buffers for the wchar_t and
// float arrays, no managed references. Sizes and offsets are pinned by NativeAudioEngineTests against
// the numbers of tests/session/test_abi_layout.cpp. All entry points take raw pointers so the
// LibraryImport generator never has to marshal a struct.

/// <summary><c>WfDeviceInfo</c>, 800 bytes.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WfDeviceInfo
{
    public const int IdChars = 256;
    public const int NameChars = 128;

    public uint Flow;
    public uint State;
    public uint MixRate;
    public uint MixChannels;
    public uint IsDefaultConsole;
    public uint IsDefaultCommunications;
    public int RawSupported;
    public uint Reserved;
    public fixed char Id[IdChars];
    public fixed char Name[NameChars];

    public string GetId() { fixed (char* text = Id) return WfString.Read(text, IdChars); }
    public string GetName() { fixed (char* text = Name) return WfString.Read(text, NameChars); }
}

/// <summary><c>WfAudioSessionInfo</c>, 1048 bytes.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WfAudioSessionInfo
{
    public const int ProcessNameChars = 128;
    public const int DisplayNameChars = 128;
    public const int EndpointIdChars = 256;

    public uint Pid;
    public uint ParentPid;
    public uint State;
    public uint IsRender;
    public uint IsSystemSounds;
    public float Peak;
    public fixed char ProcessName[ProcessNameChars];
    public fixed char DisplayName[DisplayNameChars];
    public fixed char EndpointId[EndpointIdChars];

    public string GetProcessName() { fixed (char* text = ProcessName) return WfString.Read(text, ProcessNameChars); }
    public string GetDisplayName() { fixed (char* text = DisplayName) return WfString.Read(text, DisplayNameChars); }
    public string GetEndpointId() { fixed (char* text = EndpointId) return WfString.Read(text, EndpointIdChars); }
}

/// <summary><c>WfSessionConfig</c>, 1072 bytes. <see cref="Size"/> must be <c>sizeof(WfSessionConfig)</c>.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WfSessionConfig
{
    public const int DeviceIdChars = 256;

    public uint Size;
    public uint OthersMode;
    public uint TargetPid;
    public uint NsLevel;
    public uint EnableAec;
    public uint PreferRaw;
    public uint AllowDeviceLoopbackFallback;
    public uint KeepaliveMode;
    public uint TimestampMode;
    public uint Reserved0;
    public double BaseDurationSec;
    public fixed char MicDeviceId[DeviceIdChars];
    public fixed char RenderDeviceId[DeviceIdChars];
}

/// <summary>
/// <c>WfFrame</c>, 672 bytes: source @0, flags @4, attempt_id @8, valid_lo @12, valid_hi @14,
/// timeline_sample @16, offset_sec @24, samples[160] @32.
/// </summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WfFrame
{
    public const int SampleCount = 160;

    public uint Source;
    public uint Flags;
    public uint AttemptId;
    public ushort ValidLo;
    public ushort ValidHi;
    public long TimelineSample;
    public double OffsetSec;
    public fixed float Samples[SampleCount];
}

/// <summary><c>WfEvent</c>, 296 bytes.</summary>
[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal unsafe struct WfEvent
{
    public const int DetailChars = 128;

    public uint Kind;
    public uint Source;
    public uint AttemptId;
    public uint Reason;
    public int HResult;
    public uint Sequence;
    public double AtOffsetSec;
    public double DurationSec;
    public fixed char Detail[DetailChars];
}

/// <summary><c>WfLevels</c>, 16 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WfLevels
{
    public float Me;
    public float Others;
    public uint MeActive;
    public uint OthersActive;
}

/// <summary><c>WfMetrics</c>, 176 bytes.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WfMetrics
{
    public double ErleDb;
    public double DriftPpmMe;
    public double DriftPpmOthers;
    public double JitterP95MsMe;
    public double JitterP95MsOthers;
    public ulong UnfilteredFrames;
    public ulong LateDropped;
    public ulong RefDiscarded;
    public ulong InboxOverflows;
    public ulong TimestampErrors;
    public ulong Discontinuities;
    public ulong WatchdogRecoveries;
    public ulong Restarts;
    public ulong FramesMe;
    public ulong FramesOthers;
    public ulong FramesDropped;
    public ulong EventsDropped;
    public ulong AecResets;
    public ulong RingOverflows;
    public int DelayMs;
    public uint State;
    public uint MicRaw;
    public uint RefModeActual;
    public uint AttemptId;
    public uint OrphanedSteps;
}

/// <summary>
/// <c>WfDiagnostics</c>, 88 bytes (additive within ABI v1, 2026-11-23): size @0, power_request @4, dsp_tick_gap_max_ms @8,
/// capture_gap_max_ms_me/others/reference @16/24/32, ring_high_water_me/others/reference @40/44/48, ring_capacity_slots @52,
/// dsp_slices_max @56, reserved0 @60, ring_loss_gaps @64; appended (72 -> 88): render_switches @72, aec_render_resets @76,
/// keepalive_failures @80, keepalive_starts @84. Size-versioned: the caller sets <see cref="Size"/>, the DLL writes
/// min(size, its own) bytes and stores what it wrote; fields it did not write stay 0 (a 72-byte DLL leaves the last four 0).
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct WfDiagnostics
{
    /// <summary>The first <c>sizeof(WfDiagnostics)</c> (72 bytes, up to <see cref="RingLossGaps"/>).</summary>
    public const int PrefixSize = 72;

    public uint Size;
    public uint PowerRequest;
    public double DspTickGapMaxMs;
    public double CaptureGapMaxMsMe;
    public double CaptureGapMaxMsOthers;
    public double CaptureGapMaxMsReference;
    public uint RingHighWaterMe;
    public uint RingHighWaterOthers;
    public uint RingHighWaterReference;
    public uint RingCapacitySlots;
    public uint DspSlicesMax;
    public uint Reserved0;
    public ulong RingLossGaps;
    public uint RenderSwitches;
    public uint AecRenderResets;
    public uint KeepaliveFailures;
    public uint KeepaliveStarts;
}

/// <summary>Return codes (<c>WF_OK</c>, <c>WF_E_*</c>).</summary>
internal static class WfResult
{
    public const int Ok = 0;
    public const int InvalidArg = -1;
    public const int State = -2;
    public const int NotFake = -3;
    public const int Fail = -4;
    public const int BufferTooSmall = -5;
    public const int Unsupported = -6;

    public static string Name(int code) => code switch
    {
        Ok => "WF_OK",
        InvalidArg => "WF_E_INVALID_ARG",
        State => "WF_E_STATE",
        NotFake => "WF_E_NOT_FAKE",
        Fail => "WF_E_FAIL",
        BufferTooSmall => "WF_E_BUFFER_TOO_SMALL",
        Unsupported => "WF_E_UNSUPPORTED",
        _ => "WF_E(" + code + ")"
    };

    /// <summary>Throws an <see cref="AudioEngineException"/> (or <see cref="InvalidOperationException"/> for WF_E_STATE).</summary>
    public static void Check(int code, string function)
    {
        if (code == Ok) return;
        var message = $"{function} failed with {Name(code)}.";
        if (code == State) throw new InvalidOperationException(message);
        throw new AudioEngineException(message, code);
    }
}

/// <summary>Constants of the ABI that have no managed enum of their own.</summary>
internal static class WfConst
{
    public const uint AbiVersion = 1;
    public const uint FrameSamples = 160;
    public const uint SampleRate = 16000;

    public const uint CapProcessLoopback = 0x1;
    public const uint CapAec3 = 0x2;
    public const uint CapFake = 0x4;

    public const uint FlowRender = 0;
    public const uint FlowCapture = 1;
    public const uint FlowAll = 2;

    public const uint FrameUnfiltered = 0x1;
    public const uint FrameFirstAfterGap = 0x2;
    /// <summary>Reserved; the DLL never sets it (the stop flush only pushes the real cleaned tail out).</summary>
    public const uint FrameSyntheticSilence = 0x4;

    public const uint EventStopped = 16;
    public const uint EventError = 17;
    /// <summary><c>WF_EVENT_WARNING</c> (additive within ABI v1, 2026-11-23): never fatal.</summary>
    public const uint EventWarning = 18;
    public const uint GapProcessingOverflow = 6;
    /// <summary><c>WfEvent.reason</c> of every event that is not a gap (additive within ABI v1).</summary>
    public const uint GapNone = 7;

    /// <summary><c>HRESULT_FROM_WIN32(WAIT_TIMEOUT)</c>: the Error event of a stop whose DSP thread did not finish.</summary>
    public const int StopTimeoutHResult = unchecked((int)0x80070102);

    /// <summary>
    /// The 20 exports of ABI v1, in header order (<c>wf_session_get_origin</c> was added within v1, strictly
    /// additive; a DLL without it is treated as incompatible).
    /// </summary>
    public static readonly string[] Exports =
    [
        "wf_abi_version", "wf_capabilities", "wf_devices_list", "wf_audio_sessions_list",
        "wf_session_create", "wf_session_create_fake", "wf_session_start", "wf_session_set_mic_device",
        "wf_session_stop", "wf_session_destroy", "wf_session_event_handle", "wf_session_read_frames",
        "wf_session_read_events", "wf_session_get_levels", "wf_session_get_metrics", "wf_session_get_origin",
        "wf_fake_push", "wf_fake_set_now", "wf_fake_tick", "wf_fake_signal"
    ];

    /// <summary>
    /// Exports added within v1 that the binding treats as optional (2026-11-23): an older DLL without them still records,
    /// only <see cref="IAudioEngine.Diagnostics"/> and <see cref="NativeAudioEngine.PowerRequestsActive"/> are then null.
    /// </summary>
    public static readonly string[] OptionalExports = ["wf_session_get_diagnostics", "wf_power_requests_active"];
}

/// <summary>Fixed-size, NUL-terminated wchar_t arrays of the ABI.</summary>
internal static unsafe class WfString
{
    public static string Read(char* text, int capacity)
    {
        var span = new ReadOnlySpan<char>(text, capacity);
        var length = span.IndexOf('\0');
        if (length < 0) length = capacity;
        return length == 0 ? string.Empty : new string(span[..length]);
    }

    /// <summary>
    /// Writes <paramref name="value"/> NUL terminated. An id that does not fit is rejected instead of
    /// truncated, because a truncated endpoint id would silently open a different (default) device.
    /// </summary>
    public static void Write(char* destination, int capacity, string? value, string parameter)
    {
        var span = new Span<char>(destination, capacity);
        span.Clear();
        if (string.IsNullOrEmpty(value)) return;
        if (value.Length > capacity - 1)
            throw new ArgumentException($"The device id is longer than {capacity - 1} characters.", parameter);
        value.AsSpan().CopyTo(span);
    }
}

/// <summary>P/Invoke entry points of <c>WallflyAudio.dll</c> (the 20 required exports of ABI v1 and the optional ones).</summary>
internal static unsafe partial class WallflyAudioNative
{
    public const string Library = "WallflyAudio";

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial uint wf_abi_version();

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_capabilities(uint* flags);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_devices_list(uint flow, WfDeviceInfo* output, uint capacity, uint* count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_audio_sessions_list(uint flow, WfAudioSessionInfo* output, uint capacity, uint* count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_create(WfSessionConfig* config, nint* session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_create_fake(WfSessionConfig* config, nint* session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_start(nint session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_set_mic_device(nint session, string? deviceId);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_stop(nint session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_destroy(nint session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint wf_session_event_handle(nint session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_read_frames(nint session, WfFrame* output, uint capacity, uint* count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_read_events(nint session, WfEvent* output, uint capacity, uint* count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_get_levels(nint session, WfLevels* output);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_get_metrics(nint session, WfMetrics* output);

    /// <summary>The session clock origin (QPC, 100 ns) and base duration; WF_E_STATE before <c>wf_session_start</c>.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_get_origin(nint session, long* originQpc100ns, double* baseDurationSec);

    /// <summary>Optional (see <see cref="WfConst.OptionalExports"/>): the session's recording-health diagnostics.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_session_get_diagnostics(nint session, WfDiagnostics* output);

    /// <summary>Optional (see <see cref="WfConst.OptionalExports"/>): power requests the DLL's sessions hold right now.</summary>
    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_power_requests_active(uint* count);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_fake_push(nint session, uint source, float* samples, uint frames, uint rate,
        long qpc100ns, ulong devicePosition, uint flags);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_fake_set_now(nint session, long qpc100ns);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_fake_tick(nint session);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf16)]
    public static partial int wf_fake_signal(nint session, uint signal, uint source, int hresult);
}

/// <summary>Load state of the DLL, probed once per process.</summary>
internal static unsafe class NativeAudioLibrary
{
    private static readonly Lazy<(AudioEngineCapabilities Capabilities, string? Error)> probe = new(Probe);

    /// <summary><c>wf_capabilities</c>; <see cref="AudioEngineCapabilities.None"/> when the DLL is missing or incompatible.</summary>
    public static AudioEngineCapabilities Capabilities => probe.Value.Capabilities;

    /// <summary>Why the DLL cannot be used, or null when it can.</summary>
    public static string? UnavailableReason => probe.Value.Error;

    /// <summary>0 not tried yet, 1 present, −1 missing (an older DLL): the optional diagnostics exports.</summary>
    private static int diagnosticsExports;

    /// <summary>
    /// Runs <paramref name="call"/> against an optional export: false (and every later call too) once the DLL turned out not
    /// to have it (<see cref="EntryPointNotFoundException"/>), or when the DLL cannot be used at all.
    /// </summary>
    public static bool TryOptional(Func<int> call, out int code)
    {
        code = WfResult.Unsupported;
        if (Volatile.Read(ref diagnosticsExports) < 0 || probe.Value.Error != null) return false;
        try { code = call(); }
        catch (EntryPointNotFoundException)
        {
            Volatile.Write(ref diagnosticsExports, -1);
            System.Diagnostics.Trace.TraceWarning("WallflyAudio: this WallflyAudio.dll has no recording diagnostics (an older build); they stay off.");
            return false;
        }
        Volatile.Write(ref diagnosticsExports, 1);
        return true;
    }

    /// <summary>Throws <see cref="NotSupportedException"/> when the DLL is missing or speaks another ABI.</summary>
    public static void EnsureAvailable()
    {
        if (probe.Value.Error is string error) throw new NotSupportedException(error);
    }

    private static (AudioEngineCapabilities, string?) Probe()
    {
        try
        {
            var version = WallflyAudioNative.wf_abi_version();
            if (version != WfConst.AbiVersion)
                return (AudioEngineCapabilities.None, $"WallflyAudio.dll speaks ABI v{version}; this build expects v{WfConst.AbiVersion}.");
            uint flags = 0;
            var code = WallflyAudioNative.wf_capabilities(&flags);
            if (code != WfResult.Ok) return (AudioEngineCapabilities.None, $"wf_capabilities failed with {WfResult.Name(code)}.");
            // Binds the last export added within v1: an older v1 build lacks it (null pointers are WF_E_INVALID_ARG).
            WallflyAudioNative.wf_session_get_origin(0, null, null);
            return ((AudioEngineCapabilities)flags, null);
        }
        catch (EntryPointNotFoundException error)
        {
            return (AudioEngineCapabilities.None, "WallflyAudio.dll is incompatible (an ABI v1 export is missing): " + error.Message);
        }
        catch (Exception error) when (error is DllNotFoundException or BadImageFormatException)
        {
            return (AudioEngineCapabilities.None, "WallflyAudio.dll could not be loaded from the application directory: " + error.Message);
        }
    }
}

/// <summary>
/// The opaque session pointer plus the lock that keeps every call made from outside the drain thread
/// (levels, metrics, mic switch, fake injection) from racing <c>wf_session_destroy</c>.
/// </summary>
internal sealed class NativeSessionHandle
{
    public object Gate { get; } = new();
    private nint value;

    /// <summary>Read under <see cref="Gate"/>; 0 before start and after destroy.</summary>
    public nint Value => value;

    public void Attach(nint handle) { lock (Gate) value = handle; }

    /// <summary>Clears the handle; after this returns no caller holding <see cref="Gate"/> can still use it.</summary>
    public nint Detach() { lock (Gate) { var handle = value; value = 0; return handle; } }

    /// <summary>The handle for the drain and stop paths, which run strictly before <see cref="Detach"/>.</summary>
    public nint Peek() => Volatile.Read(ref value);
}
