using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

// Ported from Forms/BossTemplate/BossDifficulty.cs (was a top-level enum there too).
public enum BossDifficulty
{
    Easy,
    Medium,
    Hard
}

public sealed record RewardImageItem(Bitmap? Image, string? Caption = null)
{
    /// <summary>
    /// Backs a small type-icon row next to a Pokemon reward's name (Caption).
    /// Harmless no-op for Item Rewards, which reuse this same record with an
    /// item name/quantity as Caption - that text never matches a Pokemon
    /// name, so GetTypes just returns an empty list and the Item Rewards
    /// template (which doesn't bind to Types) never shows it anyway.
    /// </summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(Caption ?? string.Empty);
}

/// <summary>
/// One row of the selected Pokemon's move list. TypeName feeds a 36x36 type
/// icon through TypeIconConverter (the same converter every other type-icon
/// row uses); Detail is the category/power/effect text under the bold name.
/// A move MoveLookupService can't find keeps its plain name with no icon and
/// no detail - same degrade-gracefully contract as before the redesign, when
/// the type was plain text inside Detail instead of an icon.
/// </summary>
public sealed record BossMoveDisplayItem(string TypeName, string Name, string Detail)
{
    public bool HasTypeIcon => !string.IsNullOrWhiteSpace(TypeName);
    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);
}

/// <summary>
/// §108. One combatant's portrait in the strip beside the boss name. A
/// dual-NPC boss gets one per trainer and the strip's own selection chrome
/// marks the one being viewed; a single-NPC boss gets exactly one, shown but
/// not clickable, because there is nothing to switch to. Index is the
/// position in the file's NPC list, which is what actually drives the swap -
/// nothing here matches on the trainer's name, so two combatants sharing a
/// name (or having none) still switch correctly. A portrait that fails to
/// resolve leaves Portrait null and the strip falls back to the name as
/// text, the same degrade-gracefully contract the old switch button had.
/// </summary>
public sealed record BossNpcPortraitItem(int Index, string Name, Bitmap? Portrait)
{
    public bool HasPortrait => Portrait is not null;
}

/// <summary>
/// See MIGRATION_GUIDE.md §43/§92. One member of the displayed boss team -
/// the sprite button in the left column and, when selected, the summary card
/// and the Description/Moves panels all read from this one item.
/// </summary>
public sealed record BossTeamMemberItem(Bitmap? Sprite, BossPokemonData Pokemon)
{
    private Bitmap? itemIcon;
    private bool itemIconLoaded;

    /// <summary>
    /// See MIGRATION_GUIDE.md §45. Pokemon.Ability plus " - " and its
    /// description from AbilityLookupService, e.g. "Pure Power - Raises the
    /// Pokemon's Attack stat." Falls back to just the plain ability name (same
    /// "degrade gracefully" behavior as MoveDisplayItems below) when the
    /// ability isn't found or has no description on file.
    /// </summary>
    public string AbilityDisplayText
    {
        get
        {
            AbilityData? ability = AbilityLookupService.Find(Pokemon.Ability);

            if (ability is null || string.IsNullOrWhiteSpace(ability.Description))
                return string.IsNullOrWhiteSpace(Pokemon.Ability) ? "Unknown" : Pokemon.Ability;

            return $"{Pokemon.Ability} - {ability.Description}";
        }
    }

    public string NatureDisplayText =>
        string.IsNullOrWhiteSpace(Pokemon.Nature) ? "Unknown" : Pokemon.Nature;

    public string ItemDisplayText =>
        string.IsNullOrWhiteSpace(Pokemon.Item) ? "None" : Pokemon.Item;

    /// <summary>
    /// The held item's icon from SharedPokemonLibrary/Assets/Items, resolved
    /// by the same Spaces_To_Underscores.png naming the boss files' own item
    /// reward pictures already follow (e.g. "Focus Sash" ->
    /// "Focus_Sash.png"). Null - and the icon simply doesn't render - for
    /// "None", empty values, or names with no matching file; loaded at most
    /// once per team member.
    /// </summary>
    public Bitmap? ItemIcon
    {
        get
        {
            if (!itemIconLoaded)
            {
                itemIconLoaded = true;
                itemIcon = LoadHeldItemIcon(Pokemon.Item);
            }

            return itemIcon;
        }
    }

