using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services.Simulator;
using PokemonSim.Models;
using PokemonSim.Simulation;
using Serilog;

namespace Foot_Tracker.ViewModels;

/// <summary>§173. One editable slot of a custom opponent. Everything is
/// typed by hand - species, nature, ability, item and four moves - and the
/// spread is the two things a PRO player actually decides: the six IVs,
/// and which stats carry 252 EVs. IVs are held as text so a half-typed box
/// is not an error; they are parsed (and defaulted to 31) on save.</summary>
public sealed partial class CustomBossSlotViewModel : ViewModelBase
{
    [ObservableProperty] private string species = "";
    [ObservableProperty] private string natureName = "Hardy";
    [ObservableProperty] private string ability = "";
    [ObservableProperty] private string item = "";

    [ObservableProperty] private string move1 = "";
    [ObservableProperty] private string move2 = "";
    [ObservableProperty] private string move3 = "";
    [ObservableProperty] private string move4 = "";

    [ObservableProperty] private string ivHp = "31";
    [ObservableProperty] private string ivAttack = "31";
    [ObservableProperty] private string ivDefense = "31";
    [ObservableProperty] private string ivSpAttack = "31";
    [ObservableProperty] private string ivSpDefense = "31";
    [ObservableProperty] private string ivSpeed = "31";

    // §174: any value, not just 252 - "100, 0, 0, 152, 0, 252" is a
    // perfectly ordinary competitive spread and the file format always
    // accepted it. Text for the same reason the IV boxes are text.
    [ObservableProperty] private string evHp = "0";
    [ObservableProperty] private string evAttack = "0";
    [ObservableProperty] private string evDefense = "0";
    [ObservableProperty] private string evSpAttack = "0";
    [ObservableProperty] private string evSpDefense = "0";
    [ObservableProperty] private string evSpeed = "0";

    [ObservableProperty] private string evTotal = "Total 0";

    // §199: the picture this slot battles with, when the author wants one
    // the species name cannot reach - a counterpart skin, or any file they
    // browsed to. Empty is the ordinary artwork for the species, which is
    // what every roster written before §199 says by saying nothing.
    // §200: the exact form, by the id the sprite library names its files
    // with. Text for the same reason the IV boxes are text - a half-typed
    // box is not an error.
    [ObservableProperty] private string dexNumberText = "";

    [ObservableProperty] private string spritePath = "";
    [ObservableProperty] private Avalonia.Media.Imaging.Bitmap? spritePreview;
    [ObservableProperty] private string spriteNote = "";
    [ObservableProperty] private bool hasSprite;
    [ObservableProperty] private bool hasSpriteNote;

    public string SlotLabel { get; init; } = "Slot";

    /// <summary>§199: the window opens the sprite picker or a file browser
    /// and hands back a path (null = cancelled). Supplied by
    /// CustomBossWindow, which owns the dialogs.</summary>
    public Func<bool, Task<string?>>? RequestSprite { get; set; }

    public IReadOnlyList<string> NatureNames { get; } = Enum.GetNames<Nature>();

    /// <summary>§199. The preview follows the box, so a path typed by hand
    /// is checked the moment it is typed rather than at the next
    /// battle.</summary>
    partial void OnSpritePathChanged(string value) => RefreshSprite();

    /// <summary>§200: the preview answers the dex box too, so typing 10233
    /// shows you a Hisuian Typhlosion rather than leaving you to find out
    /// at the next battle.</summary>
    partial void OnDexNumberTextChanged(string value) => RefreshSprite();

    void RefreshSprite()
    {
        string path = (SpritePath ?? "").Trim();
        int dex = Dex(DexNumberText);

        HasSprite = path.Length > 0 || dex > 0;

        if (!HasSprite)
        {
            SpritePreview = null;
            SpriteNote = "";
            HasSpriteNote = false;
            return;
        }

        // §200: the same ladder the battle walks, so what is previewed here
        // is what will actually be drawn - an explicit file first, then the
        // exact form, and the species name only when neither was given.
        if (path.Length > 0)
        {
            // §139's cached, card-sized read - the same one the encounter
            // cards use, and it takes an absolute path too (GetCardSprite).
            SpritePreview = Services.CounterpartSpriteService.GetCardSprite(path);

            // This is the one place a missing file can be reported: the
            // format class deliberately does not touch a disk, so nothing
            // downstream of here can tell the author their path is wrong.
            SpriteNote = SpritePreview == null
                ? "That file could not be read - the battle will fall back to the ordinary artwork."
                : "";
        }
        else
        {
            SpritePreview = Services.PokemonSpriteService.GetSpriteByDexNumber(dex);

            SpriteNote = SpritePreview == null
                ? $"The sprite library has no {dex}.png - the battle will fall back to the species name."
                : "";
        }

        HasSpriteNote = SpriteNote.Length > 0;
    }

