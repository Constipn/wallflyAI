using System.Windows;
using System.Windows.Controls;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>The folder capsule of the editor header and the preview, with its "Move to folder" popover.</summary>
public partial class FolderPickerButton : UserControl
{
    public FolderPickerButton() => InitializeComponent();

    private void Capsule_Click(object sender, RoutedEventArgs args)
    {
        if (DataContext is FolderPickerViewModel model) { model.Refresh(); model.IsOpen = !model.IsOpen; }
    }
}
