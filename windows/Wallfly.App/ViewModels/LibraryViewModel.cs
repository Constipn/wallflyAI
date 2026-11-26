using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.App.Controls;
using Wallfly.Core;
using Wallfly.Core.Search;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

public enum ScopeKind { All, Unfiled, Folder }

/// <summary>Which meetings the list shows (macOS <c>MeetingLibraryScope</c>).</summary>
public readonly record struct LibraryScope(ScopeKind Kind, Guid? FolderID = null)
{
    public static LibraryScope All => new(ScopeKind.All);
    public static LibraryScope Unfiled => new(ScopeKind.Unfiled);
    public static LibraryScope Folder(Guid id) => new(ScopeKind.Folder, id);

    public bool Contains(FolderCatalog catalog, Meeting meeting) => Kind switch
    {
        ScopeKind.All => true,
        ScopeKind.Unfiled => catalog.FolderID(meeting.Id) == null,
        _ => catalog.FolderID(meeting.Id) == FolderID
    };
}

/// <summary>One sidebar row: "All meetings", "No folder" or a folder with its colours and meeting count.</summary>
public sealed class FolderRowViewModel(LibraryScope scope) : ObservableObject
{
    private string name = "";
    private IconKind icon;
    private string foreground = Palette.Ink;
    private string iconForeground = Palette.Muted;
    private string selectedBackground = Palette.Gray100;
    private int count;
    private bool selected;
    private bool dropTarget;
    private string toolTip = "";

    public LibraryScope Scope { get; } = scope;
    public Guid? FolderID => Scope.FolderID;
    public bool IsCustom => Scope.Kind == ScopeKind.Folder;
    /// <summary>Meetings can be dropped on "No folder" and on folders, never on "All meetings".</summary>
    public bool AcceptsDrop => Scope.Kind != ScopeKind.All;
    public string Name { get => name; set => SetProperty(ref name, value); }
    public IconKind Icon { get => icon; set => SetProperty(ref icon, value); }
    public string Foreground { get => foreground; set => SetProperty(ref foreground, value); }
    public string IconForeground { get => iconForeground; set => SetProperty(ref iconForeground, value); }
    public string ToolTip { get => toolTip; set => SetProperty(ref toolTip, value); }
    public int Count { get => count; set => SetProperty(ref count, value); }
    public string SelectedBackground { get => selectedBackground; set { if (SetProperty(ref selectedBackground, value)) OnPropertyChanged(nameof(Background)); } }
    public bool IsSelected { get => selected; set { if (SetProperty(ref selected, value)) OnPropertyChanged(nameof(Background)); } }
    public bool IsDropTarget { get => dropTarget; set { if (SetProperty(ref dropTarget, value)) OnPropertyChanged(nameof(Background)); } }
    /// <summary>Selection colour while a meeting is dragged over the row, otherwise the folder's own background when selected.</summary>
    public string Background => IsDropTarget ? Palette.Selection : IsSelected ? SelectedBackground : "Transparent";
}

/// <summary>A folder badge in a library row.</summary>
public sealed record FolderBadge(string Name, IconKind Icon, string Foreground, string Background);

/// <summary>One search excerpt under a library row: the passage label and the text around the first match.</summary>
public sealed record SnippetViewModel(string Label, string Text, string Query);

/// <summary>One library row: 94 px plus 70 px per search excerpt (at most two).</summary>
public sealed class MeetingRowViewModel(Meeting meeting) : ObservableObject
{
    public const double BaseHeight = 94;
    public const double SnippetHeight = 70;
    private string title = "";
    private string date = "";
    private FolderBadge? folder;
    private bool recording;
    private string query = "";
    private IReadOnlyList<SnippetViewModel> snippets = [];
    private MeetingSearchResult? result;

    public Meeting Meeting { get; } = meeting;
    public Guid Id => Meeting.Id;
    public string Title { get => title; private set => SetProperty(ref title, value); }
    public string DateText { get => date; private set => SetProperty(ref date, value); }
    public FolderBadge? Folder { get => folder; private set => SetProperty(ref folder, value); }
    public bool IsRecording { get => recording; private set => SetProperty(ref recording, value); }
    public string Query { get => query; private set => SetProperty(ref query, value); }
    public IReadOnlyList<SnippetViewModel> Snippets { get => snippets; private set { if (SetProperty(ref snippets, value)) OnPropertyChanged(nameof(Height)); } }
    public double Height => BaseHeight + Snippets.Count * SnippetHeight;
    public string AutomationName => Title + ", " + (Folder?.Name ?? Strings.NoFolder);
    public string ToolTip => Title + (Folder != null ? " · " + Folder.Name : "");

