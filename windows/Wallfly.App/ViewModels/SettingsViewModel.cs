using System.Collections.ObjectModel;
using System.IO;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Audio;
using Wallfly.Core;
using Wallfly.Core.Json;
using Wallfly.Core.Persistence;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>
/// A meeting under Storage that needs "Try again": audio whose automatic deletion failed (Core
/// <see cref="MeetingLibraryService.RetryAudioCleanup"/>) or a transcript copy that could not be written (Core
/// <see cref="MeetingLibraryService.RetryTranscriptMirror"/>).
/// </summary>
public sealed record PendingDeletionViewModel(Guid MeetingID, string Text, string Detail, IRelayCommand TryAgainCommand);

/// <summary>Whether this run may move the library (Core <see cref="MeetingLibraryService.MoveLibraryAsync"/>).</summary>
public interface ILibraryLocation
{
    /// <summary>False when the data folder came from <c>--data-dir</c> (tests, smoke test): the pointer is not touched.</summary>
    bool CanChange { get; }
}

/// <summary>The library location of this run: changeable only when the app follows the library pointer.</summary>
public sealed class LibraryLocation(bool canChange) : ILibraryLocation
{
    public bool CanChange { get; } = canChange;
}

/// <summary>
/// The settings window (spec 9.3) with its three areas, and the shared sections of the onboarding. Edits go into a
/// draft; "Done" (and "Next" in the onboarding) saves draft and keys. While a recording runs only the live controls
/// work: "Switch microphone" (Core switches the running engine, then saves the choice), the Deepgram key and "Reconnect".
/// Storage moves the library through Core and offers "Try again" for failed audio deletions and transcript copies.
/// </summary>
public sealed class SettingsViewModel : ObservableObject
{
    public const string AutomaticAudio = "\u0001automatic";

    private readonly MeetingLibraryService store;
    private readonly IDialogService dialogs;
    private readonly ISystemProbe system;
    private readonly ILibraryLocation location;
    private readonly ITeamsDetection? detection;
    private SettingsArea area;
    private string openAiKey;
    private string deepgramKey;
    private string liveDeepgramKey = "";
    private string? deviceIssue;
    private string? appsIssue;
    private string? saveError;
    private string? diagnosticsStatus;
    private string? storageStatus;
    private string? liveMicrophoneError;
    private string customVocabulary;

