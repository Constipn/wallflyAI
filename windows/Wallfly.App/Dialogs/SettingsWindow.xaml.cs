using System.ComponentModel;
using System.Windows;
using Wallfly.App.ViewModels;
using Wallfly.Core.Services;

namespace Wallfly.App.Dialogs;

/// <summary>Settings (spec 9.3): modal, three areas; "Done" and the close button save.</summary>
public partial class SettingsWindow : Window
{
    private readonly MeetingLibraryService store;
    private bool saved;

    public SettingsWindow(SettingsViewModel model, MeetingLibraryService store)
    {
        InitializeComponent();
        this.store = store;
        ViewModel = model;
        DataContext = model;
        model.CloseRequested += () => { saved = true; if (IsVisible) Close(); };
        model.PropertyChanged += ModelChanged;
        store.Updated += model.StoreChanged;
        Closed += (_, _) => { store.Updated -= model.StoreChanged; model.Detach(); };
        ShowArea();
    }

    public SettingsViewModel ViewModel { get; }

    private void ModelChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(SettingsViewModel.Area) or "") ShowArea();
    }

    private void ShowArea()
    {
        RecordingArea.Visibility = ViewModel.Area == SettingsArea.Recording ? Visibility.Visible : Visibility.Collapsed;
        AiArea.Visibility = ViewModel.Area == SettingsArea.AiLanguage ? Visibility.Visible : Visibility.Collapsed;
        StorageArea.Visibility = ViewModel.Area == SettingsArea.Storage ? Visibility.Visible : Visibility.Collapsed;
        Scroller.ScrollToTop();
    }

    /// <summary>Closing with the title bar saves like "Done" (macOS saves on disappear).</summary>
    protected override void OnClosing(CancelEventArgs args)
    {
        if (!saved && !store.Busy) ViewModel.Save();
        base.OnClosing(args);
    }
}