    public void Update(FolderCatalog catalog, bool showBadge, bool isRecording, string activeQuery, MeetingSearchResult? searchResult)
    {
        Title = Meeting.Title.Trim().Length == 0 ? Strings.UntitledMeeting : Meeting.Title;
        DateText = Formats.ListDate(Meeting.StartedAt ?? Meeting.CreatedAt);
        var assigned = catalog.Folders.FirstOrDefault(item => item.Id == catalog.FolderID(Meeting.Id));
        FolderBadge? badge = null;
        if (showBadge && assigned != null)
        {
            var colors = FolderStyles.Colors(assigned);
            badge = new(assigned.Name, FolderStyles.Icon(assigned.Icon).Kind, colors.Foreground, colors.Background);
        }
        Folder = badge;
        IsRecording = isRecording;
        Query = activeQuery;
        if (!ReferenceEquals(result, searchResult) || Snippets.Count > 0 && searchResult == null)
        {
            result = searchResult;
            Snippets = searchResult == null || activeQuery.Length == 0 ? []
                : searchResult.Hits.Take(2).Select(hit => new SnippetViewModel(hit.Passage.Label, hit.Snippet, activeQuery)).ToList();
        }
        OnPropertyChanged(nameof(AutomationName));
        OnPropertyChanged(nameof(ToolTip));
    }
}

/// <summary>
/// The middle column (spec 9.1): the scope's meetings or the search results, the 180 ms debounced search with excerpts
/// and highlight, and the sidebar folder rows. Selection and navigation go through <see cref="AppViewModel"/>.
/// </summary>
public sealed class LibraryViewModel : ObservableObject
{
    public static readonly TimeSpan SearchDelay = TimeSpan.FromMilliseconds(180);

    private readonly AppViewModel app;
    private readonly IUiScheduler scheduler;
    private LibraryScope scope = LibraryScope.All;
    private string searchText = "";
    private string activeQuery = "";
    private bool searching;
    private bool searchEverywhere;
    private IReadOnlyList<MeetingSearchResult> results = [];
    private IDisposable? pendingSearch;
    private MeetingRowViewModel? selectedRow;
    private bool syncing;

    public LibraryViewModel(AppViewModel app, IUiScheduler scheduler)
    {
        this.app = app;
        this.scheduler = scheduler;
        ClearSearchCommand = new RelayCommand(() => SearchText = "");
        ToggleSearchEverywhereCommand = new RelayCommand(() => SearchEverywhere = !SearchEverywhere);
    }

    private MeetingLibraryService Store => app.Store;

    public ObservableCollection<FolderRowViewModel> Folders { get; } = [];
    public ObservableCollection<MeetingRowViewModel> Rows { get; } = [];
    public IRelayCommand ClearSearchCommand { get; }
    public IRelayCommand ToggleSearchEverywhereCommand { get; }

    /// <summary>Raised after a search finished, with the results in display order.</summary>
    public event Action? SearchCompleted;

    public LibraryScope Scope => scope;

    public string SearchText
    {
        get => searchText;
        set
        {
            if (!SetProperty(ref searchText, value ?? "")) return;
            app.OnSearchEdited();
            QueueSearch();
            Notify();
        }
    }

    public bool HasQuery => MeetingSearch.Terms(searchText).Count > 0;
    /// <summary>The query of the shown results; rows highlight this, not the text still being typed.</summary>
    public string ActiveQuery => activeQuery;
    public bool IsSearching { get => searching; private set { if (SetProperty(ref searching, value)) Notify(); } }
    public IReadOnlyList<MeetingSearchResult> Results => results;

    public bool SearchEverywhere
    {
        get => searchEverywhere;
        set { if (SetProperty(ref searchEverywhere, value)) { QueueSearch(); Notify(); } }
    }

