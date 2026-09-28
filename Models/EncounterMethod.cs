using System;
using System.Collections.Generic;
using Avalonia.Media;
using Foot_Tracker.Services;

namespace Foot_Tracker.Models;

/// <summary>
/// §410. The three ways a species is met on a map, as the published pages
/// record them (§397): on land, on the water, or by rod - the two icon
/// facts of a Pokédex row and their absence. There is no cave, headbutt
/// or special method in the data, so there is none here; a boss is a pin,
/// not a method.
/// </summary>
public enum EncounterMethod
{
    Grass,
    Surf,
    Fishing,
}

/// <summary>§410. What the Maps window says and shows for each method:
/// one name, one colour (the type colours §407 already used - grass,
/// water, ice), and the list a page row breaks into.</summary>
public static class EncounterMethods
{
    public static readonly IReadOnlyList<EncounterMethod> All = new[]
    {
        EncounterMethod.Grass, EncounterMethod.Surf, EncounterMethod.Fishing,
    };

    /// <summary>The methods a page row lists, in the order its text has
    /// them: Grass, Surf, or both; a rod when neither.</summary>
    public static IReadOnlyList<EncounterMethod> Of(SpawnPokemon row)
    {
        if (row.Fishing)
            return new[] { EncounterMethod.Fishing };

        var methods = new List<EncounterMethod>(2);

        if (row.Land)
            methods.Add(EncounterMethod.Grass);

        if (row.Water)
            methods.Add(EncounterMethod.Surf);

        return methods;
    }

    public static bool Has(SpawnPokemon row, EncounterMethod method) => method switch
    {
        EncounterMethod.Grass => row.Land,
        EncounterMethod.Surf => row.Water,
        EncounterMethod.Fishing => row.Fishing,
        _ => false,
    };

    public static string Name(EncounterMethod method) => method switch
    {
        EncounterMethod.Grass => "Grass",
        EncounterMethod.Surf => "Surf",
        EncounterMethod.Fishing => "Fishing",
        _ => string.Empty,
    };

    /// <summary>The method by its name, case-insensitive; null for any
    /// other text - "Any method" among them.</summary>
    public static EncounterMethod? Parse(string? name)
    {
        foreach (EncounterMethod method in All)
        {
            if (string.Equals(Name(method), name?.Trim(), StringComparison.OrdinalIgnoreCase))
                return method;
        }

        return null;
    }

    public static Color Colour(EncounterMethod method) => PokemonTypeColors.For(method switch
    {
        EncounterMethod.Grass => "Grass",
        EncounterMethod.Surf => "Water",
        _ => "Ice",
    });
}
