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

public sealed record BossMoveDisplayItem(string Name, string Detail);

/// <summary>
/// See MIGRATION_GUIDE.md §43. MoveDisplayItems resolves Pokemon.Moves (plain
/// names, e.g. "Thunderbolt") against MoveLookupService into a bold Name plus
/// a " - Type - Power - Effect" Detail suffix for BossDetailWindow.axaml's
/// inline detail panel. A move MoveLookupService can't find (a typo in the
/// boss file, or a move added to the game after moves.json was built) falls
/// back to just the plain name with an empty Detail, matching Find's own
/// "degrade gracefully rather than throw" behavior.
/// </summary>
public sealed record BossTeamMemberItem(Bitmap? Sprite, BossPokemonData Pokemon)
{
    /// <summary>
    /// See MIGRATION_GUIDE.md §45. Pokemon.Ability plus " - " and its
    /// description from AbilityLookupService, e.g. "Pure Power - Raises the
    /// Pokemon's Attack stat." Falls back to just the plain ability name (same
    /// "degrade gracefully" behavior as MoveDisplayItems above) when the
    /// ability isn't found or has no description on file.
    /// </summary>
    public string AbilityDisplayText
    {
        get
        {
            AbilityData? ability = AbilityLookupService.Find(Pokemon.Ability);

            if (ability is null || string.IsNullOrWhiteSpace(ability.Description))
                return Pokemon.Ability;

            return $"{Pokemon.Ability} - {ability.Description}";
        }
    }

    public IReadOnlyList<BossMoveDisplayItem> MoveDisplayItems =>
        Pokemon.Moves.Select(BuildMoveDisplayItem).ToList();

    /// <summary>Backs a small type-icon row next to this team member's name.</summary>
    public IReadOnlyList<string> Types => PokemonSpriteService.GetTypes(Pokemon.Name);

    private static BossMoveDisplayItem BuildMoveDisplayItem(string moveName)
    {
        MoveData? move = MoveLookupService.Find(moveName);

        if (move is null)
            return new BossMoveDisplayItem(moveName, string.Empty);

        string powerText = move.Power is int power ? power.ToString() : "—";
        var segments = new List<string> { move.Type, powerText };

        if (!string.IsNullOrWhiteSpace(move.Effect))
            segments.Add(move.Effect);

        return new BossMoveDisplayItem(moveName, " - " + string.Join(" - ", segments));
    }
}

/// <summary>
/// One combatant's team section within the "Boss Team" display. NpcName is
/// null for the ordinary case (a single flat team, the vast majority of boss
/// files) - HasNpcName drives the XAML so no heading renders for those, and
/// the layout looks exactly as it did before dual-boss files existed. See
/// BossDifficultyData.GetNpcTeams for where these groups come from.
/// </summary>
public sealed record BossTeamGroupItem(string? NpcName, ObservableCollection<BossTeamMemberItem> Members)
{
    public bool HasNpcName => !string.IsNullOrWhiteSpace(NpcName);
}

/// <summary>
/// Ported from Forms/BossTemplate/BossTemplate.cs. The original located every
/// control by string name via Controls.Find(...) and set .Text/.Image directly;
/// here the same data just becomes bound properties/collections. BossPokemonCard.cs
/// (a floating "team member details" popup) is folded into SelectedTeamMember,
/// shown as an inline panel instead of a second window.
/// </summary>
public sealed partial class BossDetailViewModel : ViewModelBase
{
    [ObservableProperty] private string bossNameText = string.Empty;
    [ObservableProperty] private string locationText = string.Empty;
    [ObservableProperty] private Bitmap? locationImage;
    [ObservableProperty] private string requirementText = string.Empty;
    [ObservableProperty] private string pokedollarsText = string.Empty;
    [ObservableProperty] private string pveCoinsText = string.Empty;
    [ObservableProperty] private string? errorMessage;

    [ObservableProperty] private BossTeamMemberItem? selectedTeamMember;
    [ObservableProperty] private bool hasSelectedTeamMember;

