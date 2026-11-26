using NAudio.CoreAudioApi;
using NAudio.CoreAudioApi.Interfaces;

namespace Wallfly.Teams;

/// <summary>One audio capture session (IAudioSessionManager2) on an active capture endpoint.</summary>
public sealed record MicSession(int ProcessId, string ProcessName, bool Active, bool Teams);

public sealed record MicProbeResult(bool TeamsHoldsMicrophone, IReadOnlyList<MicSession> Sessions, string? Error)
{
    /// <summary>Probe failures fail open: the caller reads Teams' UI as if Teams held the microphone.</summary>
    public bool ShouldReadTeams => TeamsHoldsMicrophone || Error is not null;
}

/// <summary>
/// Cheap pre-filter (cheap signal): does a Teams process hold an active microphone capture session?
/// Enumerates capture sessions of every active capture endpoint with NAudio (IAudioSessionManager2) and
/// matches the session's process: one of the Teams executables, or msedgewebview2.exe whose ancestor is
/// Teams. Run on an MTA thread. A failure returns <see cref="MicProbeResult.Error"/> (fail open).
/// </summary>
public sealed class MicSessionProbe(TeamsSelectorSource selectors)
{
    public MicProbeResult Probe()
    {
        try
        {
            var sessions = Sessions(DataFlow.Capture, ProcessTree.Snapshot());
            return new MicProbeResult(sessions.Any(session => session.Active && session.Teams), sessions, null);
        }
        catch (Exception error) when (IsProbeFailure(error))
        {
            return new MicProbeResult(false, [], error.GetType().Name + ": " + error.Message);
        }
    }

    /// <summary>
    /// Whether a Teams process holds an ACTIVE render or capture session (the call's audio stream or its open microphone):
    /// the call is not over, whatever the meeting view shows (screen sharing). Null when the sessions cannot be read.
    /// Run on an MTA thread (the UIA thread).
    /// </summary>
    public bool? TeamsAudioActive()
    {
        try
        {
            var tree = ProcessTree.Snapshot();
            return Sessions(DataFlow.Capture, tree).Any(session => session.Active && session.Teams)
                || Sessions(DataFlow.Render, tree).Any(session => session.Active && session.Teams);
        }
        catch (Exception error) when (IsProbeFailure(error)) { return null; }
    }

    private List<MicSession> Sessions(DataFlow flow, IReadOnlyDictionary<int, TeamsProcessInfo> tree)
    {
        var current = selectors.Current;
        var sessions = new List<MicSession>();
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in enumerator.EnumerateAudioEndPoints(flow, DeviceState.Active))
        {
            using (device)
            {
                var collection = device.AudioSessionManager.Sessions;
                for (var i = 0; i < collection.Count; i++)
                {
                    using var session = collection[i];
                    if (session.IsSystemSoundsSession) continue;
                    var pid = (int)session.GetProcessID;
                    var name = tree.TryGetValue(pid, out var process) ? process.Name : "";
                    var teams = IsTeamsProcess(tree, pid, current);
                    sessions.Add(new MicSession(pid, name, session.State == AudioSessionState.AudioSessionStateActive, teams));
                }
            }
        }
        return sessions;
    }

    private static bool IsProbeFailure(Exception error) =>
        error is System.Runtime.InteropServices.COMException or InvalidCastException or UnauthorizedAccessException or InvalidOperationException;

    public static bool IsTeamsProcess(IReadOnlyDictionary<int, TeamsProcessInfo> tree, int pid, TeamsSelectors selectors)
    {
        if (!tree.TryGetValue(pid, out var process)) return false;
        if (selectors.MicProcessNames.Contains(process.Name, StringComparer.OrdinalIgnoreCase)) return true;
        return selectors.WebViewProcessNames.Contains(process.Name, StringComparer.OrdinalIgnoreCase)
            && ProcessTree.DescendsFrom(tree, process.ParentPid, selectors.ProcessNames);
    }
}
