using Wallfly.Audio;
using Wallfly.Core.Text;

namespace Wallfly.Core.Services;

/// <summary>What the Others channel records for one recording.</summary>
public enum MeetingAudioKind
{
    /// <summary>Process loopback excluding Wallfly (the whole system; manual choice "Whole system (per-process capture)").</summary>
    WholeSystem,
    /// <summary>Process loopback including the Teams process tree (manual choice only).</summary>
    Teams,
    /// <summary>Process loopback including another chosen app's process tree (manual choice only).</summary>
    App,
    /// <summary>
    /// Device loopback of an output endpoint: everything that plays there (the system audio). The automatic source;
    /// the stream is also the echo canceller's reference.
    /// </summary>
    DeviceLoopback
}

/// <summary>
/// The resolved meeting-audio source of one recording (port of <c>RecordingAudioSelection</c>, Windows: device loopback as
/// the automatic default). The silent-meeting-audio guard may replace it during the recording (switch to device loopback,
/// another output endpoint).
/// </summary>
/// <param name="AppName">Executable name of the included app (<c>ms-teams.exe</c> for Teams); empty for the whole system and device loopback.</param>
/// <param name="ProcessId">Root PID of the included process tree (the capture target); 0 otherwise.</param>
/// <param name="Explanation">An addition to the source line (Teams view unreadable, switched by the guard); empty otherwise.</param>
/// <param name="Automatic">Chosen by "Automatic (system output, recommended)".</param>
/// <param name="TeamsCall">
/// Live Teams speaker names apply: a Teams call was detected as active (at the start, or later by the recording's Teams
/// watch), or Teams was chosen manually as the source. Independent of what the Others channel records.
/// </param>
/// <param name="TeamsProcessId">Root PID of the Teams process tree for name attribution; 0 when unknown. Independent of <paramref name="ProcessId"/>.</param>
/// <param name="RenderDeviceId">The output endpoint of device loopback; empty = the default render endpoint.</param>
/// <param name="RenderDeviceName">Its display name when known (the source line); empty otherwise.</param>
public sealed record MeetingAudioSelection(MeetingAudioKind Kind, string AppName = "", int ProcessId = 0, string Explanation = "",
    bool Automatic = false, bool TeamsCall = false, int TeamsProcessId = 0, string RenderDeviceId = "", string RenderDeviceName = "")
{
    public static MeetingAudioSelection WholeSystem(string explanation = "", bool automatic = false) =>
        new(MeetingAudioKind.WholeSystem, Explanation: explanation, Automatic: automatic);

    public static MeetingAudioSelection DeviceLoopback(string renderDeviceId = "", bool automatic = false) =>
        new(MeetingAudioKind.DeviceLoopback, Automatic: automatic, RenderDeviceId: renderDeviceId ?? "");

    /// <summary>The audio source is the Teams process tree (process loopback, manual choice). Not whether names apply: <see cref="TeamsCall"/>.</summary>
    public bool IsTeams => Kind == MeetingAudioKind.Teams;

    public OthersMode OthersMode => Kind switch
    {
        MeetingAudioKind.WholeSystem => OthersMode.SystemExclude,
        MeetingAudioKind.DeviceLoopback => OthersMode.DeviceLoopback,
        _ => OthersMode.ProcessInclude
    };

    public string DisplayName => Kind switch
    {
        MeetingAudioKind.WholeSystem => CoreMessages.MeetingAudioWholeSystem,
        MeetingAudioKind.Teams => CoreMessages.MeetingAudioTeams,
        MeetingAudioKind.DeviceLoopback => CoreMessages.MeetingAudioSystemOutput,
        _ => AppName
    };

    /// <summary>
    /// "Meeting audio: system output · Headset (USB)", "Meeting audio: Microsoft Teams", "Meeting audio: system output ·
    /// Teams meeting cannot be reliably detected · …", "Meeting audio: system output · Speakers · switched automatically".
    /// </summary>
    public string Description =>
        CoreMessages.MeetingAudioPrefix + DisplayName
        + (Kind == MeetingAudioKind.DeviceLoopback && RenderDeviceName.Length > 0 ? " · " + RenderDeviceName : "")
        + (Explanation.Length > 0 ? " · " + Explanation : "");

    /// <summary>The guard's switch: device loopback on <paramref name="renderDeviceId"/> for the rest of the recording (names unchanged).</summary>
    public MeetingAudioSelection SwitchedToDeviceLoopback(string renderDeviceId, string? renderDeviceName) =>
        this with
        {
            Kind = MeetingAudioKind.DeviceLoopback, AppName = "", ProcessId = 0, RenderDeviceId = renderDeviceId ?? "",
            RenderDeviceName = renderDeviceName ?? "", Explanation = CoreMessages.MeetingAudioSwitchedAutomatically
        };
}

