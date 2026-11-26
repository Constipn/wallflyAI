using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Wallfly.App.Controls;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>
/// The library list (spec 9.1): virtualized rows of 94 px (+70 px per search excerpt), single click previews,
/// double click or Enter opens the editor, the context menu renames, deletes or moves, and rows drag onto folders.
/// </summary>
public partial class MeetingListView : UserControl
{
    private Point dragStart;
    private MeetingRowViewModel? pressed;
    private bool syncingSelection;

    public MeetingListView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (args.OldValue is AppViewModel previous)
            {
                previous.SearchFocusRequested -= FocusSearch;
                previous.Library.PropertyChanged -= LibraryChanged;
            }
            if (args.NewValue is AppViewModel app)
            {
                app.SearchFocusRequested += FocusSearch;
                app.Library.PropertyChanged += LibraryChanged;
                QueueSelectionSync();
            }
        };
        IsVisibleChanged += (_, _) => QueueSelectionSync();
    }

    /// <summary>
    /// Narrow list-only layout (MainWindow.UpdateColumns): the header becomes the title band, on the chrome brush, clear
    /// of the caption buttons and set off from the list by a line.
    /// </summary>
    internal void SetCaptionBand(bool band)
    {
        HeaderBand.SetResourceReference(Border.BackgroundProperty, band ? "ChromeBand" : "Paper");
        HeaderBand.BorderThickness = new Thickness(0, 0, 0, band ? 1 : 0);
        Body.Padding = new Thickness(0, band ? 14 : 0, 0, 0);
        CaptionSpacer.Visibility = band ? Visibility.Visible : Visibility.Collapsed;
    }

    // The selection is set imperatively after the rows settled: a bound SelectedItem can end up out of step with
    // SelectedIndex when the row collection is rebuilt while the list is collapsed (focus mode).
    private void LibraryChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(LibraryViewModel.SelectedRow) or "") QueueSelectionSync();
    }

    private void QueueSelectionSync() => Dispatcher.BeginInvoke(SyncSelection, System.Windows.Threading.DispatcherPriority.Loaded);

    private void SyncSelection()
    {
        if (App is not { } app) return;
        var wanted = app.Library.SelectedRow;
        if (ReferenceEquals(List.SelectedItem, wanted) && (wanted == null || List.SelectedIndex >= 0)) return;
        syncingSelection = true;
        try
        {
            List.SelectedItem = null;
            List.SelectedItem = wanted;
            if (wanted != null) List.ScrollIntoView(wanted);
        }
        finally { syncingSelection = false; }
    }

    /// <summary>User selection (click, arrow keys) previews the meeting.</summary>
    private void List_SelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (syncingSelection || App is not { } app || List.SelectedItem is not MeetingRowViewModel row) return;
        app.Library.SelectedRow = row;
    }

    private AppViewModel? App => DataContext as AppViewModel;

    private void FocusSearch()
    {
        Dispatcher.BeginInvoke(() => { SearchBox.Focus(); SearchBox.SelectAll(); });
    }

    private void Search_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key == Key.Escape && SearchBox.Text.Length > 0) { App?.Library.ClearSearchCommand.Execute(null); args.Handled = true; }
        else if (args.Key == Key.Down && List.Items.Count > 0) { List.SelectedIndex = Math.Max(0, List.SelectedIndex); (List.ItemContainerGenerator.ContainerFromIndex(List.SelectedIndex) as ListBoxItem)?.Focus(); args.Handled = true; }
    }

    private MeetingRowViewModel? RowAt(object originalSource) =>
        originalSource is DependencyObject source && ItemsControl.ContainerFromElement(List, source) is ListBoxItem item ? item.DataContext as MeetingRowViewModel : null;

    private void List_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args)
    {
        dragStart = args.GetPosition(List);
        pressed = RowAt(args.OriginalSource);
    }

    /// <summary>A click on the already selected meeting while its editor is open returns to the preview, as on macOS.</summary>
    private void List_PreviewMouseLeftButtonUp(object sender, MouseButtonEventArgs args)
    {
        if (App is { } app && RowAt(args.OriginalSource) is { } row && row == pressed && app.SelectedID == row.Id && app.Presentation != WorkspacePresentation.Preview && args.ClickCount == 1)
            app.PreviewMeeting(row.Id);
        pressed = null;
    }

    private void List_PreviewMouseMove(object sender, MouseEventArgs args)
    {
        if (args.LeftButton != MouseButtonState.Pressed || pressed is not { } row || App is not { Store.FoldersAvailable: true }) return;
        var position = args.GetPosition(List);
        if (Math.Abs(position.X - dragStart.X) < SystemParameters.MinimumHorizontalDragDistance && Math.Abs(position.Y - dragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;
        pressed = null;
        DragDrop.DoDragDrop(List, new DataObject(MeetingDragPayload.Format, MeetingDragPayload.Encode(row.Id)), DragDropEffects.Move);
    }

    private void List_MouseDoubleClick(object sender, MouseButtonEventArgs args)
    {
        if (App is { } app && RowAt(args.OriginalSource) is { } row) { args.Handled = true; app.OpenEditor(row.Id); }
    }

    private void List_KeyDown(object sender, KeyEventArgs args)
    {
        if (args.Key is Key.Enter or Key.Return && App is { } app && List.SelectedItem is MeetingRowViewModel row) { args.Handled = true; app.OpenEditor(row.Id); }
    }

    /// <summary>"Rename meeting…", "Delete meeting…" (both locked while busy) and "Move to folder" with the current folder checked.</summary>
    private void List_ContextMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (App is not { } app || RowAt(args.OriginalSource) is not { } row) { args.Handled = true; return; }
        var menu = new ContextMenu();
        var busy = app.Store.Busy;
        var rename = new MenuItem { Header = Strings.RenameMeetingMenu, IsEnabled = !busy, Icon = Glyph(IconKind.Rename) };
        rename.Click += (_, _) => app.RenameMeeting(row.Id);
        var delete = new MenuItem { Header = Strings.DeleteMeetingMenu, IsEnabled = !busy, Icon = Glyph(IconKind.Delete) };
        delete.Click += (_, _) => app.DeleteMeeting(row.Id);
        menu.Items.Add(rename);
        menu.Items.Add(delete);
        menu.Items.Add(new Separator());
        menu.Items.Add(MoveMenu(app, row.Id));
        List.ContextMenu = menu;
    }

    internal static MenuItem MoveMenu(AppViewModel app, Guid meetingID)
    {
        var move = new MenuItem { Header = Strings.MoveToFolder, IsEnabled = app.Store.FoldersAvailable, Icon = Glyph(IconKind.Folder) };
        foreach (var option in app.FolderOptions(meetingID))
        {
            var item = new MenuItem { Header = option.Name, IsChecked = option.IsCurrent, Icon = Glyph(option.Icon, option.Foreground) };
            var target = option.FolderID;
            item.Click += (_, _) => app.MoveMeeting(meetingID, target);
            move.Items.Add(item);
        }
        return move;
    }

    internal static Icon Glyph(IconKind kind, string? color = null)
    {
        var icon = new Icon { Kind = kind, Size = 13 };
        if (color != null) icon.Foreground = (Brush)new BrushConverter().ConvertFromString(color)!;
        return icon;
    }
}
