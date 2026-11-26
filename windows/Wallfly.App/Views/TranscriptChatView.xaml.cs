using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wallfly.App.Controls;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>
/// The conversation as a chat (spec 9.2): Me right, Others left, bubbles at most 75 % wide. While live it follows new
/// speech until the reader scrolls up; then "Latest ↓" brings it back.
/// </summary>
public partial class TranscriptChatView : UserControl
{
    private ScrollViewer? scroller;
    private TranscriptViewModel? model;
    private double lastOffset;

    public TranscriptChatView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) => Attach(args.NewValue as TranscriptViewModel);
        Turns.Loaded += (_, _) =>
        {
            scroller ??= FindScroller(Turns);
            if (scroller != null) { scroller.ScrollChanged -= Scrolled; scroller.ScrollChanged += Scrolled; }
            if (model?.IsLive == true) ScrollToEnd();
        };
    }

    private void Attach(TranscriptViewModel? next)
    {
        if (model != null) model.ScrollToEndRequested -= ScrollToEnd;
        model = next;
        if (model != null) model.ScrollToEndRequested += ScrollToEnd;
        lastOffset = 0;
        if (model?.IsLive == true) Dispatcher.BeginInvoke(ScrollToEnd);
    }

    private void ScrollToEnd()
    {
        scroller ??= FindScroller(Turns);
        Dispatcher.BeginInvoke(() => scroller?.ScrollToEnd(), System.Windows.Threading.DispatcherPriority.Background);
    }

    /// <summary>Following stops once the reader scrolls up away from the end and resumes within 40 px of it.</summary>
    private void Scrolled(object sender, ScrollChangedEventArgs args)
    {
        if (model == null || scroller == null) return;
        var distance = scroller.ExtentHeight - scroller.VerticalOffset - scroller.ViewportHeight;
        if (scroller.VerticalOffset < lastOffset - 1 && distance > 2) model.Following = false;
        else if (distance <= 40) model.Following = true;
        lastOffset = scroller.VerticalOffset;
    }

    /// <summary>The speaker label opens "Rename speaker…" (named participants only) and "Assign this segment…".</summary>
    private void Speaker_Click(object sender, RoutedEventArgs args)
    {
        if (sender is not Button button || button.DataContext is not ChatTurnViewModel turn || model == null) return;
        var menu = SpeakerMenu(model, turn);
        menu.PlacementTarget = button;
        menu.IsOpen = true;
    }

    /// <summary>The speaker menu of one turn (macOS <c>speakerMenu</c>); disabled entries while Core does not allow edits.</summary>
    internal static ContextMenu SpeakerMenu(TranscriptViewModel model, ChatTurnViewModel turn)
    {
        var menu = new ContextMenu { Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        if (turn.ParticipantID != null)
        {
            menu.Items.Add(new MenuItem { Header = Strings.RenameSpeaker, Command = model.RenameSpeakerCommand, CommandParameter = turn, Icon = MeetingListView.Glyph(IconKind.Rename) });
            menu.Items.Add(new Separator());
        }
        menu.Items.Add(new MenuItem { Header = Strings.AssignSegment, Command = model.AssignSegmentCommand, CommandParameter = turn, Icon = MeetingListView.Glyph(IconKind.Person) });
        return menu;
    }

    private static ScrollViewer? FindScroller(DependencyObject parent)
    {
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(parent); index++)
        {
            var child = VisualTreeHelper.GetChild(parent, index);
            if (child is ScrollViewer viewer) return viewer;
            if (FindScroller(child) is { } found) return found;
        }
        return null;
    }
}
