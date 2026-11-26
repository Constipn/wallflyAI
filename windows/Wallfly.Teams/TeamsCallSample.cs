namespace Wallfly.Teams;

/// <summary>Result of one Teams read (spec §6). <see cref="Unknown"/> is never evidence that a call ended.</summary>
public enum TeamsCallState
{
    /// <summary>A visible, enabled leave/hang-up control was found.</summary>
    Active,
    /// <summary>The read completed, at least one Teams web UI was readable, and no leave control exists.</summary>
    Absent,
    /// <summary>Incomplete, unreadable, initializing or minimized: no conclusion.</summary>
    Unknown,
    /// <summary>No Teams process.</summary>
    NotRunning,
    /// <summary>The UIA reader is paused after repeated stalls (UiaThread watchdog); like Unknown.</summary>
    Unavailable,
}

/// <summary>A sample tagged with the Teams process it belongs to (a new process resets the policies).</summary>
public readonly record struct TeamsCallSample(TeamsCallState State, int Pid)
{
    public static TeamsCallSample Active(int pid) => new(TeamsCallState.Active, pid);
    public static TeamsCallSample Absent(int pid) => new(TeamsCallState.Absent, pid);
    public static readonly TeamsCallSample Unknown = new(TeamsCallState.Unknown, 0);
    public static readonly TeamsCallSample NotRunning = new(TeamsCallState.NotRunning, 0);
    public static readonly TeamsCallSample Unavailable = new(TeamsCallState.Unavailable, 0);

    public override string ToString() => State is TeamsCallState.Active or TeamsCallState.Absent ? $"{State}({Pid})" : State.ToString();
}

/// <summary>One read of the call reader: the sample, the meeting window rectangle and a status line.</summary>
public sealed record TeamsDetectionSnapshot(TeamsCallSample Sample, TeamsBounds? WindowFrame, string Status, UiaReadDiagnostics Diagnostics)
{
    public static TeamsDetectionSnapshot Of(TeamsCallSample sample, string status) => new(sample, null, status, new UiaReadDiagnostics());
}
