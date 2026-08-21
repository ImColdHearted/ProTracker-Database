using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed partial class BossCooldownCardItem : ViewModelBase
{
    public required string BossId { get; init; }
    public required string BossName { get; init; }
    public Bitmap? Sprite { get; init; }

    [ObservableProperty] private string cooldownText = "READY";

    public void RefreshCooldown()
    {
        var cooldown = BossCooldownService.GetCooldown(BossName);

        CooldownText = cooldown is null || cooldown.TimeRemaining <= TimeSpan.Zero
            ? "READY"
            : $"{cooldown.TimeRemaining.Days:00}:{cooldown.TimeRemaining.Hours:00}:{cooldown.TimeRemaining.Minutes:00}";
    }
}

/// <summary>Ported from Forms/Cooldowns/Bosses/BossCooldownForm.cs.</summary>
public sealed partial class BossCooldownViewModel : ViewModelBase
{
    public ObservableCollection<BossCooldownCardItem> Bosses { get; } = new();

    [ObservableProperty] private BossCooldownCardItem? pendingConfirmation;

    /// <summary>Set by the View to show a Yes/No confirmation (replaces MessageBox.Show).</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public BossCooldownViewModel()
    {
        LoadBossesIntoGrid();
    }

    private void LoadBossesIntoGrid()
    {
        Bosses.Clear();

        string bossesFolder = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");
        if (!Directory.Exists(bossesFolder))
            return;

        foreach (string file in Directory.GetFiles(bossesFolder, "*.json").OrderBy(x => x))
        {
            try
            {
                string json = File.ReadAllText(file);
                var boss = JsonSerializer.Deserialize<BossCooldownDefinition>(
                    json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });

                if (boss is null || string.IsNullOrWhiteSpace(boss.Name))
                    continue;

                var card = new BossCooldownCardItem
                {
                    BossId = boss.BossId,
                    BossName = boss.Name,
                    Sprite = LoadBossImage(boss.NPCPicture)
                };

                card.RefreshCooldown();
                Bosses.Add(card);
            }
            catch
            {
                // Skip malformed boss files, same as the original's try/catch-per-file.
            }
        }
    }

    [RelayCommand]
    private async Task CardClicked(BossCooldownCardItem card)
    {
        bool confirmed = ConfirmAsync is not null
            ? await ConfirmAsync($"Start the cooldown for {card.BossName}?")
            : true;

        if (!confirmed)
            return;

        BossCooldownService.RegisterBossDefeat(card.BossId);
        card.RefreshCooldown();
    }

    private static Bitmap? LoadBossImage(string? relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath))
            return null;

        string cleanPath = relativePath.Replace('/', Path.DirectorySeparatorChar).Replace('\\', Path.DirectorySeparatorChar);
        string fullPath = Path.Combine(AppContext.BaseDirectory, cleanPath);

        return File.Exists(fullPath) ? new Bitmap(fullPath) : null;
    }
}
