using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using Wallfly.App.ViewModels;
using Wallfly.Core.Services;

namespace Wallfly.App.Dialogs;

/// <summary>
/// "Set up Wallfly" (spec 9.4): modal, without a close button and not closable with Esc; only "Done" and
/// "Set up later" end it. The height follows the work area (at most 750 px).
/// </summary>
public partial class OnboardingWindow : Window
{
    private const int StyleIndex = -16;
    private const int SystemMenu = 0x00080000;
    private bool finished;

    public OnboardingWindow(OnboardingViewModel model, MeetingLibraryService store)
    {
        InitializeComponent();
        ViewModel = model;
        DataContext = model;
        Height = Math.Min(750, SystemParameters.WorkArea.Height - 100);
        model.CloseRequested += () => { finished = true; Close(); };
        model.PropertyChanged += (_, args) => { if (args.PropertyName is nameof(OnboardingViewModel.Step) or "") Scroller.ScrollToTop(); };
        store.Updated += Refresh;
        Closed += (_, _) => { store.Updated -= Refresh; model.Settings.Detach(); };
        Activated += (_, _) => model.RefreshPermissions();
        PreviewKeyDown += (_, args) => { if (args.Key == Key.Escape) args.Handled = true; };
        SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(this).Handle;
            _ = SetWindowLong(handle, StyleIndex, GetWindowLong(handle, StyleIndex) & ~SystemMenu);
        };
    }

    public OnboardingViewModel ViewModel { get; }

    private void Refresh()
    {
        ViewModel.Settings.StoreChanged();
        ViewModel.NextCommand.NotifyCanExecuteChanged();
        ViewModel.SetUpLaterCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Alt+F4 does not dismiss the setup either; it ends only through its buttons.</summary>
    protected override void OnClosing(CancelEventArgs args)
    {
        if (!finished) args.Cancel = true;
        base.OnClosing(args);
    }

    /// <summary>For the smoke test, which shows the window without a modal loop.</summary>
    internal void CloseForTest() { finished = true; Close(); }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
}
