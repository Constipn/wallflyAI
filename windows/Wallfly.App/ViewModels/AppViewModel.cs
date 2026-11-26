using System.ComponentModel;
using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.App.Controls;
using Wallfly.Core;
using Wallfly.Core.Export;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>What the detail column shows (macOS <c>workspacePresentation</c>).</summary>
public enum WorkspacePresentation { Overview, Preview, Editor }

public enum DetailMode { Empty, Preview, Editor, Todos }

/// <summary>
/// The root view model of the main window: navigation (overview, preview, focus-mode editor), the library, the to-dos,
/// the recording bar, the error banner and every command with a shortcut. It owns no data: the
/// <see cref="MeetingLibraryService"/> stays the single source of truth and this class translates its state for the views.
/// </summary>
public sealed class AppViewModel : ObservableObject, IDisposable
{
    public static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(1);
    public const double CompactWidth = 820;
    public const double NarrowWidth = 640;

    private readonly IDisposable ticker;
    private Guid? selectedID;
    private WorkspacePresentation presentation = WorkspacePresentation.Overview;
    private bool tasksSelected;
    private bool focusMode;
    private bool settingUp;
    private bool quitting;
    private double layoutWidth = 1340;
    private MeetingViewModel? current;

    public AppViewModel(MeetingLibraryService store, IDialogService dialogs, IUiScheduler scheduler, UiStateStore uiState, ISystemProbe system, ILibraryLocation location)
    {
        Store = store;
        Dialogs = dialogs;
        Scheduler = scheduler;
        UiState = uiState;
        System = system;
        Location = location;
        Recording = new RecordingViewModel(this);
        Library = new LibraryViewModel(this, scheduler);
        Preview = new MeetingPreviewViewModel(this);
        Todos = new TodosViewModel(this);

        NewNoteCommand = new AsyncRelayCommand(NewNoteAsync, () => !store.Busy && !settingUp);
        ShowMeetingsCommand = new RelayCommand(() => { TasksSelected = false; ShowOverview(); });
        ShowTodosCommand = new RelayCommand(() => { TasksSelected = true; ShowOverview(); });
        SelectScopeCommand = new RelayCommand<FolderRowViewModel>(row => { if (row != null) SelectScope(row.Scope); });
        OpenActiveMeetingCommand = new RelayCommand(OpenActiveMeeting);
        OpenSettingsCommand = new RelayCommand<string>(name => OpenSettings(Enum.TryParse<SettingsArea>(name, out var area) ? area : SettingsArea.Recording));
        QuitCommand = new RelayCommand(() => QuitRequested?.Invoke());
        FocusSearchCommand = new RelayCommand(() => { FocusMode = false; TasksSelected = false; SearchFocusRequested?.Invoke(); });
        EscapeCommand = new RelayCommand(Escape);
        StartRecordingCommand = new AsyncRelayCommand(StartRecordingAsync, () => current != null && CanStartRecording && !Recording.ShowGoToRecording);
        StopRecordingCommand = new AsyncRelayCommand(StopRecordingAsync, () => current != null && store.IsRecording && !store.IsStopping && !Recording.ShowGoToRecording);
        EnhanceCommand = new AsyncRelayCommand(() => store.EnhanceAsync(current!.Id),
            () => current != null && current.HasTranscriptText && !store.Busy && store.EnhancementConfigurationIssue == null);
        ToggleTranscriptCommand = new RelayCommand(() => current?.ToggleTranscriptBoxCommand.Execute(null));
        SelectTabCommand = new RelayCommand<string>(name => { if (Enum.TryParse<NoteTab>(name, out var tab)) current?.SelectTab(tab); });
        BackToPreviewCommand = new RelayCommand(() => { FocusMode = false; if (selectedID is Guid id) PreviewMeeting(id); });
        ToggleFocusModeCommand = new RelayCommand(() => FocusMode = !FocusMode);
        BackToListCommand = new RelayCommand(() => { SetSelection(null, WorkspacePresentation.Overview); });
        ExportMarkdownCommand = new RelayCommand(ExportMarkdown, () => SelectedMeeting != null);
        ShowSavedFilesCommand = new RelayCommand(() => { if (SelectedMeeting is { } meeting) ShowSavedFiles(meeting.Id); });
        RenameSelectedCommand = new RelayCommand(() => { if (selectedID is Guid id) RenameMeeting(id); }, () => !store.Busy && SelectedMeeting != null);
        DeleteSelectedCommand = new RelayCommand(() => { if (selectedID is Guid id) DeleteMeeting(id); }, () => !store.Busy && SelectedMeeting != null);
        MoveSelectedCommand = new RelayCommand<FolderOption>(option => { if (option != null && selectedID is Guid id) MoveMeeting(id, option.FolderID); });
        CreateFolderCommand = new RelayCommand(() => CreateFolder(null), () => store.FoldersAvailable);
        EditFolderCommand = new RelayCommand<FolderRowViewModel>(row => { if (row?.FolderID is Guid id) EditFolder(id); }, row => !store.Busy);
        DeleteFolderCommand = new RelayCommand<FolderRowViewModel>(row => { if (row?.FolderID is Guid id) DeleteFolder(id); }, row => !store.Busy);
        DismissErrorCommand = new RelayCommand(() => store.Error = "");
        ErrorActionCommand = new AsyncRelayCommand(ErrorActionAsync);
        RetryTaskDetectionCommand = new AsyncRelayCommand<Guid?>(id => id is Guid value ? store.EnhanceAsync(value, tasksOnly: true) : Task.CompletedTask);

        store.Updated += OnStoreUpdated;
        store.PropertyChanged += OnStorePropertyChanged;
        store.EnhancementCompleted += OnEnhancementCompleted;
        store.TaskReviewRequested += OnTaskReviewRequested;
        Library.SearchCompleted += OnSearchCompleted;
        ticker = scheduler.Every(TickInterval, Tick);
        Refresh();
    }

