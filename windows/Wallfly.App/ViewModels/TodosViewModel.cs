using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Wallfly.App.Controls;
using Wallfly.Core;
using Wallfly.Core.Services;

namespace Wallfly.App.ViewModels;

/// <summary>One accepted to-do with its source meeting.</summary>
public sealed class TodoRowViewModel : ObservableObject
{
    private readonly AppViewModel app;

    public TodoRowViewModel(AppViewModel app, Meeting meeting, MeetingTask task)
    {
        this.app = app;
        Meeting = meeting;
        Task = task;
        OpenMeetingCommand = new RelayCommand(() => app.OpenEditor(meeting.Id));
    }

    public Meeting Meeting { get; }
    public MeetingTask Task { get; }
    public string Text => Task.Text;
    /// <summary>The owner shows only when the meeting named one explicitly.</summary>
    public string? Owner => string.IsNullOrWhiteSpace(Task.Owner) ? null : Task.Owner;
    public string MeetingLink => (Meeting.Title.Trim().Length == 0 ? Strings.UntitledMeeting : Meeting.Title) + " · " + Formats.ShortDate(Meeting.CreatedAt);
    public string AutomationName => Strings.Format(Task.IsCompleted ? Strings.ReopenTask : Strings.CompleteTask, Task.Text);

    public bool IsCompleted
    {
        get => Task.IsCompleted;
        set
        {
            if (value == Task.IsCompleted) return;
            app.Store.SetTaskCompleted(Meeting.Id, Task.Id, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(AutomationName));
        }
    }

    public IRelayCommand OpenMeetingCommand { get; }
}

/// <summary>To-dos of one folder ("No folder" last), in catalog order.</summary>
public sealed record TodoGroupViewModel(Guid? FolderID, string Name, IconKind Icon, IReadOnlyList<TodoRowViewModel> Rows)
{
    public int Count => Rows.Count;
}

/// <summary>A meeting whose task recognition is pending or failed ("Retry task detection").</summary>
public sealed record RetryRowViewModel(Meeting Meeting, IRelayCommand OpenCommand, IAsyncRelayCommand RetryCommand)
{
    public string Title => Meeting.Title;
}

/// <summary>"Review suggestions: &lt;meeting&gt;".</summary>
public sealed record ReviewRowViewModel(Meeting Meeting, IRelayCommand ReviewCommand)
{
    public string Text => Strings.Format(Strings.ReviewSuggestions, Meeting.Title);
}

/// <summary>
/// "My to-dos" (spec 9.6, port of <c>TaskOverviewView</c>): accepted tasks grouped by folder, "Done · n" collapsed,
/// "Review suggestions", "Retry task detection" and the introduction card with "Load example".
/// </summary>
public sealed class TodosViewModel : ObservableObject
{
    private readonly AppViewModel app;
    private bool doneExpanded;
    private string signature = "";

    public TodosViewModel(AppViewModel app)
    {
        this.app = app;
        DismissIntroductionCommand = new RelayCommand(() => { app.UiState.State.IntroductionDismissed = true; app.UiState.Save(); OnPropertyChanged(nameof(ShowIntroduction)); });
        LoadExampleCommand = new RelayCommand(app.LoadExample, () => !app.Store.Busy && !app.IsSettingUp);
        SetUpEnhancementCommand = new RelayCommand(() => app.OpenSettings(SettingsArea.AiLanguage));
        GoToRecordingCommand = new RelayCommand(app.OpenActiveMeeting);
    }

    public ObservableCollection<TodoGroupViewModel> OpenGroups { get; } = [];
    public ObservableCollection<TodoGroupViewModel> DoneGroups { get; } = [];
    public ObservableCollection<RetryRowViewModel> NeedsRetry { get; } = [];
    public ObservableCollection<ReviewRowViewModel> Reviews { get; } = [];
    public IRelayCommand DismissIntroductionCommand { get; }
    public IRelayCommand LoadExampleCommand { get; }
    public IRelayCommand SetUpEnhancementCommand { get; }
    public IRelayCommand GoToRecordingCommand { get; }

