namespace Wallfly.Audio;

/// <summary>One microphone of an acquire attempt (macOS <c>MicrophoneCandidate</c>).</summary>
/// <param name="Fallback">The system default replacing a configured microphone.</param>
/// <param name="Reason">Why the configured microphone is not used, when that is already known from the inventory.</param>
public sealed record MicrophoneCandidate(AudioDevice Device, bool Fallback, string? Reason);

/// <summary>
/// Spec 3.1 "Gerätewahl" (macOS <c>MicrophoneCandidates</c>): the configured microphone if present, then the system
/// default; no other device is ever used. <c>WallflyAudio.dll</c> applies this rule inside one start attempt
/// (<c>RealPlant::do_start</c>); the app uses the same order to name the microphone that runs.
/// </summary>
public static class MicrophoneCandidates
{
    /// <summary>
    /// <paramref name="devices"/> is a capture list as <see cref="IAudioDeviceCatalog.List"/> returns it (the
    /// "system default" entry with an empty id is ignored; the default is the endpoint with
    /// <see cref="AudioDevice.IsDefaultConsole"/>). <paramref name="selectedId"/> empty = system default.
    /// </summary>
    public static IReadOnlyList<MicrophoneCandidate> Order(IEnumerable<AudioDevice> devices, string? selectedId)
    {
        ArgumentNullException.ThrowIfNull(devices);
        var endpoints = devices.Where(device => device.Id.Length > 0).ToList();
        var selected = selectedId?.Trim() ?? "";
        var pinned = selected.Length == 0 ? null : endpoints.FirstOrDefault(device => SameId(device.Id, selected));
        var systemDefault = endpoints.FirstOrDefault(device => device.IsDefaultConsole);
        var result = new List<MicrophoneCandidate>();
        if (pinned != null) result.Add(new(pinned, false, null));
        if (systemDefault != null && (pinned == null || !SameId(systemDefault.Id, pinned.Id)))
            result.Add(new(systemDefault, selected.Length > 0, selected.Length > 0 && pinned == null ? MicrophonePolicy.MissingReason : null));
        return result;
    }

    /// <summary>Endpoint ids compare case-insensitively, as the MMDevice API treats them.</summary>
    public static bool SameId(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Error classification and recovery schedule of the microphone (spec 3.1, macOS <c>MicrophonePolicy</c> /
/// <c>MicrophoneRecoveryPolicy</c>). The schedule itself runs in the native supervisor; these values mirror it.
/// </summary>
public static class MicrophonePolicy
{
    /// <summary><c>E_ACCESSDENIED</c>: Windows privacy settings deny the microphone.</summary>
    public const int AccessDenied = unchecked((int)0x80070005);
    /// <summary><c>HRESULT_FROM_WIN32(ERROR_NOT_FOUND)</c>: the configured endpoint id no longer resolves.</summary>
    public const int EndpointNotFound = unchecked((int)0x80070490);
    /// <summary><c>WfEvent.detail</c> of a Me <c>WF_EVENT_DEVICE_ACQUIRED</c> that runs on the system default instead.</summary>
    public const string FallbackPrefix = "fallback:";
    public const string MissingReason = "selected microphone missing";

    /// <summary>"3 Versuche im Abstand von 0,5 s; danach degraded", then 5 background retries 1/2/4/8/16 s (cap 30 s).</summary>
    public const int Attempts = 3;
    public static readonly TimeSpan AttemptDelay = TimeSpan.FromSeconds(0.5);
    public const int BackgroundRetries = 5;
    public static readonly TimeSpan BackgroundRetryCap = TimeSpan.FromSeconds(30);

    /// <summary>The only failure that stops all retries (and that a fallback must never hide).</summary>
    public static bool PermissionDenied(int hresult) => hresult == AccessDenied;

    /// <summary>Every failure of the configured microphone allows the system default in the same attempt, except a permission error.</summary>
    public static bool FallbackAllowed(int hresult) => hresult < 0 && !PermissionDenied(hresult);

    /// <summary>Short reason for the fallback hint, e.g. "USB microphone unavailable".</summary>
    public static string UnavailableReason(string deviceName) => $"{deviceName} unavailable";

    /// <summary>Delay before background retry <paramref name="retry"/> (0-based): 1, 2, 4, 8, 16 s, never more than 30 s.</summary>
    public static TimeSpan BackgroundRetryDelay(int retry) =>
        TimeSpan.FromSeconds(Math.Min(BackgroundRetryCap.TotalSeconds, Math.Pow(2, Math.Max(0, retry))));
}
