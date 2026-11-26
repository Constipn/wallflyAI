using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using Wallfly.App.ViewModels;
using Wallfly.Core.Services;
using Wallfly.Teams;

namespace Wallfly.App.Shell;

/// <summary>The Wallfly Detector process next to Wallfly.exe; faked in the tests and the smoke test, which never start it.</summary>
public interface IDetectorProcess
{
    string ExecutablePath { get; }
    bool IsInstalled { get; }
    /// <summary>The shipped helper's version: the product version of its file, which is what it writes as <c>helperVersion</c>.</summary>
    string? ShippedVersion { get; }
    /// <summary>A helper runs in this Windows session (its single-instance mutex exists).</summary>
    bool IsRunning { get; }
    bool Start();
    /// <summary>Ends every helper of this session; true when none is left after <paramref name="timeout"/>.</summary>
    bool Stop(TimeSpan timeout);
}

public enum DetectorAction { None, Start, Restart }

/// <summary>
/// The app's side of Teams detection (spec §6, port of macOS <c>MeetingDetectionService</c> plus the activation and sleep
/// parts of <c>MeetingStore</c>):
/// <list type="bullet">
/// <item>the activation pipe server: "Take notes" in the helper → UI thread → <see cref="DetectionStorage.Consume(Guid, DateTimeOffset)"/>
/// → <see cref="AppViewModel.OpenDetectedTeamsMeeting"/>;</item>
/// <item>heartbeat.json every 2 s and on every change of busy/presenting;</item>
/// <item>"Detect Teams meetings automatically": configuration.json, the HKCU Run key (registered once on first launch) and
/// the helper process (started when it is not running, restarted once when it runs another build);</item>
/// <item>the helper status for the settings and "Create Teams diagnostics";</item>
/// <item>sleep: a running recording ends with "Recording ended because the computer went to sleep".</item>
/// </list>
/// Everything runs on the UI thread except the pipe and power callbacks, which are marshalled through <c>onUiThread</c>.
/// </summary>
internal sealed class DetectorIntegration : ITeamsDetection, IDisposable
{
    public static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan DiagnosticTimeout = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan StopTimeout = TimeSpan.FromSeconds(5);
    /// <summary>Windows does not wait for apps before sleeping: the recording ends within the quit budget.</summary>
    public static readonly TimeSpan SleepStopBudget = TimeSpan.FromSeconds(3);

    private readonly AppViewModel app;
    private readonly MeetingLibraryService store;
    private readonly IDetectorProcess detector;
    private readonly IRunKeyStore runKey;
    private readonly IUiScheduler scheduler;
    private readonly Func<Func<bool>, Task<bool>> onUiThread;
    private readonly UiStateStore uiState;
    private readonly IPowerEvents? power;
    private readonly TimeProvider time;
    private ActivationPipeServer? server;
    private IDisposable? ticker;
    private (bool Busy, bool Presenting)? lastHeartbeat;
    private (bool Enabled, bool MicPrefilter)? applied;
    private (bool Enabled, bool MicPrefilter)? savedSettings;
    private DetectionDiagnosticRequest? pendingDiagnostic;
    private bool restartTried;
    private string? error;
    private bool started;
    private bool disposed;

    /// <param name="onUiThread">Runs a callback on the UI thread (the WPF dispatcher; tests run it inline).</param>
    /// <param name="power">Sleep notifications; owned (disposed) by this instance.</param>
    public DetectorIntegration(AppViewModel app, DetectionStorage storage, IDetectorProcess detector, IRunKeyStore runKey, IUiScheduler scheduler,
        Func<Func<bool>, Task<bool>> onUiThread, UiStateStore uiState, IPowerEvents? power = null, TimeProvider? time = null,
        string pipeName = WallflyPaths.ActivationPipeName)
    {
        this.app = app;
        store = app.Store;
        Storage = storage;
        this.detector = detector;
        this.runKey = runKey;
        this.scheduler = scheduler;
        this.onUiThread = onUiThread;
        this.uiState = uiState;
        this.power = power;
        this.time = time ?? TimeProvider.System;
        PipeName = pipeName;
    }

    public DetectionStorage Storage { get; }
    public IDetectorProcess Detector => detector;
    public string PipeName { get; }
    public DetectorStatusInfo Status { get; private set; } = DetectorStatusInfo.Unavailable;
    public string? DiagnosticStatus { get; private set; }
    /// <summary>The recording stop started by the last sleep notification (tests and the smoke test wait for it).</summary>
    public Task SleepStop { get; private set; } = Task.CompletedTask;
    public bool PipeListening => server != null;

