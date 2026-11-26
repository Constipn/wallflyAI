using System.Text.Json;
using System.Text.Json.Serialization;

namespace Wallfly.Core;

/// <summary>
/// Windows addition (<see cref="Meeting.RecordingSessions"/>): how one recording attempt captured its audio, so a meeting
/// file tells afterwards which source, devices and transcription ran and when the source changed. Ids, device and process
/// names and status strings only; never text. Enum-like values are stored as strings, so a value written by a newer
/// version survives a load/save cycle.
/// </summary>
public sealed class RecordingSessionInfo
{
    /// <summary>The session id (the archive files are named <c>&lt;id&gt;-me.wav</c> / <c>-others.wav</c>).</summary>
    public Guid Id { get; set; }
    public DateTimeOffset StartedAt { get; set; }
    /// <summary>Meeting time (seconds) at which this attempt started: the duration of the earlier attempts.</summary>
    public double MeetingOffset { get; set; }
    /// <summary>The meeting-audio source: <c>deviceLoopback</c>, <c>wholeSystem</c>, <c>teams</c> or <c>app</c> (<c>MeetingAudioKind</c>).</summary>
    public string AudioKind { get; set; } = "";
    /// <summary>The capture mode of the Others channel: <c>systemExclude</c>, <c>processInclude</c> or <c>deviceLoopback</c>.</summary>
    public string OthersMode { get; set; } = "";
    /// <summary>Chosen by "Automatic (system output)".</summary>
    public bool Automatic { get; set; }
    /// <summary>
    /// An active Teams call was seen for this attempt (live names): by the automatic source at the start, or later by the
    /// recording's Teams watch (a call joined after the recording started); null when nobody looked (manual source, no Teams hook).
    /// </summary>
    public bool? TeamsCall { get; set; }
    /// <summary>The Teams probe that decided <see cref="TeamsCall"/> (<c>Active</c>, <c>Absent</c>, <c>Unknown</c>, …); null when none ran.</summary>
    public string? TeamsProbe { get; set; }
    /// <summary>The addition to the visible source line (Teams view unreadable at the start); null otherwise.</summary>
    public string? Explanation { get; set; }
    /// <summary>Executable of the included app (<c>ms-teams.exe</c>); null for the whole system and device loopback.</summary>
    public string? AppName { get; set; }
    public int? ProcessId { get; set; }
    /// <summary>The output device: the endpoint device loopback records, else the echo reference (empty id = system default).</summary>
    public RecordingDeviceInfo? RenderDevice { get; set; }
    /// <summary>The microphone that runs (named once the engine acquired it).</summary>
    public RecordingDeviceInfo? Microphone { get; set; }
    /// <summary><c>Deepgram</c>, <c>OpenAi</c> or <c>LocalWhisper</c>.</summary>
    public string? TranscriptionProvider { get; set; }
    /// <summary><c>nova-3</c>, <c>gpt-transcribe</c> (Others <c>whisper-1</c> with Teams names: <c>gpt-transcribe/whisper-1</c>), the Whisper model file.</summary>
    public string? TranscriptionModel { get; set; }
    public string? Locale { get; set; }
    /// <summary>Source and device changes during the attempt, in order (at most the first 200); null when there were none.</summary>
    public List<RecordingSourceSwitch>? Switches { get; set; }
    /// <summary>Changes beyond the first 200 of <see cref="Switches"/>, only counted (a flapping device); null when none.</summary>
    public int? DroppedSwitches { get; set; }
    /// <summary>Meeting time (seconds) at which this attempt stopped; null while it runs (and in files written before 2026-11-24).</summary>
    public double? StoppedAt { get; set; }
    /// <summary>
    /// Why it stopped (<c>RecordingStopReasons</c>): <c>manual</c>, <c>meeting ended</c>, <c>sleep</c>, <c>pipeline failed</c>,
    /// <c>engine failed</c>, <c>quit</c>; null while it runs or when unknown.
    /// </summary>
    public string? StopReason { get; set; }
    /// <summary>
    /// "meeting ended" only: what the auto-stop saw (Teams UI state and how long, the Teams audio sessions, Others audible),
    /// e.g. <c>ui=Absent for 10s; render=inactive(ms-teams.exe/1234) capture=none; others=0.0s audible in 10s</c>.
    /// </summary>
    public string? StopEvidence { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>An audio endpoint of a recording attempt.</summary>
public sealed class RecordingDeviceInfo
{
    /// <summary>The endpoint id; empty for the system default.</summary>
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}

/// <summary>One change of the meeting-audio source or an audio device during a recording attempt.</summary>
public sealed class RecordingSourceSwitch
{
    /// <summary>Meeting time in seconds.</summary>
    public double At { get; set; }
    /// <summary>The mode or channel before (<c>processInclude</c>, <c>meeting</c>, …); null when not applicable.</summary>
    public string? From { get; set; }
    /// <summary>The mode or channel after (<c>systemExclude</c>, …).</summary>
    public string? To { get; set; }
    /// <summary>The device the engine reported (its detail text) or the name of the endpoint switched to, when known.</summary>
    public string? Device { get; set; }
    /// <summary>The endpoint id a guard switch moved to; null otherwise (and for the default endpoint).</summary>
    public string? DeviceId { get; set; }
    /// <summary>
    /// Why: <c>processLoopbackSilent</c>, <c>renderEndpointChanged</c>, <c>deviceLoopbackSilent</c> (the guard),
    /// <c>deviceSwitched</c>, <c>deviceLoopbackFallback</c> (the engine), <c>includeModeSilent</c> (before 2026-11-23), ….
    /// </summary>
    public string Reason { get; set; } = "";
    [JsonExtensionData] public Dictionary<string, JsonElement>? AdditionalData { get; set; }
}