    /// <summary>§200. A dex box that is empty or nonsense means "work it out
    /// from the species name", which is what every roster written before
    /// this said.</summary>
    static int Dex(string? text) =>
        int.TryParse(text?.Trim(), out int value) && value > 0 ? value : 0;

    /// <summary>§199. Choose from the counterpart skins, or browse to any
    /// file. Both come back through the same delegate; the bool says
    /// which dialog the window should open.</summary>
    [RelayCommand]
    private async Task ChooseSprite(string? mode)
    {
        if (RequestSprite == null)
            return;

        string? picked = await RequestSprite(mode == "browse");

        if (picked != null)
            SpritePath = picked;
    }

    [RelayCommand]
    private void ClearSprite() => SpritePath = "";

    partial void OnEvHpChanged(string value) => RefreshEvTotal();
    partial void OnEvAttackChanged(string value) => RefreshEvTotal();
    partial void OnEvDefenseChanged(string value) => RefreshEvTotal();
    partial void OnEvSpAttackChanged(string value) => RefreshEvTotal();
    partial void OnEvSpDefenseChanged(string value) => RefreshEvTotal();
    partial void OnEvSpeedChanged(string value) => RefreshEvTotal();

    /// <summary>The games give 510 EVs to spend; PRO's own hard bosses
    /// ignore that (§164 puts 400 in every stat), so this reports rather
    /// than restricts - it just says when a spread is past the usual
    /// budget.</summary>
    void RefreshEvTotal()
    {
        int[] evs = EvArray();
        int total = evs.Sum();

        EvTotal = total <= 510
            ? $"Total {total}"
            : $"Total {total} (over the games' 510 budget)";
    }

    int[] EvArray() => new[]
    {
        Ev(EvHp), Ev(EvAttack), Ev(EvDefense),
        Ev(EvSpAttack), Ev(EvSpDefense), Ev(EvSpeed)
    };

    public void Apply(CustomOpponentPokemon mon)
    {
        Species = mon.Species;
        NatureName = string.IsNullOrWhiteSpace(mon.Nature) ? "Hardy" : mon.Nature;
        Ability = mon.Ability ?? "";
        Item = mon.Item ?? "";
        SpritePath = mon.Sprite ?? "";
        DexNumberText = mon.DexNumber is int dex && dex > 0 ? dex.ToString() : "";

        List<string> moves = mon.Moves.ToList();
        Move1 = moves.ElementAtOrDefault(0) ?? "";
        Move2 = moves.ElementAtOrDefault(1) ?? "";
        Move3 = moves.ElementAtOrDefault(2) ?? "";
        Move4 = moves.ElementAtOrDefault(3) ?? "";

        int[] ivs = CustomOpponents.ResolveIvs(mon);
        IvHp = ivs[0].ToString();
        IvAttack = ivs[1].ToString();
        IvDefense = ivs[2].ToString();
        IvSpAttack = ivs[3].ToString();
        IvSpDefense = ivs[4].ToString();
        IvSpeed = ivs[5].ToString();

        // Either form in the file - maxEvs shorthand or an explicit list -
        // arrives here already resolved to six numbers.
        int[] evs = CustomOpponents.ResolveEvs(mon);
        EvHp = evs[0].ToString();
        EvAttack = evs[1].ToString();
        EvDefense = evs[2].ToString();
        EvSpAttack = evs[3].ToString();
        EvSpDefense = evs[4].ToString();
        EvSpeed = evs[5].ToString();

        RefreshEvTotal();
    }

    /// <summary>Null when the slot is empty - an unnamed species is a slot
    /// the author simply did not use.</summary>
    public CustomOpponentPokemon? ToPokemon()
    {
        if (string.IsNullOrWhiteSpace(Species))
            return null;

        // §174: a spread of nothing but 252s and zeroes keeps the readable
        // shorthand in the file; anything else is written out in full.
        int[] evs = EvArray();
        List<string>? maxEvs = CustomOpponents.AsMaxEvNames(evs);

        int dexNumber = Dex(DexNumberText);

        return new CustomOpponentPokemon
        {
            Species = Species.Trim(),
            Nature = string.IsNullOrWhiteSpace(NatureName) ? null : NatureName,
            Ability = string.IsNullOrWhiteSpace(Ability) ? null : Ability.Trim(),
            Item = string.IsNullOrWhiteSpace(Item) ? null : Item.Trim(),
            // §199: forward-slashed and nulled-if-empty by the format
            // itself, so a path browsed to on Windows still resolves for a
            // Linux user opening the same roster.
            Sprite = CustomOpponents.NormalizeSpritePath(SpritePath),
            // §200: null rather than 0, so a slot that names no form leaves
            // the field out of the file entirely.
            DexNumber = dexNumber > 0 ? dexNumber : null,
            Moves = new[] { Move1, Move2, Move3, Move4 }
                .Where(m => !string.IsNullOrWhiteSpace(m))
                .Select(m => m.Trim())
                .ToList(),
            Ivs = new List<int>
            {
                Iv(IvHp), Iv(IvAttack), Iv(IvDefense),
                Iv(IvSpAttack), Iv(IvSpDefense), Iv(IvSpeed)
            },
            MaxEvs = maxEvs,
            Evs = maxEvs == null ? evs.ToList() : null
        };
    }