    public event Action? StatusChanged;

    /// <summary>Syncs configuration.json and the helper with the saved settings, then starts heartbeat, pipe server and sleep watch.</summary>
    public void Start()
    {
        if (started || disposed) return;
        started = true;
        savedSettings = SavedSettings();
        Apply(savedSettings.Value.Enabled, savedSettings.Value.MicPrefilter, startup: true);
        store.PropertyChanged += StoreChanged;
        store.Updated += SettingsMaybeChanged;
        ticker = scheduler.Every(HeartbeatInterval, Tick);
        WriteHeartbeat(force: true);
        if (power != null) power.Suspending += OnSuspending;
        StartServer();
    }

    // Activation ("Take notes")

    /// <summary>
    /// The activation handler, for a pipe message and for <c>--detected-teams</c> of the first instance; UI thread only.
    /// The ID only names the request: the fresh, unused request.json written by the helper is what allows a recording.
    /// </summary>
    public DetectedMeetingResult HandleActivation(Guid id) => app.OpenDetectedTeamsMeeting(() => Consume(id));

    private bool Consume(Guid id)
    {
        try { return Storage.Consume(id, time.GetUtcNow()) == id; }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            store.Error = Strings.Format(Strings.DetectedMeetingFailed, failure.Message);
            return false;
        }
    }

    private void StartServer()
    {
        try
        {
            var listening = new ActivationPipeServer((id, _) => onUiThread(() => !disposed && HandleActivation(id).Accepted), PipeName);
            listening.Received += (message, accepted) => Trace.TraceInformation($"Activation {(accepted ? "accepted" : "rejected")}: {message}");
            listening.Start();
            server = listening;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            // Another process holds the pipe name: "Take notes" then starts Wallfly.exe --detected-teams, which forwards here.
            Trace.TraceWarning("Activation pipe unavailable: " + failure.Message);
        }
    }

    // Heartbeat

    /// <summary>heartbeat.json: busy while a recording starts, runs or stops and while AI processing runs; presenting in presentation mode.</summary>
    public static (bool Busy, bool Presenting) HeartbeatState(MeetingLibraryService store) => (store.Busy, store.PresentationMode);

    private void WriteHeartbeat(bool force)
    {
        if (disposed) return;
        var state = HeartbeatState(store);
        if (!force && lastHeartbeat == state) return;
        try
        {
            Storage.WriteHeartbeat(state.Busy, state.Presenting, time.GetUtcNow());
            lastHeartbeat = state;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            // A missing heartbeat only means the helper may show its popup; the next tick tries again.
        }
    }

    private void StoreChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is null or "" or nameof(MeetingLibraryService.Busy) or nameof(MeetingLibraryService.IsRecording)
            or nameof(MeetingLibraryService.IsStarting) or nameof(MeetingLibraryService.IsStopping) or nameof(MeetingLibraryService.ProcessingID)
            or nameof(MeetingLibraryService.PresentationMode))
            WriteHeartbeat(force: false);
    }

    private void Tick()
    {
        if (disposed) return;
        WriteHeartbeat(force: true);
        // A helper that had not written a status at startup gets its version check once its status is fresh. A helper
        // that is not running is only started by the toggle, the next launch or "Start helper now" (no restart loop).
        if (applied is { Enabled: true } && !restartTried) EnsureRunning(allowStart: false);
        if (StatusChanged != null || pendingDiagnostic != null) Refresh();
    }

    // Configuration, sign-in start and helper process

    private (bool Enabled, bool MicPrefilter) SavedSettings() => (store.Settings.DetectTeamsMeetings, store.Settings.DetectorMicPrefilter());

    /// <summary>A save changed the two helper settings (an edit applied from the settings window is already in place).</summary>
    private void SettingsMaybeChanged()
    {
        var saved = SavedSettings();
        if (savedSettings == saved) return;
        savedSettings = saved;
        if (applied != saved) Apply(saved.Enabled, saved.MicPrefilter, startup: false);
    }

    public void Apply(bool enabled, bool micPrefilter) => Apply(enabled, micPrefilter, startup: false);

    /// <summary>
    /// The settings window closed: a toggle it applied at once but did not save (closed while a recording started, or a
    /// failed save) is undone, so configuration.json, the sign-in start and settings.json agree again.
    /// </summary>
    public void ApplySavedSettings()
    {
        if (!started || disposed) return;
        var saved = SavedSettings();
        savedSettings = saved;
        if (applied != saved) Apply(saved.Enabled, saved.MicPrefilter, startup: false);
    }

    private void Apply(bool enabled, bool micPrefilter, bool startup)
    {
        if (disposed) return;
        var previous = applied;
        applied = (enabled, micPrefilter);
        error = null;
        // The helper reads configuration.json on every tick and exits when detection is off.
        WriteConfiguration(enabled, micPrefilter);
        if (startup) RegisterOnce(enabled);
        else if (previous?.Enabled != enabled) SetAutostart(enabled);
        if (enabled) EnsureRunning();
        Refresh();
    }

    private bool WriteConfiguration(bool enabled, bool micPrefilter)
    {
        try
        {
            Storage.WriteConfiguration(new DetectionConfiguration { Enabled = enabled, MicPrefilter = micPrefilter });
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            error = Strings.Format(Strings.DetectorSettingFailed, failure.Message);
            return false;
        }
    }

    /// <summary>First launch registers the sign-in start once; later only the toggle changes it (Task Manager's choice stays).</summary>
    private void RegisterOnce(bool enabled)
    {
        if (!enabled || uiState.State.DetectorRegistrationAttempted || !detector.IsInstalled) return;
        uiState.State.DetectorRegistrationAttempted = true;
        uiState.Save();
        SetAutostart(true);
    }

    private void SetAutostart(bool enabled)
    {
        // A development build has no helper next to Wallfly.exe: nothing to register.
        if (enabled && !detector.IsInstalled) return;
        try { DetectorAutostart.SetEnabled(enabled, detector.ExecutablePath, runKey); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            error = Strings.Format(Strings.DetectorSettingFailed, failure.Message);
        }
    }

    /// <summary>
    /// What to do with the helper: start it when it is not running; restart it (once per app run) when a fresh status
    /// shows another build than the one shipped next to Wallfly.exe, e.g. after an update. A missing or stale status is
    /// left alone: the helper may just be starting, and a restart could not tell which build runs either.
    /// </summary>
    public static DetectorAction Decide(bool enabled, bool installed, bool running, DetectionStatus? status, string? shippedVersion, DateTimeOffset now, bool restartTried = false)
    {
        if (!enabled || !installed) return DetectorAction.None;
        if (!running) return DetectorAction.Start;
        if (restartTried || string.IsNullOrEmpty(shippedVersion) || status is null || !status.IsFresh(now)) return DetectorAction.None;
        return string.Equals(status.HelperVersion, shippedVersion, StringComparison.Ordinal) ? DetectorAction.None : DetectorAction.Restart;
    }

    /// <param name="allowStart">False for the 2-s check: only an outdated helper is replaced.</param>
    private void EnsureRunning(bool allowStart = true)
    {
        var action = Decide(true, detector.IsInstalled, detector.IsRunning, Storage.Read<DetectionStatus>(DetectionStorage.StatusFile), detector.ShippedVersion,
            time.GetUtcNow(), restartTried);
        if (action == DetectorAction.None || (action == DetectorAction.Start && !allowStart)) return;
        if (action == DetectorAction.Restart)
        {
            restartTried = true;
            if (!detector.Stop(StopTimeout)) { error = Strings.DetectorStopFailed; return; }
        }
        if (!detector.Start()) error = Strings.DetectorStartFailed;
    }

    /// <summary>"Start helper now" (not running) and "Restart helper" (running, but no fresh status).</summary>
    public void StartHelper()
    {
        if (disposed || applied is not { Enabled: true } configuration) return;
        error = null;
        if (WriteConfiguration(true, configuration.MicPrefilter))
        {
            if (detector.IsRunning && !detector.Stop(StopTimeout)) error = Strings.DetectorStopFailed;
            else if (!detector.Start()) error = Strings.DetectorStartFailed;
        }
        Refresh();
    }

    // Status and diagnostics

    public void Refresh()
    {
        if (disposed) return;
        var enabled = applied?.Enabled ?? store.Settings.DetectTeamsMeetings;
        var installed = detector.IsInstalled;
        AutostartState? autostart = null;
        if (enabled && installed)
        {
            try { autostart = DetectorAutostart.GetState(detector.ExecutablePath, runKey); }
            catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or System.Security.SecurityException) { }
        }
        Status = Describe(enabled, installed, enabled && detector.IsRunning, Storage.Read<DetectionStatus>(DetectionStorage.StatusFile), autostart, time.GetUtcNow(), error);
        UpdateDiagnostic();
        StatusChanged?.Invoke();
    }

    /// <summary>The settings rows: "Disabled", "Helper: not running", "Helper: running · last check 3 s ago" with the helper's message, …</summary>
    public static DetectorStatusInfo Describe(bool enabled, bool installed, bool running, DetectionStatus? status, AutostartState? autostart, DateTimeOffset now, string? error = null)
    {
        if (!enabled) return new(Strings.DetectorDisabled, Error: error);
        if (!installed) return new(Strings.DetectorNotInstalled, Error: error);
        var start = autostart switch
        {
            AutostartState.Enabled => Strings.DetectorAutostartEnabled,
            AutostartState.NotRegistered => Strings.DetectorAutostartOff,
            AutostartState.DisabledByUser => Strings.DetectorAutostartDisabledByUser,
            AutostartState.OtherPath => Strings.DetectorAutostartOtherPath,
            _ => null
        };
        if (!running) return new(Strings.DetectorNotRunning, null, start, Strings.StartHelperNow, error);
        if (status is null || !status.IsFresh(now)) return new(Strings.DetectorNotResponding, null, start, Strings.RestartHelper, error);
        var age = (int)Math.Floor(Math.Max(0, (now - status.Date).TotalSeconds));
        return new(Strings.Format(Strings.DetectorRunning, age), string.IsNullOrWhiteSpace(status.Message) ? null : status.Message, start, null, error);
    }

    /// <summary>Writes diagnostic-request.json; the helper answers with a structure-only diagnostic-report.json (no names, no texts).</summary>
    public void RequestDiagnostics()
    {
        if (disposed || applied is not { Enabled: true }) return;
        var request = new DetectionDiagnosticRequest { Date = time.GetUtcNow() };
        try { Storage.Write(request, DetectionStorage.DiagnosticRequestFile); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
            DiagnosticStatus = Strings.Format(Strings.TeamsDiagnosticsFailed, failure.Message);
            Refresh();
            return;
        }
        pendingDiagnostic = request;
        DiagnosticStatus = Strings.TeamsDiagnosticsCreating;
        if (detector.IsInstalled && !detector.IsRunning && !detector.Start()) error = Strings.DetectorStartFailed;
        Refresh();
    }

    private void UpdateDiagnostic()
    {
        if (pendingDiagnostic is not { } pending) return;
        if (Storage.Read<DetectionDiagnosticReport>(DetectionStorage.DiagnosticReportFile)?.RequestId == pending.Id)
        {
            DiagnosticStatus = Strings.TeamsDiagnosticsCreated;
            pendingDiagnostic = null;
        }
        else if (time.GetUtcNow() - pending.Date > DiagnosticTimeout)
        {
            DiagnosticStatus = Strings.TeamsDiagnosticsNoResponse;
            pendingDiagnostic = null;
        }
    }

    /// <summary>Explorer with the detection folder, the diagnostic report selected when there is one.</summary>
    public void OpenDetectionFolder()
    {
        try { Directory.CreateDirectory(Storage.Root); }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { }
        var report = Storage.PathOf(DetectionStorage.DiagnosticReportFile);
        if (File.Exists(report)) app.Dialogs.Reveal(report);
        else app.Dialogs.OpenFolder(Storage.Root);
    }

    // Sleep

    private void OnSuspending()
    {
        if (disposed) return;
        _ = onUiThread(() =>
        {
            if (!disposed) SleepStop = StopForSleepAsync();
            return true;
        });
    }

    /// <summary>
    /// The macOS app ends a recording when the Mac goes to sleep; Windows does the same on PBT_APMSUSPEND. Core's
    /// <see cref="MeetingLibraryService.StopRecordingAsync"/> only logs its stop reason, so the banner is set here (a stop
    /// failure the service reports replaces it). A stop that is already running (the user, the auto-stop) is waited for
    /// and keeps its own outcome.
    /// </summary>
    internal async Task StopForSleepAsync()
    {
        if (store.IsStopping) { await store.StopRecordingAsync(SleepStopBudget, RecordingStopReasons.Sleep); return; }
        if (!store.IsRecording) return;
        store.Error = Strings.RecordingEndedSleep;
        await store.StopRecordingAsync(SleepStopBudget, RecordingStopReasons.Sleep);
        if (store.Error.Length == 0) store.Error = Strings.RecordingEndedSleep;
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        ticker?.Dispose();
        store.PropertyChanged -= StoreChanged;
        store.Updated -= SettingsMaybeChanged;
        if (power != null)
        {
            power.Suspending -= OnSuspending;
            power.Dispose();
        }
        server?.Dispose();
        server = null;
    }
}

