using System.Text.Json;

namespace SharedPokemonLibrary;

public static class PokemonLibraryLoader
{
    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    public static IReadOnlyList<PokemonLibraryEntry> Load(string jsonPath)
    {
        if (string.IsNullOrWhiteSpace(jsonPath))
            throw new ArgumentException("A JSON path is required.", nameof(jsonPath));

        if (!File.Exists(jsonPath))
            throw new FileNotFoundException("The Pokémon library file was not found.", jsonPath);

        string json = File.ReadAllText(jsonPath);

        return JsonSerializer.Deserialize<List<PokemonLibraryEntry>>(json, JsonOptions)
            ?? throw new InvalidDataException("The Pokémon library JSON was empty or invalid.");
    }

    public static PokemonLibraryEntry? FindByName(
        IEnumerable<PokemonLibraryEntry> entries,
        string scannedName)
    {
        if (string.IsNullOrWhiteSpace(scannedName))
            return null;

        string normalized = Normalize(scannedName);

        return entries.FirstOrDefault(entry =>
            Normalize(entry.Name) == normalized ||
            Normalize(entry.Identifier) == normalized ||
            entry.OcrAliases.Any(alias => Normalize(alias) == normalized));
    }

    public static string GetSpritePath(
        string spriteDirectory,
        PokemonLibraryEntry pokemon)
    {
        ArgumentNullException.ThrowIfNull(pokemon);
        return Path.Combine(spriteDirectory, pokemon.Sprite);
    }

    private static string Normalize(string value) =>
        new(value
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
}
