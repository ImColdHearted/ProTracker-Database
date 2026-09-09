using System;
using System.Globalization;
using Avalonia.Data.Converters;
using Foot_Tracker.Services;

namespace Foot_Tracker.Converters;

// Maps a Pokemon type name (e.g. "Fire") to its icon from
// SharedPokemonLibrary/Assets/Typings/*.png - backs the small type-icon rows
// added next to Pokemon names throughout the app (see each ViewModel's Types
// property, e.g. TargetDisplayItem.Types). Follows the exact same
// IValueConverter + static Instance pattern as ColorToBrushConverter.cs/
// StringNotEmptyConverter.cs in this same folder.
public sealed class TypeIconConverter : IValueConverter
{
    public static readonly TypeIconConverter Instance = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        return value is string typeName ? PokemonSpriteService.GetTypeIcon(typeName) : null;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        throw new NotSupportedException();
    }
}
