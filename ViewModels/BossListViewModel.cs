using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace Foot_Tracker.ViewModels;

public sealed record BossListItem(string BossId, string DisplayName);

/// <summary>
/// Ported from ProTrackerandDatabase.cs's boss menu (OpenBoss/BossDifficultyMenuItem_Click),
/// which had a hand-built ToolStripMenuItem tree of ~48 bosses x 3 difficulties. That's
/// impractical to hand-author in XAML, so this is a single browsable list + difficulty
/// picker instead - same underlying action (new BossTemplate(bossId, difficulty)).
/// </summary>
public sealed partial class BossListViewModel : ViewModelBase
{
    public ObservableCollection<BossListItem> Bosses { get; } = new();
    public BossDifficulty[] DifficultyOptions { get; } = Enum.GetValues<BossDifficulty>();

    [ObservableProperty] private BossListItem? selectedBoss;
    [ObservableProperty] private BossDifficulty selectedDifficulty = BossDifficulty.Easy;

    /// <summary>Raised with (bossId, difficulty) when "Open" is clicked - the View shows BossDetailWindow.</summary>
    public event Action<string, BossDifficulty>? OpenRequested;

    public BossListViewModel()
    {
        string bossesFolder = Path.Combine(AppContext.BaseDirectory, "DataFiles", "Bosses");

        if (!Directory.Exists(bossesFolder))
            return;

        foreach (string file in Directory.GetFiles(bossesFolder, "*.json").OrderBy(x => x))
        {
            string bossID = Path.GetFileNameWithoutExtension(file);
            Bosses.Add(new BossListItem(bossID, bossID));
        }
    }

    [RelayCommand]
    private void Open()
    {
        if (SelectedBoss is not null)
        {
            OpenRequested?.Invoke(SelectedBoss.BossId, SelectedDifficulty);
        }
    }
}
