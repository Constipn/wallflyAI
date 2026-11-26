using System.Windows;
using Wallfly.Teams;

namespace Wallfly.Detector;

/// <summary>
/// Wallfly Detector: no main window, no taskbar entry, one instance per session (<c>Local\WallflyDetector</c>).
/// Exits when detection is disabled in <c>configuration.json</c>. Arguments: <c>--autostart</c> (from the Run
/// key, informational), <c>--detection-dir &lt;path&gt;</c> and <c>--selectors &lt;file&gt;</c> (manual tests against a
/// scratch folder and a selector file instead of %LOCALAPPDATA%\Wallfly).
/// </summary>
public partial class App : Application
{
    private Mutex? instance;
    private DetectorController? controller;

    protected override void OnStartup(StartupEventArgs args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(args);
        instance = new Mutex(initiallyOwned: true, WallflyPaths.DetectorMutexName, out var created);
        if (!created)
        {
            instance.Dispose();
            instance = null;
            Shutdown();
            return;
        }
        var root = Argument(args.Args, "--detection-dir");
        var selectorsPath = Argument(args.Args, "--selectors");
        var storage = new DetectionStorage(root);
        if (!storage.ReadConfiguration().Enabled)
        {
            Shutdown();
            return;
        }
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            eventArgs.Handled = true;
            DetectorLog.Write(storage, "unhandled: " + eventArgs.Exception);
        };
        controller = new DetectorController(storage, Dispatcher, () => Shutdown(), selectorsPath);
        controller.Start();
    }

    private static string? Argument(string[] args, string name)
    {
        var index = Array.FindIndex(args, argument => argument.Equals(name, StringComparison.OrdinalIgnoreCase));
        return index >= 0 && index + 1 < args.Length ? System.IO.Path.GetFullPath(args[index + 1]) : null;
    }

    protected override void OnExit(ExitEventArgs args)
    {
        controller?.Dispose();
        if (instance is not null)
        {
            try { instance.ReleaseMutex(); } catch (ApplicationException) { }
            instance.Dispose();
        }
        base.OnExit(args);
    }
}
