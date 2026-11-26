using Wallfly.Audio.Native;

namespace Wallfly.Audio;

/// <summary>
/// Endpoint list for the settings UI over <c>wf_devices_list</c> (active endpoints only). The first entry
/// is always the system default (<see cref="SystemDefaultId"/>), which is what an empty
/// <see cref="AudioEngineConfig.MicrophoneDeviceId"/>/<see cref="AudioEngineConfig.RenderDeviceId"/> selects;
/// the DLL resolves it to the default console endpoint. The ids are the values the session accepts.
/// The DLL enumerates on its own MTA worker thread, so calling this from the WPF UI thread is safe.
/// </summary>
public sealed class NativeAudioDeviceCatalog : IAudioDeviceCatalog
{
    public const string SystemDefaultId = "";

    public IReadOnlyList<AudioDevice> List(AudioFlow flow)
    {
        NativeAudioLibrary.EnsureAvailable();
        var result = new List<AudioDevice>
        {
            new(SystemDefaultId, flow == AudioFlow.Capture ? "System default microphone" : "System default output")
        };
        foreach (var device in NativeAudioEnumeration.Devices(flow == AudioFlow.Capture ? WfConst.FlowCapture : WfConst.FlowRender))
            result.Add(new(device.GetId(), device.GetName(), device.IsDefaultConsole != 0, device.IsDefaultCommunications != 0));
        return result;
    }
}

/// <summary><c>AudioSessionState</c> of an audio session.</summary>
public enum AudioSessionActivity
{
    Inactive = 0,
    Active = 1,
    Expired = 2
}

/// <summary>One audio session (<c>WfAudioSessionInfo</c>): which process plays or records on which endpoint.</summary>
public sealed record AudioSessionEntry(
    uint ProcessId,
    uint ParentProcessId,
    AudioSessionActivity State,
    bool IsRender,
    bool IsSystemSounds,
    float Peak,
    string ProcessName,
    string DisplayName,
    string EndpointId);

/// <summary>Audio sessions over <c>wf_audio_sessions_list</c>; the source of the Teams process picker.</summary>
public static class NativeAudioSessions
{
    /// <summary>
    /// Sessions of every active endpoint of <paramref name="flow"/> (render by default, which is where a
    /// meeting app plays); <c>null</c> lists render and capture.
    /// </summary>
    public static IReadOnlyList<AudioSessionEntry> List(AudioFlow? flow = AudioFlow.Render)
    {
        NativeAudioLibrary.EnsureAvailable();
        var native = flow switch
        {
            AudioFlow.Render => WfConst.FlowRender,
            AudioFlow.Capture => WfConst.FlowCapture,
            _ => WfConst.FlowAll
        };
        return NativeAudioEnumeration.Sessions(native)
            .Select(item => new AudioSessionEntry(item.Pid, item.ParentPid, (AudioSessionActivity)item.State, item.IsRender != 0,
                item.IsSystemSounds != 0, item.Peak, item.GetProcessName(), item.GetDisplayName(), item.GetEndpointId()))
            .ToList();
    }
}

/// <summary>The size-then-fill protocol of the two list calls, retried when endpoints appear in between.</summary>
internal static unsafe class NativeAudioEnumeration
{
    private const int Attempts = 4;
    private const int Slack = 4;

    public static WfDeviceInfo[] Devices(uint flow)
    {
        uint count = 0;
        var code = WallflyAudioNative.wf_devices_list(flow, null, 0, &count);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (code is not (WfResult.Ok or WfResult.BufferTooSmall)) break;
            var buffer = new WfDeviceInfo[count + Slack];
            fixed (WfDeviceInfo* output = buffer)
                code = WallflyAudioNative.wf_devices_list(flow, output, (uint)buffer.Length, &count);
            if (code == WfResult.Ok) return buffer[..(int)count];
        }
        WfResult.Check(code == WfResult.Ok ? WfResult.Fail : code, "wf_devices_list");
        return [];
    }

    public static WfAudioSessionInfo[] Sessions(uint flow)
    {
        uint count = 0;
        var code = WallflyAudioNative.wf_audio_sessions_list(flow, null, 0, &count);
        for (var attempt = 0; attempt < Attempts; attempt++)
        {
            if (code is not (WfResult.Ok or WfResult.BufferTooSmall)) break;
            var buffer = new WfAudioSessionInfo[count + Slack];
            fixed (WfAudioSessionInfo* output = buffer)
                code = WallflyAudioNative.wf_audio_sessions_list(flow, output, (uint)buffer.Length, &count);
            if (code == WfResult.Ok) return buffer[..(int)count];
        }
        WfResult.Check(code == WfResult.Ok ? WfResult.Fail : code, "wf_audio_sessions_list");
        return [];
    }
}
