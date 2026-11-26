using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Core;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>One entry of the step indicator ("1 Storage · 2 AI services · 3 Permissions").</summary>
public sealed record OnboardingStep(int Index, string Label, bool IsCurrent, bool ShowsSeparator);

/// <summary>
/// "Set up Wallfly" (spec 9.4, port of <c>MeetingOnboardingView</c>): three steps over the settings sections. "Next"
/// saves without completing; "Done" and "Set up later" complete the setup for good. Windows asks only for the
/// microphone in step 3 (no system-audio or Teams permission exists). No recording starts while it is open.
/// </summary>
public sealed class OnboardingViewModel : ObservableObject
{
    private static readonly string[] Titles = [Strings.StepStorage, Strings.StepAi, Strings.StepPermissions];
    private readonly MeetingLibraryService store;
    private readonly ISystemProbe system;
    private int step;
    private MicrophoneAccess microphone;

    public OnboardingViewModel(MeetingLibraryService store, SettingsViewModel settings, ISystemProbe system, IDialogService dialogs, int initialStep = 0)
    {
        this.store = store;
        this.system = system;
        Settings = settings;
        step = Math.Clamp(initialStep, 0, Titles.Length - 1);
        microphone = system.MicrophoneAccess();
        NextCommand = new RelayCommand(Next, () => !store.Busy);
        BackCommand = new RelayCommand(() => Step--, () => step > 0 && !store.Busy);
        SetUpLaterCommand = new RelayCommand(Finish, () => !store.Busy);
        MicrophoneCommand = new RelayCommand(() => { dialogs.OpenUri("ms-settings:privacy-microphone"); RefreshPermissions(); });
    }

    public SettingsViewModel Settings { get; }
    public bool Completed { get; private set; }
    public event Action? CloseRequested;

    public int Step
    {
        get => step;
        set
        {
            if (!SetProperty(ref step, Math.Clamp(value, 0, Titles.Length - 1))) return;
            if (step == Titles.Length - 1) RefreshPermissions();
            OnPropertyChanged(string.Empty);
            BackCommand.NotifyCanExecuteChanged();
        }
    }

    public IReadOnlyList<OnboardingStep> Steps => Titles.Select((title, index) => new OnboardingStep(index, Strings.Format(Strings.StepLabel, index + 1, title), index == step, index > 0)).ToList();
    public bool IsLastStep => step >= Titles.Length - 1;
    public string PrimaryText => IsLastStep ? Strings.Done : Strings.Next;
    public bool ShowBack => step > 0;
    public bool IsStorageStep => step == 0;
    public bool IsAiStep => step == 1;
    public bool IsPermissionsStep => step == 2;

    public IRelayCommand NextCommand { get; }
    public IRelayCommand BackCommand { get; }
    public IRelayCommand SetUpLaterCommand { get; }
    public IRelayCommand MicrophoneCommand { get; }

    public bool MicrophoneAllowed => microphone == MicrophoneAccess.Allowed;
    public string MicrophoneButtonText => MicrophoneAllowed ? Strings.OpenSettingsButton : Strings.Allow;
    public string MicrophoneDetail => microphone == MicrophoneAccess.Denied ? Strings.PermissionMicrophoneDenied : Strings.PermissionMicrophoneDetail;

    /// <summary>Only what still blocks transcription or enhancement; a deliberately disabled enhancement is no issue.</summary>
    public IReadOnlyList<string> OpenIssues
    {
        get
        {
            var issues = new List<string>();
            if (Settings.TranscriptionIssue is { } transcription) issues.Add(transcription);
            if (store.Settings.Provider != EnhancementProvider.Disabled && store.EnhancementConfigurationIssue is { } enhancement) issues.Add(enhancement);
            return issues;
        }
    }

    public string? Error => Settings.SaveError ?? (store.Error.Length == 0 ? null : store.Error);

    public void RefreshPermissions()
    {
        microphone = system.MicrophoneAccess();
        OnPropertyChanged(nameof(MicrophoneAllowed)); OnPropertyChanged(nameof(MicrophoneButtonText)); OnPropertyChanged(nameof(MicrophoneDetail));
        OnPropertyChanged(nameof(OpenIssues));
    }

    /// <summary>"Next" saves the step but does not complete the setup; on the last step it is "Done".</summary>
    private void Next()
    {
        if (IsLastStep) { Finish(); return; }
        if (Settings.Save()) Step++;
        OnPropertyChanged(nameof(Error));
    }

    /// <summary>"Done" and "Set up later": saves and marks the setup completed, so it does not open again.</summary>
    public void Finish()
    {
        Settings.Draft.OnboardingCompleted = true;
        if (!Settings.Save()) { Settings.Draft.OnboardingCompleted = store.Settings.OnboardingCompleted; OnPropertyChanged(nameof(Error)); return; }
        Completed = true;
        CloseRequested?.Invoke();
    }
}