    public string ScopeName =>
        HasQuery && SearchEverywhere ? Strings.AllMeetings
        : scope.Kind == ScopeKind.Folder ? Store.Catalog.Folders.FirstOrDefault(folder => folder.Id == scope.FolderID)?.Name ?? Strings.AllMeetings
        : scope.Kind == ScopeKind.Unfiled ? Strings.NoFolder : Strings.AllMeetings;

    public string HeaderTitle => HasQuery ? Strings.SearchResults : ScopeName;
    public int Count => Rows.Count;
    public string SearchingInText => Strings.Format(Strings.SearchingIn, ScopeName);
    public bool CanSearchEverywhere => scope.Kind != ScopeKind.All;
    public string SearchEverywhereText => SearchEverywhere ? Strings.SearchCurrentFolderOnly : Strings.SearchAllMeetings;
    public bool ShowEmpty => Rows.Count == 0 && !IsSearching;
    public IconKind EmptyIcon => HasQuery ? IconKind.Search : IconKind.Tray;
    public string EmptyTitle => HasQuery ? Strings.NoResults : Strings.NoMeetingsYet;
    public string EmptyDetail => HasQuery ? Strings.Format(Strings.NoResultsDetail, searchText.Trim(), ScopeName) : Strings.NoMeetingsYetDetail;
    public bool ShowFolderBadges => scope.Kind == ScopeKind.All || HasQuery && SearchEverywhere;

    /// <summary>
    /// Two-way with the list. A user selection previews the meeting (single click and arrow keys, as on macOS);
    /// updates that only re-sync the list never navigate.
    /// </summary>
    public MeetingRowViewModel? SelectedRow
    {
        get => selectedRow;
        set
        {
            if (syncing || ReferenceEquals(selectedRow, value)) return;
            selectedRow = value;
            OnPropertyChanged();
            if (value != null && app.SelectedID != value.Id) app.PreviewMeeting(value.Id);
        }
    }

    /// <summary>The scope's meetings, newest first (macOS <c>visibleMeetings</c>).</summary>
    public IReadOnlyList<Meeting> VisibleMeetings =>
        Store.Meetings.Where(meeting => scope.Contains(Store.Catalog, meeting)).OrderByDescending(meeting => meeting.CreatedAt).ToList();

    private IReadOnlyList<Meeting> SearchMeetings => SearchEverywhere ? Store.Meetings.ToList() : VisibleMeetings;

    /// <summary>What the list shows: the search results in rank order, or the scope's meetings.</summary>
    public IReadOnlyList<Meeting> FilteredMeetings
    {
        get
        {
            if (!HasQuery) return VisibleMeetings;
            var byID = SearchMeetings.ToDictionary(meeting => meeting.Id);
            return results.Select(result => byID.GetValueOrDefault(result.Id)).OfType<Meeting>().ToList();
        }
    }

    public MeetingSearchResult? ResultFor(Guid id) => HasQuery ? results.FirstOrDefault(result => result.Id == id) : null;

    /// <summary>Called by <see cref="AppViewModel.SelectScope"/>; leaving the scope also leaves "search everywhere".</summary>
    internal void SetScope(LibraryScope value)
    {
        scope = value;
        searchEverywhere = false;
        OnPropertyChanged(nameof(SearchEverywhere));
        OnPropertyChanged(nameof(Scope));
        if (HasQuery) QueueSearch();
        Refresh();
    }

    /// <summary>
    /// Content changed: a running query searches again after the debounce, like the macOS <c>.task(id:)</c>. A search
    /// that is already pending is not postponed, so a live transcript cannot starve the results.
    /// </summary>
    internal void ContentChanged()
    {
        if (HasQuery && pendingSearch == null) QueueSearch();
        Refresh();
    }

    private void QueueSearch()
    {
        pendingSearch?.Dispose();
        if (!HasQuery)
        {
            pendingSearch = null;
            results = [];
            activeQuery = "";
            IsSearching = false;
            Refresh();
            return;
        }
        IsSearching = true;
        pendingSearch = scheduler.Schedule(SearchDelay, RunSearch);
    }

    private void RunSearch()
    {
        pendingSearch = null;
        var query = searchText;
        results = MeetingSearch.Search(SearchMeetings, query);
        activeQuery = query;
        IsSearching = false;
        Refresh();
        SearchCompleted?.Invoke();
    }

