using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed record RouteButtonItem(string DisplayName, string RegionFolder, string FileName)
{
    public bool IsSelected { get; set; }
}

public sealed record EncounterSlotItem(string PokemonName, Bitmap? Sprite);

/// <summary>
/// Ported from Forms/Interactive Maps/KantoMapForm.cs, generalized so any region
/// with a SharedPokemonLibrary/Data/Regions/&lt;RegionFolder&gt; folder of Route*.json
/// files can reuse it (pass a different regionFolder/regionId to Load).
///
/// NOT ported: the clickable pin markers positioned directly on the map image
/// (MapMarker_Click + the Designer-placed marker controls). The original's pixel
/// coordinates came from the WinForms Designer and don't translate directly -
/// if you want them back, overlay Buttons on a Canvas/Grid on top of the map
/// Image using coordinates relative to the image size. The route list (left
/// panel) already gives full access to every route without needing the pins.
/// </summary>
public sealed partial class RegionMapViewModel : ViewModelBase
{
    [ObservableProperty] private string locationName = "Unknown Location";
    [ObservableProperty] private string locationType = string.Empty;
    [ObservableProperty] private string locationDescription = string.Empty;
    [ObservableProperty] private string? statusMessage;
    [ObservableProperty] private RouteButtonItem? selectedRoute;

    public ObservableCollection<RouteButtonItem> Routes { get; } = new();
    public ObservableCollection<EncounterSlotItem> Encounters { get; } = new();

    public void Load(string regionFolder)
    {
        Routes.Clear();

        string path = Path.Combine(AppContext.BaseDirectory, "SharedPokemonLibrary", "Data", "Regions", regionFolder);

        if (!Directory.Exists(path))
        {
            StatusMessage = $"The {regionFolder} route folder could not be found:\n{path}";
            return;
        }

        var routeFiles = Directory.GetFiles(path, "Route*.json").OrderBy(GetRouteNumber);

        foreach (string filePath in routeFiles)
        {
            string fileName = Path.GetFileName(filePath);
            string displayName = Path.GetFileNameWithoutExtension(filePath);

            Routes.Add(new RouteButtonItem(FormatRouteName(displayName), regionFolder, fileName));
        }
    }

    [RelayCommand]
    private void SelectRoute(RouteButtonItem route)
    {
        if (SelectedRoute is not null)
            SelectedRoute.IsSelected = false;

        route.IsSelected = true;
        SelectedRoute = route;

        try
        {
            var routeGroups = RouteRepository.Load(route.RegionFolder, route.FileName);
            DisplayRoute(routeGroups);
        }
        catch (Exception ex)
        {
            StatusMessage = ex.ToString();
        }
    }

    private void DisplayRoute(List<RouteEncounterGroup> routeGroups)
    {
        if (routeGroups.Count == 0)
        {
            ClearLocation();
            return;
        }

        var firstGroup = routeGroups[0];

        LocationName = firstGroup.DisplayName;
        LocationType = "Route";
        LocationDescription = string.Join(", ", routeGroups.Select(g =>
            g.RequiresMembership ? $"{g.Method} (Membership)" : g.Method));

        var allEncounters = routeGroups.SelectMany(g => g.Encounters).ToList();
        LoadEncounters(allEncounters);
    }

    private void ClearLocation()
    {
        LocationName = "Unknown Location";
        LocationType = string.Empty;
        LocationDescription = string.Empty;
        Encounters.Clear();
    }

    private void LoadEncounters(IReadOnlyList<RouteEncounter> encounters)
    {
        Encounters.Clear();

        foreach (var encounter in encounters)
        {
            var spriteEntry = SpriteMapRepository.FindByName(encounter.Pokemon);
            int dexNumber = spriteEntry?.DexNumber ?? 0;

            Encounters.Add(new EncounterSlotItem(encounter.Pokemon, LoadSprite(dexNumber)));
        }
    }

    private static Bitmap? LoadSprite(int dexNumber)
    {
        if (dexNumber <= 0)
            return null;

        string fullPath = Path.Combine(AppContext.BaseDirectory, "SharedPokemonLibrary", "Assets", "Sprites", $"{dexNumber}.png");
        return File.Exists(fullPath) ? new Bitmap(fullPath) : null;
    }

    private static int GetRouteNumber(string filePath)
    {
        string name = Path.GetFileNameWithoutExtension(filePath);
        string numberText = new string(name.Where(char.IsDigit).ToArray());
        return int.TryParse(numberText, out int number) ? number : int.MaxValue;
    }

    private static string FormatRouteName(string fileNameWithoutExtension)
    {
        string numberText = new string(fileNameWithoutExtension.Where(char.IsDigit).ToArray());
        return string.IsNullOrWhiteSpace(numberText) ? fileNameWithoutExtension : $"Route {numberText}";
    }
}