    // Pokemon rewards split by BossPokemonReward.WinStreakRequired: bosses scraped
    // from a wiki page with a separate "beat 3 times in a row" bonus table have that
    // table's picks come through here with WinStreakRequired=3 (see
    // BossWikiScraperService.cs), rather than in the flat PokemonRewards list. Some
    // bosses' bonus table lists the exact same Pokemon as their base table (that's
    // real scraped data, not a bug) - HasStreakBonusRewards/StreakBonusHeading exist
    // so that still reads as "these are also available at streak N" instead of
    // looking like an accidental duplicate. Older boss files with no
    // winStreakRequired field at all just default to 0, so this section stays
    // hidden for them exactly as before this was added.
    [ObservableProperty] private bool hasStreakBonusRewards;
    [ObservableProperty] private string streakBonusHeading = "Win Streak Bonus";

    partial void OnSelectedTeamMemberChanged(BossTeamMemberItem? value) => HasSelectedTeamMember = value is not null;

    public ObservableCollection<RewardImageItem> ItemRewards { get; } = new();
    public ObservableCollection<RewardImageItem> PokemonRewards { get; } = new();
    public ObservableCollection<RewardImageItem> StreakBonusRewards { get; } = new();
    public ObservableCollection<BossTeamGroupItem> TeamGroups { get; } = new();

    public void Load(string bossId, BossDifficulty difficulty)
    {
        try
        {
            BossData boss = BossRepository.Load(bossId);
            string difficultyKey = difficulty.ToString().ToLowerInvariant();

            if (!boss.Difficulties.TryGetValue(difficultyKey, out BossDifficultyData? selected))
            {
                ErrorMessage = $"Difficulty '{difficulty}' was not found for {boss.Name}.";
                return;
            }

            BossNameText = $"{boss.Name} ({difficulty})";
            LocationText = boss.Location;

            string locationPicturePath = !string.IsNullOrWhiteSpace(boss.LocationPicture)
                ? boss.LocationPicture
                : boss.LocationImage;

            LocationImage = LoadImageFromRelativePath(locationPicturePath);

            RequirementText = !string.IsNullOrWhiteSpace(boss.Requirement) ? boss.Requirement : boss.Requirements;

            PokedollarsText = $"${selected.Rewards.Pokedollars.Minimum:N0} - ${selected.Rewards.Pokedollars.Maximum:N0}";
            PveCoinsText = $"{selected.Rewards.PveCoins} PVE Coins";

            ItemRewards.Clear();
            foreach (var item in selected.Rewards.Items)
            {
                string itemCaption = string.IsNullOrWhiteSpace(item.Name)
                    ? item.Quantity
                    : $"{item.Name}\n{item.Quantity}";

                ItemRewards.Add(new RewardImageItem(LoadImageFromRelativePath(item.Picture), itemCaption));
            }

            PokemonRewards.Clear();
            StreakBonusRewards.Clear();
            int? bonusStreakValue = null;
            foreach (var reward in selected.Rewards.Pokemon)
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
                ? $"Win Streak Bonus ({streakValue}+ Wins in a Row)"
                : "Win Streak Bonus";

            TeamGroups.Clear();
            foreach ((string? npcName, List<BossPokemonData> team) in selected.GetNpcTeams())
            {
                var members = new ObservableCollection<BossTeamMemberItem>(
                    team.Select(pokemon => new BossTeamMemberItem(LoadSpriteByDexNumber(pokemon.DexNumber), pokemon)));

                TeamGroups.Add(new BossTeamGroupItem(npcName, members));
            }
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.ToString();
        }
    }

    [RelayCommand]
    private void SelectTeamMember(BossTeamMemberItem item)
    {
        // Clicking the already-open member again closes the detail panel,
        // same as the original's "click same Pokémon -> close card" behavior.
        SelectedTeamMember = SelectedTeamMember == item ? null : item;
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
