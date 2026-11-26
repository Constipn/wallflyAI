using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.Core;

namespace Wallfly.App.ViewModels;

/// <summary>The three editor tabs (Ctrl+1/2/3).</summary>
public enum NoteTab { Notes, Enhanced, Transcript }

/// <summary>
/// The focus-mode editor of one meeting (spec 9.2): header, title and notes written straight into the meeting (the
/// service's writer schedules the save), the tabs, the enhance action with its three wordings, the live transcript box.
/// </summary>
public sealed class MeetingViewModel : ObservableObject, IDisposable
{
    private readonly AppViewModel app;
    private NoteTab selectedTab;
    private bool transcriptBoxOpen;
    private double headerHeight;
    private double areaWidth = 980;
    private double areaHeight = 850;
    private double? dragHeight;

    public MeetingViewModel(AppViewModel app, Meeting meeting)
    {
        this.app = app;
        Meeting = meeting;
        Folder = new FolderPickerViewModel(app, meeting.Id);
        Transcript = new TranscriptViewModel(app, meeting);
        Chat = new MeetingChatViewModel(app, meeting, () => TranscriptBoxOpen = false);
        selectedTab = OpeningTab(meeting, app.IsLive(meeting.Id));
        dragHeight = app.UiState.State.TranscriptHeight > 0 ? app.UiState.State.TranscriptHeight : null;
        EnhanceCommand = new AsyncRelayCommand(EnhanceAsync, () => !app.Store.Busy);
        EnhanceAgainCommand = new AsyncRelayCommand(() => app.Store.EnhanceAsync(Id), () => !app.Store.Busy && ConfigurationIssue == null);
        SetUpEnhancementCommand = new RelayCommand(() => app.OpenSettings(SettingsArea.AiLanguage));
        SelectTabCommand = new RelayCommand<string>(name => { if (Enum.TryParse<NoteTab>(name, out var tab)) SelectTab(tab); });
        ToggleTranscriptBoxCommand = new RelayCommand(ToggleTranscriptBox, () => CurrentTab != NoteTab.Transcript);
        CloseTranscriptBoxCommand = new RelayCommand(() => TranscriptBoxOpen = false);
        ResetTranscriptHeightCommand = new RelayCommand(() => SetPreferredTranscriptHeight(null));
        meeting.PropertyChanged += MeetingChanged;
    }

    public Meeting Meeting { get; }
    public Guid Id => Meeting.Id;
    public FolderPickerViewModel Folder { get; }
    public TranscriptViewModel Transcript { get; }
    /// <summary>The "Ask anything" chat below the editor, the same on every tab.</summary>
    public MeetingChatViewModel Chat { get; }

    /// <summary>Finished meetings with enhanced notes open on them; live meetings and everything else on the raw notes.</summary>
    public static NoteTab OpeningTab(Meeting? meeting, bool live) =>
        live || meeting == null || meeting.EnhancedNotes.Length == 0 ? NoteTab.Notes : NoteTab.Enhanced;

    // Header

    public string DateText => Formats.LongDate(Meeting.StartedAt ?? Meeting.CreatedAt);
    public bool HasDuration => Meeting.Duration > 0;
    public string DurationText => Meeting.Timestamp(Meeting.Duration);

    /// <summary>Write-through: <see cref="Meeting"/> raises PropertyChanged and the service marks it dirty for its writer.</summary>
    public string Title
    {
        get => Meeting.Title;
        set { if (!IsLocked && value != null && Meeting.Title != value) Meeting.Title = value; }
    }

    public string Notes
    {
        get => Meeting.Notes;
        set { if (!IsLocked && value != null && Meeting.Notes != value) Meeting.Notes = value; }
    }

    /// <summary>Title and notes are read-only while this meeting is being enhanced.</summary>
    public bool IsLocked => app.Store.ProcessingID == Id;
    public bool IsEnhancing => app.Store.ProcessingID == Id;
    public string ProgressText => IsEnhancing ? app.Store.Status : "";

    // Tabs

    public bool IsLive => app.IsLive(Id);
    /// <summary>The transcript tab exists only for finished meetings with a transcript; live meetings keep the box.</summary>
    public bool IsTranscriptTabAvailable => Meeting.Transcript.Count > 0 && !IsLive;
    public NoteTab SelectedTab => selectedTab;