    /// <param name="detection">Wallfly Detector; null hides the helper rows (onboarding, tests).</param>
    public SettingsViewModel(MeetingLibraryService store, IDialogService dialogs, ISystemProbe system, ILibraryLocation location, SettingsArea area = SettingsArea.Recording,
        ITeamsDetection? detection = null)
    {
        this.store = store;
        this.dialogs = dialogs;
        this.system = system;
        this.location = location;
        this.detection = detection;
        this.area = area;
        Draft = WallflyJson.Copy(store.Settings);
        openAiKey = store.ApiKey;
        deepgramKey = store.DeepgramKey;
        customVocabulary = string.Join(Environment.NewLine, Draft.CustomVocabulary ?? []);
        Enhancement = new EnhancementSettingsViewModel(WallflyJson.Copy(store.Settings), store.AnthropicApiKey, () => openAiKey, dialogs);
        TranscriptionProviders =
        [
            new(TranscriptionProvider.Deepgram, Strings.ProviderDeepgram), new(TranscriptionProvider.LocalWhisper, Strings.ProviderLocalWhisper),
            new(TranscriptionProvider.OpenAi, Strings.ProviderOpenAi)
        ];
        Languages = [new("auto", Strings.LanguageAutomatic), new("de-DE", Strings.LanguageGerman), new("en-US", Strings.LanguageEnglish)];
        SpeakerSeparations = [new(SpeakerSeparationEngine.Deepgram, Strings.SpeakerSeparationDeepgram), new(SpeakerSeparationEngine.OnDevice, Strings.SpeakerSeparationOnDevice)];
        RefreshMicrophonesCommand = new RelayCommand(RefreshDevices);
        RefreshAppsCommand = new RelayCommand(RefreshApps);
        OpenMicrophonePrivacyCommand = new RelayCommand(() => dialogs.OpenUri("ms-settings:privacy-microphone"));
        OpenSoundSettingsCommand = new RelayCommand(() => dialogs.OpenUri("ms-settings:sound"));
        CopyDiagnosticsCommand = new RelayCommand(CopyDiagnostics);
        ChooseWhisperAppCommand = new RelayCommand(() => { if (dialogs.AskFile(Strings.WhisperAppFilter, WhisperAppPath) is { } path) WhisperAppPath = path; });
        ChooseWhisperModelCommand = new RelayCommand(() => { if (dialogs.AskFile(Strings.WhisperModelFilter, WhisperModelPath) is { } path) WhisperModelPath = path; });
        ChangeLocationCommand = new AsyncRelayCommand(ChangeLocationAsync, () => CanMoveLibrary);
        ShowLibraryCommand = new RelayCommand(() => dialogs.OpenFolder(store.Library.Root));
        ChooseMirrorFolderCommand = new RelayCommand(ChooseMirrorFolder, () => !store.Busy);
        ShowMirrorFolderCommand = new RelayCommand(() => { if (MirrorPath is { } path) dialogs.OpenFolder(path); });
        ReconnectCommand = new RelayCommand(Reconnect, () => store.CanReconnectTranscription && liveDeepgramKey.Trim().Length > 0);
        SwitchMicrophoneCommand = new AsyncRelayCommand<string>(SwitchMicrophoneAsync, _ => store.IsRecording && !store.IsStopping && !store.IsMovingLibrary);
        DoneCommand = new RelayCommand(() => { if (Save()) CloseRequested?.Invoke(); });
        OpenSetupCommand = new RelayCommand(() => OpenSetupRequested?.Invoke(), () => !store.Busy);
        SelectAreaCommand = new RelayCommand<string>(name => { if (Enum.TryParse<SettingsArea>(name, out var value)) Area = value; });
        StartHelperCommand = new RelayCommand(() => detection?.StartHelper(), () => detection?.Status.ActionText != null);
        OpenDetectionFolderCommand = new RelayCommand(() => detection?.OpenDetectionFolder(), () => detection != null);
        CreateTeamsDiagnosticsCommand = new RelayCommand(() => detection?.RequestDiagnostics(), () => detection != null && DetectTeams);
        RefreshDevices();
        RefreshApps();
        RefreshPendingDeletions();
        store.PropertyChanged += StorePropertyChanged;
        if (detection != null)
        {
            detection.StatusChanged += DetectionChanged;
            detection.Refresh();
        }
    }

    public AppSettings Draft { get; }
    public EnhancementSettingsViewModel Enhancement { get; }

    public event Action? CloseRequested;
    public event Action? OpenSetupRequested;

    public SettingsArea Area { get => area; set => SetProperty(ref area, value); }
    public IRelayCommand<string> SelectAreaCommand { get; }

    /// <summary>Everything but the live controls is read-only while the app records or processes.</summary>
    public bool CanEdit => !store.Busy;
    public string? BusyNote => !store.Busy ? null : store.IsMovingLibrary ? Strings.MovingLibrary : store.IsRecording ? Strings.MoreAfterRecording : Strings.ProcessingInProgress;
    public string? SaveError { get => saveError; private set => SetProperty(ref saveError, value); }

    // Recording · audio

    public ObservableCollection<Choice<string>> Microphones { get; } = [];
    public ObservableCollection<Choice<string>> Outputs { get; } = [];
    public ObservableCollection<Choice<string>> MeetingAudioOptions { get; } = [];
    public string? DeviceIssue { get => deviceIssue; private set => SetProperty(ref deviceIssue, value); }
    public string? AppsIssue { get => appsIssue; private set => SetProperty(ref appsIssue, value); }

    public Choice<string>? SelectedMicrophone
    {
        get => Microphones.FirstOrDefault(choice => choice.Value == Draft.MicrophoneUID);
        set { if (value != null && value.Value != Draft.MicrophoneUID) { Draft.MicrophoneUID = value.Value; OnPropertyChanged(); } }
    }

    public Choice<string>? SelectedOutput
    {
        get => Outputs.FirstOrDefault(choice => choice.Value == Draft.OutputDeviceID);
        set { if (value != null && value.Value != Draft.OutputDeviceID) { Draft.OutputDeviceID = value.Value; OnPropertyChanged(); } }
    }

