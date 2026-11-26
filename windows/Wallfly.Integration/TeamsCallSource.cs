using Wallfly.Audio;
using Wallfly.Core.Services;
using Wallfly.Teams;

namespace Wallfly.Integration;

/// <summary>
/// <see cref="ITeamsCallSource"/> over Wallfly.Teams: call samples from <see cref="TeamsCallMonitor"/> (watchdog-protected
/// UIA thread, ≤ 0.22 s per read), the render peak of the Teams process tree and the loudest foreign render session
/// (<see cref="RenderActivity"/>) from the audio sessions of every render endpoint (<see cref="NativeAudioSessions.List"/>),
/// and the meeting-end policy for the auto-stop.
/// </summary>
public sealed class TeamsCallSource : ITeamsCallSource
{
    /// <summary>The roots of the Teams process tree (New Teams, its module host, the VDI client).</summary>
    public static readonly IReadOnlyList<string> TeamsProcessNames = ["ms-teams.exe", "ms-teams_modulehost.exe", "msteamsvdi.exe"];

    private readonly Func<CancellationToken, Task<TeamsCallSample>> sample;
    private readonly Func<IReadOnlyList<AudioSessionEntry>> sessions;
    private readonly Func<IReadOnlyList<AudioSessionEntry>> allSessions;
    private readonly Func<IReadOnlyDictionary<int, TeamsProcessInfo>> processes;
    private readonly int ownProcessId;

    public TeamsCallSource(TeamsCallMonitor monitor)
        : this(async token => (await monitor.SampleAsync().WaitAsync(token).ConfigureAwait(false)).Sample) { }

    /// <param name="sample">One call sample (tests pass a script).</param>
    /// <param name="sessions">The render audio sessions; null: <see cref="NativeAudioSessions.List"/>.</param>
    /// <param name="processes">The process tree; null: <see cref="ProcessTree.Snapshot"/>.</param>
    /// <param name="ownProcessId">Wallfly's process, whose sessions <see cref="RenderActivity"/> skips; null: this process.</param>
    /// <param name="allSessions">
    /// Render AND capture sessions for <see cref="CallAudio"/>; null: <paramref name="sessions"/> when given (tests), else
    /// <see cref="NativeAudioSessions.List"/> of both flows (one enumeration).
    /// </param>
    public TeamsCallSource(Func<CancellationToken, Task<TeamsCallSample>> sample,
        Func<IReadOnlyList<AudioSessionEntry>>? sessions = null, Func<IReadOnlyDictionary<int, TeamsProcessInfo>>? processes = null,
        int? ownProcessId = null, Func<IReadOnlyList<AudioSessionEntry>>? allSessions = null)
    {
        this.sample = sample;
        this.sessions = sessions ?? (() => NativeAudioSessions.List(AudioFlow.Render));
        this.allSessions = allSessions ?? sessions ?? (() => NativeAudioSessions.List(null));
        this.processes = processes ?? ProcessTree.Snapshot;
        this.ownProcessId = ownProcessId ?? Environment.ProcessId;
    }