    /// <summary>Two-way for the tab radio buttons: reads the shown tab, writes through <see cref="SelectTab"/>.</summary>
    public NoteTab Tab { get => CurrentTab; set { SelectTab(value); OnPropertyChanged(); } }
    public NoteTab CurrentTab => selectedTab == NoteTab.Transcript && !IsTranscriptTabAvailable ? NoteTab.Notes : selectedTab;
    public bool ShowsSparkles => CurrentTab == NoteTab.Enhanced && Meeting.EnhancedNotes.Length > 0;

    public void SelectTab(NoteTab tab)
    {
        if (tab == NoteTab.Transcript && !IsTranscriptTabAvailable) return;
        selectedTab = tab;
        if (tab == NoteTab.Transcript) transcriptBoxOpen = false;
        NotifyTabs();
    }

    // Enhancement (spec 9.2)

    public bool HasTranscriptText => TranscriptGrouping.HasTranscriptText(Meeting);
    public string? ConfigurationIssue => app.Store.EnhancementConfigurationIssue;
    /// <summary>"Set up enhancement…" without a configured provider, otherwise "Enhance notes" or "Enhance again".</summary>
    public string EnhanceButtonText => ConfigurationIssue != null ? Strings.SetUpEnhancement : Meeting.EnhancedNotes.Length == 0 ? Strings.EnhanceNotes : Strings.EnhanceAgain;
    public string EnhanceToolTip => ConfigurationIssue ?? Strings.EnhanceNotesHint;
    public bool HasEnhancedNotes => Meeting.EnhancedNotes.Length > 0;
    public string EnhancedMarkdown => Meeting.DisplayedEnhancedNotes;
    public string EnhancedFooter => Strings.Format(Strings.EnhancedWith, Meeting.EnhancementEngine);
    public bool ShowEnhancedEmpty => !IsEnhancing && !HasEnhancedNotes;
    public bool ShowEnhancedContent => !IsEnhancing && HasEnhancedNotes;

    public IAsyncRelayCommand EnhanceCommand { get; }
    public IAsyncRelayCommand EnhanceAgainCommand { get; }
    public IRelayCommand SetUpEnhancementCommand { get; }
    public IRelayCommand<string> SelectTabCommand { get; }

    private Task EnhanceAsync()
    {
        if (ConfigurationIssue != null) { app.OpenSettings(SettingsArea.AiLanguage); return Task.CompletedTask; }
        return app.Store.EnhanceAsync(Id);
    }

    /// <summary>After a successful enhancement the editor switches to "Enhanced notes".</summary>
    internal void EnhancementCompleted()
    {
        if (Meeting.EnhancedNotes.Length > 0) SelectTab(NoteTab.Enhanced);
    }

    // Live transcript box (spec 9.2)

    public bool TranscriptBoxOpen
    {
        get => transcriptBoxOpen;
        set
        {
            if (!SetProperty(ref transcriptBoxOpen, value)) return;
            // Both panels grow from the bottom of the editor; only one of them is open.
            if (value) Chat.IsExpanded = false;
            NotifyBox();
        }
    }

    public bool IsTranscriptBoxVisible => transcriptBoxOpen && CurrentTab != NoteTab.Transcript;
    public bool ShowTranscriptToggle => CurrentTab != NoteTab.Transcript;
    public string TranscriptToggleHint => transcriptBoxOpen ? Strings.HideTranscript : Strings.ShowTranscript;
    public IRelayCommand ToggleTranscriptBoxCommand { get; }
    public IRelayCommand CloseTranscriptBoxCommand { get; }
    public IRelayCommand ResetTranscriptHeightCommand { get; }

    private void ToggleTranscriptBox() { if (CurrentTab != NoteTab.Transcript) TranscriptBoxOpen = !TranscriptBoxOpen; }

    /// <summary>
    /// 120–300 px automatically (more in narrow windows); a dragged height is kept and clamped below the header. The
    /// automatic height also yields to the header, as the flexible macOS frame does, so the tabs are never covered.
    /// </summary>
    public double TranscriptBoxHeight
    {
        get
        {
            var header = headerHeight > 0 ? headerHeight : TranscriptBoxLayout.TallestHeader;
            var height = TranscriptBoxLayout.Height(areaWidth, areaHeight, dragHeight, header);
            return dragHeight != null ? height : Math.Max(TranscriptBoxLayout.Minimum, Math.Min(height, areaHeight - header - TranscriptBoxLayout.EditorChrome));
        }
    }

