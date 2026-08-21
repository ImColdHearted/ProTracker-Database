using Foot_Tracker.Models;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Linq;

public static class RouteRepository
{
    public static List<RouteEncounterGroup> Load(
        string region,
        string fileName)
    {
        string filePath = Path.Combine(
            AppContext.BaseDirectory,
            "SharedPokemonLibrary",
            "Data",
            "Regions",
            region,
            fileName);

        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException(
                $"The route file could not be found:\n{filePath}",
                filePath);
        }

        string json = File.ReadAllText(filePath);

        JsonSerializerOptions options = new()
        {
            PropertyNameCaseInsensitive = true
        };

        List<RouteEncounterGroup>? routeGroups =
            JsonSerializer.Deserialize<List<RouteEncounterGroup>>(
                json,
                options);

        if (routeGroups == null)
        {
            throw new InvalidDataException(
                $"The route file '{fileName}' could not be read.");
        }

        return routeGroups;
    }
}