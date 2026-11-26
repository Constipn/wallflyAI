using System.Windows;

namespace Wallfly.App.Dialogs;

/// <summary>A question with one confirming action (delete, move library), or a message with "Done" when <c>confirm</c> is null.</summary>
public partial class ConfirmDialog : Window
{
    public ConfirmDialog(string title, string message, string? confirm)
    {
        InitializeComponent();
        TitleText.Text = title;
        MessageText.Text = message;
        if (confirm == null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            ConfirmButton.Content = Strings.Done;
            ConfirmButton.IsCancel = true;
        }
        else ConfirmButton.Content = confirm;
    }

    private void Confirm_Click(object sender, RoutedEventArgs args) => DialogResult = true;
}
