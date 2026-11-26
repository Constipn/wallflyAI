using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>The read-only preview of a single click (spec 9.1/9.6); scrolls the active match to the centre.</summary>
public partial class MeetingPreviewView : UserControl
{
    private MeetingPreviewViewModel? model;

    public MeetingPreviewView()
    {
        InitializeComponent();
        DataContextChanged += (_, args) =>
        {
            if (model != null) model.ScrollRequested -= ScrollTo;
            model = args.NewValue as MeetingPreviewViewModel;
            if (model != null) { model.ScrollRequested += ScrollTo; model.SetAreaHeight(ActualHeight); }
        };
        // The expanded chat takes about half of the preview's height, as in the editor.
        SizeChanged += (_, _) => model?.SetAreaHeight(ActualHeight);
    }

    private void ScrollTo(string passageID)
    {
        // Wait for the selected tab's passages to be laid out.
        Dispatcher.BeginInvoke(() =>
        {
            FrameworkElement? target = passageID == "title" ? TitleBlock : null;
            if (target == null && model != null)
            {
                var passage = model.Passages.FirstOrDefault(item => item.Id == passageID);
                if (passage != null) target = PassageList.ItemContainerGenerator.ContainerFromItem(passage) as FrameworkElement;
            }
            if (target == null || !target.IsVisible) return;
            var top = target.TransformToAncestor(Page).Transform(new Point()).Y + Page.Margin.Top;
            Scroller.ScrollToVerticalOffset(Math.Max(0, top - (Scroller.ViewportHeight - target.ActualHeight) / 2));
        }, DispatcherPriority.Loaded);
    }
}
