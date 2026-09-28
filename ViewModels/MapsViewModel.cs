using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Controls;
using Foot_Tracker.Models;
using Foot_Tracker.Services;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>§407/§410. One line of the Encounter Locations table on a
/// Pokémon's card - a map and how the species is met there - and one
/// line of a map's card: a species on its page. The map and its page row
/// travel with it, so a click can light the box and a sort can read the
/// text.</summary>
public sealed class MapsLocationRow
{
    public required SpawnMap Map { get; init; }

    public required SpawnPokemon Pokemon { get; init; }

    /// <summary>The map's name - the Location column.</summary>
    public string Location => Map.Map;

    public string Region => Map.Region;

    /// <summary>The species' name - the map card's first column.</summary>
    public string Species => Pokemon.Name;

    /// <summary>§408. The name's colour: the members colour for a
    /// members-only spawn, the panel's ink otherwise.</summary>
    public required IBrush TitleBrush { get; init; }

    public bool IsMembers => Pokemon.MembersOnly;

    public string MembersText => Pokemon.MembersOnly ? "MS" : string.Empty;

    public Bitmap? Sprite { get; init; }

    /// <summary>§410. The methods, for their glyphs.</summary>
    public IReadOnlyList<EncounterMethod> Methods { get; init; } = Array.Empty<EncounterMethod>();

    /// <summary>"Grass, Surf" - the Method column's text and sort key.</summary>
    public string MethodText => Pokemon.MethodText;

    public string TimeText => Pokemon.TimeText;

    /// <summary>§412. Whether the map is on the picture - by a box of its
    /// own, or at the spot it is linked to. Set when the row is made.</summary>
    public bool OnPicture { get; init; }

    /// <summary>Said in the row's tooltip when the map has no box yet.</summary>
    public string Note => OnPicture ? "Click to bring its box into view." : "Not drawn on the picture yet.";

    /// <summary>§429. The level range players who share have met this
    /// species at on this map; null until anyone has.</summary>
    public SpawnLevelRange? Levels { get; init; }

    /// <summary>"12-18", "15", or "" - the Level column's text and sort key.</summary>
    public string LevelText => Levels?.Text ?? string.Empty;

    /// <summary>§429. The Level cell's tooltip: how sure the range is, and
    /// the repel trick it allows. With a repel on, a wild Pokémon below the
    /// lead's level does not appear; so a lead at the species' HIGHEST level
    /// here still meets it (at that level) while everything lower on the
    /// map stays away, and a lead one above it never meets it at all.</summary>
    public string LevelNote =>
        Levels is null
            ? "No level shared for this spot yet. Players who tick \"Share level data\" build these ranges."
            : $"Lv. {Levels.Text} from {Levels.Samples} shared sighting{(Levels.Samples == 1 ? "" : "s")}. "
              + $"Repel trick: a lead at Lv. {Levels.Max} keeps every lower-level spawn away and still meets this one.";
}

/// <summary>§412. One map among those sharing a spot - a safari zone's
/// area - on the map card's chooser: the map, its name, its species count.</summary>
public sealed record MapsAreaRow(SpawnMap Map, string Name, string CountText);

/// <summary>§410. One line of the Encounter Summary: a fact and its value.</summary>
public sealed record MapsFactRow(string Label, string Value);

/// <summary>§419. One placed Pokéstop on the Pokéstops card: the pin, where
/// it stands and its region.</summary>
public sealed record MapsStopRow(BossPin Pin, string Name, string Region);

/// <summary>§410. One line of the Abilities table: the name and what it
/// does, from the ability list the calculator reads. §413: and whether it
/// is the hidden ability.</summary>
public sealed record MapsAbilityRow(string Name, string Description, bool Hidden = false);

/// <summary>§410. One line of the Base Stats table: the number, and the
/// number as a share of the bar (255 is the most any base stat is).</summary>
public sealed record MapsStatRow(string Name, int Value, IBrush Bar);

/// <summary>§410. One line of the Weaknesses table: the attacking type,
/// its badge, and how much it does - "2×" or "4×".</summary>
public sealed record MapsTypeRow(string Type, Bitmap? Icon, string Multiplier, IBrush Brush);

/// <summary>
/// §407/§410. Game Data → Maps: the world picture with every published
/// box on it as a quiet marker, and a panel that answers a search. A
/// Pokémon gives its card - sprite and types, an encounter summary, its
/// abilities, base stats and weaknesses as tables, and an Encounter
/// Locations table of every map it spawns on with method glyphs, time and
/// region - and lights its boxes on the picture with the same glyphs. A
/// map gives its card - the species on its page - and lights its box. A
/// boss gives its card and lights its pin.
///
/// §410 adds the filters over the toolbar - a region, a method, a time
/// of day, bosses only - which narrow both the markers on the picture and
/// the rows of the table; and the two-way tie between them: a marker
/// clicked selects its row, a row selected lights its marker and brings
/// it into view, a row hovered lights its marker, a marker hovered names
/// itself by the pointer.
///
/// The picture is the world (§406); the boxes come from the published
/// pages (§397/§402), the pins from §409, the card's facts from the
/// sprite library (types), the calculator's pokedex (abilities, base
/// stats), the ability list (what an ability does) and the type chart
/// (weaknesses). Nothing here is a second copy of any of those.
/// </summary>
public sealed partial class MapsViewModel : ViewModelBase, IDisposable
{
    public static readonly double[] ZoomSteps = { 0.25, 0.5, 0.75, 1, 1.5, 2, 3, 4, 6, 8 };

    private static readonly string[] AllTypes =
    {
        "Normal", "Fire", "Water", "Electric", "Grass", "Ice", "Fighting", "Poison", "Ground",
        "Flying", "Psychic", "Bug", "Rock", "Ghost", "Dragon", "Dark", "Steel", "Fairy",
    };

    private const int FlashTicks = 8;

    private const string TwoTimes = "2\u00d7";

    private const string FourTimes = "4\u00d7";

    public const string AnyRegion = "All regions";

    public const string AnyMethod = "Any method";

    public const string AnyTime = "Any time";

    /// <summary>§408. Members-only spawns are told apart by colour: the
    /// map's name and the "MS" after it in this magenta.</summary>
    public static readonly IBrush MembersBrush = new SolidColorBrush(Color.FromRgb(0xD8, 0x50, 0xE8));