    public async Task<TeamsCallProbe> SampleAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await sample(cancellationToken).ConfigureAwait(false);
        return new TeamsCallProbe(Map(result.State), result.Pid);
    }

    public float? TeamsRenderPeak(int processId)
    {
        if (processId <= 0) return null;
        try
        {
            var tree = processes();
            var peaks = sessions()
                .Where(session => session.IsRender && !session.IsSystemSounds && InTree(tree, (int)session.ProcessId, processId))
                .Select(session => session.Peak)
                .ToList();
            return peaks.Count == 0 ? 0f : peaks.Max();
        }
        catch (Exception) { return null; }
    }

    public RenderSessionActivity? RenderActivity(bool preferTeams = false)
    {
        try
        {
            var candidates = sessions()
                .Where(session => session.IsRender && !session.IsSystemSounds && session.ProcessId != 0 && (int)session.ProcessId != ownProcessId)
                .OrderByDescending(session => float.IsFinite(session.Peak) ? session.Peak : 0f)
                .ThenByDescending(session => session.State == AudioSessionActivity.Active)
                .ToList();
            if (candidates.Count == 0) return RenderSessionActivity.None;
            // The process snapshot is only taken when there is a candidate to classify.
            var tree = processes();
            bool IsTeams(AudioSessionEntry session) => ProcessTree.DescendsFrom(tree, (int)session.ProcessId, TeamsProcessNames);
            // A known Teams call: the loudest session of the Teams process tree (even a silent one) answers, so a louder
            // video elsewhere never stands in for the call. Only without any (non-expired) Teams session the loudest of all counts.
            var loudest = (preferTeams ? candidates.FirstOrDefault(session => session.State != AudioSessionActivity.Expired && IsTeams(session)) : null)
                ?? candidates[0];
            var pid = (int)loudest.ProcessId;
            var name = loudest.ProcessName.Length > 0 ? loudest.ProcessName : tree.TryGetValue(pid, out var process) ? process.Name : "";
            return new RenderSessionActivity(float.IsFinite(loudest.Peak) ? Math.Max(0f, loudest.Peak) : 0f, loudest.EndpointId, pid, name, IsTeams(loudest));
        }
        catch (Exception) { return null; }
    }

    public TeamsCallAudio? CallAudio()
    {
        try
        {
            var candidates = allSessions()
                .Where(session => !session.IsSystemSounds && session.ProcessId != 0 && (int)session.ProcessId != ownProcessId
                    && session.State != AudioSessionActivity.Expired)
                .ToList();
            if (candidates.Count == 0) return TeamsCallAudio.None;
            var tree = processes();
            var teams = candidates.Where(session => ProcessTree.DescendsFrom(tree, (int)session.ProcessId, TeamsProcessNames)).ToList();
            var render = teams.Where(session => session.IsRender).ToList();
            var capture = teams.Where(session => !session.IsRender).ToList();
            string Describe(List<AudioSessionEntry> flow)
            {
                if (flow.Count == 0) return "none";
                var shown = flow.FirstOrDefault(session => session.State == AudioSessionActivity.Active) ?? flow[0];
                var pid = (int)shown.ProcessId;
                var name = shown.ProcessName.Length > 0 ? shown.ProcessName : tree.TryGetValue(pid, out var process) ? process.Name : "?";
                return $"{(shown.State == AudioSessionActivity.Active ? "active" : "inactive")}({name}/{pid})";
            }
            return new TeamsCallAudio(render.Any(session => session.State == AudioSessionActivity.Active),
                capture.Any(session => session.State == AudioSessionActivity.Active), render.Count > 0,
                $"render={Describe(render)} capture={Describe(capture)}");
        }
        catch (Exception) { return null; }
    }

    public IMeetingEndDetector CreateMeetingEndDetector() => new EndDetector();

    /// <summary>Whether <paramref name="pid"/> is <paramref name="root"/> or one of its descendants.</summary>
    internal static bool InTree(IReadOnlyDictionary<int, TeamsProcessInfo> tree, int pid, int root)
    {
        var seen = new HashSet<int>();
        for (var current = pid; current != 0 && seen.Add(current); )
        {
            if (current == root) return true;
            if (!tree.TryGetValue(current, out var process)) return false;
            current = process.ParentPid;
        }
        return false;
    }

    internal static TeamsCallStatus Map(TeamsCallState state) => state switch
    {
        TeamsCallState.Active => TeamsCallStatus.Active,
        TeamsCallState.Absent => TeamsCallStatus.Absent,
        TeamsCallState.NotRunning => TeamsCallStatus.NotRunning,
        TeamsCallState.Unavailable => TeamsCallStatus.Unavailable,
        _ => TeamsCallStatus.Unknown
    };

    internal static TeamsCallState Map(TeamsCallStatus status) => status switch
    {
        TeamsCallStatus.Active => TeamsCallState.Active,
        TeamsCallStatus.Absent => TeamsCallState.Absent,
        TeamsCallStatus.NotRunning => TeamsCallState.NotRunning,
        TeamsCallStatus.Unavailable => TeamsCallState.Unavailable,
        _ => TeamsCallState.Unknown
    };

    /// <summary><see cref="MeetingEndPolicy"/> (armed by two active samples, fires once after ≥ 10 s absent).</summary>
    private sealed class EndDetector : IMeetingEndDetector
    {
        private readonly MeetingEndPolicy policy = new();
        public bool Observe(TeamsCallStatus status, double time) => policy.Observe(Map(status), time);
        public void Veto() => policy.Veto();
    }
}
