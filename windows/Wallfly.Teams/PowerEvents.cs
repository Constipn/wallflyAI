using Microsoft.Win32;

namespace Wallfly.Teams;

/// <summary>Sleep/wake notifications; faked in tests.</summary>
public interface IPowerEvents : IDisposable
{
    /// <summary>The computer is about to sleep (PBT_APMSUSPEND).</summary>
    event Action? Suspending;

    /// <summary>The computer woke up (PBT_APMRESUMEAUTOMATIC/SUSPEND).</summary>
    event Action? Resumed;
}

/// <summary>
/// <see cref="SystemEvents.PowerModeChanged"/> (WM_POWERBROADCAST). SystemEvents raises on its own thread
/// unless the subscribing thread pumps messages; callers marshal to their UI thread. On Modern Standby
/// systems a lid close may not send PBT_APMSUSPEND; consumers must also tolerate a missing Suspending.
/// </summary>
public sealed class PowerEvents : IPowerEvents
{
    public PowerEvents() => SystemEvents.PowerModeChanged += OnChanged;

    public event Action? Suspending;
    public event Action? Resumed;

    private void OnChanged(object? sender, PowerModeChangedEventArgs args)
    {
        if (args.Mode == PowerModes.Suspend) Suspending?.Invoke();
        else if (args.Mode == PowerModes.Resume) Resumed?.Invoke();
    }

    public void Dispose() => SystemEvents.PowerModeChanged -= OnChanged;
}
