using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;
using Foot_Tracker.Services.Simulator;

namespace Foot_Tracker.ViewModels;

/// <summary>One stored Pokemon as the picker lists it.</summary>
public sealed partial class SimulatorStorageCard : ObservableObject
{
    public required StoredPokemon Entry { get; init; }
    public required string Headline { get; init; }
    public required string Detail { get; init; }

    [ObservableProperty] private Bitmap? sprite;

    /// <summary>§203: already chosen this visit. The card stays on the
    /// page and says so rather than vanishing, so a mis-click is visible
    /// and the same Pokemon cannot be added twice by accident.</summary>
    [ObservableProperty] private bool isPicked;
}

/// <summary>
/// §162. The Pokemon storage picker: everything ever imported from the
/// game, newest first - click one to drop it into the team, or delete the
/// entries that no longer matter. The storage itself fills automatically
/// on every successful import.
///
/// §203: it stays open. Clicking a Pokemon used to close the window, so
/// filling a team meant reopening this dialog once per slot; now a click
/// banks one and the window waits for Done. And it pages, eight at a time -
/// a box of eighty in one scroll is a list, not a box.
///
/// Replace from Storage (§199) still wants exactly one, so it opens the
/// same window with AllowMultiple off and the first click closes it.
/// </summary>
public sealed partial class SimulatorStorageViewModel : ViewModelBase
{
    /// <summary>§203. How many cards a page holds.</summary>
    public const int PageSize = 8;

    /// <summary>Everything that matched, across all pages - Cards is the
    /// slice of this the current page shows.</summary>
    readonly List<SimulatorStorageCard> allCards = new();

    public ObservableCollection<SimulatorStorageCard> Cards { get; } = new();

    [ObservableProperty] private string statusText = "";

    // ---- §203: paging ----
    [ObservableProperty] private string pageText = "";
    [ObservableProperty] private bool canGoBack;
    [ObservableProperty] private bool canGoForward;
    [ObservableProperty] private bool hasPages;

    int pageIndex;

    /// <summary>§203: false for Replace from Storage, which swaps one
    /// Pokemon and has nothing to do with a second pick.</summary>
    public bool AllowMultiple { get; init; } = true;

    /// <summary>§275. How many team slots are free when this window opens -
    /// the most that can be banked here before the team is full.
    ///
    /// Replace from Storage does not consult it: that is a swap, and a full
    /// team is exactly the situation it exists for. Left at int.MaxValue by
    /// default so nothing that forgets to set it is quietly capped at
    /// zero - a picker that refuses every pick is a worse bug than one that
    /// lets §203's status line report the overflow as before.</summary>
    public int FreeTeamSlots { get; init; } = int.MaxValue;

    /// <summary>§275. How big a full team is, for the refusal's wording
    /// only.</summary>
    public int MaxTeamSize { get; init; } = 6;

    /// <summary>§275. Set by the View to show an OK-only notice. The full
    /// team refusal goes through here rather than into StatusText: §198 put
    /// it in a status line and §203 left it there, and a line at the top of
    /// a window whose cards are at the bottom is a line nobody reads while
    /// clicking. This is the one thing in this window that has to be seen.</summary>
    public Func<string, Task>? WarnAsync { get; set; }

    /// <summary>§203: everything chosen this visit, in the order it was
    /// chosen. The caller reads this however the window was dismissed -
    /// closing it with the X must not throw away picks already made.</summary>
    public List<StoredPokemon> Picked { get; } = new();

    /// <summary>The first pick, for the single-pick callers. Kept for
    /// them rather than for the multi-pick path, which reads Picked.</summary>
    public StoredPokemon? Selected => Picked.Count > 0 ? Picked[0] : null;

    public event Action? Confirmed;

    public SimulatorStorageViewModel()
    {
        Refill();
    }

    void Refill()
    {
        allCards.Clear();

        foreach (StoredPokemon entry in SimulatorPokemonStorage.All()
                     .OrderByDescending(e => e.AddedUtc))
        {
            allCards.Add(new SimulatorStorageCard
            {
                Entry = entry,
                Headline = (entry.IsShiny ? "Shiny " : "") +
                           $"{entry.SpeciesName}  Lv. {entry.Level}  {entry.NatureName}" +
                           (string.IsNullOrWhiteSpace(entry.AbilityName) ? "" : $"  -  {entry.AbilityName}"),
                Detail = string.Join("  /  ", entry.MoveNames),
                // A Pokemon picked before a delete rebuilt the list keeps
                // its tick - the pick is of the entry, not of the card.
                IsPicked = Picked.Any(p => Same(p, entry))
            });
        }

        ShowPage(pageIndex);
    }