    /// <summary>The view reports the editor column's size and the measured header height.</summary>
    public void SetLayout(double width, double height, double header)
    {
        if (Math.Abs(width - areaWidth) < .5 && Math.Abs(height - areaHeight) < .5 && Math.Abs(header - headerHeight) < .5) return;
        areaWidth = width; areaHeight = height; headerHeight = header;
        Chat.SetAreaHeight(height);
        OnPropertyChanged(nameof(TranscriptBoxHeight));
    }

    /// <summary>Drag of the grip (null: back to automatic, the double-click).</summary>
    public void SetPreferredTranscriptHeight(double? height)
    {
        dragHeight = height is double value ? TranscriptBoxLayout.Height(areaWidth, areaHeight, value, headerHeight > 0 ? headerHeight : TranscriptBoxLayout.TallestHeader) : null;
        app.UiState.State.TranscriptHeight = dragHeight ?? 0;
        app.UiState.Save();
        OnPropertyChanged(nameof(TranscriptBoxHeight));
    }

    private void MeetingChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(Meeting.Title)) { OnPropertyChanged(nameof(Title)); app.Library.RefreshRow(Id); }
        else if (args.PropertyName is nameof(Meeting.Notes)) { OnPropertyChanged(nameof(Notes)); Chat.Refresh(); }
    }

    /// <summary>Store state changed (recording, processing, transcript, settings).</summary>
    public void Refresh()
    {
        Folder.Refresh();
        Transcript.Refresh();
        Chat.Refresh();
        if (selectedTab == NoteTab.Transcript && !IsTranscriptTabAvailable) selectedTab = NoteTab.Notes;
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(Notes)); OnPropertyChanged(nameof(DateText));
        OnPropertyChanged(nameof(HasDuration)); OnPropertyChanged(nameof(DurationText)); OnPropertyChanged(nameof(IsLocked));
        OnPropertyChanged(nameof(IsEnhancing)); OnPropertyChanged(nameof(ProgressText)); OnPropertyChanged(nameof(IsLive));
        OnPropertyChanged(nameof(HasTranscriptText)); OnPropertyChanged(nameof(ConfigurationIssue)); OnPropertyChanged(nameof(EnhanceButtonText));
        OnPropertyChanged(nameof(EnhanceToolTip)); OnPropertyChanged(nameof(HasEnhancedNotes)); OnPropertyChanged(nameof(EnhancedMarkdown));
        OnPropertyChanged(nameof(EnhancedFooter)); OnPropertyChanged(nameof(ShowEnhancedEmpty)); OnPropertyChanged(nameof(ShowEnhancedContent));
        NotifyTabs();
        EnhanceCommand.NotifyCanExecuteChanged();
        EnhanceAgainCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Only the running meeting's duration ticks every second.</summary>
    internal void RefreshDuration() { OnPropertyChanged(nameof(HasDuration)); OnPropertyChanged(nameof(DurationText)); }

    internal void RefreshProgress() => OnPropertyChanged(nameof(ProgressText));

    private void NotifyTabs()
    {
        OnPropertyChanged(nameof(SelectedTab)); OnPropertyChanged(nameof(CurrentTab)); OnPropertyChanged(nameof(Tab)); OnPropertyChanged(nameof(IsTranscriptTabAvailable));
        OnPropertyChanged(nameof(ShowsSparkles));
        NotifyBox();
    }

    private void NotifyBox()
    {
        OnPropertyChanged(nameof(TranscriptBoxOpen)); OnPropertyChanged(nameof(IsTranscriptBoxVisible)); OnPropertyChanged(nameof(ShowTranscriptToggle));
        OnPropertyChanged(nameof(TranscriptToggleHint));
        ToggleTranscriptBoxCommand.NotifyCanExecuteChanged();
        app.Recording.Refresh();
    }

    public void Dispose()
    {
        Meeting.PropertyChanged -= MeetingChanged;
        Chat.Dispose();
    }
}
