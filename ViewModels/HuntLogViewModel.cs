using System;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Foot_Tracker.Models;
using Foot_Tracker.Services;

namespace Foot_Tracker.ViewModels;

/// <summary>
/// Backs the "Hunting Log" window (Hunting Logs menu) - one row per individual
/// wild encounter detected this or any previous session for the active PRO
/// client, most recent first, with species, level, map, and when it happened.
/// Distinct from the existing Session Encounters table on the main window
/// (HuntSession.EncounterCounts, a running per-species tally with no per-event
/// detail) - this is the full log behind it. Clicking a row opens
/// HuntLogSpeciesDetailWindow, scoped to just that one species - see
/// HuntLogWindow.axaml.cs's row-click handler (and MainWindow.axaml.cs's own,
/// wired the same way from the existing Session Encounters table).
///
/// The list stays live while this window is open - HuntLogService raises
/// LogChanged whenever an encounter is registered (or the saved-list cap trims
/// an old entry), so there's no manual refresh button here, same as
/// PreviouslyBattledUsersViewModel/PvpOpponentService.
/// </summary>
public sealed partial class HuntLogViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<HuntLogDisplayItem> Entries { get; } = new();

    [ObservableProperty] private bool hasNoEntries;

    // Mirror of HasNoEntries (rather than an XAML boolean-negation binding - no
    // such converter exists in this codebase) - drives IsEnabled on the Remove
    // Previous/Clear All buttons, same as
    // PreviouslyBattledUsersViewModel.HasOpponents.
    [ObservableProperty] private bool hasEntries;

    [ObservableProperty] private string exportStatusMessage = string.Empty;

    /// <summary>Set by HuntLogWindow.axaml.cs to show ExportFormatDialogWindow -
    /// same picker PreviouslyBattledUsersWindow uses. Returns "csv" or "json",
    /// or null if the dialog was cancelled.</summary>
    public Func<Task<string?>>? RequestExportFormat { get; set; }

    /// <summary>Set by HuntLogWindow.axaml.cs to show a save-file dialog for the
    /// format RequestExportFormat already returned - same
    /// delegate-set-by-the-View pattern PreviouslyBattledUsersViewModel.
    /// RequestExportFilePath uses.</summary>
    public Func<string, string, Task<string?>>? RequestExportFilePath { get; set; }

    /// <summary>Set by HuntLogWindow.axaml.cs to show a Yes/No confirm before
    /// actually removing anything - same ConfirmDialogWindow.ShowAsync pattern
    /// PreviouslyBattledUsersViewModel.ConfirmAsync uses. Required, not just
    /// preferred: RemovePrevious/ClearAll refuse to remove anything if this
    /// hook isn't wired.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public HuntLogViewModel()
    {
        LoadEntries();

        HuntLogService.LogChanged += OnLogChanged;
    }

    private void OnLogChanged()
    {
        // HuntLogService.RegisterEncounter is reached via MainWindowViewModel's
        // own UI-thread-marshaled handler, but this subscriber marshals again
        // defensively - same belt-and-suspenders reasoning
        // PreviouslyBattledUsersViewModel.OnOpponentsChanged uses for
        // PvpOpponentService's identically-shaped event, which genuinely does
        // fire from a background thread.
        Dispatcher.UIThread.Post(LoadEntries);
    }

    private void LoadEntries()
    {
        Entries.Clear();

        foreach (var entry in HuntLogService.Entries.OrderByDescending(e => e.EncounteredAtUtc))
        {
            Entries.Add(BuildDisplayItem(entry));
        }

        HasNoEntries = Entries.Count == 0;
        HasEntries = Entries.Count > 0;
    }

    /// <summary>Shared with HuntLogSpeciesDetailViewModel so both windows format
    /// a HuntLogEntry into a display row identically.</summary>
    internal static HuntLogDisplayItem BuildDisplayItem(HuntLogEntry entry) => new()
    {
        PokemonName = entry.PokemonName,
        LevelText = entry.Level is int level ? $"Lv. {level}" : "Lv. ?",
        GenderSymbol = entry.Gender switch
        {
            "Male" => "♂",
            "Female" => "♀",
            _ => string.Empty
        },
        RareTypeText = entry.RareType ?? string.Empty,
        Map = entry.Map,
        EncounteredAt = entry.EncounteredAtUtc.ToLocalTime().ToString("g"),
        Sprite = PokemonSpriteService.GetSprite(entry.PokemonName)
    };

    /// <summary>Removes the single most recent encounter, any species - backs
    /// the "Remove Previous" button. See HuntLogService.RemoveMostRecent's
    /// remarks for why "most recent" needs no explicit row selection.</summary>
    [RelayCommand]
    private async Task RemovePrevious()
    {
        if (Entries.Count == 0)
            return;

        string confirmMessage =
            $"Remove the most recent encounter ({Entries[0].PokemonName})? This can't be undone.";

        bool confirmed = ConfirmAsync is not null && await ConfirmAsync(confirmMessage);

        if (!confirmed)
            return;

        HuntLogService.RemoveMostRecent();

        ExportStatusMessage = "Removed the most recent encounter.";
    }

    /// <summary>Wipes the entire saved log, every species - backs the "Clear
    /// All" button. Always confirmed first, same as RemovePrevious, since
    /// neither can be undone.</summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        if (Entries.Count == 0)
            return;

        bool confirmed = ConfirmAsync is not null
            && await ConfirmAsync("Clear the entire hunting log? This can't be undone.");

        if (!confirmed)
            return;

        HuntLogService.ClearAll();

        ExportStatusMessage = "Cleared the entire hunting log.";
    }

    /// <summary>
    /// Exports the full saved log (not just what's currently rendered) as
    /// either CSV or JSON, based on the format chosen in the
    /// RequestExportFormat popup - see HuntLogExportService.
    /// </summary>
    [RelayCommand]
    private async Task Export()
    {
        if (RequestExportFormat is null || RequestExportFilePath is null)
            return;

        string? format = await RequestExportFormat();

        if (format is null)
            return;

        string suggestedName = $"ProTracker-HuntingLog-{DateTime.Now:yyyy-MM-dd-HHmmss}";

        string? path = await RequestExportFilePath(suggestedName, format);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            if (extension == "json")
                HuntLogExportService.ExportJson(HuntLogService.Entries, path);
            else
                HuntLogExportService.ExportCsv(HuntLogService.Entries, path);

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
