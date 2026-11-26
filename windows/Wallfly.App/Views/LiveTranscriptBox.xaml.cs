using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>
/// The live transcript box (spec 9.2): white, radius 22, red LIVE pill, 120–300 px automatic height, a grip to drag
/// it taller or shorter (double click: automatic). Clicks elsewhere in the window collapse it (handled by the window).
/// </summary>
public partial class LiveTranscriptBox : UserControl
{
    private double startHeight;
    private double startY;

    public LiveTranscriptBox() => InitializeComponent();

    /// <summary>The visual whose clicks do not count as "outside".</summary>
    public FrameworkElement Panel => Box;

    private MeetingViewModel? Model => DataContext as MeetingViewModel;

    // Window coordinates: the grip moves with the box edge, so its own coordinates would feed back.
    private void Grip_DragStarted(object sender, DragStartedEventArgs args)
    {
        startHeight = Box.ActualHeight;
        startY = Mouse.GetPosition(Window.GetWindow(this)).Y;
    }

    private void Grip_DragDelta(object sender, DragDeltaEventArgs args)
    {
        if (Model is not { } model) return;
        var y = Mouse.GetPosition(Window.GetWindow(this)).Y;
        model.SetPreferredTranscriptHeight(startHeight - (y - startY));
    }

    private void Grip_MouseDoubleClick(object sender, MouseButtonEventArgs args)
    {
        Model?.ResetTranscriptHeightCommand.Execute(null);
        args.Handled = true;
    }
}
