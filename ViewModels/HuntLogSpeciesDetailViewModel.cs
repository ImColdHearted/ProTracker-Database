using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs HuntLogSpeciesDetailWindow - the same Catch Logs data as
/// HuntLogViewModel, filtered down to a single species. Opened by clicking a
/// Pokemon's row inside the Catch Logs window (HuntLogWindow.axaml.cs's
/// row-click handler) - "similar to previously battled opponents for PVP,"
/// just scoped per-species instead of showing every species combined in one
/// table. Titled "Catch History" (§99) because this shows SUCCESSFUL CATCHES
/// ONLY, from the persistent per-client catch log that survives a hunt
/// reset - deliberately distinct from SessionEncounterHistoryViewModel, the
/// per-Pokemon list of EVERY encounter of the current hunt that the main
/// window's Session Encounters rows open and Reset clears. The main-window
/// rows used to open THIS window instead, which read as the app calling a
/// catch list an encounter history - see MIGRATION_GUIDE.md §99. Remove
/// Previous/Clear All/Export here only ever touch this one species' catch
/// entries - see HuntLogService's *ForSpecies methods.
/// </summary>
public sealed partial class HuntLogSpeciesDetailViewModel : ViewModelBase, IDisposable
{
    public string PokemonName { get; }

    public string Title => $"{PokemonName} - Catch History";

    public ObservableCollection<HuntLogDisplayItem> Entries { get; } = new();

    [ObservableProperty] private bool hasNoEntries;

    [ObservableProperty] private bool hasEntries;

    [ObservableProperty] private string exportStatusMessage = string.Empty;

    /// <summary>Set by HuntLogSpeciesDetailWindow.axaml.cs - same
    /// ExportFormatDialogWindow picker every other export flow in this app
    /// uses.</summary>
    public Func<Task<string?>>? RequestExportFormat { get; set; }

    public Func<string, string, Task<string?>>? RequestExportFilePath { get; set; }

    /// <summary>Required, not just preferred - RemovePrevious/ClearAll refuse
    /// to remove anything if this hook isn't wired, same as
    /// HuntLogViewModel/PreviouslyBattledUsersViewModel.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public HuntLogSpeciesDetailViewModel(string pokemonName)
    {
        PokemonName = pokemonName;

        LoadEntries();

        HuntLogService.LogChanged += OnLogChanged;
    }

    private void OnLogChanged()
    {
        Dispatcher.UIThread.Post(LoadEntries);
    }

    private void LoadEntries()
    {
        Entries.Clear();

        foreach (var entry in HuntLogService.GetEntriesForSpecies(PokemonName))
        {
            Entries.Add(HuntLogViewModel.BuildDisplayItem(entry));
        }

        HasNoEntries = Entries.Count == 0;
        HasEntries = Entries.Count > 0;
    }

    /// <summary>Removes the single most recent encounter OF THIS SPECIES ONLY -
    /// every other species' entries (and this window shows only one species
    /// anyway) are unaffected. See HuntLogService.RemoveMostRecentForSpecies.</summary>
    [RelayCommand]
    private async Task RemovePrevious()
    {
        if (Entries.Count == 0)
            return;

        string confirmMessage =
            $"Remove the most recent {PokemonName} encounter? This can't be undone.";

        bool confirmed = ConfirmAsync is not null && await ConfirmAsync(confirmMessage);

        if (!confirmed)
            return;

        HuntLogService.RemoveMostRecentForSpecies(PokemonName);

        ExportStatusMessage = "Removed the most recent encounter.";
    }

    /// <summary>Wipes every logged encounter OF THIS SPECIES ONLY - the rest of
    /// the hunting log (every other species) is untouched. Always confirmed
    /// first, same as RemovePrevious, since neither can be undone.</summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        if (Entries.Count == 0)
            return;

        bool confirmed = ConfirmAsync is not null
            && await ConfirmAsync($"Clear every logged {PokemonName} encounter? This can't be undone.");

        if (!confirmed)
            return;

        HuntLogService.ClearAllForSpecies(PokemonName);

        ExportStatusMessage = $"Cleared all {PokemonName} encounters.";
    }

    /// <summary>Exports only this species' entries as either CSV or JSON, based
    /// on the format chosen in the RequestExportFormat popup.</summary>
    [RelayCommand]
    private async Task Export()
    {
        if (RequestExportFormat is null || RequestExportFilePath is null)
            return;

        string? format = await RequestExportFormat();

        if (format is null)
            return;

        string suggestedName = $"ProTracker-HuntingLog-{PokemonName}-{DateTime.Now:yyyy-MM-dd-HHmmss}";

        string? path = await RequestExportFilePath(suggestedName, format);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            var speciesEntries = HuntLogService.GetEntriesForSpecies(PokemonName);

            if (extension == "json")
                HuntLogExportService.ExportJson(speciesEntries, path);
            else
                HuntLogExportService.ExportCsv(speciesEntries, path);

            ExportStatusMessage = $"Exported to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"Export failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        HuntLogService.LogChanged -= OnLogChanged;
    }
}