    /// <summary>§203. The page's eight cards. Sprites load per page rather
    /// than for the whole box, so opening this with eighty Pokemon in
    /// storage reads eight files instead of eighty.</summary>
    void ShowPage(int index)
    {
        int pages = Math.Max(1, (allCards.Count + PageSize - 1) / PageSize);

        pageIndex = Math.Clamp(index, 0, pages - 1);

        Cards.Clear();

        foreach (SimulatorStorageCard card in allCards.Skip(pageIndex * PageSize).Take(PageSize))
        {
            Cards.Add(card);

            if (card.Sprite == null)
                _ = LoadSpriteAsync(card);
        }

        HasPages = allCards.Count > PageSize;
        CanGoBack = pageIndex > 0;
        CanGoForward = pageIndex < pages - 1;
        PageText = $"Page {pageIndex + 1} of {pages}";

        RefreshStatus();
    }

    void RefreshStatus()
    {
        if (allCards.Count == 0)
        {
            StatusText = "Nothing stored yet - Pokemon land here automatically when you import them from the game.";
            return;
        }

        string what = AllowMultiple
            ? "click as many as you want, then press Done"
            : "click one to swap it in";

        StatusText = Picked.Count == 0
            ? $"{allCards.Count} stored - {what}."
            : $"{allCards.Count} stored, {Picked.Count} chosen ({string.Join(", ", Picked.Select(p => p.SpeciesName))}) - {what}.";
    }

    /// <summary>The identity SimulatorPokemonStorage itself matches on.</summary>
    static bool Same(StoredPokemon a, StoredPokemon b) =>
        (a.GameId != null && a.GameId == b.GameId) || a.Fingerprint == b.Fingerprint;

    [RelayCommand]
    private void NextPage() => ShowPage(pageIndex + 1);

    [RelayCommand]
    private void PreviousPage() => ShowPage(pageIndex - 1);

    async Task LoadSpriteAsync(SimulatorStorageCard card)
    {
        string species = card.Entry.SpeciesName;

        bool shiny = card.Entry.IsShiny;

        Bitmap? sprite = await Task.Run(() => shiny
            ? PokemonSpriteService.GetShinyEncounterSprite(species)
            : PokemonSpriteService.GetEncounterSprite(species));

        if (Cards.Contains(card))
            card.Sprite = sprite;
    }

    /// <summary>§203: a click banks the Pokemon and the window stays open.
    /// Clicking an already-picked one takes it back off, because the only
    /// other thing a second click could mean is a mistake.</summary>
    [RelayCommand]
    private async Task PickCard(SimulatorStorageCard? card)
    {
        if (card == null)
            return;

        if (!AllowMultiple)
        {
            Picked.Clear();
            Picked.Add(card.Entry);
            Confirmed?.Invoke();
            return;
        }

        int already = Picked.FindIndex(p => Same(p, card.Entry));

        if (already >= 0)
        {
            Picked.RemoveAt(already);
            card.IsPicked = false;
            RefreshStatus();
            return;
        }

        // §275: the pick that would not fit is refused HERE, at the click,
        // rather than banked and dropped on the way out. Taking six picks
        // and then reporting that four of them stayed in storage is a window
        // that let the player do something it never intended to honour.
        //
        // Tested against what is already banked, not just against the team:
        // with four on the team and two picked, the third click is the one
        // that overflows, and it is that click that has to say so.
        if (Picked.Count >= FreeTeamSlots)
        {
            await Warn();
            return;
        }

        Picked.Add(card.Entry);
        card.IsPicked = true;

        RefreshStatus();
    }

    /// <summary>§275. Why the pick was refused - the team is full, and the
    /// way in is the team card's own Replace from Storage. Names that
    /// command exactly as the team menu spells it, because a message that
    /// sends the player looking for a button under another name is not
    /// help.</summary>
    private async Task Warn()
    {
        if (WarnAsync == null)
            return;

        string message = Picked.Count == 0
            ? $"Your team already has {MaxTeamSize} Pokemon, so there is no slot for this one.\n\n"
              + "Use Replace from Storage on a team card to swap one in."
            : $"That would be more than {MaxTeamSize} Pokemon. You have already chosen "
              + $"{Picked.Count} here, which fills the team.\n\n"
              + "Use Replace from Storage on a team card to swap another one in.";

        await WarnAsync(message);
    }

    [RelayCommand]
    private void DeleteCard(SimulatorStorageCard? card)
    {
        if (card == null)
            return;

        SimulatorPokemonStorage.Remove(card.Entry);

        // A deleted Pokemon cannot join a team, so it leaves the picks too.
        Picked.RemoveAll(p => Same(p, card.Entry));

        Refill();
    }
}
