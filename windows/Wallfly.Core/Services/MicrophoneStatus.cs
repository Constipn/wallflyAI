using Wallfly.Audio;
using Wallfly.Core.Text;

namespace Wallfly.Core.Services;

/// <summary>
/// The microphone the running recording actually uses (macOS <c>actualMicrophoneName</c>): its name and, when the
/// configured microphone failed and the system default took over in the same attempt (spec 3.1), the reason.
/// </summary>
public sealed record MicrophoneStatus(string Name, bool Fallback = false, string? Reason = null)
{
    /// <summary>"USB microphone" or "Microphone array · Fallback (selected microphone missing)".</summary>
    public string Description => Fallback ? CoreMessages.MicrophoneFallback(Name, Reason) : Name;

    /// <summary>
    /// Names the microphone of a Me <see cref="AudioEngineEventKind.DeviceAcquired"/>. The engine's own name wins
    /// (<c>mic:&lt;name&gt;</c> in the detail, 2026-11-23); without it: the configured one (<paramref name="selectedId"/>), or
    /// the system default when it is empty (the communications default the DLL opens first, else the console default) or
    /// when the event is a fallback (<see cref="AudioEngineEvent.IsMicrophoneFallback"/>: the console default).
    /// <paramref name="devices"/> is the capture list of <see cref="IAudioDeviceCatalog"/>; the candidate order is
    /// <see cref="MicrophoneCandidates.Order"/>.
    /// </summary>
    public static MicrophoneStatus Resolve(IReadOnlyList<AudioDevice> devices, string? selectedId, AudioEngineEvent acquired)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var reported = acquired.Fields.Microphone;
        var candidates = MicrophoneCandidates.Order(devices, selectedId);
        if (acquired.IsMicrophoneFallback)
        {
            // The DLL fell back to the system default: the last candidate (the default) is the one that runs.
            var used = candidates.LastOrDefault(candidate => candidate.Device.IsDefaultConsole);
            return new(reported ?? used?.Device.Name ?? CoreMessages.SystemDefaultMicrophone, true,
                acquired.MicrophoneFallbackReason is { Length: > 0 } reason ? reason : used?.Reason);
        }
        if (reported != null) return new(reported);
        var selected = selectedId?.Trim() ?? "";
        var running = selected.Length > 0
            ? devices.FirstOrDefault(device => device.Id.Length > 0 && MicrophoneCandidates.SameId(device.Id, selected))
            : devices.FirstOrDefault(device => device.Id.Length > 0 && device.IsDefaultCommunications)
              ?? devices.FirstOrDefault(device => device.Id.Length > 0 && device.IsDefaultConsole);
        return new(running?.Name ?? (selected.Length > 0 ? CoreMessages.SelectedMicrophone : CoreMessages.SystemDefaultMicrophone));
    }
}