    public bool HasItemIcon => ItemIcon is not null;

    /// <summary>
    /// Pokédex description from the newest mainline game that has one - see
    /// PokemonDescriptionService (§93). Form names resolve to their base
    /// species there, and anything unresolvable falls back to the same
    /// clean placeholder line the panel showed before descriptions existed.
    /// </summary>
    public string DescriptionDisplayText =>
        PokemonDescriptionService.Find(Pokemon.Name)
        ?? "No description data for this Pokémon yet.";

    public IReadOnlyList<BossMoveDisplayItem> MoveDisplayItems =>
        Pokemon.Moves.Select(BuildMoveDisplayItem).ToList();

    /// <summary>Backs a small type-icon row next to this team member's name.</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(Pokemon.Name);

    private static BossMoveDisplayItem BuildMoveDisplayItem(string moveName)
    {
        MoveData? move = MoveLookupService.Find(moveName);

        if (move is null)
            return new BossMoveDisplayItem(string.Empty, moveName, string.Empty);

        var segments = new List<string>();

        if (!string.IsNullOrWhiteSpace(move.Category))
            segments.Add(move.Category);

        if (move.Power is int power)
            segments.Add($"{power} Power");

        if (!string.IsNullOrWhiteSpace(move.Effect))
            segments.Add(move.Effect);

        return new BossMoveDisplayItem(move.Type, moveName, string.Join(" - ", segments));
    }

    private static Bitmap? LoadHeldItemIcon(string? itemName)
    {
        if (string.IsNullOrWhiteSpace(itemName))
            return null;

        string trimmed = itemName.Trim();

        if (trimmed.Equals("None", StringComparison.OrdinalIgnoreCase))
            return null;

        string fullPath = Path.Combine(
            AppContext.BaseDirectory,
            "SharedPokemonLibrary",
            "Assets",
            "Items",
            trimmed.Replace(' ', '_') + ".png");

        return File.Exists(fullPath) ? new Bitmap(fullPath) : null;
    }
}

/// <summary>
/// Ported from Forms/BossTemplate/BossTemplate.cs; redesigned per the user's
/// wireframe in MIGRATION_GUIDE.md §92. Header (portrait / name + difficulty /
/// requirements + location), a left column of selectable team sprites, a
/// selected-member summary card, and two info panels that swap between three
/// modes: Team (description placeholder + moves), Rewards (item + Pokemon
/// rewards), and 3-Time (the win-streak bonus table §59 split out).
///
/// Dual-boss files (Medusa &amp; Eldir, Team Rocket, the Gamers, Shary &amp;
/// Shaui) show one portrait per combatant in a strip beside the name
/// (§108, replacing §93's single cycle button): the pairing is not
/// hard-coded anywhere - it IS the data, the same named-NPC nesting
/// BossDifficultyData.GetNpcTeams already parses, so any future dual or
/// triple boss file picks the feature up automatically, one portrait per
/// entry with no cycling to reason about. Picking a portrait refreshes every
/// NPC-specific piece (name, team, selection); rewards, location and
/// requirements are shared by the whole fight in PRO, and live at the file
/// level in the JSON, so they correctly stay put - and so does the header
/// image, which is the location and nothing else now.
/// </summary>
public sealed partial class BossDetailViewModel : ViewModelBase
{
    private BossData? boss;
    private BossDifficulty difficulty;
    private List<(string? NpcName, List<BossPokemonData> Team)> npcTeams = new();
    private int activeNpcIndex;
    private Bitmap? portraitBitmap;
    private Bitmap? locationBitmap;

