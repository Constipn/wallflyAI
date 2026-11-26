using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls.Primitives;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;

namespace Wallfly.App.Shell;

/// <summary>
/// The integrated window frame (Windows 11 22H2 and later): content runs up to the top edge, Windows draws the native
/// minimize/maximize/close buttons (snap layouts keep working) and Mica shows through wherever the content is
/// transparent. <see cref="WindowChrome"/> with the glass frame over the whole window; WPF hands WM_NCHITTEST to
/// DwmDefWindowProc first, so the maximize button reports HTMAXBUTTON to Windows. Older builds keep the standard frame.
/// <para><c>CaptionHeight</c> (0 = standard frame) is the draggable band at the top, measured from the window's top edge
/// and including the top resize border; controls inside it need <c>WindowChrome.IsHitTestVisibleInChrome</c>.
/// <c>CaptionButtonsWidth</c> is the width the caption buttons take at the right edge (0 without the integrated frame),
/// for a spacer that keeps content clear of them.</para>
/// </summary>
public static class WindowFrame
{
    /// <summary>Mica (DWMWA_SYSTEMBACKDROP_TYPE) exists from Windows 11 22H2, build 22621.</summary>
    public static bool IsIntegrated { get; } = Environment.OSVersion.Platform == PlatformID.Win32NT && Environment.OSVersion.Version.Major >= 10 && Environment.OSVersion.Version.Build >= 22621;

    public static readonly DependencyProperty CaptionHeightProperty = DependencyProperty.RegisterAttached(
        "CaptionHeight", typeof(double), typeof(WindowFrame), new PropertyMetadata(0.0, CaptionHeightChanged));

    private static readonly DependencyPropertyKey CaptionButtonsWidthKey = DependencyProperty.RegisterAttachedReadOnly(
        "CaptionButtonsWidth", typeof(double), typeof(WindowFrame), new PropertyMetadata(0.0));

    public static readonly DependencyProperty CaptionButtonsWidthProperty = CaptionButtonsWidthKey.DependencyProperty;

    public static double GetCaptionHeight(DependencyObject element) => (double)element.GetValue(CaptionHeightProperty);
    public static void SetCaptionHeight(DependencyObject element, double value) => element.SetValue(CaptionHeightProperty, value);
    public static double GetCaptionButtonsWidth(DependencyObject element) => (double)element.GetValue(CaptionButtonsWidthProperty);

    private static readonly ConditionalWeakTable<Window, Frame> Frames = new();

    /// <summary>
    /// At startup, before the first window: the chrome brushes become transparent where Mica shows through (otherwise
    /// they keep the Theme.xaml colours), and scroll bars stay usable inside the resize border at the window edge.
    /// </summary>
    public static void Initialize(ResourceDictionary resources)
    {
        if (!IsIntegrated) return;
        resources["ChromeSidebar"] = Brushes.Transparent;
        resources["ChromeBand"] = Brushes.Transparent;
        // The resize border lies inside the window with WindowChrome; a scroll bar at the right edge would lose most of
        // its 10 px to it otherwise.
        EventManager.RegisterClassHandler(typeof(ScrollBar), FrameworkElement.LoadedEvent,
            new RoutedEventHandler((sender, _) => WindowChrome.SetIsHitTestVisibleInChrome((ScrollBar)sender, true)));
    }

    private static void CaptionHeightChanged(DependencyObject element, DependencyPropertyChangedEventArgs args)
    {
        if (!IsIntegrated || element is not Window window || DesignerProperties.GetIsInDesignMode(window)) return;
        if (Frames.TryGetValue(window, out var frame)) frame.Apply();
        else if ((double)args.NewValue > 0) Frames.Add(window, new Frame(window));
    }

    private sealed class Frame
    {
        private readonly Window window;
        private IntPtr handle;

        public Frame(Window window)
        {
            this.window = window;
            window.SourceInitialized += SourceInitialized;
            window.Loaded += Loaded;
            window.StateChanged += Changed;
            window.SizeChanged += Changed;
            window.DpiChanged += DpiChanged;
            window.Closed += Closed;
            if (new WindowInteropHelper(window).Handle != IntPtr.Zero) SourceInitialized(window, EventArgs.Empty);
        }

        private bool Resizable => window.ResizeMode is ResizeMode.CanResize or ResizeMode.CanResizeWithGrip;

        private void SourceInitialized(object? sender, EventArgs args)
        {
            handle = new WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;
            Apply();
            window.Background = Brushes.Transparent;
            if (HwndSource.FromHwnd(handle)?.CompositionTarget is { } target) target.BackgroundColor = Colors.Transparent;
            var light = 0;
            _ = DwmSetWindowAttribute(handle, UseImmersiveDarkMode, ref light, sizeof(int));
            var mica = BackdropMainWindow;
            _ = DwmSetWindowAttribute(handle, SystemBackdropType, ref mica, sizeof(int));
            // SizeToContent was measured with the standard frame before the chrome removed it; measuring again avoids
            // an unpainted strip at the bottom and right.
            if (window.SizeToContent != SizeToContent.Manual)
            {
                var mode = window.SizeToContent;
                window.SizeToContent = SizeToContent.Manual;
                window.SizeToContent = mode;
            }
            Update();
        }

