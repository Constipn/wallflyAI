using System.Windows;
using System.Windows.Controls;
using Wallfly.Core;
using Wallfly.Core.Services;

namespace Wallfly.App;

/// <summary>
/// "Add to my to-dos?" (port of <c>PersonalTaskReviewView</c>): every personal suggestion of one meeting with
/// "Accept" and "Not my task". Only accepted tasks appear under My to-dos; "Later" keeps them pending.
/// </summary>
public sealed class TaskReviewWindow : Window
{
    private readonly MeetingLibraryService store;
    private readonly StackPanel list = new();

    public Guid MeetingID { get; }

    public TaskReviewWindow(MeetingLibraryService store, Guid meetingID)
    {
        this.store = store;
        MeetingID = meetingID;
        // The app frame (integrated title bar, icon, Paper/Mica background) like every other window.
        Style = (Style)FindResource("AppWindow");
        Title = Strings.TaskReviewTitle; Width = 560; Height = 460; MinWidth = 420; MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var layout = new DockPanel { Margin = new Thickness(28, 24, 28, 20) }; Content = layout;
        var header = new StackPanel(); DockPanel.SetDock(header, Dock.Top); layout.Children.Add(header);
        header.Children.Add(new TextBlock { Text = Strings.TaskReviewTitle, FontFamily = (System.Windows.Media.FontFamily)FindResource("DisplayFont"), FontSize = 22, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 116, 0) });
        header.Children.Add(new TextBlock { Text = store.Meetings.FirstOrDefault(meeting => meeting.Id == meetingID)?.Title ?? "", Foreground = (System.Windows.Media.Brush)FindResource("Muted"), Margin = new Thickness(0, 6, 0, 10) });
        header.Children.Add(new TextBlock { Text = Strings.TaskReviewHint, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });
        var footer = new DockPanel { Margin = new Thickness(0, 14, 0, 0) }; DockPanel.SetDock(footer, Dock.Bottom); layout.Children.Add(footer);
        var later = new Button { Content = Strings.TaskReviewLater, IsCancel = true }; later.Click += (_, _) => Close();
        DockPanel.SetDock(later, Dock.Right); footer.Children.Add(later);
        footer.Children.Add(new TextBlock { Text = Strings.TaskReviewLaterHint, FontSize = 11, Foreground = (System.Windows.Media.Brush)FindResource("Muted"), VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap });
        layout.Children.Add(new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        Render();
    }

    /// <summary>Rebuilds the list; closes the window once nothing is left to decide.</summary>
    public void Render()
    {
        list.Children.Clear();
        var meeting = store.Meetings.FirstOrDefault(item => item.Id == MeetingID);
        IReadOnlyList<MeetingTask> pending = meeting == null ? [] : store.PendingTasks(meeting);
        if (pending.Count == 0) { if (IsLoaded) Close(); return; }
        foreach (var task in pending)
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };
            row.Children.Add(new TextBox { Text = task.Text, IsReadOnly = true, BorderThickness = new Thickness(0), TextWrapping = TextWrapping.Wrap, Background = System.Windows.Media.Brushes.Transparent });
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
            var accept = new Button { Content = Strings.TaskReviewAccept, Style = (Style)FindResource("PrimaryButton"), Margin = new Thickness(0, 0, 10, 0), IsEnabled = !store.Busy };
            var dismiss = new Button { Content = Strings.TaskReviewDismiss, IsEnabled = !store.Busy };
            accept.Click += (_, _) => { store.ReviewTask(MeetingID, task.Id, accept: true); Render(); };
            dismiss.Click += (_, _) => { store.ReviewTask(MeetingID, task.Id, accept: false); Render(); };
            buttons.Children.Add(accept); buttons.Children.Add(dismiss);
            row.Children.Add(buttons);
            row.Children.Add(new Separator { Margin = new Thickness(0, 14, 0, 0) });
            list.Children.Add(row);
        }
    }
}
