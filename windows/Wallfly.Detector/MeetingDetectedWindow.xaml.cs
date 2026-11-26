using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Wallfly.Teams;

namespace Wallfly.Detector;

/// <summary>
/// The "Meeting detected · Microsoft Teams" popup (420 × 90): never takes focus (WS_EX_NOACTIVATE), floats above
/// other windows (WS_EX_TOPMOST), has no taskbar/Alt-Tab entry (WS_EX_TOOLWINDOW), and sits at the top right of
/// the work area of the monitor showing the Teams window. A custom window instead of a toast, because toasts
/// are suppressed by Do Not Disturb and in full screen (spec §6).
/// </summary>
public partial class MeetingDetectedWindow : Window
{
    private const double MarginRight = 20;
    private const double MarginTop = 14;
    private TeamsBounds? anchor;

    public MeetingDetectedWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => ApplyExtendedStyles();
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(Place);
    }

    public event Action? TakeNotesRequested;
    public event Action? DismissRequested;

    public void SetState(bool launching, string? error)
    {
        TakeNotesButton.IsEnabled = !launching;
        TakeNotesText.Text = launching ? "Opening…" : "Take notes";
        ErrorText.Text = error ?? "";
        ErrorText.Visibility = string.IsNullOrEmpty(error) ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Shows without activation, top right of the monitor that contains <paramref name="teamsWindow"/>.</summary>
    public void ShowNear(TeamsBounds? teamsWindow)
    {
        anchor = teamsWindow;
        if (!IsVisible) Show();
        Place();
    }

    private void Place()
    {
        var handle = new WindowInteropHelper(this).Handle;
        if (handle == 0) return;
        var (work, dpi) = Monitors.WorkAreaFor(anchor);
        var scale = dpi / 96.0;
        var width = (int)Math.Round(Width * scale);
        var height = (int)Math.Round(Height * scale);
        var x = (int)Math.Round(work.Right - width - MarginRight * scale);
        var y = (int)Math.Round(work.Y + MarginTop * scale);
        NativeMethods.SetWindowPos(handle, NativeMethods.HWND_TOPMOST, x, y, width, height,
            NativeMethods.SWP_NOACTIVATE | NativeMethods.SWP_SHOWWINDOW);
    }

    private void ApplyExtendedStyles()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var style = NativeMethods.GetWindowLongPtrW(handle, NativeMethods.GWL_EXSTYLE);
        style |= NativeMethods.WS_EX_NOACTIVATE | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_TOPMOST;
        style &= ~NativeMethods.WS_EX_APPWINDOW;
        NativeMethods.SetWindowLongPtrW(handle, NativeMethods.GWL_EXSTYLE, style);
    }

    private void OnTakeNotes(object sender, RoutedEventArgs args) => TakeNotesRequested?.Invoke();

    private void OnDismiss(object sender, RoutedEventArgs args) => DismissRequested?.Invoke();

    private static partial class NativeMethods
    {
        public const int GWL_EXSTYLE = -20;
        public const nint WS_EX_TOPMOST = 0x00000008;
        public const nint WS_EX_TOOLWINDOW = 0x00000080;
        public const nint WS_EX_APPWINDOW = 0x00040000;
        public const nint WS_EX_NOACTIVATE = 0x08000000;
        public const uint SWP_NOACTIVATE = 0x0010;
        public const uint SWP_SHOWWINDOW = 0x0040;
        public static readonly nint HWND_TOPMOST = -1;

        [LibraryImport("user32.dll")]
        public static partial nint GetWindowLongPtrW(nint window, int index);

        [LibraryImport("user32.dll")]
        public static partial nint SetWindowLongPtrW(nint window, int index, nint value);

        [LibraryImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        public static partial bool SetWindowPos(nint window, nint insertAfter, int x, int y, int width, int height, uint flags);
    }
}
