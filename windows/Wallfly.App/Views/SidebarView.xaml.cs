using System.Windows;
using System.Windows.Controls;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>
/// The sidebar (spec 9.1): app name, "New note", navigation, folders with counts and "Settings". Folder rows accept
/// meetings dragged from the list; the context menu of a folder offers "Edit folder…" and "Delete folder…".
/// </summary>
public partial class SidebarView : UserControl
{
    public SidebarView() => InitializeComponent();

    private AppViewModel? App => DataContext as AppViewModel;

    private static FolderRowViewModel? Row(object sender) => (sender as FrameworkElement)?.DataContext as FolderRowViewModel;

    private void Folder_DragEnter(object sender, DragEventArgs args) => Folder_DragOver(sender, args);

    private void Folder_DragOver(object sender, DragEventArgs args)
    {
        var row = Row(sender);
        var accepted = row is { AcceptsDrop: true } && App?.Store.FoldersAvailable == true
            && MeetingDragPayload.Decode(args.Data.GetData(MeetingDragPayload.Format)) != null;
        args.Effects = accepted ? DragDropEffects.Move : DragDropEffects.None;
        if (row != null) row.IsDropTarget = accepted;
        args.Handled = true;
    }

    private void Folder_DragLeave(object sender, DragEventArgs args)
    {
        if (Row(sender) is { } row) row.IsDropTarget = false;
    }

    private void Folder_Drop(object sender, DragEventArgs args)
    {
        if (Row(sender) is not { } row) return;
        row.IsDropTarget = false;
        if (!row.AcceptsDrop || App is not { } app || MeetingDragPayload.Decode(args.Data.GetData(MeetingDragPayload.Format)) is not Guid meetingID) return;
        app.MoveMeeting(meetingID, row.FolderID);
        args.Handled = true;
    }

    private void Folder_ContextMenuOpening(object sender, ContextMenuEventArgs args)
    {
        if (sender is not FrameworkElement element || Row(sender) is not { IsCustom: true } row || App is not { } app) { args.Handled = true; return; }
        var menu = new ContextMenu();
        var edit = new MenuItem { Header = Strings.EditFolderMenu, Icon = new Controls.Icon { Kind = Controls.IconKind.Rename, Size = 13 }, IsEnabled = !app.Store.Busy && app.Store.FoldersAvailable };
        edit.Click += (_, _) => app.EditFolderCommand.Execute(row);
        var delete = new MenuItem { Header = Strings.DeleteFolderMenu, Icon = new Controls.Icon { Kind = Controls.IconKind.Delete, Size = 13 }, IsEnabled = !app.Store.Busy && app.Store.FoldersAvailable };
        delete.Click += (_, _) => app.DeleteFolderCommand.Execute(row);
        menu.Items.Add(edit);
        menu.Items.Add(delete);
        element.ContextMenu = menu;
    }
}
