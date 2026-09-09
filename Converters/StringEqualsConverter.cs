using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Foot_Tracker.Converters;

// Backs MainWindow.axaml's Since Form Sound/Since Shiny Sound File-menu
// submenus - each option's IsChecked reflects whether the bound
// sound-selection string (SinceFormSound/SinceShinySound) equals that
// option's own ConverterParameter ("None"/"Yay"/"Shiny"), so the
// currently-selected sound shows a checkmark without a separate bool
// property per option. One-way only (see ConvertBack) - an option's actual
// selection change goes through its own Command/CommandParameter instead
// (SetSinceFormSound/SetSinceShinySoundCommand), following the exact same
// IValueConverter + static Instance pattern as EnumDisplayNameConverter.cs
// and ColorToBrushConverter.cs in this same folder. Works with any two
// strings, not just sound names.
public sealed class StringEqualsConverter : IValueConverter
{
    public static readonly StringEqualsConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return string.Equals(value as string, parameter as string, StringComparison.Ordinal);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
