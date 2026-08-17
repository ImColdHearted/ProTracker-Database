using System.Text.Json;

namespace SharedPokemonLibrary;

public static class MapEncounterLoader
{
    private static readonly JsonSerializerOptions Options =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    public static IReadOnlyList<MapEncounterGroup> Load(string jsonPath)
    {
        if (!File.Exists(jsonPath))
            throw new FileNotFoundException(jsonPath);

        string json = File.ReadAllText(jsonPath);

        return JsonSerializer.Deserialize<List<MapEncounterGroup>>(json, Options)
               ?? new List<MapEncounterGroup>();
    }
}