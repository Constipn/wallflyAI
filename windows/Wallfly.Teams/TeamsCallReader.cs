using System.Collections.Concurrent;

namespace Wallfly.Teams;

/// <summary>
/// Reader state that must survive an abandoned UIA thread (thread-safe): main windows (sticky per HWND),
/// windows whose read stalled the thread (skipped for a while), first UIA contact per window (Chromium builds
/// its tree lazily, so the first seconds after contact are "initializing" and cannot prove absence), and the
/// window being read right now (so a watchdog abandonment can be attributed to it).
/// </summary>
public sealed class TeamsReaderState
{
    private readonly ConcurrentDictionary<nint, byte> mainWindows = new();
    private readonly ConcurrentDictionary<nint, double> stalledUntil = new();
    private readonly ConcurrentDictionary<nint, (double First, double Last)> contacts = new();
    private long currentWindow;

    /// <summary>A window not read for this long is treated as a new contact again (Chromium may drop its tree).</summary>
    public double RecontactSeconds { get; init; } = 30;

    public nint CurrentWindow
    {
        get => (nint)Interlocked.Read(ref currentWindow);
        set => Interlocked.Exchange(ref currentWindow, value);
    }

    public bool IsMainWindow(nint window) => mainWindows.ContainsKey(window);

    public void MarkMainWindow(nint window) => mainWindows[window] = 0;

    public bool IsStalled(nint window, double now) => stalledUntil.TryGetValue(window, out var until) && now < until;

    public void MarkStalled(nint window, double until)
    {
        if (window != 0) stalledUntil[window] = until;
    }

    /// <summary>Records a read of <paramref name="window"/> and returns the time of first contact.</summary>
    public double Touch(nint window, double now)
    {
        var entry = contacts.AddOrUpdate(window, (now, now), (_, previous) => now - previous.Last > RecontactSeconds ? (now, now) : (previous.First, now));
        return entry.First;
    }

    /// <summary>Forgets windows that no longer exist (HWNDs are reused by Windows).</summary>
    public void Retain(IReadOnlyCollection<nint> windows)
    {
        var alive = windows.ToHashSet();
        foreach (var key in mainWindows.Keys) if (!alive.Contains(key)) mainWindows.TryRemove(key, out _);
        foreach (var key in stalledUntil.Keys) if (!alive.Contains(key)) stalledUntil.TryRemove(key, out _);
        foreach (var key in contacts.Keys) if (!alive.Contains(key)) contacts.TryRemove(key, out _);
    }

    public void Clear()
    {
        mainWindows.Clear();
        stalledUntil.Clear();
        contacts.Clear();
    }
}

/// <summary>
/// Port of the macOS TeamsCallReader for Windows. Structure only, never text or chat.
/// <list type="bullet">
/// <item><c>notRunning</c>: no Teams process (<c>ms-teams.exe</c>).</item>
/// <item><c>active</c>: a visible, enabled leave/hang-up button (selectors) in a Teams window (<c>TeamsWebView</c>)
/// that is not a recognised main window.</item>
/// <item><c>absent</c>: every candidate window was read completely, no window is in its initializing grace,
/// and at least one Teams web UI was readable (a web area, or a recognised main window).</item>
/// <item><c>unknown</c>: anything else (no windows, minimized or stalled windows, budget hit, read errors).</item>
/// </list>
/// Budgets: ≤ 4 Teams processes, ≤ 8 windows (rotating, the last meeting window first), per window
/// ≤ 2 200 nodes and depth 36, 0.22 s per sample (1 s with diagnostics). Not thread-safe: one instance per
/// UIA thread; shared knowledge lives in <see cref="TeamsReaderState"/>.
/// </summary>
public sealed class TeamsCallReader(ITeamsDesktop desktop, ITeamsWindowReader windowReader, TeamsSelectorSource selectors, TeamsReaderState state, Func<double>? clock = null)
{
    public const string StatusWaiting = "Ready · waiting for Microsoft Teams";
    public const string StatusNoWindow = "Teams window currently not readable";
    public const string StatusDetected = "Teams meeting detected";
    public const string StatusNoMeeting = "Ready · no Teams meeting running";
    public const string StatusWebNotAccessible = "Teams web interface currently not accessible";
    public const string StatusNotReadable = "Teams view currently not readable";

    private readonly Func<double> clock = clock ?? MonotonicClock.Now;
    private int cursor;
    private nint? preferredWindow;

    public TeamsReaderState State => state;