    public MeetingLibraryService Store { get; }
    public IDialogService Dialogs { get; }
    public IUiScheduler Scheduler { get; }
    public UiStateStore UiState { get; }
    public ISystemProbe System { get; }
    public ILibraryLocation Location { get; }
    public LibraryViewModel Library { get; }
    public RecordingViewModel Recording { get; }
    public MeetingPreviewViewModel Preview { get; }
    public TodosViewModel Todos { get; }
    /// <summary>Wallfly Detector, set by the shell; null in tests that do not need it (the settings then hide the helper rows).</summary>
    public ITeamsDetection? Detection { get; set; }

    /// <summary>The editor of the selected meeting while the editor is shown; null otherwise.</summary>
    public MeetingViewModel? Current { get => current; private set => SetProperty(ref current, value); }

    /// <summary>The shell brings the window to the front (capsule, tray, "Go to recording").</summary>
    public event Action? ShowWindowRequested;
    /// <summary>Ctrl+F: the view focuses the search field.</summary>
    public event Action? SearchFocusRequested;
    /// <summary>Ctrl+Q and "Quit Wallfly": the shell runs <see cref="RequestQuitAsync"/> and shuts down on success.</summary>
    public event Action? QuitRequested;

    // Navigation state

    public Guid? SelectedID => selectedID;
    public Meeting? SelectedMeeting => selectedID is Guid id ? Store.Meetings.FirstOrDefault(meeting => meeting.Id == id) : null;
    public Meeting? ActiveMeeting => Store.ActiveID is Guid id ? Store.Meetings.FirstOrDefault(meeting => meeting.Id == id) : null;
    public WorkspacePresentation Presentation => presentation;
    public bool IsEditor => presentation == WorkspacePresentation.Editor && current != null;

    public bool TasksSelected
    {
        get => tasksSelected;
        set { if (SetProperty(ref tasksSelected, value)) NotifyLayout(); }
    }

    /// <summary>Focus mode hides sidebar and list; the editor opens in it (spec 9.1).</summary>
    public bool FocusMode
    {
        get => focusMode;
        set { if (SetProperty(ref focusMode, value)) { NotifyLayout(); Recording.Refresh(); } }
    }

    /// <summary>True while the onboarding is open: no recording can start.</summary>
    public bool IsSettingUp
    {
        get => settingUp;
        set { if (SetProperty(ref settingUp, value)) { NewNoteCommand.NotifyCanExecuteChanged(); Recording.Refresh(); } }
    }

    public bool ShowingTodos => tasksSelected && presentation == WorkspacePresentation.Overview;

    /// <summary>Sidebar navigation "Meetings" (two-way for its radio button).</summary>
    public bool MeetingsNavigationChecked { get => !tasksSelected; set { if (value) ShowMeetingsCommand.Execute(null); } }
    /// <summary>Sidebar navigation "My to-dos".</summary>
    public bool TodosNavigationChecked { get => tasksSelected; set { if (value) ShowTodosCommand.Execute(null); } }

