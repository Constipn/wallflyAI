using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wallfly.Teams;

/// <summary>A running process relevant to Teams detection.</summary>
public sealed record TeamsProcessInfo(int Pid, string Name, int ParentPid, DateTime? StartTime);

/// <summary>A top-level window (EnumWindows). <see cref="Rect"/> is in physical pixels.</summary>
public sealed record TeamsWindowInfo(nint Handle, int ProcessId, string ProcessName, string ClassName, bool Visible, bool Minimized, TeamsBounds Rect);

/// <summary>Process and window enumeration; faked in tests.</summary>
public interface ITeamsDesktop
{
    /// <summary>Processes with one of the executable names, the oldest first (the oldest is the Teams "main" PID).</summary>
    IReadOnlyList<TeamsProcessInfo> FindProcesses(IReadOnlyCollection<string> executableNames);

    /// <summary>Top-level windows (visible or hidden) of the given processes whose class is one of <paramref name="classes"/>.
    /// Callers read only visible ones; hidden ones keep their per-window knowledge (e.g. the main window closed to the tray).</summary>
    IReadOnlyList<TeamsWindowInfo> FindWindows(IReadOnlyCollection<int> processIds, IReadOnlyCollection<string> classes);
}

/// <summary>Win32 implementation of <see cref="ITeamsDesktop"/> (Toolhelp snapshot + EnumWindows).</summary>
public sealed class Win32TeamsDesktop : ITeamsDesktop
{
    public IReadOnlyList<TeamsProcessInfo> FindProcesses(IReadOnlyCollection<string> executableNames)
    {
        var names = new HashSet<string>(executableNames, StringComparer.OrdinalIgnoreCase);
        return ProcessTree.Snapshot().Values
            .Where(process => names.Contains(process.Name))
            .Select(process => process with { StartTime = StartTimeOf(process.Pid) })
            .OrderBy(process => process.StartTime ?? DateTime.MaxValue).ThenBy(process => process.Pid)
            .ToList();
    }

    public IReadOnlyList<TeamsWindowInfo> FindWindows(IReadOnlyCollection<int> processIds, IReadOnlyCollection<string> classes)
    {
        var pids = processIds.ToHashSet();
        var classSet = new HashSet<string>(classes, StringComparer.OrdinalIgnoreCase);
        return TopLevelWindows.Enumerate(includeHidden: true, pids.Contains)
            .Where(window => classSet.Count == 0 || classSet.Contains(window.ClassName))
            .ToList();
    }

    private static DateTime? StartTimeOf(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return process.StartTime;
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { return null; }
    }
}

/// <summary>All processes with parent PIDs (CreateToolhelp32Snapshot).</summary>
public static class ProcessTree
{
    public static IReadOnlyDictionary<int, TeamsProcessInfo> Snapshot()
    {
        var result = new Dictionary<int, TeamsProcessInfo>();
        var snapshot = NativeMethods.CreateToolhelp32Snapshot(NativeMethods.TH32CS_SNAPPROCESS, 0);
        if (snapshot == -1 || snapshot == 0) return result;
        try
        {
            var entry = new NativeMethods.ProcessEntry32 { Size = (uint)Marshal.SizeOf<NativeMethods.ProcessEntry32>() };
            if (!NativeMethods.Process32First(snapshot, ref entry)) return result;
            do
            {
                var pid = (int)entry.ProcessId;
                result[pid] = new TeamsProcessInfo(pid, entry.ExeFileName, (int)entry.ParentProcessId, null);
            }
            while (NativeMethods.Process32Next(snapshot, ref entry));
        }
        finally { NativeMethods.CloseHandle(snapshot); }
        return result;
    }

    /// <summary>Whether <paramref name="pid"/> or one of its ancestors runs one of the executables.</summary>
    public static bool DescendsFrom(IReadOnlyDictionary<int, TeamsProcessInfo> tree, int pid, IReadOnlyCollection<string> executableNames)
    {
        var names = new HashSet<string>(executableNames, StringComparer.OrdinalIgnoreCase);
        var seen = new HashSet<int>();
        for (var current = pid; current != 0 && seen.Add(current) && tree.TryGetValue(current, out var process); current = process.ParentPid)
            if (names.Contains(process.Name)) return true;
        return false;
    }
}

