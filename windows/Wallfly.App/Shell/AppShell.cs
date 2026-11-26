using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Threading;
using Wallfly.App.ViewModels;
using Wallfly.Audio;
using Wallfly.Core.Credentials;
using Wallfly.Core.Persistence;
using Wallfly.Core.Services;
using Wallfly.Integration;
using Wallfly.Teams;

namespace Wallfly.App.Shell;

/// <summary>
/// Composition root: the service, the root view model, the main window, capsule and tray, the session-end guard and the
/// Teams detection (activation pipe, heartbeat, helper process, sleep). With <c>--smoke-test</c> every device, provider,
/// dialog and the helper are fakes (own pipe name and detection folder), so the run needs no hardware and no network.
/// </summary>
internal sealed class AppShell : IDisposable
{
    private readonly bool smoke;
    private SessionEndGuard? guard;
    private bool shutDown;
    private bool disposed;

    public AppShell(string root, bool smoke, bool pointerManaged)
    {
        this.smoke = smoke;
        Root = root;
        var dispatcher = Dispatcher.CurrentDispatcher;
        SmokeProvider = smoke ? new UiSmokeProvider() : null;
        SmokeMatching = smoke ? new SmokeSpeakerMatching() : null;
        // The Core hooks: Teams call source (Teams-call detection for live names, silent-meeting-audio guard, "Stop recording when the meeting
        // ends"), live Teams speaker names, speaker matching after a stop and speaker editing. The smoke run has no Teams,
        // UI Automation or models: only the pure speaker editor and a scripted matching step, with its own library pointer.
        var hooks = smoke ? UiSmokeHooks.Create(root, SmokeMatching!) : CreateHooks();
        Store = new MeetingLibraryService(new MeetingLibrary(root), action => dispatcher.InvokeAsync(action).Task,
            providerFactory: smoke ? (_, _) => SmokeProvider! : null,
            transcriptionFactory: smoke ? (_, _) => new UiSmokeTranscriptionClient() : null,
            engineFactory: smoke ? _ => SmokeEngine = new FakeAudioEngine() : null,
            devices: smoke ? new FakeAudioDeviceCatalog(new AudioDevice("{0.0.1.00000000}.{smoke-headset}", "Headset Microphone (Smoke Test)", true, true),
                new AudioDevice("{0.0.1.00000000}.{smoke-array}", "Microphone Array (Smoke Test)")) : null,
            credentials: smoke ? new InMemoryCredentialStore() : null,
            loadCredential: !smoke,
            hooks: hooks);
        var wpfDialogs = new WpfDialogService(() => Window);
        Dialogs = smoke ? new SmokeDialogService(root) : wpfDialogs;
        System = smoke ? new SmokeSystemProbe(Store.Devices) : new SystemProbe(Store.Devices);
        // A subfolder: *.json files in the library root are read as meetings.
        var uiState = new UiStateStore(Path.Combine(smoke ? root : new LibraryPointer().Directory, "UI"));
        ViewModel = new AppViewModel(Store, Dialogs, new DispatcherScheduler(dispatcher), uiState, System, new LibraryLocation(!smoke && pointerManaged));
        wpfDialogs.Attach(() => ViewModel);
        Window = new MainWindow(ViewModel, Store);
        Window.QuitConfirmed += () => Shutdown(0);
        Indicator = new LiveMeetingIndicator(Window, Store, OpenRunningMeeting, () => _ = Window.QuitAsync());
        // A session end that another app vetoes after the save ended the recording: the service accepts work again.
        Window.SourceInitialized += (_, _) => guard = new SessionEndGuard(Window, SaveForSessionEndAsync, () => Shutdown(0), Store.CancelClose);
        var detectionStorage = new DetectionStorage(smoke ? Path.Combine(root, "Detection") : null);
        Power = smoke ? new SmokePowerEvents() : CreatePowerEvents();
        Detection = new DetectorIntegration(ViewModel, detectionStorage,
            smoke ? new SmokeDetectorProcess(detectionStorage) : new DetectorProcess(DetectorAutostart.DefaultDetectorPath),
            smoke ? new InMemoryRunKeyStore() : new RegistryRunKeyStore(),
            new DispatcherScheduler(dispatcher), callback => dispatcher.InvokeAsync(callback).Task, uiState, Power,
            pipeName: smoke ? WallflyPaths.ActivationPipeName + ".Smoke." + Guid.NewGuid().ToString("N") : WallflyPaths.ActivationPipeName);
        ViewModel.Detection = Detection;
    }