    public Choice<string>? SelectedMeetingAudio
    {
        get => MeetingAudioOptions.FirstOrDefault(choice => choice.Value == (Draft.AutomaticMeetingAudioSource ? AutomaticAudio : Draft.AudioSourceBundleID));
        set
        {
            if (value == null) return;
            if (value.Value == AutomaticAudio) Draft.AutomaticMeetingAudioSource = true;
            else { Draft.AutomaticMeetingAudioSource = false; Draft.AudioSourceBundleID = value.Value; }
            OnPropertyChanged();
        }
    }

    public IRelayCommand RefreshMicrophonesCommand { get; }
    public IRelayCommand RefreshAppsCommand { get; }

    /// <summary>While recording, the source Core chose (macOS shows <c>recordingAudioDescription</c> here); otherwise the rule.</summary>
    public string MeetingAudioDetail => store.RecordingAudioDescription is { Length: > 0 } description ? description : Strings.MeetingAudioDetail;

    // Recording · speakers and Teams. All toggles persist through the draft; the two helper toggles also apply at once
    // (configuration.json, sign-in start, helper start), like the macOS toggle, so the status line below them is current.

    public bool TeamsSpeakerNames { get => Draft.TeamsSpeakerNames; set { Draft.TeamsSpeakerNames = value; OnPropertyChanged(); } }
    public bool MatchVoices { get => Draft.AcousticSpeakerRecognition; set { Draft.AcousticSpeakerRecognition = value; OnPropertyChanged(); OnPropertyChanged(nameof(DeleteDetail)); } }

