using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs Views/HuntingStatsWindow.axaml - retitled "Lifetime Stats" by the
/// user, with boss and PVP rows added alongside the original hunting numbers
/// (see MIGRATION_GUIDE.md §91). Everything loads once from
/// LifetimeStatsService when the window opens. The boss/PVP counters begin at
/// zero as of the build that introduced them - fights from before then were
/// never recorded anywhere, so there is nothing to backfill from.
/// </summary>
public sealed partial class HuntingStatsViewModel : ViewModelBase
{
    [ObservableProperty] private string totalTimeHunting = "00:00:00";
    [ObservableProperty] private string totalPokemon = "0";
    [ObservableProperty] private string shinyPokemon = "0";
    [ObservableProperty] private string eventForms = "0";
    [ObservableProperty] private string successfulCatches = "0";
    [ObservableProperty] private string failedCatches = "0";
    [ObservableProperty] private string formFormRate = "N/A";
    [ObservableProperty] private string shinyFormRate = "N/A";
    [ObservableProperty] private string bossesFought = "0";
    [ObservableProperty] private string lossesToBosses = "0";
    [ObservableProperty] private string bossVictories = "0";

    // The odd casing is deliberate: CommunityToolkit's generator upper-cases
    // only the first character of the field name, and the window's XAML binds
    // PVPMatches/PVPLosses/PVPWins.
    [ObservableProperty] private string pVPMatches = "0";
    [ObservableProperty] private string pVPLosses = "0";
    [ObservableProperty] private string pVPWins = "0";

    public HuntingStatsViewModel()
    {
        var stats = LifetimeStatsService.Load();

        TotalTimeHunting = TimeFormatHelper.FormatElapsed(stats.TotalHuntingTime);
        TotalPokemon = DisplayNumber.Count(stats.TotalEncounters);
        ShinyPokemon = DisplayNumber.Count(stats.ShinyEncounters);
        EventForms = DisplayNumber.Count(stats.FormEncounters);
        SuccessfulCatches = DisplayNumber.Count(stats.SuccessfulCatches);
        FailedCatches = DisplayNumber.Count(stats.FailedCatches);

        // "1 in N" per category - the old combined shiny+form rate split into
        // two when the window gained a separate row for each.
        FormFormRate = stats.FormEncounters > 0
            ? $"1 in {stats.TotalEncounters / (double)stats.FormEncounters:F0}"
            : "N/A";
        ShinyFormRate = stats.ShinyEncounters > 0
            ? $"1 in {stats.TotalEncounters / (double)stats.ShinyEncounters:F0}"
            : "N/A";

        BossesFought = DisplayNumber.Count(stats.BossBattles);
        BossVictories = DisplayNumber.Count(stats.BossVictories);
        LossesToBosses = DisplayNumber.Count(stats.BossLosses);

        // Matches = every battle the tracker identified an opponent for (the
        // same per-opponent counts behind Previously Battled Users). Wins +
        // losses can lag behind it: a battle whose window disappeared before
        // the result text was read ends with no recorded outcome.
        PVPMatches = DisplayNumber.Count(stats.PvpOpponentBattleCounts.Values.Sum());
        PVPWins = DisplayNumber.Count(stats.PvpWins);
        PVPLosses = DisplayNumber.Count(stats.PvpLosses);
    }
}
