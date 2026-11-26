using System.IO;
using System.Reflection;
using System.Windows.Threading;
using Wallfly.Teams;

namespace Wallfly.Detector;

/// <summary>
/// The helper's 2-s loop (port of DetectorDelegate, macOS 1.13.0): reads configuration.json (disabled → exit),
/// samples Teams on the watchdog-protected UIA thread (optionally only while Teams holds the microphone),
/// writes status.json and session.json, feeds <see cref="DetectionPolicy"/> and drives the popup through
/// <see cref="DetectionPopupPolicy"/>. A click writes request.json and hands the ID to Wallfly. Sleep pauses
/// everything and requires fresh evidence afterwards.
/// </summary>
internal sealed class DetectorController : IDisposable
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan ActivationTimeout = TimeSpan.FromSeconds(3);

    private readonly DetectionStorage storage;
    private readonly Dispatcher dispatcher;
    private readonly Action shutdown;
    private readonly TeamsSelectorSource selectors;
    private readonly TeamsCallMonitor monitor;
    private readonly DetectionPopupPolicy popup = new();
    private readonly DispatcherTimer timer;
    private readonly string helperVersion;
    private IPowerEvents? power;
    private DetectionPolicy policy = new();
    private MeetingDetectedWindow? window;
    private TeamsBounds? lastFrame;
    private DetectionDiagnosticRequest? pendingDiagnostic;
    private bool sampling;
    private bool sleeping;
    private bool launching;
    private bool disposed;

    public DetectorController(DetectionStorage storage, Dispatcher dispatcher, Action shutdown, string? selectorsPath = null)
    {
        this.storage = storage;
        this.dispatcher = dispatcher;
        this.shutdown = shutdown;
        selectors = selectorsPath is null ? TeamsSelectorSource.CreateDefault() : new TeamsSelectorSource(selectorsPath);
        monitor = new TeamsCallMonitor(selectors);
        timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = Interval };
        timer.Tick += (_, _) => Tick();
        helperVersion = typeof(DetectorController).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "development";
    }

    private static double Now => MonotonicClock.Now();
    private static double WallSeconds => DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;

    public void Start()
    {
        policy = storage.Read<DetectionPolicy>(DetectionStorage.SessionFile) ?? new DetectionPolicy();
        policy.Resume();
        // Continue a recently requested, unsuccessful diagnostic across an update; ordinary launches collect no trace.
        var report = storage.Read<DetectionDiagnosticReport>(DetectionStorage.DiagnosticReportFile);
        if (report is not null && report.ShouldRefresh(helperVersion, DateTimeOffset.UtcNow) && !storage.Exists(DetectionStorage.DiagnosticRequestFile))
            TryWrite(new DetectionDiagnosticRequest(), DetectionStorage.DiagnosticRequestFile);
        try
        {
            power = new PowerEvents();
            power.Suspending += () => dispatcher.BeginInvoke(OnSuspend);
            power.Resumed += () => dispatcher.BeginInvoke(OnResume);
        }
        catch (Exception error) when (error is InvalidOperationException or System.Runtime.InteropServices.ExternalException)
        {
            DetectorLog.Write(storage, "power events unavailable: " + error.Message);
        }
        selectors.Changed += (_, _) => DetectorLog.Write(storage, "teams-selectors.json reloaded");
        timer.Start();
        Tick();
    }

    private void OnSuspend()
    {
        sleeping = true;
        policy.Resume();
        Apply(popup.Reset());
    }

    private void OnResume()
    {
        sleeping = false;
        policy.Resume();
        Tick();
    }

    private async void Tick()
    {
        if (sleeping || disposed) return;
        try
        {
            var configuration = storage.ReadConfiguration();
            if (!configuration.Enabled)
            {
                Apply(popup.Reset());
                timer.Stop();
                shutdown();
                return;
            }
            if (MainAppSuppressesPopup()) Apply(popup.Reset());
            if (sampling) return;
            sampling = true;
            try
            {
                if (storage.TakeDiagnosticRequest(DateTimeOffset.UtcNow) is { } request) pendingDiagnostic = request;
                if (pendingDiagnostic is not null && !pendingDiagnostic.IsFresh(DateTimeOffset.UtcNow)) pendingDiagnostic = null;
                var diagnostic = pendingDiagnostic;
                var snapshot = await monitor.SampleAsync(diagnostic is not null, configuration.MicPrefilter, policy.EventId is not null);
                Receive(snapshot, diagnostic);
            }
            finally { sampling = false; }
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            DetectorLog.Write(storage, "tick failed: " + error.Message);
        }
    }

    private void Receive(TeamsDetectionSnapshot snapshot, DetectionDiagnosticRequest? diagnostic)
    {
        if (sleeping || disposed) return;
        if (!storage.ReadConfiguration().Enabled)
        {
            Apply(popup.Reset());
            return;
        }
        var message = snapshot.Status;
        if (diagnostic is not null && !snapshot.Diagnostics.Initializing)
        {
            pendingDiagnostic = null;
            if (!TryWrite(DetectionDiagnosticReport.Create(diagnostic, snapshot, helperVersion, DateTimeOffset.UtcNow), DetectionStorage.DiagnosticReportFile))
                message += " · Diagnostics could not be saved";
        }
        TryWrite(new DetectionStatus
        {
            Date = DateTimeOffset.UtcNow,
            Message = message,
            HelperVersion = helperVersion,
            State = StateName(snapshot.Sample.State),
            Pid = snapshot.Sample.Pid == 0 ? null : snapshot.Sample.Pid,
        }, DetectionStorage.StatusFile);
        var previous = policy.Clone();
        var detected = policy.Observe(snapshot.Sample, WallSeconds);
        if (!policy.Equals(previous) && !TryWrite(policy, DetectionStorage.SessionFile))
        {
            // Without persisted state a restart would offer the same call again: no popup instead.
            Apply(popup.Reset());
            return;
        }
        if (snapshot.Sample.State == TeamsCallState.Active && snapshot.WindowFrame is { } frame) lastFrame = frame;
        if (detected is { } id) popup.EventDetected(id, Now);
        Apply(popup.Update(Now, policy.EventId, snapshot.Sample.State, MainAppSuppressesPopup()));
    }

    private bool MainAppSuppressesPopup() =>
        WallflyPaths.IsAppRunning() && storage.Read<DetectionHeartbeat>(DetectionStorage.HeartbeatFile)?.SuppressesPopup(DateTimeOffset.UtcNow) == true;

    private void Apply(PopupCommand command)
    {
        switch (command)
        {
            case PopupCommand.Show:
                if (window is null)
                {
                    window = new MeetingDetectedWindow();
                    window.TakeNotesRequested += OnTakeNotes;
                    window.DismissRequested += () => Apply(popup.Dismiss());
                }
                if (!window.IsVisible) window.SetState(launching, error: null);
                window.ShowNear(lastFrame);
                break;
            case PopupCommand.HideTemporarily:
                window?.Hide();
                break;
            case PopupCommand.Hide:
                window?.Hide();
                window?.SetState(launching: false, error: null);
                break;
        }
    }

    private async void OnTakeNotes()
    {
        if (launching || popup.DisplayedEvent is not { } id || window is null) return;
        if (!TryWrite(new DetectionRequest(id, DateTimeOffset.UtcNow), DetectionStorage.RequestFile))
        {
            window.SetState(false, "Could not prepare launch. Try again.");
            return;
        }
        launching = true;
        window.SetState(true, null);
        ActivationDelivery delivery;
        try { delivery = await DetectedTeamsActivation.ActivateAsync(id, AppContext.BaseDirectory, ActivationTimeout); }
        catch (Exception error) when (error is IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            DetectorLog.Write(storage, "activation failed: " + error.Message);
            delivery = ActivationDelivery.Failed;
        }
        launching = false;
        DetectorLog.Write(storage, "take notes → " + delivery);
        switch (delivery)
        {
            case ActivationDelivery.Accepted:
            case ActivationDelivery.Launched:
                Apply(popup.Launched());
                break;
            case ActivationDelivery.NoServer:
                window.SetState(false, "Wallfly was not found.");
                break;
            case ActivationDelivery.Rejected:
                window.SetState(false, "Wallfly is busy. Try again.");
                break;
            default:
                window.SetState(false, "Could not open. Try again.");
                break;
        }
    }

    private bool TryWrite<T>(T value, string name)
    {
        try
        {
            storage.Write(value, name);
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        {
            DetectorLog.Write(storage, $"writing {name} failed: {error.Message}");
            return false;
        }
    }

    private static string StateName(TeamsCallState state) => state switch
    {
        TeamsCallState.Active => "active",
        TeamsCallState.Absent => "absent",
        TeamsCallState.NotRunning => "notRunning",
        TeamsCallState.Unavailable => "unavailable",
        _ => "unknown",
    };

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        timer.Stop();
        power?.Dispose();
        monitor.Dispose();
        selectors.Dispose();
        window?.Close();
        try { storage.Remove(DetectionStorage.StatusFile); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

/// <summary>Small rolling log next to the detection files (no Teams content, no names).</summary>
internal static class DetectorLog
{
    private const long MaxBytes = 256 * 1024;

    public static void Write(DetectionStorage storage, string message)
    {
        try
        {
            Directory.CreateDirectory(storage.Root);
            var path = storage.PathOf("detector.log");
            if (File.Exists(path) && new FileInfo(path).Length > MaxBytes) File.Move(path, path + ".1", overwrite: true);
            File.AppendAllText(path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff} {message}{Environment.NewLine}");
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
    }
}