    /// <summary>A box that is empty or nonsense means "perfect", which is
    /// what a boss slot almost always wants.</summary>
    static int Iv(string text) =>
        int.TryParse(text?.Trim(), out int value) ? Math.Clamp(value, 0, 31) : 31;

    /// <summary>An EV box: empty or nonsense is none. The ceiling is
    /// §164's 512, not the games' 252, because PRO's hard bosses really
    /// do put 400 in a stat and the factory accepts it.</summary>
    static int Ev(string text) =>
        int.TryParse(text?.Trim(), out int value) ? Math.Clamp(value, 0, 512) : 0;
}

/// <summary>
/// §173. The custom opponent editor: six hand-typed slots, saved as one
/// .json in the writable CustomBosses folder. It never edits a shipped
/// file in place - opening one and saving makes your own copy, which then
/// wins on load, so a build's own custom opponents cannot be lost by
/// editing them.
/// </summary>
public sealed partial class CustomBossViewModel : ViewModelBase
{
    [ObservableProperty] private string name = "";
    [ObservableProperty] private string notes = "";
    [ObservableProperty] private string levelText = "100";
    [ObservableProperty] private string status = "";
    [ObservableProperty] private string fileHint = "";

    public ObservableCollection<CustomBossSlotViewModel> Slots { get; } = new();

    /// <summary>Raised when a save succeeded - the window closes on it.</summary>
    public event Action? Saved;

    public CustomBossViewModel()
    {
        for (int i = 1; i <= CustomOpponents.MaxTeamSize; i++)
            Slots.Add(new CustomBossSlotViewModel { SlotLabel = $"Slot {i}" });

        FileHint = "Saves to " + CustomBossStore.WritableFolder;
    }

    /// <summary>Loads an existing opponent for editing. Null starts a new
    /// one, which is what the New button hands over.</summary>
    public void Load(CustomBossEntry? entry)
    {
        if (entry == null)
        {
            Status = "New custom opponent - name it, fill at least one slot, then Save.";
            return;
        }

        Name = entry.Team.Name;
        Notes = entry.Team.Notes ?? "";
        LevelText = entry.Team.Level?.ToString() ?? "100";

        for (int i = 0; i < Slots.Count && i < entry.Team.Team.Count; i++)
            Slots[i].Apply(entry.Team.Team[i]);

        Status = entry.Editable
            ? $"Editing {entry.Title}."
            : $"Editing {entry.Title} - it ships with the app, so saving makes your own copy.";
    }

    public CustomOpponentTeam Build()
    {
        var team = new CustomOpponentTeam
        {
            Name = Name.Trim(),
            Notes = string.IsNullOrWhiteSpace(Notes) ? null : Notes.Trim(),
            Level = int.TryParse(LevelText?.Trim(), out int level) && level >= 1 && level <= 100
                ? level
                : 100
        };

        foreach (CustomBossSlotViewModel slot in Slots)
        {
            CustomOpponentPokemon? mon = slot.ToPokemon();

            if (mon != null)
                team.Team.Add(mon);
        }

        return team;
    }

    [RelayCommand]
    private void Save()
    {
        CustomOpponentTeam team = Build();
        List<string> problems = CustomOpponents.Validate(team);

        // A missing name or an empty team is the one thing worth refusing:
        // the file would have nothing to select or nobody to battle.
        if (string.IsNullOrWhiteSpace(team.Name) || team.Team.Count == 0)
        {
            Status = string.Join("  ", problems);
            return;
        }

        try
        {
            string path = CustomBossStore.Save(team.Name, team);

            Status = problems.Count == 0
                ? $"Saved {System.IO.Path.GetFileName(path)}."
                : $"Saved {System.IO.Path.GetFileName(path)} - note: {string.Join("  ", problems)}";

            Saved?.Invoke();
        }
        catch (Exception ex)
        {
            Log.Warning(ex, "Custom opponents: save failed.");
            Status = "Could not save: " + ex.Message;
        }
    }
}
