using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Dialogs;

/// <summary>
/// "Assign this segment" (the macOS speaker submenu, as a dialog): the participants of the meeting with their colours,
/// "Others · unknown" and "New participant…" with a name field. "Assign" needs a choice, and a valid name for a new
/// participant.
/// </summary>
public partial class AssignSpeakerDialog : Window
{
    private readonly List<(RadioButton Button, SpeakerAssignment? Choice)> choices = [];
    private readonly RadioButton newParticipant;

    public AssignSpeakerDialog(string excerpt, IReadOnlyList<SpeakerOption> participants)
    {
        InitializeComponent();
        Excerpt.Text = excerpt;
        foreach (var participant in participants) Add(Label(participant.Name, participant.Color), SpeakerAssignment.Existing(participant.ParticipantID));
        Add(Label(Strings.AssignOthersUnknown, Palette.Muted), SpeakerAssignment.Unknown);
        newParticipant = Add(Label(Strings.NewParticipant, null), null);
    }

    /// <summary>The chosen participant, Others, or the new participant with its name; null while nothing valid is chosen.</summary>
    public SpeakerAssignment? Result
    {
        get
        {
            if (newParticipant.IsChecked == true) return TranscriptViewModel.SpeakerName(NameBox.Text) is { } name ? SpeakerAssignment.Named(name) : null;
            return choices.FirstOrDefault(entry => entry.Button.IsChecked == true).Choice;
        }
    }

    /// <summary>For the smoke test, which shows the dialog without a modal loop: checks an entry (the last is "New participant…").</summary>
    internal void Select(int index, string? newName = null)
    {
        choices[Math.Clamp(index, 0, choices.Count - 1)].Button.IsChecked = true;
        if (newName != null) NameBox.Text = newName;
    }

    private RadioButton Add(UIElement content, SpeakerAssignment? choice)
    {
        var button = new RadioButton { Style = (Style)FindResource("OptionRadio"), GroupName = "Speaker", Content = content, Margin = new Thickness(0, 0, 0, 10) };
        button.Checked += (_, _) => Changed();
        choices.Add((button, choice));
        Options.Children.Add(button);
        return button;
    }

    private static StackPanel Label(string text, string? color)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal };
        if (color != null)
            panel.Children.Add(new Ellipse { Width = 7, Height = 7, Margin = new Thickness(0, 0, 7, 0), VerticalAlignment = VerticalAlignment.Center, Fill = (Brush)new BrushConverter().ConvertFromString(color)! });
        panel.Children.Add(new TextBlock { Text = text, FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis });
        return panel;
    }

    private void Changed()
    {
        // The constructor checks nothing, so this only runs once every entry exists.
        var naming = newParticipant?.IsChecked == true;
        NameBox.IsEnabled = naming;
        if (naming) NameBox.Focus();
        AssignButton.IsEnabled = Result != null;
    }

    private void Name_Changed(object sender, TextChangedEventArgs args) => AssignButton.IsEnabled = Result != null;

    private void Assign_Click(object sender, RoutedEventArgs args) { if (Result != null) DialogResult = true; }
}
