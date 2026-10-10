// Velotext: small value converters used by the XAML views.
// Copyright (c) 2026 Velotext contributors. Licensed under the MIT License.

using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Velotext.App.Views;

/// <summary>Converts a boolean to <see cref="Visibility"/>. Set <see cref="Invert"/> to show on false.</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    /// <summary>Show when the value is false instead of true.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is true) != Invert ? Visibility.Visible : Visibility.Collapsed;

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        (value is Visibility.Visible) != Invert;
}

/// <summary>Converts <c>null</c> to collapsed and anything else to visible (or the reverse with <see cref="Invert"/>).</summary>
public sealed class NullToVisibilityConverter : IValueConverter
{
    /// <summary>Show when the value is null instead of non-null.</summary>
    public bool Invert { get; set; }

    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var hasValue = value is not null && (value is not string text || text.Length > 0);
        return hasValue != Invert ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <inheritdoc />
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Converts an enum value to its integer (for ComboBox.SelectedIndex) and back.</summary>
public sealed class EnumIndexConverter : IValueConverter
{
    /// <inheritdoc />
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Enum e ? System.Convert.ToInt32(e, CultureInfo.InvariantCulture) : 0;

    /// <inheritdoc />
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var enumType = Nullable.GetUnderlyingType(targetType) ?? targetType;
        return value is int index && index >= 0 ? Enum.ToObject(enumType, index) : Binding.DoNothing;
    }
}