/// <summary>
/// <c>Wallfly.exe --detected-teams &lt;UUID&gt;</c> (the helper's fallback when the pipe had no listener): while Wallfly
/// already runs, this second process hands the ID to it over the pipe and exits silently, before the single-instance check.
/// </summary>
internal static class ActivationForwarding
{
    public const int Attempts = 10;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(3);
    public static readonly TimeSpan RetryDelay = TimeSpan.FromMilliseconds(500);

    /// <returns>
    /// True when this process must exit silently (the running app answered, could not be reached, or never opened its
    /// pipe); false when no Wallfly runs and this process starts as the first instance and handles the ID itself.
    /// </returns>
    public static async Task<bool> ForwardAsync(Guid id, Func<Guid, Task<ActivationDelivery>> send, Func<bool> appRunning, Func<TimeSpan, Task>? delay = null)
    {
        // The usual case (the helper only launches Wallfly.exe when its pipe had no listener): no app, so no pipe wait.
        if (!appRunning()) return false;
        delay ??= wait => Task.Delay(wait);
        for (var attempt = 1; ; attempt++)
        {
            var delivery = await send(id).ConfigureAwait(false);
            // Answered ("ok" or "rejected"), or reached but not answering in time: never a second window or message.
            if (delivery != ActivationDelivery.NoServer) return true;
            // The app ended meanwhile: this process becomes the app.
            if (!appRunning()) return false;
            // Wallfly runs but its pipe is not open yet (it is still starting): try again, then give up silently.
            if (attempt >= Attempts) return true;
            await delay(RetryDelay).ConfigureAwait(false);
        }
    }