    public string Root { get; }
    public MeetingLibraryService Store { get; }
    public AppViewModel ViewModel { get; }
    public MainWindow Window { get; }
    public LiveMeetingIndicator Indicator { get; }
    public IDialogService Dialogs { get; }
    public ISystemProbe System { get; }
    public SessionEndGuard? Guard => guard;
    public UiSmokeProvider? SmokeProvider { get; }
    public SmokeSpeakerMatching? SmokeMatching { get; }
    public FakeAudioEngine? SmokeEngine { get; private set; }
    public DetectorIntegration Detection { get; }
    public IPowerEvents? Power { get; }

    /// <param name="detectedTeams">The request ID of <c>Wallfly.exe --detected-teams</c> when this process is the first instance.</param>
    public void Start(Guid? detectedTeams = null)
    {
        Detection.Start();
        if (smoke)
        {
            Window.Loaded += async (_, _) => await new SmokeTest(this).RunAsync();
            Window.Show();
            return;
        }
        Window.Show();
        // First start: the setup opens over the window; no recording can start while it is open.
        if (!Store.Settings.OnboardingCompleted) Window.Dispatcher.BeginInvoke(() => Dialogs.ShowOnboarding(), DispatcherPriority.ApplicationIdle);
        // After the setup check: while the setup is open the request stays unused (and the helper can offer it again).
        if (detectedTeams is Guid id) Window.Dispatcher.BeginInvoke(() => Detection.HandleActivation(id), DispatcherPriority.ApplicationIdle);
    }

    /// <summary>
    /// The production hooks (<see cref="IntegrationServices.CreateDefault"/>). Should they fail to come up, the app still
    /// works without them: whole-system meeting audio, no auto-stop, no Teams names and no speaker matching.
    /// </summary>
    private static LibraryServiceHooks? CreateHooks()
    {
        try { return IntegrationServices.CreateDefault(); }
        catch (Exception error)
        {
            Trace.TraceError("Wallfly: the Teams and speaker hooks are unavailable: " + error.Message);
            return null;
        }
    }

    /// <summary>Sleep notifications (<c>SystemEvents.PowerModeChanged</c>); without them a sleeping recording simply continues after wake-up.</summary>
    private static IPowerEvents? CreatePowerEvents()
    {
        try { return new PowerEvents(); }
        catch (Exception error) when (error is InvalidOperationException or ExternalException)
        {
            Trace.TraceWarning("Power events unavailable: " + error.Message);
            return null;
        }
    }

    /// <summary>Capsule click and tray "Open current meeting": the window comes back with the running note.</summary>
    private void OpenRunningMeeting()
    {
        Window.BringToFront();
        ViewModel.OpenActiveMeeting();
        Indicator.Refresh();
    }

    /// <summary>
    /// Session end saves without asking: a running recording ends within the quit budget and speaker matching is dropped
    /// (the next start shows it as interrupted). Should the session end be called off, <see cref="SessionEndGuard"/> calls
    /// <see cref="MeetingLibraryService.CancelClose"/>, which queues the matching again.
    /// </summary>
    private Task<bool> SaveForSessionEndAsync() =>
        Store.Busy || Store.PostRecordingSteps.Any(step => step.HasPendingWork) ? Store.CloseAsync() : Task.FromResult(Store.Flush());

    public void Shutdown(int exitCode)
    {
        if (shutDown) return;
        shutDown = true;
        Window.AllowClose();
        Dispose();
        Application.Current.Shutdown(exitCode);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Detection.Dispose();
        Indicator.Dispose();
        guard?.Dispose();
        ViewModel.Dispose();
        Store.Dispose();
    }
}
