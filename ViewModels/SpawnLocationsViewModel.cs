using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>§403. One map a species spawns on, as the Spawn Locations
/// window lists it: the published page, its row for the species, and
/// whether the map has a box on the region picture to point at.</summary>
public sealed record SpawnLocationRow(SpawnMap Map, string Title, string Detail, bool HasBox);

/// <summary>
/// §403. Where a species spawns, on the picture: the Search's "Locations"
/// answer. Every published map whose page lists the species (§397), on
/// the world picture (§406 - the same pixels the admin drew on) with the
/// boxes of those maps drawn and flashing, and the list beside it, by
/// region - a map without a box is listed too, with a note, so the answer
/// is whole even before every map is drawn.
///
/// Read-only: the boxes come from the published pages, the picture from
/// DataFiles/RegionMaps. The window does the pointer work (pan, zoom,
/// scrolling a box into view); this owns what is shown. Clicking a map in
/// the list narrows the flashing to its boxes; clicking it again, or
/// nothing, widens it back to every box on the picture.
/// </summary>
public sealed partial class SpawnLocationsViewModel : ViewModelBase, IDisposable
{
    public static readonly double[] ZoomSteps = { 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8 };

    /// <summary>How long the found boxes flash after the window opens or
    /// the choice changes, and how fast.</summary>
    private const int FlashTicks = 8;

    private readonly DispatcherTimer flashTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private int flashTicksLeft;

    public string Species { get; }

    public Bitmap? Sprite { get; }

    public string Title => $"Where {Species} spawns";

    [ObservableProperty] private Bitmap? image;

    [ObservableProperty] private int imagePixelWidth;

    [ObservableProperty] private int imagePixelHeight;

    [ObservableProperty] private bool hasImage;

    [ObservableProperty] private double zoom = 1;

    [ObservableProperty] private double canvasWidth;

    [ObservableProperty] private double canvasHeight;

    [ObservableProperty] private string zoomText = "100%";

    /// <summary>Every map the species is listed on, by region then name
    /// (§406: one picture holds them all).</summary>
    public ObservableCollection<SpawnLocationRow> Rows { get; } = new();

    [ObservableProperty] private SpawnLocationRow? selectedRow;

    /// <summary>The boxes drawn: every box of every listed map with one,
    /// and which row each belongs to, in step.</summary>
    public ObservableCollection<MapBoxShape> Shapes { get; } = new();

    private readonly List<SpawnLocationRow> shapeOwners = new();

    /// <summary>The indices in <see cref="Shapes"/> drawn as found: the
    /// selected row's boxes, or all of them when no row is selected.</summary>
    public IReadOnlyList<int> Highlighted { get; private set; } = Array.Empty<int>();

    public bool FlashOn { get; private set; }

    [ObservableProperty] private string status = string.Empty;

    /// <summary>Raised when the layer should redraw. The window subscribes.</summary>
    public event Action? Redraw;

    /// <summary>Raised with a box the window should bring into view.</summary>
    public event Action<MapBoxShape>? BringIntoView;

