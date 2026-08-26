using CommunityToolkit.Mvvm.ComponentModel;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

public sealed partial class HuntingStatsViewModel : ViewModelBase
{
    [ObservableProperty] private string totalTimeHunting = "00:00:00";
    [ObservableProperty] private string totalPokemon = "0";
    [ObservableProperty] private string shinyPokemon = "0";
    [ObservableProperty] private string eventForms = "0";
    [ObservableProperty] private string successfulCatches = "0";
    [ObservableProperty] private string failedCatches = "0";
    [ObservableProperty] private string catchRate = "0.00%";
    [ObservableProperty] private string shinyFormRate = "N/A";

    public HuntingStatsViewModel()
    {
        var stats = LifetimeStatsService.Load();

        TotalTimeHunting = TimeFormatHelper.FormatElapsed(stats.TotalHuntingTime);
        TotalPokemon = stats.TotalEncounters.ToString();
        ShinyPokemon = stats.ShinyEncounters.ToString();
        EventForms = stats.FormEncounters.ToString();
        SuccessfulCatches = stats.SuccessfulCatches.ToString();
        FailedCatches = stats.FailedCatches.ToString();

        int totalCatchAttempts = (int)(stats.SuccessfulCatches + stats.FailedCatches);
        double rate = totalCatchAttempts > 0 ? stats.SuccessfulCatches / (double)totalCatchAttempts * 100.0 : 0.0;
        CatchRate = $"{rate:F2}%";

        double totalRare = stats.ShinyEncounters + stats.FormEncounters;
        ShinyFormRate = totalRare > 0 ? $"1 in {stats.TotalEncounters / totalRare:F0}" : "N/A";
    }
}