    public DetailMode DetailMode =>
        IsEditor ? DetailMode.Editor
        : ShowingTodos ? DetailMode.Todos
        : SelectedMeeting is { } meeting && (!Library.HasQuery || Library.ResultFor(meeting.Id) != null) ? DetailMode.Preview
        : DetailMode.Empty;

    /// <summary>The view reports its width; narrow windows hide the sidebar, very narrow ones show list or detail.</summary>
    public double LayoutWidth
    {
        get => layoutWidth;
        set { if (Math.Abs(layoutWidth - value) >= 1 && SetProperty(ref layoutWidth, value)) NotifyLayout(); }
    }

    public bool IsNarrow => layoutWidth < NarrowWidth;
    public bool SidebarVisible => !focusMode && layoutWidth >= CompactWidth;
    public bool ListVisible => !focusMode && !ShowingTodos && (!IsNarrow || DetailMode == DetailMode.Empty);
    public bool DetailVisible => !(IsNarrow && ListVisible);
    public bool ShowNarrowBack => IsNarrow && !focusMode && !IsEditor && DetailMode is DetailMode.Preview;
    public string ToolbarCaption => ShowingTodos ? Strings.TodosCaption : Strings.PreviewCaption;
    public string EmptyDetailText => Library.HasQuery ? Strings.EmptySearchDetail : Strings.EmptyDetail;
    public bool ShowPresentationToggle => Store.IsStarting || Store.IsRecording || Store.IsStopping;
    public bool PresentationMode { get => Store.PresentationMode; set { Store.PresentationMode = value; OnPropertyChanged(); } }
    public bool ShowMoreMenu => IsEditor && SelectedMeeting != null;
    public bool HasActiveMeeting => ActiveMeeting != null;
    public IconKind FocusIcon => focusMode ? IconKind.ShowLibrary : IconKind.FocusMode;
    public string FocusHint => focusMode ? Strings.ShowLibrary : Strings.FocusMode;
    public IReadOnlyList<FolderOption> SelectedFolderOptions => selectedID is Guid id ? FolderOptions(id) : [];

    // Error banner: text plus exactly one action

    public string ErrorText => Store.Error.Trim();
    public bool HasError => ErrorText.Length > 0;
    public string? ErrorActionText => Store.Recovery is not { } recovery ? null
        : recovery.Action == EnhancementRecoveryAction.OpenSettings ? Strings.ErrorOpenSettings : Strings.ErrorTryAgain;

    // Commands

    public IAsyncRelayCommand NewNoteCommand { get; }
    public IRelayCommand ShowMeetingsCommand { get; }
    public IRelayCommand ShowTodosCommand { get; }
    public IRelayCommand<FolderRowViewModel> SelectScopeCommand { get; }
    public IRelayCommand OpenActiveMeetingCommand { get; }
    public IRelayCommand<string> OpenSettingsCommand { get; }
    public IRelayCommand QuitCommand { get; }
    public IRelayCommand FocusSearchCommand { get; }
    public IRelayCommand EscapeCommand { get; }
    public IAsyncRelayCommand StartRecordingCommand { get; }
    public IAsyncRelayCommand StopRecordingCommand { get; }
    public IAsyncRelayCommand EnhanceCommand { get; }
    public IRelayCommand ToggleTranscriptCommand { get; }
    public IRelayCommand<string> SelectTabCommand { get; }
    public IRelayCommand BackToPreviewCommand { get; }
    public IRelayCommand ToggleFocusModeCommand { get; }
    public IRelayCommand BackToListCommand { get; }
    public IRelayCommand ExportMarkdownCommand { get; }
    public IRelayCommand ShowSavedFilesCommand { get; }
    public IRelayCommand RenameSelectedCommand { get; }
    public IRelayCommand DeleteSelectedCommand { get; }
    public IRelayCommand<FolderOption> MoveSelectedCommand { get; }
    public IRelayCommand CreateFolderCommand { get; }
    public IRelayCommand<FolderRowViewModel> EditFolderCommand { get; }
    public IRelayCommand<FolderRowViewModel> DeleteFolderCommand { get; }
    public IRelayCommand DismissErrorCommand { get; }
    public IAsyncRelayCommand ErrorActionCommand { get; }
    public IAsyncRelayCommand<Guid?> RetryTaskDetectionCommand { get; }