    [ObservableProperty] private string windowTitle = "Boss";
    [ObservableProperty] private string headerNameText = string.Empty;
    [ObservableProperty] private string headerDifficultyText = string.Empty;
    // The big header image: the LOCATION, permanently (§108). §93's swap
    // button that traded it for the boss portrait is gone - the portraits
    // have their own strip now, and a picture that moves out from under you
    // when you click something else is the thing that made the old header
    // confusing. The boss portrait is still the fallback here, for the few
    // files that carry no location picture at all, so the box is never blank.
    [ObservableProperty] private Bitmap? headerImage;
    [ObservableProperty] private bool hasHeaderImage;
    [ObservableProperty] private string locationText = string.Empty;
    [ObservableProperty] private string requirementText = string.Empty;
    [ObservableProperty] private string pokedollarsText = string.Empty;
    [ObservableProperty] private string pveCoinsText = string.Empty;
    [ObservableProperty] private string? errorMessage;
    [ObservableProperty] private bool hasError;

    // ---- §108 trainer portrait strip ----
    // One entry per combatant, built once per boss load. HasNpcPortraits
    // hides the strip entirely for a file with no NPC list at all;
    // CanSwitchNpc is what makes the strip clickable, so a single-NPC boss
    // shows its portrait as a plain picture rather than a control that
    // cannot do anything.
    public ObservableCollection<BossNpcPortraitItem> NpcPortraits { get; } = new();
    [ObservableProperty] private bool hasNpcPortraits;
    [ObservableProperty] private bool canSwitchNpc;
    [ObservableProperty] private BossNpcPortraitItem? selectedNpcPortrait;

    // ---- Display mode: Team (both false), Rewards, or 3-Time ----
    // Two ToggleButtons bind here; checking one unchecks the other, and
    // unchecking both (clicking the active one again) returns to team view.
    [ObservableProperty] private bool showRewardsMode;
    [ObservableProperty] private bool showStreakMode;
    [ObservableProperty] private bool isTeamMode = true;

    partial void OnShowRewardsModeChanged(bool value)
    {
        if (value)
            ShowStreakMode = false;

        UpdateModeFlags();
    }

    partial void OnShowStreakModeChanged(bool value)
    {
        if (value)
            ShowRewardsMode = false;

        UpdateModeFlags();
    }

    private void UpdateModeFlags() => IsTeamMode = !ShowRewardsMode && !ShowStreakMode;

    // ---- Team / selection ----
    [ObservableProperty] private BossTeamMemberItem? selectedTeamMember;
    [ObservableProperty] private bool hasSelectedTeamMember;
    [ObservableProperty] private bool hasTeamMembers;

    partial void OnSelectedTeamMemberChanged(BossTeamMemberItem? value) =>
        HasSelectedTeamMember = value is not null;

    // ---- Rewards (see §59 for the win-streak split) ----
    [ObservableProperty] private bool hasStreakBonusRewards;
    [ObservableProperty] private string streakBonusHeading = "3-Time Rewards";
    [ObservableProperty] private string streakExplanationText = string.Empty;

    public ObservableCollection<RewardImageItem> ItemRewards { get; } = new();
    public ObservableCollection<RewardImageItem> PokemonRewards { get; } = new();
    public ObservableCollection<RewardImageItem> StreakBonusRewards { get; } = new();
    public ObservableCollection<BossTeamMemberItem> TeamMembers { get; } = new();