    /// <summary>"Speaker separation": Deepgram (cloud) or On this device; read per run, so a change applies to the next run.</summary>
    public IReadOnlyList<Choice<SpeakerSeparationEngine>> SpeakerSeparations { get; }
    public Choice<SpeakerSeparationEngine> SelectedSpeakerSeparation
    {
        get => SpeakerSeparations.First(choice => choice.Value == Draft.EffectiveSpeakerSeparation);
        set
        {
            // Only a real change is stored: a user who never chose keeps following AppSettings.DefaultSpeakerSeparation.
            if (value == null || value.Value == Draft.EffectiveSpeakerSeparation) return;
            Draft.SpeakerSeparation = value.Value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SpeakerSeparationDetail));
        }
    }
    public string SpeakerSeparationDetail => Draft.EffectiveSpeakerSeparation == SpeakerSeparationEngine.Deepgram
        ? Strings.SpeakerSeparationDeepgramDetail : Strings.SpeakerSeparationOnDeviceDetail;
    public bool DetectTeams
    {
        get => Draft.DetectTeamsMeetings;
        set
        {
            if (value == Draft.DetectTeamsMeetings) return;
            Draft.DetectTeamsMeetings = value;
            OnPropertyChanged();
            CreateTeamsDiagnosticsCommand.NotifyCanExecuteChanged();
            detection?.Apply(value, Draft.DetectorMicPrefilter());
        }
    }
    public bool MicPrefilter
    {
        get => Draft.DetectorMicPrefilter();
        set
        {
            if (value == Draft.DetectorMicPrefilter()) return;
            Draft.SetDetectorMicPrefilter(value);
            OnPropertyChanged();
            detection?.Apply(Draft.DetectTeamsMeetings, value);
        }
    }
    /// <summary>Bound to <see cref="AppSettings.AutoStopWhenMeetingEnds"/>; the service does the stopping.</summary>
    public bool AutoStop { get => Draft.AutoStopWhenMeetingEnds; set { Draft.AutoStopWhenMeetingEnds = value; OnPropertyChanged(); } }
    public bool HideIndicatorFromCapture { get => Draft.HideIndicatorFromScreenCapture; set { Draft.HideIndicatorFromScreenCapture = value; OnPropertyChanged(); } }

    // Recording · Wallfly Detector status

    public bool HasDetection => detection != null;
    public string? DetectionLine => detection?.Status.Line;
    public string? DetectionDetail => detection?.Status.Detail;
    public string? DetectionAutostart => detection?.Status.Autostart;
    public string? DetectionError => detection?.Status.Error;
    public string? DetectionActionText => detection?.Status.ActionText;
    public string? DetectionDiagnosticStatus => detection?.DiagnosticStatus;
    public IRelayCommand StartHelperCommand { get; }
    public IRelayCommand OpenDetectionFolderCommand { get; }
    public IRelayCommand CreateTeamsDiagnosticsCommand { get; }

    /// <summary>The window closed: it stops following the store and the helper status, and a helper toggle it did not save is undone.</summary>
    public void Detach()
    {
        store.PropertyChanged -= StorePropertyChanged;
        if (detection == null) return;
        detection.StatusChanged -= DetectionChanged;
        detection.ApplySavedSettings();
    }

    private void DetectionChanged()
    {
        OnPropertyChanged(nameof(DetectionLine)); OnPropertyChanged(nameof(DetectionDetail)); OnPropertyChanged(nameof(DetectionAutostart));
        OnPropertyChanged(nameof(DetectionError)); OnPropertyChanged(nameof(DetectionActionText)); OnPropertyChanged(nameof(DetectionDiagnosticStatus));
        StartHelperCommand.NotifyCanExecuteChanged();
    }

    public IRelayCommand OpenMicrophonePrivacyCommand { get; }
    public IRelayCommand OpenSoundSettingsCommand { get; }
    public IRelayCommand CopyDiagnosticsCommand { get; }
    public string? DiagnosticsStatus { get => diagnosticsStatus; private set => SetProperty(ref diagnosticsStatus, value); }

    // AI & language · transcription

    public IReadOnlyList<Choice<TranscriptionProvider>> TranscriptionProviders { get; }
    public Choice<TranscriptionProvider> SelectedTranscriptionProvider
    {
        get => TranscriptionProviders.First(choice => choice.Value == Draft.TranscriptionProvider);
        set
        {
            if (value == null || value.Value == Draft.TranscriptionProvider) return;
            Draft.TranscriptionProvider = value.Value;
            Draft.TranscriptionModel = ModelDefaults.SupportedModel(value.Value);
            OnPropertyChanged(string.Empty);
        }
    }

    public bool IsDeepgram => Draft.TranscriptionProvider == TranscriptionProvider.Deepgram;
    public bool IsOpenAiTranscription => Draft.TranscriptionProvider == TranscriptionProvider.OpenAi;
    public bool IsLocalWhisper => Draft.TranscriptionProvider == TranscriptionProvider.LocalWhisper;
    /// <summary>OpenAI tells speakers apart less reliably: the hint turns into a warning.</summary>
    public bool SpeakerHintIsWarning => IsOpenAiTranscription;

    public string OpenAiKey
    {
        get => openAiKey;
        set { if (SetProperty(ref openAiKey, value ?? "")) { OnPropertyChanged(nameof(OpenAiKeyMissing)); OnPropertyChanged(nameof(OpenAiKeyStatus)); Enhancement.OpenAiKeyChanged(); } }
    }

    public string DeepgramKey
    {
        get => deepgramKey;
        set { if (SetProperty(ref deepgramKey, value ?? "")) { OnPropertyChanged(nameof(DeepgramKeyMissing)); OnPropertyChanged(nameof(DeepgramKeyStatus)); } }
    }

    public bool OpenAiKeyMissing => openAiKey.Trim().Length == 0;
    public string OpenAiKeyStatus => OpenAiKeyMissing ? Strings.KeyMissing : Strings.KeyEntered;
    public bool DeepgramKeyMissing => deepgramKey.Trim().Length == 0;
    public string DeepgramKeyStatus => DeepgramKeyMissing ? Strings.KeyMissing : Strings.KeyEntered;

    public string WhisperAppPath { get => Draft.OpenWhisprAppPath; set { Draft.OpenWhisprAppPath = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(LocalFound)); OnPropertyChanged(nameof(LocalStatus)); } }
    public string WhisperModelPath { get => Draft.WhisperModelPath; set { Draft.WhisperModelPath = value ?? ""; OnPropertyChanged(); OnPropertyChanged(nameof(LocalFound)); OnPropertyChanged(nameof(LocalStatus)); } }
    public bool LocalFound => Draft.OpenWhisprAppPath.Length > 0 && Draft.WhisperModelPath.Length > 0 && system.FileExists(Draft.OpenWhisprAppPath) && system.FileExists(Draft.WhisperModelPath);
    public string LocalStatus => LocalFound ? Strings.LocalFound : Strings.LocalMissing;
    public IRelayCommand ChooseWhisperAppCommand { get; }
    public IRelayCommand ChooseWhisperModelCommand { get; }

    public IReadOnlyList<Choice<string>> Languages { get; }
    public Choice<string> SelectedLanguage
    {
        get => Languages.FirstOrDefault(choice => choice.Value == Draft.Locale) ?? Languages[0];
        set { if (value != null) { Draft.Locale = value.Value; OnPropertyChanged(); } }
    }

    /// <summary>
    /// "Custom vocabulary" (Deepgram keyterms): the text as typed, one term per line or comma-separated. The draft holds
    /// the parsed list (<see cref="ParseVocabulary"/>), so typing is never reformatted.
    /// </summary>
    public string CustomVocabulary
    {
        get => customVocabulary;
        set { if (SetProperty(ref customVocabulary, value ?? "")) Draft.CustomVocabulary = ParseVocabulary(customVocabulary); }
    }

    /// <summary>Lines and comma-separated parts, trimmed, without empty entries and case-insensitive duplicates (the first spelling wins).</summary>
    public static List<string> ParseVocabulary(string? text) =>
        (text ?? "").Split(['\r', '\n', ','], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase).ToList();

    /// <summary>What still blocks transcription (onboarding step 3); null when nothing does.</summary>
    public string? TranscriptionIssue => Draft.TranscriptionProvider switch
    {
        TranscriptionProvider.Deepgram => DeepgramKeyMissing ? Strings.DeepgramKeyMissing : null,
        TranscriptionProvider.OpenAi => OpenAiKeyMissing ? Strings.OpenAiTranscriptionKeyMissing : null,
        _ => LocalFound ? null : Strings.LocalWhisperMissing
    };

    // Storage

    public string LibraryPath => store.Library.Root;
    /// <summary>"Change…": this run follows the library pointer and nothing runs (recording, AI processing, speaker matching).</summary>
    public bool CanMoveLibrary => location.CanChange && !store.Busy && !store.PostRecordingSteps.Any(step => step.HasPendingWork);
    /// <summary>Core copies, verifies and switches the library: the storage area shows a spinner with "Moving the library…".</summary>
    public bool IsMovingLibrary => store.IsMovingLibrary;
    public IAsyncRelayCommand ChangeLocationCommand { get; }
    public IRelayCommand ShowLibraryCommand { get; }
    public string? StorageStatus { get => storageStatus; private set => SetProperty(ref storageStatus, value); }

    public bool KeepAudio
    {
        get => Draft.KeepAudioAfterProcessing;
        set { if (value) { Draft.KeepAudioAfterProcessing = true; OnPropertyChanged(); OnPropertyChanged(nameof(DeleteAutomatically)); } }
    }

    public bool DeleteAutomatically
    {
        get => !Draft.KeepAudioAfterProcessing;
        set { if (value) { Draft.KeepAudioAfterProcessing = false; OnPropertyChanged(); OnPropertyChanged(nameof(KeepAudio)); } }
    }

    public string DeleteDetail => Draft.AcousticSpeakerRecognition ? Strings.DeleteAfterMatching : Strings.DeleteAfterTranscript;
    public ObservableCollection<PendingDeletionViewModel> PendingDeletions { get; } = [];
    /// <summary>Meetings whose transcript copy failed (Core <see cref="MeetingLibraryService.TranscriptMirrorErrors"/>), with "Try again".</summary>
    public ObservableCollection<PendingDeletionViewModel> MirrorErrors { get; } = [];

    public bool MirrorEnabled
    {
        get => Draft.TranscriptMirrorEnabled;
        set { Draft.TranscriptMirrorEnabled = value && Draft.TranscriptMirrorFolder != null; OnPropertyChanged(); }
    }

    public string? MirrorPath => Draft.TranscriptMirrorFolder?.Path is { Length: > 0 } path ? path : null;
    public bool HasMirrorFolder => MirrorPath != null;
    public string ChooseMirrorText => HasMirrorFolder ? Strings.Change : Strings.ChooseFolder;
    public IRelayCommand ChooseMirrorFolderCommand { get; }
    public IRelayCommand ShowMirrorFolderCommand { get; }

    // Live controls while recording

    public bool ShowLiveControls => store.IsRecording && !store.IsStopping;
    public bool ShowLiveDeepgram => ShowLiveControls && store.Settings.TranscriptionProvider == TranscriptionProvider.Deepgram;
    public string LiveDeepgramKey
    {
        get => liveDeepgramKey;
        set { if (SetProperty(ref liveDeepgramKey, value ?? "")) ReconnectCommand.NotifyCanExecuteChanged(); }
    }

    /// <summary>
    /// "Switch microphone" while recording (macOS <c>applyMicrophoneSelection</c>): the running engine switches first, then
    /// Core saves the choice. A failed switch keeps the previous microphone, and the picker shows it again.
    /// </summary>
    public Choice<string>? LiveMicrophone
    {
        get => Microphones.FirstOrDefault(choice => choice.Value == store.Settings.MicrophoneUID);
        set
        {
            if (value == null || value.Value == store.Settings.MicrophoneUID) return;
            if (SwitchMicrophoneCommand.CanExecute(value.Value)) _ = SwitchMicrophoneCommand.ExecuteAsync(value.Value);
            else OnPropertyChanged();
        }
    }

    public string? LiveMicrophoneError { get => liveMicrophoneError; private set => SetProperty(ref liveMicrophoneError, value); }

    public IRelayCommand ReconnectCommand { get; }
    public IAsyncRelayCommand<string> SwitchMicrophoneCommand { get; }
    public IRelayCommand DoneCommand { get; }
    public IRelayCommand OpenSetupCommand { get; }

    private async Task SwitchMicrophoneAsync(string? microphoneUID)
    {
        if (microphoneUID == null) return;
        LiveMicrophoneError = null;
        if (await store.SwitchMicrophoneAsync(microphoneUID))
        {
            // Saved by Core; the draft follows, so a later "Done" keeps the switched microphone.
            Draft.MicrophoneUID = microphoneUID;
            OnPropertyChanged(nameof(SelectedMicrophone));
        }
        else LiveMicrophoneError = store.Error.Trim().Length > 0 ? store.Error.Trim() : Strings.SettingsSaveFailed;
        OnPropertyChanged(nameof(LiveMicrophone));
    }

    private void Reconnect()
    {
        try { store.ReconnectTranscription(liveDeepgramKey); deepgramKey = store.DeepgramKey; OnPropertyChanged(nameof(DeepgramKey)); }
        catch (Exception) { SaveError = Strings.SettingsSaveFailed; }
    }

    /// <summary>Saves draft and keys; while busy there is nothing to save (the live controls apply at once).</summary>
    public bool Save()
    {
        if (store.Busy) return true;
        try
        {
            var updated = WallflyJson.Copy(Draft);
            Enhancement.Apply(updated);
            updated.TranscriptionModel = ModelDefaults.SupportedModel(updated.TranscriptionProvider);
            store.SaveSettings(updated, openAiKey, deepgramKey, Enhancement.AnthropicKey);
            SaveError = null;
            return true;
        }
        catch (InvalidOperationException error) { SaveError = error.Message; }
        catch (Exception) { SaveError = Strings.SettingsSaveFailed; }
        return false;
    }

    /// <summary>The window re-reads the busy state when the store changes.</summary>
    public void StoreChanged()
    {
        OnPropertyChanged(nameof(CanEdit)); OnPropertyChanged(nameof(BusyNote)); OnPropertyChanged(nameof(ShowLiveControls)); OnPropertyChanged(nameof(ShowLiveDeepgram));
        OnPropertyChanged(nameof(IsMovingLibrary)); OnPropertyChanged(nameof(CanMoveLibrary)); OnPropertyChanged(nameof(LibraryPath));
        OnPropertyChanged(nameof(MeetingAudioDetail)); OnPropertyChanged(nameof(LiveMicrophone));
        ReconnectCommand.NotifyCanExecuteChanged(); ChangeLocationCommand.NotifyCanExecuteChanged(); OpenSetupCommand.NotifyCanExecuteChanged();
        ChooseMirrorFolderCommand.NotifyCanExecuteChanged(); SwitchMicrophoneCommand.NotifyCanExecuteChanged();
        RefreshPendingDeletions();
    }

    /// <summary>Store changes without an Updated event: the transcript copy failures and the source of the running recording.</summary>
    private void StorePropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MeetingLibraryService.TranscriptMirrorErrors)) RefreshPendingDeletions();
        else if (args.PropertyName is nameof(MeetingLibraryService.RecordingAudioDescription)) OnPropertyChanged(nameof(MeetingAudioDetail));
        else if (args.PropertyName is nameof(MeetingLibraryService.IsMovingLibrary)) { OnPropertyChanged(nameof(IsMovingLibrary)); ChangeLocationCommand.NotifyCanExecuteChanged(); }
    }

    private void RefreshDevices()
    {
        Microphones.Clear(); Outputs.Clear();
        Microphones.Add(new("", Strings.SystemDefault));
        Outputs.Add(new("", Strings.SystemDefault));
        DeviceIssue = null;
        try
        {
            foreach (var device in system.Devices(AudioFlow.Capture).Where(device => device.Id.Length > 0)) Microphones.Add(new(device.Id, device.Name));
            foreach (var device in system.Devices(AudioFlow.Render).Where(device => device.Id.Length > 0)) Outputs.Add(new(device.Id, device.Name));
        }
        catch (Exception) { DeviceIssue = Strings.DevicesUnreadable; }
        if (Draft.MicrophoneUID.Length > 0 && Microphones.All(choice => choice.Value != Draft.MicrophoneUID)) Microphones.Add(new(Draft.MicrophoneUID, Strings.SavedMicrophoneUnavailable));
        if (Draft.OutputDeviceID.Length > 0 && Outputs.All(choice => choice.Value != Draft.OutputDeviceID)) Outputs.Add(new(Draft.OutputDeviceID, Strings.SavedOutputUnavailable));
        OnPropertyChanged(nameof(SelectedMicrophone)); OnPropertyChanged(nameof(SelectedOutput));
    }

    private void RefreshApps()
    {
        MeetingAudioOptions.Clear();
        MeetingAudioOptions.Add(new(AutomaticAudio, Strings.MeetingAudioAutomatic));
        MeetingAudioOptions.Add(new("", Strings.MeetingAudioWholeSystem));
        AppsIssue = null;
        try
        {
            var apps = system.AudioSessions().Where(session => !session.IsSystemSounds && session.ProcessName.Length > 0)
                .GroupBy(session => session.ProcessName, StringComparer.OrdinalIgnoreCase)
                .Select(group => new Choice<string>(group.Key, AppLabel(group.First())))
                .OrderBy(choice => choice.Label, StringComparer.CurrentCultureIgnoreCase);
            foreach (var app in apps) MeetingAudioOptions.Add(app);
        }
        catch (Exception) { AppsIssue = Strings.AppsUnreadable; }
        if (!Draft.AutomaticMeetingAudioSource && Draft.AudioSourceBundleID.Length > 0 && MeetingAudioOptions.All(choice => choice.Value != Draft.AudioSourceBundleID))
            MeetingAudioOptions.Add(new(Draft.AudioSourceBundleID, Strings.SavedAppUnavailable));
        OnPropertyChanged(nameof(SelectedMeetingAudio));
    }

    private static string AppLabel(AudioSessionEntry session) =>
        session.DisplayName.Length > 0 && !session.DisplayName.StartsWith('@') ? session.DisplayName : Path.GetFileNameWithoutExtension(session.ProcessName);

    /// <summary>
    /// "Try again" under Storage (macOS <c>AudioRetentionSection</c>): meetings whose audio deletion failed
    /// (<see cref="Meeting.AudioDeletionError"/>) and meetings whose transcript copy failed. Rebuilt on every store change.
    /// </summary>
    private void RefreshPendingDeletions()
    {
        Rebuild(PendingDeletions, store.Meetings.Where(meeting => meeting.AudioDeletionError != null)
            .Select(meeting => new PendingDeletionViewModel(meeting.Id, Strings.Format(Strings.PendingDeletion, meeting.Title), meeting.AudioDeletionError!,
                new RelayCommand(() => RetryAudioCleanup(meeting.Id), () => !store.IsMovingLibrary))));
        var errors = store.TranscriptMirrorErrors;
        Rebuild(MirrorErrors, store.Meetings.Where(meeting => errors.ContainsKey(meeting.Id))
            .Select(meeting => new PendingDeletionViewModel(meeting.Id, Strings.Format(Strings.MirrorFailedItem, meeting.Title), errors[meeting.Id],
                new RelayCommand(() => RetryTranscriptMirror(meeting.Id), () => !store.IsMovingLibrary && store.Settings.TranscriptMirrorEnabled))));
    }

    /// <summary>Replaces the rows only when they changed, so an open list does not flicker on every store update.</summary>
    private static void Rebuild(ObservableCollection<PendingDeletionViewModel> rows, IEnumerable<PendingDeletionViewModel> next)
    {
        var items = next.ToList();
        if (items.Select(item => (item.MeetingID, item.Text, item.Detail)).SequenceEqual(rows.Select(row => (row.MeetingID, row.Text, row.Detail)))) return;
        rows.Clear();
        foreach (var item in items) rows.Add(item);
    }

    private void RetryAudioCleanup(Guid meetingID)
    {
        store.RetryAudioCleanup(meetingID);
        RefreshPendingDeletions();
    }

    private void RetryTranscriptMirror(Guid meetingID)
    {
        store.RetryTranscriptMirror(meetingID);
        RefreshPendingDeletions();
    }

    /// <summary>
    /// "Change…" (macOS <c>chooseLibraryLocation</c>): Core moves the library into <c>&lt;folder&gt;\Wallfly</c> (copy,
    /// verify, rebase audio paths, switch the pointer, remove the originals). The old library stays active on any failure.
    /// </summary>
    private async Task ChangeLocationAsync()
    {
        if (!CanMoveLibrary || dialogs.AskFolder(Strings.ChooseLibraryFolder, null) is not { } target) return;
        var destination = Path.Combine(target, MeetingLibraryService.MeetingLibraryFolderName);
        if (!dialogs.Confirm(Strings.MoveLibraryTitle, Strings.Format(Strings.MoveLibraryQuestion, destination), Strings.MoveLibraryConfirm)) return;
        StorageStatus = Strings.MovingLibrary;
        var moved = await store.MoveLibraryAsync(target);
        StorageStatus = moved ? Strings.Format(Strings.MoveLibraryDone, store.Library.Root)
            : store.Error.Trim() is { Length: > 0 } error ? error : Strings.Format(Strings.MoveLibraryFailed, destination);
        StoreChanged();
    }

    private void ChooseMirrorFolder()
    {
        if (dialogs.AskFolder(Strings.ChooseMirrorFolder, MirrorPath) is not { } folder) return;
        Draft.TranscriptMirrorFolder = new TranscriptMirrorInfo { Path = folder };
        OnPropertyChanged(nameof(MirrorPath)); OnPropertyChanged(nameof(HasMirrorFolder)); OnPropertyChanged(nameof(ChooseMirrorText));
    }

    private void CopyDiagnostics()
    {
        var text = new StringBuilder();
        text.AppendLine("Wallfly audio diagnostics · " + DateTimeOffset.Now.ToString("u", System.Globalization.CultureInfo.InvariantCulture));
        text.AppendLine("Microphone access: " + system.MicrophoneAccess());
        try
        {
            foreach (var device in system.Devices(AudioFlow.Capture)) text.AppendLine($"Microphone: {device.Name}{(device.IsDefaultConsole ? " (default)" : "")}{(device.IsDefaultCommunications ? " (communications)" : "")}");
            foreach (var device in system.Devices(AudioFlow.Render)) text.AppendLine($"Output: {device.Name}{(device.IsDefaultConsole ? " (default)" : "")}");
            foreach (var session in system.AudioSessions()) text.AppendLine($"Session: {session.ProcessName} pid {session.ProcessId} {session.State} peak {session.Peak:0.000}");
        }
        catch (Exception error) { text.AppendLine("Audio devices unreadable: " + error.Message); }
        text.AppendLine($"Transcription: {Draft.TranscriptionProvider} · {Draft.Locale} · meeting audio {(Draft.AutomaticMeetingAudioSource ? "automatic" : Draft.AudioSourceBundleID.Length == 0 ? "whole system" : Draft.AudioSourceBundleID)}");
        text.AppendLine("Status: " + store.Status + (store.TranscriptionState.Length > 0 ? " · " + store.TranscriptionState : ""));
        dialogs.CopyText(text.ToString());
        DiagnosticsStatus = Strings.DiagnosticsCopied;
    }
}