    /// <summary>§414. A row's ink when it is not members-only: the
    /// Encounters text colour, the colour the tables are in - read when a
    /// card is built, so it follows the Appearance window.</summary>
    /// <remarks>§423. The key is spelt here rather than read off
    /// ThemeManager.EncountersTextBrushKey - the one thing this view model
    /// wanted from ThemeManager, and the phone build (which has no
    /// ThemeManager, its colours being fixed in its App.axaml) links this
    /// file. The two spellings are the same string; ThemeManager's own
    /// constant still names the resource on the desktop.</remarks>
    private static IBrush InkBrush =>
        Avalonia.Application.Current is { } app
        && app.Resources.TryGetValue("ThemeEncountersTextBrush", out object? found)
        && found is IBrush brush
            ? brush
            : new SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0));

    private static readonly IBrush LowStat = new SolidColorBrush(Color.FromRgb(0xE0, 0x50, 0x50));
    private static readonly IBrush MidStat = new SolidColorBrush(Color.FromRgb(0xE8, 0xC0, 0x40));
    private static readonly IBrush HighStat = new SolidColorBrush(Color.FromRgb(0x40, 0xD0, 0x70));

    private readonly DispatcherTimer flashTimer = new() { Interval = TimeSpan.FromMilliseconds(400) };

    private int flashTicksLeft;

    // ------------------------------------------------------------ picture

    [ObservableProperty] private Bitmap? image;

    [ObservableProperty] private int imagePixelWidth;

    [ObservableProperty] private int imagePixelHeight;

    [ObservableProperty] private bool hasImage;

    [ObservableProperty] private double zoom = 0.5;

    [ObservableProperty] private double canvasWidth;

    [ObservableProperty] private double canvasHeight;

    [ObservableProperty] private string zoomText = "50%";

    /// <summary>Every published box that passes the filters, one shape
    /// each, and the map each belongs to, in step.</summary>
    public ObservableCollection<MapBoxShape> Shapes { get; } = new();

    private readonly List<SpawnMap> shapeOwners = new();

    /// <summary>§412. Every spot on the picture and the maps found there -
    /// itself first, then those linked to it - by the spot's key; rebuilt
    /// with the shapes.</summary>
    private readonly Dictionary<string, IReadOnlyList<SpawnMap>> spots = new(StringComparer.Ordinal);

    public IReadOnlyList<int> Highlighted { get; private set; } = Array.Empty<int>();

    /// <summary>§410. The boxes under the table row the pointer is on.</summary>
    public IReadOnlyList<int> Hovered { get; private set; } = Array.Empty<int>();

    /// <summary>§410. The method glyphs on each found box: how the shown
    /// species is met on that box's map.</summary>
    public IReadOnlyDictionary<int, IReadOnlyList<EncounterMethod>> BoxMethods { get; private set; } =
        new Dictionary<int, IReadOnlyList<EncounterMethod>>();

    public bool FlashOn { get; private set; }

    // ------------------------------------------------------------- search

    /// <summary>What can be typed: every species the library knows, every
    /// published map and every boss, for the box's completion.</summary>
    public ObservableCollection<string> Choices { get; } = new();

    [ObservableProperty] private string query = string.Empty;

    // ------------------------------------------------------------ filters

    public IReadOnlyList<string> RegionChoices { get; } =
        new[] { AnyRegion }.Concat(SpawnRegions.All).ToList();

    public IReadOnlyList<string> MethodChoices { get; } =
        new[] { AnyMethod }.Concat(EncounterMethods.All.Select(EncounterMethods.Name)).ToList();

    public IReadOnlyList<string> TimeChoices { get; } = new[] { AnyTime, "Morning", "Day", "Night" };

    [ObservableProperty] private string selectedRegion = AnyRegion;

    [ObservableProperty] private string selectedMethod = AnyMethod;

    [ObservableProperty] private string selectedTime = AnyTime;

    [ObservableProperty] private bool bossesOnly;

    /// <summary>§422. Whether the boss pins and the Pokéstop pins are on
    /// the picture at all. Both on by default; two buttons beside the
    /// search take each off and put it back. "Bosses only" is the other
    /// way round - it hides the SPAWN markers - and the two compose: bosses
    /// only with bosses hidden is a bare picture, which is what was asked.</summary>
    [ObservableProperty] private bool showBosses = true;

    [ObservableProperty] private bool showPokestops = true;

    public string BossesToggleText => ShowBosses ? "Hide Bosses" : "Show Bosses";

    public string PokestopsToggleText => ShowPokestops ? "Hide Pokéstops" : "Show Pokéstops";

    [ObservableProperty] private string filterText = string.Empty;

    /// <summary>§429. The level-sharing opt-in, offered here beside the
    /// column it feeds as well as in Tracker Settings. Both read and write
    /// the same preference; unticked until the player ticks it.</summary>
    [ObservableProperty] private bool shareLevelData = TrackerSettings.ShareLevelData;

    partial void OnShareLevelDataChanged(bool value)
    {
        if (value == TrackerSettings.ShareLevelData)
            return;

        try
        {
            UiPreferences preferences = UiPreferencesService.Load();
            preferences.ShareLevelData = value;
            UiPreferencesService.Save(preferences);
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Maps: the level-sharing choice could not be saved.");
        }

        TrackerSettings.ApplyLevelSharing(value);

        if (!value)
            LevelShareService.Discard();

        Status = value
            ? "Level sharing is on: from the next encounter, the species, map and level of what you meet joins the shared ranges. Nothing that names you is sent."
            : "Level sharing is off. Nothing more is sent.";
    }

    // -------------------------------------------------------------- cards

    [ObservableProperty] private bool showsNothing = true;

    [ObservableProperty] private bool showsPokemon;

    [ObservableProperty] private bool showsMap;

    [ObservableProperty] private string cardTitle = string.Empty;

    [ObservableProperty] private string cardSubtitle = string.Empty;

    [ObservableProperty] private Bitmap? cardSprite;

    [ObservableProperty] private Bitmap? typeIcon1;

    [ObservableProperty] private Bitmap? typeIcon2;

    /// <summary>§410. The Encounter Summary table.</summary>
    public ObservableCollection<MapsFactRow> Summary { get; } = new();

    public ObservableCollection<MapsAbilityRow> Abilities { get; } = new();

    [ObservableProperty] private string abilitiesNote = string.Empty;

    [ObservableProperty] private bool hasAbilitiesNote;

    public ObservableCollection<MapsStatRow> Stats { get; } = new();

    [ObservableProperty] private string statsTotal = string.Empty;

    [ObservableProperty] private string statsNote = string.Empty;

    [ObservableProperty] private bool hasStatsNote;

    public ObservableCollection<MapsTypeRow> Weaknesses { get; } = new();

    [ObservableProperty] private string weaknessesNote = string.Empty;

    [ObservableProperty] private bool hasWeaknessesNote;

    /// <summary>A Pokémon card's Encounter Locations - every map that
    /// lists it and passes the filters.</summary>
    public ObservableCollection<MapsLocationRow> Locations { get; } = new();

    [ObservableProperty] private string locationsHeading = "Encounter Locations";

    [ObservableProperty] private bool hasNoLocations;

    [ObservableProperty] private bool hasNoMapRows;

    /// <summary>§410. The table's selected row: set by a click on the
    /// table or on a marker; lights that map's boxes alone.</summary>
    [ObservableProperty] private MapsLocationRow? selectedLocation;

    /// <summary>§412. The maps sharing the shown map's spot - a click on a
    /// shared spot offers them all; picking one shows its species.</summary>
    public ObservableCollection<MapsAreaRow> Areas { get; } = new();

    [ObservableProperty] private MapsAreaRow? selectedArea;

    [ObservableProperty] private bool hasAreas;

    [ObservableProperty] private string areasHeading = string.Empty;

    /// <summary>A map card's species.</summary>
    public ObservableCollection<MapsLocationRow> MapRows { get; } = new();

    [ObservableProperty] private string status = "Type a Pokémon, a map or a boss, or click a marker on the picture.";

    /// <summary>The map whose card is shown, or whose boxes a Pokémon
    /// card's row narrowed to; null when every found box is lit.</summary>
    private SpawnMap? focusedMap;

    private string? shownPokemon;

    /// <summary>Set while a card is being rebuilt, so the table's own
    /// selection change (its rows are cleared) is not taken for a click.</summary>
    private bool rebuilding;

    // ------------------------------------------------------------- bosses

    /// <summary>§409. Every published boss pin as the layer draws it, and
    /// the pin each belongs to, in step.</summary>
    public ObservableCollection<MapPinShape> PinShapes { get; } = new();

    private readonly List<BossPin> pinOwners = new();

    public IReadOnlyList<int> HighlightedPins { get; private set; } = Array.Empty<int>();

    [ObservableProperty] private bool showsBoss;

    [ObservableProperty] private Bitmap? bossPortrait;

    /// <summary>§410. The boss card's facts as a table: location,
    /// cooldown, requirements.</summary>
    public ObservableCollection<MapsFactRow> BossFacts { get; } = new();

    [ObservableProperty] private string bossPinText = string.Empty;

    /// <summary>The boss whose card is shown; null for none.</summary>
    private BossInfo? shownBoss;

    // ---------------------------------------------------------- pokéstops

    /// <summary>§419. The Pokéstop card: one stop's, or every stop's.</summary>
    [ObservableProperty] private bool showsPokestop;

    [ObservableProperty] private Bitmap? pokestopPortrait;

    public ObservableCollection<MapsFactRow> PokestopFacts { get; } = new();

    /// <summary>§419. What a stop can give and how many - two of these at
    /// random per visit.</summary>
    public ObservableCollection<MapsFactRow> PokestopRewards { get; } = new();

    [ObservableProperty] private string pokestopNote = string.Empty;

    /// <summary>§419. Every stop on the picture, on the every-stop card.</summary>
    public ObservableCollection<MapsStopRow> PlacedStops { get; } = new();

    [ObservableProperty] private bool hasPlacedStops;

    /// <summary>The stop whose card is shown (its pin's key), or every stop;
    /// neither when another card is shown.</summary>
    private string? shownStopKey;

    private bool shownAllStops;

    /// <summary>Set by the window - opens the boss's own window.</summary>
    public Action<BossInfo>? OpenBoss { get; set; }

    public event Action? Redraw;

    public event Action<MapBoxShape>? BringIntoView;

    public MapsViewModel()
    {
        flashTimer.Tick += OnFlashTick;

        LoadPicture();
        FillChoices();
        RebuildShapes();
        RebuildPins();

        SpawnDataService.Changed += OnSpawnsChanged;
        BossPinService.Changed += OnPinsChanged;
        // §429: the level ranges change the tables' Level column and the
        // summary line, so a card is shown again when they do.
        SpawnLevelService.Changed += OnLevelsChanged;
        _ = RefreshQuietlyAsync();
    }

    private void OnLevelsChanged() => Dispatcher.UIThread.Post(ShowAgain);

    private async Task RefreshQuietlyAsync()
    {
        try
        {
            await SpawnDataService.RefreshAsync();
            await BossPinService.RefreshAsync();
            await SpawnLevelService.RefreshAsync();
        }
        catch (Exception ex)
        {
            Log.Debug(ex, "Maps: the published lists could not be refreshed.");
        }
    }

    private void OnPinsChanged() => Dispatcher.UIThread.Post(() =>
    {
        RebuildPins();
        FillChoices();

        if (shownBoss is BossInfo boss)
            ShowBoss(boss);
        else if (shownStopKey is not null || shownAllStops)
            ShowStopAgain();
    });

    /// <summary>§419. The Pokéstop card again from the current pins - the
    /// every-stop card when the one shown has gone.</summary>
    private void ShowStopAgain() =>
        ShowPokestop(shownStopKey is string key ? pinOwners.FirstOrDefault(p => p.Key == key) : null);

    private void OnSpawnsChanged() => Dispatcher.UIThread.Post(() =>
    {
        FillChoices();
        RebuildShapes();
        ShowAgain();
    });

    /// <summary>Whatever is shown, shown again from the current copy and
    /// the current filters, keeping the selected row where it still is.</summary>
    private void ShowAgain()
    {
        string? selectedKey = SelectedLocation?.Map.Key;

        if (shownPokemon is string pokemon)
        {
            ShowPokemon(pokemon);

            if (selectedKey is not null && Locations.FirstOrDefault(r => r.Map.Key == selectedKey) is MapsLocationRow row)
                SelectedLocation = row;
        }
        else if (focusedMap is SpawnMap map && SpawnDataService.Find(map.Map) is SpawnMap fresh)
        {
            ShowMap(fresh);
        }
        else if (shownBoss is BossInfo boss)
        {
            ShowBoss(boss);
        }
        else if (shownStopKey is not null || shownAllStops)
        {
            ShowStopAgain();
        }
    }

    // ------------------------------------------------------------ filters

    partial void OnSelectedRegionChanged(string value) => ApplyFilters();

    partial void OnSelectedMethodChanged(string value) => ApplyFilters();

    partial void OnSelectedTimeChanged(string value) => ApplyFilters();

    partial void OnBossesOnlyChanged(bool value) => ApplyFilters();

    /// <summary>§422. A pin kind shown or hidden: the pins are rebuilt
    /// without (or with) that kind, then the filters run as for any other
    /// change, so the filter line names it and whatever card is open is
    /// shown again against the pins that remain.</summary>
    partial void OnShowBossesChanged(bool value) => ApplyPinVisibility();

    partial void OnShowPokestopsChanged(bool value) => ApplyPinVisibility();

    private void ApplyPinVisibility()
    {
        OnPropertyChanged(nameof(BossesToggleText));
        OnPropertyChanged(nameof(PokestopsToggleText));

        RebuildPins();
        ApplyFilters();
    }

    [RelayCommand]
    private void ToggleBosses() => ShowBosses = !ShowBosses;

    [RelayCommand]
    private void TogglePokestops() => ShowPokestops = !ShowPokestops;

    /// <summary>Whether no filter is set.</summary>
    public bool IsUnfiltered =>
        SelectedRegion == AnyRegion && SelectedMethod == AnyMethod && SelectedTime == AnyTime && !BossesOnly
        && ShowBosses && ShowPokestops;

    [RelayCommand]
    private void ClearFilters()
    {
        SelectedRegion = AnyRegion;
        SelectedMethod = AnyMethod;
        SelectedTime = AnyTime;
        BossesOnly = false;
        ShowBosses = true;
        ShowPokestops = true;
    }

    private void ApplyFilters()
    {
        // A combo box whose list is rebound can push null; that is "any".
        if (SelectedRegion is null) { SelectedRegion = AnyRegion; return; }
        if (SelectedMethod is null) { SelectedMethod = AnyMethod; return; }
        if (SelectedTime is null) { SelectedTime = AnyTime; return; }

        FilterText = IsUnfiltered ? string.Empty : "Filtered: " + string.Join(", ", FilterParts());

        RebuildShapes();
        ShowAgain();
    }

    private IEnumerable<string> FilterParts()
    {
        if (SelectedRegion != AnyRegion) yield return SelectedRegion;
        if (SelectedMethod != AnyMethod) yield return SelectedMethod;
        if (SelectedTime != AnyTime) yield return SelectedTime;
        if (BossesOnly) yield return "bosses only";
        if (!ShowBosses) yield return "bosses hidden";
        if (!ShowPokestops) yield return "Pokéstops hidden";
    }

    /// <summary>Whether a map passes the region filter.</summary>
    private bool PassesRegion(SpawnMap map) =>
        SelectedRegion == AnyRegion || string.Equals(map.Region, SelectedRegion, StringComparison.OrdinalIgnoreCase);

    /// <summary>Whether a map's markers are drawn: its region passes, and
    /// the picture is not narrowed to bosses.</summary>
    private bool PassesMap(SpawnMap map) => !BossesOnly && PassesRegion(map);

    /// <summary>Whether a species' row on a map passes the method and
    /// time filters.</summary>
    private bool PassesRow(SpawnPokemon row)
    {
        if (EncounterMethods.Parse(SelectedMethod) is EncounterMethod method && !EncounterMethods.Has(row, method))
            return false;

        return SelectedTime switch
        {
            "Morning" => row.Morning,
            "Day" => row.Day,
            "Night" => row.Night,
            _ => true,
        };
    }

    // ------------------------------------------------------------ picture

    /// <summary>§409. Every published pin on the picture, by proportion
    /// when placed on a picture of another size.</summary>
    private void RebuildPins()
    {
        PinShapes.Clear();
        pinOwners.Clear();

        foreach (BossPin pin in BossPinService.Current)
        {
            // §419: a Pokéstop's label, sprite and blue ring, or a boss's.
            string label = PokestopCatalogService.LabelFor(pin);
            Bitmap? portrait = PokestopCatalogService.PortraitFor(pin);
            bool stop = PokestopCatalogService.IsPokestop(pin);

            // §422: a hidden kind is left out of BOTH lists, which are
            // built in step - so the layer, the hit test, the highlights
            // and the every-stop card all see the same pins.
            if (stop ? !ShowPokestops : !ShowBosses)
                continue;

            bool same = !HasImage || (pin.ImageWidth == ImagePixelWidth && pin.ImageHeight == ImagePixelHeight);

            PinShapes.Add(same
                ? new MapPinShape(label, pin.X, pin.Y, portrait, stop)
                : new MapPinShape(
                    label,
                    (int)Math.Round(pin.X * (ImagePixelWidth / (double)pin.ImageWidth)),
                    (int)Math.Round(pin.Y * (ImagePixelHeight / (double)pin.ImageHeight)),
                    portrait,
                    stop));
            pinOwners.Add(pin);
        }

        SetHighlight();
    }

    /// <summary>The published pin a shape belongs to.</summary>
    public BossPin? PinOwner(int shapeIndex) =>
        shapeIndex >= 0 && shapeIndex < pinOwners.Count ? pinOwners[shapeIndex] : null;

    partial void OnZoomChanged(double value)
    {
        CanvasWidth = ImagePixelWidth * value;
        CanvasHeight = ImagePixelHeight * value;
        ZoomText = $"{Math.Round(value * 100)}%";
        Redraw?.Invoke();
    }

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
            Log.Warning(ex, "Maps: the world picture could not be read from {Path}.", path);
        }

        HasImage = Image is not null && ImagePixelWidth > 0 && ImagePixelHeight > 0;

        OnZoomChanged(Zoom);
    }

    private void FillChoices()
    {
        Choices.Clear();

        IEnumerable<string> names = PokemonSpriteService.AllPokemon.Select(p => p.Name)
            // §421: the regional forms are hunt targets of their own, under
            // the library's names ("Linoone-Galarian"), and complete too.
            .Concat(PokemonSpriteService.GetHuntableRegionalForms().Select(f => f.Name))
            .Concat(SpawnDataService.Current.Select(m => m.Map))
            .Concat(BossCatalogService.All.Select(b => b.Name))
            // §415: a boss's other names complete too ("Team Rocket", "Jessie").
            .Concat(BossCatalogService.All.SelectMany(b => b.Aliases ?? Array.Empty<string>()))
            // §419: every stop at once, and each placed stop by name.
            .Concat(new[] { PokestopCatalogService.DisplayName })
            .Concat(BossPinService.Current.Where(p => PokestopCatalogService.IsPokestop(p)).Select(PokestopCatalogService.LabelFor))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(n => n, StringComparer.OrdinalIgnoreCase);

        foreach (string name in names)
            Choices.Add(name);
    }

    /// <summary>Every published box that passes the filters, on the
    /// picture, placed by proportion when drawn on a picture of another
    /// size (§399).</summary>
    private void RebuildShapes()
    {
        Shapes.Clear();
        shapeOwners.Clear();
        spots.Clear();

        foreach (SpawnMap map in SpawnDataService.Current.Where(m => m.HasBoxes && PassesMap(m)))
        {
            // §412: a spot other maps share is named for the group and lit
            // as one with them.
            IReadOnlyList<SpawnMap> linked = SpawnDataService.LinkedTo(map);
            spots[map.Key] = new[] { map }.Concat(linked).ToList();

            string label = SpawnDataService.SpotName(map, linked);

            foreach (MapMarker marker in map.Markers.Where(m => m.IsValid))
            {
                Shapes.Add(Place(label, map.Key, marker));
                shapeOwners.Add(map);
            }
        }

        SetHighlight();
    }

    private MapBoxShape Place(string label, string group, MapMarker marker)
    {
        if (!HasImage || (marker.ImageWidth == ImagePixelWidth && marker.ImageHeight == ImagePixelHeight))
            return new MapBoxShape(label, marker.X, marker.Y, marker.Width, marker.Height, group);

        double sx = ImagePixelWidth / (double)marker.ImageWidth;
        double sy = ImagePixelHeight / (double)marker.ImageHeight;

        return new MapBoxShape(
            label,
            (int)Math.Round(marker.X * sx),
            (int)Math.Round(marker.Y * sy),
            Math.Max(1, (int)Math.Round(marker.Width * sx)),
            Math.Max(1, (int)Math.Round(marker.Height * sy)),
            group);
    }

    /// <summary>§412. The maps found at a spot: itself, then those linked
    /// to it.</summary>
    private IReadOnlyList<SpawnMap> MembersOf(SpawnMap spot) =>
        spots.TryGetValue(spot.Key, out IReadOnlyList<SpawnMap>? members)
            ? members
            : new[] { spot }.Concat(SpawnDataService.LinkedTo(spot)).ToList();

    /// <summary>§412. The maps a click on a spot offers: the spot alone
    /// when nothing is linked; else those linked, after the spot itself
    /// when its own page has species (a spot drawn only to hold the group -
    /// "Hoenn Safari Zone" - has none, and is not offered).</summary>
    private IReadOnlyList<SpawnMap> AreasOf(SpawnMap spot)
    {
        IReadOnlyList<SpawnMap> members = MembersOf(spot);

        if (members.Count <= 1)
            return members;

        return members.Where(m => m.Key != spot.Key || m.Pokemon.Count > 0).ToList();
    }

    /// <summary>§412. The shapes of the spot a map is found at - its own,
    /// or the one it is linked to; none when it is on no spot.</summary>
    private List<int> ShapesOf(SpawnMap map)
    {
        string? key = SpawnDataService.SpotFor(map)?.Key;

        return key is null
            ? new List<int>()
            : Enumerable.Range(0, shapeOwners.Count).Where(i => shapeOwners[i].Key == key).ToList();
    }

    /// <summary>The index of the box under a picture pixel - the smallest
    /// when boxes overlap - or -1.</summary>
    public int ShapeAt(int x, int y)
    {
        int best = -1;
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
                best = i;
            }
        }

        return best;
    }

    /// <summary>The map whose box is under a picture pixel, or null.</summary>
    public SpawnMap? MapAt(int x, int y)
    {
        int index = ShapeAt(x, y);

        return index >= 0 && index < shapeOwners.Count ? shapeOwners[index] : null;
    }

    /// <summary>The map a shape belongs to.</summary>
    public SpawnMap? ShapeOwner(int shapeIndex) =>
        shapeIndex >= 0 && shapeIndex < shapeOwners.Count ? shapeOwners[shapeIndex] : null;

    // -------------------------------------------------------------- hover

    /// <summary>§410. The chip for a hovered box: the map's name, and how
    /// and when the shown species is met there when one is shown.</summary>
    public string HoverTextFor(SpawnMap map)
    {
        IReadOnlyList<SpawnMap> areas = AreasOf(map);

        // §412: at a shared spot, the map the species is in when it is in
        // one, a count when in several; the group and its size otherwise.
        if (shownPokemon is string species)
        {
            List<SpawnMap> hits = areas.Where(m => RowFor(m, species) is SpawnPokemon r && PassesRow(r)).ToList();

            if (hits.Count == 1 && RowFor(hits[0], species) is SpawnPokemon row)
            {
                string members = row.MembersOnly ? " · MS" : string.Empty;
                return $"{hits[0].Map} · {row.MethodText} · {row.TimeText}{members}";
            }

            if (hits.Count > 1)
                return $"{SpotNameOf(map)} · {species} in {hits.Count} of its {areas.Count} maps";
        }

        if (areas.Count > 1)
            return $"{SpotNameOf(map)} · {areas.Count} maps · click to choose";

        return map.Region.Length > 0 ? $"{map.Map} · {map.Region}" : map.Map;
    }

    /// <summary>§412. What a spot is called - the group's shared words.</summary>
    private string SpotNameOf(SpawnMap spot) =>
        SpawnDataService.SpotName(spot, MembersOf(spot).Where(m => m.Key != spot.Key).ToList());

    /// <summary>§410. The chip for a hovered pin.</summary>
    public string HoverTextFor(BossPin pin)
    {
        // §419: a Pokéstop says where it is and when it refreshes.
        if (PokestopCatalogService.IsPokestop(pin))
            return $"{PokestopCatalogService.DisplayName} · {PokestopCatalogService.PlaceOf(pin)} · every 2 days";

        BossInfo? boss = BossCatalogService.Find(pin.BossId);
        string name = boss?.Name ?? pin.Boss;

        return boss is { Location.Length: > 0 } ? $"{name} · {boss.Location}" : name;
    }

    /// <summary>§410. The table row the pointer is on lights its map's
    /// boxes; null lights none.</summary>
    public void HoverRow(MapsLocationRow? row)
    {
        Hovered = row is null
            ? Array.Empty<int>()
            : ShapesOf(row.Map);

        Redraw?.Invoke();
    }

    /// <summary>The map's row for a species: by name, and, §421, by the
    /// library's name for the row's name - so a page published while a
    /// record was still filed as "Galarian Linoone" answers to
    /// Linoone-Galarian until it is republished.</summary>
    private static SpawnPokemon? RowFor(SpawnMap map, string species) =>
        map.Pokemon.FirstOrDefault(p => string.Equals(p.Name, species, StringComparison.OrdinalIgnoreCase))
        ?? map.Pokemon.FirstOrDefault(p => string.Equals(PokemonSpriteService.ResolveLibraryName(p.Name), species, StringComparison.OrdinalIgnoreCase));

    // -------------------------------------------------------------- search

    /// <summary>The typed name, resolved: a species the library knows
    /// opens its card, a published map its own, a boss its own; anything
    /// else is said. Case-insensitive, whole name.</summary>
    [RelayCommand]
    private void Search()
    {
        string wanted = Query.Trim();

        if (wanted.Length == 0)
            return;

        // §421: through the library's one door for typed names, so a
        // regional form answers to every spelling - "Galarian Linoone",
        // "Linoone Galar", "Linoone-Galarian" - and opens the card filed
        // under the library's own. Before this only the species list was
        // consulted, so no regional form could be searched at all, however
        // it was spelled and whatever the Spawns pages held for it.
        string? species = PokemonSpriteService.ResolveLibraryName(wanted);

        if (species is not null)
        {
            ShowPokemon(species);
            return;
        }

        if (SpawnDataService.Find(wanted) is SpawnMap map)
        {
            ShowMap(map);
            return;
        }

        // §409: a boss, by its display name.
        if (BossCatalogService.FindByName(wanted) is BossInfo boss)
        {
            ShowBoss(boss);
            return;
        }

        // §419: "Pokéstop" for every stop; a stop by its name or place.
        if (IsPokestopWord(wanted))
        {
            ShowPokestop(null);
            return;
        }

        if (pinOwners.FirstOrDefault(p => PokestopCatalogService.IsPokestop(p)
                && (string.Equals(PokestopCatalogService.LabelFor(p), wanted, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(PokestopCatalogService.PlaceOf(p), wanted, StringComparison.OrdinalIgnoreCase))) is BossPin stop)
        {
            ShowPokestop(stop);
            return;
        }

        Status = $"Nothing called \"{wanted}\" - a Pokémon the library knows, a published map, a boss, or a Pokéstop.";
    }

    /// <summary>§410. A marker clicked: on a Pokémon's card, the row for
    /// that map is selected when the card lists it; otherwise the map's
    /// own card opens.</summary>
    public void PickMap(SpawnMap map)
    {
        // §412: a row whose map is found at this spot - its own box, or
        // linked to it.
        if (ShowsPokemon && Locations.FirstOrDefault(r => SpawnDataService.SpotFor(r.Map)?.Key == map.Key) is MapsLocationRow row)
        {
            SelectedLocation = row;
            return;
        }

        ShowMap(map);
        Query = CardTitle;
    }

    private void SetCard(bool pokemon, bool map, bool boss, bool stop = false)
    {
        ShowsPokemon = pokemon;
        ShowsMap = map;
        ShowsBoss = boss;
        ShowsPokestop = stop;
        ShowsNothing = !(pokemon || map || boss || stop);
    }

    /// <summary>§419. "Pokéstop", "Pokestop", "Pokéstops", "Pokestops".</summary>
    private static bool IsPokestopWord(string text)
    {
        string t = text.Trim().ToLowerInvariant().Replace('\u00e9', 'e');
        return t is "pokestop" or "pokestops" or "poke stop" or "poke stops";
    }

    /// <summary>§419. A Pokéstop's card - where it stands, its region's
    /// requirement, the cooldown and what it can give - with its pin lit;
    /// with no pin, every stop's: the same facts, each region's
    /// requirement, and every stop on the picture to pick from, all lit.</summary>
    public void ShowPokestop(BossPin? pin)
    {
        rebuilding = true;

        SelectedLocation = null;
        shownPokemon = null;
        focusedMap = null;
        shownBoss = null;
        shownStopKey = pin?.Key;
        shownAllStops = pin is null;

        List<BossPin> placed = pinOwners.Where(p => PokestopCatalogService.IsPokestop(p)).ToList();

        PokestopPortrait = PokestopCatalogService.Portrait();
        PokestopFacts.Clear();

        if (pin is not null)
        {
            string place = PokestopCatalogService.PlaceOf(pin);
            string region = PokestopCatalogService.FindFor(pin)?.Region ?? string.Empty;

            CardTitle = PokestopCatalogService.DisplayName;
            CardSubtitle = place;

            PokestopFacts.Add(new MapsFactRow("Location", place));
            PokestopFacts.Add(new MapsFactRow("Region", region.Length > 0 ? region : "-"));
            PokestopFacts.Add(new MapsFactRow("Requirement", PokestopCatalogService.RequirementFor(region)));
        }
        else
        {
            CardTitle = PokestopCatalogService.DisplayName + "s";
            CardSubtitle = $"{placed.Count} on the picture";

            foreach (string region in new[] { SpawnRegions.Kanto, SpawnRegions.Johto, SpawnRegions.Hoenn, SpawnRegions.Sinnoh })
                PokestopFacts.Add(new MapsFactRow($"Requirement ({region})", PokestopCatalogService.RequirementFor(region)));
        }

        PokestopFacts.Add(new MapsFactRow("Cooldown", PokestopCatalogService.CooldownText));
        PokestopFacts.Add(new MapsFactRow("Per visit", $"{PokestopCatalogService.ItemsPerVisit} items, at random"));

        PokestopRewards.Clear();

        foreach (PokestopReward reward in PokestopCatalogService.Rewards)
            PokestopRewards.Add(new MapsFactRow(reward.Item, reward.QuantityText));

        PokestopNote = $"Each visit gives {PokestopCatalogService.ItemsPerVisit} of these at random.";

        PlacedStops.Clear();

        if (pin is null)
        {
            List<string> order = SpawnRegions.All.ToList();

            foreach (MapsStopRow row in placed
                .Select(p => new MapsStopRow(p, PokestopCatalogService.PlaceOf(p), PokestopCatalogService.FindFor(p)?.Region ?? SpawnRegions.Other))
                .OrderBy(r => order.IndexOf(r.Region) < 0 ? order.Count : order.IndexOf(r.Region))
                .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase))
            {
                PlacedStops.Add(row);
            }
        }

        HasPlacedStops = PlacedStops.Count > 0;

        SetCard(pokemon: false, map: false, boss: false, stop: true);

        Status = pin is not null
            ? $"Pokéstop at {PokestopCatalogService.PlaceOf(pin)}."
            : placed.Count == 0
                ? "No Pokéstop is on the picture yet."
                : $"{placed.Count} Pokéstops on the picture.";

        rebuilding = false;

        SetHighlight();
        StartFlashing();

        if (pin is not null)
            BringPinIntoView();
    }

    /// <summary>A species' card: sprite and types, the encounter summary,
    /// abilities, base stats, weaknesses, and every published map that
    /// lists it and passes the filters, with its boxes lit.</summary>
    public void ShowPokemon(string species)
    {
        rebuilding = true;

        SelectedLocation = null;
        shownPokemon = species;
        focusedMap = null;
        shownBoss = null;
        shownStopKey = null;
        shownAllStops = false;

        IReadOnlyList<string> types = PokemonSpriteService.GetTypes(species);

        CardTitle = species;
        CardSubtitle = types.Count > 0 ? string.Join(" / ", types) : string.Empty;
        CardSprite = PokemonSpriteService.GetDisplaySprite(species);
        TypeIcon1 = types.Count > 0 ? PokemonSpriteService.GetTypeIcon(types[0]) : null;
        TypeIcon2 = types.Count > 1 ? PokemonSpriteService.GetTypeIcon(types[1]) : null;

        CalculatorDataService.CalcSpecies? calc = CalculatorDataService.Find(species);

        // §413: the dex first - every Pokémon and form the library knows -
        // and the calculator's pokedex for anything it lacks.
        DexEntry? dex = PokemonDexService.Find(species);

        FillAbilities(species, dex, calc);
        FillStats(species, dex, calc);
        FillWeaknesses(types);

        // The maps that list it, then the rows that pass the filters.
        List<SpawnMap> maps = SpawnDataService.Current
            .Where(m => RowFor(m, species) is not null)
            .ToList();

        List<string> order = SpawnRegions.All.ToList();

        Locations.Clear();

        foreach (SpawnMap map in maps
            .Where(PassesRegion)
            .OrderBy(m => order.IndexOf(m.Region))
            .ThenBy(m => m.Map, StringComparer.OrdinalIgnoreCase))
        {
            SpawnPokemon row = RowFor(map, species)!;

            if (!PassesRow(row))
                continue;

            Locations.Add(new MapsLocationRow
            {
                Map = map,
                Pokemon = row,
                TitleBrush = row.MembersOnly ? MembersBrush : InkBrush,
                Methods = EncounterMethods.Of(row),
                OnPicture = SpawnDataService.SpotFor(map) is not null,
                Levels = SpawnLevelService.Find(map.Map, species),
            });
        }

        FillSummary(species, maps);
        HasNoLocations = Locations.Count == 0;

        int shownCount = Locations.Count;
        LocationsHeading = shownCount == maps.Count
            ? $"Encounter Locations ({shownCount})"
            : $"Encounter Locations ({shownCount} of {maps.Count})";

        SetCard(pokemon: true, map: false, boss: false);

        int boxed = maps.Count(m => SpawnDataService.SpotFor(m) is not null);
        string mapWord = maps.Count == 1 ? "map" : "maps";
        Status = maps.Count == 0
            ? $"No published map lists {species} yet."
            : $"{species}: {DisplayNumber.Count(maps.Count)} published {mapWord}, {DisplayNumber.Count(boxed)} on the picture.";

        rebuilding = false;

        SetHighlight();
        StartFlashing();
        BringFirstIntoView();
    }

    private void FillSummary(string species, IReadOnlyList<SpawnMap> maps)
    {
        Summary.Clear();

        List<SpawnPokemon> rows = maps.Select(m => RowFor(m, species)!).ToList();

        if (rows.Count == 0)
        {
            Summary.Add(new MapsFactRow("Locations", "none published yet"));
            return;
        }

        List<string> order = SpawnRegions.All.ToList();
        var regions = maps.Select(m => m.Region).Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(r => order.IndexOf(r)).ToList();

        var methods = EncounterMethods.All.Where(m => rows.Any(r => EncounterMethods.Has(r, m)))
            .Select(EncounterMethods.Name).ToList();

        bool morning = rows.Any(r => r.Morning);
        bool day = rows.Any(r => r.Day);
        bool night = rows.Any(r => r.Night);
        string times = morning && day && night ? "All day"
            : !(morning || day || night) ? "-"
            : string.Join(", ", new[] { morning ? "Morning" : null, day ? "Day" : null, night ? "Night" : null }.Where(t => t is not null));

        int boxed = maps.Count(m => SpawnDataService.SpotFor(m) is not null);
        int members = rows.Count(r => r.MembersOnly);

        Summary.Add(new MapsFactRow("Regions", string.Join(", ", regions)));
        Summary.Add(new MapsFactRow("Locations", $"{maps.Count} ({boxed} on the picture)"));
        Summary.Add(new MapsFactRow("Methods", methods.Count > 0 ? string.Join(", ", methods) : "-"));
        Summary.Add(new MapsFactRow("Times", times));
        Summary.Add(new MapsFactRow("Members only", members == 0 ? "none" : members == maps.Count ? "every location" : $"{members} of {maps.Count}"));

        // §429: the species' levels across every location shared so far.
        List<SpawnLevelRange> ranges = maps
            .Select(m => SpawnLevelService.Find(m.Map, species))
            .OfType<SpawnLevelRange>()
            .ToList();

        Summary.Add(new MapsFactRow(
            "Levels",
            ranges.Count == 0
                ? "none shared yet"
                : $"{ranges.Min(r => r.Min)}-{ranges.Max(r => r.Max)} ({ranges.Count} of {maps.Count} locations)"));
    }

    private void FillAbilities(string species, DexEntry? dex, CalculatorDataService.CalcSpecies? calc)
    {
        Abilities.Clear();

        // §413: the dex's set, hidden one marked; the calculator's names
        // (no hidden flag) when the dex has none.
        IReadOnlyList<DexAbility> abilities = dex is { Abilities.Count: > 0 }
            ? dex.Abilities
            : (calc?.Abilities ?? Array.Empty<string>()).Select(n => new DexAbility(n, false)).ToList();

        foreach (DexAbility ability in abilities)
        {
            string description = AbilityLookupService.Find(ability.Name)?.Description ?? string.Empty;
            Abilities.Add(new MapsAbilityRow(ability.Name, description.Length > 0 ? description : "-", ability.Hidden));
        }

        AbilitiesNote = abilities.Count > 0 ? string.Empty : $"No abilities on file for {species}.";
        HasAbilitiesNote = AbilitiesNote.Length > 0;
    }

    private void FillStats(string species, DexEntry? dex, CalculatorDataService.CalcSpecies? calc)
    {
        Stats.Clear();

        // §413: the dex's numbers first, the calculator's otherwise - HP,
        // Attack, Defense, Sp. Atk, Sp. Def, Speed.
        int[]? stats =
            dex is { HasStats: true } ? new[] { dex.Hp, dex.Attack, dex.Defense, dex.SpAttack, dex.SpDefense, dex.Speed }
            : calc is not null ? new[] { calc.BaseHp, calc.BaseAttack, calc.BaseDefense, calc.BaseSpAttack, calc.BaseSpDefense, calc.BaseSpeed }
            : null;

        if (stats is null)
        {
            StatsTotal = string.Empty;
            StatsNote = $"No base stats on file for {species}.";
            HasStatsNote = true;
            return;
        }

        string[] labels = { "HP", "Attack", "Defense", "Sp. Atk", "Sp. Def", "Speed" };

        for (int i = 0; i < labels.Length; i++)
            Stats.Add(new MapsStatRow(labels[i], stats[i], StatBar(stats[i])));

        int total = stats.Sum();
        StatsTotal = $"Total {total}";
        StatsNote = string.Empty;
        HasStatsNote = false;
    }

    private static IBrush StatBar(int value) => value < 60 ? LowStat : value < 90 ? MidStat : HighStat;

    private void FillWeaknesses(IReadOnlyList<string> types)
    {
        Weaknesses.Clear();

        if (types.Count == 0)
        {
            WeaknessesNote = "No types on file.";
            HasWeaknessesNote = true;
            return;
        }

        // The chart's order within a multiplier, 4× before 2×.
        var found = new List<MapsTypeRow>();

        foreach (string type in AllTypes)
        {
            double times = PokemonBattleMath.GetTypeEffectiveness(type, types);

            if (times >= 4)
                found.Insert(found.Count(r => r.Multiplier == FourTimes), new MapsTypeRow(type, PokemonSpriteService.GetTypeIcon(type), FourTimes, TypeBrush(type)));
            else if (times >= 2)
                found.Add(new MapsTypeRow(type, PokemonSpriteService.GetTypeIcon(type), TwoTimes, TypeBrush(type)));
        }

        foreach (MapsTypeRow row in found)
            Weaknesses.Add(row);

        WeaknessesNote = Weaknesses.Count > 0 ? string.Empty : "No weaknesses.";
        HasWeaknessesNote = WeaknessesNote.Length > 0;
    }

    /// <summary>A map's card: the species on its page, with its box lit.</summary>
    public void ShowMap(SpawnMap map)
    {
        rebuilding = true;

        // §412: the maps sharing its spot. A spot drawn only to hold a group
        // opens on the group's first map.
        SpawnMap? spot = SpawnDataService.SpotFor(map);
        IReadOnlyList<SpawnMap> areas = spot is null ? new[] { map } : AreasOf(spot);

        if (areas.Count > 0 && !areas.Any(a => a.Key == map.Key))
            map = areas[0];

        SelectedLocation = null;
        shownPokemon = null;
        focusedMap = map;
        shownBoss = null;
        shownStopKey = null;
        shownAllStops = false;

        Areas.Clear();

        if (areas.Count > 1)
        {
            foreach (SpawnMap area in areas)
                Areas.Add(new MapsAreaRow(area, area.Map, area.PokemonCountText));
        }

        HasAreas = Areas.Count > 1;
        SelectedArea = Areas.FirstOrDefault(a => a.Map.Key == map.Key);
        AreasHeading = spot is null ? string.Empty : $"{SpotNameOf(spot)} · {Areas.Count} maps share this spot";

        CardTitle = map.Map;
        CardSubtitle = map.Region + (spot is null ? " · not on the picture yet" : string.Empty);
        CardSprite = null;
        TypeIcon1 = null;
        TypeIcon2 = null;

        MapRows.Clear();

        foreach (SpawnPokemon pokemon in map.Pokemon
            .Where(PassesRow)
            .OrderBy(p => p.Name, StringComparer.OrdinalIgnoreCase))
        {
            MapRows.Add(new MapsLocationRow
            {
                Map = map,
                Pokemon = pokemon,
                TitleBrush = pokemon.MembersOnly ? MembersBrush : InkBrush,
                Sprite = PokemonSpriteService.GetDisplaySprite(pokemon.Name),
                Methods = EncounterMethods.Of(pokemon),
                Levels = SpawnLevelService.Find(map.Map, pokemon.Name),
            });
        }

        HasNoMapRows = MapRows.Count == 0;
        SetCard(pokemon: false, map: true, boss: false);

        Status = map.Pokemon.Count == 0
            ? $"{map.Map} has no Pokémon on its page yet."
            : MapRows.Count == map.Pokemon.Count
                ? $"{map.Map}: {map.PokemonCountText}."
                : $"{map.Map}: {MapRows.Count} of {map.Pokemon.Count} Pokémon pass the filters.";

        rebuilding = false;

        SetHighlight();
        StartFlashing();
        BringFirstIntoView();
    }

    /// <summary>§412. Another map of the spot picked on the map card: its
    /// species. The card's own rebuild is not a pick.</summary>
    partial void OnSelectedAreaChanged(MapsAreaRow? value)
    {
        if (rebuilding || value is null || !ShowsMap || focusedMap?.Key == value.Map.Key)
            return;

        ShowMap(value.Map);
        Query = value.Name;
    }

    /// <summary>A boss's card: picture, where it stands, cooldown,
    /// requirement, and its pin lit on the picture.</summary>
    public void ShowBoss(BossInfo boss)
    {
        rebuilding = true;

        SelectedLocation = null;
        shownBoss = boss;
        shownStopKey = null;
        shownAllStops = false;
        shownPokemon = null;
        focusedMap = null;

        CardTitle = boss.Name;
        CardSubtitle = "Boss";
        BossPortrait = BossCatalogService.Portrait(boss.BossId);

        BossFacts.Clear();
        BossFacts.Add(new MapsFactRow("Location", boss.Location.Length > 0 ? boss.Location : "-"));
        BossFacts.Add(new MapsFactRow("Cooldown", boss.CooldownText.Length > 0 ? boss.CooldownText : "-"));
        BossFacts.Add(new MapsFactRow("Requirements", boss.Requirements.Length > 0 ? boss.Requirements : "-"));

        BossPin? pin = BossPinService.Find(boss.BossId);
        BossPinText = pin is null ? "Not placed on the picture yet." : string.Empty;

        SetCard(pokemon: false, map: false, boss: true);

        Status = pin is null ? $"{boss.Name}: no pin on the picture yet." : $"{boss.Name}, pinned on the picture.";

        rebuilding = false;

        SetHighlight();
        StartFlashing();
        BringPinIntoView();
    }

    [RelayCommand]
    private void OpenShownBoss()
    {
        if (shownBoss is BossInfo boss)
            OpenBoss?.Invoke(boss);
    }

    /// <summary>§410. The table's selection: a row selected lights its
    /// map's boxes alone and brings the first into view; none selected
    /// lights every found box again. The table's own clearing while a
    /// card is rebuilt is not a selection.</summary>
    partial void OnSelectedLocationChanged(MapsLocationRow? value)
    {
        if (rebuilding || !ShowsPokemon)
            return;

        if (value is null)
        {
            focusedMap = null;
            SetHighlight();
            return;
        }

        FocusMap(value.Map);
    }

    /// <summary>A map on a Pokémon card, picked: its boxes alone light,
    /// and the first comes into view. The card stays.</summary>
    public void FocusMap(SpawnMap map)
    {
        focusedMap = map;
        SetHighlight();
        StartFlashing();
        BringFirstIntoView();
    }

    private static IBrush TypeBrush(string type) => new SolidColorBrush(PokemonTypeColors.For(type));

    // --------------------------------------------------------- highlight

    private void SetHighlight()
    {
        string? species = shownPokemon;

        // §412: a map lights the spot it is found at; a species, every spot
        // where one of the maps found there lists it.
        Highlighted = focusedMap is SpawnMap map
            ? ShapesOf(map)
            : species is not null
                ? Enumerable.Range(0, shapeOwners.Count)
                    .Where(i => MembersOf(shapeOwners[i]).Any(m => RowFor(m, species) is SpawnPokemon row && PassesRow(row)))
                    .ToList()
                : Array.Empty<int>();

        // §410: the method glyphs on a found box - how the shown species
        // is met there (§412: at a shared spot, in any of its maps; in the
        // one picked, when a row is picked). A map's or a boss's card
        // draws none.
        var methods = new Dictionary<int, IReadOnlyList<EncounterMethod>>();

        if (species is not null)
        {
            foreach (int i in Highlighted)
            {
                IEnumerable<SpawnMap> where = focusedMap is SpawnMap picked ? new[] { picked } : MembersOf(shapeOwners[i]);

                List<SpawnPokemon> rows = where
                    .Select(m => RowFor(m, species))
                    .OfType<SpawnPokemon>()
                    .Where(PassesRow)
                    .ToList();

                List<EncounterMethod> ways = EncounterMethods.All
                    .Where(w => rows.Any(r => EncounterMethods.Has(r, w)))
                    .ToList();

                if (ways.Count > 0)
                    methods[i] = ways;
            }
        }

        BoxMethods = methods;

        // §409: the shown boss's pin; §419: the shown stop's, or every stop's.
        string? stopKey = shownStopKey;

        HighlightedPins = shownBoss is BossInfo boss
            ? Enumerable.Range(0, pinOwners.Count).Where(i => pinOwners[i].Key == SpawnMap.KeyFor(boss.BossId)).ToList()
            : stopKey is not null
                ? Enumerable.Range(0, pinOwners.Count).Where(i => pinOwners[i].Key == stopKey).ToList()
                : shownAllStops
                    ? Enumerable.Range(0, pinOwners.Count).Where(i => PokestopCatalogService.IsPokestop(pinOwners[i])).ToList()
                    : Array.Empty<int>();

        Redraw?.Invoke();
    }

    private void BringFirstIntoView()
    {
        if (Highlighted.Count > 0 && Highlighted[0] < Shapes.Count)
            BringIntoView?.Invoke(Shapes[Highlighted[0]]);
    }

    private void BringPinIntoView()
    {
        if (HighlightedPins.Count > 0 && HighlightedPins[0] < PinShapes.Count)
        {
            MapPinShape pin = PinShapes[HighlightedPins[0]];
            BringIntoView?.Invoke(new MapBoxShape(pin.Label, pin.X, pin.Y, 1, 1));
        }
    }

    private void StartFlashing()
    {
        flashTicksLeft = FlashTicks;
        FlashOn = true;
        flashTimer.Stop();

        if (Highlighted.Count > 0 || HighlightedPins.Count > 0)
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

    /// <summary>§410. Back to one picture pixel per screen pixel.</summary>
    [RelayCommand]
    private void ZoomReset() => Zoom = 1;

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

    public void Dispose()
    {
        flashTimer.Stop();
        flashTimer.Tick -= OnFlashTick;
        SpawnDataService.Changed -= OnSpawnsChanged;
        BossPinService.Changed -= OnPinsChanged;
        SpawnLevelService.Changed -= OnLevelsChanged;
    }
}
