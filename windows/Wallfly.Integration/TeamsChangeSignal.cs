using System.Diagnostics;
using System.Runtime.InteropServices;
using Wallfly.Teams;

namespace Wallfly.Integration;

/// <summary>Accessibility change notifications of the Teams UI (the Windows counterpart of the macOS AX observer).</summary>
public interface ITeamsChangeSignal : IDisposable
{
    /// <summary>Raised on a background thread when an accessible object of a watched process changed.</summary>
    event Action? Changed;

    /// <summary>The Teams processes to listen to (the speaker reader passes those of its last read).</summary>
    void Watch(IReadOnlyCollection<int> processIds);
}

/// <summary>
/// Out-of-context WinEvent hooks (<c>SetWinEventHook</c>, <c>WINEVENT_OUTOFCONTEXT</c>: nothing is injected into Teams) on
/// a dedicated message-loop thread. Chromium raises them for its accessibility tree once a UIA client is connected:
/// show/hide/reorder (tiles and the speaking frame appear and disappear), name and state changes. Only events of the Teams
/// process tree (the watched processes and their descendants, e.g. <c>msedgewebview2.exe</c>) count; the watcher rate
/// limits the resulting reads to one per 150 ms. Best effort: when a hook cannot be installed, the 500 ms polling remains.
/// </summary>
public sealed class WinEventTeamsChangeSignal : ITeamsChangeSignal
{
    private const uint EventObjectShow = 0x8002;
    private const uint EventObjectReorder = 0x8004;
    private const uint EventObjectStateChange = 0x800A;
    private const uint EventObjectNameChange = 0x800C;
    private const uint OutOfContext = 0x0000;
    private const uint SkipOwnProcess = 0x0002;
    private const uint WmQuit = 0x0012;
    private static readonly TimeSpan TreeRefresh = TimeSpan.FromSeconds(5);

    private delegate void WinEventProc(nint hook, uint eventType, nint window, int objectId, int childId, uint thread, uint time);

    private readonly WinEventProc callback;
    private readonly object gate = new();
    private readonly ManualResetEventSlim ready = new(false);
    private Thread? thread;
    private uint threadId;
    private volatile HashSet<int> processes = [];
    private IReadOnlyCollection<int> roots = [];
    private long treeRefreshed;
    private int disposed;

    public WinEventTeamsChangeSignal() => callback = OnWinEvent;

    public event Action? Changed;

    public void Watch(IReadOnlyCollection<int> processIds)
    {
        if (Volatile.Read(ref disposed) != 0) return;
        lock (gate)
        {
            var same = roots.Count == processIds.Count && roots.All(processIds.Contains);
            if (same && Stopwatch.GetElapsedTime(treeRefreshed) < TreeRefresh) return;
            roots = processIds.ToList();
            treeRefreshed = Stopwatch.GetTimestamp();
        }
        processes = processIds.Count == 0 ? [] : Descendants(processIds);
        if (processIds.Count > 0) EnsureStarted();
    }

    private static HashSet<int> Descendants(IReadOnlyCollection<int> rootIds)
    {
        var result = rootIds.ToHashSet();
        IReadOnlyDictionary<int, TeamsProcessInfo> tree;
        try { tree = ProcessTree.Snapshot(); }
        catch (Exception) { return result; }
        bool added;
        do
        {
            added = false;
            foreach (var process in tree.Values)
                if (!result.Contains(process.Pid) && result.Contains(process.ParentPid) && process.Pid != process.ParentPid) added |= result.Add(process.Pid);
        }
        while (added);
        return result;
    }

    private void EnsureStarted()
    {
        lock (gate)
        {
            if (thread != null || Volatile.Read(ref disposed) != 0) return;
            thread = new Thread(Run) { IsBackground = true, Name = "Wallfly Teams WinEvents" };
            thread.Start();
        }
        ready.Wait(TimeSpan.FromSeconds(2));
    }

    private void Run()
    {
        threadId = GetCurrentThreadId();
        var hooks = new List<nint>();
        try
        {
            foreach (var (first, last) in new[] { (EventObjectShow, EventObjectReorder), (EventObjectStateChange, EventObjectStateChange), (EventObjectNameChange, EventObjectNameChange) })
            {
                var hook = SetWinEventHook(first, last, 0, callback, 0, 0, OutOfContext | SkipOwnProcess);
                if (hook != 0) hooks.Add(hook);
            }
            ready.Set();
            if (hooks.Count == 0) return;
            while (GetMessage(out var message, 0, 0, 0) > 0)
            {
                TranslateMessage(ref message);
                DispatchMessage(ref message);
            }
        }
        catch (Exception error) { Trace.TraceWarning("Wallfly: the Teams change signal stopped: " + error.Message); }
        finally
        {
            ready.Set();
            foreach (var hook in hooks) UnhookWinEvent(hook);
        }
    }

    private void OnWinEvent(nint hook, uint eventType, nint window, int objectId, int childId, uint eventThread, uint time)
    {
        if (window == 0) return;
        try
        {
            GetWindowThreadProcessId(window, out var pid);
            if (processes.Contains((int)pid)) Changed?.Invoke();
        }
        catch (Exception error) { Trace.TraceWarning("Wallfly: a Teams change notification failed: " + error.Message); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0) return;
        Thread? running;
        lock (gate) running = thread;
        if (running != null)
        {
            if (threadId != 0) PostThreadMessage(threadId, WmQuit, 0, 0);
            running.Join(TimeSpan.FromSeconds(2));
        }
        ready.Dispose();
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        public nint Window;
        public uint Id;
        public nint WParam;
        public nint LParam;
        public uint Time;
        public int X;
        public int Y;
        public uint Private;
    }

    [DllImport("user32.dll")]
    private static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UnhookWinEvent(nint hook);

    [DllImport("user32.dll")]
    private static extern int GetMessage(out Message message, nint window, uint filterMin, uint filterMax);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TranslateMessage(ref Message message);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref Message message);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool PostThreadMessage(uint threadId, uint message, nint wParam, nint lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint window, out uint processId);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();
}
