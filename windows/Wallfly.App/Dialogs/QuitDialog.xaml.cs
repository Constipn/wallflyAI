using System.Windows;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Dialogs;

/// <summary>
/// "Save and end meeting?" with "Return to meeting" (default, Esc) and "Save and end" (DialogResult true). While the
/// library moves the dialog only says "Library is moving…" with "Back to Wallfly": Core refuses to quit until it is done.
/// </summary>
public partial class QuitDialog : Window
{
    public QuitDialog(QuitPrompt prompt = QuitPrompt.SaveAndEnd)
    {
        InitializeComponent();
        Prompt = prompt;
        if (prompt != QuitPrompt.LibraryMoving) return;
        TitleText.Text = Strings.QuitLibraryMovingTitle;
        DetailText.Text = Strings.QuitLibraryMovingDetail;
        ReturnButton.Content = Strings.QuitLibraryMovingReturn;
        ReturnButton.Margin = new Thickness(0);
        SaveButton.Visibility = Visibility.Collapsed;
    }

    public QuitPrompt Prompt { get; }

    private void Save_Click(object sender, RoutedEventArgs args) { if (Prompt == QuitPrompt.SaveAndEnd) DialogResult = true; }
}
