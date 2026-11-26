using System.Diagnostics;
using Wallfly.Core.Persistence;

namespace Wallfly.Core.Services;

/// <summary>The timing constants of the recording orchestration; tests shorten them.</summary>
public sealed record RecordingTimings
{
    public static RecordingTimings Default { get; } = new();

    /// <summary>Automatic source: look for an active Teams call this long (spec 3.2: 2 s) …</summary>
    public TimeSpan AutomaticSourceTimeout { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>… sampling every 150 ms while Teams answers "unknown".</summary>
    public TimeSpan AutomaticSourcePoll { get; init; } = TimeSpan.FromMilliseconds(150);
    /// <summary>
    /// A render session counts as audibly playing above this peak (−60 dBFS, the same bound as "Others silent"), so comfort
    /// noise that a working capture would also only deliver below −60 dBFS never makes the guard act.
    /// </summary>
    public float RenderPeakThreshold { get; init; } = RecordingSession.SilenceThreshold;
    /// <summary>Consecutive guard checks in which something audibly plays before the guard acts on silent meeting audio.</summary>
    public int RequiredAudibleChecks { get; init; } = 2;
    /// <summary>The previous engine keeps running at most this long until the replacement engine delivers frames.</summary>
    public TimeSpan EngineCutover { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>Auto-stop and late Teams-call detection: Teams is sampled this often during a recording (spec 6: 2 s).</summary>
    public TimeSpan AutoStopInterval { get; init; } = TimeSpan.FromSeconds(2);
    /// <summary>
    /// Auto-stop: Others audible (at least <see cref="OthersAudibleMinimum"/>, chimes not counted) within this window before a
    /// decision, while Teams has a render session, means the call still runs (the policy's 10 s absence window).
    /// </summary>
    public TimeSpan MeetingEndAudioWindow { get; init; } = TimeSpan.FromSeconds(10);
    /// <summary>
    /// Silent-meeting-audio guard: Others counts as silent when it had less than <see cref="OthersAudibleMinimum"/> of audible
    /// audio (<see cref="RecordingSession.OthersAudibleSeconds"/>) within this window on the meeting clock, and the guard only
    /// judges a capture that has run at least this long (since the start or the last switch).
    /// </summary>
    public TimeSpan SilentOthersWindow { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>Audible Others audio within <see cref="SilentOthersWindow"/> below which the meeting audio counts as silent.</summary>
    public TimeSpan OthersAudibleMinimum { get; init; } = TimeSpan.FromSeconds(1);
    /// <summary>
    /// The guard's short path: device loopback that has been silent this long (less than <see cref="OthersAudibleMinimum"/>
    /// audible within it, chimes not counted) while audio audibly plays on ANOTHER endpoint in
    /// <see cref="RequiredAudibleChecks"/> consecutive checks is retargeted at once, without waiting for
    /// <see cref="SilentOthersWindow"/> (a manual headset/speaker change while the endpoint is pinned).
    /// </summary>
    public TimeSpan OtherEndpointSilence { get; init; } = TimeSpan.FromSeconds(6);
    /// <summary>
    /// How often the guard looks (3 s). It reads the render sessions only while Others is silent;
    /// a faster poll of the audio sessions is itself suspected of disturbing the capture.
    /// </summary>
    public TimeSpan SilentOthersCheck { get; init; } = TimeSpan.FromSeconds(3);
    /// <summary>At most this many switch / reopen attempts of the guard within <see cref="GuardAttemptWindow"/>; then only the warning.</summary>
    public int GuardAttemptLimit { get; init; } = 3;
    /// <summary>The window (meeting clock) of <see cref="GuardAttemptLimit"/>.</summary>
    public TimeSpan GuardAttemptWindow { get; init; } = TimeSpan.FromSeconds(60);
    /// <summary>
    /// The guard's last resort: Others has not been heard for this long (<see cref="RecordingSession.OthersUnheardSeconds"/>,
    /// never restarted by a switch or a native endpoint move) while something audibly plays in
    /// <see cref="RequiredAudibleChecks"/> consecutive checks → the warning, even when a flapping device keeps restarting the
    /// silence measurement so that neither window ever judges. Zero disables it.
    /// </summary>
    public TimeSpan OthersUnheardLimit { get; init; } = TimeSpan.FromSeconds(120);
    /// <summary>One health line per recording in the app log this often (per-source statistics every 30 s).</summary>
    public TimeSpan HealthInterval { get; init; } = TimeSpan.FromSeconds(30);
    /// <summary>A device event (switched, lost, restored) stays in <see cref="MeetingLibraryService.AudioDeviceNotice"/> this long.</summary>
    public TimeSpan DeviceNoticeDuration { get; init; } = TimeSpan.FromSeconds(10);
}

/// <summary>
/// The optional collaborators of <see cref="MeetingLibraryService"/>. Core defines the interfaces; Wallfly.Integration
/// implements them (<c>IntegrationServices.CreateDefault()</c> returns a filled instance). Everything left null is off:
/// no Teams call source means no Teams-call detection (the automatic source still records the system output), no
/// silent-meeting-audio guard and no auto-stop; no attribution hook means no live
/// Teams names; no post-recording step means no speaker matching. The service disposes the hooks with itself.
/// </summary>
public sealed class LibraryServiceHooks : IDisposable
{
    private int disposed;

    public ISpeakerAttributionHook? SpeakerAttribution { get; init; }
    public IReadOnlyList<IPostRecordingStep> PostRecordingSteps { get; init; } = [];
    public ITeamsCallSource? TeamsCalls { get; init; }
    public ISpeakerEditor? SpeakerEditor { get; init; }
    /// <summary>Where the library pointer lives; null: <c>%LOCALAPPDATA%\Wallfly\library.json</c>.</summary>
    public LibraryPointer? LibraryPointer { get; init; }
    /// <summary>
    /// Resolves a chosen meeting app (<c>AudioSourceBundleID</c>, an executable name such as <c>ms-teams.exe</c>) to the
    /// root PID of its process tree, or null when it is not running. Null: <see cref="ProcessLookup.Resolve"/>.
    /// </summary>
    public Func<string, int?>? ResolveProcess { get; init; }
    /// <summary>Monotonic seconds for the routing deadline and the auto-stop policy. Null: <see cref="ProcessLookup.MonotonicSeconds"/>.</summary>
    public Func<double>? MonotonicClock { get; init; }
    public RecordingTimings Timings { get; init; } = RecordingTimings.Default;
    /// <summary>Owned resources (UIA thread, selector watcher, models) released by <see cref="Dispose"/>.</summary>
    public IReadOnlyList<IDisposable> Resources { get; init; } = [];

    /// <summary>The hooks that need the service (<see cref="IMeetingHostClient"/>), each once.</summary>
    internal IEnumerable<IMeetingHostClient> Clients() =>
        new object?[] { SpeakerAttribution, TeamsCalls, SpeakerEditor }.Concat(PostRecordingSteps)
            .OfType<IMeetingHostClient>().Distinct(ReferenceEqualityComparer.Instance).Cast<IMeetingHostClient>();

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        foreach (var resource in new object?[] { SpeakerAttribution, TeamsCalls, SpeakerEditor }.Concat(PostRecordingSteps).Concat(Resources)
                     .OfType<IDisposable>().Distinct(ReferenceEqualityComparer.Instance).Cast<IDisposable>())
        {
            try { resource.Dispose(); }
            catch (Exception error) { Trace.TraceError("Wallfly: disposing a hook failed: " + error.Message); }
        }
    }
}

/// <summary>The default process lookup for chosen meeting apps.</summary>
public static class ProcessLookup
{
    public const string TeamsExecutable = "ms-teams.exe";
    /// <summary>The macOS bundle id of New Teams; a library shared with a Mac may store it as the meeting app.</summary>
    public const string TeamsBundleID = "com.microsoft.teams2";

    /// <summary>True for New Teams under its Windows executable name or its macOS bundle id.</summary>
    public static bool IsTeams(string? app) =>
        string.Equals(app?.Trim(), TeamsExecutable, StringComparison.OrdinalIgnoreCase)
        || string.Equals(app?.Trim(), TeamsBundleID, StringComparison.OrdinalIgnoreCase);

    /// <summary>The oldest running process with this executable name (the root of its tree), or null.</summary>
    public static int? Resolve(string executable)
    {
        var name = Path.GetFileNameWithoutExtension((IsTeams(executable) ? TeamsExecutable : executable).Trim());
        if (name.Length == 0) return null;
        Process[] processes;
        try { processes = Process.GetProcessesByName(name); }
        catch (InvalidOperationException) { return null; }
        try
        {
            return processes
                .Select(process => (process.Id, Start: StartTime(process)))
                .OrderBy(entry => entry.Start ?? DateTime.MaxValue).ThenBy(entry => entry.Id)
                .Select(entry => (int?)entry.Id).FirstOrDefault();
        }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public static double MonotonicSeconds() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    private static DateTime? StartTime(Process process)
    {
        try { return process.StartTime; }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
}