    public bool IsLive(Guid id) => Store.ActiveID == id && (Store.IsRecording || Store.IsStarting || Store.IsStopping);
    public bool CanStartRecording => !Store.Busy && !settingUp && SelectedMeeting != null;

    // Navigation (ports of previewMeeting, selectMeeting, showOverview, selectLibrary)

    /// <summary>Single click: the read-only preview; sidebar and list stay.</summary>
    public void PreviewMeeting(Guid id)
    {
        if (Store.Meetings.All(meeting => meeting.Id != id)) return;
        Store.Flush();
        SetSelection(id, WorkspacePresentation.Preview);
    }

    /// <summary>Double click or Enter: the editor in focus mode.</summary>
    public void OpenEditor(Guid id)
    {
        if (Store.Meetings.All(meeting => meeting.Id != id)) return;
        Store.Flush();
        tasksSelected = false;
        focusMode = true;
        SetSelection(id, WorkspacePresentation.Editor, recreateEditor: true);
    }

    public void ShowOverview()
    {
        Store.Flush();
        SetSelection(selectedID, WorkspacePresentation.Overview);
    }

    public void SelectScope(LibraryScope scope)
    {
        if (scope.Kind == ScopeKind.Folder && Store.Catalog.Folders.All(folder => folder.Id != scope.FolderID)) return;
        Store.Flush();
        Library.SetScope(scope);
        Todos.ScopeChanged();
        SetSelection(selectedID, WorkspacePresentation.Overview);
        SelectVisibleMeetingIfNeeded();
    }

    /// <summary>Keeps the selection inside the scope (macOS <c>selectVisibleMeetingIfNeeded</c>).</summary>
    private void SelectVisibleMeetingIfNeeded()
    {
        var visible = Library.VisibleMeetings;
        if (visible.Any(meeting => meeting.Id == selectedID)) { Refresh(); return; }
        var next = visible.FirstOrDefault()?.Id;
        // An editor never silently shows another meeting: the new selection opens as a preview.
        var target = next == null ? WorkspacePresentation.Overview : presentation == WorkspacePresentation.Editor ? WorkspacePresentation.Preview : presentation;
        SetSelection(next, target);
    }

    public void OpenActiveMeeting()
    {
        ShowWindowRequested?.Invoke();
        if ((Store.ActiveID ?? Store.ProcessingID) is Guid id) OpenEditor(id);
    }

    public void OpenSettings(SettingsArea area) => Dialogs.ShowSettings(area);

    internal void OnSearchEdited() => TasksSelected = false;

    private void SetSelection(Guid? id, WorkspacePresentation target, bool recreateEditor = false)
    {
        selectedID = id;
        presentation = id == null && target != WorkspacePresentation.Overview ? WorkspacePresentation.Overview : target;
        // Focus mode belongs to the editor: leaving it brings sidebar and list back.
        if (presentation != WorkspacePresentation.Editor) focusMode = false;
        var wantEditor = presentation == WorkspacePresentation.Editor && SelectedMeeting is { } meeting ? meeting : null;
        if (wantEditor == null || recreateEditor || current?.Id != wantEditor.Id)
        {
            current?.Dispose();
            Current = wantEditor == null ? null : new MeetingViewModel(this, wantEditor);
        }
        Refresh();
    }

    private void Escape()
    {
        if (current?.TranscriptBoxOpen == true) { current.TranscriptBoxOpen = false; return; }
        if (current?.Chat.IsExpanded == true) { current.Chat.IsExpanded = false; return; }
        if (DetailMode == DetailMode.Preview && Preview.Chat?.IsExpanded == true) { Preview.Chat.IsExpanded = false; return; }
        if (IsEditor) BackToPreviewCommand.Execute(null);
    }

    // Meeting actions

