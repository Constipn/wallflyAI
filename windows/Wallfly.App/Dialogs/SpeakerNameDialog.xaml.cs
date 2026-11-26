using System.Windows;
using System.Windows.Controls;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Dialogs;

/// <summary>"Rename speaker" (macOS <c>SpeakerNameEditor</c>): an empty name or one over 160 characters cannot be saved.</summary>
public partial class SpeakerNameDialog : Window
{
    public SpeakerNameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        NameBox.SelectAll();
        SaveButton.IsEnabled = SpeakerName != null;
    }

    /// <summary>The trimmed name, or null when it cannot be saved.</summary>
    public string? SpeakerName => TranscriptViewModel.SpeakerName(NameBox.Text);

    private void Name_Changed(object sender, TextChangedEventArgs args) => SaveButton.IsEnabled = SpeakerName != null;

    private void Save_Click(object sender, RoutedEventArgs args) { if (SpeakerName != null) DialogResult = true; }
}
