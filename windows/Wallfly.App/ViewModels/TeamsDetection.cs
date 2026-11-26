namespace Wallfly.App.ViewModels;

/// <summary>What the settings show about Wallfly Detector: one status line, the helper's own message, the sign-in start and an action.</summary>
/// <param name="Line">"Helper: running · last check 3 s ago", "Helper: not running", "Disabled", …</param>
/// <param name="Detail">The helper's last status message (status.json), when it is fresh.</param>
/// <param name="Autostart">Whether the helper starts at sign-in (HKCU Run key and Task Manager's startup state).</param>
/// <param name="ActionText">"Start helper now" or "Restart helper"; null when nothing needs to be done.</param>
/// <param name="Error">The last failure (writing configuration.json, starting or stopping the helper).</param>
public sealed record DetectorStatusInfo(string Line, string? Detail = null, string? Autostart = null, string? ActionText = null, string? Error = null)
{
    public static DetectorStatusInfo Unavailable { get; } = new(Strings.DetectorDisabled);
}

/// <summary>
/// The Teams detection helper as the settings see it. The shell implements it (Shell\DetectorIntegration); the settings
/// view model only reads the status and forwards the user's actions.
/// </summary>
public interface ITeamsDetection
{
    DetectorStatusInfo Status { get; }
    /// <summary>Progress of "Create Teams diagnostics"; null before the first request.</summary>
    string? DiagnosticStatus { get; }
    /// <summary>Raised on the UI thread whenever <see cref="Status"/> or <see cref="DiagnosticStatus"/> may have changed (every 2 s while someone listens).</summary>
    event Action? StatusChanged;

    /// <summary>"Detect Teams meetings automatically" and the microphone pre-filter: configuration.json, sign-in start, helper start.</summary>
    void Apply(bool enabled, bool micPrefilter);
    /// <summary>The settings window closed: an applied but unsaved toggle is undone (configuration.json follows settings.json).</summary>
    void ApplySavedSettings();
    /// <summary>"Start helper now" / "Restart helper".</summary>
    void StartHelper();
    void OpenDetectionFolder();
    /// <summary>"Create Teams diagnostics": a structure-only report from the helper (no names, no texts).</summary>
    void RequestDiagnostics();
    void Refresh();
}

/// <summary>What a "Take notes" click from Wallfly Detector did.</summary>
public enum DetectedMeetingOutcome
{
    /// <summary>A new note opened and its recording is starting.</summary>
    NoteOpened,
    /// <summary>A recording was already running (or starting/stopping): its note is shown instead.</summary>
    ShowedRunning,
    /// <summary>AI processing, the setup or the quit flow is running: nothing happened and the request stays valid.</summary>
    Busy,
    /// <summary>The request was unknown, stale (≥ 120 s) or already used.</summary>
    Rejected,
}

/// <param name="Recording">Completes when the recording of a new note has started (or failed); completed otherwise.</param>
public sealed record DetectedMeetingResult(DetectedMeetingOutcome Outcome, Task Recording)
{
    /// <summary>The pipe answers "ok" for these; the helper then hides its popup.</summary>
    public bool Accepted => Outcome is DetectedMeetingOutcome.NoteOpened or DetectedMeetingOutcome.ShowedRunning;
}
