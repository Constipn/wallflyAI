using System.Collections;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Wallfly.App.Controls;

/// <summary>
/// Visible for true, a non-empty string, a non-empty collection or any other non-null value; Collapsed otherwise.
/// <c>ConverterParameter=not</c> inverts the result.
/// </summary>
public sealed class VisibleWhen : IValueConverter
{
    public static bool Truthy(object? value) => value switch
    {
        null => false,
        bool flag => flag,
        string text => text.Length > 0,
        int number => number != 0,
        ICollection collection => collection.Count > 0,
        _ => true
    };

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = Truthy(value) ^ string.Equals(parameter as string, "not", StringComparison.OrdinalIgnoreCase);
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>Negates a boolean (IsEnabled from a "busy" flag).</summary>
public sealed class Not : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>
/// Radio buttons over an enum: checked when the value's name equals the parameter (a comma-separated list matches any
/// of its names). Bound to <see cref="Visibility"/> it shows the element for a match.
/// </summary>
public sealed class EnumIs : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var name = value?.ToString();
        var match = name != null && (parameter as string ?? "").Split(',').Any(item => string.Equals(item.Trim(), name, StringComparison.Ordinal));
        return targetType == typeof(Visibility) ? match ? Visibility.Visible : Visibility.Collapsed : match;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true && parameter is string name ? Enum.Parse(targetType, name) : Binding.DoNothing;
}

/// <summary>A height in device-independent pixels to a <see cref="GridLength"/> (0 collapses the row).</summary>
public sealed class PixelLength : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is double pixels ? new GridLength(Math.Max(0, pixels)) : new GridLength(0);

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}

/// <summary>A top spacing in pixels to a <see cref="Thickness"/> (chat turns: 0, 4 or 14).</summary>
public sealed class TopMargin : IValueConverter
{
    public static TopMargin Instance { get; } = new();
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => new Thickness(0, value is double top ? top : 0, 0, 0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
