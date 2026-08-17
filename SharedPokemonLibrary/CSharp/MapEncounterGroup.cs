using System.Collections.Generic;

namespace SharedPokemonLibrary;

public sealed class MapEncounterGroup
{
    public string Id { get; set; } = string.Empty;

    public string MapId { get; set; } = string.Empty;

    public string Region { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    public string Method { get; set; } = string.Empty;

    public bool RequiresMembership { get; set; }

    public List<MapPokemonEncounter> Encounters { get; set; } = new();
}