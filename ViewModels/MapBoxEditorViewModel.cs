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

/// <summary>§399. One saved box in the editor's list - the map, which of
/// its boxes this is (§402: a map may have several), and where it sits,
/// as one line.</summary>
public sealed record MapBoxItem(SpawnMap Map, MapMarker Box, string Text);

/// <summary>
/// §399. The Map Boxes editor behind the Admin Console's Spawns section:
/// the world picture at any zoom (§406 - one picture for every region,
/// in place of one per region), every box already published, and one
/// being drawn - which, given a map's name, is published with that map's
/// page as its <see cref="MapMarker"/>.
///
/// The picture is DataFiles/RegionMaps/World.png, shipped with the build,
/// so every tracker that later draws the map has the very pixels the
/// boxes were drawn on. A box is kept in the picture's own pixels along
/// with the picture's size; a box published on a picture of another size
/// is placed here by proportion and marked as such, and saving it again
/// re-measures it against the picture in hand. The page a map goes on is
/// the one it already has, or else the catalog's region for the name.
///
/// Publishing is the same request the Spawns section makes (master token
/// only, the page composed from this machine's scans), with the box
/// attached; a map that is not published yet is published by its first
/// box. Removing a box republishes the map without one - the page stays.
///
/// The window owns the pointer work (dragging a box out, picking one by
/// clicking it, panning, zooming); this owns what is shown and what is
/// sent - including which of the two the left button does (§400:
/// <see cref="DrawMode"/>), and the order boxes were saved in this window,
/// which is what <see cref="RemoveLast"/> walks back through.
/// </summary>
public sealed partial class MapBoxEditorViewModel : ViewModelBase, IDisposable
{
    /// <summary>Screen pixels per picture pixel, the steps the zoom
    /// buttons and Ctrl+wheel move through.</summary>
    public static readonly double[] ZoomSteps = { 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8 };

    /// <summary>§400. What the left button does on the picture: draws a box
    /// (Draw) or moves the picture about (Pan). Two toggles in the toolbar,
    /// one of them always on - each turns the other off.</summary>
    [ObservableProperty] private bool drawMode = true;

    [ObservableProperty] private bool panMode;

    /// <summary>§409. The third mode: a click on the picture puts the
    /// chosen boss's pin there (a drag still pans). Draw, Pan and Boss are
    /// one choice; turning the active one off falls back to Draw.</summary>
    [ObservableProperty] private bool bossMode;

    /// <summary>§419. The fourth mode: a click puts a Pokéstop's pin there
    /// (a drag still pans). A Pokéstop is a pin like a boss's (§409), under
    /// an id beginning "Pokestop", so the server needs nothing new.</summary>
    [ObservableProperty] private bool pokestopMode;

    /// <summary>§419/§420. The Pokéstop's name - one of the catalog's stops,
    /// or anything typed. It names the stop being placed, or renames the
    /// selected one; it can be typed before or after the click.</summary>
    [ObservableProperty] private string pokestopPlace = string.Empty;

    /// <summary>§419. The stops the catalog lists, for the place box.</summary>
    public IReadOnlyList<string> PokestopChoices { get; } =
        PokestopCatalogService.All.Select(s => s.Name).ToList();

    [ObservableProperty] private string pokestopCountText = string.Empty;

    /// <summary>§412/§416. The main map of a link - the one whose box the
    /// spot is - typed or picked, or filled by clicking a box in any mode.
    /// §416: no Link mode any more; the link row is always there, beside
    /// the box row, so boxes and links are made in one place.</summary>
    [ObservableProperty] private string linkSpotName = string.Empty;

    /// <summary>§412. The main map, as published - null until the name is
    /// a published map with a box.</summary>
    [ObservableProperty] private SpawnMap? linkSpot;

    [ObservableProperty] private string linkSpotText = "Main map: a map with a box. Secondary: a map to share its spot.";

    /// <summary>§412. The secondary map - the one to link to the main
    /// map's spot - typed or picked.</summary>
    [ObservableProperty] private string linkName = string.Empty;

    /// <summary>§416. What the main-map box offers: every published map
    /// that has a box.</summary>
    public ObservableCollection<string> SpotChoices { get; } = new();

    /// <summary>§412. The maps already linked to the spot, by name.</summary>
    public ObservableCollection<string> LinkedMaps { get; } = new();

    [ObservableProperty] private string? selectedLinked;

    /// <summary>§416. Both names typed: the press says what is wrong with
    /// them, rather than a button that stays grey without a reason.</summary>
    public bool CanLink => !Busy && LinkSpotName.Trim().Length > 0 && LinkName.Trim().Length > 0;

    public bool CanUnlink => !Busy && SelectedLinked is not null;

    /// <summary>§409. The bosses the catalog knows, to pin.</summary>
    public IReadOnlyList<BossInfo> BossChoices => BossCatalogService.All;

    [ObservableProperty] private BossInfo? selectedBoss;

    /// <summary>The pin being placed - the chosen boss at the clicked
    /// point; null until a click.</summary>
    [ObservableProperty] private BossPin? draftPin;

    [ObservableProperty] private string draftPinText = "Pick a boss, then click where it stands.";

    /// <summary>Every published pin as the layer draws it, and the pin
    /// each belongs to, in step.</summary>
    public ObservableCollection<MapPinShape> PinShapes { get; } = new();

