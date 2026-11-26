using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;

namespace Wallfly.App.Shell;

/// <summary>
/// Sign-out, restart and shutdown (spec 9.6/10): on <c>WM_QUERYENDSESSION</c> the window registers
/// <c>ShutdownBlockReasonCreate("Saving your meeting…")</c>, saves (a running recording ends within the service's
/// three-second quit budget), removes the reason and lets Windows continue. <c>WM_ENDSESSION</c> then ends the app, or,
/// when another app called the session end off, reports that (the service accepts work again after its close).
/// </summary>
internal sealed class SessionEndGuard : IDisposable
{
    internal const int QueryEndSession = 0x0011;
    internal const int EndSession = 0x0016;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(4);
    private readonly Func<Task<bool>> save;
    private readonly Action ended;
    private readonly Action? cancelled;
    private readonly HwndSource source;
    private bool saved;

    /// <param name="cancelled">The session continues after a save (<c>WM_ENDSESSION</c> with FALSE).</param>
    public SessionEndGuard(Window window, Func<Task<bool>> save, Action ended, Action? cancelled = null)
    {
        this.save = save;
        this.ended = ended;
        this.cancelled = cancelled;
        source = HwndSource.FromHwnd(new WindowInteropHelper(window).EnsureHandle());
        source.AddHook(Hook);
    }

    /// <summary>The block reason was shown during the last save (for the smoke test).</summary>
    public bool ReasonWasRegistered { get; private set; }
    public bool LastSaveSucceeded { get; private set; }

    private IntPtr Hook(IntPtr window, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (message == QueryEndSession)
        {
            if (!saved) { LastSaveSucceeded = SaveBlocking(window); saved = true; }
            handled = true;
            return new IntPtr(1);
        }
        if (message == EndSession)
        {
            handled = true;
            if (wParam != IntPtr.Zero) ended();
            else if (saved)
            {
                // The session continues: a later query saves again, and a close the save started is called off.
                saved = false;
                cancelled?.Invoke();
            }
        }
        return IntPtr.Zero;
    }

    /// <summary>Runs the asynchronous save in a nested message loop, at most <see cref="Budget"/>.</summary>
    private bool SaveBlocking(IntPtr window)
    {
        ReasonWasRegistered = ShutdownBlockReasonCreate(window, Strings.ShutdownReason);
        try
        {
            var task = save();
            if (!task.IsCompleted)
            {
                var frame = new DispatcherFrame();
                var timer = new DispatcherTimer(Budget, DispatcherPriority.Send, (_, _) => frame.Continue = false, Dispatcher.CurrentDispatcher);
                task.ContinueWith(_ => frame.Continue = false, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());
                Dispatcher.PushFrame(frame);
                timer.Stop();
            }
            return task.IsCompletedSuccessfully && task.Result;
        }
        catch (Exception error) when (error is InvalidOperationException or AggregateException)
        {
            Trace.TraceError("Saving at session end failed: " + error.Message);
            return false;
        }
        finally { _ = ShutdownBlockReasonDestroy(window); }
    }

    public void Dispose() => source.RemoveHook(Hook);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonCreate(IntPtr window, string reason);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShutdownBlockReasonDestroy(IntPtr window);
}
