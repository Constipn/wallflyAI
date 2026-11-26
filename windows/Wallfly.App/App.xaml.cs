using System.IO;
using System.Windows;
using Wallfly.App.Shell;
using Wallfly.Core.Credentials;
using Wallfly.Core.Diagnostics;
using Wallfly.Core.Migration;
using Wallfly.Core.Persistence;
using Wallfly.Teams;

namespace Wallfly.App;

public partial class App : Application
{
    private Mutex? singleInstance;
    private AppShell? shell;
    private RollingFileTraceListener? log;

    protected override void OnStartup(StartupEventArgs args)
    {
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        base.OnStartup(args);
        // Integrated title bar with Mica on Windows 11 22H2+: the chrome brushes turn transparent before any window opens.
        WindowFrame.Initialize(Resources);
        var smoke = args.Args.Contains("--smoke-test");
        // "Take notes" from Wallfly Detector: a running Wallfly gets the ID over the pipe and this process ends silently,
        // before the single-instance check (which would otherwise show "already running").
        var detected = smoke ? null : DetectedTeamsActivation.FromArguments(args.Args);
        if (detected is Guid forward && Task.Run(() => ActivationForwarding.ForwardAsync(forward, ActivationForwarding.SendAsync, () => WallflyPaths.IsAppRunning())).GetAwaiter().GetResult())
        {
            Shutdown();
            return;
        }
        string? root = null;
        var pathIndex = Array.IndexOf(args.Args, "--data-dir");
        if (pathIndex >= 0 && pathIndex + 1 < args.Args.Length) root = Path.GetFullPath(args.Args[pathIndex + 1]);
        var pointerManaged = root == null;
        if (smoke && root == null) root = Path.Combine(Path.GetTempPath(), "Wallfly-UI-" + Guid.NewGuid());
        if (!smoke && root == null)
        {
            // One-time copy of the Meeting Notes data folder, before anything else writes into Wallfly.
            var pointer = new LibraryPointer();
            try { LegacyDataMigration.Run(pointer.Directory); } catch (Exception) { /* both folders stay usable */ }
            root = pointer.Resolve();
            try { new CredentialStore().MigrateLegacyDpapi(LegacyDataMigration.DefaultLegacyRoot); } catch (Exception) { }
        }
        // The helper checks the same name (WallflyPaths.AppMutexName) before it trusts the heartbeat.
        singleInstance = new Mutex(true, smoke ? WallflyPaths.AppMutexName + ".Smoke" : WallflyPaths.AppMutexName, out var created);
        if (!created)
        {
            if (detected == null) MessageBox.Show(Strings.AlreadyRunning, Strings.AppName);
            Shutdown();
            return;
        }
        // The app log (%LOCALAPPDATA%\Wallfly\logs, or the logs folder of --data-dir / the smoke run): every Trace call from here on.
        log = RollingFileTraceListener.Register(Path.Combine(pointerManaged && !smoke ? new LibraryPointer().Directory : root!, RollingFileTraceListener.FolderName));
        WallflyLog.Info($"Wallfly started version={typeof(App).Assembly.GetName().Version} pid={Environment.ProcessId} smoke={smoke} os={Environment.OSVersion.Version}");
        DispatcherUnhandledException += (_, eventArgs) =>
        {
            WallflyLog.Error("Unhandled UI exception: " + eventArgs.Exception);
            eventArgs.Handled = true;
            if (smoke)
            {
                Directory.CreateDirectory(root!);
                File.AppendAllText(Path.Combine(root!, "ui-dispatch-error.txt"), eventArgs.Exception.GetType().FullName + ": " + eventArgs.Exception.Message + "\n" + eventArgs.Exception.StackTrace + "\n");
                Environment.Exit(1);
            }
            MessageBox.Show(Strings.UnexpectedError, Strings.AppName, MessageBoxButton.OK, MessageBoxImage.Error);
        };
        if (smoke) RecordBindingErrors(Path.Combine(root!, "smoke", "binding-errors.txt"));
        shell = new AppShell(root!, smoke, pointerManaged);
        MainWindow = shell.Window;
        shell.Start(detected);
    }

    /// <summary>Smoke test only: every failed data binding is written to a file, so a broken view shows up in the run.</summary>
    private static void RecordBindingErrors(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "");
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(new FileTraceListener(path));
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level = System.Diagnostics.SourceLevels.Error;
    }

    private sealed class FileTraceListener(string path) : System.Diagnostics.TraceListener
    {
        public override void Write(string? message) => File.AppendAllText(path, message);
        public override void WriteLine(string? message) => File.AppendAllText(path, message + Environment.NewLine);
    }

    protected override void OnExit(ExitEventArgs args)
    {
        shell?.Dispose();
        singleInstance?.Dispose();
        WallflyLog.Info($"Wallfly exited code={args.ApplicationExitCode}");
        log?.Flush();
        base.OnExit(args);
    }
}
