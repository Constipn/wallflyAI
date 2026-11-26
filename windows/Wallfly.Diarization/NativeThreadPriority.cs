using System.Diagnostics;
using System.Runtime.InteropServices;

namespace Wallfly.Diarization;

/// <summary>
/// Sets the priority of the native threads that a module started. ONNX Runtime (statically linked CRT) starts its pool
/// threads at a function inside onnxruntime.dll, so the Win32 start address identifies them exactly; threads of the app
/// or of other native code (e.g. the audio engine) are never touched.
/// </summary>
internal static class NativeThreadPriority
{
    private const uint ThreadQueryInformation = 0x0040;
    private const uint ThreadSetInformation = 0x0020;
    private const int ThreadQuerySetWin32StartAddress = 9;

    /// <summary>Win32 <c>THREAD_PRIORITY_IDLE</c>: runs only when nothing else wants the processor (below <see cref="ThreadPriority.Lowest"/>).</summary>
    public const int Idle = -15;

    /// <summary>The Win32 value of a managed priority.</summary>
    public static int Win32(ThreadPriority priority) => priority switch
    {
        ThreadPriority.Lowest => -2, ThreadPriority.BelowNormal => -1, ThreadPriority.AboveNormal => 1, ThreadPriority.Highest => 2, _ => 0
    };

    /// <returns>The number of threads whose priority was changed.</returns>
    public static int Lower(string moduleName, ThreadPriority priority) => Set(moduleName, Win32(priority));

    /// <summary>The native id of the calling thread (to address it later from another thread).</summary>
    public static uint CurrentThreadId() => GetCurrentThreadId();

    /// <summary>Sets one thread's Win32 priority (e.g. <see cref="Idle"/>); false when it is gone or cannot be changed.</summary>
    public static bool SetThread(uint threadId, int win32Priority)
    {
        try
        {
            var handle = OpenThread(ThreadSetInformation | ThreadQueryInformation, false, threadId);
            if (handle == IntPtr.Zero) return false;
            try { return SetThreadPriority(handle, win32Priority); }
            finally { CloseHandle(handle); }
        }
        catch (Exception error) when (error is DllNotFoundException or EntryPointNotFoundException)
        {
            return false;
        }
    }

    /// <summary>Sets the Win32 priority of every thread <paramref name="moduleName"/> started. Returns the threads changed.</summary>
    public static int Set(string moduleName, int win32Priority)
    {
        try
        {
            using var process = Process.GetCurrentProcess();
            var module = process.Modules.Cast<ProcessModule>()
                .FirstOrDefault(candidate => string.Equals(candidate.ModuleName, moduleName, StringComparison.OrdinalIgnoreCase));
            if (module is null) return 0;
            var start = module.BaseAddress.ToInt64();
            var end = start + module.ModuleMemorySize;
            var changed = 0;
            foreach (ProcessThread thread in process.Threads)
            {
                using (thread)
                {
                    var handle = OpenThread(ThreadQueryInformation | ThreadSetInformation, false, (uint)thread.Id);
                    if (handle == IntPtr.Zero) continue;
                    try
                    {
                        if (NtQueryInformationThread(handle, ThreadQuerySetWin32StartAddress, out var address, IntPtr.Size, IntPtr.Zero) != 0) continue;
                        var value = address.ToInt64();
                        if (value < start || value >= end || GetThreadPriority(handle) == win32Priority) continue;
                        if (SetThreadPriority(handle, win32Priority)) changed++;
                    }
                    finally { CloseHandle(handle); }
                }
            }
            return changed;
        }
        catch (Exception error) when (error is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or DllNotFoundException or EntryPointNotFoundException)
        {
            return 0;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenThread(uint desiredAccess, bool inheritHandle, uint threadId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    [DllImport("kernel32.dll")]
    private static extern int GetThreadPriority(IntPtr thread);

    [DllImport("ntdll.dll")]
    private static extern int NtQueryInformationThread(IntPtr thread, int informationClass, out IntPtr information, int length, IntPtr returnLength);
}
