using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Wallfly.App.Views;

/// <summary>
/// The recording bar (spec 9.2): "Transcript" toggle, the (i) popover, and "Start recording" (Ctrl+Shift+R) or the
/// timer, two 8-bar meters and "Stop recording" (Ctrl+Shift+E). Narrow bars drop the meters first, as on macOS.
/// </summary>
public partial class RecordingBar : UserControl
{
    public RecordingBar()
    {
        InitializeComponent();
        SizeChanged += (_, args) => Meters.Visibility = args.NewSize.Width < 520 ? Visibility.Collapsed : Visibility.Visible;
    }

    internal Popup Info => InfoPopup;
    internal FrameworkElement InfoContent => InfoCard;
}