    public void Load(string bossId, BossDifficulty difficulty)
    {
        try
        {
            this.difficulty = difficulty;
            boss = BossRepository.Load(bossId);
            string difficultyKey = difficulty.ToString().ToLowerInvariant();

            HeaderNameText = boss.Name;
            HeaderDifficultyText = $"{difficulty} Difficulty";
            WindowTitle = $"{boss.Name} ({difficulty})";
            string locationPicturePath = !string.IsNullOrWhiteSpace(boss.LocationPicture)
                ? boss.LocationPicture
                : boss.LocationImage;

            locationBitmap = LoadImageFromRelativePath(locationPicturePath);
            portraitBitmap = LoadImageFromRelativePath(boss.NpcPicture);
            ApplyHeaderImages();

            if (!boss.Difficulties.TryGetValue(difficultyKey, out BossDifficultyData? selected))
            {
                ErrorMessage = $"Difficulty '{difficulty}' was not found for {boss.Name}.";
                HasError = true;
                return;
            }

            // A stub entry (see BossDifficultyData.UnavailableMessage) - this
            // difficulty is a real, intentional key in the file, but there's
            // no actual reward/team data for it, just a note explaining why.
            if (!string.IsNullOrWhiteSpace(selected.UnavailableMessage))
            {
                ErrorMessage = selected.UnavailableMessage;
                HasError = true;
                return;
            }

            LocationText = boss.Location;
            RequirementText = !string.IsNullOrWhiteSpace(boss.Requirement) ? boss.Requirement : boss.Requirements;

            PokedollarsText = $"${selected.Rewards.Pokedollars.Minimum:N0} - ${selected.Rewards.Pokedollars.Maximum:N0}";
            PveCoinsText = $"{selected.Rewards.PveCoins} PVE Coins";

            ItemRewards.Clear();
            foreach (BossItemReward item in selected.Rewards.Items)
            {
                string itemCaption = string.IsNullOrWhiteSpace(item.Name)
                    ? item.Quantity
                    : $"{item.Name}\n{item.Quantity}";

                ItemRewards.Add(new RewardImageItem(LoadImageFromRelativePath(item.Picture), itemCaption));
            }

            PokemonRewards.Clear();
            StreakBonusRewards.Clear();
            int? bonusStreakValue = null;
            foreach (BossPokemonReward reward in selected.Rewards.Pokemon)
            {
                string spritePath = !string.IsNullOrWhiteSpace(reward.Picture)
                    ? reward.Picture
                    : reward.DexNumber > 0
                        ? Path.Combine("SharedPokemonLibrary", "Assets", "Sprites", $"{reward.DexNumber}.png")
                        : string.Empty;

                var rewardItem = new RewardImageItem(LoadImageFromRelativePath(spritePath), reward.Name);

                if (reward.WinStreakRequired > 0)
                {
                    StreakBonusRewards.Add(rewardItem);
                    bonusStreakValue ??= reward.WinStreakRequired;
                }
                else
                {
                    PokemonRewards.Add(rewardItem);
                }
            }

            HasStreakBonusRewards = StreakBonusRewards.Count > 0;
            StreakBonusHeading = bonusStreakValue is int streakValue
                ? $"{streakValue}-Time Rewards ({streakValue}+ Wins in a Row)"
                : "3-Time Rewards";
            StreakExplanationText = bonusStreakValue is int streak
                ? $"Beat this boss {streak} times in a row and these join the regular reward pool on the winning fight."
                : "This boss has no 3-win bonus rewards.";

            npcTeams = selected.GetNpcTeams();
            activeNpcIndex = 0;
            BuildNpcPortraits();

            // Selecting the first portrait is what lights it up in the strip.
            // Only meaningful when there is something to switch BETWEEN: a
            // single-NPC boss stays unselected so its one portrait renders as
            // a plain picture with no selection chrome around it.
            SelectedNpcPortrait = CanSwitchNpc ? NpcPortraits.FirstOrDefault() : null;

            // Called explicitly rather than relying on the selection above to
            // trigger it - the change handler no-ops when the index already
            // matches, and this path has to work for single-NPC bosses too.
            ApplyActiveNpc();
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.ToString();
            HasError = true;
        }
    }

    /// <summary>
    /// Builds the portrait strip once per boss load (§108). Loading each
    /// combatant's bitmap here rather than on every switch is what lets
    /// ApplyActiveNpc reuse it instead of re-reading the file, and it means
    /// the strip never changes shape while the window is open.
    /// </summary>
    private void BuildNpcPortraits()
    {
        NpcPortraits.Clear();

        for (int i = 0; i < npcTeams.Count; i++)
        {
            string? name = npcTeams[i].NpcName;

            NpcPortraits.Add(new BossNpcPortraitItem(
                i,
                name ?? boss?.Name ?? string.Empty,
                LoadImageFromRelativePath(ResolveNpcPortraitPath(name))));
        }

        HasNpcPortraits = NpcPortraits.Count > 0;
        CanSwitchNpc = NpcPortraits.Count > 1;
    }

