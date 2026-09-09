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
/// Backs the "Previously Battled Users" window (PVP menu, between Guides and
/// Stats) - lists every individual PVP battle PvpTracker has automatically
/// detected, most recent first. Rebattling the same person adds a new row
/// rather than updating an existing one (PvpOpponentService is a battle log,
/// capped at its MaxSavedBattles most recent entries - the lifetime count per
/// opponent, shown as each row's TimesBattled, is tracked separately and never
/// trimmed - see LifetimeStats.PvpOpponentBattleCounts, which will also power
/// a future "who have you battled most" PVP stats view). Phase 1 of the
/// planned PVP tracking feature: names only, no team detection yet - see
/// PvpOpponentService.RegisterBattle's remarks for that planned follow-up.
///
/// The list stays live while this window is open - PvpOpponentService raises
/// OpponentsChanged whenever a battle is registered (or the saved-list cap
/// trims an old entry), so there's no manual refresh button here anymore.
/// Dispose() must be called when the window closes (see
/// PreviouslyBattledUsersWindow.axaml.cs's Closed handler) so this instance
/// unsubscribes from that static event instead of being kept alive forever.
/// </summary>
public sealed partial class PreviouslyBattledUsersViewModel : ViewModelBase, IDisposable
{
    public ObservableCollection<PvpOpponentDisplayItem> Opponents { get; } = new();

    [ObservableProperty] private bool hasNoOpponents;

    // Mirror of HasNoOpponents (rather than an XAML boolean-negation binding -
    // no such converter exists in this codebase, see Converters/) - drives
    // IsEnabled on the Remove Previous/Clear All buttons so neither can be
    // clicked (and pointlessly prompt a confirmation) when there's nothing to
    // remove. Set alongside HasNoOpponents in LoadOpponents, same as
    // BossDetailViewModel sets HasStreakBonusRewards next to the collection
    // it describes.
    [ObservableProperty] private bool hasOpponents;

    [ObservableProperty] private string exportStatusMessage = string.Empty;

    /// <summary>Set by PreviouslyBattledUsersWindow.axaml.cs to show
    /// ExportFormatDialogWindow - a small in-app popup letting the user choose
    /// CSV or JSON before the save-file dialog opens, per the explicit format
    /// picker requested (the OS save dialog's own file-type dropdown already
    /// technically let a user pick either extension, but this makes the
    /// choice its own guided step instead). Returns "csv" or "json", or null
    /// if the dialog was cancelled.</summary>
    public Func<Task<string?>>? RequestExportFormat { get; set; }

    /// <summary>Set by PreviouslyBattledUsersWindow.axaml.cs to show a save-file
    /// dialog for the format RequestExportFormat already returned - same
    /// delegate-set-by-the-View pattern MainWindowViewModel.RequestSaveFilePath
    /// uses for hunt data export, just scoped to this window and given the
    /// chosen format so the dialog can narrow its FileTypeChoices/
    /// DefaultExtension to match it. Returns the chosen full path, or null if
    /// the dialog was cancelled.</summary>
    public Func<string, string, Task<string?>>? RequestExportFilePath { get; set; }

    /// <summary>Set by PreviouslyBattledUsersWindow.axaml.cs to show a Yes/No
    /// confirm before actually removing anything - same ConfirmDialogWindow.
    /// ShowAsync pattern RemoveEventViewModel.ConfirmAsync uses. Required, not
    /// just preferred: RemovePrevious/ClearAll refuse to remove anything if
    /// this hook isn't wired, rather than silently skipping the
    /// confirmation.</summary>
    public Func<string, Task<bool>>? ConfirmAsync { get; set; }

    public PreviouslyBattledUsersViewModel()
    {
        LoadOpponents();

        PvpOpponentService.OpponentsChanged += OnOpponentsChanged;
    }

    private void OnOpponentsChanged()
    {
        // PvpOpponentService.RegisterBattle runs on PvpTracker's background
        // tracking loop, not the UI thread - the ObservableCollection can only
        // be touched from the UI thread.
        Dispatcher.UIThread.Post(LoadOpponents);
    }

    private void LoadOpponents()
    {
        Opponents.Clear();

        // Most recent battle first - PvpOpponentService.Opponents is a rolling
        // log of individual battles (not deduped by name), so the same
        // opponent can legitimately appear on more than one row here.
        foreach (var entry in PvpOpponentService.Opponents.OrderByDescending(o => o.BattledAtUtc))
        {
            Opponents.Add(new PvpOpponentDisplayItem
            {
                Name = entry.Name,
                TimesBattled = entry.TimesBattled,
                BattledAt = entry.BattledAtUtc.ToLocalTime().ToString("g")
            });
        }

        HasNoOpponents = Opponents.Count == 0;
        HasOpponents = Opponents.Count > 0;
    }

    /// <summary>Removes the single most recent battle from the list - backs
    /// the "Remove Previous" button. There's no per-row selection in this
    /// window (Opponents is a plain read-only list), so "most recent" -
    /// already how the list is sorted, see LoadOpponents - is what "previous"
    /// refers to here. See PvpOpponentService.RemoveMostRecent's remarks for
    /// why this doesn't touch the separate lifetime battle count.</summary>
    [RelayCommand]
    private async Task RemovePrevious()
    {
        if (Opponents.Count == 0)
            return;

        string confirmMessage =
            $"Remove the most recent battle (vs. \"{Opponents[0].Name}\")? This can't be undone.";

        bool confirmed = ConfirmAsync is not null && await ConfirmAsync(confirmMessage);

        if (!confirmed)
            return;

        PvpOpponentService.RemoveMostRecent();

        ExportStatusMessage = "Removed the most recent battle.";
    }

    /// <summary>Wipes the entire saved battle log - backs the "Clear All"
    /// button. Always confirmed first, same as RemovePrevious, since neither
    /// can be undone.</summary>
    [RelayCommand]
    private async Task ClearAll()
    {
        if (Opponents.Count == 0)
            return;

        bool confirmed = ConfirmAsync is not null
            && await ConfirmAsync("Clear the entire battle history? This can't be undone.");

        if (!confirmed)
            return;

        PvpOpponentService.ClearAll();

        ExportStatusMessage = "Cleared all battle history.";
    }

    /// <summary>
    /// Exports the full saved list (not just what's currently rendered) as
    /// either CSV or JSON, based on the format chosen in the
    /// RequestExportFormat popup - see PvpOpponentExportService.
    /// </summary>
    [RelayCommand]
    private async Task Export()
    {
        if (RequestExportFormat is null || RequestExportFilePath is null)
            return;

        string? format = await RequestExportFormat();

        if (format is null)
            return;

        string suggestedName = $"ProTracker-PvpOpponents-{DateTime.Now:yyyy-MM-dd-HHmmss}";

        string? path = await RequestExportFilePath(suggestedName, format);

        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            string extension = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();

            if (extension == "json")
                PvpOpponentExportService.ExportJson(PvpOpponentService.Opponents, path);
            else
                PvpOpponentExportService.ExportCsv(PvpOpponentService.Opponents, path);

            ExportStatusMessage = $"Exported to {Path.GetFileName(path)}.";
        }
        catch (Exception ex)
        {
            ExportStatusMessage = $"Export failed: {ex.Message}";
        }
    }

    public void Dispose()
    {
        PvpOpponentService.OpponentsChanged -= OnOpponentsChanged;
    }
}