    private readonly List<BossPin> pinOwners = new();

    [ObservableProperty] private BossPin? selectedPin;

    /// <summary>A draft to save - a stop's, or a boss's with a boss picked -
    /// or (§420) a stop selected to rename. A stop's missing name is said on
    /// the press rather than by a grey button.</summary>
    public bool CanSavePin =>
        !Busy && HasImage
        && ((DraftPin is { IsValid: true } draft && (PokestopCatalogService.IsPokestop(draft) || SelectedBoss is not null))
            || (PokestopMode && DraftPin is null && SelectedPin is not null && PokestopCatalogService.IsPokestop(SelectedPin)));

    public bool CanRemovePin => !Busy && SelectedPin is not null;

    [ObservableProperty] private Bitmap? image;

    [ObservableProperty] private int imagePixelWidth;

    [ObservableProperty] private int imagePixelHeight;

    [ObservableProperty] private bool hasImage;

    [ObservableProperty] private string imageMessage = string.Empty;

    [ObservableProperty] private double zoom = 1;

    [ObservableProperty] private double canvasWidth;

    [ObservableProperty] private double canvasHeight;

    [ObservableProperty] private string zoomText = "100%";

    /// <summary>The boxes published for the region, alphabetical by map.</summary>
    public ObservableCollection<MapBoxItem> Boxes { get; } = new();

    /// <summary>The same boxes as the layer draws them, in this picture's
    /// pixels.</summary>
    public ObservableCollection<MapBoxShape> Shapes { get; } = new();

    [ObservableProperty] private MapBoxItem? selectedBox;

    /// <summary>The box being made, in this picture's pixels; null when
    /// none has been dragged out yet.</summary>
    [ObservableProperty] private MapMarker? draft;

    [ObservableProperty] private string draftText = "Drag on the picture to draw a box.";

    /// <summary>The map the draft is for - typed, picked from the list,
    /// or set by clicking a saved box.</summary>
    [ObservableProperty] private string mapName = string.Empty;

    /// <summary>What the map box offers: the region's catalog names, and
    /// every map already published on this page.</summary>
    public ObservableCollection<string> MapChoices { get; } = new();

    [ObservableProperty] private string status =
        EventsSyncService.IsOnline ? "Wheel to zoom, drag to draw a box, name its map, Save." : "No events server configured - boxes cannot be published.";

    [ObservableProperty] private bool busy;

    public bool CanSave => !Busy && HasImage && Draft is { IsValid: true } && MapName.Trim().Length > 0;

    public bool CanRemove => !Busy && SelectedBox is not null;

    /// <summary>§400. The boxes saved from this window, oldest first, each
    /// with its map and region - what Remove last walks back through. A
    /// box removed (by any button) leaves the list; §402: the box itself is
    /// kept, since a map may have several and a republish hands back new
    /// objects, found again pixel for pixel.</summary>
    private readonly List<SavedBox> savedHere = new();

    private sealed record SavedBox(string Key, MapMarker Box);

    /// <summary>§400. Remove last has something to remove: the box being
    /// drawn, or failing that a box saved from this window that is still
    /// on the current region's page.</summary>
    public bool CanRemoveLast => !Busy && (Draft is { IsValid: true } || LastSavedHere() is not null);

    /// <summary>Set by the window - the events-server sign-in, the same
    /// window the console uses.</summary>
    public Func<Task<bool>>? RequestAdminSignIn { get; set; }

    /// <summary>Raised when the layer should redraw: the boxes, the draft,
    /// the selection or the zoom changed. The window subscribes.</summary>
    public event Action? Redraw;