/// <summary>EnumWindows wrapper with class, visibility, minimized state and rectangle.</summary>
public static class TopLevelWindows
{
    public static IReadOnlyList<TeamsWindowInfo> Enumerate(bool includeHidden, Func<int, bool>? processFilter = null)
    {
        var handles = new List<nint>();
        var gc = GCHandle.Alloc(handles);
        try
        {
            unsafe { NativeMethods.EnumWindows(&Collect, GCHandle.ToIntPtr(gc)); }
        }
        finally { gc.Free(); }
        IReadOnlyDictionary<int, TeamsProcessInfo>? names = null;
        var windows = new List<TeamsWindowInfo>();
        foreach (var handle in handles)
        {
            var visible = NativeMethods.IsWindowVisible(handle);
            if (!visible && !includeHidden) continue;
            NativeMethods.GetWindowThreadProcessId(handle, out var pid);
            if (processFilter is not null && !processFilter((int)pid)) continue;
            NativeMethods.GetWindowRect(handle, out var rect);
            names ??= ProcessTree.Snapshot();
            windows.Add(new TeamsWindowInfo(handle, (int)pid, names.TryGetValue((int)pid, out var process) ? process.Name : "",
                ClassName(handle), visible, NativeMethods.IsIconic(handle),
                new TeamsBounds(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top)));
        }
        return windows;
    }

    public static string ClassName(nint handle)
    {
        Span<char> buffer = stackalloc char[256];
        unsafe
        {
            fixed (char* pointer = buffer)
            {
                var length = NativeMethods.GetClassNameW(handle, pointer, buffer.Length);
                return length > 0 ? new string(buffer[..length]) : "";
            }
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvStdcall)])]
    private static int Collect(nint handle, nint parameter)
    {
        if (GCHandle.FromIntPtr(parameter).Target is List<nint> list) list.Add(handle);
        return 1;
    }
}

/// <summary>Monitor work areas for popup placement.</summary>
public static class Monitors
{
    /// <summary>Work area (physical pixels) and DPI of the monitor that contains most of <paramref name="rect"/>;
    /// the primary monitor when <paramref name="rect"/> is null.</summary>
    public static (TeamsBounds WorkArea, uint Dpi) WorkAreaFor(TeamsBounds? rect)
    {
        nint monitor;
        if (rect is { } value && !value.IsEmpty)
        {
            var native = new NativeMethods.Rect { Left = (int)value.X, Top = (int)value.Y, Right = (int)value.Right, Bottom = (int)value.Bottom };
            monitor = NativeMethods.MonitorFromRect(in native, NativeMethods.MONITOR_DEFAULTTONEAREST);
        }
        else monitor = NativeMethods.MonitorFromPoint(new NativeMethods.Point(), NativeMethods.MONITOR_DEFAULTTOPRIMARY);
        var info = new NativeMethods.MonitorInfo { Size = (uint)Marshal.SizeOf<NativeMethods.MonitorInfo>() };
        if (!NativeMethods.GetMonitorInfoW(monitor, ref info)) return (new TeamsBounds(0, 0, 1280, 800), 96);
        if (NativeMethods.GetDpiForMonitor(monitor, 0, out var dpiX, out _) != 0) dpiX = 96;
        var work = info.Work;
        return (new TeamsBounds(work.Left, work.Top, work.Right - work.Left, work.Bottom - work.Top), dpiX);
    }
}

internal static unsafe partial class NativeMethods
{
    internal const uint TH32CS_SNAPPROCESS = 0x2;
    internal const uint MONITOR_DEFAULTTOPRIMARY = 1;
    internal const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Point
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ProcessEntry32
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;
        public fixed ushort ExeFile[260];

        public readonly string ExeFileName
        {
            get
            {
                fixed (ushort* pointer = ExeFile) return new string((char*)pointer);
            }
        }
    }

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool EnumWindows(delegate* unmanaged[Stdcall]<nint, nint, int> callback, nint parameter);

    [LibraryImport("user32.dll")]
    internal static partial uint GetWindowThreadProcessId(nint window, out uint processId);

    [LibraryImport("user32.dll")]
    internal static partial int GetClassNameW(nint window, char* buffer, int maxCount);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsWindowVisible(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool IsIconic(nint window);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetWindowRect(nint window, out Rect rect);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromRect(in Rect rect, uint flags);

    [LibraryImport("user32.dll")]
    internal static partial nint MonitorFromPoint(Point point, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetMonitorInfoW(nint monitor, ref MonitorInfo info);

    [LibraryImport("shcore.dll")]
    internal static partial int GetDpiForMonitor(nint monitor, int dpiType, out uint dpiX, out uint dpiY);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32First(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll", EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool Process32Next(nint snapshot, ref ProcessEntry32 entry);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint handle);
}
