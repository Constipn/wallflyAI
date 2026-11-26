using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.App.Controls;
using Wallfly.Core;
using Wallfly.Core.Search;

namespace Wallfly.App.ViewModels;

/// <summary>One folder destination of the folder picker and the "Move to folder" menus.</summary>
public sealed record FolderOption(Guid? FolderID, string Name, IconKind Icon, string Foreground, bool IsCurrent);

/// <summary>The folder capsule of the editor header and the preview (port of <c>MeetingFolderPicker</c>).</summary>
public sealed class FolderPickerViewModel : ObservableObject
{
    private readonly AppViewModel app;
    private bool open;

    public FolderPickerViewModel(AppViewModel app, Guid meetingID)
    {
        this.app = app;
        MeetingID = meetingID;
        MoveCommand = new RelayCommand<FolderOption>(option =>
        {
            if (option == null) return;
            IsOpen = false;
            app.MoveMeeting(MeetingID, option.FolderID);
        });
        CreateFolderCommand = new RelayCommand(() => { IsOpen = false; app.CreateFolder(MeetingID); });
        Refresh();
    }

    public Guid MeetingID { get; }
    public string Label { get; private set; } = "";
    public IconKind Icon { get; private set; }
    public string Foreground { get; private set; } = Palette.Ink;
    public string Background { get; private set; } = Palette.Sidebar;
    public bool IsEnabled => app.Store.FoldersAvailable;
    public IReadOnlyList<FolderOption> Options => app.FolderOptions(MeetingID);
    public bool IsOpen { get => open; set => SetProperty(ref open, value); }
    public IRelayCommand<FolderOption> MoveCommand { get; }
    public IRelayCommand CreateFolderCommand { get; }

