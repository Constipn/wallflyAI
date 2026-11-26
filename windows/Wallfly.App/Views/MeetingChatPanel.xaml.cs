using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>
/// The "Ask anything" bar of a finished meeting with its expandable panel of questions and answers (Windows addition).
/// It sits below the editor and below the preview, outside the tab bodies, so it is the same on every tab. Enter sends, Shift+Enter adds a
/// line, Esc collapses the panel; a click into the field opens the panel again when the chat has content.
/// </summary>
public partial class MeetingChatPanel : UserControl
{
    private MeetingChatViewModel? model;

    public MeetingChatPanel()
    {
        InitializeComponent();
        DataContextChanged += (_, args) => Attach(args.NewValue as MeetingChatViewModel);
    }

    private void Attach(MeetingChatViewModel? next)
    {
        if (model != null) model.ScrollToEndRequested -= ScrollToEnd;
        model = next;
        if (model != null) { model.ScrollToEndRequested += ScrollToEnd; ScrollToEnd(); }
    }

    private void ScrollToEnd() => Dispatcher.BeginInvoke(() => Scroller.ScrollToEnd(), DispatcherPriority.Background);

    private void Input_PreviewKeyDown(object sender, KeyEventArgs args)
    {
        if (model == null) return;
        if (args.Key == Key.Escape && model.IsExpanded)
        {
            model.CollapseCommand.Execute(null);
            args.Handled = true;
        }
        else if (args.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
        {
            // Shift+Enter falls through to the text box (AcceptsReturn) and adds a line.
            args.Handled = true;
            if (model.SendCommand.CanExecute(null)) model.SendCommand.Execute(null);
        }
    }

    private void Input_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs args) => model?.InputActivated();
}
