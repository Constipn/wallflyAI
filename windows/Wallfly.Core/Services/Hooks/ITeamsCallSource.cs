namespace Wallfly.Core.Services;

/// <summary>One Teams call read (Wallfly.Teams <c>TeamsCallState</c>). <see cref="Unknown"/> never proves anything.</summary>
public enum TeamsCallStatus
{
    /// <summary>A visible, enabled leave/hang-up control.</summary>
    Active,
    /// <summary>Read completely, a Teams web UI was readable, and no leave control exists.</summary>
    Absent,
    Unknown,
    NotRunning,
    /// <summary>The UIA reader is paused after repeated stalls; treated like <see cref="Unknown"/>.</summary>
    Unavailable
}

/// <param name="ProcessId">Root PID of the Teams process tree (the oldest <c>ms-teams.exe</c>); 0 when not running.</param>
public readonly record struct TeamsCallProbe(TeamsCallStatus Status, int ProcessId = 0);

/// <summary>
/// The loudest render audio session of the machine at one read (<see cref="ITeamsCallSource.RenderActivity"/>): system
/// sounds and Wallfly's own sessions are never candidates. <see cref="Peak"/> is 0 (and the ids empty) when no other session
/// exists.
/// </summary>
/// <param name="Peak">Peak meter value (0…1) of the session.</param>
/// <param name="EndpointId">The render endpoint the session plays on (<c>AudioSessionEntry.EndpointId</c>).</param>
/// <param name="ProcessId">The process of the session.</param>
/// <param name="ProcessName">Its executable name (<c>chrome.exe</c>); empty when unknown.</param>
/// <param name="IsTeams">The process is, or descends from, <c>ms-teams.exe</c>, <c>ms-teams_modulehost.exe</c> or <c>msteamsvdi.exe</c>.</param>
public sealed record RenderSessionActivity(float Peak, string EndpointId = "", int ProcessId = 0, string ProcessName = "", bool IsTeams = false)
{
    /// <summary>No render session besides system sounds and Wallfly.</summary>
    public static RenderSessionActivity None { get; } = new(0f);
}

/// <summary>
/// The audio sessions of the Teams process tree (<c>ms-teams.exe</c>, <c>ms-teams_modulehost.exe</c>, <c>msteamsvdi.exe</c> and
/// their descendants) at one read (<see cref="ITeamsCallSource.CallAudio"/>): the auto-stop's evidence that a call still runs
/// while the Teams window cannot show it. Process names and ids only.
/// </summary>
/// <param name="RenderActive">A Teams render session is in state Active (the call's audio stream plays).</param>
/// <param name="CaptureActive">A Teams capture session is in state Active (a call keeps the microphone stream open).</param>
/// <param name="RenderSession">The Teams process tree has any render session that has not expired (active or not).</param>
/// <param name="Detail">For the log and the stop evidence, e.g. <c>render=active(ms-teams.exe/1234) capture=none</c>.</param>
public sealed record TeamsCallAudio(bool RenderActive, bool CaptureActive, bool RenderSession, string Detail = "render=none capture=none")
{
    /// <summary>The Teams process tree has no audio session.</summary>
    public static TeamsCallAudio None { get; } = new(false, false, false);
}

/// <summary>Wallfly.Teams <c>MeetingEndPolicy</c>: armed by 2 × active, fires once after ≥ 10 s absent.</summary>
public interface IMeetingEndDetector
{
    /// <param name="time">Monotonic seconds.</param>
    /// <returns>True once, when the call has been gone long enough (again after <see cref="Veto"/>).</returns>
    bool Observe(TeamsCallStatus status, double time);

    /// <summary>
    /// The recording was not stopped after <see cref="Observe"/> fired (audio evidence of a running call): the absence is
    /// kept and the next absent sample fires again; an active sample forgets it.
    /// </summary>
    void Veto();
}

/// <summary>
/// Teams call sampling (live Teams names, the auto-stop of spec 6) and the render sessions (the automatic source's endpoint,
/// the silent-meeting-audio guard)
/// (spec 6). Implemented in Wallfly.Integration (<c>TeamsCallSource</c> over <c>TeamsCallMonitor</c>).
/// </summary>
public interface ITeamsCallSource
{
    /// <summary>One read on the Teams UIA thread (≤ 0.22 s budget, stalled reads answer <see cref="TeamsCallStatus.Unknown"/>).</summary>
    Task<TeamsCallProbe> SampleAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Peak (0…1) of the loudest render audio session of the Teams process tree rooted at <paramref name="processId"/>
    /// (<c>NativeAudioSessions.List</c>); null when it cannot be read. Called off the UI thread. Not used by the recording
    /// any more (the guard reads <see cref="RenderActivity"/>); kept for diagnostics.
    /// </summary>
    float? TeamsRenderPeak(int processId);

    /// <summary>
    /// The loudest render audio session on any active render endpoint that is neither system sounds nor Wallfly's own
    /// process (<c>NativeAudioSessions.List</c>), with its endpoint, process and whether it belongs to the Teams process
    /// tree; <see cref="RenderSessionActivity.None"/> when nothing else has a session, null when the sessions cannot be read.
    /// Called off the UI and audio threads (the automatic source's endpoint at the start; the silent-meeting-audio guard).
    /// <para>
    /// <paramref name="preferTeams"/> (the start, and the guard while a Teams call is known for the recording): when the Teams
    /// process tree has any render session that has not expired, the loudest of THOSE answers, even when it is silent and
    /// another app plays louder elsewhere; the loudest session of any process answers only when Teams has no session at all.
    /// </para>
    /// </summary>
    RenderSessionActivity? RenderActivity(bool preferTeams = false);

    /// <summary>
    /// The render AND capture audio sessions of the Teams process tree (<see cref="TeamsCallAudio"/>); null when they cannot
    /// be read. Read by the auto-stop only while the Teams UI says the call is gone (never on the UI or an audio thread):
    /// UI Automation alone never ends a recording.
    /// </summary>
    TeamsCallAudio? CallAudio();

    /// <summary>A fresh end detector for one recording.</summary>
    IMeetingEndDetector CreateMeetingEndDetector();
}
