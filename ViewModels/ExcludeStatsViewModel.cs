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
/// load-a-working-copy/Save pattern as AppearanceViewModel. HideEntireStatsPanel
/// below is a separate, coarser toggle for hiding the whole panel as one unit
/// rather than picking individual stats - see UiPreferences.StatsPanelHidden.
/// </summary>
public sealed partial class ExcludeStatsViewModel : ViewModelBase
{
    private readonly UiPreferences _workingPreferences = UiPreferencesService.Load();

    public ObservableCollection<StatToggleOption> Stats { get; }

    /// <summary>§126. The encounter table's columns, reusing
    /// StatToggleOption unchanged - the type is a key, a label and a
    /// checkbox state, which is exactly what a column toggle is too.</summary>
    public ObservableCollection<StatToggleOption> TableColumns { get; }

    [ObservableProperty] private bool hideEntireStatsPanel;
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

        TableColumns = new ObservableCollection<StatToggleOption>(
            UiPreferencesService.ExcludableTableColumns.Select(column =>
                new StatToggleOption(
                    column.Key,
                    column.DisplayName,
                    _workingPreferences.ExcludedTableColumns.Contains(column.Key))));

        HideEntireStatsPanel = _workingPreferences.StatsPanelHidden;
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

            _workingPreferences.ExcludedTableColumns = TableColumns
                .Where(c => c.IsExcluded)
                .Select(c => c.Key)
                .ToList();

            _workingPreferences.StatsPanelHidden = HideEntireStatsPanel;

            UiPreferencesService.Save(_workingPreferences);

            SavedSuccessfully?.Invoke();
        }
        catch (Exception ex)
        {
            SaveError = $"The stat display preferences could not be saved.\n\n{ex.Message}";
        }
    }
}
