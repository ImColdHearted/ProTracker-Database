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

public sealed record RewardImageItem(Bitmap? Image, string? Caption = null);

public sealed record BossTeamMemberItem(Bitmap? Sprite, BossPokemonData Pokemon);

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

    partial void OnSelectedTeamMemberChanged(BossTeamMemberItem? value) => HasSelectedTeamMember = value is not null;

    public ObservableCollection<RewardImageItem> ItemRewards { get; } = new();
    public ObservableCollection<RewardImageItem> PokemonRewards { get; } = new();
    public ObservableCollection<BossTeamMemberItem> Team { get; } = new();

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
                ItemRewards.Add(new RewardImageItem(LoadImageFromRelativePath(item.Picture), item.Quantity));
            }

            PokemonRewards.Clear();
            foreach (var reward in selected.Rewards.Pokemon)
            {
                string spritePath = !string.IsNullOrWhiteSpace(reward.Picture)
                    ? reward.Picture
                    : reward.DexNumber > 0
                        ? Path.Combine("SharedPokemonLibrary", "Assets", "Sprites", $"{reward.DexNumber}.png")
                        : string.Empty;

                PokemonRewards.Add(new RewardImageItem(LoadImageFromRelativePath(spritePath), reward.Name));
            }

            Team.Clear();
            foreach (var pokemon in selected.Team)
            {
                Team.Add(new BossTeamMemberItem(LoadSpriteByDexNumber(pokemon.DexNumber), pokemon));
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