    /// <summary>
    /// Picking a portrait switches combatant (§108, replacing §93's cycle
    /// button). Guarded against the index it already shows, so re-selecting
    /// the current trainer - or the initial selection made during Load -
    /// costs nothing and cannot rebuild the team out from under a click.
    /// </summary>
    partial void OnSelectedNpcPortraitChanged(BossNpcPortraitItem? value)
    {
        if (value is null || value.Index == activeNpcIndex)
            return;

        activeNpcIndex = value.Index;
        ApplyActiveNpc();
    }

    /// <summary>
    /// Applies the active combatant's name, portrait and team. Everything
    /// here re-derives from the data on every switch, so
    /// nothing from the previously shown combatant can survive - the team
    /// list is rebuilt and the selection reset to the new team's first
    /// member (which also refreshes the summary card and both info panels).
    /// </summary>
    private void ApplyActiveNpc()
    {
        if (boss is null)
            return;

        (string? npcName, List<BossPokemonData> team) = npcTeams.Count > 0
            ? npcTeams[activeNpcIndex]
            : (null, new List<BossPokemonData>());

        HeaderNameText = npcName ?? boss.Name;
        WindowTitle = $"{HeaderNameText} ({difficulty})";
        // §108: the strip already holds every combatant's bitmap, so a switch
        // reuses it rather than re-reading the file. The direct load stays as
        // the fallback for the paths that reach here before the strip exists.
        portraitBitmap = activeNpcIndex >= 0 && activeNpcIndex < NpcPortraits.Count
            ? NpcPortraits[activeNpcIndex].Portrait
            : LoadImageFromRelativePath(ResolveNpcPortraitPath(npcName));

        ApplyHeaderImages();

        TeamMembers.Clear();
        foreach (BossPokemonData pokemon in team)
            TeamMembers.Add(new BossTeamMemberItem(LoadSpriteByDexNumber(pokemon.DexNumber), pokemon));

        HasTeamMembers = TeamMembers.Count > 0;
        SelectedTeamMember = TeamMembers.FirstOrDefault();
    }

    /// <summary>
    /// The big header image: the location picture, and only that (§108).
    /// "the big boss in the top left corner should be the location images"
    /// was §93's rule and it stays the rule - what §108 removed is the swap
    /// button that let the picture be traded for the boss portrait, because
    /// a header that changes when you click a trainer is exactly what made
    /// the two-trainer bosses read wrong. The portrait remains the fallback
    /// for a file with no location picture, so the box is never blank; it is
    /// re-derived on every NPC switch for that reason alone.
    /// </summary>
    private void ApplyHeaderImages()
    {
        HeaderImage = locationBitmap ?? portraitBitmap;
        HasHeaderImage = HeaderImage is not null;
    }

    /// <summary>
    /// Portrait path for one combatant: the file's npcPictures entry when one
    /// exists, else NPCPicture for single-NPC bosses and for the FIRST
    /// combatant of a dual file that predates npcPictures (whose NPCPicture
    /// is that first combatant's portrait by authoring convention). Anything
    /// else resolves to no image, and the switch button shows the name as
    /// text instead - a missing or not-yet-drawn portrait can't break the page.
    /// </summary>
    private string ResolveNpcPortraitPath(string? npcName)
    {
        if (boss is null)
            return string.Empty;

        if (!string.IsNullOrWhiteSpace(npcName) &&
            boss.NpcPictures.TryGetValue(npcName, out string? mapped) &&
            !string.IsNullOrWhiteSpace(mapped))
        {
            return mapped;
        }

        if (npcName is null || (npcTeams.Count > 0 && npcTeams[0].NpcName == npcName))
            return boss.NpcPicture;

        return string.Empty;
    }

    private static Bitmap? LoadSpriteByDexNumber(int dexNumber)
    {
        if (dexNumber <= 0)
            return null;

        return LoadImageFromRelativePath(Path.Combine("SharedPokemonLibrary", "Assets", "Sprites", $"{dexNumber}.png"));
    }

    private static Bitmap? LoadImageFromRelativePath(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string fullPath = Path.IsPathRooted(normalized) ? normalized : Path.Combine(AppContext.BaseDirectory, normalized);

        return File.Exists(fullPath) ? new Bitmap(fullPath) : null;
    }
}
