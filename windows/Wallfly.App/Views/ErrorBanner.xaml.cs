using System;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Wallfly.App.ViewModels;

namespace Wallfly.App.Views;

/// <summary>The error banner at the bottom of the detail column: the first line of the message, the rest via "Copy details".</summary>
public partial class ErrorBanner : UserControl
{
    public ErrorBanner() => InitializeComponent();

    private void CopyDetails_Click(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AppViewModel { ErrorText.Length: > 0 } app) return;
        try { Clipboard.SetText(app.ErrorText); }
        catch (System.Runtime.InteropServices.ExternalException) { /* the clipboard is held by another process; nothing to report */ }
    }
}

/// <summary>Shows only the first non-empty line of a multi-line message; the raw detail stays behind "Copy details".</summary>
public sealed class FirstLineConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value as string)?.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? "";

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