/// <summary>A manually chosen meeting app is not running (macOS <c>MeetingAudioSourceUnavailable</c>).</summary>
public sealed class MeetingAudioSourceUnavailableException(string message) : Exception(message);

/// <summary>
/// The meeting-audio source (spec 3.2, Windows). "Automatic (system output, recommended)" always records the
/// output device by device loopback: on the endpoint an audibly playing Teams session renders to, otherwise the configured
/// output device (empty: the default). Whether a Teams call is active (for live Teams names, never for the audio source) is
/// decided separately by <see cref="DetectTeamsCallAsync"/>: an unreadable Teams view is sampled again every 150 ms until
/// positive evidence or 2 s. Manual sources keep per-process capture: no app = the whole system (process loopback excluding
/// Wallfly), Teams or another app = include mode on its process tree. The stored settings are never changed.
/// </summary>
public sealed class MeetingAudioRouting(ITeamsCallSource? teams, Func<string, int?> resolveProcess, Func<double> now,
    RecordingTimings? timings = null, Func<CancellationToken, Task>? pause = null)
{
    private readonly RecordingTimings timings = timings ?? RecordingTimings.Default;

    public static bool IsTeamsApp(string app) => ProcessLookup.IsTeams(app);

    /// <summary>
    /// The Teams probe that decided the last <see cref="DetectTeamsCallAsync"/> (the last of its samples); null when no
    /// detection ran since the last <see cref="ResolveSourceAsync"/> (manual source, no Teams hook). For the log and
    /// <see cref="RecordingSessionInfo.TeamsProbe"/>.
    /// </summary>
    public TeamsCallStatus? LastProbeStatus { get; private set; }

    /// <summary>
    /// The whole decision in order: <see cref="ResolveSourceAsync"/>, then for the automatic source
    /// <see cref="DetectTeamsCallAsync"/> and <see cref="WithTeamsCall"/>. The service runs the parts itself so the Teams
    /// detection never delays the capture.
    /// </summary>
    /// <exception cref="MeetingAudioSourceUnavailableException">A manually chosen app is not running.</exception>
    /// <exception cref="OperationCanceledException">The start was abandoned (quit).</exception>
    public async Task<MeetingAudioSelection> ResolveAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        var selection = await ResolveSourceAsync(settings, cancellationToken).ConfigureAwait(false);
        if (!selection.Automatic) return selection;
        var probe = await DetectTeamsCallAsync(cancellationToken).ConfigureAwait(false);
        return WithTeamsCall(selection, probe, settings);
    }

    /// <summary>
    /// The audio source alone, without sampling Teams. Automatic: device loopback on the endpoint of an audibly playing
    /// Teams session (<see cref="ITeamsCallSource.RenderActivity"/> with Teams preferred, read off the calling thread), otherwise on
    /// <see cref="AppSettings.OutputDeviceID"/> (empty: the default render endpoint).
    /// </summary>
    /// <exception cref="MeetingAudioSourceUnavailableException">A manually chosen app is not running.</exception>
    public async Task<MeetingAudioSelection> ResolveSourceAsync(AppSettings settings, CancellationToken cancellationToken = default)
    {
        LastProbeStatus = null;
        cancellationToken.ThrowIfCancellationRequested();
        if (!settings.AutomaticMeetingAudioSource)
        {
            var app = settings.AudioSourceBundleID.Trim();
            if (app.Length == 0) return MeetingAudioSelection.WholeSystem();
            return Manual(app);
        }
        var endpoint = settings.OutputDeviceID ?? "";
        if (teams != null)
        {
            RenderSessionActivity? activity = null;
            // Teams first: a louder video in a browser must not hide the call's endpoint.
            try { activity = await Task.Run(() => teams.RenderActivity(preferTeams: true), cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { activity = null; }
            if (activity is { IsTeams: true, EndpointId.Length: > 0 } playing && playing.Peak > timings.RenderPeakThreshold) endpoint = playing.EndpointId;
        }
        cancellationToken.ThrowIfCancellationRequested();
        return MeetingAudioSelection.DeviceLoopback(endpoint, automatic: true);
    }

    /// <summary>
    /// Whether a Teams call is active (for live names only, never the audio source): Teams is sampled until
    /// <see cref="TeamsCallStatus.Active"/>, <see cref="TeamsCallStatus.Absent"/> or <see cref="TeamsCallStatus.NotRunning"/>,
    /// an unreadable view every 150 ms for at most 2 s. Null without a Teams call source.
    /// </summary>
    /// <exception cref="OperationCanceledException">The start was abandoned (quit).</exception>
    public async Task<TeamsCallProbe?> DetectTeamsCallAsync(CancellationToken cancellationToken = default)
    {
        if (teams is null) return null;
        var deadline = now() + timings.AutomaticSourceTimeout.TotalSeconds;
        while (true)
        {
            TeamsCallProbe probe;
            try { probe = await teams.SampleAsync(cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch (Exception) { probe = new(TeamsCallStatus.Unknown); }
            LastProbeStatus = probe.Status;
            cancellationToken.ThrowIfCancellationRequested();
            if (probe.Status is TeamsCallStatus.Active)
                return probe.ProcessId > 0 ? probe : probe with { ProcessId = resolveProcess(ProcessLookup.TeamsExecutable) ?? 0 };
            if (probe.Status is TeamsCallStatus.Absent or TeamsCallStatus.NotRunning) return probe;
            if (now() >= deadline) return probe;
            if (pause != null) await pause(cancellationToken).ConfigureAwait(false);
            else await Task.Delay(timings.AutomaticSourcePoll, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// The detection's outcome applied to <paramref name="selection"/>: an active call sets <see cref="MeetingAudioSelection.TeamsCall"/>
    /// and <see cref="MeetingAudioSelection.TeamsProcessId"/>; a view that stayed unreadable adds
    /// <see cref="CoreMessages.TeamsMeetingNotDetectable"/> when Teams names are wanted. The audio source is never changed.
    /// </summary>
    public static MeetingAudioSelection WithTeamsCall(MeetingAudioSelection selection, TeamsCallProbe? probe, AppSettings settings)
    {
        if (probe is not { } sampled) return selection;
        if (sampled.Status == TeamsCallStatus.Active)
            return selection with { TeamsCall = true, TeamsProcessId = sampled.ProcessId > 0 ? sampled.ProcessId : selection.TeamsProcessId };
        if (sampled.Status is TeamsCallStatus.Unknown or TeamsCallStatus.Unavailable && settings.TeamsSpeakerNames && selection.Explanation.Length == 0)
            return selection with { Explanation = CoreMessages.TeamsMeetingNotDetectable };
        return selection;
    }

    /// <summary>
    /// The check right before capture starts (macOS <c>MeetingAudioSource.requireRunning</c> inside the attempt): an included
    /// app's current root PID. Device loopback and the whole system need no process; a Teams process that vanished does not
    /// matter for them (names then simply find no call).
    /// </summary>
    /// <exception cref="MeetingAudioSourceUnavailableException">A manually chosen app is no longer running.</exception>
    public MeetingAudioSelection Confirm(MeetingAudioSelection selection)
    {
        if (selection.Kind is MeetingAudioKind.WholeSystem or MeetingAudioKind.DeviceLoopback) return selection;
        if (resolveProcess(selection.AppName) is int pid and > 0)
            return selection with { ProcessId = pid, TeamsProcessId = selection.IsTeams ? pid : selection.TeamsProcessId };
        throw Unavailable(selection.AppName);
    }

    private MeetingAudioSelection Manual(string app)
    {
        var teamsApp = IsTeamsApp(app);
        var executable = teamsApp ? ProcessLookup.TeamsExecutable : app;
        var pid = resolveProcess(executable) ?? throw Unavailable(app);
        // A manually chosen Teams source keeps macOS usesTeamsNames: names apply without a detected call.
        return teamsApp
            ? new(MeetingAudioKind.Teams, executable, pid, TeamsCall: true, TeamsProcessId: pid)
            : new(MeetingAudioKind.App, executable, pid);
    }

    private static MeetingAudioSourceUnavailableException Unavailable(string app) =>
        new(CoreMessages.MeetingAppNotOpen(IsTeamsApp(app) ? CoreMessages.MeetingAudioTeams : app));
}