    public static Task<ActivationDelivery> SendAsync(Guid id) => ActivationPipeClient.SendAsync(id, Timeout);
}

/// <summary>The real helper: <c>Wallfly Detector.exe</c> next to Wallfly.exe, one per Windows session.</summary>
internal sealed class DetectorProcess(string executablePath) : IDetectorProcess
{
    private string? shippedVersion;

    public string ExecutablePath { get; } = executablePath;
    public bool IsInstalled => File.Exists(ExecutablePath);

    /// <summary>Read once: the file only changes with an update, which restarts Wallfly as well.</summary>
    public string? ShippedVersion
    {
        get
        {
            if (shippedVersion != null || !IsInstalled) return shippedVersion;
            try { return shippedVersion = FileVersionInfo.GetVersionInfo(ExecutablePath).ProductVersion; }
            catch (FileNotFoundException) { return null; }
        }
    }

    public bool IsRunning => WallflyPaths.IsAppRunning(WallflyPaths.DetectorMutexName);

    public bool Start()
    {
        if (!IsInstalled) return false;
        try
        {
            // Shell execute: the long-lived helper inherits no handles (a redirected console of Wallfly stays closable).
            using var process = Process.Start(new ProcessStartInfo(ExecutablePath) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(ExecutablePath) ?? "" });
            return process != null;
        }
        catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or IOException)
        {
            Trace.TraceWarning("Wallfly Detector could not be started: " + failure.Message);
            return false;
        }
    }

    public bool Stop(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        int session;
        using (var current = Process.GetCurrentProcess()) session = current.SessionId;
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(WallflyPaths.DetectorExecutableName)))
        {
            using (process)
            {
                try
                {
                    if (process.SessionId != session) continue;
                    // The helper writes every file atomically, so ending it cannot leave a half-written file behind.
                    process.Kill();
                    process.WaitForExit(Math.Max(0, (int)(deadline - DateTime.UtcNow).TotalMilliseconds));
                }
                catch (Exception failure) when (failure is Win32Exception or InvalidOperationException or NotSupportedException) { }
            }
        }
        // The single-instance mutex goes with the process; a helper started before that would exit at once.
        while (IsRunning && DateTime.UtcNow < deadline) Thread.Sleep(50);
        return !IsRunning;
    }
}
