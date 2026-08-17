using Foot_Tracker.Models;
using System.Text.Json;

namespace Foot_Tracker.Services;

public static class CaptureRegionLoader
{
    private static readonly JsonSerializerOptions Options =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    public static IReadOnlyDictionary<string, CaptureRegion> Load(
        string jsonPath)
    {
        if (!File.Exists(jsonPath))
        {
            throw new FileNotFoundException(
                "The capture-region file was not found.",
                jsonPath);
        }

        string json = File.ReadAllText(jsonPath);

        List<CaptureRegion> regions =
            JsonSerializer.Deserialize<List<CaptureRegion>>(
                json,
                Options)
            ?? throw new InvalidDataException(
                "The capture-region file was empty or invalid.");

        return regions.ToDictionary(
            region => region.Name,
            StringComparer.OrdinalIgnoreCase);
    }
}