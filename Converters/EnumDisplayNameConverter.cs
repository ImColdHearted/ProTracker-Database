using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Foot_Tracker.Services;

namespace Foot_Tracker.Converters;

// Backs CreateEventWindow.axaml's Type ComboBox - shows each GuildEventType
// value as a spaced-out label ("Community Hunting" instead of the raw
// "CommunityHunting") via EnumFormatHelper.ToDisplayName, following the exact
// same IValueConverter + static Instance pattern as StringNotEmptyConverter.cs
// and ColorToBrushConverter.cs in this same folder. Works on any enum (calls
// ToString() on whatever's bound), not just GuildEventType.
public sealed class EnumDisplayNameConverter : IValueConverter
{
    public static readonly EnumDisplayNameConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is null ? string.Empty : EnumFormatHelper.ToDisplayName(value.ToString() ?? string.Empty);
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
