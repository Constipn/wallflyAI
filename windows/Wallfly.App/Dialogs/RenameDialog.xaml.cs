using System.Windows;
using System.Windows.Controls;

namespace Wallfly.App.Dialogs;

/// <summary>"Rename meeting": an empty name cannot be confirmed.</summary>
public partial class RenameDialog : Window
{
    public RenameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        NameBox.SelectAll();
    }

    public string MeetingName => NameBox.Text.Trim();

    private void Name_Changed(object sender, TextChangedEventArgs args) => RenameButton.IsEnabled = MeetingName.Length > 0;

    private void Rename_Click(object sender, RoutedEventArgs args) { if (MeetingName.Length > 0) DialogResult = true; }
}