        /// <summary>Creates or updates the chrome (also when CaptionHeight changes on a shown window).</summary>
        public void Apply()
        {
            if (handle == IntPtr.Zero) return;
            var height = GetCaptionHeight(window);
            if (height <= 0)
            {
                WindowChrome.SetWindowChrome(window, null);
                window.ClearValue(Window.BackgroundProperty);
                return;
            }
            var resize = Resizable ? SystemParameters.WindowResizeBorderThickness : new Thickness(0);
            var chrome = WindowChrome.GetWindowChrome(window) ?? new WindowChrome();
            chrome.GlassFrameThickness = WindowChrome.GlassFrameCompleteThickness;
            chrome.UseAeroCaptionButtons = true;
            chrome.CornerRadius = new CornerRadius(0);
            chrome.NonClientFrameEdges = NonClientFrameEdges.None;
            chrome.ResizeBorderThickness = resize;
            // WindowChrome puts the caption below the top resize border; the band is measured from the top edge.
            chrome.CaptionHeight = Math.Max(0, height - resize.Top);
            if (WindowChrome.GetWindowChrome(window) == null) WindowChrome.SetWindowChrome(window, chrome);
        }

        private void Changed(object? sender, EventArgs args) => Update();

        /// <summary>A window that dropped WS_SYSMENU after the chrome was applied (the setup) recomputes its frame, so
        /// the glass frame does not keep stale caption buttons.</summary>
        private void Loaded(object? sender, RoutedEventArgs args)
        {
            if (handle != IntPtr.Zero && WindowChrome.GetWindowChrome(window) != null && (GetWindowLong(handle, StyleIndex) & SystemMenu) == 0)
                _ = SetWindowPos(handle, IntPtr.Zero, 0, 0, 0, 0, FrameChanged | NoMove | NoSize | NoZOrder | NoActivate);
            Update();
        }

        private void DpiChanged(object? sender, DpiChangedEventArgs args) => Update();

        /// <summary>Maximized overhang and the caption-button width, after every state, size or DPI change.</summary>
        private void Update()
        {
            if (handle == IntPtr.Zero || WindowChrome.GetWindowChrome(window) == null) return;
            var dpi = VisualTreeHelper.GetDpi(window);
            // A maximized window reaches past the monitor by its (now invisible) frame; the border keeps content on screen.
            var border = new Thickness(0);
            if (window.WindowState == WindowState.Maximized)
            {
                var dots = (uint)Math.Round(96 * dpi.DpiScaleX);
                var padding = GetSystemMetricsForDpi(PaddedBorder, dots);
                var x = (GetSystemMetricsForDpi(SizeFrameX, dots) + padding) / dpi.DpiScaleX;
                var y = (GetSystemMetricsForDpi(SizeFrameY, dots) + padding) / dpi.DpiScaleY;
                if (x > 0 && y > 0) border = new Thickness(x, y, x, y);
            }
            if (window.BorderThickness != border) window.BorderThickness = border;
            // Maximized, the window's top edge sits off screen by border.Top and the content moves down by it: the
            // caption grows by the same amount so the whole band stays draggable.
            if (WindowChrome.GetWindowChrome(window) is { } chrome)
            {
                var caption = Math.Max(0, GetCaptionHeight(window) - chrome.ResizeBorderThickness.Top + border.Top);
                if (chrome.CaptionHeight != caption) chrome.CaptionHeight = caption;
            }

            var style = GetWindowLong(handle, StyleIndex);
            double width;
            if ((style & SystemMenu) == 0) width = 0;   // no caption buttons at all (the setup window)
            else if (DwmGetWindowAttribute(handle, CaptionButtonBounds, out var bounds, Marshal.SizeOf<Rect32>()) == 0
                     && bounds.Right > bounds.Left && GetWindowRect(handle, out var rect) && rect.Right - rect.Left >= bounds.Right)
                width = (rect.Right - rect.Left - bounds.Left) / dpi.DpiScaleX - border.Right;
            else width = (style & (MinimizeBox | MaximizeBox)) == 0 ? 46 : 138;
            width = Math.Clamp(Math.Ceiling(width), 0, 400);
            if (GetCaptionButtonsWidth(window) != width) window.SetValue(CaptionButtonsWidthKey, width);
        }

        private void Closed(object? sender, EventArgs args)
        {
            window.SourceInitialized -= SourceInitialized;
            window.Loaded -= Loaded;
            window.StateChanged -= Changed;
            window.SizeChanged -= Changed;
            window.DpiChanged -= DpiChanged;
            window.Closed -= Closed;
            Frames.Remove(window);
            handle = IntPtr.Zero;
        }
    }

    private const int UseImmersiveDarkMode = 20;     // DWMWA_USE_IMMERSIVE_DARK_MODE
    private const int SystemBackdropType = 38;       // DWMWA_SYSTEMBACKDROP_TYPE
    private const int BackdropMainWindow = 2;        // DWMSBT_MAINWINDOW (Mica)
    private const int CaptionButtonBounds = 5;       // DWMWA_CAPTION_BUTTON_BOUNDS
    private const int SizeFrameX = 32;               // SM_CXSIZEFRAME
    private const int SizeFrameY = 33;               // SM_CYSIZEFRAME
    private const int PaddedBorder = 92;             // SM_CXPADDEDBORDER
    private const int StyleIndex = -16;              // GWL_STYLE
    private const int SystemMenu = 0x00080000;       // WS_SYSMENU
    private const int MinimizeBox = 0x00020000;      // WS_MINIMIZEBOX
    private const int MaximizeBox = 0x00010000;      // WS_MAXIMIZEBOX
    private const uint NoSize = 0x0001, NoMove = 0x0002, NoZOrder = 0x0004, NoActivate = 0x0010, FrameChanged = 0x0020;

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect32 { public int Left, Top, Right, Bottom; }

    [DllImport("dwmapi.dll")] private static extern int DwmSetWindowAttribute(IntPtr window, int attribute, ref int value, int size);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out Rect32 value, int size);
    [DllImport("user32.dll")] private static extern int GetSystemMetricsForDpi(int index, uint dpi);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetWindowRect(IntPtr window, out Rect32 rect);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
}
