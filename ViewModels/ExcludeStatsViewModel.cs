using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs the "Exclude Stats" window (Stats menu, new - not from the original
/// WinForms app). Lets the user hide individual stat blocks from
/// MainWindow's stats panel without stopping them from being tracked -
/// hunting keeps counting normally in the background either way (see
/// HuntSession/MainWindowViewModel.ApplyExcludedStats). Follows the same
/// load-a-working-copy/Save pattern as AppearanceViewModel.
/// </summary>
public sealed partial class ExcludeStatsViewModel : ViewModelBase
{
    private readonly UiPreferences _workingPreferences = UiPreferencesService.Load();

    public ObservableCollection<StatToggleOption> Stats { get; }

    [ObservableProperty] private string? saveError;
    [ObservableProperty] private bool hasSaveError;

    partial void OnSaveErrorChanged(string? value) => HasSaveError = !string.IsNullOrEmpty(value);

    /// <summary>Raised when Save completes successfully - the View closes itself.</summary>
    public event Action? SavedSuccessfully;

    public ExcludeStatsViewModel()
    {
        Stats = new ObservableCollection<StatToggleOption>(
            UiPreferencesService.ExcludableStats.Select(stat =>
                new StatToggleOption(
                    stat.Key,
                    stat.DisplayName,
                    _workingPreferences.ExcludedStats.Contains(stat.Key))));
    }

    [RelayCommand]
    private void Save()
    {
        try
        {
            _workingPreferences.ExcludedStats = Stats
                .Where(s => s.IsExcluded)
                .Select(s => s.Key)
                .ToList();

            UiPreferencesService.Save(_workingPreferences);

            SavedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = $"The stat display preferences could not be saved.\n\n{ex.Message}";
        }
    }
}
