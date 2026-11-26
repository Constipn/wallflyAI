using System.Windows;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Dialogs;

/// <summary>Create or edit a folder: name, ten icons, ten colours, description (spec 9.6).</summary>
public partial class FolderEditorWindow : Window
{
    private bool modal;

    public FolderEditorWindow(FolderEditorViewModel model)
    {
        InitializeComponent();
        ViewModel = model;
        DataContext = model;
        model.CloseRequested += saved =>
        {
            if (!IsVisible) return;
            if (modal) DialogResult = saved; else Close();
        };
    }

    public FolderEditorViewModel ViewModel { get; }

    /// <summary>Modal use from the dialog service; the smoke test shows the window without a modal loop.</summary>
    public bool ShowModal()
    {
        modal = true;
        return ShowDialog() == true;
    }
}
