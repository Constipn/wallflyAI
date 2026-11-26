using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>
/// The focus-mode editor (spec 9.2): date · duration, folder capsule, the 31 pt display-font title, the tabs with the red
/// underline and the three tab bodies. Narrow editors (below 600 px) use 20 px instead of 38 px side padding.
/// </summary>
public partial class MeetingEditorView : UserControl
{
    public MeetingEditorView()
    {
        InitializeComponent();
        SizeChanged += (_, args) => Root.Margin = new Thickness(args.NewSize.Width < 600 ? 20 : 38, 0, args.NewSize.Width < 600 ? 20 : 38, 0);
        DataContextChanged += (_, args) =>
        {
            if (args.NewValue is MeetingViewModel) Dispatcher.BeginInvoke(() => NotesBox.Focus(), DispatcherPriority.Input);
        };
        Header.SizeChanged += (_, _) => HeaderChanged?.Invoke();
    }

    /// <summary>Height of the visible header (at most 240 px), so the live box below never clips it.</summary>
    public double HeaderHeight => HeaderScroll.ActualHeight + 1;

    public event Action? HeaderChanged;

    internal TextBox Notes => NotesBox;
    internal TextBox TitleField => TitleBox;
}