    private async Task NewNoteAsync()
    {
        if (Store.Busy || settingUp) return;
        TasksSelected = false;
        Meeting meeting;
        try { meeting = Store.CreateMeeting(Library.Scope.Kind == ScopeKind.Folder ? Library.Scope.FolderID : null); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { Store.Error = error.Message; return; }
        OpenEditor(meeting.Id);
        await StartRecordingAsync();
    }

    /// <summary>"Start recording": the editor returns to "My notes", as on macOS.</summary>
    public async Task StartRecordingAsync()
    {
        if (current is not { } editor || !CanStartRecording) return;
        editor.SelectTab(NoteTab.Notes);
        await Store.StartRecordingAsync(editor.Id);
    }

    public Task StopRecordingAsync() => Store.StopRecordingAsync();

    /// <summary>
    /// "Take notes" from Wallfly Detector (port of macOS <c>openDetectedTeamsMeeting</c>). Runs on the UI thread and returns
    /// at once, so the helper's pipe answer is not held up by the recording start. While AI processing, the setup or the
    /// quit flow runs nothing happens and <paramref name="consume"/> is not called, so the helper can offer the same
    /// request again. Otherwise the request is consumed first (a pipe message alone never starts a recording): a running
    /// recording is shown, else a new note opens and records, exactly like "New note".
    /// </summary>
    public DetectedMeetingResult OpenDetectedTeamsMeeting(Func<bool> consume)
    {
        var recordingSession = Store.IsRecording || Store.IsStarting || Store.IsStopping;
        if (!recordingSession && (Store.Busy || settingUp || quitting)) return new(DetectedMeetingOutcome.Busy, Task.CompletedTask);
        if (!consume()) return new(DetectedMeetingOutcome.Rejected, Task.CompletedTask);
        ShowWindowRequested?.Invoke();
        if (Store.IsRecording || Store.IsStarting || Store.IsStopping)
        {
            OpenActiveMeeting();
            return new(DetectedMeetingOutcome.ShowedRunning, Task.CompletedTask);
        }
        return new(DetectedMeetingOutcome.NoteOpened, NewNoteAsync());
    }

    public IReadOnlyList<FolderOption> FolderOptions(Guid meetingID)
    {
        var catalog = Store.Catalog;
        var assigned = catalog.FolderID(meetingID);
        var options = new List<FolderOption> { new(null, Strings.NoFolder, IconKind.Tray, Palette.Ink, assigned == null) };
        options.AddRange(catalog.Folders.Select(folder => new FolderOption(folder.Id, folder.Name, FolderStyles.Icon(folder.Icon).Kind,
            FolderStyles.Colors(folder).Foreground, assigned == folder.Id)));
        return options;
    }

    /// <summary>Folder assignment works during a recording too; the overview keeps its selection inside the scope.</summary>
    public void MoveMeeting(Guid meetingID, Guid? folderID)
    {
        if (!Store.FoldersAvailable) return;
        Store.Flush();
        Store.MoveMeeting(meetingID, folderID);
        if (presentation != WorkspacePresentation.Editor) SelectVisibleMeetingIfNeeded();
    }

    public void RenameMeeting(Guid id)
    {
        if (Store.Busy || Store.Meetings.FirstOrDefault(meeting => meeting.Id == id) is not { } meeting) return;
        var title = Dialogs.AskMeetingName(meeting.Title)?.Trim();
        if (string.IsNullOrEmpty(title) || Store.Busy) return;
        Store.Flush();
        meeting.Title = title;
        Store.Flush();
    }

    public void DeleteMeeting(Guid id)
    {
        if (Store.Busy || Store.Meetings.All(meeting => meeting.Id != id)) return;
        if (!Dialogs.Confirm(Strings.DeleteMeetingQuestion, Strings.DeleteMeetingDetail, Strings.DeleteMeetingConfirm)) return;
        Store.DeleteMeeting(id);
        if (Store.Meetings.All(meeting => meeting.Id != id) && selectedID == id) SetSelection(null, WorkspacePresentation.Overview);
    }

    private void ExportMarkdown()
    {
        if (SelectedMeeting is not { } meeting) return;
        Store.Flush();
        if (Dialogs.AskSaveMarkdown(MarkdownExporter.FileName(meeting)) is not { } path) return;
        try { File.WriteAllText(path, MarkdownExporter.Export(meeting)); }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { Store.Error = Strings.ExportFailed; }
    }

    /// <summary>
    /// "Re-detect speakers" in the "…" menu (macOS speaker menu): offered with "Match voices after the meeting" on and
    /// remote speech in the meeting, or an existing audio archive of a channel with timed words (the microphone pass can
    /// separate voices on the Me channel too); enabled when Core can queue the speaker matching now.
    /// </summary>
    public bool ShowRedetectSpeakers(Guid id) =>
        Store.Settings.AcousticSpeakerRecognition && Store.Meetings.FirstOrDefault(meeting => meeting.Id == id) is { } meeting
        && (meeting.Transcript.Any(segment => segment.Speaker == Speaker.Others)
            || new[] { Speaker.Me, Speaker.Others }.Any(channel => Wallfly.Speakers.AcousticSpeakerLabels.HasTimedWords(meeting, channel)
                && Wallfly.Speakers.DiarizationTimeline.Files(meeting, channel).Any(path => File.Exists(Store.Library.ResolveAudioPath(path)))));

    public bool CanRedetectSpeakers(Guid id) => Store.CanRedetectSpeakers(id);

    /// <summary>
    /// "Re-detect speakers on this device": offered next to "Re-detect speakers" when the speaker separation setting chooses
    /// the cloud, so the local model runs only on this explicit choice.
    /// </summary>
    public bool ShowRedetectSpeakersOnDevice(Guid id) =>
        Store.Settings.EffectiveSpeakerSeparation == SpeakerSeparationEngine.Deepgram && ShowRedetectSpeakers(id);

    /// <summary>Queues the speaker matching on this computer; the transcript shows its status line until it has finished.</summary>
    public bool RedetectSpeakersOnDevice(Guid id)
    {
        if (!CanRedetectSpeakers(id) || !Store.RedetectSpeakersOnDevice(id)) return false;
        Refresh();
        return true;
    }

    /// <summary>Queues the speaker matching; the transcript shows its status line until it has finished.</summary>
    public bool RedetectSpeakers(Guid id)
    {
        if (!CanRedetectSpeakers(id) || !Store.RedetectSpeakers(id)) return false;
        Refresh();
        return true;
    }

    /// <summary>"Show saved files": Explorer with the meeting file selected.</summary>
    public void ShowSavedFiles(Guid id)
    {
        Store.Library.Prepare();
        var path = Store.Library.MeetingPath(id);
        if (File.Exists(path)) Dialogs.Reveal(path); else Dialogs.OpenFolder(Store.Library.Root);
    }

    /// <summary>"Load example" of the introduction card (port of <c>importDemo</c>).</summary>
    public void LoadExample()
    {
        if (Store.Busy || settingUp) return;
        Meeting meeting;
        try { meeting = Store.CreateMeeting(Library.Scope.Kind == ScopeKind.Folder ? Library.Scope.FolderID : null); }
        catch (Exception error) when (error is InvalidOperationException or IOException) { Store.Error = error.Message; return; }
        meeting.Title = Strings.ExampleTitle;
        meeting.Duration = 184;
        meeting.Notes = string.Join("\n", Strings.ExampleNote1, Strings.ExampleNote2, Strings.ExampleNote3);
        var segments = new[] { (Speaker.Me, 12d, Strings.ExampleSegment1), (Speaker.Others, 38d, Strings.ExampleSegment2), (Speaker.Me, 74d, Strings.ExampleSegment3), (Speaker.Others, 106d, Strings.ExampleSegment4) };
        foreach (var (speaker, offset, text) in segments) meeting.Upsert(new TranscriptSegment { Speaker = speaker, Offset = offset, Text = text, IsFinal = true });
        Store.MarkDirty(meeting);
        Store.Flush();
        OpenEditor(meeting.Id);
    }

    // Folders

    /// <summary>"Create folder"; <paramref name="thenMove"/> assigns a meeting to it afterwards (from the folder picker).</summary>
    public void CreateFolder(Guid? thenMove)
    {
        if (!Store.FoldersAvailable) return;
        var editor = new FolderEditorViewModel(Store, null);
        if (Dialogs.EditFolder(editor) && thenMove is Guid meetingID) MoveMeeting(meetingID, editor.FolderID);
        Refresh();
    }

    public void EditFolder(Guid folderID)
    {
        if (Store.Catalog.Folders.FirstOrDefault(folder => folder.Id == folderID) is not { } folder) return;
        var editor = new FolderEditorViewModel(Store, folder);
        Dialogs.EditFolder(editor);
        Refresh();
    }

    /// <summary>Asks, then deletes; the meetings move to "No folder" and a deleted scope falls back to it.</summary>
    public bool DeleteFolder(Guid folderID)
    {
        if (Store.Busy || Store.Catalog.Folders.FirstOrDefault(folder => folder.Id == folderID) is not { } folder) return false;
        if (!Dialogs.Confirm(Strings.Format(Strings.DeleteFolderQuestion, folder.Name), Strings.DeleteFolderDetail, Strings.DeleteFolderConfirm)) return false;
        if (!FolderEditorViewModel.DeleteFolder(Store, folderID)) return false;
        if (Library.Scope.FolderID == folderID) Library.SetScope(LibraryScope.Unfiled);
        SelectVisibleMeetingIfNeeded();
        return true;
    }

    // Error banner

    private async Task ErrorActionAsync()
    {
        if (Store.Recovery is not { } recovery) return;
        if (recovery.Action == EnhancementRecoveryAction.OpenSettings) { OpenSettings(SettingsArea.AiLanguage); return; }
        if (Store.Busy) return;
        Store.Error = "";
        await Store.EnhanceAsync(recovery.MeetingID, recovery.TasksOnly);
    }

    // Quit and close (spec 9.6, MeetingAppDelegate.applicationShouldTerminate)

    /// <summary>Closing the window: hide to the tray while a recording session runs, otherwise quit.</summary>
    public CloseAction OnWindowClosing() => QuitPolicy.OnClose(Store.IsRecording || Store.IsStarting || Store.IsStopping);

    /// <summary>
    /// Quit: without running work it saves and returns true; with running work it asks "Save and end meeting?" and,
    /// on "Save and end", stops within three seconds. False keeps the app running ("Return to meeting" or a failed save).
    /// While the library moves, Core refuses to quit: the dialog only says "Library is moving…".
    /// </summary>
    public async Task<bool> RequestQuitAsync()
    {
        if (quitting) return false;
        if (Store.IsMovingLibrary)
        {
            ShowWindowRequested?.Invoke();
            Dialogs.AskSaveAndEnd(QuitPrompt.LibraryMoving);
            return false;
        }
        if (QuitPolicy.OnQuit(Store.Busy) == QuitAction.QuitNow)
        {
            if (Store.Flush()) return true;
            Dialogs.Inform(Strings.SaveFailedTitle, ErrorText.Length > 0 ? ErrorText : Strings.SaveFailedDetail);
            return false;
        }
        ShowWindowRequested?.Invoke();
        if (Dialogs.AskSaveAndEnd() == QuitChoice.ReturnToMeeting) { OpenActiveMeeting(); return false; }
        quitting = true;
        try
        {
            if (await Store.CloseAsync()) return true;
            if (ErrorText.Length == 0) Store.Error = Strings.QuitNotFinished;
            Dialogs.Inform(Strings.SaveFailedTitle, ErrorText);
            return false;
        }
        finally { quitting = false; }
    }

    // Store events

    private void OnStoreUpdated() => Refresh(contentChanged: true);

    private void OnStorePropertyChanged(object? sender, PropertyChangedEventArgs args)
    {
        switch (args.PropertyName)
        {
            case nameof(MeetingLibraryService.MeLevel) or nameof(MeetingLibraryService.OthersLevel):
                Recording.LevelsChanged();
                break;
            case nameof(MeetingLibraryService.Error):
                OnPropertyChanged(nameof(ErrorText)); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ErrorActionText));
                break;
            case nameof(MeetingLibraryService.Status):
                current?.RefreshProgress();
                if (ShowingTodos) Todos.Refresh();
                if (Recording.InfoOpen) Recording.RefreshInfo();
                break;
            case nameof(MeetingLibraryService.PresentationMode):
                OnPropertyChanged(nameof(PresentationMode));
                break;
            case nameof(MeetingLibraryService.TranscriptionState) or nameof(MeetingLibraryService.TranscriptionStates)
                or nameof(MeetingLibraryService.SpeakerMatchingProgress):
                if (Recording.InfoOpen) Recording.RefreshInfo();
                break;
            case nameof(MeetingLibraryService.RecordingAudioDescription) or nameof(MeetingLibraryService.MicrophoneDescription)
                or nameof(MeetingLibraryService.MeetingAudioWarning) or nameof(MeetingLibraryService.AudioDeviceNotice):
                Recording.Refresh();
                break;
            case nameof(MeetingLibraryService.TeamsSpeakerStatus):
                current?.Transcript.SpeakerStatusChanged();
                if (Recording.InfoOpen) Recording.RefreshInfo();
                break;
        }
    }

    private void OnEnhancementCompleted(Guid id)
    {
        if (current?.Id == id) current.EnhancementCompleted();
    }

    private void OnTaskReviewRequested(Guid id) => Scheduler.Schedule(TimeSpan.Zero, () => Dialogs.ShowTaskReview(id));

    private void OnSearchCompleted()
    {
        if (presentation != WorkspacePresentation.Editor && Library.Results.FirstOrDefault() is { } first && Library.Results.All(result => result.Id != selectedID))
            SetSelection(first.Id, WorkspacePresentation.Preview);
        else Refresh();
    }

    private void Tick()
    {
        Store.Tick();
        if (Store.ActiveID is Guid id)
        {
            Recording.Tick();
            if (current?.Id == id) current.RefreshDuration();
        }
    }

    /// <summary>Re-reads the store into every child view model; <paramref name="contentChanged"/> also re-runs an active search.</summary>
    public void Refresh(bool contentChanged = false)
    {
        if (selectedID is Guid id && Store.Meetings.All(meeting => meeting.Id != id))
        {
            selectedID = null;
            presentation = WorkspacePresentation.Overview;
            current?.Dispose();
            Current = null;
            // The focus layout hides the sidebar and the list; without the meeting there is nothing left to focus on.
            FocusMode = false;
        }
        if (contentChanged) Library.ContentChanged(); else Library.Refresh();
        if (DetailMode == DetailMode.Preview) Preview.Show(SelectedMeeting, Library.HasQuery ? Library.ActiveQuery : "", Library.ResultFor(selectedID ?? Guid.Empty));
        // Leaving the preview (editor, to-dos, no selection) cancels its chat's waiting question, like closing the editor.
        else Preview.ReleaseChat();
        current?.Refresh();
        // The to-dos are rebuilt only while they are shown; showing them runs this refresh.
        if (ShowingTodos) Todos.Refresh();
        Recording.Refresh();
        NotifyLayout();
        OnPropertyChanged(nameof(ShowPresentationToggle)); OnPropertyChanged(nameof(PresentationMode)); OnPropertyChanged(nameof(HasActiveMeeting));
        OnPropertyChanged(nameof(SelectedID)); OnPropertyChanged(nameof(SelectedMeeting)); OnPropertyChanged(nameof(Presentation));
        OnPropertyChanged(nameof(ErrorText)); OnPropertyChanged(nameof(HasError)); OnPropertyChanged(nameof(ErrorActionText));
        OnPropertyChanged(nameof(SelectedFolderOptions)); OnPropertyChanged(nameof(ShowMoreMenu));
        NewNoteCommand.NotifyCanExecuteChanged(); StartRecordingCommand.NotifyCanExecuteChanged(); StopRecordingCommand.NotifyCanExecuteChanged();
        EnhanceCommand.NotifyCanExecuteChanged(); RenameSelectedCommand.NotifyCanExecuteChanged(); DeleteSelectedCommand.NotifyCanExecuteChanged();
        CreateFolderCommand.NotifyCanExecuteChanged(); EditFolderCommand.NotifyCanExecuteChanged(); DeleteFolderCommand.NotifyCanExecuteChanged();
        ExportMarkdownCommand.NotifyCanExecuteChanged();
    }

    private void NotifyLayout()
    {
        OnPropertyChanged(nameof(IsEditor)); OnPropertyChanged(nameof(ShowingTodos)); OnPropertyChanged(nameof(DetailMode));
        OnPropertyChanged(nameof(IsNarrow)); OnPropertyChanged(nameof(SidebarVisible)); OnPropertyChanged(nameof(ListVisible));
        OnPropertyChanged(nameof(DetailVisible)); OnPropertyChanged(nameof(ShowNarrowBack)); OnPropertyChanged(nameof(ToolbarCaption)); OnPropertyChanged(nameof(EmptyDetailText));
        OnPropertyChanged(nameof(FocusIcon)); OnPropertyChanged(nameof(FocusHint)); OnPropertyChanged(nameof(TasksSelected)); OnPropertyChanged(nameof(FocusMode));
        OnPropertyChanged(nameof(MeetingsNavigationChecked)); OnPropertyChanged(nameof(TodosNavigationChecked));
    }

    public void Dispose()
    {
        ticker.Dispose();
        current?.Dispose();
        Preview.ReleaseChat();
        Store.Updated -= OnStoreUpdated;
        Store.PropertyChanged -= OnStorePropertyChanged;
        Store.EnhancementCompleted -= OnEnhancementCompleted;
        Store.TaskReviewRequested -= OnTaskReviewRequested;
    }
}