    public int OpenCount => OpenGroups.Sum(group => group.Count);
    public int DoneCount => DoneGroups.Sum(group => group.Count);
    public string OpenCountText => Strings.Format(Strings.OpenCount, OpenCount);
    public string DoneHeader => Strings.Format(Strings.DoneCount, DoneCount);
    public bool HasOpen => OpenGroups.Count > 0;
    public bool HasDone => DoneCount > 0;
    public string EmptyTitle => DoneCount == 0 ? Strings.NoTodosYet : Strings.AllDone;
    public bool HasRetry => NeedsRetry.Count > 0;
    public bool ShowIntroduction => !app.UiState.State.IntroductionDismissed && app.Store.Meetings.Count == 0;
    public bool IsExtracting => app.Store.ProcessingID != null;
    public string ProgressText => IsExtracting ? app.Store.Status : "";
    public bool HasActiveMeeting => app.ActiveMeeting != null;
    public string GoToRecordingText => app.ActiveMeeting is { } active ? Strings.Format(Strings.GoToRecordingTitle, active.Title) : "";
    public bool IsDoneExpanded { get => doneExpanded; set => SetProperty(ref doneExpanded, value); }

    /// <summary>Accepted tasks of <paramref name="meetings"/> grouped by folder; done tasks newest first (macOS <c>taskGroups</c>).</summary>
    public static IReadOnlyList<(Guid? FolderID, string Name, string Icon, IReadOnlyList<(Meeting Meeting, MeetingTask Task)> Rows)> Groups(
        IEnumerable<Meeting> meetings, FolderCatalog catalog, bool completed)
    {
        var rows = meetings.SelectMany(meeting => PostProcessingService.AcceptedTasks(meeting, completed).Select(task => (Meeting: meeting, Task: task))).ToList();
        var result = new List<(Guid?, string, string, IReadOnlyList<(Meeting, MeetingTask)>)>();
        foreach (var id in catalog.Folders.Select(folder => (Guid?)folder.Id).Append(null))
        {
            var matching = rows.Where(row => catalog.FolderID(row.Meeting.Id) == id).ToList();
            if (matching.Count == 0) continue;
            if (completed) matching = matching.OrderByDescending(row => row.Task.CompletedAt ?? DateTimeOffset.MinValue).ToList();
            var folder = catalog.Folders.FirstOrDefault(item => item.Id == id);
            result.Add((id, folder?.Name ?? Strings.NoFolder, folder?.Icon ?? "tray", matching));
        }
        return result;
    }

    /// <summary>Leaving the scope collapses "Done · n" again.</summary>
    internal void ScopeChanged() => IsDoneExpanded = false;

    public void Refresh()
    {
        var visible = app.Library.VisibleMeetings;
        var catalog = app.Store.Catalog;
        var key = string.Join("|", visible.SelectMany(meeting => (meeting.Tasks ?? []).Select(task =>
            $"{meeting.Id}/{task.Id}/{task.Review}/{task.IsCompleted}/{catalog.FolderID(meeting.Id)}/{task.Text}"))
            .Concat(visible.Select(meeting => $"{meeting.Id}:{meeting.Title}:{meeting.TaskExtraction}:{app.Store.PendingTasks(meeting).Count}"))
            .Concat(catalog.Folders.Select(folder => folder.Id + folder.Name + folder.Icon)))
            + "|" + app.Store.ProcessingID + app.Store.Busy;
        if (key != signature)
        {
            signature = key;
            Fill(OpenGroups, Groups(visible, catalog, false));
            Fill(DoneGroups, Groups(visible, catalog, true));
            NeedsRetry.Clear();
            foreach (var meeting in visible.Where(meeting => meeting.TaskExtraction is TaskExtractionState.Failed or TaskExtractionState.Pending && meeting.Id != app.Store.ProcessingID))
            {
                var id = meeting.Id;
                NeedsRetry.Add(new RetryRowViewModel(meeting, new RelayCommand(() => app.OpenEditor(id)),
                    new AsyncRelayCommand(() => app.Store.EnhanceAsync(id, tasksOnly: true), () => !app.Store.Busy)));
            }
            Reviews.Clear();
            foreach (var meeting in visible.Where(meeting => app.Store.PendingTasks(meeting).Count > 0))
            {
                var id = meeting.Id;
                Reviews.Add(new ReviewRowViewModel(meeting, new RelayCommand(() => app.Dialogs.ShowTaskReview(id), () => !app.Store.Busy)));
            }
        }
        LoadExampleCommand.NotifyCanExecuteChanged();
        OnPropertyChanged(string.Empty);
    }

    private void Fill(ObservableCollection<TodoGroupViewModel> target, IReadOnlyList<(Guid? FolderID, string Name, string Icon, IReadOnlyList<(Meeting Meeting, MeetingTask Task)> Rows)> groups)
    {
        target.Clear();
        foreach (var group in groups)
            target.Add(new TodoGroupViewModel(group.FolderID, group.Name, group.FolderID == null ? IconKind.Tray : FolderStyles.Icon(group.Icon).Kind,
                group.Rows.Select(row => new TodoRowViewModel(app, row.Meeting, row.Task)).ToList()));
    }
}