    /// <summary>Re-syncs folder rows and meeting rows with the store, keeping row objects (and the list's scroll position) when only their content changed.</summary>
    public void Refresh()
    {
        RefreshFolders();
        var meetings = FilteredMeetings;
        var recordingID = Store.IsRecording || Store.IsStarting || Store.IsStopping ? Store.ActiveID : null;
        var sameRows = Rows.Count == meetings.Count && Rows.Select(row => row.Id).SequenceEqual(meetings.Select(meeting => meeting.Id));
        syncing = true;
        try
        {
            if (!sameRows)
            {
                var existing = Rows.ToDictionary(row => row.Id);
                Rows.Clear();
                foreach (var meeting in meetings) Rows.Add(existing.TryGetValue(meeting.Id, out var row) && ReferenceEquals(row.Meeting, meeting) ? row : new MeetingRowViewModel(meeting));
            }
            foreach (var row in Rows)
                row.Update(Store.Catalog, ShowFolderBadges, row.Id == recordingID, HasQuery ? activeQuery : "", ResultFor(row.Id));
            // A rebuilt list dropped its selection, so the selection is pushed again even when the row object was reused.
            var selected = Rows.FirstOrDefault(row => row.Id == app.SelectedID);
            if (!sameRows || !ReferenceEquals(selectedRow, selected)) { selectedRow = selected; OnPropertyChanged(nameof(SelectedRow)); }
        }
        finally { syncing = false; }
        Notify();
    }

    /// <summary>A title typed in the editor shows in its row at once (the service raises no update for edits).</summary>
    internal void RefreshRow(Guid id)
    {
        if (Rows.FirstOrDefault(row => row.Id == id) is { } row)
            row.Update(Store.Catalog, ShowFolderBadges, row.IsRecording, HasQuery ? activeQuery : "", ResultFor(id));
    }

    private void RefreshFolders()
    {
        var catalog = Store.Catalog;
        var scopes = new List<LibraryScope> { LibraryScope.All, LibraryScope.Unfiled };
        scopes.AddRange(catalog.Folders.Select(folder => LibraryScope.Folder(folder.Id)));
        if (!Folders.Select(row => row.Scope).SequenceEqual(scopes))
        {
            Folders.Clear();
            foreach (var item in scopes) Folders.Add(new FolderRowViewModel(item));
        }
        foreach (var row in Folders)
        {
            if (row.Scope.Kind == ScopeKind.All)
            {
                row.Name = Strings.AllMeetings; row.Icon = IconKind.Stack; row.Count = Store.Meetings.Count; row.ToolTip = Strings.AllMeetings;
            }
            else if (row.Scope.Kind == ScopeKind.Unfiled)
            {
                row.Name = Strings.NoFolder; row.Icon = IconKind.Tray; row.ToolTip = Strings.NoFolder;
                row.Count = Store.Meetings.Count(meeting => catalog.FolderID(meeting.Id) == null);
            }
            else if (catalog.Folders.FirstOrDefault(folder => folder.Id == row.FolderID) is { } folder)
            {
                var colors = FolderStyles.Colors(folder);
                row.Name = folder.Name; row.Icon = FolderStyles.Icon(folder.Icon).Kind;
                row.Foreground = colors.Foreground; row.IconForeground = colors.Foreground; row.SelectedBackground = colors.Background;
                row.ToolTip = folder.Description.Length == 0 ? folder.Name : folder.Description;
                row.Count = Store.Meetings.Count(meeting => catalog.FolderID(meeting.Id) == folder.Id);
            }
            row.IsSelected = row.Scope == scope;
        }
    }

    private void Notify()
    {
        OnPropertyChanged(nameof(HasQuery)); OnPropertyChanged(nameof(ScopeName)); OnPropertyChanged(nameof(HeaderTitle));
        OnPropertyChanged(nameof(Count)); OnPropertyChanged(nameof(SearchingInText)); OnPropertyChanged(nameof(CanSearchEverywhere));
        OnPropertyChanged(nameof(SearchEverywhereText)); OnPropertyChanged(nameof(ShowEmpty)); OnPropertyChanged(nameof(EmptyIcon));
        OnPropertyChanged(nameof(EmptyTitle)); OnPropertyChanged(nameof(EmptyDetail)); OnPropertyChanged(nameof(ActiveQuery));
    }
}
