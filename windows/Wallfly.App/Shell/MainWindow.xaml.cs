using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Wallfly.App.Controls;
using Wallfly.App.ViewModels;
using Wallfly.App.Views;
using Wallfly.Core.Services;

namespace Wallfly.App.Shell;

/// <summary>
/// The main window (spec 9.1): 1340 × 850 by default, at least 360 × 520, three columns (sidebar 180–260 px, list
/// 280–440 px, detail). Closing hides the window to the tray while a recording runs and quits otherwise.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MeetingLibraryService store;
    private bool allowClose;
    private bool quitting;

    public MainWindow(AppViewModel model, MeetingLibraryService store)
    {
        InitializeComponent();
        this.store = store;
        ViewModel = model;
        DataContext = model;
        model.PropertyChanged += ModelChanged;
        model.ShowWindowRequested += BringToFront;
        model.QuitRequested += () => _ = QuitAsync();
        Root.SizeChanged += (_, _) => model.LayoutWidth = Root.ActualWidth;
        EditorHost.SizeChanged += (_, _) => ReportEditorLayout();
        Editor.HeaderChanged += ReportEditorLayout;
        Detail.SizeChanged += (_, args) =>
        {
            var narrow = args.NewSize.Width < 560;
            PresentationLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
            BackLabel.Visibility = narrow ? Visibility.Collapsed : Visibility.Visible;
        };
        PreviewMouseDown += CollapseTranscriptOnOutsideClick;
        Closing += OnClosing;
        UpdateColumns();
    }

    public AppViewModel ViewModel { get; }

    /// <summary>The shell disposes the indicator and shuts down after a successful quit.</summary>
    public event Action? QuitConfirmed;

    internal MeetingEditorView EditorView => Editor;
    internal LiveTranscriptBox TranscriptBoxView => TranscriptBox;
    internal RecordingBar RecordingBarView => RecordingBar;
    internal MeetingListView ListView => List;
    internal FrameworkElement EditorArea => EditorHost;

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(AppViewModel.SidebarVisible) or nameof(AppViewModel.ListVisible) or nameof(AppViewModel.DetailVisible) or "")
            UpdateColumns();
        if (args.PropertyName is nameof(AppViewModel.Current) or "") Dispatcher.BeginInvoke(ReportEditorLayout);
    }

    /// <summary>
    /// Focus mode and narrow windows collapse the sidebar and list columns (their minimum widths go with them). The
    /// rightmost column carries the title band with the caption buttons (WindowFrame).
    /// </summary>
    private void UpdateColumns()
    {
        var sidebar = ViewModel.SidebarVisible;
        SidebarColumn.MinWidth = sidebar ? 180 : 0;
        SidebarColumn.Width = sidebar ? new GridLength(Math.Max(180, SidebarColumn.ActualWidth > 0 ? SidebarColumn.ActualWidth : 200)) : new GridLength(0);
        Sidebar.Visibility = SidebarSplitter.Visibility = sidebar ? Visibility.Visible : Visibility.Collapsed;
        var list = ViewModel.ListVisible;
        var detail = ViewModel.DetailVisible;
        ListColumn.MinWidth = list ? 280 : 0;
        ListColumn.MaxWidth = list && detail ? 440 : double.PositiveInfinity;
        ListColumn.Width = !list ? new GridLength(0) : detail ? new GridLength(Math.Clamp(ListColumn.ActualWidth > 0 ? ListColumn.ActualWidth : 320, 280, 440)) : new GridLength(1, GridUnitType.Star);
        List.Visibility = list ? Visibility.Visible : Visibility.Collapsed;
        ListSplitter.Visibility = list && detail ? Visibility.Visible : Visibility.Collapsed;
        DetailColumn.Width = detail ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        Detail.Visibility = detail ? Visibility.Visible : Visibility.Collapsed;
        // Narrow list-only layout: the list is the rightmost column, so its header becomes the title band.
        List.SetCaptionBand(list && !detail);
    }

    private void ReportEditorLayout() => ViewModel.Current?.SetLayout(EditorHost.ActualWidth, EditorHost.ActualHeight, Editor.HeaderHeight);

    /// <summary>A click elsewhere in the window collapses the live transcript box (its toggle button is exempt).</summary>
    private void CollapseTranscriptOnOutsideClick(object sender, MouseButtonEventArgs args)
    {
        if (ViewModel.Current is not { IsTranscriptBoxVisible: true } current) return;
        if (OutsideClick.IsInside(args.OriginalSource as DependencyObject, TranscriptBox.Panel)) return;
        current.TranscriptBoxOpen = false;
    }

    public void BringToFront()
    {
        if (!IsVisible) Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    private void More_Click(object sender, RoutedEventArgs args)
    {
        if (MoreMenu() is not { } menu) return;
        MoreButton.ContextMenu = menu;
        menu.IsOpen = true;
    }

    /// <summary>The editor's "…" menu for the selected meeting; null without a selection.</summary>
    internal ContextMenu? MoreMenu()
    {
        if (ViewModel.SelectedMeeting is not { } meeting) return null;
        var menu = new ContextMenu { PlacementTarget = MoreButton, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        var busy = store.Busy;
        menu.Items.Add(Item(Strings.ExportMarkdown, IconKind.Export, true, () => ViewModel.ExportMarkdownCommand.Execute(null)));
        menu.Items.Add(Item(Strings.ShowSavedFiles, IconKind.OpenFolder, true, () => ViewModel.ShowSavedFiles(meeting.Id)));
        menu.Items.Add(MeetingListView.MoveMenu(ViewModel, meeting.Id));
        if (ViewModel.ShowRedetectSpeakers(meeting.Id))
            menu.Items.Add(Item(Strings.RedetectSpeakers, IconKind.Person, ViewModel.CanRedetectSpeakers(meeting.Id), () => ViewModel.RedetectSpeakers(meeting.Id)));
        if (ViewModel.ShowRedetectSpeakersOnDevice(meeting.Id))
            menu.Items.Add(Item(Strings.RedetectSpeakersOnDevice, IconKind.Person, ViewModel.CanRedetectSpeakers(meeting.Id), () => ViewModel.RedetectSpeakersOnDevice(meeting.Id)));
        menu.Items.Add(new Separator());
        menu.Items.Add(Item(Strings.RenameMenu, IconKind.Rename, !busy, () => ViewModel.RenameMeeting(meeting.Id)));
        var delete = Item(Strings.DeleteMenu, IconKind.Delete, !busy, () => ViewModel.DeleteMeeting(meeting.Id));
        delete.Foreground = (System.Windows.Media.Brush)FindResource("Accent");
        menu.Items.Add(delete);
        return menu;
    }

    private static MenuItem Item(string header, IconKind icon, bool enabled, Action action)
    {
        var item = new MenuItem { Header = header, IsEnabled = enabled, Icon = MeetingListView.Glyph(icon) };
        item.Click += (_, _) => action();
        return item;
    }

    private async void OnClosing(object? sender, CancelEventArgs args)
    {
        if (allowClose) return;
        args.Cancel = true;
        if (ViewModel.OnWindowClosing() == CloseAction.HideToTray)
        {
            // A failed save keeps the window open, so unsaved notes stay in front of the user.
            if (store.Flush()) Hide();
            return;
        }
        await QuitAsync();
    }

    /// <summary>Ctrl+Q, the tray's "Quit Wallfly" and closing outside a recording.</summary>
    public async Task QuitAsync()
    {
        if (quitting || allowClose) return;
        quitting = true;
        try
        {
            if (!await ViewModel.RequestQuitAsync()) return;
            allowClose = true;
            QuitConfirmed?.Invoke();
        }
        finally { quitting = false; }
    }

    /// <summary>Lets the window close without the quit flow (session end, smoke test teardown).</summary>
    internal void AllowClose() => allowClose = true;
}
