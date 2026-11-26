using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using Wallfly.App.Controls;
using Wallfly.App.ViewModels;
using Wallfly.Core;
using Wallfly.Core.Services;
using Forms = System.Windows.Forms;

namespace Wallfly.App.Shell;

/// <summary>
/// Both background entry points of a recording (spec 9.5): the floating capsule (34 × 101 px, 11 level bars, shown
/// while recording when the app is inactive, hidden or minimized; right edge −24 px, vertically centred on the main
/// window's screen; a click opens the note) and the tray icon ("Open current meeting", "Presentation mode" ✓).
/// The capsule is not owned by the main window, because an owned window disappears with a minimized owner.
/// It is excluded from screen capture (<c>WDA_EXCLUDEFROMCAPTURE</c>) unless the setting is turned off.
/// </summary>
internal sealed class LiveMeetingIndicator : IDisposable
{
    private const uint ExcludeFromCapture = 0x11;
    private readonly Window main;
    private readonly MeetingLibraryService store;
    private readonly Action openMeeting;
    private readonly Window capsule;
    private readonly Rectangle[] bars;
    private readonly Forms.NotifyIcon tray;
    private readonly System.Drawing.Icon trayIcon;
    private readonly Forms.ToolStripMenuItem presentation;
    private readonly Forms.ContextMenuStrip menu;
    private readonly DispatcherTimer monitorTimer;
    private string anchorMonitor = "";
    private bool? appliedAffinity;
    private bool disposed;

    public bool CapsuleVisible => !disposed && capsule.IsVisible;
    public bool TrayVisible => !disposed && tray.Visible;
    internal Window Capsule => capsule;

    /// <summary>True when Windows reports the capsule as excluded from capture.</summary>
    public bool CaptureExcluded
    {
        get
        {
            var handle = new WindowInteropHelper(capsule).Handle;
            return handle != IntPtr.Zero && GetWindowDisplayAffinity(handle, out var affinity) && affinity == ExcludeFromCapture;
        }
    }