    public TeamsDetectionSnapshot Sample(bool captureDiagnostics = false)
    {
        var current = selectors.Current;
        var limits = current.Limits;
        var diagnostics = new UiaReadDiagnostics();
        var processes = desktop.FindProcesses(current.ProcessNames);
        if (processes.Count == 0)
        {
            preferredWindow = null;
            state.CurrentWindow = 0;
            return new TeamsDetectionSnapshot(TeamsCallSample.NotRunning, null, StatusWaiting, diagnostics);
        }
        var pid = processes[0].Pid;
        diagnostics.Pid = pid;
        var targets = processes.Take(Math.Max(1, limits.MaxProcesses)).ToList();
        var complete = processes.Count <= limits.MaxProcesses;
        var existing = desktop.FindWindows(targets.Select(process => process.Pid).ToList(), current.WindowClasses);
        // Knowledge about a window lives as long as the window, even while hidden (main window closed to the tray).
        state.Retain(existing.Select(window => window.Handle).ToList());
        var windows = existing.Where(window => window.Visible).ToList();
        diagnostics.Windows = windows.Count;
        foreach (var process in targets)
            diagnostics.Processes.Add(new UiaReadDiagnostics.Process { Pid = process.Pid, Name = process.Name, Windows = windows.Count(window => window.ProcessId == process.Pid) });
        if (windows.Count == 0)
            return new TeamsDetectionSnapshot(TeamsCallSample.Unknown, null, StatusNoWindow, diagnostics);

        var now = clock();
        var deadline = now + (captureDiagnostics ? limits.DiagnosticBudgetMilliseconds : limits.BudgetMilliseconds) / 1000.0;
        complete = complete && windows.Count <= limits.MaxWindows;
        var offset = cursor % windows.Count;
        var ordered = windows.Skip(offset).Concat(windows.Take(offset)).ToList();
        if (preferredWindow is { } preferred && ordered.FindIndex(window => window.Handle == preferred) is var index and > 0)
        {
            var window = ordered[index];
            ordered.RemoveAt(index);
            ordered.Insert(0, window);
        }
        foreach (var window in ordered.Take(limits.MaxWindows))
        {
            // Advance from the window actually read, even after the preferred window was moved to the front.
            cursor = (windows.FindIndex(candidate => candidate.Handle == window.Handle) + 1) % windows.Count;
            now = clock();
            if (state.IsStalled(window.Handle, now))
            {
                diagnostics.StalledWindows++;
                complete = false;
                continue;
            }
            if (window.Minimized && !current.ReadMinimizedWindows)
            {
                diagnostics.MinimizedWindows++;
                complete = false;
                continue;
            }
            if (current.RejectMainWindows && state.IsMainWindow(window.Handle))
            {
                diagnostics.MainWindowsRejected++;
                continue;
            }
            var firstContact = state.Touch(window.Handle, now);
            if (now - firstContact < limits.InitializingSeconds) diagnostics.Initializing = true;
            state.CurrentWindow = window.Handle;
            var read = windowReader.ReadWindow(window, current, deadline, captureDiagnostics);
            state.CurrentWindow = 0;
            diagnostics.Merge(read.Diagnostics);
            if (current.RejectMainWindows && read.MainWindowMarker)
            {
                // The main window (chat tabs) is never the meeting window; a hang-up button there belongs to the
                // call whose own window is read separately. Once recognised, the rest of its (large) tree is
                // irrelevant, so an incomplete read of it does not block absence; it proves the web UI is readable.
                state.MarkMainWindow(window.Handle);
                diagnostics.MainWindowsRejected++;
                continue;
            }
            if (read.LeaveControl)
            {
                preferredWindow = window.Handle;
                return new TeamsDetectionSnapshot(TeamsCallSample.Active(pid), window.Rect, StatusDetected, diagnostics);
            }
            complete = complete && read.Complete;
            if (clock() >= deadline)
            {
                // Reads that started before the deadline finished; further windows wait for the next sample.
                if (!ReferenceEquals(window, ordered.Take(limits.MaxWindows).Last()))
                {
                    complete = false;
                    diagnostics.HitLimit = true;
                }
                break;
            }
        }
        preferredWindow = null;
        var sample = diagnostics.AbsenceSample(pid, complete);
        var status = sample.State == TeamsCallState.Absent
            ? StatusNoMeeting
            : diagnostics.WebAreasRead == 0 && diagnostics.MainWindowsRejected == 0 ? StatusWebNotAccessible : StatusNotReadable;
        return new TeamsDetectionSnapshot(sample, null, status, diagnostics);
    }
}