    public SpawnLocationsViewModel(string species)
    {
        Species = species.Trim();
        // §425: a regional form's sprite too - see MainWindowViewModel.
        Sprite = PokemonSpriteService.GetEncounterSprite(Species);

        flashTimer.Tick += OnFlashTick;

        LoadPicture();
        Rebuild();
        SpawnDataService.Changed += OnSpawnsChanged;

        // The copy in hand shows at once; the server's list follows.
        _ = RefreshQuietlyAsync();
    }

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await SpawnDataService.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Spawn locations: the published list could not be refreshed.");
        }
    }

    private void OnSpawnsChanged() => Dispatcher.UIThread.Post(Rebuild);

    partial void OnZoomChanged(double value)
    {
        CanvasWidth = ImagePixelWidth * value;
        CanvasHeight = ImagePixelHeight * value;
        ZoomText = $"{Math.Round(value * 100)}%";
        Redraw?.Invoke();
    }

    partial void OnSelectedRowChanged(SpawnLocationRow? value)
    {
        SetHighlight();
        StartFlashing();

        if (value is not null)
        {
            int first = shapeOwners.IndexOf(value);

            if (first >= 0)
                BringIntoView?.Invoke(Shapes[first]);
        }
    }

    // -------------------------------------------------------------- data

    /// <summary>Every published map whose page lists the species.</summary>
    private List<SpawnMap> MapsForSpecies() =>
        SpawnDataService.Current
            .Where(m => m.Pokemon.Any(p => string.Equals(p.Name, Species, StringComparison.OrdinalIgnoreCase)))
            .ToList();

    /// <summary>The rows and boxes, from the copy in hand - on open and
    /// whenever the copy changes.</summary>
    private void Rebuild()
    {
        List<SpawnMap> maps = MapsForSpecies();

        int regions = maps.Select(m => m.Region).Distinct(StringComparer.OrdinalIgnoreCase).Count();
        int boxed = maps.Count(m => m.HasBoxes);
        string mapWord = maps.Count == 1 ? "map" : "maps";
        string hasWord = boxed == 1 ? "has" : "have";
        string across = regions > 1 ? $" across {regions} regions" : string.Empty;

        Status = maps.Count == 0
            ? $"No published map lists {Species} yet."
            : $"{Species} is listed on {DisplayNumber.Count(maps.Count)} published {mapWord}{across}; {DisplayNumber.Count(boxed)} {hasWord} a box on the picture.";

        SelectedRow = null;
        RebuildRows(maps);
    }

    /// <summary>§406. The world picture - the one every box is drawn on.</summary>
    private void LoadPicture()
    {
        string path = RegionMaps.WorldPath;

        try
        {
            if (File.Exists(path))
            {
                var bitmap = new Bitmap(path);
                Image = bitmap;
                ImagePixelWidth = bitmap.PixelSize.Width;
                ImagePixelHeight = bitmap.PixelSize.Height;
            }
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Spawn locations: the world picture could not be read from {Path}.", path);
        }

        HasImage = Image is not null && ImagePixelWidth > 0 && ImagePixelHeight > 0;

        OnZoomChanged(Zoom);
    }

    private void RebuildRows(List<SpawnMap> maps)
    {
        Rows.Clear();
        Shapes.Clear();
        shapeOwners.Clear();

        List<string> order = SpawnRegions.All.ToList();

        foreach (SpawnMap map in maps.OrderBy(m => order.IndexOf(m.Region)).ThenBy(m => m.Map, StringComparer.OrdinalIgnoreCase))
        {
            SpawnPokemon? me = map.Pokemon.FirstOrDefault(p => string.Equals(p.Name, Species, StringComparison.OrdinalIgnoreCase));
            List<MapMarker> boxes = map.Markers.Where(m => m.IsValid).ToList();

            string detail = (me is null ? string.Empty : $"{me.MethodText} · {me.TimeText}" + (me.MembersOnly ? " · MS" : string.Empty))
                + (boxes.Count == 0 ? (me is null ? "Not drawn on the picture yet" : " · not drawn on the picture yet") : string.Empty);

            var row = new SpawnLocationRow(map, $"{map.Region} · {map.Map}", detail, boxes.Count > 0);
            Rows.Add(row);

            foreach (MapMarker box in boxes)
            {
                Shapes.Add(Place(map.Map, box));
                shapeOwners.Add(row);
            }
        }

        SetHighlight();
        StartFlashing();
    }

    /// <summary>A published box in this picture's pixels - by proportion
    /// when it was drawn on a picture of another size (§399).</summary>
    private MapBoxShape Place(string label, MapMarker marker)
    {
        if (!HasImage || (marker.ImageWidth == ImagePixelWidth && marker.ImageHeight == ImagePixelHeight))
            return new MapBoxShape(label, marker.X, marker.Y, marker.Width, marker.Height);

        double sx = ImagePixelWidth / (double)marker.ImageWidth;
        double sy = ImagePixelHeight / (double)marker.ImageHeight;

        return new MapBoxShape(
            label,
            (int)Math.Round(marker.X * sx),
            (int)Math.Round(marker.Y * sy),
            Math.Max(1, (int)Math.Round(marker.Width * sx)),
            Math.Max(1, (int)Math.Round(marker.Height * sy)));
    }

    /// <summary>The row whose box is under a picture pixel, if any - the
    /// smallest when boxes overlap.</summary>
    public SpawnLocationRow? RowAt(int x, int y)
    {
        SpawnLocationRow? best = null;
        long bestArea = long.MaxValue;

        for (int i = 0; i < Shapes.Count && i < shapeOwners.Count; i++)
        {
            MapBoxShape s = Shapes[i];

            if (x < s.X || y < s.Y || x >= s.X + s.Width || y >= s.Y + s.Height)
                continue;

            long area = (long)s.Width * s.Height;

            if (area < bestArea)
            {
                bestArea = area;
                best = shapeOwners[i];
            }
        }

        return best;
    }

    // --------------------------------------------------------- highlight

    private void SetHighlight()
    {
        Highlighted = SelectedRow is SpawnLocationRow row
            ? Enumerable.Range(0, shapeOwners.Count).Where(i => shapeOwners[i] == row).ToList()
            : Enumerable.Range(0, Shapes.Count).ToList();

        Redraw?.Invoke();
    }

    /// <summary>The found boxes blink for a few seconds, then stay lit.</summary>
    private void StartFlashing()
    {
        flashTicksLeft = FlashTicks;
        FlashOn = true;
        flashTimer.Stop();

        if (Shapes.Count > 0)
            flashTimer.Start();

        Redraw?.Invoke();
    }

    private void OnFlashTick(object? sender, EventArgs e)
    {
        if (--flashTicksLeft <= 0)
        {
            flashTimer.Stop();
            FlashOn = false;
        }
        else
        {
            FlashOn = !FlashOn;
        }

        Redraw?.Invoke();
    }

    // -------------------------------------------------------------- zoom

    [RelayCommand]
    private void ZoomIn() => StepZoom(+1);

    [RelayCommand]
    private void ZoomOut() => StepZoom(-1);

    public void StepZoom(int direction)
    {
        double current = Zoom;

        if (direction > 0)
        {
            foreach (double step in ZoomSteps)
            {
                if (step > current + 0.0001)
                {
                    Zoom = step;
                    return;
                }
            }
        }
        else
        {
            for (int i = ZoomSteps.Length - 1; i >= 0; i--)
            {
                if (ZoomSteps[i] < current - 0.0001)
                {
                    Zoom = ZoomSteps[i];
                    return;
                }
            }
        }
    }

    /// <summary>The largest step at which the whole picture fits.</summary>
    public void FitTo(double viewportWidth, double viewportHeight)
    {
        if (!HasImage || viewportWidth <= 0 || viewportHeight <= 0)
            return;

        double best = ZoomSteps[0];

        foreach (double step in ZoomSteps)
        {
            if (ImagePixelWidth * step <= viewportWidth && ImagePixelHeight * step <= viewportHeight)
                best = step;
        }

        Zoom = best;
    }

    public void Dispose()
    {
        flashTimer.Stop();
        flashTimer.Tick -= OnFlashTick;
        SpawnDataService.Changed -= OnSpawnsChanged;
    }
}