    public MapBoxEditorViewModel()
    {
        LoadPicture();
        RebuildPins();
        SpawnDataService.Changed += OnSpawnsChanged;
        BossPinService.Changed += OnPinsChanged;
        _ = RefreshPinsQuietlyAsync();

        // §402: the copy in hand shows at once; the server's list follows
        // when it answers (the Changed event rebuilds the boxes), so a box
        // published elsewhere - or under an older build's shape - is here.
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
            Log.Debug(ex, "Map boxes: the published list could not be refreshed.");
        }
    }

    // §400. The two mode toggles are one choice: turning either on turns
    // the other off, and turning one off turns the other on - the
    // generated setters raise only on a change, so this settles at once.
    partial void OnDrawModeChanged(bool value) => ModeChanged(value, draw: true);

    partial void OnPanModeChanged(bool value) => ModeChanged(value, pan: true);

    partial void OnBossModeChanged(bool value)
    {
        // §419: a draft of the other kind does not carry across.
        if (value && DraftPin is not null && PokestopCatalogService.IsPokestop(DraftPin))
            DraftPin = null;

        ModeChanged(value, boss: true);
    }

    partial void OnPokestopModeChanged(bool value)
    {
        if (value && DraftPin is not null && !PokestopCatalogService.IsPokestop(DraftPin))
            DraftPin = null;

        ModeChanged(value, stop: true);
        OnDraftPinChanged(DraftPin);
        OnPropertyChanged(nameof(CanSavePin));
    }

    /// <summary>§419/§420. The name typed with a stop's draft down: the draft
    /// wears it at once. Its id stays - the id is the stop's, not the name's.</summary>
    partial void OnPokestopPlaceChanged(string value)
    {
        if (DraftPin is BossPin pin && PokestopCatalogService.IsPokestop(pin))
        {
            pin.Boss = value.Trim().Length > 0 ? PokestopCatalogService.PinNameFor(value) : PokestopCatalogService.DisplayName;
            OnDraftPinChanged(pin);
        }
    }

    /// <summary>§409. Three toggles, one choice: the one turned on turns the
    /// others off; the active one turned off leaves Draw on (or Pan, when
    /// Draw itself was turned off). The generated setters raise only on a
    /// change, so this settles at once.</summary>
    private void ModeChanged(bool on, bool draw = false, bool pan = false, bool boss = false, bool stop = false)
    {
        if (on)
        {
            if (!draw) DrawMode = false;
            if (!pan) PanMode = false;
            if (!boss) BossMode = false;
            if (!stop) PokestopMode = false;
            return;
        }

        if (!DrawMode && !PanMode && !BossMode && !PokestopMode)
        {
            if (draw) PanMode = true;
            else DrawMode = true;
        }
    }

    partial void OnSelectedBossChanged(BossInfo? value)
    {
        if (DraftPin is BossPin pin && value is not null)
        {
            pin.BossId = value.BossId;
            pin.Boss = value.Name;
            DraftPinText = $"{value.Name} at {pin.X},{pin.Y} - Save pin.";
            Redraw?.Invoke();
        }

        OnPropertyChanged(nameof(CanSavePin));
    }

    partial void OnDraftPinChanged(BossPin? value)
    {
        // §420: a stop's draft may still want its name.
        DraftPinText = value is not null
            ? PokestopCatalogService.IsPokestop(value) && PokestopPlace.Trim().Length == 0
                ? $"New Pokéstop at {value.X},{value.Y} - name it, then Save pin."
                : $"{value.Boss} at {value.X},{value.Y} - Save pin."
            : PokestopMode
                ? SelectedPin is BossPin picked && PokestopCatalogService.IsPokestop(picked)
                    ? $"Selected: {PokestopCatalogService.PlaceOf(picked)} - rename it and Save pin, or Remove pin."
                    : "Click where a Pokéstop stands, name it, Save pin - as many as you like."
                : "Pick a boss, then click where it stands.";

        OnPropertyChanged(nameof(CanSavePin));
        Redraw?.Invoke();
    }

    partial void OnSelectedPinChanged(BossPin? value)
    {
        // §419: a stop's pin names its place; a boss's pin its boss.
        if (value is not null && PokestopCatalogService.IsPokestop(value))
            PokestopPlace = PokestopCatalogService.PlaceOf(value);
        else if (value is not null && BossCatalogService.Find(value.BossId) is BossInfo boss)
            SelectedBoss = boss;

        OnPropertyChanged(nameof(CanSavePin));

        if (DraftPin is null)
            OnDraftPinChanged(null);

        OnPropertyChanged(nameof(CanRemovePin));
        Redraw?.Invoke();
    }

    partial void OnZoomChanged(double value)
    {
        CanvasWidth = ImagePixelWidth * value;
        CanvasHeight = ImagePixelHeight * value;
        ZoomText = $"{Math.Round(value * 100)}%";
        Redraw?.Invoke();
    }

    partial void OnDraftChanged(MapMarker? value)
    {
        DraftText = value is { IsValid: true }
            ? $"Box: {value.Describe} - name its map, then Save."
            : "Drag on the picture to draw a box.";

        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRemoveLast));
        Redraw?.Invoke();
    }

    partial void OnMapNameChanged(string value) => OnPropertyChanged(nameof(CanSave));

    /// <summary>§416. The main map typed or picked: resolved as it is typed,
    /// so the linked list fills as soon as the name is a map with a box.</summary>
    partial void OnLinkSpotNameChanged(string value)
    {
        RefreshLinks();
        OnPropertyChanged(nameof(CanLink));
    }

    partial void OnLinkNameChanged(string value) => OnPropertyChanged(nameof(CanLink));

    partial void OnSelectedLinkedChanged(string? value)
    {
        if (value is not null)
            LinkName = value;

        OnPropertyChanged(nameof(CanUnlink));
    }

    partial void OnBusyChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanRemove));
        OnPropertyChanged(nameof(CanRemoveLast));
        OnPropertyChanged(nameof(CanSavePin));
        OnPropertyChanged(nameof(CanRemovePin));
        OnPropertyChanged(nameof(CanLink));
        OnPropertyChanged(nameof(CanUnlink));
    }

    partial void OnHasImageChanged(bool value)
    {
        OnPropertyChanged(nameof(CanSave));
        OnPropertyChanged(nameof(CanSavePin));
    }

    partial void OnSelectedBoxChanged(MapBoxItem? value)
    {
        // §417: the box row's map follows the selection; the link row's
        // main map does not - see PickBox.
        if (value is not null)
            MapName = value.Map.Map;

        OnPropertyChanged(nameof(CanRemove));
        Redraw?.Invoke();
    }

    // ------------------------------------------------------------ picture

    private void LoadPicture()
    {
        Draft = null;
        SelectedBox = null;

        string path = RegionMaps.WorldPath;

        Image = null;
        ImagePixelWidth = 0;
        ImagePixelHeight = 0;

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
            Log.Warning(ex, "Map boxes: the world picture could not be read from {Path}.", path);
        }

        HasImage = Image is not null && ImagePixelWidth > 0 && ImagePixelHeight > 0;

        ImageMessage = HasImage
            ? $"World - {ImagePixelWidth}×{ImagePixelHeight} pixels."
            : $"No world picture. Put one at {path} and reopen this window.";

        // Re-derive the canvas for the new picture at the current zoom.
        OnZoomChanged(Zoom);

        FillMapChoices();
        RebuildBoxes();
    }

    private void FillMapChoices()
    {
        MapChoices.Clear();

        // §416: the main-map box offers the maps that have a box.
        SpotChoices.Clear();

        foreach (SpawnMap spot in SpawnDataService.Current
            .Where(m => m.HasBoxes && !m.IsLinked)
            .OrderBy(m => m.Map, StringComparer.OrdinalIgnoreCase))
        {
            SpotChoices.Add(spot.Map);
        }

        var names = new List<string>();

        // §406: every map, whatever its region - one picture holds them all.
        foreach (CatalogLocation location in LocationCatalogService.All)
            names.Add(location.Name);

        foreach (SpawnMap map in SpawnDataService.Current)
            names.Add(map.Map);

        foreach (string name in names.Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(n => n, StringComparer.OrdinalIgnoreCase))
            MapChoices.Add(name);
    }

    private void OnSpawnsChanged() => Dispatcher.UIThread.Post(() =>
    {
        FillMapChoices();
        RebuildBoxes();
        RefreshLinks();
    });

    // --------------------------------------------------------------- links

    /// <summary>§412. The spot's line and its linked maps, from the copy in
    /// hand - after a pick, and after every change to the published list.</summary>
    private void RefreshLinks()
    {
        string? keep = SelectedLinked;

        LinkedMaps.Clear();

        // The main map again from the current list - a box drawn or taken
        // down since it was typed changes whether it is a spot.
        LinkSpot = SpawnDataService.Find(LinkSpotName.Trim()) is { HasBoxes: true } fresh ? fresh : null;

        if (LinkSpot is SpawnMap spot)
        {
            IReadOnlyList<SpawnMap> linked = SpawnDataService.LinkedTo(spot);

            foreach (SpawnMap map in linked)
                LinkedMaps.Add(map.Map);

            string shown = SpawnDataService.SpotName(spot, linked);
            LinkSpotText = linked.Count == 0
                ? $"{spot.Map}: nothing linked yet."
                : $"{spot.Map}: shown as \"{shown}\", {linked.Count} linked.";
        }
        else
        {
            LinkSpotText = LinkSpotName.Trim().Length == 0
                ? "Main map: a map with a box. Secondary: a map to share its spot."
                : $"\"{LinkSpotName.Trim()}\" has no box yet - draw and save one, then link to it.";
        }

        SelectedLinked = keep is not null && LinkedMaps.Contains(keep) ? keep : null;
        OnPropertyChanged(nameof(CanLink));
        OnPropertyChanged(nameof(CanUnlink));
    }

    /// <summary>§417. A box clicked on the picture: selected, and - only when
    /// the link row's main map is still empty - named as the main map. A
    /// main map once set stays until it is typed over, picked, or cleared:
    /// saving a box selects the new box, and a click to look at a box, must
    /// never move the spot a run of floors is being linked to.</summary>
    public void PickBox(MapBoxItem? box)
    {
        SelectedBox = box;

        if (box is not null && LinkSpotName.Trim().Length == 0)
            LinkSpotName = box.Map.Map;
    }

    /// <summary>§412. Publishes the named map linked to the spot: it gets
    /// no box of its own and is found on the picture at the spot's box.
    /// A map not published yet is published by this, with its scans, as a
    /// first box publishes one.</summary>
    [RelayCommand]
    private async Task LinkMap()
    {
        string main = LinkSpotName.Trim();

        if (main.Length == 0)
        {
            Status = "Name the main map - the one with the box - or click its box.";
            return;
        }

        if (SpawnDataService.Find(main) is not SpawnMap spot)
        {
            Status = $"{main} is not published yet - draw its box and Save box first.";
            return;
        }

        if (!spot.HasBoxes)
        {
            Status = $"{spot.Map} has no box yet - draw one and Save box first.";
            return;
        }

        if (spot.IsLinked)
        {
            Status = $"{spot.Map} is itself linked to {spot.LinkedTo} - use that as the main map.";
            return;
        }

        string map = LinkName.Trim();
        string key = SpawnMap.KeyFor(map);

        if (key.Length == 0)
        {
            Status = "Name the secondary map - the one to share the spot.";
            return;
        }

        if (key == spot.Key)
        {
            Status = $"{spot.Map} is the main map itself.";
            return;
        }

        // §416: a secondary map with a box of its own gives the box up - the
        // spot is where it is found now. Said, so it is never a surprise.
        bool hadBoxes = SpawnDataService.Find(map) is { HasBoxes: true };

        await PublishLinkAsync(map, spot.Map,
            hadBoxes
                ? $"Linked {map} to {spot.Map}'s spot; its own box was taken down."
                : $"Linked {map} to {spot.Map}'s spot.");
    }

    /// <summary>§412. Republishes the selected linked map without its link;
    /// its page stays, off the picture until it gets a box or a link.</summary>
    [RelayCommand]
    private async Task Unlink()
    {
        if (SelectedLinked is not string map)
        {
            Status = "Pick a linked map first.";
            return;
        }

        await PublishLinkAsync(map, null, $"Unlinked {map}; its page stays.");
    }

    private async Task PublishLinkAsync(string map, string? spot, string success)
    {
        if (!await BeginPublishAsync(map))
            return;

        try
        {
            SpawnMap page = SpawnDataService.BuildFromPokedex(RegionFor(map), map);
            page.LinkedTo = spot;

            // §416: a linked map has no box of its own (a box would win over
            // the link, §412); unlinking leaves it as it is.
            if (spot is not null)
                page.Markers = new List<MapMarker>();

            // The boxes it had, in case the server cannot keep the link.
            List<MapMarker> had = SpawnDataService.Find(map)?.Markers.Where(m => m.IsValid).ToList() ?? new List<MapMarker>();

            SpawnMap saved = await EventsSyncService.SaveSpawnMapAsync(page);

            // §418: a server older than §412 stores the page and silently
            // drops the link - it has nowhere to keep one. Say so, and put
            // back any box the link took down, rather than report a link
            // that is not there.
            bool dropped = spot is not null && !saved.IsLinked;

            if (dropped && had.Count > 0)
            {
                page.LinkedTo = null;
                page.Markers = had;
                saved = await EventsSyncService.SaveSpawnMapAsync(page);
            }

            SpawnDataService.ApplyOne(saved);

            FillMapChoices();
            RebuildBoxes();
            RefreshLinks();

            if (dropped)
            {
                Status = $"The events server did not keep the link - it is running a build older than links (§412). Deploy worker.js (its /v1/health should say section 412 or later), then Link {map} again."
                    + (had.Count > 0 ? " Its own box was put back." : string.Empty);
                return;
            }

            LinkName = string.Empty;

            Status = saved.Pokemon.Count == 0 && spot is not null
                ? success + " Its page has no Pokémon yet - scan the Pokedex for it, then Republish it in the Spawns section."
                : success;
        }
        catch (EventsSyncException ex)
        {
            Status = DescribePublishFailure(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    private void OnPinsChanged() => Dispatcher.UIThread.Post(RebuildPins);

    // --------------------------------------------------------------- pins

    /// <summary>§409. Every published pin, placed on this picture - by
    /// proportion when placed on a picture of another size.</summary>
    private void RebuildPins()
    {
        string? keep = SelectedPin?.Key;

        PinShapes.Clear();
        pinOwners.Clear();

        foreach (BossPin pin in BossPinService.Current)
        {
            PinShapes.Add(PlacePin(pin));
            pinOwners.Add(pin);
        }

        SelectedPin = pinOwners.FirstOrDefault(p => p.Key == keep);

        // §419: how many of the catalog's stops are on the picture.
        int placed = pinOwners.Count(p => PokestopCatalogService.IsPokestop(p));
        PokestopCountText = $"{placed} placed ({PokestopCatalogService.All.Count} listed).";

        Redraw?.Invoke();
    }

    /// <summary>A pin as the layer draws it, with the boss's picture and
    /// its name as this machine's catalog has them (the pin's own name
    /// when the catalog lacks the boss).</summary>
    public MapPinShape PlacePin(BossPin pin)
    {
        // §419: a Pokéstop's label and sprite, or a boss's.
        string label = PokestopCatalogService.LabelFor(pin);
        Bitmap? image = PokestopCatalogService.PortraitFor(pin);
        bool stop = PokestopCatalogService.IsPokestop(pin);

        if (!HasImage || (pin.ImageWidth == ImagePixelWidth && pin.ImageHeight == ImagePixelHeight))
            return new MapPinShape(label, pin.X, pin.Y, image, stop);

        return new MapPinShape(
            label,
            (int)Math.Round(pin.X * (ImagePixelWidth / (double)pin.ImageWidth)),
            (int)Math.Round(pin.Y * (ImagePixelHeight / (double)pin.ImageHeight)),
            image,
            stop);
    }

    /// <summary>The published pin a shape belongs to.</summary>
    public BossPin? PinOwner(int shapeIndex) =>
        shapeIndex >= 0 && shapeIndex < pinOwners.Count ? pinOwners[shapeIndex] : null;

    /// <summary>A click in Boss mode: the chosen boss's pin at the picture
    /// pixel, clamped to the picture. Nothing until a boss is chosen.</summary>
    public void SetDraftPin(int x, int y)
    {
        if (!HasImage)
            return;

        // §419/§420: in Pokéstop mode, a new stop wherever the click is -
        // its own id, so there is no limit to how many; the name can come
        // before or after the click.
        if (PokestopMode)
        {
            // A name still in the box from a stop already placed - the one
            // just saved, or one clicked - is that stop's, not the new one's.
            if (pinOwners.Any(p => PokestopCatalogService.IsPokestop(p)
                    && string.Equals(PokestopCatalogService.PlaceOf(p), PokestopPlace.Trim(), StringComparison.OrdinalIgnoreCase)))
            {
                PokestopPlace = string.Empty;
            }

            string place = PokestopPlace.Trim();

            DraftPin = new BossPin
            {
                BossId = PokestopCatalogService.NewId(),
                Boss = place.Length > 0 ? PokestopCatalogService.PinNameFor(place) : PokestopCatalogService.DisplayName,
                X = Math.Clamp(x, 0, ImagePixelWidth - 1),
                Y = Math.Clamp(y, 0, ImagePixelHeight - 1),
                ImageWidth = ImagePixelWidth,
                ImageHeight = ImagePixelHeight,
            };
            return;
        }

        if (SelectedBoss is not BossInfo boss)
        {
            Status = "Pick a boss first.";
            return;
        }

        DraftPin = new BossPin
        {
            BossId = boss.BossId,
            Boss = boss.Name,
            X = Math.Clamp(x, 0, ImagePixelWidth - 1),
            Y = Math.Clamp(y, 0, ImagePixelHeight - 1),
            ImageWidth = ImagePixelWidth,
            ImageHeight = ImagePixelHeight,
        };
    }

    [RelayCommand]
    private async Task SavePin()
    {
        BossPin? pin = DraftPin is { IsValid: true } draft ? draft : null;

        // §420: a stop needs its name; with no draft, the selected stop is
        // saved again under the name typed - a rename.
        if (PokestopMode)
        {
            string place = PokestopPlace.Trim();

            if (pin is null && SelectedPin is BossPin picked && PokestopCatalogService.IsPokestop(picked))
            {
                pin = new BossPin
                {
                    BossId = picked.BossId,
                    Boss = picked.Boss,
                    X = picked.X,
                    Y = picked.Y,
                    ImageWidth = picked.ImageWidth,
                    ImageHeight = picked.ImageHeight,
                };
            }

            if (pin is null)
            {
                Status = "Click where the Pokéstop stands first.";
                return;
            }

            if (place.Length == 0)
            {
                Status = "Name this Pokéstop first - pick one from the list or type any name.";
                return;
            }

            pin.Boss = PokestopCatalogService.PinNameFor(place);
        }

        if (pin is null)
        {
            Status = "Click where the boss stands first.";
            return;
        }

        if (!await BeginPublishAsync(pin.Boss))
            return;

        try
        {
            BossPin saved = await EventsSyncService.SaveBossPinAsync(pin);

            BossPinService.ApplyOne(saved);
            RebuildPins();

            DraftPin = null;
            SelectedPin = pinOwners.FirstOrDefault(p => p.Key == saved.Key);
            Status = $"Placed {saved.Boss} at {saved.X},{saved.Y}.";
        }
        catch (EventsSyncException ex)
        {
            Status = DescribePublishFailure(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    [RelayCommand]
    private async Task RemovePin()
    {
        if (SelectedPin is not BossPin pin)
        {
            Status = "Click a pin first.";
            return;
        }

        if (!await BeginPublishAsync(pin.Boss))
            return;

        try
        {
            await EventsSyncService.DeleteBossPinAsync(pin.BossId);

            BossPinService.RemoveOne(pin.BossId);
            RebuildPins();

            SelectedPin = null;
            Status = $"Took down the pin for {pin.Boss}.";
        }
        catch (EventsSyncException ex)
        {
            Status = ex.StatusCode == 404 ? $"The pin for {pin.Boss} was not on the server any more." : DescribePublishFailure(ex);
        }
        finally
        {
            Busy = false;
        }
    }

    /// <summary>The checks every publish makes - the server configured, no
    /// other publish running, the master token in hand (asked for
    /// otherwise). Sets Busy when it answers true; the caller clears it.</summary>
    private async Task<bool> BeginPublishAsync(string what)
    {
        if (!EventsSyncService.IsOnline)
        {
            Status = "No events server configured.";
            return false;
        }

        if (Busy)
            return false;

        if (!EventsSyncService.HasAdminCredentials && (RequestAdminSignIn is null || !await RequestAdminSignIn()))
        {
            Status = "Publishing needs the master admin token.";
            return false;
        }

        Busy = true;
        Status = $"Publishing {what}...";
        return true;
    }

    private static string DescribePublishFailure(EventsSyncException ex)
    {
        if (ex.Unauthorized)
            EventsSyncService.ForgetAdminCredentials();

        return ex.Unauthorized
            ? "The events server rejected the sign-in - try again to sign in."
            : ex.StatusCode == 404
                ? "Could not publish it - the events server is running an older build - redeploy worker.js, then try again."
                : $"Could not publish it - {ex.Message}.";
    }

    /// <summary>Every published box, placed on this picture - by
    /// proportion when it was drawn on a picture of another size, which
    /// the line says.</summary>
    private void RebuildBoxes()
    {
        MapBoxItem? keep = SelectedBox;

        Boxes.Clear();
        Shapes.Clear();

        foreach (SpawnMap map in SpawnDataService.Current.Where(m => m.HasBoxes))
        {
            // §402: one line and one shape per box; a map with several
            // numbers them so the lines can be told apart.
            List<MapMarker> markers = map.Markers.Where(m => m.IsValid).ToList();

            for (int i = 0; i < markers.Count; i++)
            {
                MapMarker marker = markers[i];
                MapBoxShape shape = Place(map.Map, marker);

                bool scaled = HasImage && (marker.ImageWidth != ImagePixelWidth || marker.ImageHeight != ImagePixelHeight);
                string which = markers.Count > 1 ? $" #{i + 1}" : string.Empty;

                // §412: a spot says how many maps share it.
                int linked = SpawnDataService.LinkedTo(map).Count;

                string text = $"{map.Map}{which} · {marker.Describe}"
                    + (linked > 0 ? $" · {linked} linked" : string.Empty)
                    + (scaled ? $" (drawn on a {marker.ImageWidth}×{marker.ImageHeight} picture)" : string.Empty);

                Boxes.Add(new MapBoxItem(map, marker, text));
                Shapes.Add(shape);
            }
        }

        SelectedBox = keep is null ? null : FindBox(keep.Map.Key, keep.Box);

        OnPropertyChanged(nameof(CanRemoveLast));
        Redraw?.Invoke();
    }

    /// <summary>§400. The box saved most recently from this window that is
    /// still on its page - dropping entries whose box has gone since
    /// (removed from the console, or by hand).</summary>
    private MapBoxItem? LastSavedHere()
    {
        for (int i = savedHere.Count - 1; i >= 0; i--)
        {
            SavedBox entry = savedHere[i];

            MapBoxItem? item = FindBox(entry.Key, entry.Box);

            if (item is not null)
                return item;

            savedHere.RemoveAt(i);
        }

        return null;
    }

    /// <summary>§402. The listed box for a map that is this box, pixel for
    /// pixel - the way a box is found again after a republish.</summary>
    private MapBoxItem? FindBox(string key, MapMarker box) =>
        Boxes.FirstOrDefault(b => b.Map.Key == key && b.Box.SameAs(box));

    /// <summary>A published box in this picture's pixels.</summary>
    private MapBoxShape Place(string label, MapMarker marker)
    {
        if (!HasImage || (marker.ImageWidth == ImagePixelWidth && marker.ImageHeight == ImagePixelHeight))
            return new MapBoxShape(label, marker.X, marker.Y, marker.Width, marker.Height);

        double sx = ImagePixelWidth / (double)marker.ImageWidth;
        double sy = ImagePixelHeight / (double)marker.ImageHeight;

        int x = (int)Math.Round(marker.X * sx);
        int y = (int)Math.Round(marker.Y * sy);
        int width = Math.Max(1, (int)Math.Round(marker.Width * sx));
        int height = Math.Max(1, (int)Math.Round(marker.Height * sy));

        return new MapBoxShape(label, x, y, width, height);
    }

    /// <summary>The saved box under a picture pixel, if any - the smallest
    /// when boxes overlap, so a small box inside a big one can still be
    /// picked.</summary>
    public MapBoxItem? BoxAt(int x, int y)
    {
        MapBoxItem? best = null;
        long bestArea = long.MaxValue;

        for (int i = 0; i < Shapes.Count && i < Boxes.Count; i++)
        {
            MapBoxShape s = Shapes[i];

            if (x < s.X || y < s.Y || x >= s.X + s.Width || y >= s.Y + s.Height)
                continue;

            long area = (long)s.Width * s.Height;

            if (area < bestArea)
            {
                bestArea = area;
                best = Boxes[i];
            }
        }

        return best;
    }

    /// <summary>A drag's result in picture pixels, from any two corners.
    /// Clamped to the picture; a box smaller than two pixels each way is a
    /// click, not a box, and clears the draft instead.</summary>
    public void SetDraftFromCorners(int x1, int y1, int x2, int y2)
    {
        if (!HasImage)
            return;

        int left = Math.Clamp(Math.Min(x1, x2), 0, ImagePixelWidth - 1);
        int top = Math.Clamp(Math.Min(y1, y2), 0, ImagePixelHeight - 1);
        int right = Math.Clamp(Math.Max(x1, x2), 0, ImagePixelWidth - 1);
        int bottom = Math.Clamp(Math.Max(y1, y2), 0, ImagePixelHeight - 1);

        int width = right - left + 1;
        int height = bottom - top + 1;

        if (width < 2 || height < 2)
        {
            Draft = null;
            return;
        }

        Draft = new MapMarker
        {
            X = left,
            Y = top,
            Width = width,
            Height = height,
            ImageWidth = ImagePixelWidth,
            ImageHeight = ImagePixelHeight,
        };
    }

    // --------------------------------------------------------------- zoom

    [RelayCommand]
    private void ZoomIn() => StepZoom(+1);

    [RelayCommand]
    private void ZoomOut() => StepZoom(-1);

    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

    /// <summary>The next step up or down from the current zoom.</summary>
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

    /// <summary>The largest step at which the whole picture fits a
    /// viewport of the given size - the window's Fit button.</summary>
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

    // ------------------------------------------------------------ publish

    /// <summary>§400. Takes back the last thing drawn: the box being drawn,
    /// if there is one; otherwise the box saved most recently from this
    /// window on this region's page, republishing its map without it. One
    /// step back per press - a box saved in an earlier session is not
    /// "last" here, so it is reached through Remove box after a click.</summary>
    [RelayCommand]
    private async Task RemoveLast()
    {
        if (Draft is { IsValid: true })
        {
            Draft = null;
            Status = "Dropped the box being drawn.";
            return;
        }

        if (LastSavedHere() is not MapBoxItem item)
        {
            OnPropertyChanged(nameof(CanRemoveLast));
            Status = "Nothing saved from this window is left to take back - click a box, then Remove box.";
            return;
        }

        await PublishAsync(item.Map.Map, Without(item), null, $"Removed the box for {item.Map.Map}; its page stays.");
    }

    /// <summary>Publishes the named map with the drawn box added to the
    /// boxes it has (§402: a map may have several - a route the picture
    /// draws in pieces gets one per piece) - the same request as the
    /// Spawns section's Add, so a map not yet published is published by
    /// its first box.</summary>
    [RelayCommand]
    private async Task SaveBox()
    {
        if (Draft is not { IsValid: true } box)
        {
            Status = "Draw a box first.";
            return;
        }

        string map = MapName.Trim();

        if (map.Length == 0 || SpawnMap.KeyFor(map).Length == 0)
        {
            Status = "Name the map the box marks.";
            return;
        }

        List<MapMarker> boxes = SpawnDataService.Find(map)?.Markers.Where(m => m.IsValid).ToList() ?? new List<MapMarker>();
        boxes.Add(box);

        string count = boxes.Count > 1 ? $" (its {Ordinal(boxes.Count)})" : string.Empty;
        await PublishAsync(map, boxes, box, $"Saved the box for {map}{count} on the {RegionFor(map)} page.");
    }

    /// <summary>§406. The page a map goes on: the one it is already
    /// published under, else the catalog's region for the name, else
    /// Other - the Spawns section can move it.</summary>
    private static string RegionFor(string map) =>
        SpawnDataService.Find(map)?.Region
        ?? SpawnRegions.ForCatalogRegion(LocationCatalogService.Find(map)?.Region);

    private static string Ordinal(int n) => n switch
    {
        1 => "first",
        2 => "second",
        3 => "third",
        _ => n + "th",
    };

    /// <summary>§402. A map's boxes without the given one - what Remove box
    /// and Remove last publish.</summary>
    private static List<MapMarker> Without(MapBoxItem item) =>
        item.Map.Markers.Where(m => m.IsValid && !m.SameAs(item.Box)).ToList();

    /// <summary>Republishes the selected box's map without that box (§402:
    /// its other boxes stay). The page and its species stay.</summary>
    [RelayCommand]
    private async Task RemoveBox()
    {
        if (SelectedBox is not MapBoxItem item)
        {
            Status = "Click a box, or pick one in the list, first.";
            return;
        }

        await PublishAsync(item.Map.Map, Without(item), null, $"Removed the box for {item.Map.Map}; its page stays.");
    }

    /// <param name="markers">Every box the map is to have.</param>
    /// <param name="added">The one being saved, for the order Remove last
    /// keeps; null when one is being removed.</param>
    private async Task PublishAsync(string map, List<MapMarker> markers, MapMarker? added, string success)
    {
        if (!EventsSyncService.IsOnline)
        {
            Status = "No events server configured.";
            return;
        }

        if (Busy)
            return;

        if (!EventsSyncService.HasAdminCredentials && (RequestAdminSignIn is null || !await RequestAdminSignIn()))
        {
            Status = "Publishing needs the master admin token.";
            return;
        }

        Busy = true;
        Status = $"Publishing {map}...";

        try
        {
            SpawnMap page = SpawnDataService.BuildFromPokedex(RegionFor(map), map);
            page.Markers = markers;

            // §412: a map given a box of its own stops sharing another's
            // spot - its own box is where it is now.
            if (markers.Count > 0)
                page.LinkedTo = null;

            SpawnMap saved = await EventsSyncService.SaveSpawnMapAsync(page);

            // The machine that published it sees it at once. The Changed
            // event rebuilds the list too, but through the dispatcher -
            // later than the selection below - so rebuild here first.
            SpawnDataService.ApplyOne(saved);

            // §400: the order boxes were saved in, for Remove last - boxes
            // the map no longer has leave; the one just saved goes last.
            savedHere.RemoveAll(entry => entry.Key == saved.Key && !saved.Markers.Any(m => m.SameAs(entry.Box)));

            if (added is not null)
                savedHere.Add(new SavedBox(saved.Key, added));

            FillMapChoices();
            RebuildBoxes();

            Draft = null;
            SelectedBox = added is null ? null : FindBox(saved.Key, added);

            Status = saved.Pokemon.Count == 0 && added is not null
                ? success + " The page has no Pokémon yet - scan the Pokedex for the species that live there, then Republish it in the Spawns section."
                : success;
        }
        catch (EventsSyncException ex)
        {
            if (ex.Unauthorized)
                EventsSyncService.ForgetAdminCredentials();

            Status = ex.Unauthorized
                ? "The events server rejected the sign-in - try again to sign in."
                : ex.StatusCode == 404
                    ? "Could not publish it - the events server is running an older build - redeploy worker.js, then try again."
                    : $"Could not publish it - {ex.Message}.";
        }
        finally
        {
            Busy = false;
        }
    }

    private async Task RefreshPinsQuietlyAsync()
    {
        try
        {
            await BossPinService.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Map boxes: the boss pins could not be refreshed.");
        }
    }

    public void Dispose()
    {
        SpawnDataService.Changed -= OnSpawnsChanged;
        BossPinService.Changed -= OnPinsChanged;
    }
}