    public LiveMeetingIndicator(Window main, MeetingLibraryService store, Action openMeeting, Action quit)
    {
        this.main = main; this.store = store; this.openMeeting = openMeeting;
        var accent = AudioMeter.Frozen(Palette.Accent);
        var content = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center };
        content.Children.Add(new Border
        {
            Width = 18.2, Height = 18.2, CornerRadius = new CornerRadius(9.1), BorderBrush = accent, BorderThickness = new Thickness(1.4),
            HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 0, 0, 7),
            Child = new Icon { Kind = IconKind.Waveform, Size = 10, StrokeWidth = 2.4, Foreground = accent, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center }
        });
        bars = Enumerable.Range(0, 11).Select(_ => new Rectangle { Width = 4.2, Height = 2.1, RadiusX = 1.05, RadiusY = 1.05, Fill = accent, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 1.05, 0, 1.05) }).ToArray();
        foreach (var bar in bars) content.Children.Add(bar);
        var button = new Button
        {
            Content = content, Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            Padding = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = Strings.OpenRunningMeeting, Focusable = false,
            // Avoid the application-wide button template.
            Template = new ControlTemplate(typeof(Button)) { VisualTree = new FrameworkElementFactory(typeof(ContentPresenter)) }
        };
        AutomationProperties.SetName(button, Strings.OpenRunningMeeting);
        button.Click += (_, _) => Open();
        capsule = new Window
        {
            Title = Strings.CapsuleTitle, Width = 34, Height = 101,
            WindowStyle = WindowStyle.None, ResizeMode = ResizeMode.NoResize,
            AllowsTransparency = true, Background = Brushes.Transparent,
            ShowInTaskbar = false, ShowActivated = false, Topmost = true,
            Content = new Border
            {
                CornerRadius = new CornerRadius(17), Background = AudioMeter.Frozen(Palette.Paper),
                BorderBrush = AudioMeter.Frozen(Palette.Hairline), BorderThickness = new Thickness(.525), Child = button
            }
        };
        capsule.SourceInitialized += (_, _) =>
        {
            var handle = new WindowInteropHelper(capsule).Handle;
            var style = GetWindowLong(handle, -20);
            _ = SetWindowLong(handle, -20, style | 0x08000000 | 0x00000080 | 0x00000008); // NOACTIVATE | TOOLWINDOW | TOPMOST
            ApplyCaptureExclusion();
        };
        menu = new Forms.ContextMenuStrip();
        menu.Items.Add(Strings.TrayOpenMeeting, null, (_, _) => Open());
        presentation = new Forms.ToolStripMenuItem(Strings.PresentationMode) { CheckOnClick = false };
        presentation.Click += (_, _) => store.PresentationMode = !store.PresentationMode;
        menu.Items.Add(presentation);
        menu.Items.Add(new Forms.ToolStripSeparator());
        menu.Items.Add(Strings.QuitMenu, null, (_, _) => quit());
        trayIcon = CreateTrayIcon();
        tray = new Forms.NotifyIcon { Icon = trayIcon, Text = Strings.AppName, ContextMenuStrip = menu };
        tray.DoubleClick += (_, _) => Open();
        tray.MouseClick += (_, args) => { if (args.Button == Forms.MouseButtons.Left) Open(); };
        store.Updated += Refresh;
        store.PropertyChanged += StoreChanged;
        main.Activated += WindowChanged;
        main.Deactivated += WindowChanged;
        main.StateChanged += WindowChanged;
        main.IsVisibleChanged += VisibilityChanged;
        main.LocationChanged += WindowChanged;
        // Also detects monitor removal, changed work areas and mixed-DPI layouts.
        monitorTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(500), DispatcherPriority.Background, (_, _) => Refresh(), main.Dispatcher);
        Refresh();
    }

    internal void Open() => openMeeting();
    internal void TogglePresentationFromTray() => presentation.PerformClick();

    private void StoreChanged(object? sender, PropertyChangedEventArgs args)
    {
        if (args.PropertyName is nameof(MeetingLibraryService.MeLevel) or nameof(MeetingLibraryService.OthersLevel))
        {
            var level = Math.Clamp(Math.Max(store.MeLevel, store.OthersLevel), 0, 1);
            if (!double.IsFinite(level)) level = 0;
            for (var index = 0; index < bars.Length; index++) bars[index].Width = 4.2 + level * 9.1 * (1 - index / 16d);
        }
        else if (args.PropertyName == nameof(MeetingLibraryService.PresentationMode)) Refresh();
    }

    private void WindowChanged(object? sender, EventArgs args) => Refresh();
    private void VisibilityChanged(object sender, DependencyPropertyChangedEventArgs args) => Refresh();

    /// <summary>Applies the "Hide the recording indicator in screen shares" setting (default on).</summary>
    private void ApplyCaptureExclusion()
    {
        var handle = new WindowInteropHelper(capsule).Handle;
        var wanted = store.Settings.HideIndicatorFromScreenCapture;
        if (handle == IntPtr.Zero || appliedAffinity == wanted) return;
        if (SetWindowDisplayAffinity(handle, wanted ? ExcludeFromCapture : 0)) appliedAffinity = wanted;
        else Trace.TraceWarning("SetWindowDisplayAffinity failed: " + Marshal.GetLastWin32Error());
    }

    internal void Refresh()
    {
        if (disposed) return;
        var session = store.IsStarting || store.IsRecording || store.IsStopping;
        // Only while recording; a hidden window keeps the icon too, so the app always stays reachable.
        tray.Visible = session || !main.IsVisible;
        tray.Text = store.IsStopping ? Strings.TrayFinishing : store.IsRecording ? Strings.TrayRecording : Strings.AppName;
        presentation.Checked = store.PresentationMode;
        presentation.Enabled = store.IsRecording && !store.IsStopping;
        ApplyCaptureExclusion();
        var mainHandle = new WindowInteropHelper(main).Handle;
        if (main.IsVisible && main.WindowState != WindowState.Minimized && mainHandle != IntPtr.Zero)
            anchorMonitor = Forms.Screen.FromHandle(mainHandle).DeviceName;
        var show = LiveIndicatorPolicy.Visible(store.IsRecording, store.IsStopping, store.PresentationMode,
            main.IsActive, main.IsVisible, main.WindowState == WindowState.Minimized);
        if (!show) { capsule.Hide(); return; }
        Position();
        if (!capsule.IsVisible) capsule.Show();
    }

    private void Position()
    {
        var screen = Forms.Screen.AllScreens.FirstOrDefault(item => item.DeviceName == anchorMonitor)
            ?? Forms.Screen.PrimaryScreen ?? Forms.Screen.AllScreens[0];
        anchorMonitor = screen.DeviceName;
        var area = screen.WorkingArea;
        var handle = new WindowInteropHelper(capsule).EnsureHandle();
        // Move first: GetDpiForWindow then reflects the destination monitor.
        if (Forms.Screen.FromHandle(handle).DeviceName != screen.DeviceName)
            _ = SetWindowPos(handle, new IntPtr(-1), area.Right - 60, area.Top + area.Height / 2, 0, 0, 0x0010 | 0x0001);
        var scale = Math.Max(1, GetDpiForWindow(handle)) / 96d;
        var position = LiveIndicatorPolicy.Placement(area.Left, area.Top, area.Width, area.Height, scale);
        _ = SetWindowPos(handle, new IntPtr(-1), position.X, position.Y, position.Width, position.Height, 0x0010);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true; monitorTimer.Stop();
        store.Updated -= Refresh; store.PropertyChanged -= StoreChanged;
        main.Activated -= WindowChanged; main.Deactivated -= WindowChanged; main.StateChanged -= WindowChanged;
        main.IsVisibleChanged -= VisibilityChanged; main.LocationChanged -= WindowChanged;
        tray.Visible = false; tray.Dispose(); trayIcon.Dispose(); menu.Dispose(); capsule.Close();
    }

    /// <summary>The brand icon (Assets/Wallfly.ico, embedded as a WPF resource) in the size the tray shows at this DPI.</summary>
    private static System.Drawing.Icon CreateTrayIcon()
    {
        try
        {
            var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Wallfly;component/Assets/Wallfly.ico"))?.Stream;
            if (stream != null)
            {
                using (stream) return new System.Drawing.Icon(stream, Forms.SystemInformation.SmallIconSize);
            }
        }
        catch (Exception exception) { Trace.TraceWarning("Tray icon resource not available: " + exception.Message); }
        return (System.Drawing.Icon)System.Drawing.SystemIcons.Application.Clone();
    }

    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongW")] private static extern int SetWindowLong(IntPtr window, int index, int value);
    [DllImport("user32.dll")] private static extern uint GetDpiForWindow(IntPtr window);
    [DllImport("user32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowDisplayAffinity(IntPtr window, uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowDisplayAffinity(IntPtr window, out uint affinity);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