    public void Refresh()
    {
        var catalog = app.Store.Catalog;
        var folder = catalog.Folders.FirstOrDefault(item => item.Id == catalog.FolderID(MeetingID));
        var colors = folder == null ? null : FolderStyles.Colors(folder);
        Label = folder?.Name ?? Strings.AssignFolder;
        Icon = folder == null ? IconKind.FolderAdd : FolderStyles.Icon(folder.Icon).Kind;
        Foreground = colors?.Foreground ?? Palette.Ink;
        Background = colors?.Background ?? Palette.Sidebar;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>One readable unit of the preview: a note line, an enhanced line or a transcript turn.</summary>
public sealed class PassageViewModel(SearchPassage passage, string query, bool markdown) : ObservableObject
{
    private bool active;
    public SearchPassage Passage { get; } = passage;
    public string Id => Passage.Id;
    public string Text => Passage.Text;
    public string Query { get; } = query;
    public string? Timestamp => Passage.Offset is double offset ? Meeting.Timestamp(offset) : null;
    /// <summary>Enhanced lines without a query render as Markdown (headings, bullets), like <c>MarkdownLine</c>.</summary>
    public bool IsMarkdown { get; } = markdown;
    public string MarkdownText => Text;
    public bool IsActive { get => active; set => SetProperty(ref active, value); }
}

/// <summary>
/// The read-only preview of a single click (port of <c>MeetingPreview</c>): source tabs, passages with search highlight
/// and "Match i of n" with arrows that switch to the tab of the match.
/// </summary>
public sealed class MeetingPreviewViewModel : ObservableObject
{
    private readonly AppViewModel app;
    private MeetingSearchSource source = MeetingSearchSource.Notes;
    private int hitIndex;
    private string query = "";
    private MeetingSearchResult? result;
    private double areaHeight = 850;

    public MeetingPreviewViewModel(AppViewModel app)
    {
        this.app = app;
        PreviousMatchCommand = new RelayCommand(() => Move(-1), () => HasHits);
        NextMatchCommand = new RelayCommand(() => Move(1), () => HasHits);
        EditCommand = new RelayCommand(() => { if (Meeting != null) app.OpenEditor(Meeting.Id); });
        SelectSourceCommand = new RelayCommand<string>(name => { if (Enum.TryParse<MeetingSearchSource>(name, out var value)) Source = value; });
        CopyTranscriptCommand = new RelayCommand(() => { if (Meeting != null) app.Dialogs.CopyText(TranscriptGrouping.CopyableTranscript(Meeting)); });
    }

    public Meeting? Meeting { get; private set; }
    public FolderPickerViewModel? Folder { get; private set; }

    /// <summary>
    /// The "Ask anything" chat below the preview, the same on all three tabs; null without a shown meeting. A new one is
    /// created for every other meeting (the stored chat is read then) and dropped when the preview is left, which cancels
    /// a waiting question, as closing the editor does.
    /// </summary>
    public MeetingChatViewModel? Chat { get; private set; }
    public string DateText => Meeting == null ? "" : Formats.LongDate(Meeting.StartedAt ?? Meeting.CreatedAt);
    public string Title => Meeting == null ? "" : Meeting.Title.Trim().Length == 0 ? Strings.UntitledMeeting : Meeting.Title;
    public string Query => query;
    public ObservableCollection<PassageViewModel> Passages { get; } = [];
    public bool HasPassages => Passages.Count > 0;
    public string EmptyDetail => source == MeetingSearchSource.Transcript ? Strings.NoContentTranscriptDetail : Strings.NoContentNotesDetail;
    public IconKind EmptyIcon => source == MeetingSearchSource.Transcript ? IconKind.Waveform : IconKind.Rename;
    public bool IsTranscriptSource => source == MeetingSearchSource.Transcript;
    public bool CanCopyTranscript => Meeting?.Transcript.Count > 0;

    public IReadOnlyList<MeetingSearchHit> Hits => result?.Hits ?? [];
    public bool HasHits => Hits.Count > 0;
    public int HitIndex => hitIndex;
    public string MatchText => HasHits ? Strings.Format(Strings.MatchOf, hitIndex + 1, Hits.Count) : "";
    public MeetingSearchHit? ActiveHit => hitIndex >= 0 && hitIndex < Hits.Count ? Hits[hitIndex] : null;

    public IRelayCommand PreviousMatchCommand { get; }
    public IRelayCommand NextMatchCommand { get; }
    public IRelayCommand EditCommand { get; }
    public IRelayCommand<string> SelectSourceCommand { get; }
    public IRelayCommand CopyTranscriptCommand { get; }

    /// <summary>Asks the view to bring a passage (or "title") into view, centred.</summary>
    public event Action<string>? ScrollRequested;

    public MeetingSearchSource Source
    {
        get => source;
        set { if (SetProperty(ref source, value)) { RebuildPassages(); NotifySource(); } }
    }

    /// <summary>Shows a meeting; a new search result starts again at the first match, as on macOS.</summary>
    public void Show(Meeting? meeting, string activeQuery, MeetingSearchResult? searchResult)
    {
        var resultChanged = !ReferenceEquals(result, searchResult) || !ReferenceEquals(Meeting, meeting) || query != activeQuery;
        Meeting = meeting;
        query = activeQuery;
        result = searchResult;
        if (Folder?.MeetingID != meeting?.Id) Folder = meeting == null ? null : new FolderPickerViewModel(app, meeting.Id);
        if (Chat?.MeetingID != meeting?.Id)
        {
            Chat?.Dispose();
            Chat = meeting == null ? null : new MeetingChatViewModel(app, meeting);
            Chat?.SetAreaHeight(areaHeight);
        }
        else Chat?.Refresh();
        if (resultChanged) Reset(); else { RebuildPassages(); Folder?.Refresh(); }
        OnPropertyChanged(string.Empty);
        PreviousMatchCommand.NotifyCanExecuteChanged();
        NextMatchCommand.NotifyCanExecuteChanged();
    }

    /// <summary>The meeting's text or folder changed.</summary>
    public void Refresh()
    {
        if (Meeting == null) return;
        Folder?.Refresh();
        Chat?.Refresh();
        RebuildPassages();
        OnPropertyChanged(nameof(Title)); OnPropertyChanged(nameof(DateText)); OnPropertyChanged(nameof(CanCopyTranscript));
    }

    /// <summary>
    /// The preview is no longer shown (editor, to-dos, nothing selected): drops the chat, so a waiting question is
    /// cancelled. The shown meeting and its tab stay; showing the preview again creates a fresh chat from the store.
    /// </summary>
    public void ReleaseChat()
    {
        if (Chat == null) return;
        Chat.Dispose();
        Chat = null;
        OnPropertyChanged(nameof(Chat));
    }

    /// <summary>The preview's height (from the view), so the expanded chat takes about half of it.</summary>
    public void SetAreaHeight(double height)
    {
        if (height <= 0) return;
        areaHeight = height;
        Chat?.SetAreaHeight(height);
    }

    private void Reset()
    {
        hitIndex = 0;
        var fallback = Meeting == null || Meeting.EnhancedNotes.Trim().Length == 0 ? MeetingSearchSource.Notes : MeetingSearchSource.Enhanced;
        source = Hits.FirstOrDefault() is { } first && first.Passage.Source != MeetingSearchSource.Title ? first.Passage.Source : fallback;
        RebuildPassages();
        NotifySource();
        RequestScroll();
    }

    /// <summary>Next or previous match, wrapping around; switches to the match's tab.</summary>
    public void Move(int delta)
    {
        if (!HasHits) return;
        hitIndex = (hitIndex + delta + Hits.Count) % Hits.Count;
        if (ActiveHit is { } hit && hit.Passage.Source != MeetingSearchSource.Title && hit.Passage.Source != source)
        {
            source = hit.Passage.Source;
            RebuildPassages();
            NotifySource();
        }
        MarkActive();
        OnPropertyChanged(nameof(HitIndex)); OnPropertyChanged(nameof(MatchText)); OnPropertyChanged(nameof(ActiveHit));
        RequestScroll();
    }

    private void RequestScroll()
    {
        if (ActiveHit is { } hit) ScrollRequested?.Invoke(hit.Passage.Source == MeetingSearchSource.Title ? "title" : hit.Passage.Id);
    }

    /// <summary>Replaces the passages only when their text changed, so store updates keep the reader's scroll position.</summary>
    private void RebuildPassages()
    {
        var markdown = query.Length == 0 && source == MeetingSearchSource.Enhanced;
        var next = Meeting == null ? [] : MeetingSearch.Passages(Meeting).Where(passage => passage.Source == source).ToList();
        var same = next.Count == Passages.Count && Passages.Select((passage, index) =>
            passage.Id == next[index].Id && passage.Text == next[index].Text && passage.Query == query && passage.IsMarkdown == markdown).All(equal => equal);
        if (!same)
        {
            Passages.Clear();
            foreach (var passage in next) Passages.Add(new PassageViewModel(passage, query, markdown));
        }
        MarkActive();
        OnPropertyChanged(nameof(HasPassages));
    }

    private void MarkActive()
    {
        var active = ActiveHit?.Passage.Id;
        foreach (var passage in Passages) passage.IsActive = passage.Id == active;
    }

    private void NotifySource()
    {
        OnPropertyChanged(nameof(Source)); OnPropertyChanged(nameof(IsTranscriptSource));
        OnPropertyChanged(nameof(EmptyDetail)); OnPropertyChanged(nameof(EmptyIcon));
    }
}
