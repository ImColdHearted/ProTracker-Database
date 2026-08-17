using System.Collections.Generic;

namespace SharedPokemonLibrary;

public sealed class MapPokemonEncounter
{
    public string Pokemon { get; set; } = string.Empty;

    public List<string> TimePeriods { get; set; } = new();

    public int MinLevel { get; set; }

    public int MaxLevel { get; set; }

    public string? HeldItem { get; set; }
}