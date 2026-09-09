using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Foot_Tracker.Converters;

// Backs PreviouslyBattledUsersWindow.axaml's export-status TextBlock -
// Avalonia.Controls.StringConverters.IsNotNullOrEmpty (used first) doesn't
// resolve against this project's Avalonia 12.1.1 reference, so this is a
// tiny local replacement following the exact same IValueConverter + static
// Instance pattern as ColorToBrushConverter.cs in this same folder.
public sealed class StringNotEmptyConverter : IValueConverter
{
    public static readonly StringNotEmptyConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return !string.IsNullOrEmpty(value as string);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
